using System.Numerics;
using Agapanthe.Assets;
using Agapanthe.Assets.Model;
using Agapanthe.Core;
using Agapanthe.Engine;
using Agapanthe.Graphics;
using Agapanthe.Platform;
using Agapanthe.Rendering;
using Agapanthe.World;
using Silk.NET.Input;

// M5 validation app: loads a real glTF 2.0 model and renders it with full Cook-Torrance PBR
// (3-point HDR lighting, ACES tonemap; +/- adjusts exposure, L swings the key light) through
// GltfLoader (CPU DTOs) -> SceneBuilder -> Renderer. Original M4 pipeline description:
// GltfLoader (CPU DTOs) -> SceneBuilder (GPU upload: meshes, textures + mip chains, per-material set 1)
// -> Renderer.DrawScene (per-frame camera set 0, per-instance material set 1 + world transform).
// The Sandbox no longer wires any Vulkan object by hand — the Renderer owns the mesh pipeline, both set
// layouts, the material allocator and the camera UBOs; the Scene owns every mesh/texture/sampler.
//
// Model selection:
//   dotnet run --project samples/Sandbox                          -> default DamagedHelmet.glb
//   dotnet run --project samples/Sandbox -- BoxTextured.gltf      -> a fixture by bare name (from models/)
//   dotnet run --project samples/Sandbox -- /abs/path/model.glb   -> an arbitrary absolute/relative path
// Fixtures are copied into <output>/models/ by Sandbox.csproj, so a bare fixture name and the default
// both resolve against AppContext.BaseDirectory regardless of the shell's working directory.

// Shader directory: for hot reload (M8-05) we want the *editable* repo source (…/shaders), not the read-only
// copy MSBuild drops next to the executable under bin/. ResolveShaderDirectory prefers the source tree when it
// can be found by walking up from the output directory, and falls back to the bin/ copy for a deployed build.
var shaderDir = ResolveShaderDirectory();
var modelsDir = Path.Combine(AppContext.BaseDirectory, "models");

// Resolve the model path: CLI arg first (as given, then as a bare fixture name under models/), else the
// default fixture. Fail fast with a clear message if nothing resolves — before opening any GPU resource.
var modelPath = ResolveModelPath(args, modelsDir);
if (modelPath is null)
{
    Log.Error($"Sandbox: model '{args[0]}' not found (tried as-is and under '{modelsDir}').");
    Environment.Exit(2);
    return;
}

// Auto-close after N rendered frames when AGAPANTHE_MAX_FRAMES is set (CI/headless validation).
var maxFrames = int.TryParse(Environment.GetEnvironmentVariable("AGAPANTHE_MAX_FRAMES"), out var mf) ? mf : -1;
var renderedFrames = 0;

using var window = new EngineWindow("Agapanthe — M7 IBL", 1280, 720);

GraphicsDevice? device = null;
Swapchain? swapchain = null;
Renderer? renderer = null;
ResourceRegistry? registry = null;   // owns the GPU resources (the old Scene's possession half)
GameWorld? world = null;             // owns the entities (the old Scene's draw-list half, now ECS)
FrameRenderer? frameRenderer = null;

// Render lists, owned by the app and reused every frame (Clear keeps capacity → zero alloc per frame).
var renderList = new RenderList();
var sceneBounds = Double3Bounds.Empty;

var camera = new Camera();
var controller = new FreeCameraController();
var resizePending = false;

// Load bench (W4): AGAPANTHE_CULL_STATS=1 puts the Sandbox in bench mode — the camera is placed INSIDE the scene
// (so the frustum culls most of it), the drawables spin in place each frame (deterministic, by frame index), and
// the per-frame cull cost + visible/total counts + allocation delta are logged every BenchLogEvery frames. Pair
// it with AGAPANTHE_SCENE=grid:100x100 for the 10 000-entity exit-criterion run.
var benchMode = Environment.GetEnvironmentVariable("AGAPANTHE_CULL_STATS") is { Length: > 0 };
// Churn bench (P3-M2 F2): AGAPANTHE_CHURN=N spawns a small HIERARCHY and despawns a whole subtree every frame, so
// the lifecycle path (deferred spawn/despawn, cascade, archetype moves) is exercised at scale — the flat grid bench
// never touches it. Default 0 = off, so the render baseline (V3's bit-identical gate) is untouched.
var churnPerFrame = int.TryParse(Environment.GetEnvironmentVariable("AGAPANTHE_CHURN"), out var cpf) && cpf > 0 ? cpf : 0;
var inputDebug = Environment.GetEnvironmentVariable("AGAPANTHE_INPUT_DEBUG") is { Length: > 0 };
const int BenchLogEvery = 60;
var benchFrame = 0;
long benchTicks = 0;
long benchAllocBefore = 0;
long benchCpuStart = 0;

// P3-M2: the frame order now lives in the engine's FrameOrchestrator, not in a Sandbox closure. The orchestrator
// caches its own render delegate (allocated once), so the zero-alloc hot path is preserved.
FrameOrchestrator? orchestrator = null;

