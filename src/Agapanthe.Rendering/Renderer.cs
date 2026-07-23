using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using Agapanthe.Core;
using Agapanthe.Graphics;
using Agapanthe.Rendering.Passes;

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
///   the model's own descriptor allocator (owned by the <see cref="ResourceRegistry"/>) and bound per instance.</item>
/// </list>
/// <para>
/// <b>Ownership.</b> The Renderer owns its shaders, both set layouts, the material allocator, the pipeline
/// and the camera UBOs. It does <b>not</b> own any <see cref="Scene"/> (the caller builds and disposes those
/// against <see cref="MaterialSetLayout"/>). <see cref="Dispose"/> releases
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

/// <summary>
/// Cascaded-shadow-map tunables (P3-M5). <paramref name="Count"/> cascades split the camera frustum by depth;
/// <paramref name="Lambda"/> blends the uniform (0) and logarithmic (1) split partitions;
/// <paramref name="MaxDistance"/> is the far bound of the shadowed range (metres). The 2×2 atlas holds up to 4.
/// </summary>
/// <param name="Count">Number of cascades (≤ 4 for the 2×2 atlas).</param>
/// <param name="Lambda">Uniform↔logarithmic split blend, in [0,1] (0 = uniform, 1 = logarithmic).</param>
/// <param name="MaxDistance">Far bound of the shadowed range, in metres.</param>
public readonly record struct CascadeSettings(int Count, float Lambda, float MaxDistance)
{
    /// <summary>
    /// Four cascades shadowing out to 200 m, with a strongly logarithmic split (λ=0.85).
    /// <para>
    /// λ was 0.5 and that wasted the near cascade (audit MAJEUR-1). With a near plane of ~0.1 m the logarithmic
    /// term collapses (<c>0.1·2000^0.25 ≈ 0.67</c>), so a 50/50 blend lands close to the UNIFORM split: cascade 0
    /// reached 25 m (3.2 cm/texel), and the 5×5 PCF smeared the contact shadow over ~16 cm. At λ=0.85 cascade 0
    /// covers ~8 m at 1.0 cm/texel — <b>3.2× sharper at contact for 2.5% coarser texels in cascade 3</b>, because
    /// the far cascade's radius is dominated by <c>far·tan(fov)</c>, not by the width of its own slice. λ 0.75–0.95
    /// is the usual range for a 0.1→200 m span; 0.5 suits a short range.
    /// </para>
    /// </summary>
    public static CascadeSettings Default => new(4, 0.85f, 200f);
}

public sealed class Renderer : IDisposable
{
    /// <summary>Depth attachment format the scene pass renders against; the pipeline declares the same.</summary>
    public const PixelFormat DepthFormat = PixelFormat.D32Sfloat;

    /// <summary>
    /// Directional shadow-map side in texels (architect decision 9). Square, D32, created once at construction
    /// and invariant to swapchain resize (it is <b>not</b> an <see cref="EnsureTargets"/> attachment).
    /// <para>
    /// 4096 since the Sandbox grew a ground plane: a shadow is only as sharp as its texels are small, and a fit
    /// that must span a floor rather than a single model spreads the map over metres. 64 MiB of D32 buys back the
    /// texel density (a single cascade; CSM is the real answer when the world gets large).
    /// </para>
    /// </summary>
    public const uint ShadowMapResolution = 4096;

    /// <summary>
    /// Side of one CSM cascade's tile in the 2×2 shadow atlas (P3-M5): the 4096² map holds four 2048² cascades, so
    /// the total shadow-map memory is unchanged from the single-cascade era. Each cascade texel-snaps to THIS
    /// resolution, not to <see cref="ShadowMapResolution"/>.
    /// </summary>
    public const uint ShadowTileResolution = ShadowMapResolution / 2;

    // Slope-scaled depth bias for the shadow pass, retained from M6 capture tuning (see the type remarks and
    // the M6-03 write-up). Constant fights the flat-surface acne floor; slope handles grazing-angle acne where
    // one shadow texel spans many depth units. Static pipeline state — changing needs a pipeline rebuild.
    private const float ShadowDepthBiasConstant = 1.25f;
    private const float ShadowDepthBiasSlope = 1.75f;

    /// <summary>
    /// HDR scene-color format (architect decision 2): the scene pass renders here (linear, unclamped), and
    /// the tonemap pass (decision 3) resolves it to the sRGB swapchain. The scene pipeline declares this as
    /// its color format; the tonemap pipeline declares the swapchain format.
    /// </summary>
    public const PixelFormat HdrFormat = PixelFormat.Rgba16Sfloat;

    // Debug-label names for the per-frame passes (M8-07): grouped in a RenderDoc/Nsight capture. Literals/const
    // so no managed allocation happens when they are pushed every frame (the label API also no-ops with debug
    // utils off). Kept as fields so every recorder references the same string.
    private const string ShadowPassLabel = "Shadow";
    private const string ScenePassLabel = "Scene";
    private const string SkyboxLabel = "Skybox";
    private const string TonemapPassLabel = "Tonemap";

    // Per-frame-slot camera UBOs: host-visible, rewritten every frame (Write<T> is correct — no staging).
    private readonly GpuBuffer?[] _cameraUbos = new GpuBuffer?[GraphicsDevice.FramesInFlight];
    private readonly GpuBuffer?[] _lightsUbos = new GpuBuffer?[GraphicsDevice.FramesInFlight];

    // Per-frame-slot per-instance transform SSBOs (P3-M1): host-visible, the visible entities' model matrices
    // compacted here in sorted order each frame, read by the vertex shader via gl_InstanceIndex. Grown on demand
    // (never per-frame in steady state); the scene and shadow passes see different visible subsets, hence two.
    private readonly InstanceBufferRing _sceneTransforms;
    private readonly InstanceBufferRing _shadowTransforms;

    // One indirect-args buffer per frame in flight, per pass (P3-M4 W0): the CPU writes one
    // DrawIndexedIndirectCommand per batch and the pass issues DrawIndexedIndirect instead of DrawIndexed.
    private readonly IndirectArgsRing _sceneArgs;
    private readonly IndirectArgsRing _shadowArgs;

    // Reused per-frame indirect-command scratch (P3-M4 W0): one DrawIndexedIndirectCommand per scene batch / per
    // (cascade, mesh-batch) shadow region. Grown on demand, never re-allocated per frame (0-alloc steady state).
    private DrawIndexedIndirectCommand[] _sceneCommands = new DrawIndexedIndirectCommand[64];
    private DrawIndexedIndirectCommand[] _shadowCommands = new DrawIndexedIndirectCommand[64];

    // GPU scene cull (P3-M4 W1): the compute pipeline that frustum-culls the scene candidates and compacts the
    // survivors, plus the per-frame upload rings it reads (candidates in, per-batch base offsets in) and the
    // reused CPU scratch that fills them. The args ring (compute writes instanceCount) and the instance ring
    // (compute writes survivors) are the same _sceneArgs / _sceneTransforms the draw then consumes.
    private ShaderModule? _sceneCullShader;
    private DescriptorSetLayout? _sceneCullSetLayout;
    private ComputePipeline? _sceneCullPipeline;
    // The candidate buffer is now PERSISTENT (P3-M6): the World maintains a SceneCandidateSet (structural rebuild vs
    // per-entity dirty patch), and this holds the FramesInFlight GPU copies, synced before each cull. It replaces the
    // per-frame StorageBufferRing<SceneCandidate>.Upload of all candidates — the O(n) sort/upload regression P3-M4 left.
    private readonly PersistentInstanceBuffer _sceneCandidates;
    private readonly StorageBufferRing<uint> _batchBase;
    private uint[] _batchBaseScratch = new uint[64];

    // GPU shadow cull (P3-M6 W3): the compute pipeline that culls the persistent scene candidates against the four
    // cascade frusta and compacts casters per (cascade, mesh-batch) region — replacing the P3-M5 CPU CollectShadowCasters
    // + four managed lists. Reads the SAME persistent candidate buffer the scene cull reads (shared, read-only); writes
    // the shadow instance ring + shadow args ring. The cascade planes ride a per-frame storage buffer; the loop bounds
    // a 16-byte push constant.
    private ShaderModule? _shadowCullShader;
    private DescriptorSetLayout? _shadowCullSetLayout;
    private ComputePipeline? _shadowCullPipeline;
    private readonly StorageBufferRing<Vector4> _shadowCullPlanes;
    private readonly StorageBufferRing<uint> _shadowMeshBase;
    private Vector4[] _shadowPlaneScratch = new Vector4[4 * ShadowCullPlanesPerCascade]; // 4 cascades × 7 planes
    private uint[] _shadowMeshBaseScratch = new uint[16];

