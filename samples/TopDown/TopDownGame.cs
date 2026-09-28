using Agapanthe.App;
using Agapanthe.Platform.App.Systems;
#if !MASTER
using Agapanthe.DebugUi;
using Silk.NET.Input;
#endif

namespace TopDown;

/// <summary>
/// Slice-2 — the 2nd dissimilar slice: a top-down orthographic app. Its whole point is to be a second,
/// independent <see cref="IGame"/> proving <see cref="AppHost"/>/<see cref="ISceneRecipe"/>/
/// <see cref="ISceneSystemFactory"/> are reusable outside <c>samples/Sandbox</c> — everything here mirrors
/// <c>Sandbox.SandboxGame</c>'s shape exactly, just with a single cooked scene and the shared
/// <see cref="DriveControlSystemFactory"/> (from <c>Agapanthe.Platform.App</c>, the same class Sandbox uses,
/// not a duplicate).
/// <para>
/// <see cref="IGame.Universe"/> stays <c>UniverseId.None</c> (dev-host convention, matching
/// <c>SandboxGame</c>).
/// </para>
/// </summary>
internal sealed class TopDownGame : IGame
{
    public string Title => "Agapanthe TopDown";

    public string DefaultScene => "topdown";

    public IReadOnlyList<ISceneRecipe> Scenes { get; } = [new SceneRecipe("topdown")];

    public IReadOnlyList<ISceneSystemFactory> SceneSystems { get; } = [new DriveControlSystemFactory()];

#if !MASTER
    // ImGui debug-overlay spec, D3: excluded from Master (the DebugUi ProjectReference itself is conditional,
    // != 'Master' — this override simply does not exist in that configuration's compiled output).
    public Func<SimSceneContext, PresentationSceneContext, IDisposable?>? ConfigureDebugTools =>
        (sim, presentation) =>
        {
            var system = new ImGuiDebugSystem(
                presentation.Device, presentation.Renderer, presentation.RenderList,
                sim.Simulation.Stats, presentation.Window, sim.Options.OverlayVisible);
            presentation.Orchestrator.Add(system);
            presentation.Window.KeyPressed += key =>
            {
                if (key == Key.F3)
                {
                    system.Toggle();
                }
            };
            return system;
        };
#endif
}
