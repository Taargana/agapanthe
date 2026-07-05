using System.Numerics;
using Agapanthe.Assets;
using Agapanthe.Core;
using Agapanthe.Graphics;
using Agapanthe.Platform;
using Agapanthe.Rendering;
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

var shaderDir = Path.Combine(AppContext.BaseDirectory, "shaders");
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

using var window = new EngineWindow("Agapanthe — M5 PBR", 1280, 720);

GraphicsDevice? device = null;
Swapchain? swapchain = null;
Renderer? renderer = null;
Scene? scene = null;
FrameRenderer? frameRenderer = null;

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

    // Renderer owns the mesh pipeline, both descriptor set layouts, the material allocator and the
    // per-frame camera UBOs. It exposes MaterialAllocator/MaterialSetLayout for the SceneBuilder.
    renderer = new Renderer(device, swapchain, shaderDir);

    // Load the model to CPU DTOs (no GPU dependency), then upload it into a GPU-resident Scene.
    var model = GltfLoader.Load(modelPath);
    LogModelStats(model, modelPath);
    scene = SceneBuilder.Build(device, model, renderer.MaterialAllocator, renderer.MaterialSetLayout);

    // Frame the model: compute its world-space AABB, sit the camera back by 1.5x the diagonal along a
    // slightly-raised front direction, and orient yaw/pitch to look at the centre.
    FrameCamera(camera, controller, model);

    // Default M5 lighting: a warm directional key (sun) plus a cool rim and a soft fill point
    // light placed from the model bounds — a classic 3-point setup that reads PBR materials
    // well. HDR intensities (> 1) are expected; the ACES tonemap compresses them.
    SetupLights(renderer.Lights, model);

    // The scene clear color lives on the Renderer now (it owns the scene pass); the FrameRenderer only
    // drives sync/acquire/present and no longer carries a clear color.
    frameRenderer = new FrameRenderer(device, swapchain, () => window.FramebufferSize);

    // Capturing 'scene'/'camera'/'renderer' (all assigned by now) exactly once — the delegate is built a
    // single time here, never per frame.
    drawScene = (cmd, frame, target) => renderer!.DrawScene(scene!, camera, cmd, frame, target);

    Log.Info($"Sandbox: initialized on '{device.AdapterName}'. Rendering '{scene.Name}' — " +
             "clic pour capturer la souris, WASD + souris, Échap libère puis quitte.");
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

    frameRenderer.DrawFrame(drawScene!);

    if (maxFrames > 0 && ++renderedFrames >= maxFrames)
    {
        window.Close();
    }
};

try
{
    window.Run();
}
finally
{
    // Tear down in strict order, GPU-idle first — runs even if init threw inside the Loaded handler, so
    // the leak report is always produced. Order (M4-11):
    //   WaitIdle -> frameRenderer.Dispose -> scene.Dispose (meshes/materials/textures/samplers, deferred)
    //   -> renderer.Dispose (pipeline/layouts/allocator/UBOs; the allocator destroys its pools synchronously,
    //      which is why scene — whose material sets came from that allocator — is disposed first)
    //   -> device.DeletionQueue.FlushAll() (drain every deferred destroy while the device is alive and idle)
    //   -> swapchain -> device -> window.
    frameRenderer?.WaitIdle();
    frameRenderer?.Dispose();
    scene?.Dispose();
    renderer?.Dispose();
    device?.DeletionQueue.FlushAll();
    swapchain?.Dispose();
    device?.Dispose();
    window.Dispose();
}

var clean = ResourceTracker.Report();
Log.Info(clean ? "Sandbox: clean shutdown, no GPU resource leaks." : "Sandbox: LEAKS DETECTED (see above).");
Environment.Exit(clean ? 0 : 1);

// --- Local helpers -----------------------------------------------------------------------------------

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

// Positions the camera to frame the whole model: world-space AABB over every mesh's transformed
// positions, then sit back 1.5x the diagonal along a slightly-raised front direction and aim at the centre.
// 3-point lighting from the model's world bounds: warm key (directional), cool rim point light
// behind/above, soft warm fill point light front/below. Ranges scale with the model diagonal.
static void SetupLights(SceneLights lights, Agapanthe.Assets.Model.ModelAsset model)
{
    var (center, diagonal) = ModelBounds(model);
    var reach = MathF.Max(diagonal, 0.001f);

    lights.Directional = new DirectionalLight
    {
        Direction = new Vector3(0.4f, -0.7f, -0.6f), // down, slightly right, toward the model front
        Color = new Vector3(1f, 0.96f, 0.9f),        // warm sun
        Intensity = 3f,
    };
    lights.Points[0] = new PointLight
    {
        Position = center + new Vector3(-0.8f, 0.9f, -1.1f) * reach, // rim: behind-left, above
        Color = new Vector3(0.55f, 0.65f, 1f),                       // cool blue
        Intensity = 4f * reach * reach,                              // inverse-square: scale by distance²
        Range = 6f * reach,
    };
    lights.Points[1] = new PointLight
    {
        Position = center + new Vector3(0.9f, -0.2f, 1.2f) * reach,  // fill: front-right, slightly low
        Color = new Vector3(1f, 0.85f, 0.7f),                        // warm
        Intensity = 1.5f * reach * reach,
        Range = 6f * reach,
    };
    lights.PointCount = 2;
    lights.Ambient = new Vector3(0.03f, 0.03f, 0.035f);
}

static (Vector3 Center, float Diagonal) ModelBounds(Agapanthe.Assets.Model.ModelAsset model)
{
    var min = new Vector3(float.PositiveInfinity);
    var max = new Vector3(float.NegativeInfinity);
    foreach (var mesh in model.Meshes)
    {
        foreach (var localPos in mesh.Positions)
        {
            var p = Vector3.Transform(localPos, mesh.WorldTransform);
            min = Vector3.Min(min, p);
            max = Vector3.Max(max, p);
        }
    }

    return min.X > max.X
        ? (Vector3.Zero, 1f)
        : ((min + max) * 0.5f, Vector3.Distance(min, max));
}

static void FrameCamera(Camera camera, FreeCameraController controller, Agapanthe.Assets.Model.ModelAsset model)
{
    var min = new Vector3(float.PositiveInfinity);
    var max = new Vector3(float.NegativeInfinity);
    var any = false;

    foreach (var mesh in model.Meshes)
    {
        var world = mesh.WorldTransform;
        foreach (var localPos in mesh.Positions)
        {
            var p = Vector3.Transform(localPos, world);
            min = Vector3.Min(min, p);
            max = Vector3.Max(max, p);
            any = true;
        }
    }

    if (!any)
    {
        // Degenerate model (no geometry): keep a sane default so the app still runs.
        camera.Position = new Vector3(0f, 0f, 3f);
        camera.Yaw = 0f;
        camera.Pitch = 0f;
        return;
    }

    var center = (min + max) * 0.5f;
    var diagonal = Vector3.Distance(min, max);
    var distance = MathF.Max(diagonal * 1.5f, 0.001f);

    // Look at the model from the front (+Z) and slightly above. dir points from centre toward the camera.
    var dir = Vector3.Normalize(new Vector3(0f, 0.35f, 1f));
    camera.Position = center + (dir * distance);

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
