using Agapanthe.Core;
using Silk.NET.Vulkan;
using Semaphore = Silk.NET.Vulkan.Semaphore;

namespace Agapanthe.Graphics;

/// <summary>
/// Presentation swapchain: images, views and the per-image render-finished
/// semaphores (one per swapchain image, not per frame, to respect WSI semaphore
/// reuse rules — spec §3.3). Resize/OUT_OF_DATE is handled by full recreation
/// after vkDeviceWaitIdle, so destruction here is immediate, not deferred.
/// </summary>
public sealed unsafe class Swapchain : IDisposable
{
    private readonly GraphicsDevice _device;
    private SwapchainKHR _swapchain;
    private Image[] _images = [];
    private ImageView[] _imageViews = [];
    private Semaphore[] _renderFinishedSemaphores = [];
    private bool _disposed;

    public Swapchain(GraphicsDevice device, int framebufferWidth, int framebufferHeight)
    {
        ArgumentNullException.ThrowIfNull(device);
        _device = device;
        try
        {
            Create((uint)Math.Max(1, framebufferWidth), (uint)Math.Max(1, framebufferHeight));
        }
        catch
        {
            DestroyResources();
            GC.SuppressFinalize(this);
            throw;
        }
    }

    ~Swapchain()
    {
        ResourceTracker.ReportFinalizerLeak(nameof(Swapchain));
    }

    public int ImageCount => _images.Length;
    public uint Width => Extent.Width;
    public uint Height => Extent.Height;

    /// <summary>Color attachment format as a raw VkFormat value (for pipeline creation).</summary>
    public uint ColorFormat => (uint)ImageFormat;

    internal SwapchainKHR Handle => _swapchain;
    internal Format ImageFormat { get; private set; }
    internal Extent2D Extent { get; private set; }
    internal ReadOnlySpan<Image> Images => _images;
    internal ReadOnlySpan<ImageView> ImageViews => _imageViews;
    internal Semaphore RenderFinishedSemaphore(uint imageIndex) => _renderFinishedSemaphores[imageIndex];

    /// <summary>
    /// Acquires the next image, signaling <paramref name="imageAvailable"/>. Returns false
    /// when the swapchain is out of date and must be recreated before rendering.
    /// SUBOPTIMAL still delivers a usable image; the present path reports the recreate.
    /// </summary>
    internal bool TryAcquireNextImage(Semaphore imageAvailable, out uint imageIndex)
    {
        uint index = 0;
        var result = _device.KhrSwapchain.AcquireNextImage(
            _device.Device, _swapchain, ulong.MaxValue, imageAvailable, default, &index);
        imageIndex = index;

        if (result == Result.ErrorOutOfDateKhr)
        {
            return false;
        }

        if (result != Result.SuboptimalKhr)
        {
            VkCheck.ThrowIfFailed(result, "vkAcquireNextImageKHR");
        }

        return true;
    }

    /// <summary>
    /// Presents <paramref name="imageIndex"/>, waiting on that image's render-finished
    /// semaphore. Returns false when the swapchain must be recreated (OUT_OF_DATE/SUBOPTIMAL).
    /// </summary>
    internal bool Present(uint imageIndex)
    {
        var waitSemaphore = _renderFinishedSemaphores[imageIndex];
        var swapchain = _swapchain;
        var presentInfo = new PresentInfoKHR
        {
            SType = StructureType.PresentInfoKhr,
            WaitSemaphoreCount = 1,
            PWaitSemaphores = &waitSemaphore,
            SwapchainCount = 1,
            PSwapchains = &swapchain,
            PImageIndices = &imageIndex,
        };

        var result = _device.KhrSwapchain.QueuePresent(_device.PresentQueue, &presentInfo);
        if (result is Result.ErrorOutOfDateKhr or Result.SuboptimalKhr)
        {
            return false;
        }

        VkCheck.ThrowIfFailed(result, "vkQueuePresentKHR");
        return true;
    }

    /// <summary>Full recreation after vkDeviceWaitIdle (spec §3.3).</summary>
    public void Recreate(int framebufferWidth, int framebufferHeight)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _device.WaitIdle();
        DestroyResources();
        Create((uint)Math.Max(1, framebufferWidth), (uint)Math.Max(1, framebufferHeight));
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