window.Loaded += () =>
{
    var requiredExtensions = window.GetRequiredVulkanExtensions();
    device = new GraphicsDevice("Agapanthe Sandbox", requiredExtensions, window.VkSurface!);

    var (width, height) = window.FramebufferSize;
    swapchain = new Swapchain(device, width, height);
    camera.AspectRatio = (float)width / height;

    // Headless IBL-generation check (M7-04): AGAPANTHE_IBL_TEST=<prefix> generates IBL maps from the HDRI
    // fixture, dumps the environment cube faces + irradiance + BRDF LUT as PPMs, then closes — no scene, no
    // render loop. Lets the compute path be validated (0 validation, 0 leak) before the skybox/mesh
    // integration (M7-05) exists.
    if (Environment.GetEnvironmentVariable("AGAPANTHE_IBL_TEST") is { Length: > 0 } iblPrefix)
    {
        RunIblTest(device, shaderDir, modelsDir, iblPrefix);
        window.Close();
        return;
    }

    // Renderer owns the mesh pipeline, both descriptor set layouts, the material allocator and the
    // per-frame camera UBOs. It exposes MaterialAllocator/MaterialSetLayout for the SceneBuilder.
    renderer = new Renderer(device, swapchain, shaderDir);
    // Half a stop under 1: the studio HDRI is bright enough that at exposure 1 the backdrop and the ground both
    // clip to white and every shadow drowns. (+/- still moves it in thirds of a stop at runtime.)
    renderer.Exposure = 0.5f;

    // Load the model to CPU DTOs (no GPU dependency), then upload it into a GPU-resident Scene.
    var model = GltfLoader.Load(modelPath);
    LogModelStats(model, modelPath);
    // The seam (spec §3.2): the registry owns the GPU resources and hands back a GPU-free description of each
    // drawable (specs). It is engine-wide, so several models can be loaded without their handles colliding.
    // The world spawns an entity per spec — from here on, the model IS entities.
    // AGAPANTHE_WORLD_ORIGIN="x,y,z" places the model that far from the world origin, in double (spec §3.3).
    // The whole point of camera-relative rendering is that the image must be IDENTICAL wherever this puts it —
    // which is exactly what the M3 precision proof asserts (10 000 km == the origin, to within 1 LSB).
    registry = new ResourceRegistry();

    // AGAPANTHE_UNLOAD_TEST=N runs N load/unload cycles before the real load. Unload had NO caller anywhere in the
    // engine (audit M3), so the streaming path the registry exists for was entirely unproven — and it was in fact
    // leaking a descriptor set per material, invisibly, because the leak gate counts pools and not sets. This puts
    // it under the real gate: N cycles must end with the same resource count as zero cycles. Safe here — no frame
    // is in flight yet, so the synchronous destruction of the model's descriptor pools has nothing to race.
    if (int.TryParse(Environment.GetEnvironmentVariable("AGAPANTHE_UNLOAD_TEST"), out var unloadCycles)
        && unloadCycles > 0)
    {
        for (var i = 0; i < unloadCycles; i++)
        {
            var (throwaway, _) = registry.Load(device, model, renderer.MaterialSetLayout);
            registry.Unload(throwaway);
        }

        Log.Info($"Sandbox: [unload-test] {unloadCycles} load/unload cycles done; the leak report below covers them.");
    }

    var worldOrigin = ParseDouble3(Environment.GetEnvironmentVariable("AGAPANTHE_WORLD_ORIGIN"));

    // A double's ULP grows with magnitude: at 1e15 m it is ~0.125 m, which is LARGER than a walking step — the
    // camera then refuses to move along that axis, silently, because the step rounds straight back to where it
    // started. (That is the same wall the FLOAT position hit at 1e7 m, and the reason positions are double at all;
    // double just moves it out by 8 orders of magnitude, it does not remove it.) Beyond ~1e12 m the movement is
    // visibly quantized, so say so rather than let it look like a broken strafe.
    var originReach = Math.Max(Math.Max(Math.Abs(worldOrigin.X), Math.Abs(worldOrigin.Y)), Math.Abs(worldOrigin.Z));
    if (originReach > 1e12)
    {
        Log.Warn(
            $"Sandbox: AGAPANTHE_WORLD_ORIGIN is {originReach:0.###e+00} m out — a double's step there is " +
            $"~{Math.BitIncrement(originReach) - originReach:F3} m, so camera movement will be quantized or drop " +
            "entirely on the large axes. Fine for a precision capture; unusable for flying around.");
    }

    var (modelId, specs) = registry.Load(
        device, model, renderer.MaterialSetLayout, worldOrigin);

    world = new GameWorld();

    // AGAPANTHE_SCENE selects the scene layout. grid:NxN (or grid:N) replicates the loaded model across an N×N grid
    // (the M4 load bench). drop:N replicates it as a cloud of physics BODIES above the ground (P3-M3). One model
    // loaded, its handles are global (M3), so every copy is just more entities pointing at the same GPU resources.
    // Unset / unparseable → the model once, as before.
    var sceneSpec = Environment.GetEnvironmentVariable("AGAPANTHE_SCENE");
    var (rows, cols) = ParseGrid(sceneSpec);
    var dropCount = ParseDrop(sceneSpec);
    bool multiInstance;
    double? physicsGroundY = null; // set by drop mode: the Y of the collision plane AND the rendered ground quad
    if (dropCount > 0)
    {
        physicsGroundY = SpawnDropScene(world, specs, dropCount, worldOrigin);
        multiInstance = true;
        Log.Info($"Sandbox: [scene] drop {dropCount} bodies = {dropCount * specs.Length} entities " +
                 $"(ground y={physicsGroundY:F2}); set AGAPANTHE_PHYSICS=1 to simulate.");
    }
    else if (rows * cols > 1)
    {
        var spacing = ModelDiagonal(specs) * 1.5;
        SpawnGrid(world, specs, rows, cols, spacing);
        multiInstance = true;
        Log.Info($"Sandbox: [scene] grid {rows}x{cols} = {rows * cols * specs.Length} entities " +
                 $"(spacing {spacing:F1} m), one model upload.");
    }
    else
    {
        foreach (var spec in specs)
        {
            world.SpawnImported(in spec);
        }

        multiInstance = false;
    }

    // System 2: the scene extent comes from the world (union of per-entity world spheres), replacing Scene.Bounds*.
    // Folded once here to frame the camera and place the lights; re-aggregated every frame in drawScene (P3-M1,
    // debt #1) so it tracks moving entities.
    sceneBounds = world.AggregateBounds();

    // Frame the model: sit the camera back by 1.5x the diagonal along a slightly-raised front direction,
    // orient yaw/pitch to look at the centre.
    FrameCamera(camera, controller, renderer, in sceneBounds);

    // Bench: put the camera INSIDE the scene, looking along -Z, so the frustum sees only a forward wedge and
    // culling has most of the grid to reject (the exit criterion wants << all visible). near/far span the local
    // neighbourhood, not the whole grid.
    if (benchMode)
    {
        var (center, diagonal) = NarrowBounds(in sceneBounds);
        camera.Position = center;
        camera.Yaw = 0f;
        camera.Pitch = 0f;
        camera.Near = MathF.Max(diagonal * 0.001f, 0.01f);
        camera.Far = MathF.Max(diagonal * 0.5f, 1f);
        // Cap the shadow distance to the view too, so the LIGHT volume is bounded and the shadow-caster list is
        // culled as well (otherwise the huge framing shadow distance would make every entity a caster).
        renderer.ShadowDistance = camera.Far;
    }

    // A GROUND to catch the shadows. Without it the model floats in the HDRI and the only shadow receiver is the
    // model itself: shadow quality, shadow crawl and the whole shadow-fit path are then impossible to SEE — the
    // P3-M1 visual check ran straight into that. A plain lit quad, sized to what is in the scene (the model, or the
    // 100×100 bench grid) and sitting just under it, uploaded through the normal model path — it is a ModelAsset
    // like any other, so it costs no new engine API. Spawned AFTER the framing so the camera still frames the
    // MODEL, not the (much larger) floor. AGAPANTHE_GROUND=0 removes it, which is what the precision captures want
    // (they compare against pre-ground baselines).
    var groundSpawned = false;
    if (Environment.GetEnvironmentVariable("AGAPANTHE_GROUND") is not "0" && !sceneBounds.IsEmpty)
    {
        var extent = sceneBounds.Max - sceneBounds.Min;
        var span = Math.Max(Math.Max(extent.X, extent.Z), 1e-3);
        // Wide enough that its edges leave the frame: a small slab floating in the sky reads as a platform, not as
        // ground. Clears the bench grid too (span already spans it).
        var groundSize = (float)Math.Max((span * 2.5) + (extent.Y * 8.0), 40.0);
        // In drop mode the plane must sit at the PHYSICS ground Y (bodies start high above it and fall onto it), not
        // just under the initial cloud the way a static scene does. Everywhere else: just under the lowest geometry.
        var groundY = physicsGroundY ?? (sceneBounds.Min.Y - (extent.Y * 0.02) - 0.01);
        var groundOrigin = new Double3(
            (sceneBounds.Min.X + sceneBounds.Max.X) * 0.5,
            groundY,
            (sceneBounds.Min.Z + sceneBounds.Max.Z) * 0.5);

        var (_, groundSpecs) = registry.Load(
            device, BuildGroundModel(groundSize), renderer.MaterialSetLayout, groundOrigin);
        foreach (var spec in groundSpecs)
        {
            // The ground RECEIVES shadows but must not CAST them (P3-M5): a large flat receiver self-shadows into
            // acne, and its huge bounds needlessly inflate every cascade's fit.
            world.SpawnImported(in spec, castsShadow: false);
        }

        // The ground IS part of the scene from here on: the light placement and the shadow fit must see it.
        sceneBounds = world.AggregateBounds();
        groundSpawned = true;
        Log.Info(
            $"Sandbox: [scene] ground plane {groundSize:F1} m at y={groundOrigin.Y:F2} " +
            "(AGAPANTHE_GROUND=0 to remove).");
    }

    // Default M5 lighting: a warm directional key (sun) plus a cool rim and a soft fill point
    // light placed from the scene bounds — a classic 3-point setup that reads PBR materials
    // well. HDR intensities (> 1) are expected; the ACES tonemap compresses them.
    // On a multi-instance scene (grid) the rig is DROPPED: it is scaled to the scene diagonal
    // (inflated by the ground plane) → point lights sit hundreds of metres out, and inverse-square
    // attenuation then paints a brightness gradient across the crowd ("each helmet has its own
    // light"). The sun + IBL are physically consistent across the whole grid; the studio rig is a
    // single-model showcase only.
    SetupLights(renderer.Lights, in sceneBounds, multiInstance);

    // The environment (M7): the renderer needs one before it can draw — the ambient and the skybox both sample it.
    // AGAPANTHE_HDRI=<path> always wins. Otherwise: with the ground on, an OUTDOOR sky, generated procedurally; with
    // AGAPANTHE_GROUND=0, the studio HDRI fixture, which is what the material/IBL captures have always used.
    // <para>
    // Why not the studio HDRI over grass: it is an INDOOR probe, with dark walls a few metres left and right. A
    // skybox sits at infinity, so those walls never recede however far you fly — the scene reads as a corridor you
    // cannot leave. An open sky removes the walls and, being physically consistent with the sun, also lights the
    // ground the way the shadow expects.
    // </para>
    var hdriOverride = Environment.GetEnvironmentVariable("AGAPANTHE_HDRI");
    if (hdriOverride is { Length: > 0 } && File.Exists(hdriOverride))
    {
        renderer.SetEnvironment(HdrImageLoader.Load(hdriOverride));
        Log.Info($"Sandbox: environment '{hdriOverride}'.");
    }
    else if (groundSpawned)
    {
        renderer.SetEnvironment(BuildSkyEnvironment(renderer.Lights.Directional.Direction));
        Log.Info("Sandbox: environment = procedural outdoor sky (AGAPANTHE_HDRI=<path.hdr> to override).");
    }
    else
    {
        var iblHdrPath = Path.Combine(modelsDir, "studio_small_1k.hdr");
        if (File.Exists(iblHdrPath))
        {
            renderer.SetEnvironment(HdrImageLoader.Load(iblHdrPath));
            Log.Info($"Sandbox: environment '{iblHdrPath}'.");
        }
        else
        {
            Log.Error($"Sandbox: HDRI environment '{iblHdrPath}' not found; the M7 renderer requires one to draw.");
        }
    }

    // The scene clear color lives on the Renderer now (it owns the scene pass); the FrameRenderer only
    // drives sync/acquire/present and no longer carries a clear color.
    frameRenderer = new FrameRenderer(device, swapchain, () => window.FramebufferSize);

    // The frame order lives in the ENGINE now (P3-M2 D1), not in this closure: the orchestrator registers the
    // default systems (PostSimulation: propagate transforms, aggregate bounds; Render: fit light, cull, draw) in the
    // one correct order and caches its own render delegate. The Sandbox adds only what is ITS business — the bench
    // spinner and the churn — as Stage.Simulation systems. That both keeps the invariant out of the sample AND
    // proves the scheduler is extensible.
    // P3-M4 W1: AGAPANTHE_CULL_VERIFY=1 turns on the GPU-vs-CPU visible-count check, logged after the capture.
    renderer!.VerifyCull = Environment.GetEnvironmentVariable("AGAPANTHE_CULL_VERIFY") is "1";

    orchestrator = FrameOrchestrator.CreateDefault(world!, renderer!, registry!, camera, renderList);
    if (benchMode)
    {
        // The spin (animate every drawable + drift the yaw, deterministic by frame count) was the first thing the
        // old closure did; as a Simulation system it runs at the same point — before propagate/aggregate/draw.
        orchestrator.Add(Stage.Simulation, new BenchSpinSystem(world!, camera));
    }

    if (churnPerFrame > 0)
    {
        // Churn as a Simulation system: it enqueues spawns/despawns; the scheduler's end-of-Simulation barrier
        // (= world.FlushStructuralChanges) applies them before PostSimulation propagates. No manual flush.
        orchestrator.Add(Stage.Simulation, new ChurnSystem(world!, churnPerFrame));
    }

    // AGAPANTHE_PHYSICS=1 drives the drop:N bodies (P3-M3): a fixed-step rigid-body integration in the Simulation
    // stage, so PostSimulation re-derives world transforms and bounds from the fallen positions and the GPU shadow
    // cull sees the casters MOVE. Opt-in, and only meaningful with a drop scene (it needs a ground Y); a grid or the
    // lone model just ignores it.
    if (physicsGroundY is { } physGroundY && Environment.GetEnvironmentVariable("AGAPANTHE_PHYSICS") is "1")
    {
        var settings = PhysicsSettings.Default((float)physGroundY);
        orchestrator.Add(Stage.Simulation, new PhysicsSystem(world!, in settings));
        Log.Info($"Sandbox: [physics] enabled — gravity {settings.Gravity}, ground y={settings.GroundY:F2}, " +
                 $"fixed dt {settings.FixedDt:F4}s.");
    }

    // Camera-relative proof (spec §3.3): both are world-space doubles, and the GPU sees neither — it only ever
    // sees their difference. Logged so a far-out run is visibly far out, not silently at the origin.
    Log.Info($"Sandbox: model at world origin {worldOrigin}, eye at {camera.Position} " +
             $"(distance to model centre: {Double3.Distance(camera.Position, NarrowBounds(in sceneBounds).Center):F3}).");

    Log.Info($"Sandbox: initialized on '{device.AdapterName}'. Rendering '{registry.NameOf(modelId)}' — " +
             "clic pour capturer la souris, WASD + souris, Échap libère puis quitte. " +
             $"Hot reload actif sur '{shaderDir}'.");

    // Headless proof of the <1s reload budget (spec §6): AGAPANTHE_SHADER_RELOAD_TEST=1 forces one reload of
    // every graphics pass and logs the per-pass wall time, then the normal loop continues. Runs before the
    // first frame (GPU idle, no in-flight frames) so the deferred pipeline swap is safe. Debug-only branch —
    // it never touches the per-frame hot path.
    if (Environment.GetEnvironmentVariable("AGAPANTHE_SHADER_RELOAD_TEST") is { Length: > 0 })
    {
        renderer.ReloadAllForTest();
    }
};

