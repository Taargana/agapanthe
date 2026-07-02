using Agapanthe.Core;
using Silk.NET.Core.Contexts;
using Silk.NET.Maths;
using Silk.NET.Windowing;

namespace Agapanthe.Platform;

/// <summary>
/// Engine window wrapping Silk.NET windowing (GLFW). Owns the OS window and the
/// frame loop. Exposes the Vulkan surface source as an opaque <see cref="IVkSurface"/>
/// so this project never references Vulkan itself.
/// </summary>
public sealed class EngineWindow : IDisposable
{
    private readonly IWindow _window;
    private bool _disposed;

    public EngineWindow(string title, int width, int height)
    {
        var options = WindowOptions.DefaultVulkan with
        {
            Title = title,
            Size = new Vector2D<int>(width, height),
        };

        _window = Window.Create(options);
        _window.Load += () => Loaded?.Invoke();
        _window.Update += dt => Updated?.Invoke(dt);
        _window.Render += dt => Rendered?.Invoke(dt);
        _window.FramebufferResize += size => FramebufferResized?.Invoke(size.X, size.Y);
        _window.Closing += () => Closing?.Invoke();

        ResourceTracker.Register("EngineWindow");
    }

    /// <summary>Fires once the window and its input context exist. Create GPU resources here.</summary>
    public event Action? Loaded;

    public event Action<double>? Updated;

    public event Action<double>? Rendered;

    /// <summary>Framebuffer size in pixels (differs from window size on HiDPI displays).</summary>
    public event Action<int, int>? FramebufferResized;

    public event Action? Closing;

    /// <summary>Framebuffer size in pixels, the size swapchains must use.</summary>
    public (int Width, int Height) FramebufferSize
    {
        get
        {
            var size = _window.FramebufferSize;
            return (size.X, size.Y);
        }
    }

    /// <summary>
    /// Opaque Vulkan surface source. Only valid between <see cref="Loaded"/> and
    /// <see cref="Closing"/>. Null when the platform has no Vulkan support.
    /// </summary>
    public IVkSurface? VkSurface => _window.VkSurface;

    /// <summary>Runs the frame loop until the window closes. Blocks the calling thread.</summary>
    public void Run() => _window.Run();

    public void Close() => _window.Close();

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _window.Dispose();
        ResourceTracker.Unregister("EngineWindow");
        GC.SuppressFinalize(this);
    }

    ~EngineWindow() => ResourceTracker.ReportFinalizerLeak("EngineWindow");
}
