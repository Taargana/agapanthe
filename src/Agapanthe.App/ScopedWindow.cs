using System.Numerics;
using Silk.NET.Core.Contexts;
using Silk.NET.Input;

namespace Agapanthe.App;

/// <summary>
/// Scene management spec, D11: wraps the real <see cref="IWindow"/> for one scene's lifetime, tracking every
/// event subscription so <see cref="Dispose"/> can undo all of them at once — the structural fix for a scene's
/// stale handler (e.g. a key binding, a free-fly look subscription) firing against a disposed <see cref="World.
/// GameWorld"/> after a switch. Makes the missing-unsubscribe bug structurally unreachable rather than a discipline
/// every future subscriber (including an external package) must remember: the five existing subscription call
/// sites (<c>SceneInput.EnableFreeFly</c>, <c>RecipeInput</c>, <c>LandingChallengeSystemFactory</c>,
/// <c>DriveControlSystemFactory</c>, <c>ThinClientSceneRecipe</c>) already write <c>p.Window.Updated += handler</c>
/// and need zero changes — they now go through this wrapper transparently.
/// </summary>
public sealed class ScopedWindow : IWindow
{
    private readonly IWindow _inner;
    private readonly Dictionary<Delegate, Delegate> _loadedTrampolines = new();
    private readonly Dictionary<Delegate, Delegate> _updatedTrampolines = new();
    private readonly Dictionary<Delegate, Delegate> _renderedTrampolines = new();
    private readonly Dictionary<Delegate, Delegate> _framebufferResizedTrampolines = new();
    private readonly Dictionary<Delegate, Delegate> _keyPressedTrampolines = new();
    private readonly Dictionary<Delegate, Delegate> _charInputTrampolines = new();
    private readonly List<Action> _cleanups = new();
    private bool _disposed;

    public ScopedWindow(IWindow inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
    }

    public event Action? Loaded
    {
        add
        {
            if (value is null)
            {
                return;
            }

            Action trampoline = () =>
            {
                if (!_disposed)
                {
                    value();
                }
            };
            _loadedTrampolines[value] = trampoline;
            _inner.Loaded += trampoline;
        }
        remove
        {
            if (value is not null && _loadedTrampolines.Remove(value, out var trampoline))
            {
                _inner.Loaded -= (Action)trampoline;
            }
        }
    }

    public event Action<double>? Updated
    {
        add
        {
            if (value is null)
            {
                return;
            }

            Action<double> trampoline = dt =>
            {
                if (!_disposed)
                {
                    value(dt);
                }
            };
            _updatedTrampolines[value] = trampoline;
            _inner.Updated += trampoline;
        }
        remove
        {
            if (value is not null && _updatedTrampolines.Remove(value, out var trampoline))
            {
                _inner.Updated -= (Action<double>)trampoline;
            }
        }
    }

    public event Action<double>? Rendered
    {
        add
        {
            if (value is null)
            {
                return;
            }

            Action<double> trampoline = dt =>
            {
                if (!_disposed)
                {
                    value(dt);
                }
            };
            _renderedTrampolines[value] = trampoline;
            _inner.Rendered += trampoline;
        }
        remove
        {
            if (value is not null && _renderedTrampolines.Remove(value, out var trampoline))
            {
                _inner.Rendered -= (Action<double>)trampoline;
            }
        }
    }

    public event Action<int, int>? FramebufferResized
    {
        add
        {
            if (value is null)
            {
                return;
            }

            Action<int, int> trampoline = (w, h) =>
            {
                if (!_disposed)
                {
                    value(w, h);
                }
            };
            _framebufferResizedTrampolines[value] = trampoline;
            _inner.FramebufferResized += trampoline;
        }
        remove
        {
            if (value is not null && _framebufferResizedTrampolines.Remove(value, out var trampoline))
            {
                _inner.FramebufferResized -= (Action<int, int>)trampoline;
            }
        }
    }

    public event Action<Key>? KeyPressed
    {
        add
        {
            if (value is null)
            {
                return;
            }

            Action<Key> trampoline = key =>
            {
                if (!_disposed)
                {
                    value(key);
                }
            };
            _keyPressedTrampolines[value] = trampoline;
            _inner.KeyPressed += trampoline;
        }
        remove
        {
            if (value is not null && _keyPressedTrampolines.Remove(value, out var trampoline))
            {
                _inner.KeyPressed -= (Action<Key>)trampoline;
            }
        }
    }

