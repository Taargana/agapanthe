using Agapanthe.Core;
using Silk.NET.Vulkan;
using VkSampler = Silk.NET.Vulkan.Sampler;
using VkAddressMode = Silk.NET.Vulkan.SamplerAddressMode;

namespace Agapanthe.Graphics;

/// <summary>Minification/magnification filter for a <see cref="Sampler"/>.</summary>
public enum SamplerFilter
{
    /// <summary>Linear (bilinear) filtering — the sensible default for textures.</summary>
    Linear = 0,

    /// <summary>Nearest-neighbour filtering (blocky; pixel-art / data textures).</summary>
    Nearest = 1,
}

/// <summary>How texture coordinates outside <c>[0, 1]</c> are resolved by a <see cref="Sampler"/>.</summary>
public enum SamplerAddressMode
{
    /// <summary>Tile the texture (the default).</summary>
    Repeat = 0,

    /// <summary>Clamp to the edge texel.</summary>
    ClampToEdge = 1,

    /// <summary>Mirror on every repeat.</summary>
    MirroredRepeat = 2,
}

/// <summary>
/// Options for a <see cref="Sampler"/>, expressed without any Vulkan types. Every field's zero value is
/// the sensible default, so <c>default(SamplerDesc)</c> and <c>new SamplerDesc()</c> both yield
/// Linear/Linear filtering, Repeat addressing and no anisotropy.
/// </summary>
/// <param name="Filter">Min/mag filter. Default <see cref="SamplerFilter.Linear"/>.</param>
/// <param name="MipFilter">Mip filter (mipmap sampling mode). Default <see cref="SamplerFilter.Linear"/>.</param>
/// <param name="AddressMode">Wrap mode for all three axes. Default <see cref="SamplerAddressMode.Repeat"/>.</param>
/// <param name="MaxAnisotropy">
/// Requested max anisotropy; <c>0</c> (default) disables it. Clamped to the device
/// <c>maxSamplerAnisotropy</c> limit. Only honoured when the <c>samplerAnisotropy</c> device feature was
/// enabled at device creation — see <see cref="Sampler"/>.
/// </param>
/// <param name="MipLodBias">LOD bias added when sampling mips. Default <c>0</c>.</param>
public readonly record struct SamplerDesc(
    SamplerFilter Filter = SamplerFilter.Linear,
    SamplerFilter MipFilter = SamplerFilter.Linear,
    SamplerAddressMode AddressMode = SamplerAddressMode.Repeat,
    float MaxAnisotropy = 0f,
    float MipLodBias = 0f);

/// <summary>
/// A texture sampler (<c>VkSampler</c>). Immutable once created; combined with a <see cref="GpuImage"/>
/// view when writing a combined-image-sampler descriptor
/// (<see cref="FrameContext.WriteCombinedImageSampler"/> / <see cref="DescriptorAllocator.WriteCombinedImageSampler"/>).
/// <para>
/// Follows the module resource pattern: registered in the <see cref="ResourceTracker"/> as "VkSampler",
/// a finalizer that only reports a leak when a handle was actually acquired, and deferred disposal
/// through the device <see cref="DeletionQueue"/> with a non-capturing payload (spec §3.2.5) — the raw
/// <c>VkSampler</c> handle travels by value and a cached static destructor frees it once the frame that
/// used it leaves flight.
/// </para>
/// <para>
/// <b>Anisotropy.</b> <see cref="SamplerDesc.MaxAnisotropy"/> is honoured only when the
/// <c>samplerAnisotropy</c> device feature was enabled at logical-device creation. As of M3,
/// <see cref="GraphicsDevice.CreateLogicalDevice"/> enables only <c>dynamicRendering</c> and
/// <c>synchronization2</c> — <b>not</b> <c>samplerAnisotropy</c> — so anisotropy is <b>silently disabled</b>
/// (a debug log notes it if requested). Enabling it is a device-creation change (out of this task's
/// scope); flip <see cref="AnisotropyFeatureEnabled"/> once the feature is turned on and the clamp path
/// below activates against the device limit.
/// </para>
/// </summary>
public sealed unsafe class Sampler : IDisposable
{
    // Tracks whether the samplerAnisotropy device feature is enabled at device creation. Kept as a
    // constant because it mirrors GraphicsDevice.CreateLogicalDevice, which does not enable it in M3.
    // Enabling anisotropy requires enabling the feature there first; then set this to true.
    private const bool AnisotropyFeatureEnabled = false;

    // VK_LOD_CLAMP_NONE — sample the entire mip chain with no artificial max LOD.
    private const float LodClampNone = 1000f;

