using System.Threading;
using Agapanthe.App;
using Agapanthe.Graphics;

namespace Agapanthe.Ui.Noesis;

/// <summary>
/// The Noesis-backed <see cref="IUiHost"/> — everything the Key.K vertical-slice demo used to do inline in
/// <c>AppHost.RunClient</c>, now behind the swappable contract. Owns a <see cref="VulkanRenderDevice"/>, a single
/// offscreen <see cref="VulkanRenderTarget"/>, and the parsed Noesis <see cref="global::Noesis.View"/> for one XAML
/// document — loading is entirely a construction-time concern of this class, deliberately outside
/// <see cref="IUiHost"/> itself (no second UI engine exists yet to validate a generic "load a document" shape
/// against).
/// </summary>
public sealed class NoesisUiHost : IUiHost
{
    // GUI.Init/Shutdown wrap Noesis's whole process-global native state, independent of how many hosts/views
    // exist — refcounted here (audit finding: a naive per-instance GUI.Init() with no matching Shutdown would
    // double-init on a 2nd host and never release the library). Mirrors AudioDevice's Interlocked single-instance
    // guard in spirit, though OpenAL's is a hard "only one live instance" refusal — GUI.Init/Shutdown is a normal
    // refcounted library lifetime, so multiple hosts sharing it is the correct behaviour, not a degrade.
    private static int _liveHostCount;

    private readonly GraphicsDevice _gpu;
    private readonly VulkanRenderDevice _device;
    private readonly VulkanRenderTarget _target;
    private readonly global::Noesis.View _view;
    private bool _disposed;

    public NoesisUiHost(GraphicsDevice device, string shaderDirectory, string xaml, uint width = 800, uint height = 600)
    {
        ArgumentNullException.ThrowIfNull(device);
        _gpu = device;

        if (Interlocked.Increment(ref _liveHostCount) == 1)
        {
            global::Noesis.GUI.Init();
        }

        try
        {
            _device = new VulkanRenderDevice(device, shaderDirectory);
            _target = (VulkanRenderTarget)_device.CreateRenderTarget("UiHost", width, height, 1, false);
            _device.SetRenderTarget(_target);

            var root = (global::Noesis.FrameworkElement)global::Noesis.GUI.ParseXaml(xaml);
            _view = global::Noesis.GUI.CreateView(root);
            _view.SetSize((int)width, (int)height);
            _view.Renderer.Init(_device);
        }
        catch
        {
            // Audit finding: a throw here (ParseXaml/CreateView/Renderer.Init failing after the device/target
            // were already created) used to leak the GpuImage the target owns — _device was never assigned to
            // a live NoesisUiHost, so nothing would ever dispose it. _device is a `readonly` field but C# does
            // not require definite assignment for a class's instance fields, so reading it here (possibly still
            // null, if the throw happened before its own assignment) is legal and safe.
            _device?.Dispose();
            if (Interlocked.Decrement(ref _liveHostCount) == 0)
            {
                global::Noesis.GUI.Shutdown();
            }

            throw;
        }
    }

    // Only publish a frame once something has actually been drawn into it (audit finding: previously this
    // returned the target's image unconditionally after the first Tick, even if Noesis emitted zero batches —
    // e.g. a fully transparent root with no Background — leaving the image in its initial Undefined layout.
    // Renderer.DrawTexture unconditionally transitions FROM ColorAttachment, so compositing an Undefined image
    // would be a layout-mismatch validation error sampling uninitialized memory.)
    public GpuImage? CurrentFrame => _target.EverRendered ? _target.Image : null;

    public void Tick(double deltaSeconds)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Audit finding: DrawBatch's LoadOp is Clear only on the image's very first-ever draw (EverRendered),
        // Load forever after — correct for the layout transition it also gates, but wrong for "clear once per
        // frame": without this, any transparent or animated content accumulates across frames (only invisible
        // here because the demo XAML is solid opaque red). RequestClear arms a separate one-shot flag that
        // DrawBatch consumes on its first batch of THIS Tick, independent of the once-only layout transition.
        _target.RequestClear();

        _view.Update(deltaSeconds);
        _view.Renderer.UpdateRenderTree();
        _view.Renderer.RenderOffscreen();
        // Audit finding: RenderOffscreen may bind and render into Noesis's OWN offscreen sub-targets (group
        // opacity, effects — not exercised by today's solid-fill-only demo, but a real gap for any richer
        // content). SetRenderTarget must be called again here so Render() draws into the target we composite,
        // not whatever RenderOffscreen last bound. Noesis's own reference integration loop rebinds the same way.
        _device.SetRenderTarget(_target);
        _view.Renderer.Render();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // Audit finding: Noesis requires the renderer to release its device-side resources (its internal
        // ramp/glyph textures, its reference to this RenderDevice) before the device itself is disposed — never
        // called before, a real use-after-free risk if Noesis's own teardown ever touches the device afterward.
        _view.Renderer.Shutdown();
        _view.Dispose();
        _device.Dispose();

        if (Interlocked.Decrement(ref _liveHostCount) == 0)
        {
            global::Noesis.GUI.Shutdown();
        }
    }
}
