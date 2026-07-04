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
    private bool _recenterCapture;
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
            RecenterCursor();
        };
        _window.Render += dt => Rendered?.Invoke(dt);
        _window.FramebufferResize += size => FramebufferResized?.Invoke(size.X, size.Y);
        _window.FocusChanged += OnFocusChanged;
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

    /// <summary>Edge-triggered key press (fires once per physical press, unlike <see cref="IsKeyDown"/> polling).</summary>
    public event Action<Key>? KeyPressed;

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
    /// Always zero while the cursor is not captured — look input is strictly correlated
    /// to an active capture, never to stray OS mouse events.
    /// </summary>
    public Vector2 MouseDelta => _mouseCaptured ? _mouseDelta : Vector2.Zero;

    /// <summary>True while the cursor is captured for FPS-style look (hidden + locked).</summary>
    public bool MouseCaptured => _mouseCaptured;

    /// <summary>
    /// When true (default), clicking inside the window captures the cursor. Release stays
    /// explicit (<see cref="SetMouseCaptured"/>) or automatic on focus loss.
    /// </summary>
    public bool CaptureMouseOnClick { get; set; } = true;

    /// <summary>Convenience keyboard poll; false when no keyboard is present.</summary>
    public bool IsKeyDown(Key key) => _keyboard?.IsKeyPressed(key) ?? false;

    /// <summary>
    /// Captures or releases the cursor. Capture prefers <see cref="CursorMode.Raw"/>
    /// (unaccelerated, unbounded FPS look — Windows/Linux). Where raw motion is not
    /// supported (e.g. macOS) it hides the cursor and re-centers it every update tick
    /// instead: deltas are measured against the window center, so the cursor can never
    /// pin against a screen edge and stall the look. Release restores the normal cursor.
    /// </summary>
    public void SetMouseCaptured(bool captured)
    {
        _mouseCaptured = captured;

        if (_mouse is null)
        {
            return;
        }

        var cursor = _mouse.Cursor;
        if (captured)
        {
            if (cursor.IsSupported(CursorMode.Raw))
            {
                cursor.CursorMode = CursorMode.Raw;
                _recenterCapture = false;
            }
            else
            {
                // CursorMode.Disabled is not trustworthy everywhere (macOS keeps a real,
                // screen-clamped cursor), so use the deterministic classic: hide + warp.
                cursor.CursorMode = CursorMode.Hidden;
                _recenterCapture = true;
            }
        }
        else
        {
            cursor.CursorMode = CursorMode.Normal;
            _recenterCapture = false;
        }

        // Drop the last position so a re-capture doesn't emit one huge delta jump.
        _hasLastMousePosition = false;
        _mouseDelta = Vector2.Zero;
        RecenterCursor();
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

        if (_keyboard is not null)
        {
            _keyboard.KeyDown += OnKeyDown;
        }

        if (_mouse is not null)
        {
            _mouse.MouseMove += OnMouseMove;
            _mouse.MouseDown += OnMouseDown;
        }

        // No capture here: grabbing the cursor before the window has focus is unreliable
        // (notably GLFW on macOS) and hostile UX. Capture happens on the first click.
        Loaded?.Invoke();
    }

    private void OnKeyDown(IKeyboard keyboard, Key key, int scancode) => KeyPressed?.Invoke(key);

    private void OnMouseDown(IMouse mouse, MouseButton button)
    {
        // The click that lands inside the window proves both focus and intent — the only
        // portable moment to grab the cursor (works the same under GLFW win32/x11/cocoa).
        if (CaptureMouseOnClick && !_mouseCaptured)
        {
            SetMouseCaptured(true);
        }
    }

    private void OnFocusChanged(bool focused)
    {
        // Losing focus always releases the capture: background windows must never keep
        // steering the camera. Regaining focus does NOT recapture — that takes a click.
        if (!focused && _mouseCaptured)
        {
            SetMouseCaptured(false);
        }
    }

    private void OnMouseMove(IMouse mouse, Vector2 position)
    {
        // Deltas only accumulate while captured; positions are still tracked so the first
        // captured move has a valid reference and produces no jump.
        if (_hasLastMousePosition && _mouseCaptured)
        {
            _mouseDelta += position - _lastMousePosition;
        }

        _lastMousePosition = position;
        _hasLastMousePosition = true;
    }

    /// <summary>
    /// Recenter-capture mode only: warps the (hidden) cursor back to the window center after
    /// each update tick, so it always has the full window to move in before the next one.
    /// Resetting the reference position to the center keeps the synthetic warp event from
    /// counting as look motion.
    /// </summary>
    private void RecenterCursor()
    {
        if (!_mouseCaptured || !_recenterCapture || _mouse is null)
        {
            return;
        }

        var center = new Vector2(_window.Size.X / 2f, _window.Size.Y / 2f);
        _mouse.Position = center;
        _lastMousePosition = center;
        _hasLastMousePosition = true;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_keyboard is not null)
        {
            _keyboard.KeyDown -= OnKeyDown;
        }

        if (_mouse is not null)
        {
            _mouse.MouseMove -= OnMouseMove;
            _mouse.MouseDown -= OnMouseDown;
        }

        _input?.Dispose();
        _window.Dispose();
        ResourceTracker.Unregister("EngineWindow");
        GC.SuppressFinalize(this);
    }

    ~EngineWindow() => ResourceTracker.ReportFinalizerLeak("EngineWindow");
}
