using Agapanthe.Core;
using Agapanthe.Graphics;
using Agapanthe.Rendering;

namespace Agapanthe.Tests;

/// <summary>Contenu-1 — the GPU-free AssetKey ⇄ handle bookkeeping <see cref="ResourceRegistry"/> composes.
/// Tested directly (over plain handle arrays) because the suite instantiates no <c>GraphicsDevice</c>.</summary>
public sealed class ModelKeyIndexTests
{
    private static MeshHandle M(int i) => new(i, 1);
    private static MaterialHandle T(int i) => new(i, 1);

    // ── Test 5 — Add rejects None + duplicate; a key frees on Remove ───────────────────────────────────────────

    [Fact]
    public void Add_RejectsNone()
        => Assert.Throws<ArgumentException>(() => new ModelKeyIndex().Add(AssetKey.None, [M(0)], [T(0)]));

    [Fact]
    public void Add_RejectsDuplicateKey_ThenFreesOnRemove()
    {
        var idx = new ModelKeyIndex();
        idx.Add(new AssetKey("m/a"), [M(0)], [T(0)]);
        Assert.Throws<GraphicsException>(() => idx.Add(new AssetKey("m/a"), [M(1)], [T(1)]));

        idx.Remove(new AssetKey("m/a"));
        idx.Add(new AssetKey("m/a"), [M(2)], [T(2)]); // no throw
    }

    // ── Test 6 — Identify then Resolve round-trips ────────────────────────────────────────────────────────────

    [Fact]
    public void IdentifyThenResolve_RoundTrips()
    {
        var idx = new ModelKeyIndex();
        idx.Add(new AssetKey("m/a"), [M(10), M(11)], [T(20), T(21)]);

        var key = idx.Identify(M(11), T(20));
        Assert.Equal(new MeshRefKey(new AssetKey("m/a"), 1, 0), key);

        var (mesh, mat) = idx.Resolve(key.Key, key.LocalMesh, key.LocalMat);
        Assert.Equal(M(11), mesh);
        Assert.Equal(T(20), mat);
    }

    // ── Test 7 — unknown / cross-model / out-of-range all throw ───────────────────────────────────────────────

    [Fact]
    public void Identify_UnknownHandle_Throws()
    {
        var idx = new ModelKeyIndex();
        idx.Add(new AssetKey("m/a"), [M(0)], [T(0)]);
        Assert.Throws<GraphicsException>(() => idx.Identify(new MeshHandle(999, 0), T(0)));
        Assert.Throws<GraphicsException>(() => idx.Identify(M(0), new MaterialHandle(999, 0)));
    }

    [Fact]
    public void Identify_CrossModelPair_Throws()
    {
        var idx = new ModelKeyIndex();
        idx.Add(new AssetKey("m/a"), [M(0)], [T(0)]);
        idx.Add(new AssetKey("m/b"), [M(1)], [T(1)]);
        Assert.Throws<GraphicsException>(() => idx.Identify(M(0), T(1)));
    }

    [Fact]
    public void Resolve_UnknownKeyOrOutOfRange_Throws()
    {
        var idx = new ModelKeyIndex();
        idx.Add(new AssetKey("m/a"), [M(0)], [T(0)]);
        Assert.Throws<GraphicsException>(() => idx.Resolve(new AssetKey("nope"), 0, 0));
        Assert.Throws<GraphicsException>(() => idx.Resolve(new AssetKey("m/a"), 5, 0));
        Assert.Throws<GraphicsException>(() => idx.Resolve(new AssetKey("m/a"), 0, 5));
    }

    // ── Test 8 — Remove then re-add resolves fresh handles (the bug the milestone fixes) ──────────────────────

    [Fact]
    public void RemoveThenReAdd_ResolvesFreshHandles()
    {
        var idx = new ModelKeyIndex();
        idx.Add(new AssetKey("m/a"), [M(1)], [T(1)]);            // generation 1
        Assert.Equal(new MeshRefKey(new AssetKey("m/a"), 0, 0), idx.Identify(M(1), T(1)));

        idx.Remove(new AssetKey("m/a"));
        Assert.Throws<GraphicsException>(() => idx.Identify(M(1), T(1))); // old handles now unknown

        idx.Add(new AssetKey("m/a"), [new MeshHandle(1, 2)], [new MaterialHandle(1, 2)]); // reload, gen 2
        var (mesh, mat) = idx.Resolve(new AssetKey("m/a"), 0, 0);
        Assert.Equal(new MeshHandle(1, 2), mesh);
        Assert.Equal(new MaterialHandle(1, 2), mat);
        Assert.NotEqual(M(1), mesh); // different generation — a load-order change re-resolves by KEY, not by luck
    }

    // ── audit — a fully-Invalid pair identifies as None (re-saving a resolver-less load must not throw) ─────────

    [Fact]
    public void Identify_InvalidPair_ReturnsNone()
    {
        var idx = new ModelKeyIndex();
        idx.Add(new AssetKey("m/a"), [M(0)], [T(0)]);
        Assert.Equal(MeshRefKey.None, idx.Identify(MeshHandle.Invalid, MaterialHandle.Invalid));
    }

    [Fact]
    public void Add_HandleAlreadyMappedToAnotherKey_Throws()
    {
        var idx = new ModelKeyIndex();
        idx.Add(new AssetKey("m/a"), [M(7)], [T(8)]);
        // A second model claiming a live handle already owned by "m/a" is an invariant break, not a silent overwrite.
        Assert.Throws<GraphicsException>(() => idx.Add(new AssetKey("m/b"), [M(7)], [T(9)]));
    }
}
