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
        ArgumentNullException.ThrowIfNull(desc.FragmentShader);
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

    private void CreateLayout(Vk vk, GraphicsPipelineDesc desc)
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

    private void CreatePipeline(Vk vk, GraphicsPipelineDesc desc)
    {
        var entryPoint = (byte*)SilkMarshal.StringToPtr("main");

        // Vertex input arrays must outlive the create call; allocate them for the whole method.
        var attributeCount = desc.VertexLayout?.Attributes.Count ?? 0;
        var attributes = stackalloc VertexInputAttributeDescription[Math.Max(1, attributeCount)];
        var binding = default(VertexInputBindingDescription);
        try
        {
            var stages = stackalloc PipelineShaderStageCreateInfo[2];
            stages[0] = new PipelineShaderStageCreateInfo
            {
                SType = StructureType.PipelineShaderStageCreateInfo,
                Stage = ShaderStageFlags.VertexBit,
                Module = desc.VertexShader.Handle,
                PName = entryPoint,
            };
            stages[1] = new PipelineShaderStageCreateInfo
            {
                SType = StructureType.PipelineShaderStageCreateInfo,
                Stage = ShaderStageFlags.FragmentBit,
                Module = desc.FragmentShader.Handle,
                PName = entryPoint,
            };

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
            var rasterizer = new PipelineRasterizationStateCreateInfo
            {
                SType = StructureType.PipelineRasterizationStateCreateInfo,
                PolygonMode = PolygonMode.Fill,
                CullMode = ToVkCull(desc.Cull),
                FrontFace = desc.FrontFace == FrontFace.CounterClockwise
                    ? Silk.NET.Vulkan.FrontFace.CounterClockwise
                    : Silk.NET.Vulkan.FrontFace.Clockwise,
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
            var blendAttachment = new PipelineColorBlendAttachmentState
            {
                ColorWriteMask = ColorComponentFlags.RBit | ColorComponentFlags.GBit
                                 | ColorComponentFlags.BBit | ColorComponentFlags.ABit,
                BlendEnable = false,
            };
            var colorBlend = new PipelineColorBlendStateCreateInfo
            {
                SType = StructureType.PipelineColorBlendStateCreateInfo,
                AttachmentCount = 1,
                PAttachments = &blendAttachment,
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
                ColorAttachmentCount = 1,
                PColorAttachmentFormats = &colorFormat,
                DepthAttachmentFormat = desc.DepthFormat == PixelFormat.Undefined ? Format.Undefined : depthFormat,
            };

            var pipelineInfo = new GraphicsPipelineCreateInfo
            {
                SType = StructureType.GraphicsPipelineCreateInfo,
                PNext = &renderingInfo,
                StageCount = 2,
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
