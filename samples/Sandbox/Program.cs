using System.Numerics;
using System.Runtime.InteropServices;
using Agapanthe.Core;
using Agapanthe.Graphics;
using Agapanthe.Platform;
using Agapanthe.Rendering;
using Silk.NET.Input;

// M2 validation app: renders a spinning per-face-colored cube with depth testing, seen
// through a free camera (WASD + mouse look, Space/Ctrl for up/down, Escape to quit).
// Exercises vertex/index buffers, the camera UBO on set 0, the model-matrix push constant
// and per-frame descriptor pools. On exit it prints the ResourceTracker report.

var shaderDir = Path.Combine(AppContext.BaseDirectory, "shaders");

// Auto-close after N rendered frames when AGAPANTHE_MAX_FRAMES is set (CI/headless validation).
var maxFrames = int.TryParse(Environment.GetEnvironmentVariable("AGAPANTHE_MAX_FRAMES"), out var mf) ? mf : -1;
var renderedFrames = 0;

using var window = new EngineWindow("Agapanthe — M2 Cube", 1280, 720);

GraphicsDevice? device = null;
Swapchain? swapchain = null;
ShaderCompiler? shaderCompiler = null;
ShaderModule? vertexShader = null;
ShaderModule? fragmentShader = null;
DescriptorSetLayout? frameSetLayout = null;
GraphicsPipeline? pipeline = null;
FrameRenderer? renderer = null;
GpuBuffer? vertexBuffer = null;
GpuBuffer? indexBuffer = null;
var cameraUbos = new GpuBuffer?[GraphicsDevice.FramesInFlight];

var camera = new Camera { Position = new Vector3(0f, 1.2f, 3f), Pitch = -0.3f };
var controller = new FreeCameraController();
var modelAngle = 0f;
uint indexCount = 0;
var resizePending = false;

// Hoisted out of the render handler so the delegate is allocated once, not per frame
// (spec §3.2.5, zero managed allocation on the hot path).
Action<CommandList, FrameContext>? recordCube = null;

