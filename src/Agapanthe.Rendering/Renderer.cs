using System.Numerics;
using System.Runtime.CompilerServices;
using Agapanthe.Core;
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
/// <summary>
/// Read-only knobs for the directional shadow pass (M6, architect decisions 6-9). These are baked into the
/// shadow pipeline and the shadow map at <see cref="Renderer"/> construction; the depth bias in particular is
/// static pipeline state, so changing any of them would require rebuilding the renderer (out of scope for M6,
/// where a single cascade with fixed values is enough — CSM and runtime tuning are phase 2). Exposed purely so
/// the Sandbox and diagnostics can read what is in effect.
/// </summary>
/// <param name="Resolution">Square shadow-map side in texels (2048² for M6).</param>
/// <param name="DepthBiasConstant">Constant depth-bias factor fighting acne (<c>depthBiasConstantFactor</c>).</param>
/// <param name="DepthBiasSlope">Slope-scaled depth-bias factor (<c>depthBiasSlopeFactor</c>).</param>
public readonly record struct ShadowSettings(uint Resolution, float DepthBiasConstant, float DepthBiasSlope);

public sealed class Renderer : IDisposable
{
    /// <summary>Depth attachment format the scene pass renders against; the pipeline declares the same.</summary>
    public const PixelFormat DepthFormat = PixelFormat.D32Sfloat;

    /// <summary>
    /// Directional shadow-map side in texels (architect decision 9). Square, D32, created once at construction
    /// and invariant to swapchain resize (it is <b>not</b> an <see cref="EnsureTargets"/> attachment).
    /// </summary>
    public const uint ShadowMapResolution = 2048;

    // Slope-scaled depth bias for the shadow pass, retained from M6 capture tuning (see the type remarks and
    // the M6-03 write-up). Constant fights the flat-surface acne floor; slope handles grazing-angle acne where
    // one shadow texel spans many depth units. Static pipeline state — changing needs a pipeline rebuild.
    private const float ShadowDepthBiasConstant = 1.25f;
    private const float ShadowDepthBiasSlope = 1.75f;

    // Position-only view over the mesh vertex layout for the shadow pass: it keeps the full 60-byte stride
    // (the mesh vertex buffers are bound unchanged) but declares ONLY location 0, so shadow.vert consumes
    // every declared attribute — no "attribute not consumed by vertex shader" validation warning.
    private static readonly VertexLayout ShadowVertexLayout = new(
        stride: 60,
        attributes: [new VertexAttribute(Location: 0, Offset: 0, PixelFormat.R32G32B32Sfloat)]);

    /// <summary>
    /// HDR scene-color format (architect decision 2): the scene pass renders here (linear, unclamped), and
    /// the tonemap pass (decision 3) resolves it to the sRGB swapchain. The scene pipeline declares this as
    /// its color format; the tonemap pipeline declares the swapchain format.
    /// </summary>
    public const PixelFormat HdrFormat = PixelFormat.Rgba16Sfloat;

    // Per-frame-slot camera UBOs: host-visible, rewritten every frame (Write<T> is correct — no staging).
    private readonly GpuBuffer?[] _cameraUbos = new GpuBuffer?[GraphicsDevice.FramesInFlight];
    private readonly GpuBuffer?[] _lightsUbos = new GpuBuffer?[GraphicsDevice.FramesInFlight];

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

    // Directional shadow pass resources (architect decisions 6-9). The shadow map is a D32 2048² depth image
    // that is BOTH a depth attachment (shadow pass writes it) and sampled (scene pass reads it through a
    // comparison sampler). Created once at construction — invariant to swapchain resize, so unlike the HDR /
    // depth targets it lives outside EnsureTargets. The shadow pipeline is depth-only (no fragment shader):
    // model + lightViewProj travel as 128 bytes of push constants, no descriptor set (decision 7).
    private ShaderModule? _shadowVertexShader;
    private GpuImage? _shadowMap;
    private Sampler? _shadowSampler;
    private GraphicsPipeline? _shadowPipeline;