    private void Create(uint framebufferWidth, uint framebufferHeight)
    {
        var vk = _device.Api;

        SurfaceCapabilitiesKHR caps;
        VkCheck.ThrowIfFailed(
            _device.KhrSurface.GetPhysicalDeviceSurfaceCapabilities(_device.PhysicalDevice, _device.Surface, &caps),
            "vkGetPhysicalDeviceSurfaceCapabilitiesKHR");

        var surfaceFormat = ChooseSurfaceFormat();

        // 0xFFFFFFFF means the surface size is driven by the swapchain; clamp the
        // framebuffer size into the supported range in that case.
        var extent = caps.CurrentExtent.Width != uint.MaxValue
            ? caps.CurrentExtent
            : new Extent2D(
                Math.Clamp(framebufferWidth, caps.MinImageExtent.Width, caps.MaxImageExtent.Width),
                Math.Clamp(framebufferHeight, caps.MinImageExtent.Height, caps.MaxImageExtent.Height));

        var minImageCount = caps.MinImageCount + 1;
        if (caps.MaxImageCount > 0 && minImageCount > caps.MaxImageCount)
        {
            minImageCount = caps.MaxImageCount;
        }

        var concurrent = _device.GraphicsQueueFamily != _device.PresentQueueFamily;
        var queueFamilies = stackalloc uint[2] { _device.GraphicsQueueFamily, _device.PresentQueueFamily };

        var createInfo = new SwapchainCreateInfoKHR
        {
            SType = StructureType.SwapchainCreateInfoKhr,
            Surface = _device.Surface,
            MinImageCount = minImageCount,
            ImageFormat = surfaceFormat.Format,
            ImageColorSpace = surfaceFormat.ColorSpace,
            ImageExtent = extent,
            ImageArrayLayers = 1,
            ImageUsage = ImageUsageFlags.ColorAttachmentBit,
            ImageSharingMode = concurrent ? SharingMode.Concurrent : SharingMode.Exclusive,
            QueueFamilyIndexCount = concurrent ? 2u : 0u,
            PQueueFamilyIndices = concurrent ? queueFamilies : null,
            PreTransform = caps.CurrentTransform,
            CompositeAlpha = ChooseCompositeAlpha(caps.SupportedCompositeAlpha),
            PresentMode = PresentModeKHR.FifoKhr, // Always available; Mailbox is a deferred option (spec §3.3).
            Clipped = true,
            OldSwapchain = default,
        };

        SwapchainKHR swapchain;
        VkCheck.ThrowIfFailed(
            _device.KhrSwapchain.CreateSwapchain(_device.Device, &createInfo, null, &swapchain),
            "vkCreateSwapchainKHR");
        _swapchain = swapchain;
        ResourceTracker.Register("VkSwapchain");
        ImageFormat = surfaceFormat.Format;
        Extent = extent;

        uint imageCount = 0;
        VkCheck.ThrowIfFailed(
            _device.KhrSwapchain.GetSwapchainImages(_device.Device, _swapchain, &imageCount, null),
            "vkGetSwapchainImagesKHR");
        _images = new Image[imageCount];
        fixed (Image* p = _images)
        {
            VkCheck.ThrowIfFailed(
                _device.KhrSwapchain.GetSwapchainImages(_device.Device, _swapchain, &imageCount, p),
                "vkGetSwapchainImagesKHR");
        }

        _imageViews = new ImageView[imageCount];
        for (var i = 0; i < _images.Length; i++)
        {
            var viewInfo = new ImageViewCreateInfo
            {
                SType = StructureType.ImageViewCreateInfo,
                Image = _images[i],
                ViewType = ImageViewType.Type2D,
                Format = surfaceFormat.Format,
                SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1),
            };
            ImageView view;
            VkCheck.ThrowIfFailed(vk.CreateImageView(_device.Device, &viewInfo, null, &view), "vkCreateImageView");
            _imageViews[i] = view;
            ResourceTracker.Register("VkImageView");
        }

        _renderFinishedSemaphores = new Semaphore[imageCount];
        var semaphoreInfo = new SemaphoreCreateInfo { SType = StructureType.SemaphoreCreateInfo };
        for (var i = 0; i < _renderFinishedSemaphores.Length; i++)
        {
            Semaphore semaphore;
            VkCheck.ThrowIfFailed(vk.CreateSemaphore(_device.Device, &semaphoreInfo, null, &semaphore), "vkCreateSemaphore");
            _renderFinishedSemaphores[i] = semaphore;
            ResourceTracker.Register("VkSemaphore");
        }

        Log.Debug($"Swapchain: {extent.Width}x{extent.Height}, {imageCount} images, format {surfaceFormat.Format}.");
    }

    private SurfaceFormatKHR ChooseSurfaceFormat()
    {
        uint count = 0;
        VkCheck.ThrowIfFailed(
            _device.KhrSurface.GetPhysicalDeviceSurfaceFormats(_device.PhysicalDevice, _device.Surface, &count, null),
            "vkGetPhysicalDeviceSurfaceFormatsKHR");
        if (count == 0)
        {
            throw new GraphicsException("Surface reports no supported formats.");
        }

        var formats = new SurfaceFormatKHR[count];
        fixed (SurfaceFormatKHR* p = formats)
        {
            VkCheck.ThrowIfFailed(
                _device.KhrSurface.GetPhysicalDeviceSurfaceFormats(_device.PhysicalDevice, _device.Surface, &count, p),
                "vkGetPhysicalDeviceSurfaceFormatsKHR");
        }

        foreach (var format in formats)
        {
            if (format is { Format: Format.B8G8R8A8Srgb, ColorSpace: ColorSpaceKHR.SpaceSrgbNonlinearKhr })
            {
                return format;
            }
        }

        Log.Warn($"Swapchain: no B8G8R8A8_SRGB surface format; falling back to {formats[0].Format}.");
        return formats[0];
    }

    private static CompositeAlphaFlagsKHR ChooseCompositeAlpha(CompositeAlphaFlagsKHR supported)
    {
        if ((supported & CompositeAlphaFlagsKHR.OpaqueBitKhr) != 0)
        {
            return CompositeAlphaFlagsKHR.OpaqueBitKhr;
        }

        // At least one bit is always set; take the lowest supported one.
        var value = (uint)supported;
        return (CompositeAlphaFlagsKHR)(value & unchecked((uint)-(int)value));
    }

    private void DestroyResources()
    {
        var vk = _device.Api;

        foreach (var view in _imageViews)
        {
            if (view.Handle != 0)
            {
                vk.DestroyImageView(_device.Device, view, null);
                ResourceTracker.Unregister("VkImageView");
            }
        }

        _imageViews = [];

        foreach (var semaphore in _renderFinishedSemaphores)
        {
            if (semaphore.Handle != 0)
            {
                vk.DestroySemaphore(_device.Device, semaphore, null);
                ResourceTracker.Unregister("VkSemaphore");
            }
        }

        _renderFinishedSemaphores = [];
        _images = [];

        if (_swapchain.Handle != 0)
        {
            _device.KhrSwapchain.DestroySwapchain(_device.Device, _swapchain, null);
            _swapchain = default;
            ResourceTracker.Unregister("VkSwapchain");
        }
    }
}