window.Loaded += () =>
{
    var requiredExtensions = window.GetRequiredVulkanExtensions();
    device = new GraphicsDevice("Agapanthe Sandbox", requiredExtensions, window.VkSurface!);

    var (width, height) = window.FramebufferSize;
    swapchain = new Swapchain(device, width, height);
    camera.AspectRatio = (float)width / height;

    shaderCompiler = new ShaderCompiler();
    var vertSpirv = shaderCompiler.CompileFile(Path.Combine(shaderDir, "cube.vert"), ShaderStage.Vertex);
    var fragSpirv = shaderCompiler.CompileFile(Path.Combine(shaderDir, "cube.frag"), ShaderStage.Fragment);
    vertexShader = new ShaderModule(device, vertSpirv, ShaderStage.Vertex);
    fragmentShader = new ShaderModule(device, fragSpirv, ShaderStage.Fragment);

    var (vertices, indices) = Primitives.Cube();
    indexCount = (uint)indices.Length;
    vertexBuffer = new GpuBuffer(device, (ulong)(vertices.Length * Marshal.SizeOf<Vertex>()), BufferUsage.Vertex);
    vertexBuffer.Write<Vertex>(vertices);
    indexBuffer = new GpuBuffer(device, (ulong)(indices.Length * sizeof(ushort)), BufferUsage.Index);
    indexBuffer.Write<ushort>(indices);

    for (var i = 0; i < cameraUbos.Length; i++)
    {
        cameraUbos[i] = new GpuBuffer(device, (ulong)Marshal.SizeOf<CameraUniforms>(), BufferUsage.Uniform);
    }

    frameSetLayout = new DescriptorSetLayout(
        device,
        [new DescriptorBinding(Binding: 0, DescriptorKind.UniformBuffer, ShaderStages.Vertex)]);

    // Declare only the attributes cube.vert consumes (position + color); normal and uv stay
    // in the 44-byte stride but an unconsumed attribute is a validation warning (spec §4).
    var cubeVertexLayout = new VertexLayout(
        Vertex.Layout.Stride,
        [Vertex.Layout.Attributes[0], Vertex.Layout.Attributes[1]]);

    pipeline = new GraphicsPipeline(device, new GraphicsPipelineDesc
    {
        VertexShader = vertexShader,
        FragmentShader = fragmentShader,
        VertexLayout = cubeVertexLayout,
        SetLayouts = [frameSetLayout],
        PushConstants = [new PushConstantRange(Offset: 0, Size: 64, ShaderStages.Vertex)],
        ColorFormat = swapchain.ColorFormat,
        DepthFormat = FrameRenderer.DepthFormat,
        DepthTest = true,
        // The Y-flipped Vulkan projection (MathHelpers.PerspectiveVulkan) mirrors winding in
        // framebuffer space, so the cube's CCW-from-outside faces arrive clockwise.
        FrontFace = FrontFace.Clockwise,
    });

    renderer = new FrameRenderer(device, swapchain, () => window.FramebufferSize)
    {
        ClearColor = (0.02f, 0.02f, 0.05f, 1f),
    };

    recordCube = (cmd, frame) =>
    {
        var ubo = cameraUbos[frame.Slot]!;
        var uniforms = new CameraUniforms(camera.ViewMatrix, camera.ProjectionMatrix);
        ubo.Write<CameraUniforms>(new ReadOnlySpan<CameraUniforms>(in uniforms));

        var set = frame.AllocateSet(frameSetLayout!);
        frame.WriteUniformBuffer(set, 0, ubo);

        cmd.BindPipeline(pipeline!);
        cmd.BindDescriptorSet(pipeline!, 0, set);
        cmd.BindVertexBuffer(vertexBuffer!);
        cmd.BindIndexBuffer(indexBuffer!, IndexFormat.UInt16);

        var model = Matrix4x4.CreateRotationY(modelAngle);
        cmd.PushConstants(pipeline!, ShaderStages.Vertex, in model);
        cmd.DrawIndexed(indexCount);
    };

    Log.Info($"Sandbox: initialized on '{device.AdapterName}'. Rendering cube — WASD + souris, Échap pour quitter.");
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

window.Updated += dt =>
{
    if (window.IsKeyDown(Key.Escape))
    {
        window.Close();
        return;
    }

    modelAngle += (float)dt * 0.8f;

    var input = new CameraInput(
        moveForward: window.IsKeyDown(Key.W),
        moveBackward: window.IsKeyDown(Key.S),
        moveLeft: window.IsKeyDown(Key.A),
        moveRight: window.IsKeyDown(Key.D),
        moveUp: window.IsKeyDown(Key.Space),
        moveDown: window.IsKeyDown(Key.ControlLeft),
        lookDelta: window.MouseDelta);
    controller.Update(camera, (float)dt, in input);
};

window.Rendered += _ =>
{
    if (renderer is null || swapchain is null)
    {
        return;
    }

    if (resizePending)
    {
        resizePending = false;
        renderer.RequestResize();
    }

    renderer.DrawFrame(recordCube!);

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
    frameSetLayout?.Dispose();
    foreach (var ubo in cameraUbos)
    {
        ubo?.Dispose();
    }

    indexBuffer?.Dispose();
    vertexBuffer?.Dispose();
    fragmentShader?.Dispose();
    vertexShader?.Dispose();
    shaderCompiler?.Dispose();
    // GpuBuffer disposal is deferred through the DeletionQueue; drain it while the
    // device still exists (the device is idle at this point).
    device?.DeletionQueue.FlushAll();
    swapchain?.Dispose();
    device?.Dispose();
    window.Dispose();
}

var clean = ResourceTracker.Report();
Log.Info(clean ? "Sandbox: clean shutdown, no GPU resource leaks." : "Sandbox: LEAKS DETECTED (see above).");
Environment.Exit(clean ? 0 : 1);

/// <summary>Set 0, binding 0 — must match shaders/cube.vert layout (two mat4, 128 bytes).</summary>
[StructLayout(LayoutKind.Sequential)]
internal readonly struct CameraUniforms(Matrix4x4 view, Matrix4x4 proj)
{
    public readonly Matrix4x4 View = view;
    public readonly Matrix4x4 Proj = proj;
}
