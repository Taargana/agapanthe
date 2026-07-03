using Agapanthe.Core;
using Silk.NET.Vulkan;

namespace Agapanthe.Graphics;

/// <summary>
/// A device-local image with its own memory and a full-subresource view. M2 uses it for the
/// depth attachment; color/texture images follow in M3. Immediate (non-deferred) disposal is
/// used for swapchain-sized attachments, which are only recreated after vkDeviceWaitIdle.
/// </summary>
public sealed unsafe class GpuImage : IDisposable
{
    private readonly GraphicsDevice _device;
    private Image _image;
    private DeviceMemory _memory;
    private ImageView _view;
    private bool _disposed;

    public GpuImage(GraphicsDevice device, uint width, uint height, PixelFormat format, ImagePurpose purpose)
    {
        ArgumentNullException.ThrowIfNull(device);
        _device = device;
        Format = format;
        var vk = device.Api;

        var isDepth = purpose == ImagePurpose.DepthAttachment;
        var usage = isDepth ? ImageUsageFlags.DepthStencilAttachmentBit : ImageUsageFlags.ColorAttachmentBit;
        var aspect = isDepth ? ImageAspectFlags.DepthBit : ImageAspectFlags.ColorBit;

        try
        {
            var imageInfo = new ImageCreateInfo
            {
                SType = StructureType.ImageCreateInfo,
                ImageType = ImageType.Type2D,
                Format = format.ToVk(),
                Extent = new Extent3D(width, height, 1),
                MipLevels = 1,
                ArrayLayers = 1,
                Samples = SampleCountFlags.Count1Bit,
                Tiling = ImageTiling.Optimal,
                Usage = usage,
                SharingMode = SharingMode.Exclusive,
                InitialLayout = ImageLayout.Undefined,
            };
            Image image;
            VkCheck.ThrowIfFailed(vk.CreateImage(device.Device, &imageInfo, null, &image), "vkCreateImage");
            _image = image;
            ResourceTracker.Register("VkImage");

            vk.GetImageMemoryRequirements(device.Device, _image, out var requirements);
            var allocInfo = new MemoryAllocateInfo
            {
                SType = StructureType.MemoryAllocateInfo,
                AllocationSize = requirements.Size,
                MemoryTypeIndex = device.FindMemoryType(requirements.MemoryTypeBits, MemoryPropertyFlags.DeviceLocalBit),
            };
            DeviceMemory memory;
            VkCheck.ThrowIfFailed(vk.AllocateMemory(device.Device, &allocInfo, null, &memory), "vkAllocateMemory");
            _memory = memory;
            ResourceTracker.Register("VkDeviceMemory");
            VkCheck.ThrowIfFailed(vk.BindImageMemory(device.Device, _image, _memory, 0), "vkBindImageMemory");

            var viewInfo = new ImageViewCreateInfo
            {
                SType = StructureType.ImageViewCreateInfo,
                Image = _image,
                ViewType = ImageViewType.Type2D,
                Format = format.ToVk(),
                SubresourceRange = new ImageSubresourceRange(aspect, 0, 1, 0, 1),
            };
            ImageView view;
            VkCheck.ThrowIfFailed(vk.CreateImageView(device.Device, &viewInfo, null, &view), "vkCreateImageView");
            _view = view;
            ResourceTracker.Register("VkImageView");
        }
        catch
        {
            DestroyNow();
            GC.SuppressFinalize(this);
            throw;
        }
    }

    ~GpuImage()
    {
        // Only report when a native handle was actually acquired; ctor argument-validation
        // exceptions reach the finalizer with nothing registered (audit M2, finding 1).
        if (_image.Handle != 0 || _memory.Handle != 0 || _view.Handle != 0)
        {
            ResourceTracker.ReportFinalizerLeak(nameof(GpuImage));
        }
    }

    public PixelFormat Format { get; }

    internal Image Handle => _image;
    internal ImageView View => _view;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        DestroyNow();
        GC.SuppressFinalize(this);
    }

    private void DestroyNow()
    {
        var vk = _device.Api;
        if (_view.Handle != 0)
        {
            vk.DestroyImageView(_device.Device, _view, null);
            _view = default;
            ResourceTracker.Unregister("VkImageView");
        }

        if (_image.Handle != 0)
        {
            vk.DestroyImage(_device.Device, _image, null);
            _image = default;
            ResourceTracker.Unregister("VkImage");
        }

        if (_memory.Handle != 0)
        {
            vk.FreeMemory(_device.Device, _memory, null);
            _memory = default;
            ResourceTracker.Unregister("VkDeviceMemory");
        }
    }
}

/// <summary>What a <see cref="GpuImage"/> is used for (drives usage flags and view aspect).</summary>
public enum ImagePurpose
{
    ColorAttachment,
    DepthAttachment,
}