// MoltenVK does not always report OUT_OF_DATE on resize; recreate explicitly to be safe.
window.FramebufferResized += (w, h) =>
{
    resizePending = true;
    if (w > 0 && h > 0)
    {
        camera.AspectRatio = (float)w / h;
    }
};

// Escape is two-stage: the first press releases the mouse capture, quitting only happens
// when the cursor is already free. Edge-triggered so holding the key can't skip a stage.
// Live look-sensitivity tuning, ×1.25 per press, clamped to [0.0001, 0.01] rad/px:
//   PageUp/PageDown = horizontal (yaw) · Home/End = vertical (pitch)
const float sensStep = 1.25f;
const float sensMin = 0.0001f;
const float sensMax = 0.01f;
window.KeyPressed += key =>
{
    switch (key)
    {
        case Key.Escape when window.MouseCaptured:
            window.SetMouseCaptured(false);
            break;
        case Key.Escape:
            window.Close();
            break;
        case Key.PageUp:
            controller.LookSensitivityX = MathF.Min(controller.LookSensitivityX * sensStep, sensMax);
            LogSensitivity();
            break;
        case Key.PageDown:
            controller.LookSensitivityX = MathF.Max(controller.LookSensitivityX / sensStep, sensMin);
            LogSensitivity();
            break;
        case Key.Home:
            controller.LookSensitivityY = MathF.Min(controller.LookSensitivityY * sensStep, sensMax);
            LogSensitivity();
            break;
        case Key.End:
            controller.LookSensitivityY = MathF.Max(controller.LookSensitivityY / sensStep, sensMin);
            LogSensitivity();
            break;
        // Exposure: +/- in thirds of a stop (x2^(1/3)), clamped to [1/64, 64].
        case Key.Equal or Key.KeypadAdd when renderer is not null:
            renderer.Exposure = MathF.Min(renderer.Exposure * 1.26f, 64f);
            Log.Info($"Exposure: {renderer.Exposure:F3} ({MathF.Log2(renderer.Exposure):+0.0;-0.0} EV)");
            break;
        case Key.Minus or Key.KeypadSubtract when renderer is not null:
            renderer.Exposure = MathF.Max(renderer.Exposure / 1.26f, 1f / 64f);
            Log.Info($"Exposure: {renderer.Exposure:F3} ({MathF.Log2(renderer.Exposure):+0.0;-0.0} EV)");
            break;
        // N: cycle the shading debug views (PBR -> normals -> basecolor -> ... , see mesh.frag).
        case Key.N when renderer is not null:
            renderer.DebugView = (renderer.DebugView + 1) % 11;
            Log.Info($"Debug view: {renderer.DebugView} ({DebugViews.Names[renderer.DebugView]})");
            break;
        // L: swing the key light around the vertical axis (lighting debug).
        case Key.L when renderer is not null:
            var d = renderer.Lights.Directional;
            var yawStep = MathF.PI / 8f;
            var (sin, cos) = MathF.SinCos(yawStep);
            d.Direction = new Vector3(
                (d.Direction.X * cos) - (d.Direction.Z * sin),
                d.Direction.Y,
                (d.Direction.X * sin) + (d.Direction.Z * cos));
            renderer.Lights.Directional = d;
            Log.Info($"Key light direction: {d.Direction}");
            break;
    }

    void LogSensitivity()
        => Log.Info($"Look sensitivity: X={controller.LookSensitivityX:F5} Y={controller.LookSensitivityY:F5} rad/px");
};

window.Updated += dt =>
{
    // Camera control only while the cursor is captured (a click in the window captures it,
    // focus loss releases it) — mouse motion outside the window never steers the view.
    // The glTF model is static (M4: no animation); the world transform comes from the node hierarchy.
    if (!window.MouseCaptured)
    {
        // Update() isn't driven while the cursor is free, so clear the smoothed look delta: the next
        // captured frame then starts from rest instead of gliding out a stale delta (no rotation kick).
        controller.ResetLook();
        return;
    }

    // GLFW reports keys by their US-QWERTY POSITION, so on an AZERTY keyboard the physical W/A keys are the ones
    // engraved Z/Q. Accept both spellings (and the arrow keys) so the camera moves whatever the layout.
    var input = new CameraInput(
        moveForward: window.IsKeyDown(Key.W) || window.IsKeyDown(Key.Z) || window.IsKeyDown(Key.Up),
        moveBackward: window.IsKeyDown(Key.S) || window.IsKeyDown(Key.Down),
        moveLeft: window.IsKeyDown(Key.A) || window.IsKeyDown(Key.Q) || window.IsKeyDown(Key.Left),
        moveRight: window.IsKeyDown(Key.D) || window.IsKeyDown(Key.Right),
        moveUp: window.IsKeyDown(Key.Space),
        // ControlLeft is intercepted by macOS in some setups (Ctrl+click = right click), so C
        // doubles as the descend key.
        moveDown: window.IsKeyDown(Key.ControlLeft) || window.IsKeyDown(Key.C),
        lookDelta: window.MouseDelta,
        sprint: window.IsKeyDown(Key.ShiftLeft));

    // AGAPANTHE_INPUT_DEBUG=1 logs what the window actually reports and what the camera actually does with it —
    // the only way to tell "the key never arrived" from "the movement was computed wrong", which no capture and no
    // unit test can distinguish (the controller itself is covered: CameraTests.Controller_Strafe_*).
    if (inputDebug)
    {
        var before = camera.Position;
        controller.Update(camera, (float)dt, in input);
        var delta = camera.Position - before;
        if (input.MoveForward || input.MoveBackward || input.MoveLeft || input.MoveRight
            || input.MoveUp || input.MoveDown)
        {
            Log.Info(
                $"[input] fwd={input.MoveForward} back={input.MoveBackward} left={input.MoveLeft} " +
                $"right={input.MoveRight} | right-axis={camera.Right} forward-axis={camera.Forward} " +
                $"| delta=({delta.X:F3}, {delta.Y:F3}, {delta.Z:F3})");
        }

        return;
    }

    controller.Update(camera, (float)dt, in input);
};

