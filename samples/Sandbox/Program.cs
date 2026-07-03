using Agapanthe.Core;
using Agapanthe.Graphics;
using Agapanthe.Platform;

// M1 validation app: opens a window and renders the hard-coded triangle, exercising the
// full Graphics bootstrap (instance/device/swapchain), shaderc compilation, the dynamic-
// rendering pipeline and the synchronization2 frame loop. On exit it prints the
// ResourceTracker report — a clean run leaks nothing.

var shaderDir = Path.Combine(AppContext.BaseDirectory, "shaders");

// Auto-close after N rendered frames when AGAPANTHE_MAX_FRAMES is set (CI/headless validation).
var maxFrames = int.TryParse(Environment.GetEnvironmentVariable("AGAPANTHE_MAX_FRAMES"), out var mf) ? mf : -1;
var renderedFrames = 0;

using var window = new EngineWindow("Agapanthe — M1 Triangle", 1280, 720);

GraphicsDevice? device = null;
Swapchain? swapchain = null;
ShaderCompiler? shaderCompiler = null;
ShaderModule? vertexShader = null;
ShaderModule? fragmentShader = null;
GraphicsPipeline? pipeline = null;
FrameRenderer? renderer = null;
var resizePending = false;

// Hoisted out of the render handler so the delegate is allocated once, not per frame
// (spec §3.2.5, zero managed allocation on the hot path).
Action<CommandList>? recordTriangle = null;

window.Loaded += () =>
{
    var requiredExtensions = window.GetRequiredVulkanExtensions();
    device = new GraphicsDevice("Agapanthe Sandbox", requiredExtensions, window.VkSurface!);

    var (width, height) = window.FramebufferSize;
    swapchain = new Swapchain(device, width, height);

    shaderCompiler = new ShaderCompiler();
    var vertSpirv = shaderCompiler.CompileFile(Path.Combine(shaderDir, "triangle.vert"), ShaderStage.Vertex);
    var fragSpirv = shaderCompiler.CompileFile(Path.Combine(shaderDir, "triangle.frag"), ShaderStage.Fragment);
    vertexShader = new ShaderModule(device, vertSpirv, ShaderStage.Vertex);
    fragmentShader = new ShaderModule(device, fragSpirv, ShaderStage.Fragment);
    pipeline = new GraphicsPipeline(device, vertexShader, fragmentShader, swapchain.ColorFormat);

    renderer = new FrameRenderer(device, swapchain, () => window.FramebufferSize)
    {
        ClearColor = (0.02f, 0.02f, 0.05f, 1f),
    };

    recordTriangle = cmd =>
    {
        cmd.BindPipeline(pipeline!);
        cmd.Draw(3);
    };

    Log.Info("Sandbox: initialized. Rendering triangle.");
};

// MoltenVK does not always report OUT_OF_DATE on resize; recreate explicitly to be safe.
window.FramebufferResized += (_, _) => resizePending = true;

window.Rendered += _ =>
{
    if (renderer is null || swapchain is null)
    {
        return;
    }

    if (resizePending)
    {
        resizePending = false;
        var (width, height) = window.FramebufferSize;
        if (width > 0 && height > 0)
        {
            swapchain.Recreate(width, height);
        }
    }

    renderer.DrawFrame(recordTriangle!);

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
    // Tear down in reverse creation order, GPU-idle first — runs even if init threw
    // inside the Loaded handler, so the leak report is always produced.
    renderer?.WaitIdle();
    renderer?.Dispose();
    pipeline?.Dispose();
    fragmentShader?.Dispose();
    vertexShader?.Dispose();
    shaderCompiler?.Dispose();
    swapchain?.Dispose();
    device?.Dispose();
    window.Dispose();
}

var clean = ResourceTracker.Report();
Log.Info(clean ? "Sandbox: clean shutdown, no GPU resource leaks." : "Sandbox: LEAKS DETECTED (see above).");
Environment.Exit(clean ? 0 : 1);
