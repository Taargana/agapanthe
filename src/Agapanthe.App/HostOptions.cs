using Agapanthe.Core;
using Agapanthe.World;

namespace Agapanthe.App;

/// <summary>
/// Host-level knobs for <see cref="AppHost.RunClient"/>. Every value defaults from the process environment
/// (<see cref="FromEnvironment"/>), so an existing <c>AGAPANTHE_*</c> run behaves identically; a caller that
/// passes an explicit instance fully controls the host (no path reads the environment behind its back).
/// </summary>
public sealed class HostOptions
{
    /// <summary>Which scene to build. Null → <c>AGAPANTHE_SCENE</c>, then <see cref="IGame.DefaultScene"/>. A
    /// non-null value here wins over the environment — the only way to pick a scene programmatically.</summary>
    public string? Scene { get; init; }

    /// <summary>Overrides <see cref="IGame.Universe"/> when set (<c>AGAPANTHE_UNIVERSE</c>, 32 hex chars).
    /// Null → the game's value wins.</summary>
    public UniverseId? Universe { get; init; }

    /// <summary>Auto-close after N rendered frames and feed a synthetic constant dt (<c>AGAPANTHE_MAX_FRAMES</c>);
    /// <c>&lt;= 0</c> disables it. <c>&gt; 0</c> is what makes a capture run reproducible tick-for-tick.</summary>
    public int MaxFrames { get; init; } = -1;

    /// <summary><c>AGAPANTHE_CAPTURE</c> — dump the tonemapped HDR target on the last frame.</summary>
    public string? CapturePath { get; init; }

    /// <summary><c>AGAPANTHE_CAPTURE_UI</c> — dump the presented swapchain image (the only capture that shows overlays).</summary>
    public string? CaptureUiPath { get; init; }

    /// <summary><c>AGAPANTHE_SAVE</c> — snapshot the fully-built world to this path before the first tick.</summary>
    public string? SavePath { get; init; }

    /// <summary><c>AGAPANTHE_LOAD</c> — a snapshot a recipe may request restoring (via
    /// <see cref="SimSceneContext.RequestRestore"/>); the host applies it after <c>Build</c>. Contenu-3a lifted
    /// this to a typed option so the restore guard-rail has it; other per-scene tuning env vars move to the
    /// declarative scene format in Contenu-3c.
    /// <para>
    /// <b>Note:</b> a scene's <see cref="ISceneRecipe.Matches"/> does not see <see cref="HostOptions"/>, so a
    /// <i>programmatic</i> <c>LoadPath</c> (as opposed to the env var) does not influence default-scene selection —
    /// a recipe that claims the default scene "when a load is requested" still reads the environment directly.
    /// Threading an enriched token is deferred to Contenu-3c.
    /// </para></summary>
    public string? LoadPath { get; init; }

    /// <summary>Root of the cooked-content tree (<c>AGAPANTHE_CONTENT</c>). Null →
    /// <c>&lt;AppContext.BaseDirectory&gt;/content</c>. Must hold a <c>content.agmanifest</c>.</summary>
    public string? ContentRoot { get; init; }

    /// <summary>Start the debug overlay visible (<c>AGAPANTHE_OVERLAY</c> != "0").</summary>
    public bool OverlayVisible { get; init; } = true;

    /// <summary>Bench mode: per-frame cull/alloc/tick stats logged every 60 frames (<c>AGAPANTHE_CULL_STATS</c>).</summary>
    public bool CullStats { get; init; }

    /// <summary>Assert the GPU cull kept exactly the CPU frustum test's visible set, logged after the capture
    /// (<c>AGAPANTHE_CULL_VERIFY</c> == "1").</summary>
    public bool VerifyCull { get; init; }

    /// <summary>Force one reload of every graphics pass before the first frame + log the per-pass wall time
    /// (<c>AGAPANTHE_SHADER_RELOAD_TEST</c>).</summary>
    public bool ShaderReloadTest { get; init; }

    /// <summary>Request GPU timestamp instrumentation (UI-3). Still gated by hardware capability
    /// (<c>GraphicsDevice.SupportsGpuTimestamps</c>) — this only controls whether the
    /// <c>Agapanthe.Rendering.Renderer</c> is ASKED to instrument, same "0 = off" shape as
    /// <see cref="OverlayVisible"/> (<c>AGAPANTHE_GPU_TIMESTAMPS</c> != "0"), the only way to
    /// force-exercise the disabled path on hardware that does support it.</summary>
    public bool GpuTimestampsEnabled { get; init; } = true;

    /// <summary>Builds options from the environment. <paramref name="read"/> is the accessor (defaults to
    /// <see cref="Environment.GetEnvironmentVariable(string)"/>); a test passes a dictionary lookup so it never
    /// mutates process state. A malformed <c>AGAPANTHE_UNIVERSE</c> logs a warning and leaves
    /// <see cref="Universe"/> null.</summary>
    public static HostOptions FromEnvironment(Func<string, string?>? read = null)
    {
        read ??= Environment.GetEnvironmentVariable;

        UniverseId? universe = null;
        if (read("AGAPANTHE_UNIVERSE") is { Length: > 0 } hex)
        {
            try
            {
                universe = UniverseId.Parse(hex);
            }
            catch (FormatException ex)
            {
                Log.Warn($"AppHost: AGAPANTHE_UNIVERSE '{hex}' is not a 32-hex universe id ({ex.Message}); ignoring it.");
            }
        }

        return new HostOptions
        {
            Scene = NullIfEmpty(read("AGAPANTHE_SCENE"))?.Trim(),
            Universe = universe,
            MaxFrames = int.TryParse(read("AGAPANTHE_MAX_FRAMES"), out var mf) ? mf : -1,
            CapturePath = NullIfEmpty(read("AGAPANTHE_CAPTURE")),
            CaptureUiPath = NullIfEmpty(read("AGAPANTHE_CAPTURE_UI")),
            SavePath = NullIfEmpty(read("AGAPANTHE_SAVE")),
            LoadPath = NullIfEmpty(read("AGAPANTHE_LOAD")),
            ContentRoot = NullIfEmpty(read("AGAPANTHE_CONTENT")),
            OverlayVisible = read("AGAPANTHE_OVERLAY") is not "0",
            CullStats = read("AGAPANTHE_CULL_STATS") is { Length: > 0 },
            VerifyCull = read("AGAPANTHE_CULL_VERIFY") is "1",
            ShaderReloadTest = read("AGAPANTHE_SHADER_RELOAD_TEST") is { Length: > 0 },
            GpuTimestampsEnabled = read("AGAPANTHE_GPU_TIMESTAMPS") is not "0",
        };

        static string? NullIfEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;
    }
}
