using System.Numerics;
using Agapanthe.Core;

namespace Agapanthe.World;

/// <summary>
/// Per-drawable animation callback for <see cref="GameWorld.AnimateDrawables{TAnimator}"/>. Implement it as a
/// <c>struct</c> so the call is devirtualized and allocation-free.
/// </summary>
public interface IDrawableAnimator
{
    /// <summary>
    /// Advances one imported drawable, given its stable <paramref name="globalId"/>. Write the new world
    /// <paramref name="position"/> (double) and <paramref name="rotationScale"/> in place.
    /// <para>
    /// <b>Contract:</b> <paramref name="rotationScale"/> must carry rotation and scale ONLY — its translation row
    /// stays zero (the translation lives in <paramref name="position"/>, spec §3.3). A translation written here
    /// would corrupt the culling sphere (its centre is transformed by <paramref name="rotationScale"/>) AND be
    /// dropped by the render list (which overwrites the matrix's translation with <paramref name="position"/>) —
    /// culling one place, drawing another. Debug builds assert against it; in Release the divergence is
    /// <b>silent</b>, so honour the contract.
    /// </para>
    /// </summary>
    void Animate(ulong globalId, ref Double3 position, ref Matrix4x4 rotationScale);
}
