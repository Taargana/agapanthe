using Agapanthe.Core;
using Silk.NET.Vulkan;

namespace Agapanthe.Graphics;

/// <summary>
/// A descriptor set layout, created once and shared by every pipeline and descriptor set that
/// uses that binding signature (spec §3.4). Held by the caller for the app lifetime and disposed
/// at shutdown. A by-signature cache is deferred; a single shared instance satisfies "created once".
/// </summary>
public sealed unsafe class DescriptorSetLayout : IDisposable
{
    private readonly GraphicsDevice _device;
    private Silk.NET.Vulkan.DescriptorSetLayout _handle;
    private bool _disposed;

    public DescriptorSetLayout(GraphicsDevice device, ReadOnlySpan<DescriptorBinding> bindings)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (bindings.Length == 0)
        {
            throw new GraphicsException("A descriptor set layout needs at least one binding.");
        }

        _device = device;
        var vkBindings = stackalloc DescriptorSetLayoutBinding[bindings.Length];
        for (var i = 0; i < bindings.Length; i++)
        {
            vkBindings[i] = new DescriptorSetLayoutBinding
            {
                Binding = bindings[i].Binding,
                DescriptorType = ToVkType(bindings[i].Kind),
                DescriptorCount = 1,
                StageFlags = ToVkStages(bindings[i].Stages),
            };
        }

        var createInfo = new DescriptorSetLayoutCreateInfo
        {
            SType = StructureType.DescriptorSetLayoutCreateInfo,
            BindingCount = (uint)bindings.Length,
            PBindings = vkBindings,
        };
        Silk.NET.Vulkan.DescriptorSetLayout handle;
        VkCheck.ThrowIfFailed(
            device.Api.CreateDescriptorSetLayout(device.Device, &createInfo, null, &handle),
            "vkCreateDescriptorSetLayout");
        _handle = handle;
        ResourceTracker.Register("VkDescriptorSetLayout");
    }

    ~DescriptorSetLayout() => ResourceTracker.ReportFinalizerLeak(nameof(DescriptorSetLayout));

    internal Silk.NET.Vulkan.DescriptorSetLayout Handle => _handle;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _device.Api.DestroyDescriptorSetLayout(_device.Device, _handle, null);
        _handle = default;
        ResourceTracker.Unregister("VkDescriptorSetLayout");
        GC.SuppressFinalize(this);
    }

    internal static DescriptorType ToVkType(DescriptorKind kind) => kind switch
    {
        DescriptorKind.UniformBuffer => DescriptorType.UniformBuffer,
        DescriptorKind.CombinedImageSampler => DescriptorType.CombinedImageSampler,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown descriptor kind."),
    };

    internal static ShaderStageFlags ToVkStages(ShaderStages stages)
    {
        var flags = ShaderStageFlags.None;
        if ((stages & ShaderStages.Vertex) != 0)
        {
            flags |= ShaderStageFlags.VertexBit;
        }

        if ((stages & ShaderStages.Fragment) != 0)
        {
            flags |= ShaderStageFlags.FragmentBit;
        }

        if ((stages & ShaderStages.Compute) != 0)
        {
            flags |= ShaderStageFlags.ComputeBit;
        }

        return flags;
    }
}
