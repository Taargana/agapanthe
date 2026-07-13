using System.Diagnostics;
using System.Runtime.InteropServices;
using Agapanthe.Assets.Model;
using Agapanthe.Core;
using Agapanthe.Graphics;

namespace Agapanthe.Rendering;

/// <summary>
/// Generates an <see cref="IblMaps"/> set from an equirectangular HDR environment entirely on the GPU with
/// compute (spec §3.6, M7-04): equirect → radiance cubemap → diffuse irradiance → prefiltered specular
/// (roughness across the mip chain) → environment BRDF LUT. All four kernels run in a single load-time
/// <see cref="GraphicsDevice.SubmitImmediate"/> batch; the whole thing is off the per-frame hot path, so the
/// stall and the transient descriptor/upload allocations are acceptable.
/// <para>
/// <b>Barriers.</b> Each map is written as a storage image (layout General) then handed off: the environment
/// cubemap goes <c>General → ShaderReadOnlyCompute</c> because the very next kernels <i>sample</i> it in the
/// compute stage (irradiance and prefilter convolutions); the three consumed-later maps go
/// <c>General → ShaderReadOnly</c> for the fragment stage that reads them in a subsequent frame. Writing a
/// cube face is a 2D-array store through a per-mip <see cref="GpuImage.CreateMipView"/> view, so no
/// <c>imageCubeArray</c> feature is needed (MoltenVK portability).
/// </para>
/// <para>
/// <b>Ownership.</b> The generator owns its four compute pipelines, two set layouts, two samplers, the shaders
/// and the compiler for its own lifetime (reusable across HDRIs); each <see cref="Generate"/> call owns and
/// releases only its transient work (the staged equirect texture, an uploader and a descriptor pool). The
/// returned <see cref="IblMaps"/> is owned by the caller.
/// </para>
/// </summary>
public sealed class IblGenerator : IDisposable
{
    // Resolutions (board M7-04). The environment is single-mip: prefilter importance-samples its base level,
    // which is adequate for a milestone (a mip pyramid on the environment would further cut specular fireflies
    // and is a phase-2/M8 refinement). Irradiance is tiny (it is a very smooth cosine convolution); the
    // prefiltered cube is small with a full roughness mip chain; the BRDF LUT is environment-independent.
    private const uint EnvironmentSize = 512;
    private const uint IrradianceSize = 32;
    private const uint PrefilteredSize = 128;
    private const uint BrdfLutSize = 512;

    // RGBA16F everywhere for the radiance maps (linear HDR, universal storage + linear-filter support, unlike
    // 32-bit float which MoltenVK may not filter); the LUT is RG16F (two channels: scale + bias).
    private const PixelFormat RadianceFormat = PixelFormat.Rgba16Sfloat;
    private const PixelFormat BrdfFormat = PixelFormat.Rg16Sfloat;

    private const uint WorkgroupSize = 8; // matches local_size_x/y in every IBL kernel

    private readonly GraphicsDevice _device;
    private ShaderCompiler? _compiler;
    private ShaderModule? _equirectShader;
    private ShaderModule? _irradianceShader;
    private ShaderModule? _prefilterShader;
    private ShaderModule? _brdfShader;
    private DescriptorSetLayout? _sampledLayout;   // b0 = combined image sampler, b1 = storage image
    private DescriptorSetLayout? _storageLayout;   // b0 = storage image only (BRDF LUT)
    private ComputePipeline? _equirectPipeline;
    private ComputePipeline? _irradiancePipeline;
    private ComputePipeline? _prefilterPipeline;
    private ComputePipeline? _brdfPipeline;
    private Sampler? _equirectSampler;             // linear/repeat for the equirect longitude wrap
    private Sampler? _cubeSampler;                 // linear/clamp for the environment cubemap
    private bool _disposed;

