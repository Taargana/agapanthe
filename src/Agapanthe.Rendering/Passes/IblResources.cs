using Agapanthe.Assets.Model;
using Agapanthe.Graphics;

namespace Agapanthe.Rendering.Passes;

/// <summary>
/// Bundles the image-based-lighting resources the <see cref="Renderer"/> used to hold as loose fields (M7):
/// the reusable <see cref="IblGenerator"/>, the current <see cref="IblMaps"/> (produced from an HDRI via
/// <see cref="SetEnvironment"/>) and the single linear/clamp sampler shared by the mesh IBL reads and the
/// skybox environment sample.
/// <para>
/// <b>Ownership.</b> Owns the generator, the current maps and the sampler; disposing releases all three
/// (deferred for the GPU objects). The <see cref="Renderer"/> holds one instance for its lifetime and disposes
/// it at shutdown after the GPU is idle.
/// </para>
/// </summary>
internal sealed class IblResources : IDisposable
{
    private IblGenerator? _generator;
    private IblMaps? _maps;
    private Sampler? _sampler;
    private bool _disposed;

    /// <summary>
    /// Builds the generator (compiles the four IBL compute kernels from <paramref name="shaderDirectory"/>)
    /// and the shared linear/clamp sampler. The maps stay null until <see cref="SetEnvironment"/>.
    /// </summary>
    public IblResources(GraphicsDevice device, string shaderDirectory)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(shaderDirectory);

        try
        {
            _generator = new IblGenerator(device, shaderDirectory);
            // One linear/clamp sampler serves the irradiance, prefiltered (mip-walked for roughness) and BRDF
            // LUT reads, and the skybox's environment sample.
            _sampler = new Sampler(device, new SamplerDesc(
                Filter: SamplerFilter.Linear, MipFilter: SamplerFilter.Linear, AddressMode: SamplerAddressMode.ClampToEdge));
        }
        catch
        {
            DisposeResources();
            throw;
        }
    }

    /// <summary>The shared linear/clamp sampler for every IBL and skybox environment read.</summary>
    public Sampler Sampler => _sampler!;

    /// <summary>The current IBL maps, or <c>null</c> until an environment has been set.</summary>
    public IblMaps? Maps => _maps;

    /// <summary>Whether an environment has been generated and adopted (guards <see cref="Renderer.DrawScene"/>).</summary>
    public bool HasEnvironment => _maps is not null;

    /// <summary>
    /// Generates the IBL maps from <paramref name="environment"/> and adopts them, replacing (and releasing,
    /// deferred) any previously-set maps. Generates into a temporary and swaps only on success, so a failed
    /// <see cref="IblGenerator.Generate"/> leaves the previously-adopted maps valid (audit M7-07 finding m3).
    /// </summary>
    public void SetEnvironment(HdrImageAsset environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var maps = _generator!.Generate(environment);
        _maps?.Dispose();
        _maps = maps;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        DisposeResources();
    }

    private void DisposeResources()
    {
        _maps?.Dispose();
        _generator?.Dispose();
        _sampler?.Dispose();
        _maps = null;
        _generator = null;
        _sampler = null;
    }
}
