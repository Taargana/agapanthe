using Agapanthe.App;
using Agapanthe.Assets.Scene;
using Agapanthe.Core;
using Agapanthe.Engine;
using Agapanthe.Scene;
using Silk.NET.Input;

namespace Sandbox;

/// <summary>
/// Contenu-3c-2 — the <see cref="ISceneSystemFactory"/> for <see cref="SceneSystemKind.LandingChallenge"/>.
/// Resolves the probe's render handles the same way <see cref="ProbeDropSystemFactory"/> does, reads the
/// attractor off the scene's own <see cref="MaterializeResult.Physics"/> (<see cref="SceneCompiler"/> already
/// guaranteed at cook time that a <c>landing_challenge</c> system's scene has one — a null here would be a
/// materializer bug, not an authoring mistake, hence the throw rather than a silent default), constructs the
/// existing <see cref="LandingChallengeSystem"/> unchanged, and wires its own input: the <c>B</c> key (shared
/// <see cref="RecipeInput.SpawnProbeCommandKind"/> plumbing) plus <c>F5</c> quicksave to
/// <see cref="SceneSystem.QuicksavePath"/> — closing D7, the <c>AGAPANTHE_SAVE</c> host-level vs. F5-quicksave
/// name collision, by making the quicksave path authored data instead of an inline env-var read.
/// </summary>
internal sealed class LandingChallengeSystemFactory : ISceneSystemFactory
{
    public SceneSystemKind Kind => SceneSystemKind.LandingChallenge;

    public Stage Stage => Stage.PostSimulation;

    public ISystem Create(SceneSystem spec, SimSceneContext sim, PresentationSceneContext presentation, MaterializeResult result)
    {
        // Audit finding (csharp-lowlevel, 🟠): the invariant SceneCompiler.ToSystem enforces at cook time is an
        // ATTRACTOR (Mu > 0), not merely "a [physics] block exists" — a v3 blob with [physics] mu=0 would satisfy
        // `result.Physics is not null` yet carry a zero AttractorCenter/SurfaceRadius (PhysicsSettings.SurfaceRadius
        // is documented "meaningful only when Mu > 0"), silently landing every probe at 120 m from the world
        // origin instead of the planet. Mirror the cook-time check exactly, not a weaker runtime proxy for it.
        var physics = result.Physics is { Mu: > 0.0 } p
            ? p
            : throw new InvalidOperationException(
                "a landing_challenge system requires the scene's materialized Physics to carry an attractor (Mu > 0) — SceneCompiler should have rejected this at cook time.");

        var template = SceneMaterializer.BuildRuntimeTemplate(spec.ProbeModel, spec.ProbeLocalMesh, spec.ProbeLocalMat, result.Models);
        var (mesh, material) = presentation.Registry.ResolveMeshRef(spec.ProbeModel, spec.ProbeLocalMesh, spec.ProbeLocalMat);
        var probeSpec = new ImportedEntitySpec(
            mesh, material, template.Position, template.RotationScale,
            template.BoundsCenter, template.BoundsRadius, template.Order, template.Identity);

        var challenge = new LandingChallengeSystem(
            sim.World, presentation.Window, physics.AttractorCenter, physics.SurfaceRadius, spec.SurfaceBand,
            spec.ZoneCenter, spec.ZoneRadius, in probeSpec, spec.ProbeRadius, spec.DropHeight, spec.TargetCount, spec.ShotBudget);

        // Single-slot ApplyCommand — guarded centrally in SceneRecipe's dispatch loop, not here (see
        // ProbeDropSystemFactory's identical comment).
        sim.Simulation.ApplyCommand = (in SimCommand cmd) =>
        {
            if (cmd.Kind == RecipeInput.SpawnProbeCommandKind)
            {
                challenge.TryShoot(cmd.Vector);
            }
        };
        RecipeInput.WireProbeKey(sim, presentation);

        var world = sim.World;
        var quicksavePath = spec.QuicksavePath is { Length: > 0 } qsp ? qsp : "challenge.save";
        presentation.Window.KeyPressed += key =>
        {
            if (key != Key.F5)
            {
                return;
            }

            try
            {
                using var fs = File.Create(quicksavePath);
                world.Save(fs);
                Log.Info($"Sandbox: [challenge] quicksaved to '{quicksavePath}'. Relaunch with AGAPANTHE_LOAD={quicksavePath} to resume.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                Log.Warn($"Sandbox: [challenge] quicksave to '{quicksavePath}' failed: {ex.Message}");
            }
        };

        return challenge;
    }
}
