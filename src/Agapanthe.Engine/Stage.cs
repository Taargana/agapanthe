namespace Agapanthe.Engine;

/// <summary>
/// The ordered stages of one frame (P3-M2, decision D1). The whole point of this enum is that the frame ORDER —
/// propagate transforms, then aggregate bounds, then fit the light, then cull, then draw — stops being a sequence of
/// statements buried in an application's closure and becomes <b>data</b>: declared, inspectable, testable. Getting
/// that order wrong is not a crash, it is a shadow fit that is one frame stale, or a cull against a light volume that
/// does not exist yet — the kind of bug that survives every test and shows up as "the shadows flicker sometimes".
/// <para>
/// Systems registered in the same stage run in <b>registration order</b>; stages always run in the order below.
/// A <b>structural barrier</b> (deferred spawns/despawns applied) closes every stage, so no system ever observes the
/// entity storage mutating underneath its own iteration.
/// </para>
/// </summary>
public enum Stage
{
    /// <summary>
    /// Input sampling. <b>Empty on the engine side</b>: input lives in <c>Agapanthe.Platform</c>, which this project
    /// deliberately does not reference (see the .csproj). The application registers its own systems here — the stage
    /// exists so that the order has a name, not so that the engine fills it.
    /// </summary>
    Input = 0,

    /// <summary>Gameplay, animation, physics — anything that decides where things ARE this frame.</summary>
    Simulation = 1,

    /// <summary>
    /// Derived state, computed once the positions are final: transform propagation, world-bounds aggregation.
    /// Splitting this from <see cref="Simulation"/> is what guarantees the bounds are never one frame stale — the
    /// P3-M1 debt, made structural instead of a comment.
    /// </summary>
    PostSimulation = 2,

    /// <summary>
    /// Culling and drawing. The only stage whose systems see GPU types (<see cref="IRenderSystem"/>), and the only
    /// one that can be SKIPPED — the swapchain may be out of date (a resize, a minimize), and a frame is then simply
    /// not drawn. Simulation must never be skipped with it, which is why the two are ticked separately.
    /// </summary>
    Render = 3,
}
