using System.Text.RegularExpressions;

namespace Agapanthe.Tests;

/// <summary>
/// Job-2b audit finding (F1, both auditors): the lenient <c>GameWorld.AssertOwnerThread</c> guard is only safe
/// because its two known call sites (<c>IsAlive</c>, <c>GetGlobalId</c>) are genuinely read-only, touching nothing
/// a pinned system might mutate while sharing a wave with a worker (see <c>AssertOwnerThread</c>'s own remarks for
/// the reproduced race a THIRD, less careful lenient call site — or a pinned system touching structural state —
/// would open). This is a source-scan regression pin, not a behavioral test: a future developer adding a new
/// <c>AssertOwnerThread()</c> call site must re-derive this safety argument from scratch and update this list
/// deliberately, rather than the guard's surface silently growing unnoticed.
/// </summary>
public sealed class GameWorldOwnerThreadGuardTests
{
    private static readonly string[] KnownSafeCallers = ["IsAlive", "GetGlobalId"];

    [Fact]
    public void LenientAssertOwnerThread_HasOnlyTheKnownSafeCallSites()
    {
        var worldDir = Path.Combine(RepositoryRoot(), "src", "Agapanthe.World");
        // Matches a bare `AssertOwnerThread();` call — NOT `AssertOwnerThreadStrict();` (a different guard, many
        // call sites, fine) and NOT the method's own `private void AssertOwnerThread(...)` definition line (which
        // takes a parameter, so never matches the exact `();` this pattern requires).
        var callPattern = new Regex(@"\bAssertOwnerThread\(\);", RegexOptions.Compiled);

        var callSites = new List<(string File, int Line)>();
        foreach (var file in Directory.EnumerateFiles(worldDir, "*.cs", SearchOption.AllDirectories))
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                if (callPattern.IsMatch(lines[i]))
                {
                    callSites.Add((Path.GetFileName(file), i));
                }
            }
        }

        Assert.True(
            callSites.Count == KnownSafeCallers.Length,
            $"Expected exactly {KnownSafeCallers.Length} lenient AssertOwnerThread() call site(s) " +
            $"({string.Join(", ", KnownSafeCallers)}), found {callSites.Count}: " +
            string.Join(", ", callSites.Select(c => $"{c.File}:{c.Line + 1}")));

        foreach (var (callSiteFile, line) in callSites)
        {
            var content = File.ReadAllLines(Path.Combine(worldDir, callSiteFile));

            // The call must live inside one of the known-safe methods — search back a small window for its
            // signature (a public method declaration naming one of them).
            var matchedCaller = KnownSafeCallers.FirstOrDefault(caller =>
            {
                for (var i = line; i >= 0 && i > line - 20; i--)
                {
                    if (content[i].Contains(caller, StringComparison.Ordinal))
                    {
                        return true;
                    }
                }

                return false;
            });

            Assert.True(
                matchedCaller is not null,
                $"{callSiteFile}:{line + 1} calls the lenient guard from outside the known-safe callers " +
                $"({string.Join(", ", KnownSafeCallers)}) — a new lenient call site must be deliberately reviewed " +
                "against AssertOwnerThread's own safety argument, then added to KnownSafeCallers here.");
        }
    }

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Agapanthe.slnx")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
