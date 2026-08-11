using System.Numerics;

namespace Agapanthe.Ui;

/// <summary>
/// One textured, tinted rectangle — the single primitive the whole UI is drawn from (UI-1). A glyph is a quad
/// sampling the font atlas; a solid panel or a profiler bar is a quad sampling the atlas's reserved white texel.
/// Because both go through one atlas and one pipeline, an entire frame's UI is <b>one draw call</b>.
/// <para>
/// Blittable and tightly packed: the renderer bulk-copies a span of these straight into a per-frame storage buffer,
/// and the vertex shader expands each into six vertices from <c>gl_VertexIndex</c> — no vertex buffer, no index
/// buffer, no per-quad CPU work.
/// </para>
/// </summary>
/// <remarks>
/// <b>The layout is 48 bytes on purpose, not 40.</b> The four fields below occupy 40 bytes, but GLSL's std430 rounds
/// an array's stride up to the struct's largest member alignment — <c>vec4</c>, i.e. 16 — giving a 48-byte stride on
/// the GPU. A 40-byte CPU struct would therefore desynchronise after the first quad and every glyph would read its
/// neighbour's data. The two reserved words below close that gap explicitly; the shader mirrors them as
/// <c>uvec4 params</c>. They are also the natural home for a future clip rect or layer index.
/// </remarks>
public readonly record struct UiQuad
{
    /// <summary>Flag bit: the quad is an SDF glyph and the fragment shader must threshold the sampled distance.
    /// When clear, the quad is a solid fill and the atlas sample is taken as plain opacity.</summary>
    public const uint FlagSdfGlyph = 1u;

    /// <summary>Size of one quad in bytes, on both sides of the CPU/GPU boundary. Asserted by a unit test.</summary>
    public const int SizeInBytes = 48;

    public UiQuad(Vector4 rect, Vector4 uvRect, uint rgba, uint flags)
    {
        Rect = rect;
        UvRect = uvRect;
        Rgba = rgba;
        Flags = flags;
        Reserved0 = 0u;
        Reserved1 = 0u;
    }

    /// <summary>Screen rectangle in framebuffer pixels: (minX, minY, maxX, maxY), Y down from the top-left.</summary>
    public Vector4 Rect { get; }

    /// <summary>Atlas rectangle in normalised texture space: (u0, v0, u1, v1).</summary>
    public Vector4 UvRect { get; }

    /// <summary>
    /// Colour packed as 0xRRGGBBAA, <b>sRGB-encoded and NOT premultiplied</b> — colours are authored the way a human
    /// writes them. The shader converts RGB to linear and only <i>then</i> multiplies by alpha, which is the order
    /// that avoids the classic halo on semi-transparent text; alpha itself is coverage and stays linear throughout.
    /// </summary>
    public uint Rgba { get; }

    /// <summary>Bit flags; see <see cref="FlagSdfGlyph"/>.</summary>
    public uint Flags { get; }

    /// <summary>std430 padding — see the type remarks. Reserved for a future clip rect / layer index.</summary>
    public uint Reserved0 { get; }

    /// <summary>std430 padding — see the type remarks.</summary>
    public uint Reserved1 { get; }
}