    // Cull-verification (P3-M4 W1 gate): when VerifyCull is set, CullSceneOnGpu also counts the candidates the CPU
    // frustum test keeps (the oracle) and remembers this frame's args buffer + batch count so ReadBackSceneVisible
    // can sum the instanceCounts the GPU wrote. The two must match (visually identical + same visible SET). Off the
    // hot path — only when the flag is set (the bench).
    private GpuBuffer? _lastSceneArgs;
    private int _lastSceneBatchCount;

    // Shadow-cull region-count readback (P3-M7 W3): remembers the last frame's shadow args buffer + its region
    // layout so ReadBackShadowVisible can sum the instanceCounts the shadow cull wrote, per cascade. Measures the
    // 4×→~1× raster drop from the near-cut plane, AND closes the P3-M6 debt of the shadow cull having no readback
    // gate (symmetric to ReadBackSceneVisible). Set on every frame the shadow cull dispatches (VerifyCull-gated).
    private GpuBuffer? _lastShadowArgs;
    private int _lastShadowBatchCount;
    private int _lastShadowCascadeCount;

    /// <summary>P3-M4 W1 gate: enable the CPU-vs-GPU visible-count check (bench/headless only).</summary>
    public bool VerifyCull { get; set; }

    /// <summary>The number of scene candidates the CPU frustum test keeps this frame (the cull oracle), when
    /// <see cref="VerifyCull"/> is on. Compare against <see cref="ReadBackSceneVisible"/> after the GPU idles.</summary>
    public int LastSceneCpuVisible { get; private set; }

    /// <summary>Instanced draw calls issued by the last scene / shadow pass (P3-M1 bench diagnostic).</summary>
    public int LastSceneDrawCalls { get; private set; }

    public int LastShadowDrawCalls { get; private set; }

    private readonly GraphicsDevice _device;

    // Owned here and borrowed by the reloadable passes for their Reload path (M8-05). Kept so hot reload can
    // recompile a pass without the Renderer re-plumbing anything.
    private ShaderCompiler? _shaderCompiler;

    // Set 0 (per-frame camera/lights/shadow/IBL) and set 1 (per-material) layouts + the persistent per-material
    // descriptor allocator. Owned here; ScenePass borrows the two layouts to build its pipeline.
    private DescriptorSetLayout? _frameSetLayout;
    private DescriptorSetLayout? _materialSetLayout;

    // The four reloadable passes (M8-04 seam). Each POSSESSES its shader modules, its GraphicsPipeline and a
    // copy of the stable pipeline description (formats, set layouts, cull/depth — everything but the modules),
    // so it can recompile and rebuild its pipeline at the frame boundary for shader hot reload (M8-05). The
    // Renderer keeps the stable state they borrow (set layouts, samplers, targets); the record methods below
    // bind pass.Pipeline.
    private ShadowPass? _shadowPass;
    private ScenePass? _scenePass;
    private SkyboxPass? _skyboxPass;
    private TonemapPass? _tonemapPass;

    // Tonemap (HDR resolve) pass resources kept here (TonemapPass borrows the layout to build its pipeline):
    // a 1-binding set layout (combined image sampler on the HDR target) and a clamp/linear sampler. The HDR
    // image is sampled through this sampler and resolved to the swapchain.
    private DescriptorSetLayout? _tonemapSetLayout;
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

    // Directional shadow map (architect decisions 6-9): a D32 2048² depth image that is BOTH a depth
    // attachment (ShadowPass writes it) and sampled (scene pass reads it through the sampler below). Created
    // once at construction — invariant to swapchain resize, so unlike the HDR/depth targets it lives outside
    // EnsureTargets. The depth-only shadow pipeline lives in ShadowPass; the image and its sampler stay here.
    private GpuImage? _shadowMap;
    private Sampler? _shadowSampler;

    // Same WAR pattern as _hdrInitialized on the single shared shadow map: first use acquires from Undefined,
    // every later frame from ShaderReadOnly (the previous frame's scene-pass sample), so the depth write
    // serializes after that read.
    private bool _shadowInitialized;

    // Image-based lighting + skybox (M7). IblResources bundles the generator, the current maps (built from an
    // HDRI via SetEnvironment) and the shared linear/clamp sampler; the maps feed set 0 bindings 3/4/5
    // (irradiance, prefiltered, BRDF LUT) every frame and the skybox pass. The skybox set-0 layout is kept
    // here (SkyboxPass borrows it): camera UBO (ray reconstruction) + environment cubemap.
    private IblResources? _iblResources;
    private DescriptorSetLayout? _skyboxSetLayout;

    // Shadow pass set 0 (P3-M1): one storage buffer of per-instance model matrices, read in the vertex stage.
    private DescriptorSetLayout? _shadowSetLayout;

    // The four reloadable passes as a stable array (built once), so PollShaderReload iterates them without
    // re-allocating the collection every frame. Same instances as the individual _xxxPass fields.
    private IReloadablePipeline[] _reloadablePasses = [];

    // Shader hot reload (M8-05). Watches the passes' resolved source files on a background thread; null when
    // there is nothing to watch (no editable source directory). PollShaderReload drives the reload at the frame
    // boundary. The reloader is a managed object (not a GPU resource) so the ResourceTracker never sees it.
    private ShaderHotReloader? _reloader;

#if DEBUG
    // Debounce window for the shader watcher: long enough to coalesce an editor's save burst (write-temp +
    // rename) and to let a half-written file finish, short enough to stay well inside the <1s reload budget.
    // Debug-only: the watcher (and hence this window) does not exist in a shipping build (Phase 2 rule §2.1-2).
    private static readonly TimeSpan ShaderReloadDebounce = TimeSpan.FromMilliseconds(200);
#endif

    // Bounded retry for a changed file that is not readable yet (audit M8-09 M1). Consecutive failed read
    // attempts per path; the dictionary is created lazily on the FIRST failure, so the steady-state poll never
    // touches it. Past MaxReloadReadAttempts (~2 s at the debounce above) the file is dropped with a warning
    // instead of being requeued forever.
    private const int MaxReloadReadAttempts = 10;
    private Dictionary<string, int>? _reloadReadFailures;

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
        _sceneTransforms = new InstanceBufferRing(device);
        _shadowTransforms = new InstanceBufferRing(device);
        _sceneArgs = new IndirectArgsRing(device);
        _shadowArgs = new IndirectArgsRing(device);
        _sceneCandidates = new PersistentInstanceBuffer(device);
        _batchBase = new StorageBufferRing<uint>(device);
        _shadowCullPlanes = new StorageBufferRing<Vector4>(device);
        _shadowMeshBase = new StorageBufferRing<uint>(device);

