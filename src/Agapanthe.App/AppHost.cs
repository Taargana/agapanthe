using System.Diagnostics;
using Agapanthe.Assets;
using Agapanthe.Assets.Font;
using Agapanthe.Audio;
using Agapanthe.Core;
using Agapanthe.Engine;
using Agapanthe.Engine.Render;
using Agapanthe.Graphics;
using Agapanthe.Rendering;
using Agapanthe.World;
using Silk.NET.Input;

namespace Agapanthe.App;

/// <summary>
/// The client host: it owns the GPU + window bootstrap, the fixed-step frame loop, the capture harness, the
/// strict-order teardown (the 0-leak gate) and the host-level keyboard. The application supplies an
/// <see cref="IGame"/> (its scenes and identity) and a concrete <see cref="IWindow"/>; every scene-specific
/// decision lives in an <see cref="ISceneRecipe"/>.
/// <para>
/// This is the extraction of <c>samples/Sandbox/Program.cs</c>'s ~2360 lines into a reusable layer (backlog
/// §4quater). It composes a <see cref="SimulationHost"/> and hands it to
/// <see cref="FrameOrchestrator.CreateDefault(SimulationHost, GameWorld, Renderer, ResourceRegistry, Camera, RenderList, float)"/>
/// — the composition-root seam — and stamps the first real <see cref="UniverseId"/>.
/// </para>
/// </summary>
public static class AppHost
{
    private const int BenchLogEvery = 60;
    private const float SensStep = 1.25f;
    private const float SensMin = 0.0001f;
    private const float SensMax = 0.01f;

