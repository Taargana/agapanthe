using Agapanthe.Core;
using Silk.NET.Vulkan;
using Semaphore = Silk.NET.Vulkan.Semaphore;

namespace Agapanthe.Graphics;

/// <summary>
/// Drives the render loop for a swapchain: acquires an image, records a command buffer,
/// submits with synchronization2 and presents. Owns FramesInFlight worth of command
/// buffers, in-flight fences and image-available semaphores. Render-finished semaphores
/// are per swapchain image and owned by the <see cref="Swapchain"/> (spec §3.3).
/// </summary>
public sealed unsafe class FrameRenderer : IDisposable
{
    private readonly GraphicsDevice _device;
    private readonly Swapchain _swapchain;
    private readonly Func<(int Width, int Height)> _framebufferSizeProvider;

    private CommandPool _commandPool;
    private readonly CommandBuffer[] _commandBuffers = new CommandBuffer[GraphicsDevice.FramesInFlight];
    private readonly Fence[] _inFlightFences = new Fence[GraphicsDevice.FramesInFlight];
    private readonly Semaphore[] _imageAvailableSemaphores = new Semaphore[GraphicsDevice.FramesInFlight];
    private readonly FrameContext?[] _frameContexts = new FrameContext?[GraphicsDevice.FramesInFlight];

    private GpuImage? _depthImage;
    private Extent2D _depthExtent;
    private int _frameSlot;
    private bool _resizeRequested;
    private bool _disposed;

    /// <summary>Depth attachment format used by the loop; pipelines must declare the same.</summary>
    public const PixelFormat DepthFormat = PixelFormat.D32Sfloat;

    public FrameRenderer(GraphicsDevice device, Swapchain swapchain, Func<(int Width, int Height)> framebufferSizeProvider)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(swapchain);
        ArgumentNullException.ThrowIfNull(framebufferSizeProvider);
        _device = device;
        _swapchain = swapchain;
        _framebufferSizeProvider = framebufferSizeProvider;