    /// <summary>
    /// Builds the generator: compiles the four IBL compute kernels from <paramref name="shaderDirectory"/>
    /// (<c>ibl_equirect_to_cube.comp</c>, <c>ibl_irradiance.comp</c>, <c>ibl_prefilter.comp</c>,
    /// <c>ibl_brdf_lut.comp</c>), and creates the shared set layouts, compute pipelines and samplers.
    /// </summary>
    public IblGenerator(GraphicsDevice device, string shaderDirectory)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(shaderDirectory);
        _device = device;

        try
        {
            _compiler = ShaderCompiler.CreateForBuild();
            _equirectShader = Compile(shaderDirectory, "ibl_equirect_to_cube.comp");
            _irradianceShader = Compile(shaderDirectory, "ibl_irradiance.comp");
            _prefilterShader = Compile(shaderDirectory, "ibl_prefilter.comp");
            _brdfShader = Compile(shaderDirectory, "ibl_brdf_lut.comp");

            // Kernels 1-3 read one image through a sampler and write one storage image; kernel 4 (LUT) only
            // writes. The sampler binding is CombinedImageSampler for all three — the GLSL declaration differs
            // (sampler2D equirect vs samplerCube env) but the descriptor type is identical.
            _sampledLayout = new DescriptorSetLayout(
                device,
                [
                    new DescriptorBinding(0, DescriptorKind.CombinedImageSampler, ShaderStages.Compute),
                    new DescriptorBinding(1, DescriptorKind.StorageImage, ShaderStages.Compute),
                ]);
            _storageLayout = new DescriptorSetLayout(
                device,
                [new DescriptorBinding(0, DescriptorKind.StorageImage, ShaderStages.Compute)]);

            _equirectPipeline = new ComputePipeline(device, new ComputePipelineDesc
            {
                ComputeShader = _equirectShader,
                SetLayouts = [_sampledLayout],
            });
            _irradiancePipeline = new ComputePipeline(device, new ComputePipelineDesc
            {
                ComputeShader = _irradianceShader,
                SetLayouts = [_sampledLayout],
            });
            _prefilterPipeline = new ComputePipeline(device, new ComputePipelineDesc
            {
                ComputeShader = _prefilterShader,
                SetLayouts = [_sampledLayout],
                PushConstants = [new PushConstantRange(0, 4, ShaderStages.Compute)], // float roughness
            });
            _brdfPipeline = new ComputePipeline(device, new ComputePipelineDesc
            {
                ComputeShader = _brdfShader,
                SetLayouts = [_storageLayout],
            });

            // Equirect wraps in longitude (Repeat) and clamps at the poles; linear filtering (safe on the
            // half-float texture). The environment cubemap samples with linear/clamp (cube edges are seamless
            // in hardware regardless of address mode).
            _equirectSampler = new Sampler(device, new SamplerDesc(
                Filter: SamplerFilter.Linear, MipFilter: SamplerFilter.Linear, AddressMode: SamplerAddressMode.Repeat));
            _cubeSampler = new Sampler(device, new SamplerDesc(
                Filter: SamplerFilter.Linear, MipFilter: SamplerFilter.Linear, AddressMode: SamplerAddressMode.ClampToEdge));
        }
        catch
        {
            DisposeResources();
            throw;
        }
    }

    /// <summary>
    /// Generates a fresh <see cref="IblMaps"/> from <paramref name="equirect"/> (an equirectangular HDR
    /// environment, e.g. from <see cref="Agapanthe.Assets.HdrImageLoader"/>). Synchronous: stages the source,
    /// records every kernel into one immediate submit and blocks until the GPU finishes, then releases its
    /// transient resources. Logs the wall-clock generation time.
    /// </summary>
    /// <exception cref="ObjectDisposedException">The generator has been disposed.</exception>
    public IblMaps Generate(HdrImageAsset equirect)
    {
        ArgumentNullException.ThrowIfNull(equirect);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var stopwatch = Stopwatch.StartNew();

        var prefilteredMips = GpuImage.FullMipChain(PrefilteredSize, PrefilteredSize);

        // Declared before the try so the catch can release whatever was already created if a later
        // `new GpuImage`/`CreateMipView` throws (OOM, a rejected view under memory pressure): creating them
        // inside the try is the only way the failure path frees them (audit M7-07 finding M1). The mip-views
        // are owned by their GpuImage, so disposing the image releases them too — the catch only touches the
        // four images.
        GpuImage? environment = null;
        GpuImage? irradiance = null;
        GpuImage? prefiltered = null;
        GpuImage? brdfLut = null;
        GpuImage? equirectTexture = null;
        GpuUploader? uploader = null;
        DescriptorAllocator? descriptors = null;
        try
        {
            // Output maps. Every map is both a compute write target (Storage) and later sampled (Sampled);
            // TransferSrc lets debug captures read a face back (M7-04 AC). The three cubemaps use a Cube default
            // view (samplerCube read); their storage writes go through per-mip 2D-array views.
            environment = new GpuImage(
                _device, EnvironmentSize, EnvironmentSize, RadianceFormat,
                ImageUsage.Storage | ImageUsage.Sampled | ImageUsage.TransferSrc,
                arrayLayers: 6, viewKind: ImageViewKind.Cube);
            irradiance = new GpuImage(
                _device, IrradianceSize, IrradianceSize, RadianceFormat,
                ImageUsage.Storage | ImageUsage.Sampled | ImageUsage.TransferSrc,
                arrayLayers: 6, viewKind: ImageViewKind.Cube);
            prefiltered = new GpuImage(
                _device, PrefilteredSize, PrefilteredSize, RadianceFormat,
                ImageUsage.Storage | ImageUsage.Sampled | ImageUsage.TransferSrc,
                mipLevels: prefilteredMips, arrayLayers: 6, viewKind: ImageViewKind.Cube);
            brdfLut = new GpuImage(
                _device, BrdfLutSize, BrdfLutSize, BrdfFormat,
                ImageUsage.Storage | ImageUsage.Sampled | ImageUsage.TransferSrc);

            // Storage (write) views: whole-cube 2D-array per mip, single-2D for the LUT.
            var environmentStore = environment.CreateMipView(0, 0, 6);
            var irradianceStore = irradiance.CreateMipView(0, 0, 6);
            var prefilteredStore = new ImageMipView[prefilteredMips];
            for (var mip = 0u; mip < prefilteredMips; mip++)
            {
                prefilteredStore[mip] = prefiltered.CreateMipView(mip, 0, 6);
            }

            var brdfStore = brdfLut.CreateMipView(0);

            // Stage the equirect source as a half-float sampled texture (single mip). Half rather than 32-bit
            // float guarantees linear filtering support on every backend (MoltenVK).
            uploader = new GpuUploader(_device);
            equirectTexture = new GpuImage(
                _device, (uint)equirect.Width, (uint)equirect.Height, RadianceFormat,
                ImageUsage.Sampled | ImageUsage.TransferDst);
            var halfPixels = ToHalf(equirect.RgbaPixels);
            uploader.Upload<Half>(equirectTexture, halfPixels);

            // One descriptor set per dispatch (throwaway; the allocator is released after the submit). Sizes:
            // equirect + irradiance + N prefilter mips share the sampled layout; the LUT uses the storage-only
            // layout. All fit one pool.
            descriptors = new DescriptorAllocator(_device);

            var equirectSet = descriptors.AllocateSet(_sampledLayout!);
            descriptors.WriteCombinedImageSampler(equirectSet, 0, equirectTexture, _equirectSampler!);
            descriptors.WriteStorageImage(equirectSet, 1, environmentStore);

            var irradianceSet = descriptors.AllocateSet(_sampledLayout!);
            descriptors.WriteCombinedImageSampler(irradianceSet, 0, environment, _cubeSampler!);
            descriptors.WriteStorageImage(irradianceSet, 1, irradianceStore);

            var prefilterSets = new DescriptorSetHandle[prefilteredMips];
            for (var mip = 0u; mip < prefilteredMips; mip++)
            {
                var set = descriptors.AllocateSet(_sampledLayout!);
                descriptors.WriteCombinedImageSampler(set, 0, environment, _cubeSampler!);
                descriptors.WriteStorageImage(set, 1, prefilteredStore[mip]);
                prefilterSets[mip] = set;
            }

            var brdfSet = descriptors.AllocateSet(_storageLayout!);
            descriptors.WriteStorageImage(brdfSet, 0, brdfStore);

            _device.SubmitImmediate(cmd =>
            {
                // Each kernel is wrapped in a debug-label region (M8-07) so a RenderDoc/Nsight capture of the
                // load-time IBL bake shows the four stages by name; the transient CommandList exposes the same
                // label API (no-op when debug utils is off). Names are literals — no allocation.

                // --- Kernel 1: equirect -> environment cubemap. Then hand it to the compute readers below.
                using (cmd.PushDebugLabel("IBL: EquirectToCube"))
                {
                    cmd.TransitionImage(environment, ImageLayoutState.Undefined, ImageLayoutState.General);
                    cmd.BindPipeline(_equirectPipeline!);
                    cmd.BindDescriptorSet(_equirectPipeline!, 0, equirectSet);
                    cmd.Dispatch(Groups(EnvironmentSize), Groups(EnvironmentSize), 6);
                    cmd.TransitionImage(environment, ImageLayoutState.General, ImageLayoutState.ShaderReadOnlyCompute);
                }

                // --- Kernel 2: diffuse irradiance (samples the environment cubemap).
                using (cmd.PushDebugLabel("IBL: Irradiance"))
                {
                    cmd.TransitionImage(irradiance, ImageLayoutState.Undefined, ImageLayoutState.General);
                    cmd.BindPipeline(_irradiancePipeline!);
                    cmd.BindDescriptorSet(_irradiancePipeline!, 0, irradianceSet);
                    cmd.Dispatch(Groups(IrradianceSize), Groups(IrradianceSize), 6);
                    cmd.TransitionImage(irradiance, ImageLayoutState.General, ImageLayoutState.ShaderReadOnly);
                }

                // --- Kernel 3: prefiltered specular, one dispatch per roughness mip (disjoint mip
                // subresources, so no inter-dispatch barrier is needed).
                using (cmd.PushDebugLabel("IBL: Prefilter"))
                {
                    cmd.TransitionImage(prefiltered, ImageLayoutState.Undefined, ImageLayoutState.General);
                    cmd.BindPipeline(_prefilterPipeline!);
                    for (var mip = 0u; mip < prefilteredMips; mip++)
                    {
                        var roughness = prefilteredMips == 1 ? 0f : (float)mip / (prefilteredMips - 1);
                        cmd.PushConstants(_prefilterPipeline!, ShaderStages.Compute, in roughness);
                        cmd.BindDescriptorSet(_prefilterPipeline!, 0, prefilterSets[mip]);
                        var (mipW, mipH) = GpuImage.MipSize(PrefilteredSize, PrefilteredSize, mip);
                        cmd.Dispatch(Groups(mipW), Groups(mipH), 6);
                    }

                    cmd.TransitionImage(prefiltered, ImageLayoutState.General, ImageLayoutState.ShaderReadOnly);
                }

                // --- Kernel 4: environment BRDF LUT (no environment input).
                using (cmd.PushDebugLabel("IBL: BRDF LUT"))
                {
                    cmd.TransitionImage(brdfLut, ImageLayoutState.Undefined, ImageLayoutState.General);
                    cmd.BindPipeline(_brdfPipeline!);
                    cmd.BindDescriptorSet(_brdfPipeline!, 0, brdfSet);
                    cmd.Dispatch(Groups(BrdfLutSize), Groups(BrdfLutSize), 1);
                    cmd.TransitionImage(brdfLut, ImageLayoutState.General, ImageLayoutState.ShaderReadOnly);
                }
            });

            // The submit fenced already, but the transient descriptor pool is destroyed synchronously — make
            // the whole device idle before releasing it, to satisfy the allocator's contract unconditionally.
            _device.WaitIdle();
        }
        catch
        {
            environment?.Dispose();
            irradiance?.Dispose();
            prefiltered?.Dispose();
            brdfLut?.Dispose();
            throw;
        }
        finally
        {
            descriptors?.Dispose();
            equirectTexture?.Dispose();
            uploader?.Dispose();
        }

        stopwatch.Stop();
        Log.Info(
            $"IblGenerator: generated IBL from {equirect.Width}x{equirect.Height} HDR in {stopwatch.ElapsedMilliseconds} ms " +
            $"(env {EnvironmentSize}², irradiance {IrradianceSize}², prefiltered {PrefilteredSize}²×{prefilteredMips} mips, BRDF LUT {BrdfLutSize}²).");

        // Non-null on the success path (the try assigned all four; any throw went through the catch).
        return new IblMaps(environment!, irradiance!, prefiltered!, brdfLut!);
    }

    /// <summary>Disposes the generator's pipelines, layouts, samplers and shaders. Not GPU-idle-guarded here
    /// (all its transient per-<see cref="Generate"/> work already fenced); the caller idles the device at
    /// shutdown before the deferred deletion queue is drained.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        DisposeResources();
    }

    private ShaderModule Compile(string shaderDirectory, string fileName)
    {
        // CompileFileResolved (resolved-source cache key), not CompileFile (raw-source key): the build-time
        // precompiler pre-cooks every shader via CompileFileResolved, so the IBL kernels must look up under the
        // SAME key or a cache-only Release build would miss (audit M3). Identical today (the kernels have no
        // #include, so resolved == raw), but this stays correct the day one gains an include. The resolved file
        // list is unused — the IBL kernels are not hot-reloaded.
        var (spirv, _) = _compiler!.CompileFileResolved(Path.Combine(shaderDirectory, fileName), ShaderStage.Compute);
        return new ShaderModule(_device, spirv, ShaderStage.Compute);
    }

    // Whole-workgroup count covering `size` texels at the kernels' 8-wide local size (ceil division; the
    // kernels drop the out-of-range tail when size is not a multiple of 8).
    private static uint Groups(uint size) => (size + WorkgroupSize - 1) / WorkgroupSize;

    // float32 -> float16 conversion of the tightly-packed RGBA pixels for a half-float upload. Values are
    // clamped to Half.MaxValue and NaN is scrubbed to 0 first (audit M7-07 finding M2): outdoor HDRIs routinely
    // exceed 65504 (sun disc, blown speculars), and a bare cast would turn those into +Inf — a single Inf texel
    // sampled linearly then poisons the irradiance/prefilter integrals into Inf/NaN across the whole map.
    private const float HalfMax = 65504f;

    private static Half[] ToHalf(float[] pixels)
    {
        var result = new Half[pixels.Length];
        for (var i = 0; i < pixels.Length; i++)
        {
            var v = pixels[i];
            result[i] = (Half)(float.IsNaN(v) ? 0f : Math.Clamp(v, -HalfMax, HalfMax));
        }

        return result;
    }

    private void DisposeResources()
    {
        _equirectPipeline?.Dispose();
        _irradiancePipeline?.Dispose();
        _prefilterPipeline?.Dispose();
        _brdfPipeline?.Dispose();
        _sampledLayout?.Dispose();
        _storageLayout?.Dispose();
        _equirectSampler?.Dispose();
        _cubeSampler?.Dispose();
        _equirectShader?.Dispose();
        _irradianceShader?.Dispose();
        _prefilterShader?.Dispose();
        _brdfShader?.Dispose();
        _compiler?.Dispose();

        _equirectPipeline = null;
        _irradiancePipeline = null;
        _prefilterPipeline = null;
        _brdfPipeline = null;
        _sampledLayout = null;
        _storageLayout = null;
        _equirectSampler = null;
        _cubeSampler = null;
        _equirectShader = null;
        _irradianceShader = null;
        _prefilterShader = null;
        _brdfShader = null;
        _compiler = null;
    }
}
