using System.Numerics;
using Silk.NET.Core.Contexts;
using Silk.NET.Input;

namespace Agapanthe.App;

/// <summary>
/// The windowing + input surface <see cref="AppHost"/> needs, decoupled from the concrete backend (GLFW/Silk).
/// It is a 1:1 projection of <c>Agapanthe.Platform.EngineWindow</c>'s public surface — the application adapts the
/// concrete window to this interface (<c>Agapanthe.Platform.App.EngineWindowAdapter</c>, shared by every windowed
/// app since Slice-2 — it used to live in <c>samples/Sandbox</c>) so that
/// <see cref="AppHost"/> — and every <see cref="ISceneRecipe"/> — never names a platform type.
/// <para>
/// It deliberately exposes <see cref="Key"/> and the opaque <see cref="IVkSurface"/>: neither is a Vulkan type,
/// both already cross <c>EngineWindow</c>'s boundary today, and a home-grown key enum is the (deferred)
/// action-map milestone's concern, not this one.
/// </para>
/// </summary>
public interface IWindow : IDisposable
{
    /// <summary>Fires once the window and its input context exist — build GPU resources here.</summary>
    event Action? Loaded;

    /// <summary>Per-frame update tick (wall-clock seconds). The place look input is valid:
    /// <see cref="MouseDelta"/> is reset to zero right after this fires.</summary>
    event Action<double>? Updated;

    /// <summary>Per-frame render tick (wall-clock seconds).</summary>
    event Action<double>? Rendered;

    /// <summary>Framebuffer size changed, in pixels (width, height).</summary>
    event Action<int, int>? FramebufferResized;

    /// <summary>Edge-triggered key press — fires once per physical press (unlike <see cref="IsKeyDown"/> polling).</summary>
    event Action<Key>? KeyPressed;

    /// <summary>The OS title-bar text; settable at runtime.</summary>
    string Title { get; set; }

    /// <summary>Framebuffer size in pixels — the size swapchains must use (differs from window size on HiDPI).</summary>
    (int Width, int Height) FramebufferSize { get; }

    /// <summary>Opaque Vulkan surface source; valid between <see cref="Loaded"/> and <see cref="Closing"/>,
    /// null where the platform has no Vulkan support.</summary>
    IVkSurface? VkSurface { get; }

    /// <summary>Mouse motion accumulated this frame, pixels (X right, Y down). Valid inside an
    /// <see cref="Updated"/> handler; zero while the cursor is not captured.</summary>
    Vector2 MouseDelta { get; }

    /// <summary>True while the cursor is captured for FPS-style look (hidden + locked).</summary>
    bool MouseCaptured { get; }

    /// <summary>Convenience keyboard poll; false when no keyboard is present.</summary>
    bool IsKeyDown(Key key);

    /// <summary>Captures or releases the cursor for FPS-style look.</summary>
    void SetMouseCaptured(bool captured);

    /// <summary>Instance extensions the window system requires (VK_KHR_surface + the platform surface).</summary>
    string[] GetRequiredVulkanExtensions();

    /// <summary>Runs the frame loop until the window closes. Blocks the calling thread.</summary>
    void Run();

    /// <summary>Requests the window close, ending <see cref="Run"/>.</summary>
    void Close();
}
