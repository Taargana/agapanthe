using System.Collections.Concurrent;

namespace Agapanthe.Core;

/// <summary>
/// Debug-time tracker of GPU resource lifetimes. Every tracked resource must be
/// registered on creation and unregistered on destruction; anything still alive
/// when <see cref="Report"/> runs is a leak and fails the run.
/// Enabled by default in DEBUG builds only, but always compiled so release
/// builds can opt in for leak hunts.
/// </summary>
public static class ResourceTracker
{
    private static readonly ConcurrentDictionary<string, int> LiveCounts = new();
    private static long _totalCreated;

    public static bool Enabled { get; set; } =
#if DEBUG
        true;
#else
        false;
#endif

    public static void Register(string resourceType)
    {
        if (!Enabled)
        {
            return;
        }

        LiveCounts.AddOrUpdate(resourceType, 1, static (_, count) => count + 1);
        Interlocked.Increment(ref _totalCreated);
    }

    public static void Unregister(string resourceType)
    {
        if (!Enabled)
        {
            return;
        }

        var count = LiveCounts.AddOrUpdate(resourceType, -1, static (_, c) => c - 1);
        if (count < 0)
        {
            Log.Error($"ResourceTracker: over-release of '{resourceType}' (live count went negative).");
        }
    }

    /// <summary>Reports a finalizer-detected leak: the resource was never disposed.</summary>
    public static void ReportFinalizerLeak(string resourceType)
    {
        if (Enabled)
        {
            Log.Error($"ResourceTracker: '{resourceType}' reached its finalizer without Dispose(). GPU handle leaked.");
        }
    }

    public static int LiveCount(string resourceType)
        => LiveCounts.TryGetValue(resourceType, out var count) ? count : 0;

    /// <summary>Logs live resources. Returns true when nothing leaked.</summary>
    public static bool Report()
    {
        if (!Enabled)
        {
            return true;
        }

        var leaks = LiveCounts.Where(static kv => kv.Value != 0).OrderBy(static kv => kv.Key).ToList();
        if (leaks.Count == 0)
        {
            Log.Info($"ResourceTracker: no leaks ({Interlocked.Read(ref _totalCreated)} resources created and destroyed).");
            return true;
        }

        foreach (var (type, count) in leaks)
        {
            Log.Error($"ResourceTracker: LEAK {type} x{count}");
        }

        return false;
    }

    public static void Reset()
    {
        LiveCounts.Clear();
        Interlocked.Exchange(ref _totalCreated, 0);
    }
}
