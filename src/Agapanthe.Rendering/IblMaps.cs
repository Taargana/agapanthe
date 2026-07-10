using Agapanthe.Graphics;

namespace Agapanthe.Rendering;

/// <summary>
/// The image-based-lighting resource set produced by <see cref="IblGenerator"/> from one equirectangular HDR
/// environment (spec §3.6, M7): the environment cubemap plus the three precomputed maps the split-sum PBR
/// ambient reads. Every map is <c>RGBA16F</c> (or <c>RG16F</c> for the LUT), sampled by the mesh/skybox
/// shaders (M7-05); the generator leaves them all in ShaderReadOnlyOptimal.
/// <list type="bullet">
///   <item><see cref="Environment"/> — the full-resolution radiance cubemap (drawn directly as the skybox).</item>
///   <item><see cref="Irradiance"/> — diffuse irradiance cubemap (cosine convolution; the Lambertian ambient).</item>
///   <item><see cref="Prefiltered"/> — specular radiance prefiltered per roughness across its mip chain
///   (<see cref="PrefilteredMipCount"/> levels); the shader samples LOD = roughness·(mips−1).</item>
///   <item><see cref="BrdfLut"/> — the environment BRDF scale/bias LUT (RG16F, environment-independent).</item>
/// </list>
/// <para>
/// <b>Ownership.</b> This owns all four images and disposes them together (deferred, through the device
/// deletion queue). The caller (<see cref="Renderer"/>, M7-05) holds one instance for the app lifetime and
/// disposes it at shutdown after the GPU is idle, like the other renderer-owned targets.
/// </para>
/// </summary>
public sealed class IblMaps : IDisposable
{
    private bool _disposed;

    internal IblMaps(GpuImage environment, GpuImage irradiance, GpuImage prefiltered, GpuImage brdfLut)
    {
        Environment = environment;
        Irradiance = irradiance;
        Prefiltered = prefiltered;
        BrdfLut = brdfLut;
    }

    /// <summary>The radiance environment cubemap (RGBA16F). Sampled directly by the skybox pass.</summary>
    public GpuImage Environment { get; }

    /// <summary>The diffuse irradiance cubemap (RGBA16F, low resolution).</summary>
    public GpuImage Irradiance { get; }

    /// <summary>The prefiltered specular cubemap (RGBA16F); roughness is encoded across its mip chain.</summary>
    public GpuImage Prefiltered { get; }

    /// <summary>The environment BRDF integration LUT (RG16F, 2D).</summary>
    public GpuImage BrdfLut { get; }

    /// <summary>Number of roughness mip levels in <see cref="Prefiltered"/> (shader LOD = roughness·(this−1)).</summary>
    public uint PrefilteredMipCount => Prefiltered.MipLevels;

    /// <summary>Disposes all four maps (deferred through the device deletion queue).</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Environment.Dispose();
        Irradiance.Dispose();
        Prefiltered.Dispose();
        BrdfLut.Dispose();
    }
}
