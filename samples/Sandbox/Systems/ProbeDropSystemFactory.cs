using Agapanthe.App;
using Agapanthe.Assets.Scene;
using Agapanthe.Core;
using Agapanthe.Engine;
using Agapanthe.Scene;

namespace Sandbox;

/// <summary>
/// Contenu-3c — the <see cref="ISceneSystemFactory"/> for <see cref="SceneSystemKind.ProbeDrop"/>: resolves the
/// probe's render handles (its model is guaranteed already uploaded by <see cref="ClientScenePresenter"/>),
/// constructs a <see cref="ProbeDropSystem"/>, and wires its own input — the B key drops one probe on demand, via
/// the same <see cref="RecipeInput.WireProbeKey"/>/<see cref="RecipeInput.SpawnProbeCommandKind"/> plumbing
/// `PlanetDropSceneRecipe` used by hand.
/// </summary>
internal sealed class ProbeDropSystemFactory : ISceneSystemFactory
{
    public SceneSystemKind Kind => SceneSystemKind.ProbeDrop;

    public Stage Stage => Stage.Input;

    public ISystem Create(SceneSystem spec, SimSceneContext sim, PresentationSceneContext presentation, MaterializeResult result)
    {
        var template = SceneMaterializer.BuildRuntimeTemplate(spec.ProbeModel, spec.ProbeLocalMesh, spec.ProbeLocalMat, result.Models);
        var (mesh, material) = presentation.Registry.ResolveMeshRef(spec.ProbeModel, spec.ProbeLocalMesh, spec.ProbeLocalMat);
        var probeSpec = new ImportedEntitySpec(
            mesh, material, template.Position, template.RotationScale,
            template.BoundsCenter, template.BoundsRadius, template.Order, template.Identity);

        var dropper = new ProbeDropSystem(sim.World, in probeSpec, spec.Centre, spec.ProbeRadius, spec.Every);

        // Single-slot ApplyCommand/InputMap/SampleInput on SimulationHost — a scene declaring 2 input-wiring
        // systems would clobber one's assignment with the other's. Guarded centrally in SceneRecipe's dispatch
        // loop (audit finding: hoisted rather than duplicated per-factory), not here.
        sim.Simulation.ApplyCommand = (in SimCommand cmd) =>
        {
            if (cmd.Kind == RecipeInput.SpawnProbeCommandKind)
            {
                dropper.DropOne();
            }
        };
        RecipeInput.WireProbeKey(sim, presentation);

        return dropper;
    }
}
