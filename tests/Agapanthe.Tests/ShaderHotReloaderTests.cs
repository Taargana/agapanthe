using Agapanthe.Rendering;

namespace Agapanthe.Tests;

/// <summary>
/// GPU-free unit tests for the <see cref="ShaderHotReloader"/> flag / debounce / dedup / path-normalization
/// logic (M8-05). The real <see cref="FileSystemWatcher"/> is deliberately NOT exercised here (flaky in CI):
/// the reloader is constructed against a non-existent directory so no watcher is created, and change events are
/// injected directly via <see cref="ShaderHotReloader.NotifyChanged"/> — the same entry point the watcher
/// callbacks use.
/// </summary>
public sealed class ShaderHotReloaderTests
{
    // A directory that does not exist, so the constructor creates no OS watcher. NotifyChanged still works.
    private static string FakeFile(string name) =>
        Path.Combine(Path.GetTempPath(), "agapanthe-hotreload-none-" + Guid.NewGuid().ToString("N"), name);

    [Fact]
    public void HasPending_is_false_before_any_change()
    {
        using var reloader = new ShaderHotReloader([FakeFile("mesh.frag")], TimeSpan.Zero);
        Assert.False(reloader.HasPending);
        Assert.False(reloader.TryBeginReload(out var changed));
        Assert.Empty(changed);
    }

    [Fact]
    public void NotifyChanged_flags_and_drains_the_normalized_path_once_settled()
    {
        var file = FakeFile("mesh.frag");
        using var reloader = new ShaderHotReloader([file], TimeSpan.Zero);

        reloader.NotifyChanged(file);
        Assert.True(reloader.HasPending);

        Assert.True(reloader.TryBeginReload(out var changed));
        Assert.Single(changed);
        Assert.Equal(Path.GetFullPath(file), changed[0]);

        // Drained: the flag clears and a second drain returns nothing.
        Assert.False(reloader.HasPending);
        Assert.False(reloader.TryBeginReload(out var again));
        Assert.Empty(again);
    }

    [Fact]
    public void Debounce_window_blocks_the_reload_until_the_source_is_quiet()
    {
        var file = FakeFile("mesh.frag");
        // A long debounce that will not elapse during the test: the change is flagged but never drained.
        using var reloader = new ShaderHotReloader([file], TimeSpan.FromSeconds(30));

        reloader.NotifyChanged(file);
        Assert.True(reloader.HasPending); // still pending...
        Assert.False(reloader.TryBeginReload(out var changed)); // ...but not yet settled
        Assert.Empty(changed);
        Assert.True(reloader.HasPending); // flag survives a poll that did not drain
    }

    [Fact]
    public void Repeated_and_case_variant_paths_coalesce_into_a_single_entry()
    {
        var file = FakeFile("mesh.frag");
        using var reloader = new ShaderHotReloader([file], TimeSpan.Zero);

        reloader.NotifyChanged(file);
        reloader.NotifyChanged(file);
        if (OperatingSystem.IsWindows())
        {
            // Windows paths are case-insensitive: an upper-cased spelling must not create a second entry.
            reloader.NotifyChanged(file.ToUpperInvariant());
        }

        Assert.True(reloader.TryBeginReload(out var changed));
        Assert.Single(changed);
    }

    [Fact]
    public void A_change_after_a_drain_re_arms_the_reloader()
    {
        var file = FakeFile("mesh.frag");
        using var reloader = new ShaderHotReloader([file], TimeSpan.Zero);

        reloader.NotifyChanged(file);
        Assert.True(reloader.TryBeginReload(out _));
        Assert.False(reloader.HasPending);

        // A later edit (or a requeue of a half-written file) re-arms the flag and the next drain sees it.
        reloader.NotifyChanged(file);
        Assert.True(reloader.HasPending);
        Assert.True(reloader.TryBeginReload(out var changed));
        Assert.Single(changed);
    }

    [Fact]
    public void Multiple_watched_files_drain_together()
    {
        var a = FakeFile("mesh.frag");
        var b = FakeFile("mesh.vert");
        using var reloader = new ShaderHotReloader([a, b], TimeSpan.Zero);

        reloader.NotifyChanged(a);
        reloader.NotifyChanged(b);

        Assert.True(reloader.TryBeginReload(out var changed));
        Assert.Equal(2, changed.Count);
        Assert.Contains(Path.GetFullPath(a), changed);
        Assert.Contains(Path.GetFullPath(b), changed);
    }
}
