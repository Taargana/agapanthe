using Agapanthe.Assets.Scene;
using Agapanthe.Engine;
using Agapanthe.Scene;

namespace Agapanthe.App;

/// <summary>
/// Contenu-3c — the client-side scene-system factory registry's per-kind unit. A game (<see cref="IGame"/>)
/// registers one factory per <see cref="SceneSystemKind"/> it knows how to construct; <see cref="SceneRecipe"/>
/// dispatches a scene's <c>Systems</c> list through <see cref="PresentationSceneContext.SceneSystemFactories"/>
/// by matching <see cref="Kind"/>, then calls <see cref="Create"/> and attaches the result via
/// <c>sim.AddSystem(Stage, ...)</c>.
/// <para>
/// <paramref name="presentation"/>-typed as non-nullable by design: scene systems are a client-only concept
/// (window/camera-coupled — <c>ProbeDropSystem</c>/<c>LandingChallengeSystem</c> both wire keyboard input), so a
/// factory never needs its own <c>?? throw</c> guard — <see cref="SceneRecipe"/> already refuses a scene that
/// declares systems with no presentation before this is ever called. The registry itself lives on
/// <see cref="PresentationSceneContext"/>, not <see cref="SimSceneContext"/> (audit finding, Contenu-3c-1's
/// double audit): a factory's own <see cref="Create"/> names <see cref="PresentationSceneContext"/>, so putting
/// the registry on the headless-safe context would transitively re-admit a GPU/window-coupled type there.
/// </para>
/// <para>
/// There is deliberately no separate "command-handler" registry (a genuine backlog wording, rejected in the
/// interview): each factory wires its own input inline, since the 2 known systems' commands are 1:1 coupled to
/// the system consuming them, not an independently reusable concept.
/// </para>
/// </summary>
public interface ISceneSystemFactory
{
    /// <summary>Which <see cref="SceneSystem.Kind"/> this factory constructs.</summary>
    SceneSystemKind Kind { get; }

    /// <summary>Which <see cref="Stage"/> the constructed system runs on (e.g. <see cref="Stage.Input"/> for a
    /// spawner that ticks every N frames, <see cref="Stage.PostSimulation"/> for one that reads post-physics
    /// state).</summary>
    Stage Stage { get; }

    /// <summary>Constructs the concrete <see cref="ISystem"/> for <paramref name="spec"/> and wires whatever
    /// input it needs. <paramref name="result"/> is the same <see cref="MaterializeResult"/> the scene's world
    /// population came from — <c>result.Models[spec.ProbeModel]</c> is guaranteed already uploaded by
    /// <see cref="ClientScenePresenter"/> before this runs.</summary>
    ISystem Create(SceneSystem spec, SimSceneContext sim, PresentationSceneContext presentation, MaterializeResult result);
}
