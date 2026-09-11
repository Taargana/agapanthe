using Agapanthe.Assets;
using Agapanthe.Assets.Scene;
using Agapanthe.World;

namespace Agapanthe.Scene;

/// <summary>
/// Contenu-3b — the per-side orchestration over <see cref="SceneMaterializer"/>. <see cref="LoadHeadless"/> is
/// what a dedicated server (and <c>HeadlessSim --scene</c>) calls: it populates the world and hands back the
/// physics settings / restore path for the caller to wire, and ignores lights / camera / environment (a headless
/// process has no renderer). The client (<c>AppHost</c>) calls the same materializer and then applies the
/// presentation blocks itself.
/// </summary>
public static class SceneLoader
{
    /// <summary>
    /// Materialises <paramref name="def"/> into <paramref name="world"/> with no GPU. The caller wires the result:
    /// <c>result.Physics</c> → <c>host.Add(Stage.Simulation, new PhysicsSystem(world, in ps))</c>;
    /// <c>result.RestorePath</c> → <c>sim.RequestRestore(rp, SnapshotAllocatorPolicy.AdoptFromHeader)</c>.
    /// </summary>
    public static MaterializeResult LoadHeadless(
        SceneDefinition def, AssetCatalog catalog, GameWorld world, float fixedDeltaSeconds)
        => SceneMaterializer.Materialize(def, catalog, world, fixedDeltaSeconds);
}