    /// <summary>
    /// Runs <paramref name="game"/> as a windowed client to completion. Returns 0 on a clean shutdown
    /// (no ResourceTracker leak), 1 otherwise — the process exit code the pre-extraction Program.cs used.
    /// <para>
    /// The caller owns <paramref name="window"/>'s concrete type (and its title); the host owns its lifecycle
    /// from here — it is disposed LAST, in the strict teardown, after the leak report.
    /// </para>
    /// </summary>
    public static int RunClient(IGame game, IWindow window, string[] args, HostOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(game);
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(args);
        options ??= HostOptions.FromEnvironment();

        var shaderDir = ResolveShaderDirectory();
        var maxFrames = options.MaxFrames;
        var renderedFrames = 0;

        GraphicsDevice? device = null;
        Swapchain? swapchain = null;
        Renderer? renderer = null;
        ResourceRegistry? registry = null;
        FrameRenderer? frameRenderer = null;
        FrameOrchestrator? orchestrator = null;
        DebugOverlaySystem? debugOverlay = null;
        UiRenderSystem? uiSystem = null;
        AudioDevice? audioDevice = null;
        AudioClip audioDemoClip = default;

        var world = new GameWorld(GlobalIdRange.Default, ResolveUniverse(game, options));
        var camera = new Camera();
        var controller = new FreeCameraController();
        var renderList = new RenderList();
        var resizePending = false;

        // Scene management spec, D9/D13: the profiler survives every switch (one FrameStats for the whole
        // process, threaded into every SimulationHost.CreateDefault call, first load included).
        var sharedFrameStats = new FrameStats();

        // Scene management spec, D9: `sim` and the scoped window are the only NEW hoisted locals (D9's own
        // hoisting note) — `world`/`registry`/`orchestrator` above are already top-level, reassigned by the
        // switch machinery below exactly like `resizePending` already reassigns `swapchain`'s state. `catalog`
        // is hoisted too: PrefetchBackground needs it on every switch, not just the first load.
        SimSceneContext? sim = null;
        ScopedWindow? scopedWindow = null;
        AssetCatalog? catalog = null;
        string? currentSceneName = null;

        // Scene management spec, D9.1-D9.6: the in-flight switch state machine. `loaderThread`/`loaderCts`/
        // `loaderRegistry`/`loaderPrefetched`/`loaderFailure` describe AT MOST ONE in-flight background load;
        // `nextPendingSwitch` is the D9.2 case — a second request arrived while one was already running.
        Thread? loaderThread = null;
        CancellationTokenSource? loaderCts = null;
        ResourceRegistry? loaderRegistry = null;
        object? loaderPrefetched = null;
        Exception? loaderFailure = null;
        string? nextPendingSwitch = null;
        ISceneRecipe? pendingRecipe = null;

        // Bench (AGAPANTHE_CULL_STATS): measure the whole per-frame cost (tick + draw) and log every 60 frames.
        var benchFrame = 0;
        long benchTicks = 0;
        long benchAllocBefore = 0;
        long benchCpuStart = 0;

        window.Loaded += () =>
        {
            // Audio-1: independent of the GPU/Vulkan bootstrap below — always attempted (spec D7), never
            // crashes the host on a machine with no audio hardware/driver (AudioDevice.TryCreate never throws).
            audioDevice = AudioDevice.TryCreate(enabled: options.AudioEnabled);

            var requiredExtensions = window.GetRequiredVulkanExtensions();
            device = new GraphicsDevice(game.Title, requiredExtensions, window.VkSurface!);

            var (width, height) = window.FramebufferSize;
            swapchain = new Swapchain(device, width, height);
            camera.AspectRatio = (float)width / height;

            renderer = new Renderer(device, swapchain, shaderDir, options.GpuTimestampsEnabled);
            // Half a stop under 1 — the default studio HDRI clips to white at exposure 1. +/- moves it at runtime;
            // a scene recipe with its own lighting model (the planet family) overrides it in Build.
            renderer.Exposure = 0.5f;
            renderer.VerifyCull = options.VerifyCull;

            registry = new ResourceRegistry();
            frameRenderer = new FrameRenderer(device, swapchain, () => window.FramebufferSize);

            // The orchestrator is built BEFORE the recipe so the recipe can register its systems on it. It composes
            // a SimulationHost bound to the fixed-step single definition; SceneViewSystem is the only render system
            // it registers itself.
            var simulation = SimulationHost.CreateDefault(world, SimulationSettings.Default, sharedFrameStats);
            orchestrator = FrameOrchestrator.CreateDefault(
                simulation, world, renderer, registry, camera, renderList);

            // The debug overlay (UI-2) is engine infrastructure, not a game concern: it records frame metrics every
            // frame whether shown or not. Registered here so it runs after the scene view system.
            var fontPath = Path.Combine(AppContext.BaseDirectory, "fonts", "JetBrainsMono-Regular.agfont");
            if (File.Exists(fontPath))
            {
                var uiFont = FontAssetFormat.Read(File.ReadAllBytes(fontPath));
                renderer.LoadFont(uiFont);
                uiSystem = new UiRenderSystem(renderer);
                orchestrator.Add(Stage.Input, uiSystem);   // clears last frame's quads before any system draws
                orchestrator.Add(uiSystem);                 // render stage
                debugOverlay = new DebugOverlaySystem(
                    uiSystem.DrawList, uiFont, renderer, renderList, orchestrator.Simulation.Stats)
                {
                    Visible = options.OverlayVisible,
                };
                orchestrator.Add(Stage.PostSimulation, debugOverlay);
                Log.Info($"AppHost: [ui] font loaded from '{fontPath}'.");
            }
            else
            {
                Log.Warn($"AppHost: [ui] no cooked font at '{fontPath}' — text overlay disabled.");
            }

            // Contenu-2: the cooked-content catalog — recipes resolve models by AssetKey through it, no glTF at runtime.
            // A fully-procedural scene (planet*) needs no cooked content, so a missing manifest is a warning, not a
            // fatal — LoadModel on the empty catalog then fails with an actionable message only if a scene asks.
            try
            {
                catalog = AssetCatalog.Open(ResolveContentRoot(options));
            }
            catch (AssetException ex)
            {
                Log.Warn($"AppHost: {ex.Message} — procedural scenes still run; a model scene will fail.");
                catalog = AssetCatalog.Empty;
            }

            // The game builds its scene: spawn entities, register systems, frame the camera, wire input.
            // Contenu-3a: the context is split — a sim half (headless-safe) + a presentation half.
            var recipe = SelectRecipe(game, options.Scene);
            sim = new SimSceneContext
            {
                World = world,
                Simulation = orchestrator.Simulation,
                Catalog = catalog,
                Args = args,
                Options = options,
            };
            scopedWindow = new ScopedWindow(window);
            var presentation = new PresentationSceneContext
            {
                Device = device,
                Registry = registry,
                Renderer = renderer,
                Camera = camera,
                Controller = controller,
                Window = scopedWindow,
                RenderList = renderList,
                Orchestrator = orchestrator,
                SceneSystemFactories = game.SceneSystems,
            };

            // Scene management spec, D14: the first load uses the same two-phase mechanism a later switch does —
            // PrefetchBackground then Build, synchronously here (no thread for the very first scene).
            var background = new BackgroundPresentationContext
            {
                Device = device,
                Registry = registry,
                MaterialSetLayout = renderer.MaterialSetLayout,
                CancellationToken = CancellationToken.None,
            };
            var prefetched = recipe.PrefetchBackground(catalog, background);
            recipe.Build(prefetched, sim, presentation);
            currentSceneName = recipe.Name;
            WarnIfDrawablesMissingIdentity(world);

            // Contenu-3a: apply a restore the recipe requested (AGAPANTHE_LOAD) — after Build, so every asset the
            // snapshot can reference is registered. The resolver rebuilds the MeshRef render cache from AssetRef.
            if (sim.HasPendingRestore)
            {
                var from = sim.PendingRestorePath;
                var loaded = sim.ApplyPendingRestore(registry.ResolveMeshRef);
                Log.Info($"AppHost: [Contenu-3a] world restored from '{from}' — {loaded.EntityCount} entities, universe {loaded.Universe}.");
            }

            // VS-1: AGAPANTHE_SAVE snapshots the fully-built world (Save flushes pending structural changes first).
            if (options.SavePath is { } savePath)
            {
                using var saveStream = File.Create(savePath);
                // Contenu-3a: each entity carries its AssetRef, so Save emits stable AssetKeys with no delegate.
                world.Save(saveStream);
                Log.Info($"AppHost: [VS-1] world saved to '{savePath}' ({world.LiveEntityCount} entities).");
            }

            Log.Info(
                $"AppHost: initialized on '{device.AdapterName}' — '{game.Title}', scene '{recipe.Name}'. "
                + $"Hot reload active on '{shaderDir}'.");

            // Headless proof of the <1s reload budget (M8): forces one reload of every graphics pass. Runs before
            // the first frame (GPU idle) so the deferred pipeline swap is safe.
            if (options.ShaderReloadTest)
            {
                renderer.ReloadAllForTest();
            }
        };

        window.FramebufferResized += (w, h) =>
        {
            resizePending = true;
            if (w > 0 && h > 0)
            {
                camera.AspectRatio = (float)w / h;
            }
        };

        window.KeyPressed += key =>
        {
            switch (key)
            {
                case Key.Escape when window.MouseCaptured:
                    window.SetMouseCaptured(false);
                    break;
                case Key.Escape:
                    window.Close();
                    break;
                case Key.PageUp:
                    controller.LookSensitivityX = MathF.Min(controller.LookSensitivityX * SensStep, SensMax);
                    LogSensitivity();
                    break;
                case Key.PageDown:
                    controller.LookSensitivityX = MathF.Max(controller.LookSensitivityX / SensStep, SensMin);
                    LogSensitivity();
                    break;
                case Key.Home:
                    controller.LookSensitivityY = MathF.Min(controller.LookSensitivityY * SensStep, SensMax);
                    LogSensitivity();
                    break;
                case Key.End:
                    controller.LookSensitivityY = MathF.Max(controller.LookSensitivityY / SensStep, SensMin);
                    LogSensitivity();
                    break;
                case Key.Equal or Key.KeypadAdd when renderer is not null:
                    renderer.Exposure = MathF.Min(renderer.Exposure * 1.26f, 64f);
                    Log.Info($"Exposure: {renderer.Exposure:F3} ({MathF.Log2(renderer.Exposure):+0.0;-0.0} EV)");
                    break;
                case Key.Minus or Key.KeypadSubtract when renderer is not null:
                    renderer.Exposure = MathF.Max(renderer.Exposure / 1.26f, 1f / 64f);
                    Log.Info($"Exposure: {renderer.Exposure:F3} ({MathF.Log2(renderer.Exposure):+0.0;-0.0} EV)");
                    break;
                case Key.N when renderer is not null:
                    renderer.DebugView = (renderer.DebugView + 1) % DebugViewNames.Length;
                    Log.Info($"Debug view: {renderer.DebugView} ({DebugViewNames[renderer.DebugView]})");
                    break;
                case Key.L when renderer is not null:
                    var d = renderer.Lights.Directional;
                    var (sin, cos) = MathF.SinCos(MathF.PI / 8f);
                    d.Direction = new System.Numerics.Vector3(
                        (d.Direction.X * cos) - (d.Direction.Z * sin),
                        d.Direction.Y,
                        (d.Direction.X * sin) + (d.Direction.Z * cos));
                    renderer.Lights.Directional = d;
                    Log.Info($"Key light direction: {d.Direction}");
                    break;
                case Key.F3 when debugOverlay is not null:
                    debugOverlay.Toggle();
                    break;
                case Key.F:
                    // Physics queries demo (D6): crosshair raycast, not a literal cursor-position pick — once the
                    // mouse is captured (the common case, FPS-style look) it has no meaningful on-screen position,
                    // so "what's under the cursor" is answered at screen center, matching this engine's existing
                    // capture-on-click convention. Demo only: logs the hit, no new gameplay system.
                    var (fbWidth, fbHeight) = window.FramebufferSize;
                    if (fbWidth > 0 && fbHeight > 0)
                    {
                        var screenCenter = new System.Numerics.Vector2(fbWidth / 2f, fbHeight / 2f);
                        var ray = camera.ScreenPointToRay(screenCenter, (uint)fbWidth, (uint)fbHeight);
                        if (world.TryRaycast(in ray, maxDistance: 1_000_000.0, out var hit))
                        {
                            Log.Info($"AppHost: [raycast] hit entity {hit.Entity} at distance {hit.Distance:F2} m.");
                        }
                        else
                        {
                            Log.Info("AppHost: [raycast] no hit.");
                        }
                    }

                    break;
                case Key.G:
                    // Shape queries demo (D5, spec docs/plans/2026-09-14-shape-queries-overlap-design.md): "what
                    // is inside this sphere?" around the camera — demo only, no new gameplay system.
                    Span<OverlapHit> overlapResults = stackalloc OverlapHit[64];
                    var overlapCount = world.OverlapSphere(camera.Position, 10f, overlapResults, GameWorld.AllLayers);
                    Log.Info($"AppHost: [overlap] {overlapCount} entities within 10 m of the camera.");
                    for (var i = 0; i < overlapCount; i++)
                    {
                        Log.Info($"AppHost: [overlap]   {overlapResults[i].Entity} at distance {overlapResults[i].Distance:F2} m.");
                    }

                    break;
                case Key.H:
                    // Shape queries demo (D4, spec docs/plans/2026-09-14-shape-queries-box-overlap-design.md):
                    // "what is inside this box?" — a 10x10x10 AABB centered on the camera. Demo only, no new
                    // gameplay system.
                    var boxMin = camera.Position - new Double3(5, 5, 5);
                    var boxMax = camera.Position + new Double3(5, 5, 5);
                    Span<OverlapHit> boxResults = stackalloc OverlapHit[64];
                    var boxCount = world.OverlapBox(boxMin, boxMax, boxResults, GameWorld.AllLayers);
                    Log.Info($"AppHost: [overlap-box] {boxCount} entities in the box.");
                    for (var i = 0; i < boxCount; i++)
                    {
                        Log.Info($"AppHost: [overlap-box]   {boxResults[i].Entity} at distance {boxResults[i].Distance:F2} m.");
                    }

                    break;
                case Key.J when audioDevice is not null:
                    // Audio-1 demo: a synthesized beep, loaded once and cached (spec D5/D6) — always safe to
                    // call, a silent no-op on a machine with no audio device (audioDevice.Supported == false).
                    // Wrapped: an unexpected AudioException/IOException here must not crash the whole app from
                    // inside a KeyPressed handler (audit finding, csharp-lowlevel) — log and drop instead.
                    try
                    {
                        if (audioDemoClip.Equals(default(AudioClip)) && audioDevice.Supported)
                        {
                            using var demoWav = new MemoryStream();
                            WavFormat.Write(demoWav, WavFormat.SineTone());
                            demoWav.Position = 0;
                            audioDemoClip = AudioLoader.Load(demoWav, audioDevice);
                        }

                        audioDevice.Play(audioDemoClip);
                    }
                    catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException))
                    {
                        Log.Error($"AppHost: Key.J audio demo failed — {ex.Message}");
                    }

                    break;
                case Key.K:
                    // Scene management spec demo: cycles between the two simplest cooked scenes so a switch is
                    // exercisable live without depending on any particular IGame's own scene list beyond "at
                    // least one ISceneRecipe.Name it can name". Harmless if the game has no matching recipe —
                    // RequestSceneSwitch only records the request, SelectRecipe's own throw surfaces at the next
                    // poll, exactly like an unresolvable AGAPANTHE_SCENE does today.
                    if (sim is not null)
                    {
                        var next = string.Equals(currentSceneName, "grid", StringComparison.OrdinalIgnoreCase)
                            ? "model"
                            : "grid";
                        sim.RequestSceneSwitch(next);
                        Log.Info($"AppHost: [scene switch] requested '{next}'.");
                    }

                    break;
            }

