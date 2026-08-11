namespace Agapanthe.Graphics;

/// <summary>
/// The colour-blend equation a pipeline writes its fragments with (UI-1). Until this existed, blending was hardcoded
/// off for every pipeline; <see cref="Opaque"/> is the default and reproduces that behaviour exactly, so adding this
/// knob changes nothing for existing passes.
/// </summary>
public enum BlendMode
{
    /// <summary>No blending: the fragment replaces the destination. The default, and what every pre-UI pass uses.</summary>
    Opaque = 0,

    /// <summary>
    /// Classic "over": <c>src·srcA + dst·(1−srcA)</c>. For colours whose RGB has <b>not</b> been multiplied by alpha.
    /// </summary>
    AlphaBlend,

    /// <summary>
    /// Premultiplied "over": <c>src·1 + dst·(1−srcA)</c>, i.e. the shader has already multiplied RGB by alpha.
    /// <para>
    /// This is what the UI uses. Multiplying before the blend (rather than letting the fixed-function stage do it)
    /// is what keeps a bilinearly-filtered glyph edge from picking up a halo: interpolating a premultiplied texel
    /// stays on the straight line between the two colours, while interpolating straight alpha does not.
    /// </para>
    /// </summary>
    PremultipliedAlpha,
}