// Debug HUD in the title bar (no in-view text rendering exists — that would need a font atlas + overlay pass).
// Accumulated over ~0.25 s so the fps reading is stable and the OS title update is not spammed every frame.
var hudElapsed = 0.0;
var hudFrames = 0;

window.Rendered += dt =>
{
    if (frameRenderer is null || swapchain is null || orchestrator is null)
    {
        return;
    }

    if (resizePending)
    {
        resizePending = false;
        frameRenderer.RequestResize();
    }

    // Shader hot reload (M8-05): recompile any edited pass at the frame boundary, BEFORE command recording.
    // Zero-allocation early-out inside when no shader changed (a single volatile read).
    renderer?.PollShaderReload();

    // Bench measures the WHOLE per-frame cost now: tick (spin/churn/propagate/aggregate) + draw. The alloc delta
    // must stay zero in steady state (spec §6). Timing spans both, since the frame order moved into the engine.
    if (benchMode)
    {
        benchAllocBefore = GC.GetAllocatedBytesForCurrentThread();
        benchCpuStart = System.Diagnostics.Stopwatch.GetTimestamp();
    }

    // Tick OUTSIDE DrawFrame (P3-M2 D1.a): Input -> Simulation -> PostSimulation, always. DrawFrame runs only the
    // Render stage, and skips it when the swapchain is out of date — the simulation must not skip with it.
    orchestrator.Tick((float)dt);
    frameRenderer.DrawFrame(orchestrator.RenderDelegate);

    // Title-bar debug HUD (throttled). GC.GetTotalMemory is the managed heap — the number that matters for the
    // 0-alloc-per-frame goal: it should sit FLAT while flying around a static scene. renderList.Count is the scene
    // candidate count (the GPU culls it); draws are the instanced scene+shadow calls.
    hudElapsed += dt;
    hudFrames++;
    if (hudElapsed >= 0.25 && renderer is not null)
    {
        var fps = hudFrames / hudElapsed;
        var msPerFrame = hudElapsed / hudFrames * 1000.0;
        var managedMb = GC.GetTotalMemory(false) / (1024.0 * 1024.0);
        window.Title =
            $"Agapanthe — {fps:F0} fps ({msPerFrame:F1} ms) · draws {renderer.LastSceneDrawCalls}+{renderer.LastShadowDrawCalls} " +
            $"· candidates {renderList.Count} · GC {managedMb:F1} MB";
        hudElapsed = 0;
        hudFrames = 0;
    }

    if (benchMode)
    {
        var alloc = GC.GetAllocatedBytesForCurrentThread() - benchAllocBefore;
        benchTicks += System.Diagnostics.Stopwatch.GetTimestamp() - benchCpuStart;
        if (++benchFrame % BenchLogEvery == 0)
        {
            var avgMs = System.Diagnostics.Stopwatch.GetElapsedTime(0, benchTicks).TotalMilliseconds / BenchLogEvery;
            // renderList.Count is the scene CANDIDATE count now (P3-M4): every drawable, uploaded for the GPU cull.
            // The frustum cull runs on the GPU, so the CPU no longer holds a "visible" count (AGAPANTHE_CULL_VERIFY
            // reads the GPU's back). The shadow casters now live per-cascade inside the orchestrator (P3-M5).
            Log.Info(
                $"Sandbox: [cull-stats] frame {benchFrame} — candidates {renderList.Count}, " +
                $"draws {renderer!.LastSceneDrawCalls}+{renderer.LastShadowDrawCalls} (instanced), " +
                $"tick+draw avg {avgMs:F3} ms/frame, per-frame alloc {alloc} B.");
            benchTicks = 0;
        }
    }

    if (maxFrames > 0 && ++renderedFrames >= maxFrames)
    {
        // Headless debug capture: AGAPANTHE_CAPTURE=<path.ppm> dumps the tonemapped HDR target
        // on the last frame, so rendering issues can be inspected without a windowed session.
        if (Environment.GetEnvironmentVariable("AGAPANTHE_CAPTURE") is { Length: > 0 } capturePath && renderer is not null)
        {
            frameRenderer.WaitIdle();
            renderer.SaveHdrCapture(capturePath);
            // The shadow configuration this frame, logged so a capture's shadowing can be justified against the
            // protocol rather than eyeballed. (This replaced a P3-M2 line printing eyeDistance + ShadowCasterDistance:
            // both belonged to the single-cascade wedge that P3-M5 retired, so it had been printing 0.000 and a value
            // with no effect on the render — a gate tool that lies is worse than no tool. Audit M2.)
            Log.Info(
                $"Sandbox: [shadow] CSM {renderer.Cascades.Count} cascades, lambda {renderer.Cascades.Lambda:F2}, " +
                $"range {MathF.Min(renderer.Cascades.MaxDistance, renderer.ShadowDistance):F1} m " +
                $"({Renderer.ShadowTileResolution}² per cascade in the {Renderer.ShadowMapResolution}² atlas).");

            // P3-M4 W1 gate: the GPU cull must keep exactly the CPU frustum test's visible set. WaitIdle above means
            // the compute has finished, so the args instanceCounts are readable. Assert equal (the milestone gate).
            if (renderer.VerifyCull)
            {
                var gpu = renderer.ReadBackSceneVisible();
                var cpu = renderer.LastSceneCpuVisible;
                Log.Info($"Sandbox: [cull-verify] GPU visible {gpu} vs CPU visible {cpu} — {(gpu == cpu ? "MATCH" : "MISMATCH")}.");

                // P3-M7 W3: shadow raster measured per cascade. Without the near-cut plane a caster enters ~4
                // cascades (4× raster); with it, each lands in ~1. The total / cascade-0 ratio shows the drop.
                Span<int> shadowPerCascade = stackalloc int[4];
                var shadowTotal = renderer.ReadBackShadowVisible(shadowPerCascade);
                Log.Info(
                    $"Sandbox: [shadow-verify] shadow instances total {shadowTotal} — per cascade " +
                    $"[{shadowPerCascade[0]}, {shadowPerCascade[1]}, {shadowPerCascade[2]}, {shadowPerCascade[3]}].");
            }
        }

        window.Close();
    }
};

var clean = false;
try
{
    window.Run();
}
finally
{
    // Tear down in strict order, GPU-idle first — runs even if init threw inside the Loaded handler, so
    // the leak report is always produced. Order (M4-11):
    //   WaitIdle -> frameRenderer.Dispose -> registry.Dispose (meshes/materials/textures/samplers, deferred —
    //      same resources and same reverse order the old Scene used, so the leak gate is unchanged)
    //   -> renderer.Dispose (pipeline/layouts/allocator/UBOs; the allocator destroys its pools synchronously,
    //      which is why the registry — whose material sets came from that allocator — is disposed first)
    //   -> device.DeletionQueue.FlushAll() (drain every deferred destroy while the device is alive and idle)
    //   -> swapchain -> device -> [REPORT] -> window.
    // The world owns no GPU resource (managed entities only), so it plays no part in this ordering.
    frameRenderer?.WaitIdle();
    frameRenderer?.Dispose();
    world?.Dispose();
    registry?.Dispose();
    renderer?.Dispose();
    device?.DeletionQueue.FlushAll();
    swapchain?.Dispose();
    device?.Dispose();

    // GPU-leak accounting is fully decided once the device is gone: every tracked GPU resource has been
    // registered and unregistered by now. Emit the report HERE, BEFORE the window's native GLFW teardown.
    // That teardown can hit a rare, uncatchable Silk.NET access violation (0xC0000005 in GlfwEvents.Dispose,
    // M8-14); a native fault cannot be caught by managed try/catch, so anything after it may never run.
    // Reporting first guarantees the 0-leak gate result is always printed, whatever GLFW does next.
    clean = ResourceTracker.Report();
    Log.Info(clean ? "Sandbox: clean shutdown, no GPU resource leaks." : "Sandbox: LEAKS DETECTED (see above).");

    window.Dispose(); // native GLFW/window teardown LAST — a crash here can no longer mask the leak report
}

