using System.Numerics;
using Agapanthe.Assets;
using Agapanthe.Assets.Model;
using Agapanthe.Assets.Scene;
using Agapanthe.Core;
using Agapanthe.World;

namespace Agapanthe.Scene;

/// <summary>
/// Contenu-3b — turns a cooked <see cref="SceneDefinition"/> (a flat, already-expanded entity list) into live
/// entities in a <see cref="GameWorld"/>, with <b>no GPU</b>. The client and a dedicated server run the exact
/// same code here; only the <see cref="MeshRef"/> resolution afterwards differs (the client uploads models and
/// calls <see cref="GameWorld.ResolveMeshRefs"/>, a server keeps the <c>AssetRef</c> identity alone).
/// </summary>
public static class SceneMaterializer
{
    /// <summary>Production entry point: models come from the cooked catalog.</summary>
    public static MaterializeResult Materialize(
        SceneDefinition def, AssetCatalog catalog, GameWorld world, float fixedDeltaSeconds, bool spawnEntities = true)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return Materialize(def, catalog.LoadModel, world, fixedDeltaSeconds, spawnEntities);
    }

    /// <summary>Testable entry point: <paramref name="loadModel"/> decodes a model for a key (and throws — e.g.
    /// <see cref="AssetException"/> — for one it cannot).</summary>
    /// <param name="spawnEntities">Contenu-3c-2 — <c>false</c> when a restore is pending: every model the scene
    /// references is still decoded (a client must upload them before <c>GameWorld.Load</c> can resolve a
    /// restored entity's <c>AssetRef</c> against them), but no entity is spawned from the cooked definition,
    /// keeping the world empty for <c>GameWorld.Load</c> — which hard-throws on a non-empty world. Mirrors the
    /// pre-3c hand-coded recipes' <c>spawnEntities: !loadMode</c> parameter, now generalized to every
    /// <see cref="SceneDefinition"/>-driven scene instead of being reimplemented per recipe.</param>
    public static MaterializeResult Materialize(
        SceneDefinition def, Func<AssetKey, ModelAsset> loadModel, GameWorld world, float fixedDeltaSeconds, bool spawnEntities = true)
    {
        ArgumentNullException.ThrowIfNull(def);
        ArgumentNullException.ThrowIfNull(loadModel);
        ArgumentNullException.ThrowIfNull(world);

        // One decode per distinct model key — entities AND (Contenu-3c) scene systems' probe models. A probe-only
        // model (referenced by no SceneEntity) still needs to land here: ClientScenePresenter uploads everything
        // in MaterializeResult.Models, and a runtime-dropped probe needs an uploaded model to resolve its
        // MeshRef against.
        var models = new Dictionary<AssetKey, ModelAsset>();
        foreach (var entity in def.Entities)
        {
            if (!models.ContainsKey(entity.Model))
            {
                models[entity.Model] = loadModel(entity.Model);
            }
        }

        foreach (var system in def.Systems)
        {
            // Contenu-3c-3: DriveControl spawns nothing at runtime and authors no probe model (ProbeModel
            // relaxed from `required` specifically so this kind never has to fake one) — AssetKey.None must
            // never reach loadModel, which expects a real catalog key. But for every OTHER kind, None was
            // `required` through 3c-2 and is only reachable now via a forged/corrupt v4 blob (audit finding,
            // csharp-lowlevel, 🟡): silently skipping it here would defer the failure to a much-later, less
            // legible `MissingKeyException` deep in a client factory's `result.Models[spec.ProbeModel]` lookup.
            if (system.ProbeModel.IsNone)
            {
                if (system.Kind != SceneSystemKind.DriveControl)
                {
                    throw new AssetException(
                        $"scene '{def.Name}' declares a '{system.Kind}' system with no probe model — only "
                        + $"{SceneSystemKind.DriveControl} may omit one.");
                }
            }
            else if (!models.ContainsKey(system.ProbeModel))
            {
                models[system.ProbeModel] = loadModel(system.ProbeModel);
            }
        }

        // Contenu-3c-3: parallel to def.Entities — the EntityRef a body-entity's SpawnBody returned (null for a
        // drawable-only entity, or for every entity when spawnEntities is false and nothing was spawned at all).
        // A DriveControl factory indexes this by SceneSystem.ControlledEntityIndex to find the body it steers.
        var spawnedEntities = new EntityRef?[def.Entities.Count];

        uint order = 0;
        var entityIndex = 0;
        foreach (var entity in def.Entities)
        {
            var model = models[entity.Model];
            if (entity.LocalMesh < 0 || entity.LocalMesh >= model.Meshes.Count)
            {
                throw new AssetException(
                    $"scene '{def.Name}' references mesh {entity.LocalMesh} of '{entity.Model}', which has "
                    + $"{model.Meshes.Count} meshes.");
            }

            // Audit finding (csharp-lowlevel): LocalMesh was bounds-checked above but LocalMat was not — a
            // headless materialize has no resolver to catch a bad index later (unlike the client's
            // ModelKeyIndex.Resolve), so a forged/miscompiled LocalMat would silently pass through into the
            // AssetRef a server saves. Materials.Count itself is a valid index: it names the model's appended
            // engine-default material (SceneCompiler / SceneBuilder.BuildMaterials convention).
            if (entity.LocalMat < 0 || entity.LocalMat > model.Materials.Count)
            {
                throw new AssetException(
                    $"scene '{def.Name}' references material {entity.LocalMat} of '{entity.Model}', which has "
                    + $"{model.Materials.Count} material(s).");
            }

            var mesh = model.Meshes[entity.LocalMesh];
            var (position, rotationScale) = Compose(mesh, entity, def.WorldOrigin);

            var spec = new ImportedEntitySpec(
                MeshHandle.Invalid,
                MaterialHandle.Invalid,
                position,
                in rotationScale,
                mesh.BoundsCenter,
                mesh.BoundsRadius,
                order++,
                new MeshRefKey(entity.Model, entity.LocalMesh, entity.LocalMat),
                layer: entity.Layer);

            if (!spawnEntities)
            {
                entityIndex++;
                continue;
            }

            if (entity.Body is { } body)
            {
                spawnedEntities[entityIndex] = world.SpawnBody(in spec, body.Velocity, body.InverseMass, body.Restitution, body.Radius);
            }
            else
            {
                world.SpawnImported(in spec, castsShadow: entity.CastsShadow);
            }

            entityIndex++;
        }

        world.FlushStructuralChanges();

        // Audit finding (csharp-lowlevel, 🔴 blocking): Mu/AttractorCenter/SurfaceRadius were parsed, compiled,
        // and round-tripped through the binary format, but never actually applied here — every attractor scene
        // (planet-drop) silently ran uniform-gravity-with-a-zero-vector, i.e. no gravity at all. The capture gate
        // couldn't catch it (a motionless probe still renders as a scene with a probe in it).
        PhysicsSettings? physics = def.Physics is { } p
            ? p.Mu > 0.0
                ? new PhysicsSettings(p.Gravity, p.GroundY, fixedDeltaSeconds).WithAttractor(p.AttractorCenter, p.Mu, p.SurfaceRadius)
                : new PhysicsSettings(p.Gravity, p.GroundY, fixedDeltaSeconds)
            : null;

        return new MaterializeResult
        {
            Definition = def,
            Models = models,
            Physics = physics,
            RestorePath = def.Restore?.SnapshotPath,
            SpawnedEntities = spawnedEntities,
        };
    }

    /// <summary>
    /// Contenu-3c: a GPU-free <see cref="ImportedEntitySpec"/> template for a runtime-spawned drawable (a scene
    /// system's probe) — mesh/material handles left <see cref="MeshHandle.Invalid"/>/<see cref="MaterialHandle.Invalid"/>,
    /// <see cref="ImportedEntitySpec.Position"/> a placeholder (the caller stamps a fresh one at every spawn).
    /// <para>
    /// This is exactly what <see cref="Compose"/> + the entity-spawn path above already build for a
    /// <see cref="SceneEntity"/>, minus an authored placement (a probe's <em>scene</em> position is meaningless —
    /// it spawns wherever the owning system computes at runtime). A client-side <c>ISceneSystemFactory</c> calls
    /// this once at construction time, then resolves real handles via <c>ResourceRegistry.ResolveMeshRef</c> and
    /// builds the final template <see cref="ImportedEntitySpec"/> it hands to its <c>ISystem</c>.
    /// </para>
    /// </summary>
    public static ImportedEntitySpec BuildRuntimeTemplate(
        AssetKey model, int localMesh, int localMat, IReadOnlyDictionary<AssetKey, ModelAsset> models)
    {
        ArgumentNullException.ThrowIfNull(models);
        if (!models.TryGetValue(model, out var asset))
        {
            throw new AssetException($"'{model}' was not loaded into this scene's materialized models.");
        }

        if (localMesh < 0 || localMesh >= asset.Meshes.Count)
        {
            throw new AssetException($"'{model}' has {asset.Meshes.Count} mesh(es); mesh index {localMesh} is out of range.");
        }

        // Audit finding (csharp-lowlevel, 🟠): mirrors the entity path's LocalMat check above — Materials.Count
        // itself is a valid index (the appended engine-default material). No caller in this codebase reaches
        // this unvalidated today (ResolveMeshRef would throw first), but this is a public method with no such
        // documented contract, and 3c-2's LandingChallenge factory (or a future server-side spawner) could.
        if (localMat < 0 || localMat > asset.Materials.Count)
        {
            throw new AssetException($"'{model}' has {asset.Materials.Count} material(s); material index {localMat} is out of range.");
        }

        var mesh = asset.Meshes[localMesh];
        var world = mesh.WorldTransform;
        var rotationScale = world;
        rotationScale.M41 = 0f;
        rotationScale.M42 = 0f;
        rotationScale.M43 = 0f;

        return new ImportedEntitySpec(
            MeshHandle.Invalid, MaterialHandle.Invalid, Double3.Zero, in rotationScale,
            mesh.BoundsCenter, mesh.BoundsRadius, order: 0, new MeshRefKey(model, localMesh, localMat));
    }

    // Composes the mesh's own baked transform (model-local) with the entity's scene placement. When the entity
    // sits at identity rotation / unit scale — every 3b scene except a future rotated prefab — this reduces to the
    // exact matrix the render path (ResourceRegistry.Load → SpawnGrid) produces, so the cooked-scene capture is
    // byte-identical to a direct model load (AW-022). Deviation from spec §3.7 step 2, which dropped the mesh
    // translation: a multi-node model (MetalRoughSpheres) would collapse to the origin without it.
    private static (Double3 Position, Matrix4x4 RotationScale) Compose(MeshAsset mesh, SceneEntity entity, Double3 worldOrigin)
    {
        var world = mesh.WorldTransform;
        var meshTranslation = new Double3(world.M41, world.M42, world.M43);
        var meshRotationScale = world;
        meshRotationScale.M41 = 0f;
        meshRotationScale.M42 = 0f;
        meshRotationScale.M43 = 0f;

        var identityPlacement =
            entity.Rotation == Quaternion.Identity && entity.Scale == 1f;

        if (identityPlacement)
        {
            return (worldOrigin + entity.Position + meshTranslation, meshRotationScale);
        }

        var placement = Matrix4x4.CreateScale(entity.Scale) * Matrix4x4.CreateFromQuaternion(entity.Rotation);
        var rotationScale = meshRotationScale * placement;
        var position = worldOrigin + entity.Position
            + new Double3(Vector3.Transform(meshTranslation.ToVector3(Double3.Zero), placement));
        return (position, rotationScale);
    }
}