        try
        {
            // Owned by the Renderer and borrowed by every pass for its Build + Reload path. CreateForBuild picks
            // the mode (Debug = full runtime compilation + hot reload; Release = pre-cooked only, no shaderc).
            _shaderCompiler = ShaderCompiler.CreateForBuild();

            // Set 0 = per-frame data (spec §3.4): binding 0 camera (view/proj/position — the PBR
            // fragment stage needs the eye position), binding 1 lights (M5, decision 4), binding 2 the
            // directional shadow map as a comparison combined-image-sampler (M6, decision 6).
            _frameSetLayout = new DescriptorSetLayout(
                device,
                [
                    new DescriptorBinding(0, DescriptorKind.UniformBuffer, ShaderStages.Vertex | ShaderStages.Fragment),
                    new DescriptorBinding(1, DescriptorKind.UniformBuffer, ShaderStages.Fragment),
                    new DescriptorBinding(2, DescriptorKind.CombinedImageSampler, ShaderStages.Fragment),
                    // M7 IBL: irradiance (3), prefiltered specular (4), BRDF LUT (5) — all fragment-sampled.
                    new DescriptorBinding(3, DescriptorKind.CombinedImageSampler, ShaderStages.Fragment),
                    new DescriptorBinding(4, DescriptorKind.CombinedImageSampler, ShaderStages.Fragment),
                    new DescriptorBinding(5, DescriptorKind.CombinedImageSampler, ShaderStages.Fragment),
                    // P3-M1: per-instance model matrices, read-only in the vertex stage (instanced draws).
                    new DescriptorBinding(6, DescriptorKind.StorageBuffer, ShaderStages.Vertex),
                ]);

            // Set 1 = per-material, frozen 6-binding PBR shape. The allocator is public so SceneBuilder can
            // allocate persistent material sets against this layout.
            _materialSetLayout = MaterialLayout.CreateLayout(device);

            for (var i = 0; i < _cameraUbos.Length; i++)
            {
                _cameraUbos[i] = new GpuBuffer(device, (ulong)Unsafe.SizeOf<CameraUniforms>(), BufferUsage.Uniform);
                _lightsUbos[i] = new GpuBuffer(device, (ulong)Unsafe.SizeOf<LightsUniforms>(), BufferUsage.Uniform);
            }

            // --- Directional shadow map (decisions 6-9) -----------------------------------------------------
            // D32, 2048², both depth attachment (ShadowPass writes) and sampled (scene pass reads). Created
            // here, once — invariant to swapchain resize, so it stays out of EnsureTargets. The pipeline that
            // draws into it lives in ShadowPass, built below.
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

            // --- Tonemap pass resources (decision 3) --------------------------------------------------------
            // One binding: the HDR target as a combined image sampler, read by the fragment stage. Linear /
            // clamp-to-edge: the fullscreen triangle samples the HDR target 1:1, clamp is the safe screen-space
            // resolve choice. The pipeline lives in TonemapPass, built below.
            _tonemapSetLayout = new DescriptorSetLayout(
                device,
                [new DescriptorBinding(0, DescriptorKind.CombinedImageSampler, ShaderStages.Fragment)]);
            _tonemapSampler = new Sampler(device, new SamplerDesc(
                Filter: SamplerFilter.Linear,
                MipFilter: SamplerFilter.Linear,
                AddressMode: SamplerAddressMode.ClampToEdge));

            // --- Image-based lighting + skybox (M7) ---------------------------------------------------------
            // IblResources bundles the reusable generator, the current maps (produced later by SetEnvironment)
            // and the shared linear/clamp sampler. Skybox set 0: camera UBO (vertex, ray reconstruction) +
            // environment cubemap (fragment); the pipeline lives in SkyboxPass, built below.
            _iblResources = new IblResources(device, shaderDirectory);
            _skyboxSetLayout = new DescriptorSetLayout(
                device,
                [
                    new DescriptorBinding(0, DescriptorKind.UniformBuffer, ShaderStages.Vertex),
                    new DescriptorBinding(1, DescriptorKind.CombinedImageSampler, ShaderStages.Fragment),
                ]);

            // --- Reloadable passes (M8-04 seam) -------------------------------------------------------------
            // Each pass compiles its own shaders (via CompileFileResolved, so its SourceFiles are populated for
            // the hot-reload watcher) and builds its pipeline from the stable state assigned above (set layouts,
            // formats, shadow bias). The pass owns the shaders + pipeline; the Renderer keeps the state above.
            _scenePass = new ScenePass(
                device, shaderDirectory, _shaderCompiler,
                _frameSetLayout, _materialSetLayout, HdrFormat, DepthFormat);
            _shadowSetLayout = new DescriptorSetLayout(
                device,
                [new DescriptorBinding(0, DescriptorKind.StorageBuffer, ShaderStages.Vertex)]);
            _shadowPass = new ShadowPass(
                device, shaderDirectory, _shaderCompiler, _shadowSetLayout,
                DepthFormat, ShadowDepthBiasConstant, ShadowDepthBiasSlope);
            _tonemapPass = new TonemapPass(
                device, shaderDirectory, _shaderCompiler,
                _tonemapSetLayout, swapchain.ColorFormat);
            _skyboxPass = new SkyboxPass(
                device, shaderDirectory, _shaderCompiler,
                _skyboxSetLayout, HdrFormat, DepthFormat);

            // --- GPU scene cull compute (P3-M4 W1) ----------------------------------------------------------
            // Not a graphics pass (not hot-reloadable, like the IBL kernels): compiled once here. Four storage
            // buffers — candidates in (0), compacted instances out (1), indirect args in/out (2), per-batch base
            // in (3) — plus a push constant carrying the six frustum planes + the candidate count.
            var (cullSpirv, _) = _shaderCompiler.CompileFileResolved(
                Path.Combine(shaderDirectory, "scene_cull.comp"), ShaderStage.Compute);
            _sceneCullShader = new ShaderModule(device, cullSpirv, ShaderStage.Compute);
            _sceneCullSetLayout = new DescriptorSetLayout(
                device,
                [
                    new DescriptorBinding(0, DescriptorKind.StorageBuffer, ShaderStages.Compute),
                    new DescriptorBinding(1, DescriptorKind.StorageBuffer, ShaderStages.Compute),
                    new DescriptorBinding(2, DescriptorKind.StorageBuffer, ShaderStages.Compute),
                    new DescriptorBinding(3, DescriptorKind.StorageBuffer, ShaderStages.Compute),
                ]);
            _sceneCullPipeline = new ComputePipeline(device, new ComputePipelineDesc
            {
                ComputeShader = _sceneCullShader,
                SetLayouts = [_sceneCullSetLayout],
                PushConstants = [new PushConstantRange(0, (uint)Unsafe.SizeOf<CullPushConstants>(), ShaderStages.Compute)],
            });

            // --- GPU shadow cull compute (P3-M6 W3) ---------------------------------------------------------
            // Five storage buffers — candidates in (0, shared with the scene cull), compacted shadow instances out
            // (1), indirect args in/out (2), per-mesh-batch base in (3), the flat cascade planes in (4) — plus a
            // 16-byte push constant carrying the four loop bounds (cascade/shadow-batch/caster/candidate counts).
            var (shadowCullSpirv, _) = _shaderCompiler.CompileFileResolved(
                Path.Combine(shaderDirectory, "shadow_cull.comp"), ShaderStage.Compute);
            _shadowCullShader = new ShaderModule(device, shadowCullSpirv, ShaderStage.Compute);
            _shadowCullSetLayout = new DescriptorSetLayout(
                device,
                [
                    new DescriptorBinding(0, DescriptorKind.StorageBuffer, ShaderStages.Compute),
                    new DescriptorBinding(1, DescriptorKind.StorageBuffer, ShaderStages.Compute),
                    new DescriptorBinding(2, DescriptorKind.StorageBuffer, ShaderStages.Compute),
                    new DescriptorBinding(3, DescriptorKind.StorageBuffer, ShaderStages.Compute),
                    new DescriptorBinding(4, DescriptorKind.StorageBuffer, ShaderStages.Compute),
                ]);
            _shadowCullPipeline = new ComputePipeline(device, new ComputePipelineDesc
            {
                ComputeShader = _shadowCullShader,
                SetLayouts = [_shadowCullSetLayout],
                PushConstants = [new PushConstantRange(0, (uint)Unsafe.SizeOf<ShadowCullPushConstants>(), ShaderStages.Compute)],
            });

            // --- Shader hot reload (M8-05) ------------------------------------------------------------------
            // Only the four graphics passes are hot-reloadable (the IBL compute kernels are deferred — board).
            // Their SourceFiles (root shader + resolved #includes, populated by Build above) are exactly the
            // files to watch. The reloader spins up FileSystemWatcher(s) on their source directories; the
            // resulting change flag is drained in PollShaderReload at the frame boundary.
            _reloadablePasses = [_shadowPass, _scenePass, _skyboxPass, _tonemapPass];
#if DEBUG
            // Hot reload is a Debug-only luxury (Phase 2 rule §2.1-2): a shipping build spins up no watcher thread
            // and _reloader stays null, so PollShaderReload is a no-op. Only Debug watches the source files.
            var watchedFiles = new HashSet<string>(ShaderIncludeResolver.PathComparer);
            foreach (var pass in _reloadablePasses)
            {
                foreach (var file in pass.SourceFiles)
                {
                    watchedFiles.Add(file);
                }
            }

            _reloader = new ShaderHotReloader(watchedFiles, ShaderReloadDebounce);
#endif
        }
        catch
        {
            DisposeResources();
            throw;
        }
    }

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
    /// How far in front of the camera shadows are cast, in world units (spec §3.5). The shadow map covers the
    /// camera frustum truncated at this distance — beyond it, geometry is simply unshadowed, because spreading a
    /// 2048² map over the whole far plane would make its texels metres wide and the shadows useless anyway.
    /// Set it to the range that actually matters for the scene; 0 or less means "the whole frustum".
    /// <para>
    /// It is only a CAP: a scene smaller than the frustum is shadowed tightly whatever this says (see
    /// <see cref="ShadowFit"/>), so a small scene never pays for a large value.
    /// </para>
    /// </summary>
    public float ShadowDistance { get; set; } = 100f;

    /// <summary>
    /// CSM tunables (P3-M5): cascade count, split blend and shadowed range. The effective far distance is
    /// <c>min(MaxDistance, <see cref="ShadowDistance"/>)</c>, so the existing per-scene shadow-distance policy still
    /// caps the cascades. Defaults to four cascades, λ=0.5.
    /// </summary>
    public CascadeSettings Cascades { get; set; } = CascadeSettings.Default;

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
    /// Orchestration (decision 10): <see cref="EnsureTargets"/> → sync the persistent candidate buffer →
    /// <see cref="RecordShadowPass"/> → <see cref="RecordScenePass"/> → <see cref="RecordTonemapPass"/>. The CSM
    /// cascade matrices are computed by the caller (<see cref="ComputeCascades"/>) and shared: the shadow pass
    /// GPU-culls the casters per cascade and renders depth into each atlas tile, the scene pass packs the same
    /// matrices into the lights UBO so the fragment stage can re-project each surface for the PCF lookup, and the
    /// tonemap pass resolves the HDR target to the sRGB swapchain.
    /// </para>
    /// <para>
    /// Hot path: no managed allocation — index loops over the instance list, in-place span writes of the UBOs,
    /// engine attachment/target/handle types are all structs, no LINQ, no closures. The lightViewProj is
    /// passed <c>in</c> to the sub-passes to avoid recomputing it.
    /// </para>
    /// </summary>
    /// <summary>
    /// Generates the image-based-lighting maps from <paramref name="environment"/> (an equirectangular HDR,
    /// e.g. from <see cref="Agapanthe.Assets.HdrImageLoader"/>) and adopts them: the scene ambient and the
    /// skybox sample these from the next frame on. Replaces any previously-set environment (the old maps are
    /// released, deferred). Must be called at load time before the first <see cref="DrawScene"/> — the M7
    /// renderer has no ambient until an environment is set. Synchronous (runs the compute generator).
    /// </summary>
    public void SetEnvironment(Agapanthe.Assets.Model.HdrImageAsset environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ObjectDisposedException.ThrowIf(_disposed, this);
        // IblResources generates into a temporary and swaps only on success: if Generate throws, the
        // previously-adopted maps stay valid rather than being disposed while still referenced (audit M7-07 m3).
        _iblResources!.SetEnvironment(environment);
    }

    /// <summary>
    /// The four reloadable passes (M8-04 seam) for the shader hot reloader (M8-05): it maps a changed source
    /// file to the passes whose <see cref="IReloadablePipeline.SourceFiles"/> contain it and calls
    /// <see cref="IReloadablePipeline.Reload"/> on each, at the frame boundary before recording. Internal —
    /// the pass types never leave the Rendering assembly.
    /// </summary>
    internal IReadOnlyList<IReloadablePipeline> ReloadablePasses => _reloadablePasses;

    /// <summary>The compiler the passes were built with; reused by the hot reloader (M8-05) on reload.</summary>
    internal ShaderCompiler ShaderCompiler => _shaderCompiler!;

    /// <summary>
    /// Drives shader hot reload (M8-05, spec §3.6 / §6). Call once per frame from the render loop <b>before</b>
    /// <see cref="DrawFrame"/> / any command recording — the reload retires the old pipeline through the
    /// deletion queue (deferred N+FramesInFlight), which is only safe at the frame boundary (see
    /// <see cref="IReloadablePipeline"/>).
    /// <para>
    /// <b>Hot path.</b> The steady-state cost when no shader changed is a single <c>volatile bool</c> read that
    /// returns immediately — no lock, no allocation (audit point M8-09). Everything below the early-out runs
    /// only after an actual edit (rare) and is deliberately off the zero-allocation budget.
    /// </para>
    /// When a watched file changes it is mapped to the passes whose <see cref="IReloadablePipeline.SourceFiles"/>
    /// contain it, and each affected pass is reloaded once; a file that cannot be read yet (half-written) is
    /// requeued and retried on a later poll. The recompile + pipeline-recreate wall time is logged per pass to
    /// prove the &lt;1s budget (spec §6). A failed edit is logged and the previous pipeline is kept (spec §4).
    /// </summary>
    public void PollShaderReload()
    {
        // Zero-allocation early-out: a plain volatile read. Nothing changed on the vast majority of frames.
        if (_reloader is null || !_reloader.HasPending)
        {
            return;
        }

        // Debounce gate: returns false (no allocation) while the editor's save burst is still settling.
        if (!_reloader.TryBeginReload(out var changed))
        {
            return;
        }

        // Below here we are off the hot path (an edit happened) — allocations are acceptable.
        var passes = _reloadablePasses;
        Span<bool> touched = stackalloc bool[passes.Length];

        for (var c = 0; c < changed.Count; c++)
        {
            var file = changed[c];

            // A file the watcher reported but that no live pass compiled from (editor temp/swap files, unrelated
            // edits): nothing to reload.
            var matchesAnyPass = false;
            for (var p = 0; p < passes.Length; p++)
            {
                if (ContainsPath(passes[p].SourceFiles, file))
                {
                    matchesAnyPass = true;
                    break;
                }
            }

            if (!matchesAnyPass)
            {
                continue;
            }

            // Guard against a half-written file: if it is not yet readable (the editor still holds it), requeue
            // it (re-arms the debounce) and skip this cycle rather than letting the recompile fail spuriously.
            // The two failure modes must be told apart or the requeue never converges (audit M8-09 M1):
            //   - the file is gone (deleted/renamed): requeueing would re-arm _dirty and throw a FileNotFound
            //     inside IsReadable every ~200 ms, forever → drop it, warn once;
            //   - the file exists but is locked / half-written: requeue, but at most MaxReloadReadAttempts times,
            //     so an externally locked file cannot keep the poll spinning either.
            // Both paths converge; a later watcher event re-arms a dropped file with a fresh attempt count.
            if (!IsReadable(file))
            {
                if (!File.Exists(file))
                {
                    _reloadReadFailures?.Remove(file);
                    Core.Log.Warn($"Renderer: watched shader '{file}' no longer exists; dropping its reload.");
                    continue;
                }

                // Allocated lazily on the first failure only: the steady-state poll (and its early-out) stays
                // allocation-free.
                _reloadReadFailures ??= new Dictionary<string, int>(ShaderIncludeResolver.PathComparer);
                var attempts = _reloadReadFailures.TryGetValue(file, out var previous) ? previous + 1 : 1;
                if (attempts >= MaxReloadReadAttempts)
                {
                    _reloadReadFailures.Remove(file);
                    Core.Log.Warn(
                        $"Renderer: watched shader '{file}' is still unreadable after {MaxReloadReadAttempts} " +
                        "attempts (locked by another process?); giving up until it changes again.");
                    continue;
                }

                _reloadReadFailures[file] = attempts;
                _reloader.NotifyChanged(file);
                continue;
            }

            _reloadReadFailures?.Remove(file);

            for (var p = 0; p < passes.Length; p++)
            {
                if (ContainsPath(passes[p].SourceFiles, file))
                {
                    touched[p] = true;
                }
            }
        }

        for (var p = 0; p < passes.Length; p++)
        {
            if (!touched[p])
            {
                continue;
            }

            // Wall-time of the recompile (shaderc, or a disk-cache hit) + pipeline recreation, to prove the
            // <1s reload budget (spec §6). GetTimestamp/GetElapsedTime are struct-based: no allocation.
            // Reload returns false on a failed edit: it already logged the compiler error and kept the live
            // pipeline (spec §4), so do not claim a reload that did not happen.
            var start = Stopwatch.GetTimestamp();
            if (!passes[p].Reload(_shaderCompiler!))
            {
                continue;
            }

            var elapsed = Stopwatch.GetElapsedTime(start);
            Core.Log.Info(
                $"Renderer: hot-reloaded {passes[p].GetType().Name} in {elapsed.TotalMilliseconds:F1} ms " +
                "(recompile + pipeline recreate).");
        }
    }

    /// <summary>
    /// Debug hook (M8-05 verification): forces one reload of every graphics pass and logs the per-pass wall
    /// time, so the recompile + pipeline-recreate budget (&lt;1s, spec §6) can be measured headless without a
    /// windowed editing session. Not part of the normal frame loop; call once at load time (GPU idle, before
    /// the first frame). Gated behind <c>AGAPANTHE_SHADER_RELOAD_TEST</c> by the Sandbox.
    /// </summary>
    public void ReloadAllForTest()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var passes = _reloadablePasses;
        for (var p = 0; p < passes.Length; p++)
        {
            var start = Stopwatch.GetTimestamp();
            if (!passes[p].Reload(_shaderCompiler!))
            {
                continue;
            }

            var elapsed = Stopwatch.GetElapsedTime(start);
            Core.Log.Info(
                $"Renderer: [reload-test] {passes[p].GetType().Name} reloaded in {elapsed.TotalMilliseconds:F1} ms " +
                "(recompile + pipeline recreate).");
        }
    }

    // Case-correct (OS-aware) membership test against a pass's resolved source files, using the single shared
    // path comparer (ShaderIncludeResolver.PathComparer — same one the pass dedup and the watcher use, audit
    // M8-09 M2). Index loop over IReadOnlyList: no enumerator allocation.
    private static bool ContainsPath(IReadOnlyList<string> files, string path)
    {
        for (var i = 0; i < files.Count; i++)
        {
            if (ShaderIncludeResolver.PathComparer.Equals(files[i], path))
            {
                return true;
            }
        }

        return false;
    }

    // True if the file can currently be opened for reading (the writer has released it). ReadWrite share so we
    // never block the editor; any IOException means "not ready yet" -> the caller requeues it.
    private static bool IsReadable(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Fits the frame's CSM cascades (P3-M5) and reports them: <paramref name="lightViewProj"/> gets one matrix per
    /// cascade (each fitted to its own frustum slice and texel-snapped to an atlas tile) and
    /// <paramref name="splitViewDepths"/> the far view-space depth of each. Camera-only — no scene or caster bounds,
    /// hence no circularity (the P3-M2 two-pass wedge is retired). The resulting matrices drive both the shadow-cull
    /// cascade frusta and the shadow raster (P3-M6).
    /// </summary>
    public void ComputeCascades(
        in RenderView view, Span<Matrix4x4> lightViewProj, Span<float> splitViewDepths, Span<Vector4> nearCutPlanes)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var settings = Cascades with { MaxDistance = MathF.Min(Cascades.MaxDistance, ShadowDistance) };
        ShadowFit.ComputeCascades(
            in view, Lights.Directional.Direction, in settings, ShadowTileResolution,
            lightViewProj, splitViewDepths, nearCutPlanes);
    }

    public void DrawScene(
        SceneCandidateSet sceneCandidates,
        ReadOnlySpan<Frustum> cascadeFrusta,
        ReadOnlySpan<Vector4> cascadeNearCutPlanes,
        ResourceRegistry registry,
        in RenderView view,
        in Frustum cameraFrustum,
        ReadOnlySpan<Matrix4x4> cascades,
        Vector4 cascadeSplits,
        CommandList cmd,
        FrameContext frame,
        SwapchainTarget target)
    {
        ArgumentNullException.ThrowIfNull(sceneCandidates);
        ArgumentNullException.ThrowIfNull(registry);
        // The 2x2 atlas holds four tiles; a fifth cascade would place its viewport at y = 4096 on a 4096² map and
        // trip VUID-vkCmdSetScissor. Guarded here because this is a public API (audit F7).
        if (cascades.Length is < 1 or > 4)
        {
            throw new ArgumentOutOfRangeException(
                nameof(cascades), cascades.Length, "the 2x2 shadow atlas supports 1 to 4 cascades.");
        }
        ArgumentNullException.ThrowIfNull(frame);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_iblResources!.HasEnvironment)
        {
            throw new InvalidOperationException(
                "Renderer has no environment; call SetEnvironment before DrawScene (M7 IBL feeds the ambient and skybox).");
        }

        // HDR-color and depth targets are owned here; keep them sized to the swapchain image handed in by the
        // frame loop (recreated together behind a device wait when the extent changes). The shadow map is
        // resolution-invariant and lives outside this.
        EnsureTargets(target.Width, target.Height);

        // Sync the persistent candidate buffer ONCE (spec §5): both culls read the same device-local buffer
        // read-only, so it is brought up to date before either dispatch. Sync writes the host-visible staging (the
        // mirror), records the staging→device-local copy + a transfer→compute barrier on cmd, and returns the
        // device-local buffer to bind (P3-M7).
        var candidateBuffer = _sceneCandidates.Sync(cmd, sceneCandidates, frame.Slot);

        // Cascades computed by the caller; shared by the shadow pass (renders each into its atlas tile, culling the
        // candidates against cascadeFrusta on the GPU) and the scene pass (packs them into the lights UBO for PCF).
        RecordShadowPass(
            cmd, frame, sceneCandidates, candidateBuffer, cascadeFrusta, cascadeNearCutPlanes, registry, cascades);
        RecordScenePass(
            cmd, frame, sceneCandidates, candidateBuffer, registry, in view, in cameraFrustum, target, cascades,
            cascadeSplits);
        RecordTonemapPass(cmd, frame, target);
    }

    /// <summary>
    /// Pass 0 (M6, decision 7): renders scene depth from the directional light's viewpoint into the shadow
    /// map. Depth-only; <paramref name="lightViewProj"/> is a push constant (offset 0) and per-instance model
    /// matrices come from a compacted storage buffer (set 0, binding 0), so casters are drawn instanced (P3-M1).
    /// The shadow map's WAR acquire mirrors the HDR target: the first frame comes from Undefined, later frames
    /// from ShaderReadOnly (the previous frame's scene-pass sample), so this depth write serializes after that
    /// read. loadOp=Clear(1.0), storeOp=Store because the scene pass samples the result; a closing
    /// DepthAttachment→ShaderReadOnly barrier hands it over.
    /// </summary>
    private void RecordShadowPass(
        CommandList cmd, FrameContext frame, SceneCandidateSet set, GpuBuffer candidateBuffer,
        ReadOnlySpan<Frustum> cascadeFrusta, ReadOnlySpan<Vector4> cascadeNearCutPlanes, ResourceRegistry registry,
        ReadOnlySpan<Matrix4x4> cascades)
    {
        // RenderDoc/Nsight capture region for the whole pass (barriers + draws). No-op when debug utils is
        // off, so it stays on the per-frame path at zero cost; the name is a literal (no per-frame alloc).
        using var _ = cmd.PushDebugLabel(ShadowPassLabel);

        var pipeline = _shadowPass!.Pipeline;
        var shadow = _shadowMap!;
        var n = cascades.Length;
        var shadowBatches = set.ShadowBatches;
        var shadowBatchCount = shadowBatches.Length;
        var totalCasters = set.TotalCasters;
        var candidateCount = set.Count;
        var stride = (uint)System.Runtime.CompilerServices.Unsafe.SizeOf<DrawIndexedIndirectCommand>();

        // GPU shadow cull (P3-M6 W3), BEFORE the render pass — compute cannot record inside BeginRendering. It culls
        // the persistent candidates against the cascade frusta and compacts casters into one region per
        // (cascade, mesh-batch): cascade c's block is [c*totalCasters, +totalCasters), mesh m at meshBatchBase[m].
        // The instance buffer therefore reserves n*totalCasters slots (the 4x overlap is the deferred raster cost).
        var regionCount = n * shadowBatchCount;
        var instanceBuffer = _shadowTransforms.EnsureCapacity(frame.Slot, Math.Max(n * totalCasters, 1));

        GpuBuffer? argsBuffer = null;
        if (regionCount > 0)
        {
            argsBuffer = DispatchShadowCull(
                cmd, frame, candidateBuffer, instanceBuffer, set, cascadeFrusta, cascadeNearCutPlanes, registry,
                n, shadowBatchCount, totalCasters, candidateCount, regionCount);
        }

        cmd.TransitionImage(
            shadow,
            _shadowInitialized ? ImageLayoutState.ShaderReadOnly : ImageLayoutState.Undefined,
            ImageLayoutState.DepthAttachment);
        _shadowInitialized = true;

        // ONE render pass over the whole atlas: clear all four tiles once, then draw each cascade into its own
        // viewport. (Reopening a pass per tile would re-clear the map or need LoadOp.Load per tile.)
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

        LastShadowDrawCalls = 0;
        if (argsBuffer is not null)
        {
            cmd.BindPipeline(pipeline);
            var shadowSet = frame.AllocateSet(_shadowSetLayout!);
            frame.WriteStorageBuffer(shadowSet, 0, instanceBuffer);
            cmd.BindDescriptorSet(pipeline, 0, shadowSet);

            for (var c = 0; c < n; c++)
            {
                // Tile (c%2, c/2) of the 2×2 atlas. The viewport+scissor restrict rasterization to that tile, so
                // cascade c can only ever write its own quadrant of the shared map.
                cmd.SetViewportScissorRect(
                    ((uint)c % 2) * ShadowTileResolution, ((uint)c / 2) * ShadowTileResolution,
                    ShadowTileResolution, ShadowTileResolution);

                var lightViewProj = cascades[c];
                cmd.PushConstants(pipeline, ShaderStages.Vertex, in lightViewProj, offsetBytes: 0);

                // One DrawIndexedIndirect per (cascade, mesh-batch) region. Its instanceCount was written by the
                // shadow cull compute; a region the cull left at 0 is a GPU no-op (the CPU cannot know which are
                // empty without a readback — backlog §1). The region's base into the shared instance buffer
                // (cascade block + the mesh's base) travels as a vertex push constant, never firstInstance.
                for (var m = 0; m < shadowBatchCount; m++)
                {
                    var mesh = registry.Resolve(shadowBatches[m].Mesh);
                    var batchOffset = (uint)((c * totalCasters) + (int)shadowBatches[m].Offset);
                    cmd.PushConstants(pipeline, ShaderStages.Vertex, batchOffset, offsetBytes: 64);
                    cmd.BindVertexBuffer(mesh.VertexBuffer);
                    cmd.BindIndexBuffer(mesh.IndexBuffer, mesh.IndexFormat);
                    cmd.DrawIndexedIndirect(argsBuffer, (ulong)((c * shadowBatchCount) + m) * stride, drawCount: 1, stride);
                    LastShadowDrawCalls++;
                }
            }
        }

        cmd.EndRendering();

        // Hand the depth result to the scene pass as a sampled texture: wait for the late-fragment-test depth
        // writes before the scene fragment stage samples it (DepthAttachment→ShaderReadOnly, RAW barrier).
        cmd.TransitionImage(shadow, ImageLayoutState.DepthAttachment, ImageLayoutState.ShaderReadOnly);
    }

    // Dispatches the GPU shadow cull (P3-M6 W3): uploads the per-region indirect args (indexCount from each region's
    // mesh, instanceCount ZERO — the compute fills it via atomics), the mesh-batch bases, and the flat cascade planes,
    // binds the shared persistent candidate buffer, dispatches one invocation per candidate, then barriers the args
    // and compacted instances into the draw. Returns the args buffer to issue the indirect draws from.
    // Planes per cascade handed to shadow_cull.comp: the 6 ortho-frustum planes + 1 near-side view-depth cut (P3-M7).
    private const int ShadowCullPlanesPerCascade = 7;

    private GpuBuffer DispatchShadowCull(
        CommandList cmd, FrameContext frame, GpuBuffer candidateBuffer, GpuBuffer instanceBuffer,
        SceneCandidateSet set, ReadOnlySpan<Frustum> cascadeFrusta, ReadOnlySpan<Vector4> cascadeNearCutPlanes,
        ResourceRegistry registry, int n, int shadowBatchCount, int totalCasters, int candidateCount, int regionCount)
    {
        var shadowBatches = set.ShadowBatches;

        if (_shadowCommands.Length < regionCount)
        {
            System.Array.Resize(ref _shadowCommands, Math.Max(regionCount, _shadowCommands.Length * 2));
        }

        if (_shadowMeshBaseScratch.Length < shadowBatchCount)
        {
            System.Array.Resize(ref _shadowMeshBaseScratch, Math.Max(shadowBatchCount, _shadowMeshBaseScratch.Length * 2));
        }

        var planeCount = n * ShadowCullPlanesPerCascade;
        if (_shadowPlaneScratch.Length < planeCount)
        {
            System.Array.Resize(ref _shadowPlaneScratch, Math.Max(planeCount, _shadowPlaneScratch.Length * 2));
        }

        // Args: one command per (cascade, mesh-batch) region, indexCount from the mesh, instanceCount ZERO. The
        // Upload re-zeroes the whole buffer each frame, which is what resets the atomic counters.
        for (var c = 0; c < n; c++)
        {
            for (var m = 0; m < shadowBatchCount; m++)
            {
                var indexCount = registry.Resolve(shadowBatches[m].Mesh).IndexCount;
                _shadowCommands[(c * shadowBatchCount) + m] = new DrawIndexedIndirectCommand(indexCount, 0, 0, 0, 0);
            }
        }

        for (var m = 0; m < shadowBatchCount; m++)
        {
            _shadowMeshBaseScratch[m] = shadowBatches[m].Offset;
        }

        for (var c = 0; c < n; c++)
        {
            var block = c * ShadowCullPlanesPerCascade;
            cascadeFrusta[c].CopyPlanes(_shadowPlaneScratch.AsSpan(block, 6));  // the 6 ortho-frustum planes
            _shadowPlaneScratch[block + 6] = cascadeNearCutPlanes[c];           // + the near-side view-depth cut (P3-M7)
        }

        var argsBuffer = _shadowArgs.Upload(frame.Slot, _shadowCommands.AsSpan(0, regionCount));
        var meshBaseBuffer = _shadowMeshBase.Upload(frame.Slot, _shadowMeshBaseScratch.AsSpan(0, shadowBatchCount));
        var planesBuffer = _shadowCullPlanes.Upload(frame.Slot, _shadowPlaneScratch.AsSpan(0, planeCount));

        if (VerifyCull)
        {
            // Remember this frame's shadow args + region layout so ReadBackShadowVisible can sum the per-cascade
            // instanceCounts the cull wrote (P3-M7 W3 diagnostic + shadow-cull gate).
            _lastShadowArgs = argsBuffer;
            _lastShadowBatchCount = shadowBatchCount;
            _lastShadowCascadeCount = n;
        }

        var cullSet = frame.AllocateSet(_shadowCullSetLayout!);
        frame.WriteStorageBuffer(cullSet, 0, candidateBuffer);
        frame.WriteStorageBuffer(cullSet, 1, instanceBuffer);
        frame.WriteStorageBuffer(cullSet, 2, argsBuffer);
        frame.WriteStorageBuffer(cullSet, 3, meshBaseBuffer);
        frame.WriteStorageBuffer(cullSet, 4, planesBuffer);

        cmd.BindPipeline(_shadowCullPipeline!);
        cmd.BindDescriptorSet(_shadowCullPipeline!, 0, cullSet);
        var pc = new ShadowCullPushConstants
        {
            CascadeCount = (uint)n,
            ShadowBatchCount = (uint)shadowBatchCount,
            TotalCasters = (uint)totalCasters,
            CandidateCount = (uint)candidateCount,
        };
        cmd.PushConstants(_shadowCullPipeline!, ShaderStages.Compute, in pc);
        cmd.Dispatch((uint)((candidateCount + 63) / 64)); // local_size_x = 64

        // Hand the compute output to the draw: args to the indirect stage, compacted instances to the vertex stage.
        cmd.BufferBarrier(argsBuffer, BufferSync.ComputeWrite, BufferSync.IndirectRead);
        cmd.BufferBarrier(instanceBuffer, BufferSync.ComputeWrite, BufferSync.VertexStorageRead);
        return argsBuffer;
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
        CommandList cmd, FrameContext frame, SceneCandidateSet sceneCandidates, GpuBuffer candidateBuffer,
        ResourceRegistry registry, in RenderView view, in Frustum cameraFrustum, SwapchainTarget target,
        ReadOnlySpan<Matrix4x4> cascades, Vector4 cascadeSplits)
    {
        // Capture region for the forward PBR pass; the skybox draw below nests its own "Skybox" sub-label.
        using var _ = cmd.PushDebugLabel(ScenePassLabel);

        var pipeline = _scenePass!.Pipeline;
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

        // GPU cull (P3-M4 W1) runs BEFORE the render pass — a compute dispatch cannot be recorded inside
        // BeginRendering/EndRendering. It frustum-culls the scene candidates, compacts the survivors into the
        // instance buffer, and writes each batch's instanceCount into the indirect-args buffer; two buffer
        // barriers then hand those to the draw (vertex read + indirect read).
        var instanceBuffer = CullSceneOnGpu(
            cmd, frame, sceneCandidates, candidateBuffer, registry, in cameraFrustum, out var sceneBatchCount,
            out var sceneArgsBuffer);

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
        // The packed eye is view.EyeRelative — the eye's position within the frame's QUANTIZED cell (M4), up to a
        // cell (1024 m) from the origin, and the view carries that sub-cell translation. mesh.frag's
        // V = normalize(eyePos - worldPos) is correct because both are in the same camera-relative frame.
        var ubo = _cameraUbos[frame.Slot]!;
        var uniforms = new CameraUniforms(view.View, view.Projection, view.EyeRelative);
        ubo.Write(new ReadOnlySpan<CameraUniforms>(in uniforms));

        // Distance fade-out threshold (audit MAJEUR-2). The fade exists to hide the SHADOW HORIZON — the hard line
        // where a chosen range stops. If the cascades were instead cut short by the camera's far plane, there is no
        // horizon to hide (geometry is clipped there regardless), and fading would just erase the last 20% of the
        // shadows in any tightly-framed scene. So: fade only when OUR range binds; otherwise push the threshold
        // past the range so the shader never triggers it.
        var chosenRange = MathF.Min(Cascades.MaxDistance, ShadowDistance);
        var fadeStart = view.Far <= chosenRange ? float.MaxValue : chosenRange * 0.8f;

        var lightsUbo = _lightsUbos[frame.Slot]!;
        var lightsUniforms = new LightsUniforms(Lights, cascades, cascadeSplits, fadeStart, view.Origin);
        lightsUbo.Write(new ReadOnlySpan<LightsUniforms>(in lightsUniforms));

        var frameSet = frame.AllocateSet(_frameSetLayout!);
        frame.WriteUniformBuffer(frameSet, 0, ubo);
        frame.WriteUniformBuffer(frameSet, 1, lightsUbo);
        // The shadow map is in ShaderReadOnly (RecordShadowPass closing barrier), matching the descriptor's
        // declared layout; sampled through the comparison sampler for PCF in mesh.frag.
        frame.WriteCombinedImageSampler(frameSet, 2, _shadowMap!, _shadowSampler!);

        // IBL maps (M7): irradiance (3), prefiltered specular (4), BRDF LUT (5). All left in ShaderReadOnly by
        // the generator. Non-null here: DrawScene rejects an unset environment before reaching this pass.
        var ibl = _iblResources!.Maps!;
        var iblSampler = _iblResources.Sampler;
        frame.WriteCombinedImageSampler(frameSet, 3, ibl.Irradiance, iblSampler);
        frame.WriteCombinedImageSampler(frameSet, 4, ibl.Prefiltered, iblSampler);
        frame.WriteCombinedImageSampler(frameSet, 5, ibl.BrdfLut, iblSampler);

        // Point binding 6 at the COMPUTE-CULLED instance buffer (P3-M4 W1): the cull dispatch above compacted the
        // surviving matrices into it, and the vertex shader reads instances.model[gl_InstanceIndex + batchOffset].
        frame.WriteStorageBuffer(frameSet, 6, instanceBuffer);

        cmd.BindPipeline(pipeline);
        cmd.BindDescriptorSet(pipeline, 0, frameSet); // set 0: per-frame camera + lights + shadow map

        var debugView = DebugView;
        cmd.PushConstants(pipeline, ShaderStages.Fragment, in debugView, offsetBytes: 64);

        // One DrawIndexedIndirect per batch (P3-M4). The batch table now lives in the persistent set (P3-M6, rebuilt
        // only on a structural change) and the args buffer was built by the cull; each command's instanceCount was
        // written by the compute shader, so the CPU never learns how many survived. The batch's start offset into the
        // instance SSBO travels as a VERTEX push constant (batchOffset), never firstInstance.
        var sceneBatches = sceneCandidates.SceneBatches;
        var stride = (uint)System.Runtime.CompilerServices.Unsafe.SizeOf<DrawIndexedIndirectCommand>();
        LastSceneDrawCalls = 0;
        var boundMaterial = MaterialHandle.Invalid;
        for (var b = 0; b < sceneBatchCount; b++)
        {
            ref readonly var batch = ref sceneBatches[b];
            var mesh = registry.Resolve(batch.Mesh);
            // The key is material-major, so consecutive batches often share one material (same material, several
            // meshes): rebind set 1 only when it actually changes.
            if (batch.Material != boundMaterial)
            {
                cmd.BindDescriptorSet(pipeline, 1, registry.Resolve(batch.Material).DescriptorSet); // set 1: per-material
                boundMaterial = batch.Material;
            }

            cmd.PushConstants(pipeline, ShaderStages.Vertex, batch.Offset, offsetBytes: 0); // gl_InstanceIndex base
            cmd.BindVertexBuffer(mesh.VertexBuffer);
            cmd.BindIndexBuffer(mesh.IndexBuffer, mesh.IndexFormat);
            cmd.DrawIndexedIndirect(sceneArgsBuffer, (ulong)b * stride, drawCount: 1, stride);
            LastSceneDrawCalls++;
        }

        // Skybox (M7): fill the background with the environment cubemap inside this same pass, AFTER the meshes
        // so its far-plane depth test (LessOrEqual, no write) only paints pixels no mesh covered. The camera
        // UBO written above is reused for the view-ray reconstruction. Nested sub-label so the single skybox
        // draw is distinguishable under "Scene" in a capture.
        using (cmd.PushDebugLabel(SkyboxLabel))
        {
            var skyboxSet = frame.AllocateSet(_skyboxSetLayout!);
            frame.WriteUniformBuffer(skyboxSet, 0, ubo);
            frame.WriteCombinedImageSampler(skyboxSet, 1, ibl.Environment, iblSampler);
            var skyboxPipeline = _skyboxPass!.Pipeline;
            cmd.BindPipeline(skyboxPipeline);
            cmd.BindDescriptorSet(skyboxPipeline, 0, skyboxSet);
            cmd.Draw(3);
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
        // Capture region for the HDR resolve (barrier + fullscreen triangle).
        using var _ = cmd.PushDebugLabel(TonemapPassLabel);

        var tonemapPipeline = _tonemapPass!.Pipeline;
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

    // The scene-cull push constant (P3-M4 W1): the six camera-frustum planes (inward normals, normalized) plus the
    // candidate count. std430-compatible: six vec4 at 0..95, a uint at 96 → 100 bytes, within the 128-byte range.
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct CullPushConstants
    {
        public Vector4 Plane0;
        public Vector4 Plane1;
        public Vector4 Plane2;
        public Vector4 Plane3;
        public Vector4 Plane4;
        public Vector4 Plane5;
        public uint CandidateCount;
    }

    // The shadow-cull push constant (P3-M6 W3): the four loop bounds. The cascade planes ride a storage buffer
    // (binding 4), not here — up to 24 vec4 would blow the 128-byte push range. 16 bytes, std430 uvec4.
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct ShadowCullPushConstants
    {
        public uint CascadeCount;
        public uint ShadowBatchCount;
        public uint TotalCasters;
        public uint CandidateCount;
    }

    /// <summary>
    /// P3-M4 W1 gate: sums the <c>instanceCount</c>s the compute cull wrote into the last frame's indirect-args
    /// buffer — the number of candidates the GPU kept. The caller MUST have idled the GPU first (the Sandbox waits
    /// before its capture). Compare against <see cref="LastSceneCpuVisible"/>: equal ⇒ the GPU cull kept exactly the
    /// CPU's visible set. Requires <see cref="VerifyCull"/>.
    /// </summary>
    public int ReadBackSceneVisible()
    {
        if (_lastSceneArgs is null || _lastSceneBatchCount == 0)
        {
            return 0;
        }

        var commands = _lastSceneArgs.MappedSpan<DrawIndexedIndirectCommand>(_lastSceneBatchCount);
        var total = 0;
        for (var b = 0; b < _lastSceneBatchCount; b++)
        {
            total += (int)commands[b].InstanceCount;
        }

        return total;
    }

    /// <summary>
    /// P3-M7 W3 diagnostic + shadow-cull gate: sums the shadow cull's <c>instanceCount</c>s per cascade (region
    /// <c>c·shadowBatchCount + m</c>) into <paramref name="perCascade"/> and returns the grand total. Measures the
    /// near-cut plane's 4×→~1× raster drop, and — unlike P3-M6, which had no shadow readback — gives the shadow cull
    /// a numeric gate. The caller MUST have idled the GPU first. Requires <see cref="VerifyCull"/>.
    /// </summary>
    public int ReadBackShadowVisible(Span<int> perCascade)
    {
        if (_lastShadowArgs is null || _lastShadowBatchCount == 0 || _lastShadowCascadeCount == 0)
        {
            return 0;
        }

        var regionCount = _lastShadowCascadeCount * _lastShadowBatchCount;
        var commands = _lastShadowArgs.MappedSpan<DrawIndexedIndirectCommand>(regionCount);
        var total = 0;
        for (var c = 0; c < _lastShadowCascadeCount; c++)
        {
            var cascadeTotal = 0;
            for (var m = 0; m < _lastShadowBatchCount; m++)
            {
                cascadeTotal += (int)commands[(c * _lastShadowBatchCount) + m].InstanceCount;
            }

            if (c < perCascade.Length)
            {
                perCascade[c] = cascadeTotal;
            }

            total += cascadeTotal;
        }

        return total;
    }

    // The GPU scene cull (P3-M4 W1, persistent candidates P3-M6). The candidate buffer is maintained across frames by
    // the World's SceneCandidateSet (structural rebuild vs per-entity dirty patch) and synced ONCE at the top of
    // DrawScene (shared read-only with the shadow cull) — no per-frame pack/upload of all candidates. The batch table
    // likewise lives in the set (rebuilt only structurally); only the args (instanceCount re-zeroed for the atomics)
    // and the tiny per-batch base offsets are uploaded each frame. Dispatches the frustum-cull compute (compacts
    // survivors into the instance buffer, writes each batch's instanceCount), then barriers the results into the draw.
    // Returns the instance buffer to bind at set-0 binding 6; hands back the batch count and the args buffer.
    private GpuBuffer CullSceneOnGpu(
        CommandList cmd, FrameContext frame, SceneCandidateSet set, GpuBuffer candidateBuffer,
        ResourceRegistry registry, in Frustum cameraFrustum, out int batchCount, out GpuBuffer argsBuffer)
    {
        var candidateCount = set.Count;
        var batches = set.SceneBatches;
        batchCount = batches.Length;

        // Grow the reused scratch (never per-frame in steady state).
        if (_sceneCommands.Length < batchCount)
        {
            System.Array.Resize(ref _sceneCommands, Math.Max(batchCount, _sceneCommands.Length * 2));
        }

        if (_batchBaseScratch.Length < batchCount)
        {
            System.Array.Resize(ref _batchBaseScratch, Math.Max(batchCount, _batchBaseScratch.Length * 2));
        }

        // Args: one command per batch, indexCount from the mesh, instanceCount ZERO (the compute fills it via
        // atomicAdd). Batch base: the run's start offset — also the base of its compacted output region. Both are
        // O(batches) (a handful), so re-uploading them per frame is negligible next to the persistent candidate
        // buffer they used to accompany — the O(n) upload P3-M6 removed.
        for (var b = 0; b < batchCount; b++)
        {
            ref readonly var batch = ref batches[b];
            _sceneCommands[b] = new DrawIndexedIndirectCommand(registry.Resolve(batch.Mesh).IndexCount, 0, 0, 0, 0);
            _batchBaseScratch[b] = batch.Offset;
        }

        // The persistent candidate copy was synced ONCE at the top of DrawScene (shared read-only with the shadow
        // cull, spec §5); here we only upload the small per-frame buffers.
        var batchBaseBuffer = _batchBase.Upload(frame.Slot, _batchBaseScratch.AsSpan(0, batchCount));
        argsBuffer = _sceneArgs.Upload(frame.Slot, _sceneCommands.AsSpan(0, batchCount));
        var instanceBuffer = _sceneTransforms.EnsureCapacity(frame.Slot, Math.Max(candidateCount, 1));

        // Gate (P3-M4 W1): count what the CPU frustum test would keep — the oracle the GPU cull must match. Remember
        // this frame's args + batch count so ReadBackSceneVisible can sum the GPU's instanceCounts after it idles.
        if (VerifyCull)
        {
            var visible = 0;
            var candidates = set.Candidates;
            for (var k = 0; k < candidateCount; k++)
            {
                var s = candidates[k].Sphere;
                if (cameraFrustum.Intersects(new Vector3(s.X, s.Y, s.Z), s.W))
                {
                    visible++;
                }
            }

            LastSceneCpuVisible = visible;
            _lastSceneArgs = argsBuffer;
            _lastSceneBatchCount = batchCount;
        }

        // Empty scene: nothing to cull, and dispatching zero groups + binding empty buffers is needless. The draw
        // loop runs zero batches anyway.
        if (candidateCount == 0)
        {
            return instanceBuffer;
        }

        var cullSet = frame.AllocateSet(_sceneCullSetLayout!);
        frame.WriteStorageBuffer(cullSet, 0, candidateBuffer);
        frame.WriteStorageBuffer(cullSet, 1, instanceBuffer);
        frame.WriteStorageBuffer(cullSet, 2, argsBuffer);
        frame.WriteStorageBuffer(cullSet, 3, batchBaseBuffer);

        cmd.BindPipeline(_sceneCullPipeline!);
        cmd.BindDescriptorSet(_sceneCullPipeline!, 0, cullSet);

        Span<Vector4> planes = stackalloc Vector4[6];
        cameraFrustum.CopyPlanes(planes);
        var pc = new CullPushConstants
        {
            Plane0 = planes[0], Plane1 = planes[1], Plane2 = planes[2],
            Plane3 = planes[3], Plane4 = planes[4], Plane5 = planes[5],
            CandidateCount = (uint)candidateCount,
        };
        cmd.PushConstants(_sceneCullPipeline!, ShaderStages.Compute, in pc);
        cmd.Dispatch((uint)((candidateCount + 63) / 64)); // local_size_x = 64

        // Hand the compute output to the draw: the args to the indirect stage, the compacted instances to the
        // vertex stage. Two distinct destination scopes, two barriers.
        cmd.BufferBarrier(argsBuffer, BufferSync.ComputeWrite, BufferSync.IndirectRead);
        cmd.BufferBarrier(instanceBuffer, BufferSync.ComputeWrite, BufferSync.VertexStorageRead);
        return instanceBuffer;
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

    // Teardown order (also the ctor-failure cleanup path): targets, the four passes + IBL bundle, then the
    // stable state the Renderer owns (samplers, set layouts, allocator, UBOs, compiler). Each pass disposes
    // its own shaders + pipeline (deferred). Every ?. is a real guard: on a failed construction only a prefix
    // of these is assigned.
    private void DisposeResources()
    {
        // Stop the background file watcher first (managed object, not a GPU resource): no more reload flags can
        // be raised while we tear the passes down.
        _reloader?.Dispose();
        _reloader = null;
        _reloadablePasses = [];

        // Deferred release: the caller idles the GPU before Dispose (see the type remarks) and drains the
        // deletion queue with FlushAll afterwards, so the HDR/depth/shadow images are freed before the leak check.
        _hdrImage?.Dispose();
        _depthImage?.Dispose();
        _hdrImage = null;
        _depthImage = null;

        // The four reloadable passes (own their shaders + pipelines), then the IBL bundle (generator + maps +
        // sampler) and the borrowed set layouts / samplers the Renderer keeps for them.
        _scenePass?.Dispose();
        _shadowPass?.Dispose();
        _skyboxPass?.Dispose();
        _tonemapPass?.Dispose();
        _scenePass = null;
        _shadowPass = null;
        _skyboxPass = null;
        _tonemapPass = null;

        _iblResources?.Dispose();
        _skyboxSetLayout?.Dispose();
        _shadowSetLayout?.Dispose();
        _iblResources = null;
        _skyboxSetLayout = null;
        _shadowSetLayout = null;

        // Directional shadow map + its sampler (the pipeline lived in the disposed ShadowPass).
        _shadowSampler?.Dispose();
        _shadowMap?.Dispose();
        _shadowSampler = null;
        _shadowMap = null;

        _tonemapSetLayout?.Dispose();
        _tonemapSampler?.Dispose();
        _tonemapSetLayout = null;
        _tonemapSampler = null;

        _materialSetLayout?.Dispose();
        _frameSetLayout?.Dispose();
        for (var i = 0; i < _cameraUbos.Length; i++)
        {
            _cameraUbos[i]?.Dispose();
            _lightsUbos[i]?.Dispose();
        }

        _sceneTransforms.Dispose();
        _shadowTransforms.Dispose();
        _sceneArgs.Dispose();
        _shadowArgs.Dispose();
        _sceneCandidates.Dispose();
        _batchBase.Dispose();
        _shadowCullPlanes.Dispose();
        _shadowMeshBase.Dispose();
        _sceneCullPipeline?.Dispose();
        _sceneCullSetLayout?.Dispose();
        _sceneCullShader?.Dispose();
        _sceneCullPipeline = null;
        _sceneCullSetLayout = null;
        _sceneCullShader = null;
        _shadowCullPipeline?.Dispose();
        _shadowCullSetLayout?.Dispose();
        _shadowCullShader?.Dispose();
        _shadowCullPipeline = null;
        _shadowCullSetLayout = null;
        _shadowCullShader = null;

        _shaderCompiler?.Dispose();

        _materialSetLayout = null;
        _frameSetLayout = null;
        _shaderCompiler = null;
    }
}
