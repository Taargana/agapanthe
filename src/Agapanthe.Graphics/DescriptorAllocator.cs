using Agapanthe.Core;
using Silk.NET.Vulkan;

namespace Agapanthe.Graphics;

/// <summary>
/// Persistent descriptor-set allocator for sets whose lifetime spans many frames — the per-material
/// set 1 (spec §3.4). Holds a growing list of <c>VkDescriptorPool</c>s: sets are allocated from the
/// current pool, and a fresh pool is created on demand when the current one runs out of space
/// (<c>VK_ERROR_OUT_OF_POOL_MEMORY</c>) or is fragmented (<c>VK_ERROR_FRAGMENTED_POOL</c>). Pools are
/// <b>never reset</b> per frame — that is the job of the per-frame pool inside <see cref="FrameContext"/>.
/// <para>
/// <b>Ownership.</b> This is created and owned by the caller (the Sandbox / Renderer), not by
/// <see cref="GraphicsDevice"/>: <c>new DescriptorAllocator(device)</c>. <see cref="Dispose"/> destroys
/// every pool <b>synchronously</b>, so the caller must ensure the GPU is idle first
/// (<see cref="GraphicsDevice.WaitIdle"/>) — the persistent sets may still be referenced by in-flight
/// frames otherwise. Each pool is tracked in the <see cref="ResourceTracker"/> as "VkDescriptorPool".
/// </para>
/// </summary>
public sealed unsafe class DescriptorAllocator : IDisposable
{
    // Fixed per-pool sizes (spec §3.4 — grow-on-demand, not one-giant-pool). 64 sets per pool with
    // room for one uniform buffer + one combined image sampler each covers the M3 per-material layout
    // (set 1, binding 0 = baseColor) with slack; a new pool is minted when a pool fills up.
    private const uint SetsPerPool = 64;
    private const uint UniformBuffersPerPool = 64;
    private const uint CombinedImageSamplersPerPool = 64;
    // Storage images are rare (compute write targets, M7): a handful of IBL views, not per-material. 16 per
    // pool is ample and keeps the persistent pool from over-reserving descriptor memory.
    private const uint StorageImagesPerPool = 16;

    private readonly GraphicsDevice _device;
    private readonly List<DescriptorPool> _pools = new();
    private DescriptorPool _current;
    private bool _disposed;

    /// <summary>Creates an allocator owned by the caller; no pool is created until the first allocation.</summary>
    public DescriptorAllocator(GraphicsDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);
        _device = device;
    }

    ~DescriptorAllocator()
    {
        // Only report when pools were actually created and not released; ctor argument-validation
        // exceptions reach the finalizer with an empty list (audit M2, finding 1).
        if (_pools.Count > 0)
        {
            ResourceTracker.ReportFinalizerLeak(nameof(DescriptorAllocator));
        }
    }

    /// <summary>
    /// Allocates a persistent descriptor set for <paramref name="layout"/>. Valid for the lifetime of
    /// this allocator (it lives in a pool that is never reset); freed only when the whole allocator is
    /// disposed. Grows a new pool transparently when the current one is exhausted or fragmented.
    /// </summary>
    public DescriptorSetHandle AllocateSet(DescriptorSetLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_pools.Count == 0)
        {
            CreatePool();
        }

        if (TryAllocate(layout, out var set))
        {
            return new DescriptorSetHandle(set);
        }

        // Current pool is full or fragmented → grow and retry once. A fresh pool cannot be
        // out-of-space for a single set, so a second failure is a real error.
        CreatePool();
        if (TryAllocate(layout, out set))
        {
            return new DescriptorSetHandle(set);
        }

        throw new GraphicsException("vkAllocateDescriptorSets failed on a freshly created descriptor pool.");
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

    /// <summary>
    /// Points <paramref name="binding"/> of <paramref name="set"/> at <paramref name="image"/>'s default view
    /// as a storage image (compute read/write, layout General, no sampler).
    /// </summary>
    public void WriteStorageImage(DescriptorSetHandle set, uint binding, GpuImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        ObjectDisposedException.ThrowIf(_disposed, this);
        DescriptorWrites.StorageImage(_device, set.Set, binding, image.View);
    }

    /// <summary>
    /// Points <paramref name="binding"/> of <paramref name="set"/> at a specific <paramref name="view"/>
    /// (one mip / layer subrange from <see cref="GpuImage.CreateMipView"/>) as a storage image (layout General).
    /// </summary>
    public void WriteStorageImage(DescriptorSetHandle set, uint binding, ImageMipView view)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (view.Handle.Handle == 0)
        {
            throw new GraphicsException("WriteStorageImage received a default ImageMipView; use one from CreateMipView.");
        }

        DescriptorWrites.StorageImage(_device, set.Set, binding, view.Handle);
    }

    /// <summary>
    /// Destroys every pool synchronously. Only valid after <see cref="GraphicsDevice.WaitIdle"/>: the
    /// persistent sets are not deferred through the DeletionQueue, so no frame may still reference them.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var pool in _pools)
        {
            _device.Api.DestroyDescriptorPool(_device.Device, pool, null);
            ResourceTracker.Unregister("VkDescriptorPool");
        }

        _pools.Clear();
        _current = default;
        GC.SuppressFinalize(this);
    }

    private bool TryAllocate(DescriptorSetLayout layout, out DescriptorSet set)
    {
        var vkLayout = layout.Handle;
        var allocInfo = new DescriptorSetAllocateInfo
        {
            SType = StructureType.DescriptorSetAllocateInfo,
            DescriptorPool = _current,
            DescriptorSetCount = 1,
            PSetLayouts = &vkLayout,
        };
        DescriptorSet allocated;
        var result = _device.Api.AllocateDescriptorSets(_device.Device, &allocInfo, &allocated);
        switch (result)
        {
            case Result.Success:
                set = allocated;
                return true;
            case Result.ErrorOutOfPoolMemory:
            case Result.ErrorFragmentedPool:
                set = default;
                return false;
            default:
                throw new GraphicsException($"vkAllocateDescriptorSets failed: {result}");
        }
    }

    private void CreatePool()
    {
        var sizes = stackalloc DescriptorPoolSize[3]
        {
            new DescriptorPoolSize(DescriptorType.UniformBuffer, UniformBuffersPerPool),
            new DescriptorPoolSize(DescriptorType.CombinedImageSampler, CombinedImageSamplersPerPool),
            new DescriptorPoolSize(DescriptorType.StorageImage, StorageImagesPerPool),
        };
        var poolInfo = new DescriptorPoolCreateInfo
        {
            SType = StructureType.DescriptorPoolCreateInfo,
            MaxSets = SetsPerPool,
            PoolSizeCount = 3,
            PPoolSizes = sizes,
        };
        DescriptorPool pool;
        VkCheck.ThrowIfFailed(
            _device.Api.CreateDescriptorPool(_device.Device, &poolInfo, null, &pool),
            "vkCreateDescriptorPool");
        _pools.Add(pool);
        _current = pool;
        ResourceTracker.Register("VkDescriptorPool");
    }
}

