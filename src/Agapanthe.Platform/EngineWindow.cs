using System.Numerics;
using Agapanthe.Core;
using Silk.NET.Core.Contexts;
using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.Windowing;

namespace Agapanthe.Platform;

/// <summary>
/// Engine window wrapping Silk.NET windowing (GLFW). Owns the OS window, the input
/// context and the frame loop. Exposes the Vulkan surface source as an opaque
/// <see cref="IVkSurface"/> so this project never references Vulkan itself.
/// </summary>
/// <remarks>
/// Input boundary: the Silk.NET.Input types (<see cref="IInputContext"/>,
/// <see cref="IKeyboard"/>, <see cref="IMouse"/>, <see cref="Key"/>) surface here and
/// must not leak into rendering code. Consumers translate them into their own input
/// snapshots (e.g. <c>Agapanthe.Rendering.CameraInput</c>) at this seam.
/// </remarks>
public sealed class EngineWindow : IDisposable
{
    private readonly IWindow _window;
    private IInputContext? _input;
    private IKeyboard? _keyboard;
    private IMouse? _mouse;
    private Vector2 _mouseDelta;
    private Vector2 _lastMousePosition;
    private bool _hasLastMousePosition;
    private bool _mouseCaptured;
    private bool _disposed;

    public EngineWindow(string title, int width, int height)
    {
        var options = WindowOptions.DefaultVulkan with
        {
            Title = title,
            Size = new Vector2D<int>(width, height),
        };

        _window = Window.Create(options);
        _window.Load += OnLoad;
        _window.Update += dt =>
        {
            Updated?.Invoke(dt);
            // Reset after subscribers consumed this frame's motion; the next batch of
            // MouseMove events (pumped before the following Update) starts from zero.
            _mouseDelta = Vector2.Zero;
        };
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

    /// <summary>
    /// Raw Silk.NET input context. Valid between <see cref="Loaded"/> and disposal.
    /// Prefer <see cref="Keyboard"/>/<see cref="Mouse"/>/<see cref="MouseDelta"/>; this is
    /// exposed only for cases those don't cover. Do not reference it outside Platform.
    /// </summary>
    public IInputContext? Input => _input;

    /// <summary>Primary keyboard, or null if none is connected.</summary>
    public IKeyboard? Keyboard => _keyboard;

    /// <summary>Primary mouse, or null if none is connected.</summary>
    public IMouse? Mouse => _mouse;

    /// <summary>
    /// Mouse motion accumulated this frame, in pixels (X right, Y down). Valid to read
    /// inside an <see cref="Updated"/> handler; reset to zero after each update tick.
    /// </summary>
    public Vector2 MouseDelta => _mouseDelta;

    /// <summary>True while the cursor is captured for FPS-style look (hidden + locked).</summary>
    public bool MouseCaptured => _mouseCaptured;

    /// <summary>Convenience keyboard poll; false when no keyboard is present.</summary>
    public bool IsKeyDown(Key key) => _keyboard?.IsKeyPressed(key) ?? false;

    /// <summary>
    /// Captures or releases the cursor. Capture prefers <see cref="CursorMode.Raw"/>
    /// (unaccelerated FPS look) and falls back to <see cref="CursorMode.Disabled"/> when
    /// the platform can't do raw motion (e.g. macOS). Release restores the normal cursor.
    /// </summary>
    public void SetMouseCaptured(bool captured)
    {
        _mouseCaptured = captured;

        if (_mouse is null)
        {
            return;
        }

        var cursor = _mouse.Cursor;
        cursor.CursorMode = captured
            ? (cursor.IsSupported(CursorMode.Raw) ? CursorMode.Raw : CursorMode.Disabled)
            : CursorMode.Normal;

        // Drop the last position so a re-capture doesn't emit one huge delta jump.
        _hasLastMousePosition = false;
        _mouseDelta = Vector2.Zero;
    }

    /// <summary>Instance extensions the window system requires (VK_KHR_surface + platform surface).</summary>
    public unsafe string[] GetRequiredVulkanExtensions()
    {
        var surface = _window.VkSurface
            ?? throw new InvalidOperationException("Window has no Vulkan surface; call after Loaded.");

        var namePtrs = surface.GetRequiredExtensions(out var count);
        var names = new string[count];
        for (var i = 0; i < count; i++)
        {
            names[i] = Silk.NET.Core.Native.SilkMarshal.PtrToString((nint)namePtrs[i]) ?? string.Empty;
        }

        return names;
    }

    /// <summary>Runs the frame loop until the window closes. Blocks the calling thread.</summary>
    public void Run() => _window.Run();

    public void Close() => _window.Close();

    private void OnLoad()
    {
        // Create input before firing Loaded so subscribers see a live input context.
        _input = _window.CreateInput();
        _keyboard = _input.Keyboards.Count > 0 ? _input.Keyboards[0] : null;
        _mouse = _input.Mice.Count > 0 ? _input.Mice[0] : null;

        if (_mouse is not null)
        {
            _mouse.MouseMove += OnMouseMove;
            SetMouseCaptured(true);
        }

        Loaded?.Invoke();
    }

    private void OnMouseMove(IMouse mouse, Vector2 position)
    {
        if (_hasLastMousePosition)
        {
            _mouseDelta += position - _lastMousePosition;
        }

        _lastMousePosition = position;
        _hasLastMousePosition = true;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_mouse is not null)
        {
            _mouse.MouseMove -= OnMouseMove;
        }

        _input?.Dispose();
        _window.Dispose();
        ResourceTracker.Unregister("EngineWindow");
        GC.SuppressFinalize(this);
    }

    ~EngineWindow() => ResourceTracker.ReportFinalizerLeak("EngineWindow");
}