Environment.Exit(clean ? 0 : 1);

// --- Local helpers -----------------------------------------------------------------------------------

// Resolves the shader directory. Hot reload (M8-05) must watch and recompile the files the human edits, i.e.
// the repo source tree (…/shaders), not the read-only copy MSBuild lays down next to the executable under
// bin/. Walking UP from the output directory (starting one level above so the bin/ copy itself is skipped),
// the first ancestor holding shaders/mesh.frag is the editable source. A deployed build (no such ancestor)
// falls back to the bin/ copy — hot reload is then inert but harmless.
static string ResolveShaderDirectory()
{
    var binShaders = Path.Combine(AppContext.BaseDirectory, "shaders");
    for (var dir = new DirectoryInfo(AppContext.BaseDirectory).Parent; dir is not null; dir = dir.Parent)
    {
        var candidate = Path.Combine(dir.FullName, "shaders");
        if (File.Exists(Path.Combine(candidate, "mesh.frag")))
        {
            return candidate;
        }
    }

    return binShaders;
}

// Resolves the model path: an explicit CLI arg is tried verbatim (absolute or cwd-relative) then as a
// bare fixture name under models/; with no arg the default fixture is used. Returns null when an
// explicit arg matches nothing.
static string? ResolveModelPath(string[] args, string modelsDir)
{
    if (args.Length == 0)
    {
        return Path.Combine(modelsDir, "DamagedHelmet.glb");
    }

    var arg = args[0];
    if (File.Exists(arg))
    {
        return Path.GetFullPath(arg);
    }

    var underModels = Path.Combine(modelsDir, arg);
    return File.Exists(underModels) ? underModels : null;
}

// Headless IBL generation + capture (M7-04 verification). Loads the HDRI fixture, runs the compute generator,
// and writes tonemapped PPMs of the environment cube faces, one irradiance face and the BRDF LUT so the result
// can be eyeballed without the (not-yet-built) skybox pass.
static void RunIblTest(GraphicsDevice device, string shaderDir, string modelsDir, string prefix)
{
    var hdrPath = Path.Combine(modelsDir, "studio_small_1k.hdr");
    if (!File.Exists(hdrPath))
    {
        Log.Error($"Sandbox IBL test: HDRI fixture not found at '{hdrPath}'.");
        return;
    }

    var hdr = HdrImageLoader.Load(hdrPath);
    Log.Info($"Sandbox IBL test: loaded HDRI {hdr.Width}x{hdr.Height} from '{hdrPath}'.");

    using var generator = new IblGenerator(device, shaderDir);
    var maps = generator.Generate(hdr);
    try
    {
        for (var face = 0u; face < 6; face++)
        {
            var bytes = GpuReadback.ReadImage(device, maps.Environment, ImageLayoutState.ShaderReadOnly, 8, face);
            WriteHalfPpm($"{prefix}_env_f{face}.ppm", bytes, maps.Environment.Width, maps.Environment.Height);
        }

        var irr = GpuReadback.ReadImage(device, maps.Irradiance, ImageLayoutState.ShaderReadOnly, 8, 4);
        WriteHalfPpm($"{prefix}_irradiance_f4.ppm", irr, maps.Irradiance.Width, maps.Irradiance.Height);

        var lut = GpuReadback.ReadImage(device, maps.BrdfLut, ImageLayoutState.ShaderReadOnly, 4);
        WriteRgHalfPpm($"{prefix}_brdf_lut.ppm", lut, maps.BrdfLut.Width, maps.BrdfLut.Height);

        Log.Info($"Sandbox IBL test: wrote captures with prefix '{prefix}'.");
    }
    finally
    {
        maps.Dispose();
    }
}

// Writes an RGBA16F texel buffer as an 8-bit PPM with a simple Reinhard tonemap + sRGB encode (viewable).
static void WriteHalfPpm(string path, byte[] bytes, uint width, uint height)
{
    var halfs = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, Half>(bytes);
    using var output = new FileStream(path, FileMode.Create, FileAccess.Write);
    output.Write(System.Text.Encoding.ASCII.GetBytes($"P6\n{width} {height}\n255\n"));
    var row = new byte[width * 3];
    for (var y = 0; y < height; y++)
    {
        for (var x = 0; x < width; x++)
        {
            var i = ((y * (int)width) + x) * 4;
            row[(x * 3) + 0] = EncodeTonemapped((float)halfs[i + 0]);
            row[(x * 3) + 1] = EncodeTonemapped((float)halfs[i + 1]);
            row[(x * 3) + 2] = EncodeTonemapped((float)halfs[i + 2]);
        }

        output.Write(row);
    }

    static byte EncodeTonemapped(float v)
    {
        var x = Math.Max(v, 0f);
        x /= 1f + x; // Reinhard
        var srgb = x <= 0.0031308f ? x * 12.92f : (1.055f * MathF.Pow(x, 1f / 2.4f)) - 0.055f;
        return (byte)Math.Clamp((int)((srgb * 255f) + 0.5f), 0, 255);
    }
}

// Writes an RG16F texel buffer as a PPM (R->red, G->green, blue 0); the values are in [0,1], no tonemap.
static void WriteRgHalfPpm(string path, byte[] bytes, uint width, uint height)
{
    var halfs = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, Half>(bytes);
    using var output = new FileStream(path, FileMode.Create, FileAccess.Write);
    output.Write(System.Text.Encoding.ASCII.GetBytes($"P6\n{width} {height}\n255\n"));
    var row = new byte[width * 3];
    for (var y = 0; y < height; y++)
    {
        for (var x = 0; x < width; x++)
        {
            var i = ((y * (int)width) + x) * 2;
            row[(x * 3) + 0] = (byte)Math.Clamp((int)(((float)halfs[i + 0] * 255f) + 0.5f), 0, 255);
            row[(x * 3) + 1] = (byte)Math.Clamp((int)(((float)halfs[i + 1] * 255f) + 0.5f), 0, 255);
            row[(x * 3) + 2] = 0;
        }

        output.Write(row);
    }
}

static void LogModelStats(Agapanthe.Assets.Model.ModelAsset model, string path)
{
    var triangles = 0L;
    foreach (var mesh in model.Meshes)
    {
        triangles += mesh.Indices.Length / 3;
    }

    Log.Info($"Sandbox: loaded '{path}' — {model.Meshes.Count} mesh(es), {model.Materials.Count} material(s), " +
             $"{model.Images.Count} image(s), {triangles} triangle(s).");
}

// Parses AGAPANTHE_WORLD_ORIGIN / any "x,y,z" double triplet; anything unparseable means "at the origin".
static Double3 ParseDouble3(string? spec)
{
    if (spec is not { Length: > 0 })
    {
        return Double3.Zero;
    }

    var parts = spec.Split(',');
    var culture = System.Globalization.CultureInfo.InvariantCulture;
    if (parts.Length == 3
        && double.TryParse(parts[0], culture, out var x)
        && double.TryParse(parts[1], culture, out var y)
        && double.TryParse(parts[2], culture, out var z))
    {
        return new Double3(x, y, z);
    }

    Log.Warn($"Sandbox: could not parse '{spec}' as \"x,y,z\"; using the world origin.");
    return Double3.Zero;
}

// Parses AGAPANTHE_SCENE=grid:NxN or grid:N into (rows, cols). Anything else → (1, 1) = the model once.
static (int Rows, int Cols) ParseGrid(string? spec)
{
    if (spec is null || !spec.StartsWith("grid:", StringComparison.OrdinalIgnoreCase))
    {
        return (1, 1);
    }

    var dims = spec[5..].Split('x');
    if (dims.Length == 1 && int.TryParse(dims[0], out var n) && n > 0)
    {
        return (n, n);
    }

    if (dims.Length == 2 && int.TryParse(dims[0], out var r) && int.TryParse(dims[1], out var c) && r > 0 && c > 0)
    {
        return (r, c);
    }

    Log.Warn($"Sandbox: could not parse AGAPANTHE_SCENE='{spec}' (expected grid:N or grid:RxC); using one model.");
    return (1, 1);
}

// Parses AGAPANTHE_SCENE=drop:N into a body count. Anything else → 0 (not a drop scene).
static int ParseDrop(string? spec)
{
    if (spec is null || !spec.StartsWith("drop:", StringComparison.OrdinalIgnoreCase))
    {
        return 0;
    }

    if (int.TryParse(spec[5..], out var n) && n > 0)
    {
        return n;
    }

    Log.Warn($"Sandbox: could not parse AGAPANTHE_SCENE='{spec}' (expected drop:N); using one model.");
    return 0;
}

