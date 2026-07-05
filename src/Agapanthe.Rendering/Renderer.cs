using System.Runtime.CompilerServices;
using Agapanthe.Graphics;

namespace Agapanthe.Rendering;

/// <summary>
/// The forward mesh renderer: it owns the whole draw pipeline that the Sandbox used to wire by hand
/// (M4-10). One <see cref="Renderer"/> compiles the mesh shaders, builds the graphics pipeline and its two
/// descriptor set layouts, holds the persistent per-material <see cref="DescriptorAllocator"/> and the
/// per-frame-in-flight camera UBOs, and draws a <see cref="Scene"/>.
/// <list type="bullet">
///   <item><b>Set 0</b> — per-frame camera (<see cref="CameraUniforms"/>), one UBO per frame slot, written
///   and bound every frame in <see cref="DrawScene"/>. Visible to the vertex stage only (M4: the fragment
///   shader does not read the camera; that is revisited if/when M5 needs view-space lighting).</item>
///   <item><b>Set 1</b> — per-material PBR (<see cref="MaterialLayout"/>), allocated from
///   <see cref="MaterialAllocator"/> by <see cref="SceneBuilder"/> and bound per instance.</item>
/// </list>
/// <para>
/// <b>Ownership.</b> The Renderer owns its shaders, both set layouts, the material allocator, the pipeline
/// and the camera UBOs. It does <b>not</b> own any <see cref="Scene"/> (the caller builds and disposes those
/// against <see cref="MaterialAllocator"/> / <see cref="MaterialSetLayout"/>). <see cref="Dispose"/> releases
/// the owned objects but does <b>not</b> wait for the GPU to idle — the caller must
/// <see cref="FrameRenderer.WaitIdle"/> first, because the material allocator destroys its pools
/// synchronously and the camera UBOs may still be referenced by in-flight frames.
/// </para>
/// </summary>
public sealed class Renderer : IDisposable
{
    /// <summary>Depth attachment format the scene pass renders against; the pipeline declares the same.</summary>
    public const PixelFormat DepthFormat = PixelFormat.D32Sfloat;

    // Per-frame-slot camera UBOs: host-visible, rewritten every frame (Write<T> is correct — no staging).
    private readonly GpuBuffer?[] _cameraUbos = new GpuBuffer?[GraphicsDevice.FramesInFlight];

    private readonly GraphicsDevice _device;
    private ShaderCompiler? _shaderCompiler;
    private ShaderModule? _vertexShader;
    private ShaderModule? _fragmentShader;
    private DescriptorSetLayout? _frameSetLayout;
    private DescriptorSetLayout? _materialSetLayout;
    private DescriptorAllocator? _materialAllocator;
    private GraphicsPipeline? _pipeline;
    // Depth target, owned here now that the frame loop is attachment-agnostic. Swapchain-sized: (re)created
    // behind a device wait when the SwapchainTarget extent changes, so DestroyImmediately (not deferred).
    private GpuImage? _depthImage;
    private bool _disposed;

    /// <summary>
    /// Builds the renderer: compiles <c>mesh.vert</c>/<c>mesh.frag</c> from <paramref name="shaderDirectory"/>,
    /// creates set 0 (camera UBO) and set 1 (<see cref="MaterialLayout"/>) layouts, the material allocator,
    /// the pipeline and one camera UBO per frame slot.
    /// </summary>
    /// <param name="device">The graphics device.</param>
    /// <param name="swapchain">Provides the color attachment format the pipeline renders to.</param>
    /// <param name="shaderDirectory">Directory holding <c>mesh.vert</c> and <c>mesh.frag</c>.</param>
    public Renderer(GraphicsDevice device, Swapchain swapchain, string shaderDirectory)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(swapchain);
        ArgumentNullException.ThrowIfNull(shaderDirectory);

        _device = device;