            void LogSensitivity()
                => Log.Info(
                    $"Look sensitivity: X={controller.LookSensitivityX:F5} Y={controller.LookSensitivityY:F5} rad/px");
        };

        window.Rendered += dt =>
        {
            if (frameRenderer is null || swapchain is null || orchestrator is null || renderer is null)
            {
                return;
            }

            // Scene management spec, D9: the switch-orchestration poll — same frame boundary resizePending
            // already uses, never from inside Tick (D4's correction: RequestSceneSwitch may be called from a
            // non-owner thread under Job-1, so no orchestration can safely live there).
            if (sim is not null)
            {
                // Step 1: finish an in-flight load that has already completed, if any — checked FIRST, before
                // looking at any new request, so a request arriving the same poll a load finishes never cancels
                // work that is already done (self-review finding: doing it the other way round would hand off
                // the about-to-be-superseded scene anyway, then immediately switch again — harmless but wasteful).
                if (loaderThread is { IsAlive: false })
                {
                    loaderThread.Join();
                    device!.ClearSanctionedLoaderThread();
                    loaderThread = null;
                    loaderCts?.Dispose();
                    loaderCts = null;

                    if (loaderFailure is not null)
                    {
                        // D9.2's cancellation surfaces here too (PrefetchBackground's CancellationToken.
                        // ThrowIfCancellationRequested throws OperationCanceledException, caught by StartLoad's
                        // thread body like any other exception) — distinguish it in the log, but the cleanup is
                        // identical either way: keep the current scene running (D9.4), dispose the never-
                        // activated registry.
                        if (loaderFailure is OperationCanceledException)
                        {
                            Log.Info("AppHost: [scene switch] load canceled — a newer request superseded it.");
                        }
                        else
                        {
                            Log.Error($"AppHost: [scene switch] PrefetchBackground failed — {loaderFailure}");
                        }

                        loaderRegistry?.Dispose(); // never activated — no WaitIdle needed
                        loaderRegistry = null;
                        loaderPrefetched = null;
                        loaderFailure = null;
                        pendingRecipe = null;
                    }
                    else
                    {
                        PerformHandOff();
                    }
                }

                // Step 2/3: a load is still running (a new request cancels it, D9.2, queued for once it exits and
                // never blocking a frame) OR nothing is running (start the next queued/fresh request, if any).
                if (loaderThread is not null)
                {
                    var incoming = sim.DrainPendingSceneSwitch();
                    if (incoming is not null)
                    {
                        loaderCts?.Cancel();
                        nextPendingSwitch = incoming;
                    }
                }
                else
                {
                    var token = nextPendingSwitch ?? sim.DrainPendingSceneSwitch();
                    nextPendingSwitch = null;
                    if (token is not null)
                    {
                        StartLoad(token);
                    }
                }
            }

            if (resizePending)
            {
                resizePending = false;
                frameRenderer.RequestResize();
            }

            renderer.PollShaderReload();

            if (options.CullStats)
            {
                benchAllocBefore = GC.GetAllocatedBytesForCurrentThread();
                benchCpuStart = Stopwatch.GetTimestamp();
            }

            // Tick OUTSIDE DrawFrame (P3-M2 D1.a). In capture/bench mode (maxFrames > 0) feed a constant equal to
            // the fixed step so the run is reproducible tick-for-tick; an interactive session consumes the real dt.
            var wallClockDt = maxFrames > 0 ? orchestrator.FixedTickDeltaSeconds : (float)dt;
            orchestrator.Tick(wallClockDt);
            frameRenderer.DrawFrame(orchestrator.RenderDelegate);
            orchestrator.EndFrame(); // AFTER DrawFrame: the bracket covers submit + present, and resize frames too

            if (options.CullStats)
            {
                var alloc = GC.GetAllocatedBytesForCurrentThread() - benchAllocBefore;
                benchTicks += Stopwatch.GetTimestamp() - benchCpuStart;
                if (++benchFrame % BenchLogEvery == 0)
                {
                    var avgMs = Stopwatch.GetElapsedTime(0, benchTicks).TotalMilliseconds / BenchLogEvery;
                    Log.Info(
                        $"AppHost: [cull-stats] frame {benchFrame} — candidates {renderList.Count}, "
                        + $"draws {renderer.LastSceneDrawCalls}+{renderer.LastShadowDrawCalls} (instanced), "
                        + $"tick+draw avg {avgMs:F3} ms/frame, per-frame alloc {alloc} B, "
                        + $"sim ticks {orchestrator.LastFrameTickCount}.");
                    benchTicks = 0;
                }
            }

            // Arm the presented-image snapshot one frame early (a presented image may not be touched once released).
            if (maxFrames > 1 && renderedFrames == maxFrames - 2 && options.CaptureUiPath is not null)
            {
                frameRenderer.RequestCapture();
            }

            if (maxFrames > 0 && ++renderedFrames >= maxFrames)
            {
                if (options.CaptureUiPath is { } uiCapturePath)
                {
                    frameRenderer.WaitIdle();
                    var captured = frameRenderer.ReadCapture();
                    if (captured is not null)
                    {
                        var (capW, capH) = frameRenderer.LastPresentedExtent;
                        Ppm.WriteSwapchain(uiCapturePath, captured, (int)capW, (int)capH, swapchain.ColorFormat);
                        Log.Info($"AppHost: [ui] swapchain capture saved to '{uiCapturePath}' ({capW}x{capH}).");
                    }
                    else
                    {
                        Log.Warn(
                            "AppHost: [ui] no swapchain capture available — either the surface lacks TRANSFER_SRC, "
                            + "or AGAPANTHE_MAX_FRAMES is too small to arm one (needs at least 2).");
                    }
                }

                if (options.CapturePath is { } capturePath)
                {
                    frameRenderer.WaitIdle();
                    renderer.SaveHdrCapture(capturePath);
                    Log.Info(
                        $"AppHost: [shadow] CSM {renderer.Cascades.Count} cascades, lambda {renderer.Cascades.Lambda:F2}, "
                        + $"range {MathF.Min(renderer.Cascades.MaxDistance, renderer.ShadowDistance):F1} m "
                        + $"({Renderer.ShadowTileResolution}² per cascade in the {Renderer.ShadowMapResolution}² atlas).");

                    if (renderer.VerifyCull)
                    {
                        var gpu = renderer.ReadBackSceneVisible();
                        var cpu = renderer.LastSceneCpuVisible;
                        Log.Info(
                            $"AppHost: [cull-verify] GPU visible {gpu} vs CPU visible {cpu} — "
                            + $"{(gpu == cpu ? "MATCH" : "MISMATCH")}.");
                        Span<int> shadowPerCascade = stackalloc int[4];
                        var shadowTotal = renderer.ReadBackShadowVisible(shadowPerCascade);
                        Log.Info(
                            $"AppHost: [shadow-verify] shadow instances total {shadowTotal} — per cascade "
                            + $"[{shadowPerCascade[0]}, {shadowPerCascade[1]}, {shadowPerCascade[2]}, {shadowPerCascade[3]}].");
                    }
                }

                window.Close();
            }

            // Scene management spec, D9.1: resolves the recipe, spawns the sanctioned loader thread and calls
            // PrefetchBackground on it. Sanctions BEFORE Start() — never after, or the loader could submit
            // before GraphicsDevice recognizes it.
            void StartLoad(string token)
            {
                ISceneRecipe recipeToLoad;
                try
                {
                    recipeToLoad = SelectRecipe(game, token);
                }
                catch (Exception ex)
                {
                    Log.Error($"AppHost: [scene switch] '{token}' — {ex.Message}");
                    return;
                }

                pendingRecipe = recipeToLoad;
                loaderFailure = null;
                loaderPrefetched = null;
                loaderRegistry = new ResourceRegistry();
                loaderCts = new CancellationTokenSource();
                var background = new BackgroundPresentationContext
                {
                    Device = device!,
                    Registry = loaderRegistry,
                    MaterialSetLayout = renderer!.MaterialSetLayout,
                    CancellationToken = loaderCts.Token,
                };

                var thread = new Thread(() =>
                {
                    try
                    {
                        loaderPrefetched = recipeToLoad.PrefetchBackground(catalog!, background);
                    }
                    catch (Exception ex)
                    {
                        loaderFailure = ex;
                    }
                });
                loaderThread = thread;
                device!.SetSanctionedLoaderThread(thread.ManagedThreadId);
                thread.Start();
                Log.Info($"AppHost: [scene switch] loading '{token}'.");
            }

            // Scene management spec, D9.3: the hand-off, run once PrefetchBackground finished successfully.
            // frameRenderer.WaitIdle() first — ResourceRegistry.Dispose frees DescriptorAllocator pools
            // synchronously (not through the deferred DeletionQueue), so disposing the active registry without
            // this first would be a use-after-free on in-flight frames' descriptor sets.
            void PerformHandOff()
            {
                frameRenderer!.WaitIdle();

                scopedWindow?.Dispose();
                orchestrator!.Simulation.Dispose();
                registry?.Dispose();
                world.Dispose();

                world = new GameWorld(GlobalIdRange.Default, ResolveUniverse(game, options));
                var simulation = SimulationHost.CreateDefault(world, SimulationSettings.Default, sharedFrameStats);

                registry = loaderRegistry;
                loaderRegistry = null;

                orchestrator = FrameOrchestrator.CreateDefault(simulation, world, renderer!, registry!, camera, renderList);
                if (uiSystem is not null)
                {
                    orchestrator.Add(Stage.Input, uiSystem);
                    orchestrator.Add(uiSystem);
                }

                if (debugOverlay is not null)
                {
                    orchestrator.Add(Stage.PostSimulation, debugOverlay);
                }

                // Module-boundary correction (BlackEnvironment lives in Agapanthe.App, Renderer must not
                // reference App): ResetSceneState only clears Rendering's own bookkeeping; the environment
                // reset happens here, right after it.
                renderer!.ResetSceneState();
                renderer.SetEnvironment(BlackEnvironment.Build());

                scopedWindow = new ScopedWindow(window);
                sim = new SimSceneContext
                {
                    World = world,
                    Simulation = orchestrator.Simulation,
                    Catalog = catalog!,
                    Args = args,
                    // D12: never re-trigger AGAPANTHE_SAVE/re-apply AGAPANTHE_LOAD on a switch.
                    Options = options.WithoutStartupOnlyPaths(),
                };
                var presentation = new PresentationSceneContext
                {
                    Device = device!,
                    Registry = registry!,
                    Renderer = renderer,
                    Camera = camera,
                    Controller = controller,
                    Window = scopedWindow,
                    RenderList = renderList,
                    Orchestrator = orchestrator,
                    SceneSystemFactories = game.SceneSystems,
                };

                var recipeToBuild = pendingRecipe!;
                pendingRecipe = null;
                var prefetchedForBuild = loaderPrefetched;
                loaderPrefetched = null;

                // D9.5: fatal by design if this throws — the old scene is already torn down, there is nothing
                // to fall back to. Propagates to RunClient's own top-level catch (failed = true, strict teardown).
                recipeToBuild.Build(prefetchedForBuild, sim, presentation);
                currentSceneName = recipeToBuild.Name;
                WarnIfDrawablesMissingIdentity(world);
                Log.Info($"AppHost: [scene switch] now on '{recipeToBuild.Name}'.");
            }
        };

        var clean = false;
        var failed = false;
        // Audio-1: starts false, same posture as `clean` (GPU) — only set true after Dispose()+ReportLeaks()
        // both actually succeed (audit finding, csharp-lowlevel: starting it true meant a THROW inside
        // DisposeAudio's own step left it true, incorrectly reporting a clean exit).
        var audioClean = false;
        try
        {
            window.Run();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A failure inside window.Loaded (a bad AGAPANTHE_SCENE, a missing model, a cross-universe
            // WorldSerializationException, a GPU fault) OR anywhere in the frame loop surfaces here. Log it with
            // the full stack (ex.ToString), fall through to the strict teardown + leak report rather than a raw
            // crash — and force exit 1: `failed` survives the teardown's Report step overwriting `clean`.
            Log.Error($"AppHost: run aborted — {ex}");
            failed = true;
        }
        finally
        {
            var targets = new TeardownTargets(frameRenderer, world, registry, renderer, device, swapchain, window)
            {
                // The leak report is a teardown STEP (position 9, right before window.Dispose) so a native GLFW
                // fault in window.Dispose can never mask it. Its return value is the exit code.
                Report = () =>
                {
                    clean = ResourceTracker.Report();
                    Log.Info(clean
                        ? "AppHost: clean shutdown, no GPU resource leaks."
                        : "AppHost: LEAKS DETECTED (see above).");
                },
                // Scene management spec, D9.6: the window closed while a background load was in flight. Blocking
                // here is acceptable — this is shutdown, not a frame-budget-sensitive path. Must run before
                // device.Dispose(): the thread-safety spec's Dispose() throws if a loader is still sanctioned.
                DisposeInFlightLoader = () =>
                {
                    if (loaderThread is null)
                    {
                        return;
                    }

                    if (loaderThread.IsAlive)
                    {
                        loaderCts?.Cancel();
                        loaderThread.Join();
                    }

                    loaderRegistry?.Dispose();
                    loaderRegistry = null;
                    device?.ClearSanctionedLoaderThread();
                },
                // Audio-1 (spec D10): disposed FIRST (cheapest, most independent resource — each step's own
                // isolated try/catch below means exact position has no correctness consequence either way).
                // audioClean is a SEPARATE local, not folded into `clean` via `Report`'s assignment — this step
                // runs before ResourceTracker.Report(), and `clean = ResourceTracker.Report()` is a plain
                // assignment that would silently overwrite anything set here first.
                DisposeAudio = () =>
                {
                    // audioClean is left false (its initial value) if Dispose() itself throws below — a thrown
                    // teardown step must never read as a clean exit (audit finding, csharp-lowlevel).
                    if (audioDevice is null)
                    {
                        audioClean = true; // Loaded never ran far enough to create one — vacuously nothing to leak.
                        return;
                    }

                    audioDevice.Dispose();
                    audioClean = audioDevice.ReportLeaks();
                    if (!audioClean)
                    {
                        Log.Error("AppHost: AUDIO LEAK DETECTED — an OpenAL buffer/source was never released.");
                    }
                },
            };

            // Each step is isolated: a throw in one (e.g. WaitIdle on a lost device) must not skip the leak
            // report (step 9) or window.Dispose (step 10). The label — otherwise discarded — names the culprit.
            foreach (var (label, step) in BuildTeardown(in targets))
            {
                try
                {
                    step();
                }
                catch (Exception ex)
                {
                    Log.Error($"AppHost: teardown step '{label}' threw — {ex}");
                }
            }
        }

        return clean && !failed && audioClean ? 0 : 1;
    }

    /// <summary>Picks the recipe for <paramref name="sceneToken"/> (already trimmed; may be null/empty for the
    /// default). A recipe's <see cref="ISceneRecipe.Matches"/> gets first refusal — including on null/empty, so a
    /// recipe can claim the default on a side condition (e.g. <c>AGAPANTHE_LOAD</c> forces the planet scene). When
    /// nothing claims a null/empty token, <see cref="IGame.DefaultScene"/> is the fallback. Throws if a non-empty
    /// token matches none, or if <c>DefaultScene</c> names no recipe.</summary>
    // Contenu-3a (audit 🟠): the deleted MeshRefIdentifier turned "a by-hand ImportedEntitySpec copy forgot
    // .Identity" from a loud GraphicsException at save time into a silent AssetKey.None on disk. This catches that
    // class right after Build, in Debug only.
    [Conditional("DEBUG")]
    private static void WarnIfDrawablesMissingIdentity(GameWorld world)
    {
        var missing = world.DrawablesMissingIdentity();
        if (missing > 0)
        {
            Log.Warn(
                $"AppHost: {missing} drawable(s) have a resolved MeshRef but AssetRef.None — a spec copy dropped "
                + ".Identity. They will render now but serialise as AssetKey.None (invisible after a reload).");
        }
    }

    internal static ISceneRecipe SelectRecipe(IGame game, string? sceneToken)
    {
        sceneToken = sceneToken?.Trim();
        foreach (var recipe in game.Scenes)
        {
            if (recipe.Matches(sceneToken))
            {
                return recipe;
            }
        }

        if (string.IsNullOrEmpty(sceneToken))
        {
            return game.Scenes.FirstOrDefault(r => string.Equals(r.Name, game.DefaultScene, StringComparison.Ordinal))
                ?? throw new InvalidOperationException(
                    $"IGame.DefaultScene '{game.DefaultScene}' matches no recipe in Scenes.");
        }

        throw new ArgumentException($"AGAPANTHE_SCENE '{sceneToken}' matches no scene.");
    }

    /// <summary>The universe id stamped onto the world (MP-0b — the debt the <c>Agapanthe.App</c> milestone pays):
    /// <c>AGAPANTHE_UNIVERSE</c> (via <paramref name="options"/>) wins, else <see cref="IGame.Universe"/>, else
    /// <see cref="UniverseId.None"/>. A game that stays <c>None</c> keeps every pinned capture/snapshot byte-identical.</summary>
    internal static UniverseId ResolveUniverse(IGame game, HostOptions options)
        => options.Universe ?? game.Universe;

    /// <summary>The strict-order teardown (M4-11), as one ordered list. <see cref="RunClient"/>'s <c>finally</c>
    /// iterates <b>this exact list</b>; a test asserts the <c>Label</c> projection — the executed order and the
    /// asserted order are the same structure, they cannot drift. Every step is null-safe, so
    /// <c>BuildTeardown(default)</c> yields the labels with harmless no-op steps.
    /// <para>
    /// <c>ResourceTracker.Report</c> is step 9 — right before <c>window.Dispose</c> — so a native GLFW fault in the
    /// window teardown can never mask the 0-leak result. <see cref="RunClient"/> supplies it via
    /// <see cref="TeardownTargets.Report"/> (it closes over the exit-code flag); <c>default</c> leaves it a no-op.
    /// </para>
    /// </summary>
    internal static IReadOnlyList<(string Label, Action Step)> BuildTeardown(in TeardownTargets t)
    {
        var fr = t.FrameRenderer;
        var w = t.World;
        var reg = t.Registry;
        var r = t.Renderer;
        var dev = t.Device;
        var sc = t.Swapchain;
        var win = t.Window;
        var report = t.Report;
        var disposeAudio = t.DisposeAudio;
        var disposeInFlightLoader = t.DisposeInFlightLoader;
        return
        [
            ("audioDevice.Dispose+ReportLeaks", disposeAudio ?? (static () => { })),
            ("frameRenderer.WaitIdle", () => fr?.WaitIdle()),
            ("frameRenderer.Dispose", () => fr?.Dispose()),
            // Scene management spec, D9.6: stop any in-flight background load before device.Dispose() below —
            // the thread-safety spec's Dispose() throws if a loader thread is still sanctioned.
            ("scene switch: dispose in-flight loader", disposeInFlightLoader ?? (static () => { })),
            ("world.Dispose", () => w?.Dispose()),
            ("registry.Dispose", () => reg?.Dispose()),
            ("renderer.Dispose", () => r?.Dispose()),
            ("device.DeletionQueue.FlushAll", () => dev?.DeletionQueue.FlushAll()),
            ("swapchain.Dispose", () => sc?.Dispose()),
            ("device.Dispose", () => dev?.Dispose()),
            ("ResourceTracker.Report", report ?? (static () => { })),
            ("window.Dispose", () => win?.Dispose()),
        ];
    }

    // The N-key shading debug view names (mesh.frag order). Lives here because the key is host-level.
    private static readonly string[] DebugViewNames =
    [
        "PBR", "shaded normal", "geometric normal", "base color", "metallic",
        "roughness", "occlusion", "tangent (+handedness)", "key NdotL", "shadow factor", "CSM cascade",
    ];

    /// <summary>Resolves the shader directory: hot reload watches the editable repo source (…/shaders), not the
    /// read-only bin/ copy. Walk UP from the output directory (skipping the bin/ copy itself); a deployed build with
    /// no such ancestor falls back to the bin/ copy (hot reload then inert). Public so a standalone tool that builds
    /// its own device (e.g. the Sandbox's IBL test) does not duplicate it.</summary>
    public static string ResolveShaderDirectory()
    {
        var binShaders = Path.Combine(AppContext.BaseDirectory, "shaders");
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory).Parent; dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "shaders");
            if (File.Exists(Path.Combine(candidate, "mesh.frag")))
            {
                return candidate;
            }
        }

        return binShaders;
    }

    /// <summary>Resolves the cooked-content root (Contenu-2): <see cref="HostOptions.ContentRoot"/> if set, else
    /// <c>&lt;AppContext.BaseDirectory&gt;/content</c> — where the <c>CookAssets</c> MSBuild target ships the
    /// <c>.agmodel</c> blobs + <c>content.agmanifest</c>. Unlike shaders it is NOT walked up to a repo source: the
    /// cooked blobs only exist under <c>bin/</c>.</summary>
    internal static string ResolveContentRoot(HostOptions options)
        => options.ContentRoot ?? Path.Combine(AppContext.BaseDirectory, "content");
}

/// <summary>The disposables the strict teardown acts on. All nullable — <see cref="AppHost.RunClient"/> fills what
/// it managed to build (init may have thrown early inside <c>window.Loaded</c>). <see cref="Report"/> is the leak
/// report (step 9); <c>default</c> leaves it a no-op so a test can assert the label order.</summary>
internal readonly record struct TeardownTargets(
    FrameRenderer? FrameRenderer,
    GameWorld? World,
    ResourceRegistry? Registry,
    Renderer? Renderer,
    GraphicsDevice? Device,
    Swapchain? Swapchain,
    IWindow? Window)
{
    public Action? Report { get; init; }

    /// <summary>Audio-1 (spec D10): disposes the <c>AudioDevice</c> and folds <c>ReportLeaks()</c> into a
    /// caller-owned flag — <c>default</c> leaves it a no-op so a test can assert the label order unaffected.</summary>
    public Action? DisposeAudio { get; init; }

    /// <summary>Scene management spec, D9.6: cancels/joins an in-flight background scene load and disposes its
    /// never-activated registry — <c>default</c> leaves it a no-op so a test can assert the label order
    /// unaffected.</summary>
    public Action? DisposeInFlightLoader { get; init; }
}
