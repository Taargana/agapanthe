using System.Numerics;
using Agapanthe.Assets;
using Agapanthe.Core;
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
var shadowCasters = new RenderList();
var sceneBounds = Double3Bounds.Empty;

var camera = new Camera();
var controller = new FreeCameraController();
var resizePending = false;

// Hoisted out of the render handler so the delegate is allocated once, not per frame
// (spec §3.2.5, zero managed allocation on the hot path).
Action<CommandList, FrameContext, SwapchainTarget>? drawScene = null;

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
    var worldOrigin = ParseDouble3(Environment.GetEnvironmentVariable("AGAPANTHE_WORLD_ORIGIN"));
    var (modelId, specs) = registry.Load(
        device, model, renderer.MaterialAllocator, renderer.MaterialSetLayout, worldOrigin);

    world = new GameWorld();
    foreach (var spec in specs)
    {
        world.SpawnImported(in spec);
    }

    // System 2: the scene extent now comes from the world (union of per-entity world bounds), replacing
    // Scene.Bounds*. Static in M2 (nothing moves), so it is folded once here rather than every frame.
    sceneBounds = world.AggregateBounds();

    // Frame the model: sit the camera back by 1.5x the diagonal along a slightly-raised front direction,
    // orient yaw/pitch to look at the centre.
    FrameCamera(camera, controller, in sceneBounds);

    // Default M5 lighting: a warm directional key (sun) plus a cool rim and a soft fill point
    // light placed from the scene bounds — a classic 3-point setup that reads PBR materials
    // well. HDR intensities (> 1) are expected; the ACES tonemap compresses them.
    SetupLights(renderer.Lights, in sceneBounds);

    // Load the HDRI environment and generate IBL (M7). The renderer needs an environment before it can draw
    // (the ambient and skybox both sample it). Default is the fixture copied into models/ next to the
    // executable; AGAPANTHE_HDRI=<path> swaps in any other equirectangular .hdr.
    var iblHdrPath = Environment.GetEnvironmentVariable("AGAPANTHE_HDRI") is { Length: > 0 } hdriOverride
        ? hdriOverride
        : Path.Combine(modelsDir, "studio_small_1k.hdr");
    if (File.Exists(iblHdrPath))
    {
        renderer.SetEnvironment(HdrImageLoader.Load(iblHdrPath));
        Log.Info($"Sandbox: environment '{iblHdrPath}'.");
    }
    else
    {
        Log.Error($"Sandbox: HDRI environment '{iblHdrPath}' not found; the M7 renderer requires one to draw.");
    }

    // The scene clear color lives on the Renderer now (it owns the scene pass); the FrameRenderer only
    // drives sync/acquire/present and no longer carries a clear color.
    frameRenderer = new FrameRenderer(device, swapchain, () => window.FramebufferSize);

    // Capturing the world/registry/lists/camera/renderer (all assigned by now) exactly once — the delegate is
    // built a single time here, never per frame. The per-frame chain is the contract of spec §3.5: propagate
    // transforms (system 1), then build the two render lists (passthrough in M2, culled in M4), then draw.
    // AggregateBounds is NOT re-run per frame: nothing moves in M2, so the extent is folded once above.
    drawScene = (cmd, frame, target) =>
    {
        // ONE view per frame (spec §3.3): the world narrows every object against view.Origin, and the renderer
        // narrows the lights and the shadow fit against the same one. A single value = a single origin.
        var view = camera.CreateView();
        world!.PropagateTransforms();
        world.CollectRenderLists(renderList, shadowCasters, view.Origin);
        renderer!.DrawScene(
            renderList, shadowCasters, registry!, in view, cmd, frame, target, in sceneBounds);
    };

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
            renderer.DebugView = (renderer.DebugView + 1) % 10;
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

    var input = new CameraInput(
        moveForward: window.IsKeyDown(Key.W),
        moveBackward: window.IsKeyDown(Key.S),
        moveLeft: window.IsKeyDown(Key.A),
        moveRight: window.IsKeyDown(Key.D),
        moveUp: window.IsKeyDown(Key.Space),
        // ControlLeft is intercepted by macOS in some setups (Ctrl+click = right click), so C
        // doubles as the descend key.
        moveDown: window.IsKeyDown(Key.ControlLeft) || window.IsKeyDown(Key.C),
        lookDelta: window.MouseDelta,
        sprint: window.IsKeyDown(Key.ShiftLeft));
    controller.Update(camera, (float)dt, in input);
};

window.Rendered += _ =>
{
    if (frameRenderer is null || swapchain is null)
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

    frameRenderer.DrawFrame(drawScene!);

    if (maxFrames > 0 && ++renderedFrames >= maxFrames)
    {
        // Headless debug capture: AGAPANTHE_CAPTURE=<path.ppm> dumps the tonemapped HDR target
        // on the last frame, so rendering issues can be inspected without a windowed session.
        if (Environment.GetEnvironmentVariable("AGAPANTHE_CAPTURE") is { Length: > 0 } capturePath && renderer is not null)
        {
            frameRenderer.WaitIdle();
            renderer.SaveHdrCapture(capturePath);
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

static void SetupLights(SceneLights lights, in Double3Bounds bounds)
{
    var (center, diagonal) = NarrowBounds(in bounds);
    var reach = MathF.Max(diagonal, 0.001f);

    lights.Directional = new DirectionalLight
    {
        Direction = new Vector3(0.4f, -0.7f, -0.6f), // down, slightly right, toward the model front
        Color = new Vector3(1f, 0.96f, 0.9f),        // warm sun
        Intensity = 3f,
    };
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

static void FrameCamera(Camera camera, FreeCameraController controller, in Double3Bounds bounds)
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
}

file static class DebugViews
{
    public static readonly string[] Names =
    [
        "PBR", "shaded normal", "geometric normal", "base color", "metallic",
        "roughness", "occlusion", "tangent (+handedness)", "key NdotL", "shadow factor",
    ];
}
