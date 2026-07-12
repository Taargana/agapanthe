using System.Diagnostics;
using Agapanthe.Core;
using Agapanthe.Graphics;

namespace Agapanthe.Rendering;

/// <summary>
/// Watches the on-disk shader source files a set of reloadable passes were compiled from and flags when any of
/// them change, so <see cref="Renderer.PollShaderReload"/> can recompile the affected passes at the frame
/// boundary (shader hot reload, spec §3.6 / M8-05).
/// <para>
/// <b>Threading.</b> One <see cref="FileSystemWatcher"/> per distinct source directory raises its events on a
/// background thread. The event callback does the strict minimum and <b>never touches Vulkan</b>: it normalizes
/// the changed path, adds it to a set under <see cref="_lock"/>, records the event time and sets the
/// <see cref="_dirty"/> flag. The render thread polls <see cref="HasPending"/> — a plain <c>volatile</c> read
/// with <b>zero allocation</b> — every frame, and only takes the lock / allocates once something actually
/// changed (see <see cref="TryBeginReload"/>).
/// </para>
/// <para>
/// <b>Debounce.</b> Editors typically save via a burst of events (write-to-temp then rename, or several
/// <c>Changed</c> events). Reloading is deferred until the source has been quiet for <see cref="_debounceTicks"/>,
/// coalescing the burst into a single reload and avoiding reading a half-written file.
/// </para>
/// </summary>
internal sealed class ShaderHotReloader : IDisposable
{
    // Shader path comparison is OS-aware and lives in exactly ONE place (audit M8-09 M2): the pending set,
    // the pass source-file dedup (ReloadablePass), the include resolver and Renderer's file->pass mapping all
    // share ShaderIncludeResolver.PathComparer, so a path is never dropped by one layer and kept by another.
    private static StringComparer PathComparer => ShaderIncludeResolver.PathComparer;

    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly string[] _watchedFiles;

    private readonly Lock _lock = new();
    private readonly HashSet<string> _pending;
    private long _lastEventTimestamp;

    private readonly long _debounceTicks;

    // Set on the watcher thread, read every frame on the render thread. volatile so the render thread observes
    // the write promptly; the actual pending set is guarded by _lock, this is only the cheap early-out gate.
    private volatile bool _dirty;

    private bool _disposed;

    /// <param name="watchedFiles">
    /// Absolute paths of every shader source file to observe (the union of the passes' resolved
    /// <c>SourceFiles</c>). A <see cref="FileSystemWatcher"/> is created per distinct parent directory.
    /// </param>
    /// <param name="debounce">Quiet period a directory must observe before a change triggers a reload.</param>
    public ShaderHotReloader(IReadOnlyCollection<string> watchedFiles, TimeSpan debounce)
    {
        ArgumentNullException.ThrowIfNull(watchedFiles);

        _watchedFiles = watchedFiles
            .Select(Path.GetFullPath)
            .Distinct(PathComparer)
            .ToArray();
        _pending = new HashSet<string>(PathComparer);
        _debounceTicks = Math.Max(1, (long)(debounce.TotalSeconds * Stopwatch.Frequency));

        // One watcher per distinct directory the watched files live in. The source shaders are flat, but
        // includes may sit in sibling directories, so we key on each file's own directory.
        var directories = _watchedFiles
            .Select(f => Path.GetDirectoryName(f) ?? string.Empty)
            .Where(d => d.Length > 0)
            .Distinct(PathComparer);

        foreach (var directory in directories)
        {
            if (!Directory.Exists(directory))
            {
                // Deployed build with no editable source tree, or a stale path — nothing to watch here.
                Log.Warn($"ShaderHotReloader: shader directory '{directory}' does not exist; not watching it.");
                continue;
            }

            var watcher = new FileSystemWatcher(directory)
            {
                // LastWrite + Size catch in-place saves; FileName catches the write-temp-then-rename pattern.
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                IncludeSubdirectories = false,
            };
            watcher.Changed += OnChanged;
            watcher.Created += OnChanged;
            watcher.Renamed += OnRenamed;
            watcher.Error += OnError;
            watcher.EnableRaisingEvents = true;
            _watchers.Add(watcher);
        }

        if (_watchers.Count > 0)
        {
            Log.Info(
                $"ShaderHotReloader: watching {_watchedFiles.Length} shader file(s) across " +
                $"{_watchers.Count} directory(ies) for hot reload.");
        }
    }

    /// <summary>
    /// Cheap, allocation-free early-out gate for the per-frame poll: a plain <c>volatile</c> read. When it is
    /// <c>false</c> the caller must return immediately without allocating or locking (audit M8-09).
    /// </summary>
    public bool HasPending => _dirty;

    /// <summary>
    /// Called at the frame boundary once <see cref="HasPending"/> is <c>true</c>. Returns <c>false</c> (and an
    /// empty span) while the debounce window has not yet elapsed — the caller should simply try again next
    /// frame. When the source has settled it snapshots and clears the pending set, clears <see cref="_dirty"/>
    /// and returns the changed absolute paths. Only allocates on the settled path (a real reload, rare).
    /// </summary>
    public bool TryBeginReload(out IReadOnlyList<string> changed)
    {
        changed = [];
        lock (_lock)
        {
            if (!_dirty)
            {
                return false;
            }

            if (Stopwatch.GetTimestamp() - _lastEventTimestamp < _debounceTicks)
            {
                // Still within the quiet period — keep the flag set and reload on a later poll.
                return false;
            }

            if (_pending.Count == 0)
            {
                _dirty = false;
                return false;
            }

            var result = new string[_pending.Count];
            _pending.CopyTo(result);
            _pending.Clear();
            _dirty = false;
            changed = result;
            return true;
        }
    }

    /// <summary>
    /// Re-flags <paramref name="path"/> as changed. Used by the watcher callbacks and by
    /// <see cref="Renderer.PollShaderReload"/> to requeue a file that could not be read yet (half-written, mid
    /// atomic save): it re-arms the debounce so the reload is retried once the writer is done.
    /// </summary>
    public void NotifyChanged(string path)
    {
        var full = Path.GetFullPath(path);
        lock (_lock)
        {
            _pending.Add(full);
            _lastEventTimestamp = Stopwatch.GetTimestamp();
            _dirty = true;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var watcher in _watchers)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Changed -= OnChanged;
            watcher.Created -= OnChanged;
            watcher.Renamed -= OnRenamed;
            watcher.Error -= OnError;
            watcher.Dispose();
        }

        _watchers.Clear();
    }

    private void OnChanged(object sender, FileSystemEventArgs e) => NotifyChanged(e.FullPath);

    private void OnRenamed(object sender, RenamedEventArgs e) => NotifyChanged(e.FullPath);

    // The watcher's internal buffer overflowed (a flurry of changes was dropped). We no longer know which
    // files changed, so conservatively re-flag every watched file — the next poll re-checks them all.
    private void OnError(object sender, ErrorEventArgs e)
    {
        Log.Warn($"ShaderHotReloader: watcher error, re-checking all shader files. {e.GetException().Message}");
        lock (_lock)
        {
            foreach (var file in _watchedFiles)
            {
                _pending.Add(file);
            }

            _lastEventTimestamp = Stopwatch.GetTimestamp();
            _dirty = true;
        }
    }
}
