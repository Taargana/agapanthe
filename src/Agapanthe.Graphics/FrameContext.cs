using Agapanthe.Core;
using Silk.NET.Vulkan;

namespace Agapanthe.Graphics;

/// <summary>
/// An opaque reference to a descriptor set. Its validity is that of the pool it came from:
/// a set from a <see cref="FrameContext"/> pool is valid only for that frame (the pool is reset once
/// the frame's fence signals, so holding it across frames is a use-after-free), while a set from a
/// <see cref="DescriptorAllocator"/> is persistent (valid until that allocator is disposed).
/// </summary>
public readonly struct DescriptorSetHandle
{
    internal DescriptorSetHandle(DescriptorSet set) => Set = set;

    internal DescriptorSet Set { get; }
}

/// <summary>
/// Per-frame-in-flight state handed to the draw callback: a descriptor pool that is reset
/// when this frame slot's fence signals (spec §3.4 — never while the frame is still in
/// flight), plus the slot index so callers can pick their per-frame buffers (e.g. camera UBO).
/// Persistent per-material sets belong in a separate persistent pool (M3+), not here.
/// </summary>
public sealed unsafe class FrameContext : IDisposable
{
    // Generous for M2 (one camera set per frame); grow-on-demand pools arrive with M3.
    private const uint MaxSets = 64;
    private const uint MaxUniformBuffers = 64;
    private const uint MaxCombinedImageSamplers = 64;

    private readonly GraphicsDevice _device;
    private DescriptorPool _pool;
    private bool _disposed;

    internal FrameContext(GraphicsDevice device, int slot)
    {
        _device = device;
        Slot = slot;

        var sizes = stackalloc DescriptorPoolSize[2]
        {
            new DescriptorPoolSize(DescriptorType.UniformBuffer, MaxUniformBuffers),
            new DescriptorPoolSize(DescriptorType.CombinedImageSampler, MaxCombinedImageSamplers),
        };
        var poolInfo = new DescriptorPoolCreateInfo
        {
            SType = StructureType.DescriptorPoolCreateInfo,
            MaxSets = MaxSets,
            PoolSizeCount = 2,
            PPoolSizes = sizes,
        };
        DescriptorPool pool;
        VkCheck.ThrowIfFailed(
            device.Api.CreateDescriptorPool(device.Device, &poolInfo, null, &pool),
            "vkCreateDescriptorPool");
        _pool = pool;
        ResourceTracker.Register("VkDescriptorPool");
    }

    ~FrameContext()
    {
        // Only report when a native handle was actually acquired; ctor argument-validation
        // exceptions reach the finalizer with nothing registered (audit M2, finding 1).
        if (_pool.Handle != 0)
        {
            ResourceTracker.ReportFinalizerLeak(nameof(FrameContext));
        }
    }

    /// <summary>Frame-in-flight slot index in [0, <see cref="GraphicsDevice.FramesInFlight"/>).</summary>
    public int Slot { get; }

    /// <summary>Allocates a descriptor set valid for this frame only.</summary>
    public DescriptorSetHandle AllocateSet(DescriptorSetLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var vkLayout = layout.Handle;
        var allocInfo = new DescriptorSetAllocateInfo
        {
            SType = StructureType.DescriptorSetAllocateInfo,
            DescriptorPool = _pool,
            DescriptorSetCount = 1,
            PSetLayouts = &vkLayout,
        };
        DescriptorSet set;
        VkCheck.ThrowIfFailed(
            _device.Api.AllocateDescriptorSets(_device.Device, &allocInfo, &set),
            "vkAllocateDescriptorSets");
        return new DescriptorSetHandle(set);
    }

    /// <summary>Points <paramref name="binding"/> of <paramref name="set"/> at a uniform buffer.</summary>
    public void WriteUniformBuffer(DescriptorSetHandle set, uint binding, GpuBuffer buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ObjectDisposedException.ThrowIf(_disposed, this);
        DescriptorWrites.UniformBuffer(_device, set.Set, binding, buffer);
    }

    /// <summary>
    /// Points <paramref name="binding"/> of <paramref name="set"/> at a combined image sampler
    /// (<paramref name="image"/> read through <paramref name="sampler"/>, layout ShaderReadOnlyOptimal).
    /// </summary>
    public void WriteCombinedImageSampler(DescriptorSetHandle set, uint binding, GpuImage image, Sampler sampler)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(sampler);
        ObjectDisposedException.ThrowIf(_disposed, this);
        DescriptorWrites.CombinedImageSampler(_device, set.Set, binding, image, sampler);
    }

    /// <summary>Frees all sets from this frame's pool. Called by the frame loop right after
    /// the slot's fence signals, never while the frame is in flight.</summary>
    internal void Reset()
        => VkCheck.ThrowIfFailed(
            _device.Api.ResetDescriptorPool(_device.Device, _pool, 0),
            "vkResetDescriptorPool");

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _device.Api.DestroyDescriptorPool(_device.Device, _pool, null);
        _pool = default;
        ResourceTracker.Unregister("VkDescriptorPool");
        GC.SuppressFinalize(this);
    }
}