// The model's world-space diagonal, from the base specs: the span of every drawable's world sphere. Used to space
// grid cells so copies of the model do not overlap. Differences cancel any world origin baked into the positions.
static double ModelDiagonal(ImportedEntitySpec[] specs)
{
    if (specs.Length == 0)
    {
        return 1d;
    }

    var min = new Double3(double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity);
    var max = new Double3(double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity);
    foreach (var s in specs)
    {
        var r = s.BoundsRadius * MathHelpers.MaxStretch(s.RotationScale);
        var c = s.Position + new Double3(Vector3.Transform(s.BoundsCenter, s.RotationScale));
        var rr = new Double3(r, r, r);
        min = Double3.Min(min, c - rr);
        max = Double3.Max(max, c + rr);
    }

    return Math.Max(Double3.Distance(min, max), 1d);
}

// A grass-covered ground quad in the X/Z plane, centred on its own local origin (its world position comes from the
// worldOrigin handed to ResourceRegistry.Load). Wound counter-clockwise seen from above (glTF convention),
// normal +Y, UVs tiled so one texture repeat covers ~2 m however large the quad is.
// <para>
// No new shader and no new pipeline: the grass is a procedural TEXTURE fed through the existing PBR material path,
// so the ground is a ModelAsset like any other. A flat grey floor was the first attempt and it was useless — the
// studio HDRI blows it out to white and the shadow drowns in it. Grass gives the two things a shadow needs to be
// readable: a dark, non-metallic, fully rough albedo, and fine high-frequency detail whose edges make shadow crawl
// visible at a glance.
// </para>
static ModelAsset BuildGroundModel(float size)
{
    var h = size * 0.5f;
    // One grass tile ≈ 4 m of ground. Tiling it tighter (2 m) put ~20 repeats across the quad, and at a grazing
    // angle that many repeats of a fine texture beat against the pixel grid — the moiré "grid" the first version
    // showed. Fewer, larger repeats keep the minification gentle.
    var tiles = MathF.Max(size / 4f, 1f);
    var mesh = new MeshAsset
    {
        Positions = [new(-h, 0f, -h), new(h, 0f, -h), new(h, 0f, h), new(-h, 0f, h)],
        Normals = [Vector3.UnitY, Vector3.UnitY, Vector3.UnitY, Vector3.UnitY],
        Uvs = [new(0f, 0f), new(tiles, 0f), new(tiles, tiles), new(0f, tiles)],
        Indices = [0, 2, 1, 0, 3, 2],
        MaterialIndex = 0,
        Name = "Ground",
    };

    var material = new MaterialAsset
    {
        BaseColorFactor = Vector4.One, // the colour lives in the texture
        BaseColorImage = 0,
        MetallicFactor = 0f,
        RoughnessFactor = 1f,
        Name = "GrassMaterial",
    };

    return new ModelAsset
    {
        Meshes = [mesh],
        Materials = [material],
        Images = [BuildGrassImage()],
        Name = "Ground",
    };
}

// An outdoor sky as an equirectangular HDR, generated at load — no asset to ship, and physically consistent with
// the sun the scene actually uses (<paramref name="sunDirection"/> is the light's propagation direction, so the sun
// disc goes at -sunDirection). Linear radiance, values well above 1 around the sun: that is what makes the IBL read
// as daylight rather than as a flat grey dome.
// <para>
// Layout matches HdrImageLoader's equirect convention: u wraps azimuth, v goes top (zenith, v=0) to bottom (nadir).
// The lower hemisphere is a dim, desaturated bounce — no second ground, just enough to keep undersides from reading
// as a void.
// </para>
static HdrImageAsset BuildSkyEnvironment(Vector3 sunDirection)
{
    const int Width = 512;
    const int Height = 256;
    var pixels = new float[Width * Height * 4];

    var sun = sunDirection.LengthSquared() > 1e-8f
        ? Vector3.Normalize(-sunDirection) // toward the sun
        : Vector3.UnitY;

    var zenith = new Vector3(0.10f, 0.24f, 0.68f);
    var horizon = new Vector3(0.66f, 0.76f, 0.92f);
    // Below the horizon the environment must READ as the grass continuing to the horizon, not as a grey wall the
    // scene is boxed in by — that grey band is exactly what made the studio probe feel like a corridor.
    var distantGround = new Vector3(0.10f, 0.17f, 0.06f);

    for (var y = 0; y < Height; y++)
    {
        // v in [0,1] → polar angle [0, pi]; the direction is the unit vector that pixel looks at.
        var theta = (y + 0.5f) / Height * MathF.PI;
        var sinTheta = MathF.Sin(theta);
        var cosTheta = MathF.Cos(theta); // +1 at the zenith, -1 at the nadir

        for (var x = 0; x < Width; x++)
        {
            var phi = ((x + 0.5f) / Width * 2f * MathF.PI) - MathF.PI;
            var dir = new Vector3(sinTheta * MathF.Sin(phi), cosTheta, -sinTheta * MathF.Cos(phi));

            Vector3 color;
            if (cosTheta >= 0f)
            {
                // Sky: zenith → horizon, biased so most of the gradient sits near the horizon (as in reality).
                var t = MathF.Pow(1f - cosTheta, 2.5f);
                color = Vector3.Lerp(zenith, horizon, t) * 1.6f;
            }
            else
            {
                // Below the horizon: distant grass, hazed toward the horizon line and darkening toward the nadir.
                var t = MathF.Pow(1f + cosTheta, 0.6f); // 1 at the horizon, 0 straight down
                color = Vector3.Lerp(distantGround * 0.55f, Vector3.Lerp(distantGround, horizon, 0.35f), t);
            }

            // Sun: a small bright disc with a soft glow around it. Radiance far above 1 — this is the HDR part, and
            // what gives the specular highlights their punch.
            var cosToSun = Vector3.Dot(dir, sun);
            if (cosToSun > 0.9986f) // ≈ 3° disc, a few times the real sun for a usable highlight
            {
                color += new Vector3(120f, 112f, 96f);
            }
            else if (cosToSun > 0.9f)
            {
                var glow = MathF.Pow((cosToSun - 0.9f) / 0.0986f, 4f);
                color += new Vector3(6f, 5.4f, 4.4f) * glow;
            }

            var o = (((y * Width) + x) * 4);
            pixels[o + 0] = color.X;
            pixels[o + 1] = color.Y;
            pixels[o + 2] = color.Z;
            pixels[o + 3] = 1f;
        }
    }

    return new HdrImageAsset { RgbaPixels = pixels, Width = Width, Height = Height };
}

