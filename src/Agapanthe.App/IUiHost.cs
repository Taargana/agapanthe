using Agapanthe.Graphics;

namespace Agapanthe.App;

/// <summary>
/// A swappable rich-UI engine (Noesis today, something else later) — the contract that keeps
/// <see cref="AppHost"/>/<see cref="IGame"/>/<see cref="ISceneRecipe"/> free of any concrete UI middleware type.
/// Minimal on purpose: this milestone proves render + composition only (the vertical slice's only exercised
/// surface). Input forwarding, resize, and multi-document loading are real gaps, deliberately deferred rather than
/// guessed at with no second UI engine to validate the shape against.
/// </summary>
public interface IUiHost : IDisposable
{
    /// <summary>Advances the UI engine by one step (wall-clock seconds). Called once per rendered frame by
    /// <see cref="AppHost"/> — never by a scene recipe, which never sees this type either.</summary>
    void Tick(double deltaSeconds);

    /// <summary>This frame's rendered output, ready to composite — null until <see cref="Tick"/> has produced at
    /// least one frame. <see cref="AppHost"/>'s generic render system reads this and calls
    /// <see cref="Rendering.Renderer.DrawTexture"/>; it never names a concrete UI engine type.</summary>
    GpuImage? CurrentFrame { get; }
}
