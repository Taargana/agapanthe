#version 450

// UI overlay vertex shader (UI-1). Expands a storage buffer of quads into triangles with NO vertex buffer and NO
// index buffer: the pipeline declares a null VertexLayout and the pass issues one Draw(quadCount * 6), exactly the
// technique tonemap.vert uses for its fullscreen triangle. Six vertices per quad, two triangles.
//
// Everything the UI draws is one of these quads — a glyph samples the font atlas, a solid panel samples the atlas's
// reserved white texel — so an entire frame's UI is one pipeline, one descriptor set and one draw call.

// Mirrors Agapanthe.Ui.UiQuad byte for byte. The params vector is what makes the 48-byte std430 array stride match
// the CPU struct: without it the layouts desynchronise after the first quad (see UiQuad's remarks).
struct UiQuad {
    vec4 rect;    // minX, minY, maxX, maxY, in framebuffer pixels, y down from the top-left
    vec4 uvRect;  // u0, v0, u1, v1, normalised atlas space
    uvec4 params; // x = colour 0xRRGGBBAA (sRGB, NOT premultiplied), y = flags, zw = reserved
};

layout(std430, set = 0, binding = 1) readonly buffer Quads {
    UiQuad quads[];
};

layout(push_constant) uniform PushConstants {
    vec2 invScreenSize; // 1 / framebuffer size, so pixel coords reach clip space without a matrix
    float sdfPixelRange; // 0 => the atlas holds plain coverage, not a distance field
} pc;

layout(location = 0) out vec2 vUv;
layout(location = 1) out vec4 vColor; // LINEAR and PREMULTIPLIED — see below
layout(location = 2) flat out uint vFlags;

// sRGB -> linear (the exact IEC 61966-2-1 curve, not a pow(2.2) approximation).
//
// Why this is needed: the swapchain uses an sRGB format, so the hardware applies the sRGB OETF when the fragment is
// stored, and fixed-function blending runs on the LINEAR values. A shader must therefore output linear — which is
// what tonemap.frag already does for the scene. Writing sRGB bytes straight through would double-encode.
vec3 srgbToLinear(vec3 c) {
    bvec3 cutoff = lessThanEqual(c, vec3(0.04045));
    vec3 low = c / 12.92;
    vec3 high = pow((c + 0.055) / 1.055, vec3(2.4));
    return mix(high, low, cutoff);
}

void main() {
    uint quadIndex = uint(gl_VertexIndex) / 6u;
    uint corner = uint(gl_VertexIndex) % 6u;
    UiQuad q = quads[quadIndex];

    // Two triangles: (TL, TR, BL) and (BL, TR, BR). Counter-clockwise is irrelevant here — the pipeline culls
    // nothing — but the winding is kept consistent anyway.
    //   corner: 0 -> (0,0)  1 -> (1,0)  2 -> (0,1)  3 -> (0,1)  4 -> (1,0)  5 -> (1,1)
    vec2 unit = vec2(
        (corner == 1u || corner == 4u || corner == 5u) ? 1.0 : 0.0,
        (corner == 2u || corner == 3u || corner == 5u) ? 1.0 : 0.0);

    vec2 pixel = mix(q.rect.xy, q.rect.zw, unit);
    vUv = mix(q.uvRect.xy, q.uvRect.zw, unit);

    // Pixels -> clip space. Vulkan NDC is y-DOWN and the viewport keeps a top-left origin, so screen-space y maps
    // straight through with no flip (same reasoning as tonemap.vert).
    gl_Position = vec4((pixel * pc.invScreenSize) * 2.0 - 1.0, 0.0, 1.0);

    uint rgba = q.params.x;
    vec4 srgb = vec4(
        float((rgba >> 24) & 0xFFu),
        float((rgba >> 16) & 0xFFu),
        float((rgba >> 8) & 0xFFu),
        float(rgba & 0xFFu)) / 255.0;

    // ORDER MATTERS, and this is the order: convert RGB to linear FIRST, then multiply by alpha. Premultiplying in
    // sRGB space and converting afterwards tints every semi-transparent pixel — the classic halo bug. Alpha is
    // coverage, never gamma-encoded, so it is used as-is.
    vColor = vec4(srgbToLinear(srgb.rgb) * srgb.a, srgb.a);
    vFlags = q.params.y;
}