// A seamless, tileable grass albedo, generated once at load (512², sRGB, fixed seed so captures stay reproducible).
// <para>
// The first version drew 1-pixel blades into an 8-bit buffer at 256²: viewed at a grazing angle, that much
// high-frequency detail beats against the pixel grid and the ground reads as a MOIRÉ GRID — no amount of anisotropic
// filtering saves a texture whose detail is finer than a texel of its own mip chain. This version is built to
// minify gracefully: it accumulates in LINEAR float, splats each blade with a soft (anti-aliased) footprint rather
// than a hard pixel, keeps the blade/base contrast modest, and finishes with a small wrapped blur so the finest
// frequencies are gone before the mip chain is ever built.
// </para>
static ImageAsset BuildGrassImage()
{
    const int Size = 512;
    var linear = new float[Size * Size * 3];
    var rng = new Random(1337);

    // Base: dark green with low-frequency patchiness. The clumps are Gaussian blobs summed into a scalar field,
    // seamless because every distance is taken modulo the tile.
    Span<(float X, float Y, float Amp, float Radius)> clumps = stackalloc (float, float, float, float)[24];
    for (var i = 0; i < clumps.Length; i++)
    {
        clumps[i] = (
            (float)rng.NextDouble() * Size,
            (float)rng.NextDouble() * Size,
            ((float)rng.NextDouble() * 2f) - 1f,
            40f + ((float)rng.NextDouble() * 100f));
    }

    for (var y = 0; y < Size; y++)
    {
        for (var x = 0; x < Size; x++)
        {
            var patch = 0f;
            foreach (var c in clumps)
            {
                var dx = WrappedDelta(x - c.X, Size);
                var dy = WrappedDelta(y - c.Y, Size);
                var d2 = ((dx * dx) + (dy * dy)) / (c.Radius * c.Radius);
                patch += c.Amp * MathF.Exp(-d2);
            }

            var shade = Math.Clamp(0.5f + (patch * 0.22f), 0f, 1f);
            var o = (((y * Size) + x) * 3);
            linear[o + 0] = 0.055f + (0.035f * shade);
            linear[o + 1] = 0.135f + (0.12f * shade);
            linear[o + 2] = 0.042f + (0.04f * shade);
        }
    }

    // Blades: short strokes, splatted with a soft footprint so each one is anti-aliased instead of a hard pixel
    // line. Modest tint (±20% of the base) — blades that fight the base colour are exactly what aliases.
    for (var i = 0; i < 14000; i++)
    {
        var x0 = (float)rng.NextDouble() * Size;
        var y0 = (float)rng.NextDouble() * Size;
        var angle = ((float)rng.NextDouble() * MathF.PI) - (MathF.PI * 0.5f); // mostly upright, leaning either way
        var length = 5f + ((float)rng.NextDouble() * 8f);
        var tint = ((float)rng.NextDouble() * 2f) - 1f; // lighter or darker than the base
        var dx = MathF.Sin(angle);
        var dy = -MathF.Cos(angle);

        for (var t = 0f; t < length; t += 0.35f)
        {
            var fade = (1f - (t / length)) * 0.2f; // the tip fades out; 0.2 caps the contrast
            Splat(linear, Size, x0 + (dx * t), y0 + (dy * t), tint * fade);
        }
    }

    Blur(linear, Size); // kills the last single-texel frequencies, which no mip level can represent

    var pixels = new byte[Size * Size * 4];
    for (var i = 0; i < Size * Size; i++)
    {
        pixels[(i * 4) + 0] = LinearToSrgb(linear[(i * 3) + 0]);
        pixels[(i * 4) + 1] = LinearToSrgb(linear[(i * 3) + 1]);
        pixels[(i * 4) + 2] = LinearToSrgb(linear[(i * 3) + 2]);
        pixels[(i * 4) + 3] = 255;
    }

    return new ImageAsset { Rgba8Pixels = pixels, Width = Size, Height = Size, IsSrgb = true };

    // Adds `amount` (a relative brightening/darkening of the green) over the four texels the point straddles,
    // weighted bilinearly — the blade gets sub-texel edges instead of a stair-stepped one.
    static void Splat(float[] buffer, int size, float x, float y, float amount)
    {
        var ix = (int)MathF.Floor(x);
        var iy = (int)MathF.Floor(y);
        var fx = x - ix;
        var fy = y - iy;

        for (var j = 0; j <= 1; j++)
        {
            for (var i = 0; i <= 1; i++)
            {
                var w = (i == 0 ? 1f - fx : fx) * (j == 0 ? 1f - fy : fy);
                var px = ((ix + i) % size + size) % size;
                var py = ((iy + j) % size + size) % size;
                var o = (((py * size) + px) * 3);
                buffer[o + 0] = MathF.Max(0f, buffer[o + 0] * (1f + (amount * w)));
                buffer[o + 1] = MathF.Max(0f, buffer[o + 1] * (1f + (amount * w * 1.4f)));
                buffer[o + 2] = MathF.Max(0f, buffer[o + 2] * (1f + (amount * w)));
            }
        }
    }

    // A wrapped 3x3 box blur. Cheap, and it is what turns "detail the mip chain cannot represent" (which aliases)
    // into "detail it can" (which filters).
    static void Blur(float[] buffer, int size)
    {
        var source = (float[])buffer.Clone();
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var o = (((y * size) + x) * 3);
                for (var c = 0; c < 3; c++)
                {
                    var sum = 0f;
                    for (var j = -1; j <= 1; j++)
                    {
                        for (var i = -1; i <= 1; i++)
                        {
                            var px = ((x + i) % size + size) % size;
                            var py = ((y + j) % size + size) % size;
                            sum += source[(((py * size) + px) * 3) + c];
                        }
                    }

                    buffer[o + c] = sum / 9f;
                }
            }
        }
    }

    // The shortest signed distance on a wrapped axis: what makes the tile seamless.
    static float WrappedDelta(float d, int size)
    {
        var half = size * 0.5f;
        if (d > half)
        {
            d -= size;
        }
        else if (d < -half)
        {
            d += size;
        }

        return d;
    }

    // Linear colour in, sRGB byte out — the image is declared sRGB, so the encode must happen here.
    static byte LinearToSrgb(float c)
    {
        c = Math.Clamp(c, 0f, 1f);
        var s = c <= 0.0031308f ? c * 12.92f : (1.055f * MathF.Pow(c, 1f / 2.4f)) - 0.055f;
        return (byte)Math.Clamp(MathF.Round(s * 255f), 0f, 255f);
    }
}

// Spawns rows×cols copies of the model, centred on the base position, spaced on the X/Z plane. Each cell offsets
// every spec's position and bumps its draw order so the whole grid has unique, deterministic SortKeys.
static void SpawnGrid(GameWorld world, ImportedEntitySpec[] specs, int rows, int cols, double spacing)
{
    var halfR = (rows - 1) * 0.5;
    var halfC = (cols - 1) * 0.5;
    for (var r = 0; r < rows; r++)
    {
        for (var c = 0; c < cols; c++)
        {
            var offset = new Double3((c - halfC) * spacing, 0, (r - halfR) * spacing);
            var cell = (r * cols) + c;
            var orderBase = (uint)(cell * specs.Length);
            foreach (var s in specs)
            {
                world.SpawnImported(new ImportedEntitySpec(
                    s.Mesh, s.Material, s.Position + offset, s.RotationScale,
                    s.BoundsCenter, s.BoundsRadius, orderBase + s.Order));
            }
        }
    }
}

// Spawns N physics BODIES (P3-M3): copies of the model as a cloud above the ground, each a rigid body with a
// per-drawable collision radius. Laid out on a square X/Z lattice (so W1, with no body-body collision yet, lands a
// non-overlapping field), with deterministic jitter and staggered start heights so the cloud rains down over ~1 s
// rather than landing in one flat slap. Returns the ground Y: one radius below the model's natural centre, so a
// body at its natural height rests exactly ON the plane. Deterministic by index — the reproducible-capture gate.
static double SpawnDropScene(GameWorld world, ImportedEntitySpec[] specs, int n, Double3 worldOrigin)
{
    var radius = 1f; // representative collision radius across the model's drawables; spacing/ground scale to it
    foreach (var s in specs)
    {
        radius = MathF.Max(radius, s.BoundsRadius * MathHelpers.MaxStretch(s.RotationScale));
    }

    // A compact CUBE cluster (side ≈ ∛n) dropped above the ground: spaced just over a diameter apart so nothing
    // overlaps at spawn, it falls as a loose lattice and settles into a PILE — the outer bodies roll off the ones
    // beneath, which is the whole point of the body-body collision (W2). A flat lattice would only ever land side by
    // side. Deterministic jitter breaks the crystal so the heap looks natural.
    var side = Math.Max(1, (int)Math.Ceiling(Math.Cbrt(n)));
    var hspacing = radius * 2.1;
    var vspacing = radius * 2.2;
    var half = (side - 1) * 0.5;
    var perLayer = side * side;
    var groundY = worldOrigin.Y - radius;

    for (var i = 0; i < n; i++)
    {
        var layer = i / perLayer;
        var inLayer = i % perLayer;
        var cx = inLayer % side;
        var cz = inLayer / side;
        var h = Hash(i);
        var jx = (((h & 0xFFFF) / 65535.0) - 0.5) * radius * 0.4;
        var jz = ((((h >> 16) & 0xFFFF) / 65535.0) - 0.5) * radius * 0.4;
        var offset = new Double3(
            ((cx - half) * hspacing) + jx,
            (radius * 4.0) + (layer * vspacing), // stacked layers, whole cube above the ground
            ((cz - half) * hspacing) + jz);

        var orderBase = (uint)(i * specs.Length);
        foreach (var s in specs)
        {
            var bodyRadius = s.BoundsRadius * MathHelpers.MaxStretch(s.RotationScale);
            world.SpawnBody(
                new ImportedEntitySpec(
                    s.Mesh, s.Material, s.Position + offset, s.RotationScale,
                    s.BoundsCenter, s.BoundsRadius, orderBase + s.Order),
                velocity: Vector3.Zero, inverseMass: 1f, restitution: 0.3f, radius: bodyRadius);
        }
    }

    return groundY;

    // A cheap integer hash (Knuth multiplicative + xorshift finalizer): deterministic per-index jitter, no RNG state.
    static uint Hash(int i)
    {
        var x = (uint)i * 2654435761u;
        x ^= x >> 15;
        x *= 2246822519u;
        x ^= x >> 13;
        return x;
    }
}

// The scene's centre (in double — it may be 10 000 km out) and the float diagonal the framing and the light
// setup scale themselves by. The extent is measured RELATIVE to the bounds' own corner, so the diagonal of a
// far-away model stays exact instead of being the difference of two enormous floats; only then is the (small)
// half-extent added back onto the double centre. An empty world collapses to a degenerate zero box.
static (Double3 Center, float Diagonal) NarrowBounds(in Double3Bounds bounds)
{
    if (bounds.IsEmpty)
    {
        return (Double3.Zero, 0f);
    }

    var extent = bounds.Max.ToVector3(bounds.Min);
    return (bounds.Min + new Double3(extent * 0.5f), extent.Length());
}