/// <summary>
/// Descriptor-set write helpers shared by the per-frame pool (<see cref="FrameContext"/>) and the
/// persistent per-material pool (<see cref="DescriptorAllocator"/>). Centralising the
/// <c>vkUpdateDescriptorSets</c> calls here keeps the two allocators from duplicating the write logic.
/// </summary>
internal static unsafe class DescriptorWrites
{
    public static void UniformBuffer(GraphicsDevice device, DescriptorSet set, uint binding, GpuBuffer buffer)
    {
        var bufferInfo = new DescriptorBufferInfo
        {
            Buffer = buffer.Handle,
            Offset = 0,
            Range = buffer.SizeBytes,
        };
        var write = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = set,
            DstBinding = binding,
            DescriptorCount = 1,
            DescriptorType = DescriptorType.UniformBuffer,
            PBufferInfo = &bufferInfo,
        };
        device.Api.UpdateDescriptorSets(device.Device, 1, &write, 0, null);
    }

    public static void StorageBuffer(GraphicsDevice device, DescriptorSet set, uint binding, GpuBuffer buffer)
    {
        // Read-only storage buffer (SSBO), whole range. Read from the vertex shader (per-instance
        // transforms, P3-M1); no writes/atomics, so no vertexPipelineStoresAndAtomics feature needed.
        var bufferInfo = new DescriptorBufferInfo
        {
            Buffer = buffer.Handle,
            Offset = 0,
            Range = buffer.SizeBytes,
        };
        var write = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = set,
            DstBinding = binding,
            DescriptorCount = 1,
            DescriptorType = DescriptorType.StorageBuffer,
            PBufferInfo = &bufferInfo,
        };
        device.Api.UpdateDescriptorSets(device.Device, 1, &write, 0, null);
    }

    public static void StorageImage(GraphicsDevice device, DescriptorSet set, uint binding, ImageView view)
    {
        // Storage image: no sampler, layout General (the compute write layout, matching the transition the
        // IBL generator records). DescriptorCount 1 — one view per binding.
        var imageInfo = new DescriptorImageInfo
        {
            ImageView = view,
            ImageLayout = ImageLayout.General,
        };
        var write = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = set,
            DstBinding = binding,
            DescriptorCount = 1,
            DescriptorType = DescriptorType.StorageImage,
            PImageInfo = &imageInfo,
        };
        device.Api.UpdateDescriptorSets(device.Device, 1, &write, 0, null);
    }

    public static void CombinedImageSampler(
        GraphicsDevice device, DescriptorSet set, uint binding, GpuImage image, Sampler sampler)
    {
        var imageInfo = new DescriptorImageInfo
        {
            Sampler = sampler.Handle,
            ImageView = image.View,
            ImageLayout = ImageLayout.ShaderReadOnlyOptimal,
        };
        var write = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = set,
            DstBinding = binding,
            DescriptorCount = 1,
            DescriptorType = DescriptorType.CombinedImageSampler,
            PImageInfo = &imageInfo,
        };
        device.Api.UpdateDescriptorSets(device.Device, 1, &write, 0, null);
    }
}
