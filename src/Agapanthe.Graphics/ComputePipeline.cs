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
            _layout = PipelineLayoutBuilder.Create(_device, desc.SetLayouts, desc.PushConstants);
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

    /// <summary>
    /// Deferred disposal (default, spec §3.2.1/§3.2.5): the pipeline and its layout are released once the
    /// frame that used them leaves flight. Safe only because pipeline swaps happen at the frame boundary
    /// before recording; N+FramesInFlight then covers every in-flight frame.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // Non-capturing deferred destroy (zero managed allocation): the two raw handles travel by value in
        // the payload (Handle0 = VkPipeline, Handle1 = VkPipelineLayout) and the destructor is a cached
        // static delegate.
        var payload = new DeletionPayload(_pipeline.Handle, _layout.Handle);
        _pipeline = default;
        _layout = default;
        _device.EnqueueDestroy(DestroyDelegate, in payload);
        GC.SuppressFinalize(this);
    }

    // Allocated once per type: passing this reference on the deferred path costs no allocation.
    private static readonly Action<GraphicsDevice, DeletionPayload> DestroyDelegate = DestroyDeferred;

    // Deferred destructor for the non-capturing DeletionQueue path (Dispose): rebuilds the handles from the
    // value-type payload and destroys pipeline then layout. Runs after the frame leaves flight.
    private static void DestroyDeferred(GraphicsDevice device, DeletionPayload payload)
    {
        var vk = device.Api;
        var pipeline = new Pipeline(payload.Handle0);
        if (pipeline.Handle != 0)
        {
            vk.DestroyPipeline(device.Device, pipeline, null);
            ResourceTracker.Unregister("VkPipeline");
        }

        var layout = new PipelineLayout(payload.Handle1);
        if (layout.Handle != 0)
        {
            vk.DestroyPipelineLayout(device.Device, layout, null);
            ResourceTracker.Unregister("VkPipelineLayout");
        }
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
