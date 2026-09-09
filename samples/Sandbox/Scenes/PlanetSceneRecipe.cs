using Agapanthe.App;

namespace Sandbox;

/// <summary>AGAPANTHE_SCENE=planet — the P3-M8 near+far reversed-Z reference scene: a planet at the world origin
/// and the Sun as an emissive sphere at 7.48e10 m, sun-only lighting, black space. Free-fly camera.</summary>
internal sealed class PlanetSceneRecipe : ISceneRecipe
{
    public string Name => "planet";

    // Also claims a bare no-scene run when AGAPANTHE_LOAD is set: a snapshot restore needs the planet assets
    // loaded first (the Option 1 seam), which only this family does. Matches the pre-extraction behaviour where
    // `loadMode` forced the planet scene. Reads the ENV directly (not HostOptions.LoadPath) — Matches has no
    // HostOptions; a programmatic LoadPath does not select this scene (see HostOptions.LoadPath, Contenu-3c).
    public bool Matches(string? sceneToken)
        => string.Equals(sceneToken, Name, StringComparison.OrdinalIgnoreCase)
           || (string.IsNullOrEmpty(sceneToken)
               && Environment.GetEnvironmentVariable("AGAPANTHE_LOAD") is { Length: > 0 });

    public void Build(SimSceneContext sim, PresentationSceneContext? presentation)
    {
        var p = presentation ?? throw new InvalidOperationException("planet scene requires a presentation context");
        var info = PlanetStage.Build(sim, p);
        PlanetStage.RequestRestoreIfRequested(sim, in info); // no scene-specific assets — planet/Sun is all it references
        SandboxCameras.FramePlanetCamera(
            p.Camera, p.Controller, p.Renderer, info.WorldOrigin, info.SunOrigin, info.PlanetRadius, info.SunTravelDir);
        RecipeInput.WireFreeFly(sim, p);
    }
}
