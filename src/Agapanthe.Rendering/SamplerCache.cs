using Agapanthe.Assets.Model;
using Agapanthe.Graphics;

namespace Agapanthe.Rendering;

/// <summary>
/// Deduplicating cache of <see cref="Sampler"/>s keyed by their <see cref="SamplerDesc"/> (architect
/// decision session 3, #5): a glTF model reuses the same handful of sampler configs across many textures,
/// so one <c>VkSampler</c> per distinct config is created and shared. <see cref="Material"/> borrows a
/// sampler from here and never owns it.
/// <para>
/// <b>Assets → Graphics mapping.</b> <see cref="TextureSettings"/> has richer axes than
/// <see cref="SamplerDesc"/>, so the mapping collapses:
/// </para>
/// <list type="bullet">
///   <item><b>AddressMode</b> ← <see cref="TextureSettings.WrapU"/>. <see cref="SamplerDesc"/> exposes a
///   single wrap mode for all axes; <see cref="TextureSettings.WrapV"/> is dropped. glTF assets almost
///   always set U and V wrap equal, so this is lossless in practice.</item>
///   <item><b>Filter</b> (Vulkan min <i>and</i> mag) ← <see cref="TextureSettings.MagFilter"/>.
///   <see cref="SamplerDesc"/> has one filter for both; the magnification filter is chosen because it
///   governs the close-up appearance.</item>
///   <item><b>MipFilter</b> ← <see cref="TextureSettings.MinFilter"/>. glTF's minification filter carries
///   the mip-sampling intent (Assets already collapsed the mipmap-aware GL modes to Linear/Nearest).</item>
///   <item><b>MaxAnisotropy</b> = 8 — the device clamps to its limit and falls back to isotropic
///   when the <c>samplerAnisotropy</c> feature is unsupported. <b>MipLodBias</b> = 0.</item>
/// </list>
/// <b>Ownership.</b> Owns every sampler it created; <see cref="Dispose"/> disposes them all (deferred
/// through the device DeletionQueue by <see cref="Sampler"/>). Owned by <see cref="Scene"/>.
/// </summary>
public sealed class SamplerCache : IDisposable
{
    private readonly GraphicsDevice _device;
    private readonly Dictionary<SamplerDesc, Sampler> _samplers = new();
    private bool _disposed;

    public SamplerCache(GraphicsDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);
        _device = device;
    }

    /// <summary>
    /// Returns the shared sampler for <paramref name="settings"/>, creating it on first request. The
    /// returned sampler is owned by this cache — do not dispose it.
    /// </summary>
    public Sampler Get(TextureSettings settings)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var desc = ToSamplerDesc(settings);
        if (_samplers.TryGetValue(desc, out var existing))
        {
            return existing;
        }

        var sampler = new Sampler(_device, desc);
        _samplers.Add(desc, sampler);
        return sampler;
    }

    /// <summary>Maps Assets sampler settings to a Graphics <see cref="SamplerDesc"/> (see the class remarks for the collapse rules).</summary>
    public static SamplerDesc ToSamplerDesc(TextureSettings settings) => new(
        Filter: ToFilter(settings.MagFilter),
        MipFilter: ToFilter(settings.MinFilter),
        AddressMode: ToAddressMode(settings.WrapU),
        MaxAnisotropy: 8f,
        MipLodBias: 0f);

    private static SamplerFilter ToFilter(TextureFilter filter) => filter switch
    {
        TextureFilter.Nearest => SamplerFilter.Nearest,
        _ => SamplerFilter.Linear,
    };

    private static SamplerAddressMode ToAddressMode(TextureWrap wrap) => wrap switch
    {
        TextureWrap.ClampToEdge => SamplerAddressMode.ClampToEdge,
        TextureWrap.MirroredRepeat => SamplerAddressMode.MirroredRepeat,
        _ => SamplerAddressMode.Repeat,
    };

    /// <summary>Disposes every cached sampler. The device should be idle (deferred destroys drain from the DeletionQueue).</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var sampler in _samplers.Values)
        {
            sampler.Dispose();
        }

        _samplers.Clear();
    }
}
