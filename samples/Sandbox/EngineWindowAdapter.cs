using System.Numerics;
using Agapanthe.App;
using Agapanthe.Platform;
using Silk.NET.Core.Contexts;
using Silk.NET.Input;

namespace Sandbox;

/// <summary>
/// Adapts the concrete <see cref="EngineWindow"/> (GLFW/Silk, in <c>Agapanthe.Platform</c>) to
/// <see cref="IWindow"/> (in <c>Agapanthe.App</c>). It lives here — not in either project — because it is the one
/// place both are referenced; this keeps <c>Agapanthe.App</c> free of a <c>Platform</c> reference, so
/// <c>Platform</c> stays a Vulkan-free leaf. Pure forwarding, no behaviour.
/// </summary>
internal sealed class EngineWindowAdapter(EngineWindow inner) : IWindow
{
    public event Action? Loaded
    {
        add => inner.Loaded += value;
        remove => inner.Loaded -= value;
    }

    public event Action<double>? Updated
    {
        add => inner.Updated += value;
        remove => inner.Updated -= value;
    }

    public event Action<double>? Rendered
    {
        add => inner.Rendered += value;
        remove => inner.Rendered -= value;
    }

    public event Action<int, int>? FramebufferResized
    {
        add => inner.FramebufferResized += value;
        remove => inner.FramebufferResized -= value;
    }

    public event Action<Key>? KeyPressed
    {
        add => inner.KeyPressed += value;
        remove => inner.KeyPressed -= value;
    }

    public string Title
    {
        get => inner.Title;
        set => inner.Title = value;
    }

    public (int Width, int Height) FramebufferSize => inner.FramebufferSize;

    public IVkSurface? VkSurface => inner.VkSurface;

    public Vector2 MouseDelta => inner.MouseDelta;

    public bool MouseCaptured => inner.MouseCaptured;

    public bool IsKeyDown(Key key) => inner.IsKeyDown(key);

    public void SetMouseCaptured(bool captured) => inner.SetMouseCaptured(captured);

    public string[] GetRequiredVulkanExtensions() => inner.GetRequiredVulkanExtensions();

    public void Run() => inner.Run();

    public void Close() => inner.Close();

    public void Dispose() => inner.Dispose();
}
