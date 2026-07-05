using Agapanthe.Core;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;

namespace Agapanthe.Graphics;

/// <summary>
/// Everything needed to build a compute pipeline, decoupled from Vulkan types (mirror of
/// <see cref="GraphicsPipelineDesc"/> for the compute bind point). A compute pipeline is a single
/// shader stage plus a pipeline layout (descriptor set layouts + push constants); it has no vertex
/// input, rasterizer or attachment state. Used at load time to run IBL generation kernels (M7-04).
/// </summary>
public sealed class ComputePipelineDesc
{
    public required ShaderModule ComputeShader { get; init; }

    /// <summary>Descriptor set layouts, in set-index order (set 0 first). Owned by the caller and shared.</summary>
    public IReadOnlyList<DescriptorSetLayout> SetLayouts { get; init; } = [];

    public IReadOnlyList<PushConstantRange> PushConstants { get; init; } = [];
}

/// <summary>
/// A compute pipeline for load-time GPU work (IBL generation, spec §3.6). Owns its VkPipeline and
/// VkPipelineLayout; the descriptor set layouts it references are owned by the caller and shared
/// (same ownership contract as <see cref="GraphicsPipeline"/>).
/// </summary>
public sealed unsafe class ComputePipeline : IDisposable
{
    private readonly GraphicsDevice _device;
    private Pipeline _pipeline;
    private PipelineLayout _layout;
    private bool _disposed;

    public ComputePipeline(GraphicsDevice device, ComputePipelineDesc desc)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(desc);
        ArgumentNullException.ThrowIfNull(desc.ComputeShader);
        _device = device;

        var vk = device.Api;
        try
        {
            CreateLayout(vk, desc);
            CreatePipeline(vk, desc);
        }
        catch
        {
            DestroyResources();
            GC.SuppressFinalize(this);
            throw;
        }
    }

    ~ComputePipeline()
    {
        // Only report when a native handle was actually acquired; ctor argument-validation
        // exceptions reach the finalizer with nothing registered (audit M2, finding 1).
        if (_pipeline.Handle != 0 || _layout.Handle != 0)
        {
            ResourceTracker.ReportFinalizerLeak(nameof(ComputePipeline));
        }
    }

    internal Pipeline Handle => _pipeline;
    internal PipelineLayout Layout => _layout;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        DestroyResources();
        GC.SuppressFinalize(this);
    }

    // Deliberately duplicated from GraphicsPipeline.CreateLayout: that method is private and
    // GraphicsPipeline.cs is owner-locked for this task (M7-02, parallel agent constraints), so the layout
    // build is mirrored here rather than refactored into a shared helper. Extracting a shared
    // PipelineLayoutBuilder is a candidate for a later cleanup pass (noted in the M7-02 hand-off).
    private void CreateLayout(Vk vk, ComputePipelineDesc desc)
    {
        var setLayouts = stackalloc Silk.NET.Vulkan.DescriptorSetLayout[Math.Max(1, desc.SetLayouts.Count)];
        for (var i = 0; i < desc.SetLayouts.Count; i++)
        {
            setLayouts[i] = desc.SetLayouts[i].Handle;
        }

        var pushRanges = stackalloc Silk.NET.Vulkan.PushConstantRange[Math.Max(1, desc.PushConstants.Count)];
        for (var i = 0; i < desc.PushConstants.Count; i++)
        {
            var range = desc.PushConstants[i];
            pushRanges[i] = new Silk.NET.Vulkan.PushConstantRange
            {
                Offset = range.Offset,
                Size = range.Size,
                StageFlags = DescriptorSetLayout.ToVkStages(range.Stages),
            };
        }

        var layoutInfo = new PipelineLayoutCreateInfo
        {
            SType = StructureType.PipelineLayoutCreateInfo,
            SetLayoutCount = (uint)desc.SetLayouts.Count,
            PSetLayouts = desc.SetLayouts.Count > 0 ? setLayouts : null,
            PushConstantRangeCount = (uint)desc.PushConstants.Count,
            PPushConstantRanges = desc.PushConstants.Count > 0 ? pushRanges : null,
        };
        PipelineLayout layout;
        VkCheck.ThrowIfFailed(vk.CreatePipelineLayout(_device.Device, &layoutInfo, null, &layout), "vkCreatePipelineLayout");
        _layout = layout;
        ResourceTracker.Register("VkPipelineLayout");
    }

    private void CreatePipeline(Vk vk, ComputePipelineDesc desc)
    {
        var entryPoint = (byte*)SilkMarshal.StringToPtr("main");
        try
        {
            var stage = new PipelineShaderStageCreateInfo
            {
                SType = StructureType.PipelineShaderStageCreateInfo,
                Stage = ShaderStageFlags.ComputeBit,
                Module = desc.ComputeShader.Handle,
                PName = entryPoint,
            };

            var pipelineInfo = new ComputePipelineCreateInfo
            {
                SType = StructureType.ComputePipelineCreateInfo,
                Stage = stage,
                Layout = _layout,
            };

            Pipeline pipeline;
            VkCheck.ThrowIfFailed(
                vk.CreateComputePipelines(_device.Device, default, 1, &pipelineInfo, null, &pipeline),
                "vkCreateComputePipelines");
            _pipeline = pipeline;
            ResourceTracker.Register("VkPipeline");
        }
        finally
        {
            SilkMarshal.Free((nint)entryPoint);
        }
    }

    private void DestroyResources()
    {
        var vk = _device.Api;
        if (_pipeline.Handle != 0)
        {
            vk.DestroyPipeline(_device.Device, _pipeline, null);
            _pipeline = default;
            ResourceTracker.Unregister("VkPipeline");
        }

        if (_layout.Handle != 0)
        {
            vk.DestroyPipelineLayout(_device.Device, _layout, null);
            _layout = default;
            ResourceTracker.Unregister("VkPipelineLayout");
        }
    }
}
