using Agapanthe.Core;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;

namespace Agapanthe.Graphics;

/// <summary>
/// A graphics pipeline for dynamic rendering (no VkRenderPass), built from a
/// <see cref="GraphicsPipelineDesc"/>: vertex input, descriptor set layouts, push constants,
/// depth state and attachment formats. Owns its VkPipeline and VkPipelineLayout; the descriptor
/// set layouts it references are owned by the caller and shared (spec §3.4).
/// </summary>
public sealed unsafe class GraphicsPipeline : IDisposable
{
    private readonly GraphicsDevice _device;
    private Pipeline _pipeline;
    private PipelineLayout _layout;
    private bool _disposed;

    public GraphicsPipeline(GraphicsDevice device, GraphicsPipelineDesc desc)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(desc);
        ArgumentNullException.ThrowIfNull(desc.VertexShader);
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

    ~GraphicsPipeline()
    {
        // Only report when a native handle was actually acquired; ctor argument-validation
        // exceptions reach the finalizer with nothing registered (audit M2, finding 1).
        if (_pipeline.Handle != 0 || _layout.Handle != 0)
        {
            ResourceTracker.ReportFinalizerLeak(nameof(GraphicsPipeline));
        }
    }

    internal Pipeline Handle => _pipeline;
    internal PipelineLayout Layout => _layout;

    /// <summary>
    /// Deferred disposal (default, spec §3.2.1/§3.2.5): the pipeline and its layout are released once the
    /// frame that used them leaves flight, so a pipeline swapped mid-loop (shader hot reload, M8) is never
    /// destroyed while a frame in flight may still reference it. Safe only because pipeline swaps happen at
    /// the frame boundary before recording; N+FramesInFlight then covers every in-flight frame.
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