    // Same WAR pattern as _hdrInitialized on the single shared shadow map: first use acquires from Undefined,
    // every later frame from ShaderReadOnly (the previous frame's scene-pass sample), so the depth write
    // serializes after that read.
    private bool _shadowInitialized;

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

            // Set 0 = per-frame data (spec §3.4): binding 0 camera (view/proj/position — the PBR
            // fragment stage needs the eye position), binding 1 lights (M5, decision 4), binding 2 the
            // directional shadow map as a comparison combined-image-sampler (M6, decision 6).
            _frameSetLayout = new DescriptorSetLayout(
                device,
                [
                    new DescriptorBinding(0, DescriptorKind.UniformBuffer, ShaderStages.Vertex | ShaderStages.Fragment),
                    new DescriptorBinding(1, DescriptorKind.UniformBuffer, ShaderStages.Fragment),
                    new DescriptorBinding(2, DescriptorKind.CombinedImageSampler, ShaderStages.Fragment),
                ]);

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
                PushConstants =
                [
                    new PushConstantRange(0, 64, ShaderStages.Vertex),   // model matrix
                    new PushConstantRange(64, 4, ShaderStages.Fragment), // debug view selector
                ],
                // Scene renders into the HDR target (decision 2), not the swapchain: the tonemap pass owns
                // the sRGB swapchain write.
                ColorFormat = HdrFormat,
                DepthFormat = DepthFormat,
                DepthTest = true,
                // glTF winding is CCW viewed from outside. Our PerspectiveVulkan bakes the Y-flip
                // into the projection, so the image is upright and a world-CCW triangle appears
                // visually CCW on screen — and Vulkan's front-face formula (spec 'Basic Polygon
                // Rasterization', note the leading minus sign compensating the y-down framebuffer)
                // classifies visually-CCW as COUNTER-clockwise. The old Clockwise choice came from
                // applying the negative-viewport-height folklore plus a shoelace computed without
                // that sign — it culled every front face of DamagedHelmet (fixed after M5 visual
                // debugging: Cull None restored the intact shell, the decisive experiment).
                FrontFace = FrontFace.CounterClockwise,
                Cull = CullMode.Back,
            });

            for (var i = 0; i < _cameraUbos.Length; i++)
            {
                _cameraUbos[i] = new GpuBuffer(device, (ulong)Unsafe.SizeOf<CameraUniforms>(), BufferUsage.Uniform);
                _lightsUbos[i] = new GpuBuffer(device, (ulong)Unsafe.SizeOf<LightsUniforms>(), BufferUsage.Uniform);
            }

            // --- Directional shadow pass (decisions 6-9) ----------------------------------------------------
            // Shadow map: D32, 2048², both depth attachment (shadow pass writes) and sampled (scene pass reads).
            // Created here, once — it is invariant to swapchain resize, so it stays out of EnsureTargets.
            Shadow = new ShadowSettings(ShadowMapResolution, ShadowDepthBiasConstant, ShadowDepthBiasSlope);
            _shadowMap = new GpuImage(
                device, ShadowMapResolution, ShadowMapResolution, DepthFormat,
                ImageUsage.DepthAttachment | ImageUsage.Sampled);

            // Shadow sampler. Ideally this would be a comparison sampler (sampler2DShadow, LessOrEqual) for free
            // 2x2 hardware PCF, but MoltenVK's VK_KHR_portability_subset reports mutableComparisonSamplers =
            // FALSE on Apple silicon: a comparison sampler written through vkUpdateDescriptorSets is rejected
            // (VUID-VkDescriptorImageInfo-mutableComparisonSamplers-04450). So we sample the depth as a plain
            // texture and compare manually in mesh.frag (manual 3x3 PCF). Nearest filtering because linear would
            // blend raw depth values before the compare (wrong); each of the nine taps is compared individually.
            // ClampToEdge — out-of-frustum UVs are rejected in the shader (a plain sampler's border is opaque
            // black, which would read as depth 0 = spuriously shadowed, so we must not rely on the border).
            _shadowSampler = new Sampler(device, new SamplerDesc(
                Filter: SamplerFilter.Nearest,
                MipFilter: SamplerFilter.Nearest,
                AddressMode: SamplerAddressMode.ClampToEdge));

            // Depth-only shadow pipeline: shadow.vert alone (no fragment shader, no color attachment), a
            // position-only vertex layout that KEEPS the mesh's 60-byte stride (so the mesh vertex buffers feed
            // it verbatim) but declares only location 0 — declaring the full layout instead trips a validation
            // warning per unconsumed attribute (locations 1-4). 128 bytes of push constants (lightViewProj @0,
            // model @64) with NO descriptor set. Same CCW winding + back-face cull as the scene (M5 lesson);
            // slope-scaled depth bias (rasterizer state, independent of the sampler) fights acne.
            var shadowVertSpirv = _shaderCompiler.CompileFile(Path.Combine(shaderDirectory, "shadow.vert"), ShaderStage.Vertex);
            _shadowVertexShader = new ShaderModule(device, shadowVertSpirv, ShaderStage.Vertex);
            _shadowPipeline = new GraphicsPipeline(device, new GraphicsPipelineDesc
            {
                VertexShader = _shadowVertexShader,
                FragmentShader = null,
                VertexLayout = ShadowVertexLayout,
                SetLayouts = [],
                PushConstants = [new PushConstantRange(0, 128, ShaderStages.Vertex)],
                ColorFormat = PixelFormat.Undefined,
                DepthFormat = DepthFormat,
                DepthTest = true,
                DepthBiasConstant = ShadowDepthBiasConstant,
                DepthBiasSlope = ShadowDepthBiasSlope,
                Cull = CullMode.Back,
                FrontFace = FrontFace.CounterClockwise,
            });

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
    /// Scene lighting state, packed into set 0 binding 1 every frame. Mutate it directly
    /// (directional key light, up to 4 point lights, constant ambient placeholder until IBL M7).
    /// </summary>
    public SceneLights Lights { get; } = new();

    /// <summary>
    /// The directional shadow settings baked into this renderer (resolution + depth bias). Read-only in M6 —
    /// see <see cref="ShadowSettings"/>. Assigned once at construction.
    /// </summary>
    public ShadowSettings Shadow { get; private set; }

    /// <summary>
    /// Shading debug visualization (0 = normal PBR). Values map to the DEBUG_* selector in
    /// <c>mesh.frag</c>: 1 shaded normal, 2 geometric normal, 3 base color, 4 metallic,
    /// 5 roughness, 6 occlusion, 7 tangent (+handedness tint), 8 key-light NdotL,
    /// 9 directional shadow factor (greyscale, 1 = lit / 0 = shadowed).
    /// </summary>
    public int DebugView { get; set; }

    /// <summary>
    /// Debug capture: reads the HDR scene target back to the CPU, applies the same exposure +
    /// ACES + sRGB chain as the tonemap pass, and writes a binary PPM (P6). Call after at least
    /// one rendered frame, with the GPU idle (the caller WaitIdles). Stalls the queue — debug only.
    /// </summary>
    public void SaveHdrCapture(string path)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_hdrImage is null || !_hdrInitialized)
        {
            throw new InvalidOperationException("Nothing rendered yet — the HDR target is uninitialized.");
        }

        // 8 bytes/texel (Rgba16Sfloat); the image sits in ShaderReadOnly after the tonemap pass.
        var bytes = GpuReadback.ReadImage(_device, _hdrImage, ImageLayoutState.ShaderReadOnly, bytesPerTexel: 8);
        var width = (int)_hdrImage.Width;
        var height = (int)_hdrImage.Height;

        using var output = new FileStream(path, FileMode.Create, FileAccess.Write);
        var header = System.Text.Encoding.ASCII.GetBytes($"P6\n{width} {height}\n255\n");
        output.Write(header);

        var halfs = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, Half>(bytes);
        var row = new byte[width * 3];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = ((y * width) + x) * 4;
                row[(x * 3) + 0] = EncodeChannel((float)halfs[i + 0]);
                row[(x * 3) + 1] = EncodeChannel((float)halfs[i + 1]);
                row[(x * 3) + 2] = EncodeChannel((float)halfs[i + 2]);
            }

            output.Write(row);
        }

        Core.Log.Info($"Renderer: HDR capture saved to '{path}' ({width}x{height}).");

        byte EncodeChannel(float value)
        {
            // Mirror tonemap.frag: exposure, ACES fitted (Narkowicz), then sRGB OETF.
            var x = value * Exposure;
            x = Math.Clamp((x * ((2.51f * x) + 0.03f)) / ((x * ((2.43f * x) + 0.59f)) + 0.14f), 0f, 1f);
            var srgb = x <= 0.0031308f ? x * 12.92f : (1.055f * MathF.Pow(x, 1f / 2.4f)) - 0.055f;
            return (byte)Math.Clamp((int)((srgb * 255f) + 0.5f), 0, 255);
        }
    }

    /// <summary>
    /// Records the three-pass HDR frame (architect decisions 2-3 + M6 shadow, decision 10) into
    /// <paramref name="cmd"/>, resolving into the swapchain <paramref name="target"/>. Meant to be the body of
    /// the <see cref="FrameRenderer.DrawFrame"/> callback:
    /// <c>frameRenderer.DrawFrame((cmd, frame, target) => renderer.DrawScene(scene, camera, cmd, frame, target))</c>.
    /// <para>
    /// Orchestration (decision 10): <see cref="EnsureTargets"/> → <see cref="RecordShadowPass"/> →
    /// <see cref="RecordScenePass"/> → <see cref="RecordTonemapPass"/>. The directional light's
    /// <c>lightViewProj</c> is computed once here (<see cref="ComputeLightViewProj"/>) and shared: the shadow
    /// pass renders depth from the light's viewpoint into the shadow map, the scene pass packs the same matrix
    /// into the lights UBO so the fragment stage can re-project each surface for the PCF lookup, and the
    /// tonemap pass resolves the HDR target to the sRGB swapchain.
    /// </para>
    /// <para>
    /// Hot path: no managed allocation — index loops over the instance list, in-place span writes of the UBOs,
    /// engine attachment/target/handle types are all structs, no LINQ, no closures. The lightViewProj is
    /// passed <c>in</c> to the sub-passes to avoid recomputing it.
    /// </para>
    /// </summary>
    public void DrawScene(Scene scene, Camera camera, CommandList cmd, FrameContext frame, SwapchainTarget target)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(camera);
        ArgumentNullException.ThrowIfNull(frame);
        ObjectDisposedException.ThrowIf(_disposed, this);

        // HDR-color and depth targets are owned here; keep them sized to the swapchain image handed in by the
        // frame loop (recreated together behind a device wait when the extent changes). The shadow map is
        // resolution-invariant and lives outside this.
        EnsureTargets(target.Width, target.Height);

        // One light-space transform per frame, shared by the shadow pass (render depth) and the scene pass
        // (pack into the lights UBO for the PCF lookup).
        var lightViewProj = ComputeLightViewProj(scene);

        RecordShadowPass(cmd, scene, in lightViewProj);
        RecordScenePass(cmd, frame, scene, camera, target, in lightViewProj);
        RecordTonemapPass(cmd, frame, target);
    }

    /// <summary>
    /// Fits the directional light's orthographic frustum to the scene's world AABB and returns its
    /// <c>view · proj</c> (row-vector; a world point maps to light clip space as <c>p · result</c>, and std140
    /// uploads it transposed so the shaders multiply <c>result * vec4(worldPos, 1)</c>). One cascade covering
    /// the whole scene (spec §6 M6 — CSM is phase 2).
    /// <para>
    /// <b>Fit.</b> Centre = <see cref="Scene.BoundsCenter"/>; radius = half the space diagonal + 10% margin;
    /// the eye sits at <c>centre − dir · 2·radius</c> looking at the centre; the ortho spans <c>2·radius</c> on
    /// each axis with near/far clamped to <c>[0.5·radius, 3.5·radius]</c> from the eye (the scene sphere of
    /// radius <c>r</c> sits at <c>[r, 3r]</c> from the eye, comfortably inside). The up vector is +Y, swapped
    /// to +Z when the light points nearly along ±Y (else <see cref="MathHelpers.LookAt"/> degenerates). A
    /// degenerate/empty scene (zero diagonal) falls back to a unit radius so the matrix stays finite.
    /// </para>
    /// </summary>
    private Matrix4x4 ComputeLightViewProj(Scene scene)
    {
        var dir = Lights.Directional.Direction;
        dir = dir.LengthSquared() > 1e-12f ? Vector3.Normalize(dir) : new Vector3(0f, -1f, 0f);

        var center = scene.BoundsCenter;
        var radius = scene.BoundsDiagonal * 0.5f;
        radius = radius > 1e-4f ? radius * 1.1f : 1f; // 10% margin; unit fallback for an empty scene

        var eye = center - (dir * (radius * 2f));
        var up = MathF.Abs(dir.Y) > 0.99f ? Vector3.UnitZ : Vector3.UnitY;
        var view = MathHelpers.LookAt(eye, center, up);
        var proj = MathHelpers.OrthographicVulkan(2f * radius, 2f * radius, radius * 0.5f, radius * 3.5f);
        return view * proj;
    }

    /// <summary>
    /// Pass 0 (M6, decision 7): renders scene depth from the directional light's viewpoint into the shadow
    /// map. Depth-only, no descriptor set — <paramref name="lightViewProj"/> (offset 0) and each mesh's world
    /// transform (offset 64) are pushed as constants. The shadow map's WAR acquire mirrors the HDR target: the
    /// first frame comes from Undefined, later frames from ShaderReadOnly (the previous frame's scene-pass
    /// sample), so this depth write serializes after that read. loadOp=Clear(1.0), storeOp=Store because the
    /// scene pass samples the result; a closing DepthAttachment→ShaderReadOnly barrier hands it over.
    /// </summary>
    private void RecordShadowPass(CommandList cmd, Scene scene, in Matrix4x4 lightViewProj)
    {
        var pipeline = _shadowPipeline!;
        var shadow = _shadowMap!;

        cmd.TransitionImage(
            shadow,
            _shadowInitialized ? ImageLayoutState.ShaderReadOnly : ImageLayoutState.Undefined,
            ImageLayoutState.DepthAttachment);
        _shadowInitialized = true;

        cmd.BeginRendering(new RenderingAttachments
        {
            Color = null,
            Depth = new DepthAttachmentInfo
            {
                Target = new RenderTargetView(shadow),
                LoadOp = AttachmentLoadAction.Clear,
                ClearDepth = 1f,
                Store = true, // sampled by the scene pass — must survive
            },
            Width = ShadowMapResolution,
            Height = ShadowMapResolution,
        });
        cmd.SetViewportScissor(ShadowMapResolution, ShadowMapResolution);

        cmd.BindPipeline(pipeline);
        // lightViewProj is constant across the pass: push it once (offset 0), then per-instance model (offset 64).
        cmd.PushConstants(pipeline, ShaderStages.Vertex, in lightViewProj, offsetBytes: 0);

        var instances = scene.Instances;
        for (var i = 0; i < instances.Count; i++)
        {
            var mesh = instances[i].Mesh;
            cmd.BindVertexBuffer(mesh.VertexBuffer);
            cmd.BindIndexBuffer(mesh.IndexBuffer, mesh.IndexFormat);

            var model = mesh.WorldTransform;
            cmd.PushConstants(pipeline, ShaderStages.Vertex, in model, offsetBytes: 64);
            cmd.DrawIndexed(mesh.IndexCount);
        }

        cmd.EndRendering();

        // Hand the depth result to the scene pass as a sampled texture: wait for the late-fragment-test depth
        // writes before the scene fragment stage samples it (DepthAttachment→ShaderReadOnly, RAW barrier).
        cmd.TransitionImage(shadow, ImageLayoutState.DepthAttachment, ImageLayoutState.ShaderReadOnly);
    }

    /// <summary>
    /// Pass 1 (scene → HDR): the M5 forward PBR pass, now shadowed. Acquires the HDR + depth targets, begins
    /// rendering into the <see cref="HdrFormat"/> target (loadOp=Clear both), writes this slot's camera + lights
    /// UBOs (the lights UBO carries <paramref name="lightViewProj"/> for the fragment shadow lookup), points a
    /// fresh per-frame set 0 at those UBOs plus the shadow map (binding 2, now in ShaderReadOnly from the shadow
    /// pass), then for each instance binds its material set 1 and mesh buffers, pushes the world transform and
    /// issues the indexed draw.
    /// </summary>
    private void RecordScenePass(
        CommandList cmd, FrameContext frame, Scene scene, Camera camera, SwapchainTarget target,
        in Matrix4x4 lightViewProj)
    {
        var pipeline = _pipeline!;
        var hdr = _hdrImage!;
        var depth = _depthImage!;

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
        var uniforms = new CameraUniforms(camera.ViewMatrix, camera.ProjectionMatrix, camera.Position);
        ubo.Write(new ReadOnlySpan<CameraUniforms>(in uniforms));

        var lightsUbo = _lightsUbos[frame.Slot]!;
        var lightsUniforms = new LightsUniforms(Lights, lightViewProj);
        lightsUbo.Write(new ReadOnlySpan<LightsUniforms>(in lightsUniforms));

        var frameSet = frame.AllocateSet(_frameSetLayout!);
        frame.WriteUniformBuffer(frameSet, 0, ubo);
        frame.WriteUniformBuffer(frameSet, 1, lightsUbo);
        // The shadow map is in ShaderReadOnly (RecordShadowPass closing barrier), matching the descriptor's
        // declared layout; sampled through the comparison sampler for PCF in mesh.frag.
        frame.WriteCombinedImageSampler(frameSet, 2, _shadowMap!, _shadowSampler!);

        cmd.BindPipeline(pipeline);
        cmd.BindDescriptorSet(pipeline, 0, frameSet); // set 0: per-frame camera + lights + shadow map

        var debugView = DebugView;
        cmd.PushConstants(pipeline, ShaderStages.Fragment, in debugView, offsetBytes: 64);

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
    /// Pass 2 (tonemap → swapchain): transitions the HDR target to ShaderReadOnly (WAR/RAW barrier waiting on
    /// pass-1 color writes), begins rendering into the swapchain image (already in ColorAttachment from the
    /// frame loop; loadOp=DontCare because the fullscreen triangle covers every pixel), binds the tonemap
    /// pipeline and a per-frame set pointing at the HDR target through the clamp sampler, pushes
    /// <see cref="Exposure"/> and draws the 3-vertex triangle.
    /// </summary>
    private void RecordTonemapPass(CommandList cmd, FrameContext frame, SwapchainTarget target)
    {
        var tonemapPipeline = _tonemapPipeline!;
        var hdr = _hdrImage!;

        cmd.TransitionImage(hdr, ImageLayoutState.ColorAttachment, ImageLayoutState.ShaderReadOnly);

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

        // TransferSrc so debug captures (SaveHdrCapture) can read the target back.
        _hdrImage = new GpuImage(
            _device, width, height, HdrFormat,
            ImageUsage.ColorAttachment | ImageUsage.Sampled | ImageUsage.TransferSrc);
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
        // deletion queue with FlushAll afterwards, so the HDR/depth/shadow images are freed before the leak check.
        _hdrImage?.Dispose();
        _depthImage?.Dispose();
        _hdrImage = null;
        _depthImage = null;

        // Directional shadow pass: pipeline, comparison sampler, shadow map, then the depth-only vertex shader.
        _shadowPipeline?.Dispose();
        _shadowSampler?.Dispose();
        _shadowMap?.Dispose();
        _shadowVertexShader?.Dispose();
        _shadowPipeline = null;
        _shadowSampler = null;
        _shadowMap = null;
        _shadowVertexShader = null;

        _tonemapPipeline?.Dispose();
        _tonemapSetLayout?.Dispose();
        _tonemapSampler?.Dispose();
        _tonemapFragmentShader?.Dispose();
        _tonemapVertexShader?.Dispose();

        _pipeline?.Dispose();
        _materialSetLayout?.Dispose();
        _frameSetLayout?.Dispose();
        _materialAllocator?.Dispose();
        for (var i = 0; i < _cameraUbos.Length; i++)
        {
            _cameraUbos[i]?.Dispose();
            _lightsUbos[i]?.Dispose();
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
