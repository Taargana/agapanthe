using Agapanthe.Core;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;

namespace Agapanthe.Graphics;

/// <summary>
/// A graphics pipeline for dynamic rendering (no VkRenderPass). Milestone M1 targets the
/// hard-coded triangle: no vertex input, an empty pipeline layout, dynamic viewport and
/// scissor, a single color attachment. Later milestones add vertex input and layouts.
/// </summary>
public sealed unsafe class GraphicsPipeline : IDisposable
{
    private readonly GraphicsDevice _device;
    private Pipeline _pipeline;
    private PipelineLayout _layout;
    private bool _disposed;

    public GraphicsPipeline(GraphicsDevice device, ShaderModule vertex, ShaderModule fragment, uint colorFormat)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(vertex);
        ArgumentNullException.ThrowIfNull(fragment);
        _device = device;

        var vk = device.Api;
        CreateLayout(vk);
        try
        {
            CreatePipeline(vk, vertex, fragment, (Format)colorFormat);
        }
        catch
        {
            DestroyResources();
            throw;
        }
    }

    ~GraphicsPipeline() => ResourceTracker.ReportFinalizerLeak(nameof(GraphicsPipeline));

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

    private void CreateLayout(Vk vk)
    {
        var layoutInfo = new PipelineLayoutCreateInfo { SType = StructureType.PipelineLayoutCreateInfo };
        PipelineLayout layout;
        VkCheck.ThrowIfFailed(vk.CreatePipelineLayout(_device.Device, &layoutInfo, null, &layout), "vkCreatePipelineLayout");
        _layout = layout;
        ResourceTracker.Register("VkPipelineLayout");
    }

    private void CreatePipeline(Vk vk, ShaderModule vertex, ShaderModule fragment, Format colorFormat)
    {
        var entryPoint = (byte*)SilkMarshal.StringToPtr("main");
        try
        {
            var stages = stackalloc PipelineShaderStageCreateInfo[2];
            stages[0] = new PipelineShaderStageCreateInfo
            {
                SType = StructureType.PipelineShaderStageCreateInfo,
                Stage = ShaderStageFlags.VertexBit,
                Module = vertex.Handle,
                PName = entryPoint,
            };
            stages[1] = new PipelineShaderStageCreateInfo
            {
                SType = StructureType.PipelineShaderStageCreateInfo,
                Stage = ShaderStageFlags.FragmentBit,
                Module = fragment.Handle,
                PName = entryPoint,
            };

            var vertexInput = new PipelineVertexInputStateCreateInfo
            {
                SType = StructureType.PipelineVertexInputStateCreateInfo,
            };
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
                CullMode = CullModeFlags.None,
                FrontFace = FrontFace.CounterClockwise,
                LineWidth = 1f,
            };
            var multisample = new PipelineMultisampleStateCreateInfo
            {
                SType = StructureType.PipelineMultisampleStateCreateInfo,
                RasterizationSamples = SampleCountFlags.Count1Bit,
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

            // Dynamic rendering: the attachment formats are declared here instead of in a render pass.
            var format = colorFormat;
            var renderingInfo = new PipelineRenderingCreateInfo
            {
                SType = StructureType.PipelineRenderingCreateInfo,
                ColorAttachmentCount = 1,
                PColorAttachmentFormats = &format,
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
}