    private void CreatePipeline(Vk vk, GraphicsPipelineDesc desc)
    {
        var entryPoint = (byte*)SilkMarshal.StringToPtr("main");

        // Vertex input arrays must outlive the create call; allocate them for the whole method.
        var attributeCount = desc.VertexLayout?.Attributes.Count ?? 0;
        var attributes = stackalloc VertexInputAttributeDescription[Math.Max(1, attributeCount)];
        var binding = default(VertexInputBindingDescription);
        try
        {
            // A depth-only pipeline (shadow map) has no fragment shader: only the vertex stage is declared.
            var stages = stackalloc PipelineShaderStageCreateInfo[2];
            stages[0] = new PipelineShaderStageCreateInfo
            {
                SType = StructureType.PipelineShaderStageCreateInfo,
                Stage = ShaderStageFlags.VertexBit,
                Module = desc.VertexShader.Handle,
                PName = entryPoint,
            };
            uint stageCount = 1;
            if (desc.FragmentShader is { } fragment)
            {
                stages[1] = new PipelineShaderStageCreateInfo
                {
                    SType = StructureType.PipelineShaderStageCreateInfo,
                    Stage = ShaderStageFlags.FragmentBit,
                    Module = fragment.Handle,
                    PName = entryPoint,
                };
                stageCount = 2;
            }

            var vertexInput = new PipelineVertexInputStateCreateInfo
            {
                SType = StructureType.PipelineVertexInputStateCreateInfo,
            };
            if (desc.VertexLayout is { } layout)
            {
                binding = new VertexInputBindingDescription
                {
                    Binding = 0,
                    Stride = layout.Stride,
                    InputRate = VertexInputRate.Vertex,
                };
                for (var i = 0; i < layout.Attributes.Count; i++)
                {
                    var attr = layout.Attributes[i];
                    attributes[i] = new VertexInputAttributeDescription
                    {
                        Location = attr.Location,
                        Binding = 0,
                        Format = attr.Format.ToVk(),
                        Offset = attr.Offset,
                    };
                }

                vertexInput.VertexBindingDescriptionCount = 1;
                vertexInput.PVertexBindingDescriptions = &binding;
                vertexInput.VertexAttributeDescriptionCount = (uint)layout.Attributes.Count;
                vertexInput.PVertexAttributeDescriptions = attributes;
            }

            var inputAssembly = new PipelineInputAssemblyStateCreateInfo
            {
                SType = StructureType.PipelineInputAssemblyStateCreateInfo,
                Topology = PrimitiveTopology.TriangleList,
            };
            var viewportState = new PipelineViewportStateCreateInfo
            {
                SType = StructureType.PipelineViewportStateCreateInfo,
                ViewportCount = 1,
                ScissorCount = 1,
            };
            // Depth bias is a static state here (not dynamic). Enabled when either factor is non-zero;
            // slope-scaled bias fights shadow acne without pushing near-flat surfaces off the light.
            var depthBiasEnable = desc.DepthBiasConstant != 0f || desc.DepthBiasSlope != 0f;
            var rasterizer = new PipelineRasterizationStateCreateInfo
            {
                SType = StructureType.PipelineRasterizationStateCreateInfo,
                PolygonMode = PolygonMode.Fill,
                CullMode = ToVkCull(desc.Cull),
                FrontFace = desc.FrontFace == FrontFace.CounterClockwise
                    ? Silk.NET.Vulkan.FrontFace.CounterClockwise
                    : Silk.NET.Vulkan.FrontFace.Clockwise,
                DepthBiasEnable = depthBiasEnable,
                DepthBiasConstantFactor = desc.DepthBiasConstant,
                DepthBiasSlopeFactor = desc.DepthBiasSlope,
                LineWidth = 1f,
            };
            var multisample = new PipelineMultisampleStateCreateInfo
            {
                SType = StructureType.PipelineMultisampleStateCreateInfo,
                RasterizationSamples = SampleCountFlags.Count1Bit,
            };
            var depthStencil = new PipelineDepthStencilStateCreateInfo
            {
                SType = StructureType.PipelineDepthStencilStateCreateInfo,
                DepthTestEnable = desc.DepthTest,
                DepthWriteEnable = desc.DepthTest && desc.DepthWrite,
                DepthCompareOp = CompareOp.LessOrEqual,
            };
            // A depth-only pipeline (no color format) declares zero blend attachments; the color-blend state
            // stays present but empty, which is legal when there are no color attachments.
            var hasColor = desc.ColorFormat != PixelFormat.Undefined;
            var blendAttachment = new PipelineColorBlendAttachmentState
            {
                ColorWriteMask = ColorComponentFlags.RBit | ColorComponentFlags.GBit
                                 | ColorComponentFlags.BBit | ColorComponentFlags.ABit,
                BlendEnable = false,
            };
            var colorBlend = new PipelineColorBlendStateCreateInfo
            {
                SType = StructureType.PipelineColorBlendStateCreateInfo,
                AttachmentCount = hasColor ? 1u : 0u,
                PAttachments = hasColor ? &blendAttachment : null,
            };

            var dynamicStates = stackalloc DynamicState[2] { DynamicState.Viewport, DynamicState.Scissor };
            var dynamicState = new PipelineDynamicStateCreateInfo
            {
                SType = StructureType.PipelineDynamicStateCreateInfo,
                DynamicStateCount = 2,
                PDynamicStates = dynamicStates,
            };

            // Dynamic rendering: attachment formats are declared here instead of in a render pass.
            var colorFormat = desc.ColorFormat.ToVk();
            var depthFormat = desc.DepthFormat.ToVk();
            var renderingInfo = new PipelineRenderingCreateInfo
            {
                SType = StructureType.PipelineRenderingCreateInfo,
                ColorAttachmentCount = hasColor ? 1u : 0u,
                PColorAttachmentFormats = hasColor ? &colorFormat : null,
                DepthAttachmentFormat = desc.DepthFormat == PixelFormat.Undefined ? Format.Undefined : depthFormat,
            };

            var pipelineInfo = new GraphicsPipelineCreateInfo
            {
                SType = StructureType.GraphicsPipelineCreateInfo,
                PNext = &renderingInfo,
                StageCount = stageCount,
                PStages = stages,
                PVertexInputState = &vertexInput,
                PInputAssemblyState = &inputAssembly,
                PViewportState = &viewportState,
                PRasterizationState = &rasterizer,
                PMultisampleState = &multisample,
                PDepthStencilState = &depthStencil,
                PColorBlendState = &colorBlend,
                PDynamicState = &dynamicState,
                Layout = _layout,
            };

            Pipeline pipeline;
            VkCheck.ThrowIfFailed(
                vk.CreateGraphicsPipelines(_device.Device, default, 1, &pipelineInfo, null, &pipeline),
                "vkCreateGraphicsPipelines");
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

    private static CullModeFlags ToVkCull(CullMode cull) => cull switch
    {
        CullMode.None => CullModeFlags.None,
        CullMode.Back => CullModeFlags.BackBit,
        CullMode.Front => CullModeFlags.FrontBit,
        _ => CullModeFlags.None,
    };
}
