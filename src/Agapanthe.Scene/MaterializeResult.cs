using Agapanthe.Assets.Model;
using Agapanthe.Assets.Scene;
using Agapanthe.Core;
using Agapanthe.World;

namespace Agapanthe.Scene;

/// <summary>
/// What <see cref="SceneMaterializer.Materialize(SceneDefinition, System.Func{AssetKey, ModelAsset}, GameWorld, float)"/>
/// produced (Contenu-3b): the world is already populated and flushed; this carries what the <b>caller</b> still has
/// to wire.
/// </summary>
public sealed record MaterializeResult
{
    /// <summary>The scene that was materialised (echoed so a caller need not thread it separately).</summary>
    public required SceneDefinition Definition { get; init; }

    /// <summary>Every distinct model the scene referenced, decoded once. The client uploads these to the GPU and
    /// then calls <see cref="GameWorld.ResolveMeshRefs"/>; a headless caller ignores them.</summary>
    public required IReadOnlyDictionary<AssetKey, ModelAsset> Models { get; init; }

    /// <summary>The physics settings the scene's <c>[physics]</c> block asks for, with the caller-supplied fixed
    /// step folded in — <c>null</c> when the scene has no <c>[physics]</c>. The caller constructs the
    /// <c>PhysicsSystem</c> (an <c>Agapanthe.Engine</c> type this GPU-free assembly must not name).</summary>
    public PhysicsSettings? Physics { get; init; }

    /// <summary>A snapshot path the scene's <c>[restore]</c> block asks to load after materialisation, or
    /// <c>null</c>. No 3b scene uses it; the caller (<c>SimSceneContext.RequestRestore</c>) honours it.</summary>
    public string? RestorePath { get; init; }
}
