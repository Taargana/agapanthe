#version 450

// Skybox fragment shader (M7-05): samples the radiance environment cubemap along the reconstructed view ray.
// Output is LINEAR HDR with no tonemap — the tonemap pass (Renderer decision 3) applies exposure + ACES +
// sRGB to the sky exactly like the scene, so the background sits in the same tonemapped space as the model.

layout(set = 0, binding = 1) uniform samplerCube environmentMap;

layout(location = 0) in vec3 worldDir;

layout(location = 0) out vec4 outColor;

void main() {
    outColor = vec4(texture(environmentMap, normalize(worldDir)).rgb, 1.0);
}
