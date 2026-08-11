#version 450

// UI overlay fragment shader (UI-1). Resolves an SDF glyph (or a solid fill) into a premultiplied linear colour,
// blended over the already-tonemapped swapchain image.
//
// The pass runs AFTER the tonemap with LoadOp = Load, so the scene underneath is preserved; the pipeline uses
// premultiplied-alpha blending (src ONE, dst ONE_MINUS_SRC_ALPHA), which is why vColor arrives premultiplied.

#define FLAG_SDF_GLYPH 1u

layout(location = 0) in vec2 vUv;
layout(location = 1) in vec4 vColor; // linear, premultiplied (see ui.vert)
layout(location = 2) flat in uint vFlags;

layout(location = 0) out vec4 outColor;

layout(set = 0, binding = 0) uniform sampler2D atlas;

layout(push_constant) uniform PushConstants {
    vec2 invScreenSize;
    float sdfPixelRange; // 0 => coverage atlas (the documented bitmap fallback), else the SDF ramp width in texels
} pc;

void main() {
    float sample_ = texture(atlas, vUv).r;
    float coverage;

    if ((vFlags & FLAG_SDF_GLYPH) != 0u && pc.sdfPixelRange > 0.0) {
        // The cooker encodes the edge at 0.5 (stb's onedge_value = 128), so `sample_ - 0.5` is the signed distance,
        // positive inside the glyph.
        //
        // Antialiasing uses fwidth rather than the stored pixel range: fwidth measures how fast the distance changes
        // in SCREEN space, so the edge stays exactly one pixel wide at any text size, any DPI and any zoom — which
        // is the whole reason a distance field is worth baking. The stored range is kept as the coverage sentinel
        // and for future tuning.
        float distance = sample_ - 0.5;
        float width = max(fwidth(distance), 1e-5); // guard the degenerate case (huge magnification -> zero slope)
        coverage = clamp(smoothstep(-width, width, distance), 0.0, 1.0);
    } else {
        // Solid fill (the reserved white texel reads 1.0) or a coverage atlas: the sample IS the opacity.
        coverage = sample_;
    }

    // vColor is already premultiplied, so scaling the whole vector by coverage keeps that invariant: the result is
    // (rgb * a * coverage, a * coverage).
    outColor = vColor * coverage;
}
