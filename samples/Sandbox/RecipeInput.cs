using Agapanthe.App;
using Agapanthe.Core;
using Agapanthe.Engine;
using Agapanthe.Rendering;
using Silk.NET.Input;

namespace Sandbox;

/// <summary>
/// Input plumbing shared by the scene recipes: the <c>B</c>-key probe-spawn command. (The free-fly camera
/// handler this once also carried, <c>WireFreeFly</c>, was superseded by <c>Agapanthe.App.Scene.SceneInput.
/// EnableFreeFly</c> back in Contenu-3b — deleted here as a pre-existing 0-caller swept during Contenu-3c-3's
/// final cleanup, not new debt from this phase.)
/// </summary>
internal static class RecipeInput
{
    /// <summary>Opaque SimCommand.Kind bytes the Sandbox defines for itself (the engine never interprets them).</summary>
    public const byte SpawnProbeCommandKind = 1;

    /// <summary>Wires the <c>B</c> key to enqueue a probe-spawn command stamped for the next tick, carrying
    /// <c>camera.Position</c> — client context the declarative InputMap cannot supply. Drained inside Tick on the
    /// sim owner thread, then routed by the recipe's <c>ApplyCommand</c>.</summary>
    public static void WireProbeKey(SimSceneContext sim, PresentationSceneContext p)
    {
        var host = sim.Simulation;
        var camera = p.Camera;
        p.Window.KeyPressed += key =>
        {
            if (key == Key.B)
            {
                host.Commands.Enqueue(new SimCommand(host.TickIndex, SpawnProbeCommandKind, default, camera.Position, 0f, 0u));
            }
        };
    }
}
