using System.Numerics;

namespace Agapanthe.Core;

/// <summary>
/// One drawable's replicated state (Net-1): its stable identity (<see cref="GlobalId"/>), its asset identity
/// (<see cref="AssetIdentity"/> — Contenu-3a's <c>MeshRefKey</c>, so a receiver can resolve GPU handles via its
/// own <c>AssetCatalog</c> regardless of load order, exactly like a snapshot reload does), and its current
/// world-space position/rotation/scale.
/// <para>
/// Lives in <c>Agapanthe.Core</c> — the shared, dependency-free base — so both <c>Agapanthe.World</c> (which
/// produces it, <c>GameWorld.DrainDirtyDrawables</c>) and <c>Agapanthe.Net</c> (which serializes it over the
/// wire) can reference it without either depending on the other.
/// </para>
/// <para>
/// <see cref="Rotation"/>/<see cref="Scale"/> come from <c>WorldTransform</c>'s baked matrix (decomposed);
/// <see cref="Position"/> is <c>WorldPosition</c>'s authoritative double-precision value, not the matrix's
/// (float, camera-relative) translation — the same "double is the source of truth" posture the engine has held
/// since Phase 2.
/// </para>
/// </summary>
public readonly record struct DrawableTransform(
    ulong GlobalId,
    MeshRefKey AssetIdentity,
    Double3 Position,
    Quaternion Rotation,
    float Scale);