    public event Action<char>? CharInput
    {
        add
        {
            if (value is null)
            {
                return;
            }

            Action<char> trampoline = c =>
            {
                if (!_disposed)
                {
                    value(c);
                }
            };
            _charInputTrampolines[value] = trampoline;
            _inner.CharInput += trampoline;
        }
        remove
        {
            if (value is not null && _charInputTrampolines.Remove(value, out var trampoline))
            {
                _inner.CharInput -= (Action<char>)trampoline;
            }
        }
    }

    public string Title
    {
        get => _inner.Title;
        set => _inner.Title = value;
    }

    public (int Width, int Height) FramebufferSize => _inner.FramebufferSize;

    public IVkSurface? VkSurface => _inner.VkSurface;

    public Vector2 MouseDelta => _inner.MouseDelta;

    public bool MouseCaptured => _inner.MouseCaptured;

    public Vector2 MousePosition => _inner.MousePosition;

    public Vector2 ScrollDelta => _inner.ScrollDelta;

    public bool CaptureMouseOnClick
    {
        get => _inner.CaptureMouseOnClick;
        set => _inner.CaptureMouseOnClick = value;
    }

    public bool IsKeyDown(Key key) => _inner.IsKeyDown(key);

    public bool IsMouseButtonDown(MouseButton button) => _inner.IsMouseButtonDown(button);

    public void SetMouseCaptured(bool captured) => _inner.SetMouseCaptured(captured);

    public string[] GetRequiredVulkanExtensions() => _inner.GetRequiredVulkanExtensions();

    /// <summary>Always throws — re-entering the frame loop from inside a scene (which already lives entirely
    /// inside frames the real <see cref="Run"/> is driving) is a genuine misuse, not a case to silently forward
    /// or no-op.</summary>
    public void Run() => throw new NotSupportedException(
        "ScopedWindow.Run is not supported: a scene already lives inside the real window's frame loop.");

    public void Close() => _inner.Close();

    /// <summary>Registers a non-event cleanup (e.g. a recipe's own <c>NetChannel</c>) to run once, in
    /// <see cref="Dispose"/>, after every tracked event subscription has been undone.</summary>
    public void RegisterCleanup(Action cleanup)
    {
        ArgumentNullException.ThrowIfNull(cleanup);
        _cleanups.Add(cleanup);
    }

    /// <summary>The one explicit escape hatch that actually disposes the real, process-lifetime <see
    /// cref="IWindow"/> — never called by any scene-switch path, reserved for the host's own final teardown.
    /// <see cref="Dispose"/> never calls this.</summary>
    public void CloseUnderlyingWindow() => _inner.Dispose();

    /// <summary>
    /// Unsubscribes every tracked handler and runs every registered cleanup. <b>Never forwards to the real
    /// <see cref="IWindow"/>'s own <see cref="IDisposable.Dispose"/></b> — a naive full member-forward would let a
    /// scene kill the real, process-lifetime window. <c>_disposed</c> is set as the very first statement, before
    /// any actual unsubscribe: a .NET event's invocation list is snapshotted at the moment it is invoked, so an
    /// already-in-flight real-window event call (e.g. this hand-off happening inside the same <c>Updated</c> tick
    /// that is still iterating its own, already-captured invocation list) could otherwise still reach a
    /// just-removed handler. Flipping the flag first makes every trampoline — including ones mid-invocation right
    /// now — a no-op immediately, closing that race regardless of which invocation-list snapshot is executing.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        foreach (var trampoline in _loadedTrampolines.Values)
        {
            _inner.Loaded -= (Action)trampoline;
        }

        _loadedTrampolines.Clear();

        foreach (var trampoline in _updatedTrampolines.Values)
        {
            _inner.Updated -= (Action<double>)trampoline;
        }

        _updatedTrampolines.Clear();

        foreach (var trampoline in _renderedTrampolines.Values)
        {
            _inner.Rendered -= (Action<double>)trampoline;
        }

        _renderedTrampolines.Clear();

        foreach (var trampoline in _framebufferResizedTrampolines.Values)
        {
            _inner.FramebufferResized -= (Action<int, int>)trampoline;
        }

        _framebufferResizedTrampolines.Clear();

        foreach (var trampoline in _keyPressedTrampolines.Values)
        {
            _inner.KeyPressed -= (Action<Key>)trampoline;
        }

        _keyPressedTrampolines.Clear();

        foreach (var trampoline in _charInputTrampolines.Values)
        {
            _inner.CharInput -= (Action<char>)trampoline;
        }

        _charInputTrampolines.Clear();

        foreach (var cleanup in _cleanups)
        {
            cleanup();
        }

        _cleanups.Clear();
    }
}