static void SetupLights(SceneLights lights, in Double3Bounds bounds, bool multiInstance)
{
    var (center, diagonal) = NarrowBounds(in bounds);
    var reach = MathF.Max(diagonal, 0.001f);

    // AGAPANTHE_SUN="x,y,z" overrides the sun's propagation direction (debug: the P3-M7 shadow-cull near-cut is
    // margin-tuned to slice thickness, not shadow length, so a LOW sun — small |y| — is the case the visual gate
    // must exercise for light leaks). Default is a fairly steep warm key.
    var sunDir = new Vector3(0.4f, -0.7f, -0.6f); // down, slightly right, toward the model front
    if (Environment.GetEnvironmentVariable("AGAPANTHE_SUN") is { } sunSpec)
    {
        var parts = sunSpec.Split(',');
        if (parts.Length == 3
            && float.TryParse(parts[0], System.Globalization.CultureInfo.InvariantCulture, out var sx)
            && float.TryParse(parts[1], System.Globalization.CultureInfo.InvariantCulture, out var sy)
            && float.TryParse(parts[2], System.Globalization.CultureInfo.InvariantCulture, out var sz))
        {
            sunDir = Vector3.Normalize(new Vector3(sx, sy, sz));
        }
    }

    lights.Directional = new DirectionalLight
    {
        Direction = sunDir,
        Color = new Vector3(1f, 0.96f, 0.9f),        // warm sun
        // The sun must DOMINATE the studio HDRI's ambient, or its shadow is a faint smudge on the ground and the
        // whole shadow path (fit, crawl, caster cull) is unreadable — at intensity 3 it was. Paired with the
        // sub-1 exposure below, which keeps the bright HDRI backdrop from clipping to white.
        Intensity = 12f,
    };

    // Multi-instance scene: sun + IBL only. The point-light rig below scales to the (ground-inflated) diagonal, so
    // on a grid it places lights hundreds of metres out and inverse-square falloff smears a brightness gradient
    // across the foule. Keep the rig for the single-model showcase, where the scene is a couple of metres wide.
    if (multiInstance)
    {
        lights.PointCount = 0;
        lights.Ambient = new Vector3(0.08f, 0.08f, 0.09f);
        return;
    }

    lights.Points[0] = new PointLight
    {
        Position = center + new Double3(new Vector3(-0.8f, 0.9f, -1.1f) * reach), // rim: behind-left, above
        Color = new Vector3(0.55f, 0.65f, 1f),                       // cool blue
        Intensity = 4f * reach * reach,                              // inverse-square: scale by distance²
        Range = 6f * reach,
    };
    lights.Points[1] = new PointLight
    {
        Position = center + new Double3(new Vector3(0.9f, -0.2f, 1.2f) * reach), // fill: front-right, slightly low
        Color = new Vector3(1f, 0.85f, 0.7f),                        // warm
        Intensity = 1.5f * reach * reach,
        Range = 6f * reach,
    };
    lights.PointCount = 2;
    // Slightly above the 0.03 engine default: without IBL (M7) the ambient is the only thing
    // keeping unlit metal from reading as a void, so the Sandbox favors legibility.
    lights.Ambient = new Vector3(0.08f, 0.08f, 0.09f);
}

static void FrameCamera(Camera camera, FreeCameraController controller, Renderer renderer, in Double3Bounds bounds)
{
    var (center, diagonal) = NarrowBounds(in bounds);

    if (diagonal <= 0f)
    {
        // Degenerate scene (no geometry → collapsed AABB): keep a sane default so the app still runs.
        camera.Position = new Double3(0, 0, 3);
        camera.Yaw = 0f;
        camera.Pitch = 0f;
        return;
    }

    var distance = MathF.Max(diagonal * 1.5f, 0.001f);

    // Look at the model from the front (+Z) and slightly above. dir points from centre toward the camera.
    // AGAPANTHE_VIEW="x,y,z" overrides the framing direction (debug: reproduce a reported
    // angle in headless captures). dir points from the model center toward the camera.
    var dir = Vector3.Normalize(new Vector3(0f, 0.35f, 1f));
    if (Environment.GetEnvironmentVariable("AGAPANTHE_VIEW") is { } viewSpec)
    {
        var parts = viewSpec.Split(',');
        if (parts.Length == 3
            && float.TryParse(parts[0], System.Globalization.CultureInfo.InvariantCulture, out var vx)
            && float.TryParse(parts[1], System.Globalization.CultureInfo.InvariantCulture, out var vy)
            && float.TryParse(parts[2], System.Globalization.CultureInfo.InvariantCulture, out var vz))
        {
            dir = Vector3.Normalize(new Vector3(vx, vy, vz));
        }
    }
    // The eye stays in double: at 10 000 km, a float eye position would already be quantized to the metre.
    camera.Position = center + new Double3(dir * distance);

    // Orient yaw/pitch so Forward = (sy·cp, sp, -cy·cp) points from the eye to the centre (= -dir).
    var forward = -dir;
    camera.Pitch = MathF.Asin(Math.Clamp(forward.Y, -1f, 1f));
    camera.Yaw = MathF.Atan2(forward.X, -forward.Z);

    // Scale near/far to the model so nothing clips regardless of its absolute size.
    camera.Near = MathF.Max(diagonal * 0.01f, 0.01f);
    camera.Far = (distance + diagonal) * 4f;

    // Scale the travel speed to the model too: crossing the whole bounding box should take a
    // couple of seconds regardless of the model's absolute size (a fixed speed felt insane on
    // a 1-unit helmet). Shift sprints at 3x.
    controller.MoveSpeed = MathF.Max(diagonal * 0.5f, 0.01f);

    // Shadow range scaled to the model (a fixed 100 m would be absurd on a 2 m helmet), floored at the cascade
    // range so distant geometry still gets shadows.
    // <para>
    // This used to be CLAMPED to 50 m (session 16), because a SINGLE cascade cannot be both sharp and long-range:
    // stretched over a large scene it gave ~0.12 m/texel and grazing ring acne, so the range was sacrificed. P3-M5
    // makes that trade obsolete — four texel-snapped cascades stay sharp near AND far — so the cap is lifted and
    // the range now follows the cascades (Renderer.Cascades.MaxDistance), which is what puts shadows back under
    // distant helmets. Keeping the old 50 m would silently brake the CSM at a quarter of its range.
    // </para>
    renderer.ShadowDistance = MathF.Max(MathF.Max(diagonal * 4f, 1f), renderer.Cascades.MaxDistance);
}

file static class DebugViews
{
    public static readonly string[] Names =
    [
        "PBR", "shaded normal", "geometric normal", "base color", "metallic",
        "roughness", "occlusion", "tangent (+handedness)", "key NdotL", "shadow factor", "CSM cascade",
    ];
}

// Load-bench animator: spins every drawable about world +Y by a fixed step each frame. Incremental (no base
// pose needed) and deterministic by frame count. Spin preserves the bounding sphere, so the scene extent stays
// valid without a per-frame refold. A struct, so GameWorld.AnimateDrawables dispatches to it without boxing.
file readonly struct SpinAnimator(float deltaRadians) : IDrawableAnimator
{
    private readonly Matrix4x4 _delta = Matrix4x4.CreateRotationY(deltaRadians);

    public void Animate(ulong globalId, ref Double3 position, ref Matrix4x4 rotationScale)
        => rotationScale = _delta * rotationScale; // rotation only — translation row stays zero (position carries it)
}

// The load bench, as a Simulation system (P3-M2 V3): spin every drawable and drift the camera yaw, both by a fixed
// step per frame so headless captures stay deterministic. This is APP business — it belongs in the sample, not the
// engine — and registering it proves the scheduler takes application systems. It replaces the head of the old
// closure verbatim, so it runs at the same point in the frame.
file sealed class BenchSpinSystem(GameWorld world, Camera camera) : ISystem
{
    public void Execute(in TickContext ctx)
    {
        var spin = new SpinAnimator(0.02f);
        world.AnimateDrawables(ref spin);
        camera.Yaw += 0.01f;
    }
}

// Churn stress (P3-M2 F2), as a Simulation system: each frame spawn a small hierarchy and, once the ring is full,
// despawn the oldest root (cascading to its children). The scheduler's end-of-Simulation barrier applies the
// structural changes; there is no manual flush. Nodes carry no MeshRef, so they never touch what is drawn — churn
// stresses the lifecycle path without perturbing the render baseline.
file sealed class ChurnSystem(GameWorld world, int perFrame) : ISystem
{
    private readonly Queue<EntityRef> _roots = new();

    public void Execute(in TickContext ctx)
    {
        for (var i = 0; i < perFrame; i++)
        {
            var root = world.Spawn(Double3.Zero, Quaternion.Identity, 1f);
            world.Spawn(new Double3(0, 1, 0), Quaternion.Identity, 1f, root);
            world.Spawn(new Double3(1, 0, 0), Quaternion.Identity, 1f, root);
            _roots.Enqueue(root);
        }

        while (_roots.Count > perFrame * 8)
        {
            world.Despawn(_roots.Dequeue()); // cascades to the subtree
        }
    }
}