        try
        {
            CreateResources();
        }
        catch
        {
            DestroyResources();
            GC.SuppressFinalize(this);
            throw;
        }
    }

    ~FrameRenderer()
    {
        // Only report when a native handle was actually acquired; ctor argument-validation
        // exceptions reach the finalizer with nothing registered (audit M2, finding 1).
        if (_commandPool.Handle != 0)
        {
            ResourceTracker.ReportFinalizerLeak(nameof(FrameRenderer));
        }
    }

    /// <summary>Clear color applied at the start of the color attachment (RGBA, linear).</summary>
    public (float R, float G, float B, float A) ClearColor { get; set; } = (0f, 0f, 0f, 1f);

    /// <summary>
    /// Records and presents one frame. <paramref name="record"/> issues draw commands into
    /// the active dynamic-rendering scope. A frame is silently skipped when the swapchain is
    /// out of date (it is recreated first, on the next call it renders).
    /// </summary>
    public void DrawFrame(Action<CommandList, FrameContext> record)
    {
        ArgumentNullException.ThrowIfNull(record);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_resizeRequested)
        {
            _resizeRequested = false;
            RecreateSwapchain();
            return;
        }

        var vk = _device.Api;
        var fence = _inFlightFences[_frameSlot];
        fixed (Fence* pFence = &_inFlightFences[_frameSlot])
        {
            VkCheck.ThrowIfFailed(vk.WaitForFences(_device.Device, 1, pFence, true, ulong.MaxValue), "vkWaitForFences");
        }

        var imageAvailable = _imageAvailableSemaphores[_frameSlot];
        if (!_swapchain.TryAcquireNextImage(imageAvailable, out var imageIndex))
        {
            RecreateSwapchain();
            return;
        }

        // Reset the fence only once we know we will submit work that signals it, else a
        // skipped frame would leave it unsignaled and deadlock the next wait.
        fixed (Fence* pFence = &_inFlightFences[_frameSlot])
        {
            VkCheck.ThrowIfFailed(vk.ResetFences(_device.Device, 1, pFence), "vkResetFences");
        }

        // The just-waited fence guarantees the frame that used this slot (CurrentFrameIndex -
        // FramesInFlight) is complete, so its deferred destroys and descriptor sets are now
        // safe to release.
        _device.DeletionQueue.Flush(_device.CurrentFrameIndex);
        var context = _frameContexts[_frameSlot]!;
        context.Reset();

        var cmd = _commandBuffers[_frameSlot];
        VkCheck.ThrowIfFailed(vk.ResetCommandBuffer(cmd, CommandBufferResetFlags.None), "vkResetCommandBuffer");
        RecordCommandBuffer(cmd, imageIndex, record, context);

        SubmitAndPresent(cmd, imageIndex, imageAvailable);

        _frameSlot = (_frameSlot + 1) % GraphicsDevice.FramesInFlight;
        _device.AdvanceFrame();
    }

    /// <summary>Waits for the GPU to idle. Call before tearing down resources the loop used.</summary>
    public void WaitIdle() => _device.WaitIdle();

    /// <summary>
    /// Requests swapchain + depth recreation on the next frame. Call from the window's resize
    /// event: some platforms (MoltenVK) don't report OUT_OF_DATE on resize.
    /// </summary>
    public void RequestResize() => _resizeRequested = true;

    private void RecordCommandBuffer(CommandBuffer cmd, uint imageIndex, Action<CommandList, FrameContext> record, FrameContext context)
    {
        var vk = _device.Api;
        var beginInfo = new CommandBufferBeginInfo
        {
            SType = StructureType.CommandBufferBeginInfo,
            Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
        };
        VkCheck.ThrowIfFailed(vk.BeginCommandBuffer(cmd, &beginInfo), "vkBeginCommandBuffer");

        var image = _swapchain.Images[(int)imageIndex];
        var view = _swapchain.ImageViews[(int)imageIndex];

        TransitionImage(
            cmd, image, ImageAspectFlags.ColorBit,
            oldLayout: ImageLayout.Undefined,
            newLayout: ImageLayout.ColorAttachmentOptimal,
            srcStage: PipelineStageFlags2.TopOfPipeBit, srcAccess: 0,
            dstStage: PipelineStageFlags2.ColorAttachmentOutputBit,
            dstAccess: AccessFlags2.ColorAttachmentWriteBit);

        // loadOp=Clear makes the prior depth contents irrelevant, so a fresh Undefined->attachment
        // transition each frame is correct and cheaper than preserving the layout.
        TransitionImage(
            cmd, _depthImage!.Handle, ImageAspectFlags.DepthBit,
            oldLayout: ImageLayout.Undefined,
            newLayout: ImageLayout.DepthAttachmentOptimal,
            srcStage: PipelineStageFlags2.EarlyFragmentTestsBit | PipelineStageFlags2.LateFragmentTestsBit,
            srcAccess: 0,
            dstStage: PipelineStageFlags2.EarlyFragmentTestsBit | PipelineStageFlags2.LateFragmentTestsBit,
            dstAccess: AccessFlags2.DepthStencilAttachmentWriteBit);

        BeginRenderingAndDraw(cmd, view, record, context);

        TransitionImage(
            cmd, image, ImageAspectFlags.ColorBit,
            oldLayout: ImageLayout.ColorAttachmentOptimal,
            newLayout: ImageLayout.PresentSrcKhr,
            srcStage: PipelineStageFlags2.ColorAttachmentOutputBit,
            srcAccess: AccessFlags2.ColorAttachmentWriteBit,
            dstStage: PipelineStageFlags2.BottomOfPipeBit, dstAccess: 0);

        VkCheck.ThrowIfFailed(vk.EndCommandBuffer(cmd), "vkEndCommandBuffer");
    }

    private void BeginRenderingAndDraw(CommandBuffer cmd, ImageView view, Action<CommandList, FrameContext> record, FrameContext context)
    {
        var vk = _device.Api;
        var extent = _swapchain.Extent;
        var (r, g, b, a) = ClearColor;
        var clear = new ClearValue { Color = new ClearColorValue(r, g, b, a) };

        var colorAttachment = new RenderingAttachmentInfo
        {
            SType = StructureType.RenderingAttachmentInfo,
            ImageView = view,
            ImageLayout = ImageLayout.ColorAttachmentOptimal,
            LoadOp = AttachmentLoadOp.Clear,
            StoreOp = AttachmentStoreOp.Store,
            ClearValue = clear,
        };
        var depthAttachment = new RenderingAttachmentInfo
        {
            SType = StructureType.RenderingAttachmentInfo,
            ImageView = _depthImage!.View,
            ImageLayout = ImageLayout.DepthAttachmentOptimal,
            LoadOp = AttachmentLoadOp.Clear,
            StoreOp = AttachmentStoreOp.DontCare,
            ClearValue = new ClearValue { DepthStencil = new ClearDepthStencilValue(1f, 0) },
        };
        var renderingInfo = new RenderingInfo
        {
            SType = StructureType.RenderingInfo,
            RenderArea = new Rect2D(default, extent),
            LayerCount = 1,
            ColorAttachmentCount = 1,
            PColorAttachments = &colorAttachment,
            PDepthAttachment = &depthAttachment,
        };

        _device.CmdBeginRendering(cmd, &renderingInfo);

        var viewport = new Viewport { X = 0, Y = 0, Width = extent.Width, Height = extent.Height, MinDepth = 0, MaxDepth = 1 };
        var scissor = new Rect2D(default, extent);
        vk.CmdSetViewport(cmd, 0, 1, &viewport);
        vk.CmdSetScissor(cmd, 0, 1, &scissor);

        record(new CommandList(_device, cmd), context);

        _device.CmdEndRendering(cmd);
    }

    private void TransitionImage(
        CommandBuffer cmd, Image image, ImageAspectFlags aspect, ImageLayout oldLayout, ImageLayout newLayout,
        PipelineStageFlags2 srcStage, AccessFlags2 srcAccess,
        PipelineStageFlags2 dstStage, AccessFlags2 dstAccess)
    {
        var barrier = new ImageMemoryBarrier2
        {
            SType = StructureType.ImageMemoryBarrier2,
            SrcStageMask = srcStage,
            SrcAccessMask = srcAccess,
            DstStageMask = dstStage,
            DstAccessMask = dstAccess,
            OldLayout = oldLayout,
            NewLayout = newLayout,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Image = image,
            SubresourceRange = new ImageSubresourceRange(aspect, 0, 1, 0, 1),
        };
        var dependency = new DependencyInfo
        {
            SType = StructureType.DependencyInfo,
            ImageMemoryBarrierCount = 1,
            PImageMemoryBarriers = &barrier,
        };
        _device.CmdPipelineBarrier2(cmd, &dependency);
    }

    private void SubmitAndPresent(CommandBuffer cmd, uint imageIndex, Semaphore imageAvailable)
    {
        var renderFinished = _swapchain.RenderFinishedSemaphore(imageIndex);

        var waitInfo = new SemaphoreSubmitInfo
        {
            SType = StructureType.SemaphoreSubmitInfo,
            Semaphore = imageAvailable,
            StageMask = PipelineStageFlags2.ColorAttachmentOutputBit,
        };
        var signalInfo = new SemaphoreSubmitInfo
        {
            SType = StructureType.SemaphoreSubmitInfo,
            Semaphore = renderFinished,
            StageMask = PipelineStageFlags2.ColorAttachmentOutputBit,
        };
        var cmdInfo = new CommandBufferSubmitInfo
        {
            SType = StructureType.CommandBufferSubmitInfo,
            CommandBuffer = cmd,
        };
        var submit = new SubmitInfo2
        {
            SType = StructureType.SubmitInfo2,
            WaitSemaphoreInfoCount = 1,
            PWaitSemaphoreInfos = &waitInfo,
            CommandBufferInfoCount = 1,
            PCommandBufferInfos = &cmdInfo,
            SignalSemaphoreInfoCount = 1,
            PSignalSemaphoreInfos = &signalInfo,
        };

        _device.QueueSubmit2(_device.GraphicsQueue, &submit, _inFlightFences[_frameSlot]);

        if (!_swapchain.Present(imageIndex))
        {
            RecreateSwapchain();
        }
    }

    private void RecreateSwapchain()
    {
        var (width, height) = _framebufferSizeProvider();
        // A minimized window reports a zero-size framebuffer; nothing to render until it returns.
        if (width == 0 || height == 0)
        {
            return;
        }

        _swapchain.Recreate(width, height);
        EnsureDepthImage();
    }

    /// <summary>(Re)creates the depth image to match the swapchain extent. Safe to call only
    /// when the device is idle (initial creation, or right after Swapchain.Recreate's WaitIdle).</summary>
    private void EnsureDepthImage()
    {
        var extent = _swapchain.Extent;
        if (_depthImage is not null && _depthExtent.Width == extent.Width && _depthExtent.Height == extent.Height)
        {
            return;
        }

        // Swapchain-sized: only ever (re)created behind a device wait, so destroy synchronously
        // rather than deferring through the DeletionQueue.
        _depthImage?.DestroyImmediately();
        _depthImage = new GpuImage(_device, extent.Width, extent.Height, DepthFormat, ImageUsage.DepthAttachment);
        _depthExtent = extent;
    }

    private void CreateResources()
    {
        var vk = _device.Api;

        var poolInfo = new CommandPoolCreateInfo
        {
            SType = StructureType.CommandPoolCreateInfo,
            Flags = CommandPoolCreateFlags.ResetCommandBufferBit,
            QueueFamilyIndex = _device.GraphicsQueueFamily,
        };
        CommandPool pool;
        VkCheck.ThrowIfFailed(vk.CreateCommandPool(_device.Device, &poolInfo, null, &pool), "vkCreateCommandPool");
        _commandPool = pool;
        ResourceTracker.Register("VkCommandPool");

        var allocInfo = new CommandBufferAllocateInfo
        {
            SType = StructureType.CommandBufferAllocateInfo,
            CommandPool = _commandPool,
            Level = CommandBufferLevel.Primary,
            CommandBufferCount = GraphicsDevice.FramesInFlight,
        };
        fixed (CommandBuffer* p = _commandBuffers)
        {
            VkCheck.ThrowIfFailed(vk.AllocateCommandBuffers(_device.Device, &allocInfo, p), "vkAllocateCommandBuffers");
        }

        // Fences start signaled so the first WaitForFences of each slot passes immediately.
        var fenceInfo = new FenceCreateInfo { SType = StructureType.FenceCreateInfo, Flags = FenceCreateFlags.SignaledBit };
        var semaphoreInfo = new SemaphoreCreateInfo { SType = StructureType.SemaphoreCreateInfo };
        for (var i = 0; i < GraphicsDevice.FramesInFlight; i++)
        {
            Fence fence;
            VkCheck.ThrowIfFailed(vk.CreateFence(_device.Device, &fenceInfo, null, &fence), "vkCreateFence");
            _inFlightFences[i] = fence;
            ResourceTracker.Register("VkFence");

            Semaphore semaphore;
            VkCheck.ThrowIfFailed(vk.CreateSemaphore(_device.Device, &semaphoreInfo, null, &semaphore), "vkCreateSemaphore");
            _imageAvailableSemaphores[i] = semaphore;
            ResourceTracker.Register("VkSemaphore");

            _frameContexts[i] = new FrameContext(_device, i);
        }

        EnsureDepthImage();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _device.WaitIdle();
        DestroyResources();
        GC.SuppressFinalize(this);
    }

    private void DestroyResources()
    {
        var vk = _device.Api;
        // Reached only after _device.WaitIdle() (Dispose) or a failed ctor; immediate destroy is safe.
        _depthImage?.DestroyImmediately();
        _depthImage = null;
        for (var i = 0; i < _frameContexts.Length; i++)
        {
            _frameContexts[i]?.Dispose();
            _frameContexts[i] = null;
        }
        for (var i = 0; i < GraphicsDevice.FramesInFlight; i++)
        {
            if (_inFlightFences[i].Handle != 0)
            {
                vk.DestroyFence(_device.Device, _inFlightFences[i], null);
                _inFlightFences[i] = default;
                ResourceTracker.Unregister("VkFence");
            }

            if (_imageAvailableSemaphores[i].Handle != 0)
            {
                vk.DestroySemaphore(_device.Device, _imageAvailableSemaphores[i], null);
                _imageAvailableSemaphores[i] = default;
                ResourceTracker.Unregister("VkSemaphore");
            }
        }

        if (_commandPool.Handle != 0)
        {
            // Frees the allocated command buffers with the pool.
            vk.DestroyCommandPool(_device.Device, _commandPool, null);
            _commandPool = default;
            ResourceTracker.Unregister("VkCommandPool");
        }
    }
}