    private readonly GraphicsDevice _device;
    private VkSampler _handle;
    private bool _disposed;

    /// <summary>Creates a sampler from <paramref name="desc"/> (defaults to Linear/Linear/Repeat, no aniso).</summary>
    public Sampler(GraphicsDevice device, SamplerDesc desc = default)
    {
        ArgumentNullException.ThrowIfNull(device);

        _device = device;
        Desc = desc;
        var vk = device.Api;

        // Anisotropy is only legal when the device feature is enabled; otherwise it MUST stay off and
        // MaxAnisotropy MUST be 1.0 (a validation error otherwise). See the class remarks.
        var anisotropyRequested = desc.MaxAnisotropy > 0f;
        var anisotropyEnabled = AnisotropyFeatureEnabled && anisotropyRequested;
        var maxAnisotropy = 1f;
        if (anisotropyEnabled)
        {
            var limits = vk.GetPhysicalDeviceProperties(device.PhysicalDevice).Limits;
            maxAnisotropy = Math.Min(desc.MaxAnisotropy, limits.MaxSamplerAnisotropy);
        }
        else if (anisotropyRequested)
        {
            Log.Debug(
                "Sampler: anisotropy requested but the samplerAnisotropy device feature is not enabled; sampling isotropically.");
        }

        var info = new SamplerCreateInfo
        {
            SType = StructureType.SamplerCreateInfo,
            MagFilter = ToVkFilter(desc.Filter),
            MinFilter = ToVkFilter(desc.Filter),
            MipmapMode = ToVkMipmapMode(desc.MipFilter),
            AddressModeU = ToVkAddress(desc.AddressMode),
            AddressModeV = ToVkAddress(desc.AddressMode),
            AddressModeW = ToVkAddress(desc.AddressMode),
            MipLodBias = desc.MipLodBias,
            AnisotropyEnable = anisotropyEnabled,
            MaxAnisotropy = maxAnisotropy,
            CompareEnable = false,
            CompareOp = CompareOp.Always,
            MinLod = 0f,
            MaxLod = LodClampNone,
            BorderColor = BorderColor.FloatOpaqueBlack,
            UnnormalizedCoordinates = false,
        };

        VkSampler handle;
        VkCheck.ThrowIfFailed(vk.CreateSampler(device.Device, &info, null, &handle), "vkCreateSampler");
        _handle = handle;
        ResourceTracker.Register("VkSampler");
    }

    ~Sampler()
    {
        // Only report when a native handle was actually acquired; ctor argument-validation exceptions
        // reach the finalizer with nothing registered (audit M2, finding 1).
        if (_handle.Handle != 0)
        {
            ResourceTracker.ReportFinalizerLeak(nameof(Sampler));
        }
    }

    /// <summary>The options this sampler was created with.</summary>
    public SamplerDesc Desc { get; }

    internal VkSampler Handle => _handle;

    /// <summary>
    /// Deferred disposal (spec §3.2.1): the sampler is destroyed once the frame that used it leaves
    /// flight. The payload carries only the raw <c>VkSampler</c> handle (Handle0) and the destructor is a
    /// cached static delegate, so this allocates nothing (spec §3.2.5).
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        var payload = new DeletionPayload(_handle.Handle);
        _handle = default;
        _device.EnqueueDestroy(DestroyDelegate, in payload);
        GC.SuppressFinalize(this);
    }

    // Allocated once per type: passing this reference on the hot path costs no allocation.
    private static readonly Action<GraphicsDevice, DeletionPayload> DestroyDelegate = DestroyDeferred;

    private static void DestroyDeferred(GraphicsDevice device, DeletionPayload payload)
    {
        var sampler = new VkSampler(payload.Handle0);
        if (sampler.Handle != 0)
        {
            device.Api.DestroySampler(device.Device, sampler, null);
            ResourceTracker.Unregister("VkSampler");
        }
    }

    private static Filter ToVkFilter(SamplerFilter filter) =>
        filter == SamplerFilter.Nearest ? Filter.Nearest : Filter.Linear;

    private static SamplerMipmapMode ToVkMipmapMode(SamplerFilter filter) =>
        filter == SamplerFilter.Nearest ? SamplerMipmapMode.Nearest : SamplerMipmapMode.Linear;

    private static VkAddressMode ToVkAddress(SamplerAddressMode mode) => mode switch
    {
        SamplerAddressMode.Repeat => VkAddressMode.Repeat,
        SamplerAddressMode.ClampToEdge => VkAddressMode.ClampToEdge,
        SamplerAddressMode.MirroredRepeat => VkAddressMode.MirroredRepeat,
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown sampler address mode."),
    };
}
