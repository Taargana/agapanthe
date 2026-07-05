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
    /// Default clear color (RGBA, linear). Retained for source compatibility: the clear is now applied by the
    /// pass owner via <see cref="CommandList.BeginRendering"/> (the draw callback), which supplies its own
    /// clear color — this value is not consumed by the loop and is superseded in M5-02.
    /// </summary>
    public (float R, float G, float B, float A) ClearColor { get; set; } = (0f, 0f, 0f, 1f);

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

        // The loop owns only the swapchain image's acquire/present layout transitions; the callback opens its
        // own rendering scope (and owns any depth/HDR attachment) against the target between them.
        cmdList.TransitionImage(target.View, ImageLayoutState.Undefined, ImageLayoutState.ColorAttachment);

        record(cmdList, context, target);

        cmdList.TransitionImage(target.View, ImageLayoutState.ColorAttachment, ImageLayoutState.PresentSrc);

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
