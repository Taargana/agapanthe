using System.Runtime.CompilerServices;
using Noesis;
using NoesisApp;

// Noesis spike (branch spike/noesis-probe, follow-up to tools/CefAotProbe which FAILed): does the
// NoesisGUI Managed SDK survive .NET 10 hosting under Windows — JIT first, then NativeAOT?
//
// RESULT (verified 2026-09-23): PASS, both JIT and NativeAOT. No native-interop drama of the kind
// CEF/CefGlue hit: `Noesis.GUI.Init()` -> `Win32Display` -> `RenderContextWGL.Init` -> `GUI.ParseXaml`
// -> `GUI.CreateView` -> render loop -> `CaptureRenderTarget` all ran cleanly and produced a real
// 800x600 pixel buffer, first under plain JIT (`dotnet build`/`dotnet exec`, no RuntimeIdentifier in
// the csproj — matches tools/AotComponentProbe's convention), then from a genuine NativeAOT publish
// (`dotnet publish -r win-x64 --self-contained -p:PublishAot=true`; the published output is just
// `Noesis.dll` + a ~4MB exe, no CoreCLR runtime files at all — the real tell that it's truly AOT, since
// `RuntimeFeature.IsDynamicCodeSupported` alone reads False in BOTH JIT and AOT builds here — that's a
// runtimeconfig.json side effect of `PublishAot=true` being set project-wide, not a JIT/AOT
// differentiator by itself; confirmed by inspecting the generated runtimeconfig.json).
//
// No Noesis.GUI.SetLicense call: the SDK is "designed to be evaluated without requiring a license"
// (10-minute session cap per the trial page) — more than enough for a single render+capture.
//
// Corrects an earlier unverified claim made in conversation ("Noesis has native Vulkan support") —
// checked against the real NuGet package list and Noesis/Managed source: Noesis ships official render
// contexts for D3D11/WGL/GLX/EGL/Metal/NSGL only. There is no first-party Vulkan renderer (an
// "Implement Vulkan Renderer" ticket is open on their tracker); `RenderDevice` is Noesis's own
// abstract render-device interface (this probe uses the stock OpenGL/WGL implementation), and a real
// Agapanthe integration would need a hand-written Vulkan `RenderDevice` — a real but bounded, already
// well-precedented extension point (Noesis's own architecture is built for exactly this), not a
// hack. That is a separate, deferred question from the one this probe answers.
Console.WriteLine($"NoesisAotProbe: IsDynamicCodeSupported = {RuntimeFeature.IsDynamicCodeSupported}");

GUI.Init();

var display = new Win32Display();

var context = new RenderContextWGL();
// Verified against Noesis/Managed source (RenderContextWGL.Init): on WGL, the "display" parameter is
// actually the HDC, "window" is the HWND — Win32Display.NativeHandle/NativeWindow map to exactly that.
context.Init(display.NativeHandle, display.NativeWindow, 1, false, false);
context.SetDefaultRenderTarget(800, 600, true);

const string xamlSource = """
    <Grid xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" Background="Red"/>
    """;
var xamlRoot = (FrameworkElement)GUI.ParseXaml(xamlSource);
var view = GUI.CreateView(xamlRoot);
view.SetSize(800, 600);
view.Renderer.Init(context.Device);

view.Update(0.0);
view.Renderer.UpdateRenderTree();
view.Renderer.RenderOffscreen();

context.BeginRender();
view.Renderer.Render();
context.EndRender();

var capture = context.CaptureRenderTarget(null!);
if (capture is null || capture.Width <= 0 || capture.Height <= 0)
{
    Console.Error.WriteLine("NoesisAotProbe: FAIL — capture produced no usable image.");
    return 1;
}

Console.WriteLine(
    $"NoesisAotProbe: PASS — captured a {capture.Width}x{capture.Height} render target after rendering.");
return 0;