        try
        {
            _shaderCompiler = new ShaderCompiler();
            var vertSpirv = _shaderCompiler.CompileFile(Path.Combine(shaderDirectory, "mesh.vert"), ShaderStage.Vertex);
            var fragSpirv = _shaderCompiler.CompileFile(Path.Combine(shaderDirectory, "mesh.frag"), ShaderStage.Fragment);
            _vertexShader = new ShaderModule(device, vertSpirv, ShaderStage.Vertex);
            _fragmentShader = new ShaderModule(device, fragSpirv, ShaderStage.Fragment);

            // Set 0 = per-frame camera. Vertex-only: the M4 fragment shader does not sample the camera.
            _frameSetLayout = new DescriptorSetLayout(
                device,
                [new DescriptorBinding(0, DescriptorKind.UniformBuffer, ShaderStages.Vertex)]);

            // Set 1 = per-material, frozen 6-binding PBR shape. The allocator is public so SceneBuilder can
            // allocate persistent material sets against this layout.
            _materialSetLayout = MaterialLayout.CreateLayout(device);
            _materialAllocator = new DescriptorAllocator(device);

            _pipeline = new GraphicsPipeline(device, new GraphicsPipelineDesc
            {
                VertexShader = _vertexShader,
                FragmentShader = _fragmentShader,
                // Full 5-attribute layout — mesh.vert consumes every one (see the shader header note).
                VertexLayout = Vertex.Layout,
                SetLayouts = [_frameSetLayout, _materialSetLayout],
                PushConstants = [new PushConstantRange(0, 64, ShaderStages.Vertex)], // 64B model matrix
                ColorFormat = swapchain.ColorFormat,
                DepthFormat = DepthFormat,
                DepthTest = true,
                // glTF winding is CCW; the Y-flipped Vulkan projection mirrors it to clockwise in framebuffer
                // space, so Clockwise is the front face. Culling stays OFF in M4 (prudence: some sample
                // models have inconsistent winding and there is no lighting yet to reveal flipped faces) —
                // M5 turns on CullMode.Back once the winding is validated per model.
                FrontFace = FrontFace.Clockwise,
                Cull = CullMode.None,
            });

            for (var i = 0; i < _cameraUbos.Length; i++)
            {
                _cameraUbos[i] = new GpuBuffer(device, (ulong)Unsafe.SizeOf<CameraUniforms>(), BufferUsage.Uniform);
            }
        }
        catch
        {
            DisposeResources();
            throw;
        }
    }

    /// <summary>The persistent per-material descriptor allocator (set 1). Passed to <see cref="SceneBuilder"/>.</summary>
    public DescriptorAllocator MaterialAllocator => _materialAllocator!;

    /// <summary>The frozen set-1 layout (<see cref="MaterialLayout"/>). Passed to <see cref="SceneBuilder"/>.</summary>
    public DescriptorSetLayout MaterialSetLayout => _materialSetLayout!;

    /// <summary>Clear color for the scene pass (RGBA, linear). Default matches the M4 background.</summary>
    public (float R, float G, float B, float A) ClearColor { get; set; } = (0.02f, 0.02f, 0.05f, 1f);

    /// <summary>
    /// Opens the scene pass against the swapchain <paramref name="target"/> (color) plus this renderer's depth,
    /// then records the draws for <paramref name="scene"/> from <paramref name="camera"/>. Meant to be the body
    /// of the <see cref="FrameRenderer.DrawFrame"/> callback:
    /// <c>frameRenderer.DrawFrame((cmd, frame, target) => renderer.DrawScene(scene, camera, cmd, frame, target))</c>.
    /// <para>
    /// It ensures a depth image matching the target extent (recreated behind a device wait on resize),
    /// transitions it to a depth attachment (loadOp=Clear each frame), begins rendering, sets the viewport,
    /// then updates this slot's camera UBO, binds the per-frame set 0 and the pipeline, and for each instance
    /// binds its material set 1 and mesh buffers, pushes the mesh world transform and issues the indexed draw.
    /// Hot path: no managed allocation (index loop over the instance list, in-place span write of the UBO, no
    /// LINQ, no closures).
    /// </para>
    /// </summary>
    public void DrawScene(Scene scene, Camera camera, CommandList cmd, FrameContext frame, SwapchainTarget target)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(camera);
        ArgumentNullException.ThrowIfNull(frame);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var pipeline = _pipeline!;

        // The depth target is owned here; keep it sized to the swapchain image handed in by the frame loop.
        EnsureDepth(target.Width, target.Height);

        // loadOp=Clear makes the prior depth contents irrelevant, so a fresh Undefined->attachment transition
        // each frame is correct and cheaper than preserving the layout across frames.
        cmd.TransitionImage(_depthImage!, ImageLayoutState.Undefined, ImageLayoutState.DepthAttachment);

        cmd.BeginRendering(new RenderingAttachments
        {
            Color = new ColorAttachmentInfo
            {
                Target = target.View,
                LoadOp = AttachmentLoadAction.Clear,
                ClearColor = ClearColor,
            },
            Depth = new DepthAttachmentInfo
            {
                Target = new RenderTargetView(_depthImage!),
                LoadOp = AttachmentLoadAction.Clear,
                ClearDepth = 1f,
            },
            Width = target.Width,
            Height = target.Height,
        });
        cmd.SetViewportScissor(target.Width, target.Height);

        // Write this frame slot's camera UBO in place (host-visible), then point a fresh per-frame set 0 at it.
        var ubo = _cameraUbos[frame.Slot]!;
        var uniforms = new CameraUniforms(camera.ViewMatrix, camera.ProjectionMatrix);
        ubo.Write(new ReadOnlySpan<CameraUniforms>(in uniforms));

        var frameSet = frame.AllocateSet(_frameSetLayout!);
        frame.WriteUniformBuffer(frameSet, 0, ubo);

        cmd.BindPipeline(pipeline);
        cmd.BindDescriptorSet(pipeline, 0, frameSet); // set 0: per-frame camera

        // Index loop over IReadOnlyList<MeshInstance>: the indexer returns the struct by value with no
        // enumerator/boxing allocation (foreach on the interface would box the enumerator).
        var instances = scene.Instances;
        for (var i = 0; i < instances.Count; i++)
        {
            var instance = instances[i];
            var mesh = instance.Mesh;

            cmd.BindDescriptorSet(pipeline, 1, instance.Material.DescriptorSet); // set 1: per-material
            cmd.BindVertexBuffer(mesh.VertexBuffer);
            cmd.BindIndexBuffer(mesh.IndexBuffer, mesh.IndexFormat);

            var model = mesh.WorldTransform;
            cmd.PushConstants(pipeline, ShaderStages.Vertex, in model);
            cmd.DrawIndexed(mesh.IndexCount);
        }

        cmd.EndRendering();
    }

    /// <summary>
    /// Ensures the owned depth image matches <paramref name="width"/>×<paramref name="height"/>. On the first
    /// call it creates it; when the extent changes (resize) it waits for the GPU to idle, destroys the old
    /// image synchronously and recreates it — the same pattern the frame loop used before the depth moved here.
    /// The swapchain itself is already recreated behind a device wait by the frame loop, so this only reacts to
    /// the new <see cref="SwapchainTarget"/> extent it observes here.
    /// </summary>
    private void EnsureDepth(uint width, uint height)
    {
        if (_depthImage is not null && _depthImage.Width == width && _depthImage.Height == height)
        {
            return;
        }

        if (_depthImage is not null)
        {
            // Recreation only: the old image may still be referenced by an in-flight frame, so idle first, then
            // release it through the deferred deletion queue (the frame loop / FlushAll drains it — the internal
            // immediate-destroy path isn't reachable from this assembly, and after the wait deferral is safe).
            _device.WaitIdle();
            _depthImage.Dispose();
            _depthImage = null;
        }

        _depthImage = new GpuImage(_device, width, height, DepthFormat, ImageUsage.DepthAttachment);
    }

    /// <summary>
    /// Releases the owned objects. Does <b>not</b> wait for the GPU — the caller must
    /// <see cref="FrameRenderer.WaitIdle"/> first (the material allocator destroys its pools synchronously
    /// and the camera UBOs may still be in flight).
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        DisposeResources();
    }

    // Teardown order (also the ctor-failure cleanup path): pipeline, layouts, allocator, UBOs, shaders,
    // compiler. Every ?. is a real guard: on a failed construction only a prefix of these is assigned.
    private void DisposeResources()
    {
        // Deferred release: the caller idles the GPU before Dispose (see the type remarks) and drains the
        // deletion queue with FlushAll afterwards, so the depth image is freed before the leak check.
        _depthImage?.Dispose();
        _depthImage = null;
        _pipeline?.Dispose();
        _materialSetLayout?.Dispose();
        _frameSetLayout?.Dispose();
        _materialAllocator?.Dispose();
        foreach (var ubo in _cameraUbos)
        {
            ubo?.Dispose();
        }

        _fragmentShader?.Dispose();
        _vertexShader?.Dispose();
        _shaderCompiler?.Dispose();

        _pipeline = null;
        _materialSetLayout = null;
        _frameSetLayout = null;
        _materialAllocator = null;
        _fragmentShader = null;
        _vertexShader = null;
        _shaderCompiler = null;
    }
}
