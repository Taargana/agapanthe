namespace Agapanthe.Core;

/// <summary>
/// A stable, path-based identity for a render asset — the key a world snapshot stores so a drawable's
/// <c>MeshRef</c> survives a change in asset load <b>order</b> (Contenu-1; VS-1's deferred "Option 2").
/// <para>
/// It is <b>not</b> a file path the runtime opens, and <b>not</b> a content hash: renaming an asset is a
/// deliberate act that invalidates old saves, and re-cooking an asset's bytes must NOT change its identity.
/// A model loaded from disk keys as its logical path (<c>"models/DamagedHelmet.glb"</c>); a procedurally built
/// model is named by the caller (<c>"sandbox/planet-surface"</c>).
/// </para>
/// <para>
/// <see cref="None"/> is literally <c>default(AssetKey)</c> — the one state the constructor cannot produce,
/// with <see cref="Value"/> <c>null</c>. The constructor rejects null/empty/whitespace and <see cref="Normalise"/>
/// never yields <c>""</c>, so a value-bearing key can never equal <see cref="None"/>, and record-struct equality
/// gives <c>default == None</c> for free.
/// </para>
/// </summary>
public readonly record struct AssetKey
{
    /// <summary>The normalised key, e.g. <c>"models/DamagedHelmet.glb"</c>. <c>null</c> iff this is
    /// <see cref="None"/> / <c>default(AssetKey)</c>.</summary>
    public string? Value { get; }

    /// <param name="value">A logical asset path. Separators are normalised to <c>/</c> and the value is
    /// trimmed; <c>.</c> / <c>..</c> segments, a leading <c>/</c>, and empty/whitespace are rejected.
    /// <b>Case is significant</b> (Godot <c>res://</c> model): <c>"Models/X"</c> and <c>"models/x"</c> are
    /// distinct keys even on a case-insensitive filesystem — pick one casing per asset and keep it.</param>
    /// <exception cref="ArgumentException"><paramref name="value"/> is null, empty or whitespace.</exception>
    /// <exception cref="FormatException"><paramref name="value"/> has a <c>.</c>/<c>..</c> segment or a leading <c>/</c>.</exception>
    public AssetKey(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        Value = Normalise(value);
    }

    /// <summary>The sentinel "no asset" — how a headless or unresolvable <c>MeshRef</c> round-trips through a
    /// snapshot. <c>AssetKey.None == default(AssetKey)</c>.</summary>
    public static AssetKey None => default;

    /// <summary>True for <see cref="None"/> / <c>default</c>. A constructed key is never <see cref="IsNone"/>.</summary>
    public bool IsNone => string.IsNullOrEmpty(Value);

    public override string ToString() => IsNone ? "<none>" : Value!;

    // '\' → '/', trim, collapse runs of '/', then validate each segment.
    private static string Normalise(string value)
    {
        var normalised = value.Replace('\\', '/').Trim();

        while (normalised.Contains("//", StringComparison.Ordinal))
        {
            normalised = normalised.Replace("//", "/", StringComparison.Ordinal);
        }

        if (normalised.Length == 0 || normalised == "/")
        {
            // The input was only separators / whitespace (e.g. "\\" or "///").
            throw new FormatException($"AssetKey '{value}' has no path — it is only separators.");
        }

        if (normalised.StartsWith('/'))
        {
            throw new FormatException($"AssetKey '{value}' must be relative — a leading '/' is not allowed.");
        }

        foreach (var segment in normalised.Split('/'))
        {
            if (segment is "." or "..")
            {
                throw new FormatException($"AssetKey '{value}' must not contain a '{segment}' segment.");
            }
        }

        return normalised;
    }
}
