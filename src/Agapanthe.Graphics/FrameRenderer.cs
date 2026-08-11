using Agapanthe.Core;
using Silk.NET.Vulkan;
using Semaphore = Silk.NET.Vulkan.Semaphore;

namespace Agapanthe.Graphics;

/// <summary>
/// Drives frame synchronization for a swapchain: acquires an image, records a command buffer,
/// submits with synchronization2 and presents. Owns FramesInFlight worth of command buffers,
/// in-flight fences and image-available semaphores. Render-finished semaphores are per swapchain
/// image and owned by the <see cref="Swapchain"/> (spec §3.3).
/// <para>
/// It no longer owns any render pass or attachment: the draw callback receives a <see cref="SwapchainTarget"/>
/// and opens its own <see cref="CommandList.BeginRendering"/> scope (and owns any depth/HDR target). The loop
/// wraps the callback only in the Undefined→ColorAttachment and ColorAttachment→PresentSrc transitions of the
/// acquired swapchain image (spec §3.3, M5 multi-pass composition).
/// </para>
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

    private int _frameSlot;
    private bool _resizeRequested;

    // Debug capture of the presented image (UI-1). The owned destination is created on demand and only exists in a
    // run that actually asks for a capture.
    private GpuImage? _captureImage;
    private bool _captureRequested;
    private bool _captureReady;
    private bool _disposed;

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

    /// <summary>
    /// Records and presents one frame. The loop transitions the acquired swapchain image to
    /// ColorAttachment, invokes <paramref name="record"/> (which opens its own rendering scope against the
    /// <see cref="SwapchainTarget"/>), then transitions to PresentSrc. A frame is silently skipped when the
    /// swapchain is out of date (it is recreated first, on the next call it renders).
    /// </summary>
    public void DrawFrame(Action<CommandList, FrameContext, SwapchainTarget> record)
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

    /// <summary>
    /// Extent of the swapchain image the last recorded frame drew into (UI-1) — what a debug capture needs to
    /// interpret the bytes <see cref="ReadCapture"/> hands back.
    /// <para>
    /// Deliberately the EXTENT only, not the <c>SwapchainTarget</c>: that would publish an image view and image
    /// handle which are destroyed on the next swapchain recreation, inviting a use-after-free from a caller that
    /// held on to it.
    /// </para>
    /// </summary>
    public (uint Width, uint Height) LastPresentedExtent { get; private set; }

    /// <summary>
    /// Asks the NEXT recorded frame to snapshot the presented image (UI-1) — what the user actually sees, overlays
    /// included, which the HDR capture structurally cannot show (it reads the scene target, drawn before the tonemap
    /// and before every overlay).
    /// <para>
    /// One-shot: draw one more frame, then <see cref="WaitIdle"/> and call <see cref="ReadCapture"/>. Debug only —
    /// it adds a full-image copy to that frame.
    /// </para>
    /// </summary>
    /// <returns><c>false</c> when the surface does not advertise <c>TRANSFER_SRC</c>, so no capture is possible.</returns>
    public bool RequestCapture()
    {
        if (!_swapchain.CanCapture)
        {
            return false;
        }

        // Only the flag: the destination is created (or resized) by the frame that records the copy, where the
        // extent in force is known for certain.
        _captureRequested = true;
        _captureReady = false;
        return true;
    }

    /// <summary>
    /// Reads back the snapshot taken by the last <see cref="RequestCapture"/>, as tightly packed 4-byte texels in
    /// the swapchain's own format (already sRGB-encoded — no tonemap, no gamma to apply). Call with the GPU idle.
    /// </summary>
    /// <returns><c>null</c> when no capture has completed since the last request.</returns>
    public byte[]? ReadCapture()
    {
        if (!_captureReady || _captureImage is null)
        {
            return null;
        }

        return GpuReadback.ReadImage(_device, _captureImage, ImageLayoutState.TransferSrc, bytesPerTexel: 4);
    }

    /// <summary>Waits for the GPU to idle. Call before tearing down resources the loop used.</summary>
    public void WaitIdle() => _device.WaitIdle();

    /// <summary>
    /// Requests swapchain recreation on the next frame. Call from the window's resize event: some platforms
    /// (MoltenVK) don't report OUT_OF_DATE on resize. Attachments owned by the draw callback (depth/HDR) are
    /// recreated by that owner when it observes the new <see cref="SwapchainTarget"/> extent.
    /// </summary>
    public void RequestResize() => _resizeRequested = true;

    private void RecordCommandBuffer(CommandBuffer cmd, uint imageIndex, Action<CommandList, FrameContext, SwapchainTarget> record, FrameContext context)
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
        var extent = _swapchain.Extent;

        var cmdList = new CommandList(_device, cmd);
        var target = new SwapchainTarget(
            new RenderTargetView(view, image, ImageAspectFlags.ColorBit), extent.Width, extent.Height);
        // Remembered so a post-frame debug capture can read back the image that was actually presented (UI-1).
        // It stays valid until the swapchain is recreated, and a capture always runs on an idle GPU.
        LastPresentedExtent = (extent.Width, extent.Height);

        // The loop owns only the swapchain image's acquire/present layout transitions; the callback opens its
        // own rendering scope (and owns any depth/HDR attachment) against the target between them.
        cmdList.TransitionImage(target.View, ImageLayoutState.Undefined, ImageLayoutState.ColorAttachment);

        record(cmdList, context, target);

        // Debug capture of the PRESENTED image (UI-1). The snapshot has to be taken HERE, inside the frame, because
        // a presentable image may not be touched again once vkQueuePresentKHR has released it — reading it back
        // afterwards trips a validation error. So the image is copied into an owned one while it is still acquired,
        // and that copy is read at leisure by ReadCapture after the GPU goes idle.
        // The destination is sized HERE, from the extent this frame actually uses — not in RequestCapture. A resize
        // can land in between (DrawFrame bails out early on an out-of-date swapchain, leaving the request pending),
        // and copying the new extent into an image sized for the old one is out of bounds: a validation error, on a
        // debug path, which this project treats as a bug like any other.
        if (_captureRequested)
        {
            if (_captureImage is null || _captureImage.Width != extent.Width || _captureImage.Height != extent.Height)
            {
                _captureImage?.Dispose();
                // Sampled is unused by the capture path, but GpuImage always creates an image view and a view is
                // only legal on an image whose usage includes one of Sampled/Storage/*Attachment — transfer usages
                // alone are not enough (VUID-VkImageViewCreateInfo-image-04441).
                _captureImage = new GpuImage(
                    _device, extent.Width, extent.Height, _swapchain.ColorFormat,
                    ImageUsage.TransferDst | ImageUsage.TransferSrc | ImageUsage.Sampled, mipLevels: 1);
            }
        }

        if (_captureRequested && _captureImage is not null)
        {
            cmdList.TransitionImage(target.View, ImageLayoutState.ColorAttachment, ImageLayoutState.TransferSrc);
            cmdList.TransitionImage(_captureImage, ImageLayoutState.Undefined, ImageLayoutState.TransferDst);
            cmdList.CopyColorImage(target.View.Image, _captureImage.Handle, extent.Width, extent.Height);
            cmdList.TransitionImage(_captureImage, ImageLayoutState.TransferDst, ImageLayoutState.TransferSrc);
            cmdList.TransitionImage(target.View, ImageLayoutState.TransferSrc, ImageLayoutState.PresentSrc);
            _captureRequested = false;
            _captureReady = true;
        }
        else
        {
            cmdList.TransitionImage(target.View, ImageLayoutState.ColorAttachment, ImageLayoutState.PresentSrc);
        }

        VkCheck.ThrowIfFailed(vk.EndCommandBuffer(cmd), "vkEndCommandBuffer");
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
            // ALL_COMMANDS, not COLOR_ATTACHMENT_OUTPUT. The last thing the command buffer does is transition the
            // image to PresentSrc, and that barrier completes at BOTTOM_OF_PIPE — i.e. AFTER colour output. Signaling
            // at COLOR_ATTACHMENT_OUTPUT therefore released the present before the transition had finished, which
            // synchronization validation reports as PRESENT_AFTER_WRITE. The signal has to cover every stage the
            // command buffer touched (UI-2; found the moment sync validation was switched on).
            StageMask = PipelineStageFlags2.AllCommandsBit,
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

        // Debug capture destination (UI-1): null in any run that never asked for a capture.
        _captureImage?.Dispose();
        _captureImage = null;

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
