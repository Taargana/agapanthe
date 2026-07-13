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
            _shadowPass = new ShadowPass(
                device, shaderDirectory, _shaderCompiler,
                DepthFormat, ShadowDepthBiasConstant, ShadowDepthBiasSlope);
            _tonemapPass = new TonemapPass(
                device, shaderDirectory, _shaderCompiler,
                _tonemapSetLayout, swapchain.ColorFormat);
            _skyboxPass = new SkyboxPass(
                device, shaderDirectory, _shaderCompiler,
                _skyboxSetLayout, HdrFormat, DepthFormat);

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

    public void DrawScene(
        RenderList renderList,
        RenderList shadowCasters,
        ResourceRegistry registry,
        in RenderView view,
        CommandList cmd,
        FrameContext frame,
        SwapchainTarget target,
        in Double3Bounds sceneBounds)
    {
        ArgumentNullException.ThrowIfNull(renderList);
        ArgumentNullException.ThrowIfNull(shadowCasters);
        ArgumentNullException.ThrowIfNull(registry);
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

        // One light-space transform per frame, shared by the shadow pass (render depth) and the scene pass
        // (pack into the lights UBO for the PCF lookup). Fitted in the SAME camera-relative frame as the render
        // lists (view.Origin), because that is the space the vertex/fragment stages hand it world positions in.
        var lightViewProj = ComputeLightViewProj(in view, in sceneBounds);

        RecordShadowPass(cmd, shadowCasters, registry, in lightViewProj);
        RecordScenePass(cmd, frame, renderList, registry, in view, target, in lightViewProj);
        RecordTonemapPass(cmd, frame, target);
    }

    /// <summary>
    /// The directional light's <c>view · proj</c> for this frame — see <see cref="ShadowFit"/> for the fit
    /// itself (camera frustum, capped by <see cref="ShadowDistance"/>, never wider than the scene, texel-snapped).
    /// Row-vector: a camera-relative point maps to light clip space as <c>p · result</c>, and std140 uploads it
    /// transposed so the shaders multiply <c>result * vec4(worldPos, 1)</c>. One cascade (CSM is out of scope).
    /// </summary>
    private Matrix4x4 ComputeLightViewProj(in RenderView view, in Double3Bounds sceneBounds)
        => ShadowFit.ComputeLightViewProj(
            in view, in sceneBounds, Lights.Directional.Direction, ShadowDistance, ShadowMapResolution);

    /// <summary>
    /// Pass 0 (M6, decision 7): renders scene depth from the directional light's viewpoint into the shadow
    /// map. Depth-only, no descriptor set — <paramref name="lightViewProj"/> (offset 0) and each mesh's world
    /// transform (offset 64) are pushed as constants. The shadow map's WAR acquire mirrors the HDR target: the
    /// first frame comes from Undefined, later frames from ShaderReadOnly (the previous frame's scene-pass
    /// sample), so this depth write serializes after that read. loadOp=Clear(1.0), storeOp=Store because the
    /// scene pass samples the result; a closing DepthAttachment→ShaderReadOnly barrier hands it over.
    /// </summary>
    private void RecordShadowPass(
        CommandList cmd, RenderList shadowCasters, ResourceRegistry registry, in Matrix4x4 lightViewProj)
    {
        // RenderDoc/Nsight capture region for the whole pass (barriers + draws). No-op when debug utils is
        // off, so it stays on the per-frame path at zero cost; the name is a literal (no per-frame alloc).
        using var _ = cmd.PushDebugLabel(ShadowPassLabel);

        var pipeline = _shadowPass!.Pipeline;
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

        // The caster list, in its stable sorted order (spec §6 condition b). In M2 it is a passthrough of every
        // drawable; M4 culls it against the LIGHT volume (never the camera frustum — see spec §3.5).
        var items = shadowCasters.Items;
        for (var i = 0; i < items.Length; i++)
        {
            var mesh = registry.Resolve(items[i].Mesh);
            cmd.BindVertexBuffer(mesh.VertexBuffer);
            cmd.BindIndexBuffer(mesh.IndexBuffer, mesh.IndexFormat);

            var model = items[i].WorldTransform;
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
        CommandList cmd, FrameContext frame, RenderList renderList, ResourceRegistry registry, in RenderView view,
        SwapchainTarget target, in Matrix4x4 lightViewProj)
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
        // The eye is at the origin of the frame being rendered (spec §3.3), so the packed eye position is exactly
        // zero and the view carries no translation. mesh.frag's V = normalize(eyePos - worldPos) therefore becomes
        // normalize(-worldPos) with no shader change: both sides are already camera-relative.
        var ubo = _cameraUbos[frame.Slot]!;
        var uniforms = new CameraUniforms(view.View, view.Projection, view.EyeRelative);
        ubo.Write(new ReadOnlySpan<CameraUniforms>(in uniforms));

        var lightsUbo = _lightsUbos[frame.Slot]!;
        var lightsUniforms = new LightsUniforms(Lights, lightViewProj, view.Origin);
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

        cmd.BindPipeline(pipeline);
        cmd.BindDescriptorSet(pipeline, 0, frameSet); // set 0: per-frame camera + lights + shadow map

        var debugView = DebugView;
        cmd.PushConstants(pipeline, ShaderStages.Fragment, in debugView, offsetBytes: 64);

        // The camera list, in its stable sorted order. Span indexing: no enumerator, no allocation. Handles are
        // resolved to GPU resources here — the only place the two halves of the seam meet (spec §3.2).
        var items = renderList.Items;
        for (var i = 0; i < items.Length; i++)
        {
            ref readonly var item = ref items[i];
            var mesh = registry.Resolve(item.Mesh);

            cmd.BindDescriptorSet(pipeline, 1, registry.Resolve(item.Material).DescriptorSet); // set 1: per-material
            cmd.BindVertexBuffer(mesh.VertexBuffer);
            cmd.BindIndexBuffer(mesh.IndexBuffer, mesh.IndexFormat);

            var model = item.WorldTransform;
            cmd.PushConstants(pipeline, ShaderStages.Vertex, in model);
            cmd.DrawIndexed(mesh.IndexCount);
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
        _iblResources = null;
        _skyboxSetLayout = null;

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

        _shaderCompiler?.Dispose();

        _materialSetLayout = null;
        _frameSetLayout = null;
        _shaderCompiler = null;
    }
}
