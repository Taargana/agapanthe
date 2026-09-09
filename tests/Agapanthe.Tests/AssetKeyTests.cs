using Agapanthe.Core;

namespace Agapanthe.Tests;

/// <summary>Contenu-1 — the stable, path-based asset identity. Normalisation, rejection, and the
/// <c>None == default</c> collapse that lets one key type serve both the registry map and the snapshot table.</summary>
public sealed class AssetKeyTests
{
    // ── Test 1 — normalisation ────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("models\\x.glb", "models/x.glb")]
    [InlineData("  a/b  ", "a/b")]
    [InlineData("a//b", "a/b")]
    [InlineData("a\\\\b//c", "a/b/c")]
    [InlineData("models/DamagedHelmet.glb", "models/DamagedHelmet.glb")]
    public void Normalises(string input, string expected)
        => Assert.Equal(expected, new AssetKey(input).Value);

    // ── Test 2 — rejection ────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void RejectsNull() => Assert.Throws<ArgumentNullException>(() => new AssetKey(null!));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void RejectsEmptyOrWhitespace(string input)
        => Assert.Throws<ArgumentException>(() => new AssetKey(input));

    [Theory]
    [InlineData("a/../b")]
    [InlineData("a/./b")]
    [InlineData("/abs")]
    [InlineData("..")]
    [InlineData("///")]
    [InlineData("\\")]
    public void RejectsBadPaths(string input)
        => Assert.Throws<FormatException>(() => new AssetKey(input));

    // ── Contenu-2 — FromContentPath ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void FromContentPath_IsRelativeToRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "agcontent");
        Assert.Equal("models/DamagedHelmet.glb",
            AssetKey.FromContentPath(root, Path.Combine(root, "models", "DamagedHelmet.glb")).Value);
        Assert.Equal("models/sub/x.glb",
            AssetKey.FromContentPath(root, Path.Combine(root, "models", "sub", "x.glb")).Value);
    }

    [Fact]
    public void FromContentPath_RejectsPathOutsideRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "agcontent");
        Assert.Throws<ArgumentException>(
            () => AssetKey.FromContentPath(root, Path.Combine(Path.GetTempPath(), "elsewhere", "x.glb")));
    }

    // ── Test 3 — None is default ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void None_Is_Default()
    {
        Assert.Equal(default, AssetKey.None);
        Assert.True(AssetKey.None.IsNone);
        Assert.True(default(AssetKey).IsNone);
        Assert.Null(AssetKey.None.Value);
        Assert.Equal("<none>", AssetKey.None.ToString());
        Assert.NotEqual(AssetKey.None, new AssetKey("x"));
        Assert.False(new AssetKey("x").IsNone);
    }

    [Fact]
    public void MeshRefKey_None_Is_Default()
    {
        Assert.Equal(default, MeshRefKey.None);
        Assert.True(MeshRefKey.None.IsNone);
        Assert.True(new MeshRefKey(AssetKey.None, 0, 0).IsNone);
        Assert.False(new MeshRefKey(new AssetKey("m/a"), 1, 2).IsNone);
    }

    // ── Test 4 — equality + dictionary key ────────────────────────────────────────────────────────────────────

    [Fact]
    public void EqualityAndHash()
    {
        var a = new AssetKey("models/x.glb");
        var b = new AssetKey("models\\x.glb"); // normalises to the same
        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void CaseIsSignificant()
    {
        // Godot res:// model: normalisation touches separators/whitespace, never casing.
        Assert.NotEqual(new AssetKey("Models/X.glb"), new AssetKey("models/x.glb"));
        Assert.Equal("Models/X.glb", new AssetKey("Models\\X.glb").Value);
    }

    [Fact]
    public void UsableAsDictionaryKey_NoneAndDefaultCollapse()
    {
        var map = new Dictionary<AssetKey, int>
        {
            [new AssetKey("m/a")] = 1,
            [AssetKey.None] = 0,
        };

        Assert.Equal(1, map[new AssetKey("m\\a")]);
        Assert.Equal(0, map[default]);              // default resolves to the None entry
        Assert.Equal(2, map.Count);
        map[default(AssetKey)] = 99;                // does not add a second entry
        Assert.Equal(2, map.Count);
        Assert.Equal(99, map[AssetKey.None]);
    }
}
