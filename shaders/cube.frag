#version 450

layout(location = 0) in vec3 fragColor;
layout(location = 1) in vec2 fragUv;
layout(location = 0) out vec4 outColor;

// Set 1 = per-material data (spec §3.4). The checkerboard base color was uploaded through the
// staging path with a full mip chain and is sampled here, then modulated by the per-face vertex
// color so each face keeps its tint.
layout(set = 1, binding = 0) uniform sampler2D baseColor;

void main() {
    outColor = texture(baseColor, fragUv) * vec4(fragColor, 1.0);
}
