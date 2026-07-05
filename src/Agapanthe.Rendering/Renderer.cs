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

    /// <summary>
    /// HDR scene-color format (architect decision 2): the scene pass renders here (linear, unclamped), and
    /// the tonemap pass (decision 3) resolves it to the sRGB swapchain. The scene pipeline declares this as
    /// its color format; the tonemap pipeline declares the swapchain format.
    /// </summary>
    public const PixelFormat HdrFormat = PixelFormat.Rgba16Sfloat;

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

    // Tonemap (HDR resolve) pass resources: fullscreen-triangle pipeline (no vertex buffer), a 1-binding
    // set layout (combined image sampler on the HDR target), and a clamp/linear sampler. The HDR image is
    // sampled through this sampler and resolved to the swapchain.
    private ShaderModule? _tonemapVertexShader;
    private ShaderModule? _tonemapFragmentShader;
    private DescriptorSetLayout? _tonemapSetLayout;
    private GraphicsPipeline? _tonemapPipeline;
    private Sampler? _tonemapSampler;

    // Swapchain-sized attachments owned here now that the frame loop is attachment-agnostic: the HDR scene
    // color target (rendered then sampled by the tonemap pass) and the depth target. Both are (re)created
    // together in EnsureTargets behind a device wait when the SwapchainTarget extent changes.
    private GpuImage? _hdrImage;
    private GpuImage? _depthImage;

    // Tracks whether the HDR image already holds a resolved (ShaderReadOnly) layout from a prior frame.
    // Drives the pass-1 acquire barrier: the first use (fresh/recreated image, actual layout Undefined)
    // transitions Undefined->ColorAttachment; every later frame transitions ShaderReadOnly->ColorAttachment
    // so the barrier serializes this frame's color writes AFTER the previous frame's tonemap reads (a WAR
    // hazard on the single shared HDR target — pipeline barriers order against prior submits on the queue).
    private bool _hdrInitialized;

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
                // Scene renders into the HDR target (decision 2), not the swapchain: the tonemap pass owns
                // the sRGB swapchain write.
                ColorFormat = HdrFormat,
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

            // --- Tonemap pass (decision 3): fullscreen triangle, samples the HDR target, writes the swapchain.
            var tonemapVertSpirv = _shaderCompiler.CompileFile(Path.Combine(shaderDirectory, "tonemap.vert"), ShaderStage.Vertex);
            var tonemapFragSpirv = _shaderCompiler.CompileFile(Path.Combine(shaderDirectory, "tonemap.frag"), ShaderStage.Fragment);
            _tonemapVertexShader = new ShaderModule(device, tonemapVertSpirv, ShaderStage.Vertex);
            _tonemapFragmentShader = new ShaderModule(device, tonemapFragSpirv, ShaderStage.Fragment);

            // One binding: the HDR target as a combined image sampler, read by the fragment stage.
            _tonemapSetLayout = new DescriptorSetLayout(
                device,
                [new DescriptorBinding(0, DescriptorKind.CombinedImageSampler, ShaderStages.Fragment)]);

            // Linear filtering, clamp-to-edge: the fullscreen triangle samples the HDR target 1:1, so wrap
            // mode is irrelevant in practice, but clamp is the safe choice for a screen-space resolve.
            _tonemapSampler = new Sampler(device, new SamplerDesc(
                Filter: SamplerFilter.Linear,
                MipFilter: SamplerFilter.Linear,
                AddressMode: SamplerAddressMode.ClampToEdge));

            _tonemapPipeline = new GraphicsPipeline(device, new GraphicsPipelineDesc
            {
                VertexShader = _tonemapVertexShader,
                FragmentShader = _tonemapFragmentShader,
                // No vertex buffer: the vertex shader generates the triangle from gl_VertexIndex (Draw(3)).
                VertexLayout = null,
                SetLayouts = [_tonemapSetLayout],
                // 4-byte float exposure, fragment stage only.
                PushConstants = [new PushConstantRange(0, 4, ShaderStages.Fragment)],
                // Resolves to the sRGB swapchain; the format's OETF replaces any gamma pow in the shader.
                ColorFormat = swapchain.ColorFormat,
                // No depth attachment and no depth test: a fullscreen resolve. DepthFormat stays Undefined.
                DepthTest = false,
                // The triangle is a screen-space quad; face orientation is meaningless, so cull nothing.
                Cull = CullMode.None,
            });
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
    /// Linear exposure multiplier applied to the HDR color before tonemapping in the tonemap pass (decision
    /// 3). 1 is neutral; the Sandbox drives it in log2 steps. Pushed as a 4-byte fragment push constant.
    /// </summary>
    public float Exposure { get; set; } = 1f;

    /// <summary>
    /// Records the two-pass HDR frame (architect decisions 2-3) into <paramref name="cmd"/>, resolving into the
    /// swapchain <paramref name="target"/>. Meant to be the body of the <see cref="FrameRenderer.DrawFrame"/>
    /// callback:
    /// <c>frameRenderer.DrawFrame((cmd, frame, target) => renderer.DrawScene(scene, camera, cmd, frame, target))</c>.
    /// <para>
    /// <b>Pass 1 (scene → HDR).</b> It ensures HDR-color and depth targets matching the target extent
    /// (recreated together behind a device wait on resize), transitions them to color/depth attachments
    /// (loadOp=Clear both), begins rendering into the <see cref="HdrFormat"/> target, sets the viewport, then
    /// updates this slot's camera UBO, binds the per-frame set 0 and the scene pipeline, and for each instance
    /// binds its material set 1 and mesh buffers, pushes the world transform and issues the indexed draw.
    /// </para>
    /// <para>
    /// <b>Pass 2 (tonemap → swapchain).</b> It transitions the HDR target to ShaderReadOnly, begins rendering
    /// into the swapchain image (already in ColorAttachment from the frame loop; loadOp=DontCare because the
    /// fullscreen triangle covers every pixel), binds the tonemap pipeline and a per-frame set pointing at the
    /// HDR target through the clamp sampler, pushes <see cref="Exposure"/> and draws the 3-vertex triangle.
    /// </para>
    /// <para>
    /// Hot path: no managed allocation — index loop over the instance list, in-place span write of the UBO,
    /// engine attachment/target/handle types are all structs, no LINQ, no closures.
    /// </para>
    /// </summary>
    public void DrawScene(Scene scene, Camera camera, CommandList cmd, FrameContext frame, SwapchainTarget target)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(camera);
        ArgumentNullException.ThrowIfNull(frame);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var pipeline = _pipeline!;
        var tonemapPipeline = _tonemapPipeline!;

        // HDR-color and depth targets are owned here; keep them sized to the swapchain image handed in by the
        // frame loop (recreated together behind a device wait when the extent changes).
        EnsureTargets(target.Width, target.Height);
        var hdr = _hdrImage!;
        var depth = _depthImage!;

        // === Pass 1: scene -> HDR (linear Rgba16Sfloat) + depth ============================================
        // Acquire the HDR target for color writes. First use (fresh/recreated image, actual layout Undefined)
        // uses an Undefined source; every later frame comes from ShaderReadOnly (the previous frame's tonemap
        // read), so the barrier serializes these color writes AFTER that read — the WAR fix on the shared HDR
        // target. loadOp=Clear makes the discarded prior contents irrelevant either way.
        cmd.TransitionImage(
            hdr,
            _hdrInitialized ? ImageLayoutState.ShaderReadOnly : ImageLayoutState.Undefined,
            ImageLayoutState.ColorAttachment);
        _hdrInitialized = true;

        // loadOp=Clear makes the prior depth contents irrelevant, so a fresh Undefined->attachment transition
        // each frame is correct (its aspect-aware source stage serializes against the previous frame's depth).
        cmd.TransitionImage(depth, ImageLayoutState.Undefined, ImageLayoutState.DepthAttachment);

        cmd.BeginRendering(new RenderingAttachments
        {
            Color = new ColorAttachmentInfo
            {
                Target = new RenderTargetView(hdr),
                LoadOp = AttachmentLoadAction.Clear,
                ClearColor = ClearColor,
            },
            Depth = new DepthAttachmentInfo
            {
                Target = new RenderTargetView(depth),
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

        // === Pass 2: tonemap HDR -> swapchain =============================================================
        // WAR/RAW barrier: wait for pass-1 color writes (ColorAttachmentOutput) before the fragment stage
        // samples the HDR target as ShaderReadOnly.
        cmd.TransitionImage(hdr, ImageLayoutState.ColorAttachment, ImageLayoutState.ShaderReadOnly);

        // The swapchain image is already in ColorAttachment (frame loop). The fullscreen triangle covers every
        // pixel, so loadOp=DontCare — no clear needed and nothing prior to preserve.
        cmd.BeginRendering(new RenderingAttachments
        {
            Color = new ColorAttachmentInfo
            {
                Target = target.View,
                LoadOp = AttachmentLoadAction.DontCare,
            },
            Width = target.Width,
            Height = target.Height,
        });
        cmd.SetViewportScissor(target.Width, target.Height);

        // Fresh per-frame set pointing at the HDR target through the clamp sampler (the HDR image is now in
        // ShaderReadOnly, matching the descriptor's declared layout).
        var tonemapSet = frame.AllocateSet(_tonemapSetLayout!);
        frame.WriteCombinedImageSampler(tonemapSet, 0, hdr, _tonemapSampler!);

        cmd.BindPipeline(tonemapPipeline);
        cmd.BindDescriptorSet(tonemapPipeline, 0, tonemapSet);
        var exposure = Exposure;
        cmd.PushConstants(tonemapPipeline, ShaderStages.Fragment, in exposure);
        cmd.Draw(3);

        cmd.EndRendering();
    }

    /// <summary>
    /// Ensures the owned HDR-color and depth targets match <paramref name="width"/>×<paramref name="height"/>.
    /// Both are swapchain-sized and always created/recreated together, so the HDR extent is the single source
    /// of truth for the check. On the first call it creates both; when the extent changes (resize) it waits for
    /// the GPU to idle, releases the old images and recreates them. The swapchain itself is already recreated
    /// behind a device wait by the frame loop, so this only reacts to the new <see cref="SwapchainTarget"/>
    /// extent it observes here. A recreation resets <see cref="_hdrInitialized"/>: the new HDR image starts in
    /// the Undefined layout, so pass 1 must acquire it from Undefined (not ShaderReadOnly).
    /// </summary>
    private void EnsureTargets(uint width, uint height)
    {
        if (_hdrImage is not null && _hdrImage.Width == width && _hdrImage.Height == height)
        {
            return;
        }

        if (_hdrImage is not null || _depthImage is not null)
        {
            // Recreation only: the old images may still be referenced by an in-flight frame, so idle first,
            // then release them through the deferred deletion queue (the frame loop / FlushAll drains it — the
            // internal immediate-destroy path isn't reachable from this assembly, and after the wait deferral
            // is safe).
            _device.WaitIdle();
            _hdrImage?.Dispose();
            _depthImage?.Dispose();
            _hdrImage = null;
            _depthImage = null;
            _hdrInitialized = false;
        }

        _hdrImage = new GpuImage(_device, width, height, HdrFormat, ImageUsage.ColorAttachment | ImageUsage.Sampled);
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

    // Teardown order (also the ctor-failure cleanup path): targets, tonemap pipeline/layout/sampler/shaders,
    // scene pipeline, layouts, allocator, UBOs, scene shaders, compiler. Every ?. is a real guard: on a
    // failed construction only a prefix of these is assigned.
    private void DisposeResources()
    {
        // Deferred release: the caller idles the GPU before Dispose (see the type remarks) and drains the
        // deletion queue with FlushAll afterwards, so the HDR/depth images are freed before the leak check.
        _hdrImage?.Dispose();
        _depthImage?.Dispose();
        _hdrImage = null;
        _depthImage = null;

        _tonemapPipeline?.Dispose();
        _tonemapSetLayout?.Dispose();
        _tonemapSampler?.Dispose();
        _tonemapFragmentShader?.Dispose();
        _tonemapVertexShader?.Dispose();

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

        _tonemapPipeline = null;
        _tonemapSetLayout = null;
        _tonemapSampler = null;
        _tonemapFragmentShader = null;
        _tonemapVertexShader = null;
        _pipeline = null;
        _materialSetLayout = null;
        _frameSetLayout = null;
        _materialAllocator = null;
        _fragmentShader = null;
        _vertexShader = null;
        _shaderCompiler = null;
    }
}
