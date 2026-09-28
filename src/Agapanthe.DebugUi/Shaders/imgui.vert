#version 450

// ImGui debug-overlay vertex shader (D4). Vertices live in a storage buffer, read via gl_VertexIndex — same
// technique as ui.vert, but here gl_VertexIndex comes from a REAL GPU index buffer (vkCmdDrawIndexed), not a
// sequential 0..N counter: Vulkan defines gl_VertexIndex for an indexed draw as indexBuffer[element] +
// vertexOffset, so ImDrawCmd.VtxOffset maps directly onto DrawIndexed's own vertexOffset parameter — no manual
// index arithmetic needed here or on the CPU side.

struct ImGuiVertex {
    vec2 pos;
    vec2 uv;
    uint col;
};

layout(std430, set = 0, binding = 1) readonly buffer Vertices {
    ImGuiVertex verts[];
};

layout(push_constant) uniform PushConstants {
    vec2 invScreenSize; // 1 / framebuffer size — pixel coords reach clip space with no matrix, patron ui.vert
} pc;

layout(location = 0) out vec2 vUv;
layout(location = 1) out vec4 vColor; // linear, premultiplied — see srgbToLinear below

// sRGB -> linear (patron ui.vert, same IEC 61966-2-1 curve — the swapchain is sRGB, so the shader must output
// linear or blending double-encodes).
vec3 srgbToLinear(vec3 c) {
    bvec3 cutoff = lessThanEqual(c, vec3(0.04045));
    vec3 low = c / 12.92;
    vec3 high = pow((c + 0.055) / 1.055, vec3(2.4));
    return mix(high, low, cutoff);
}

void main() {
    ImGuiVertex v = verts[gl_VertexIndex];
    vUv = v.uv;

    // Vulkan NDC is y-down, viewport stays top-left origin — no flip, same reasoning as ui.vert/tonemap.vert.
    gl_Position = vec4((v.pos * pc.invScreenSize) * 2.0 - 1.0, 0.0, 1.0);

    // ImGui packs ImU32 as 0xAABBGGRR in memory (little-endian R first).
    uint rgba = v.col;
    vec4 srgb = vec4(
        float(rgba & 0xFFu),
        float((rgba >> 8) & 0xFFu),
        float((rgba >> 16) & 0xFFu),
        float((rgba >> 24) & 0xFFu)) / 255.0;

    // ORDER MATTERS (patron ui.vert): linear first, then multiply by alpha — premultiplying in sRGB space
    // tints every semi-transparent pixel (the halo bug).
    vColor = vec4(srgbToLinear(srgb.rgb) * srgb.a, srgb.a);
}
