#version 450

// Shadow depth pass (M6): renders scene geometry from the directional light's viewpoint into a depth-only
// D32 target. Only the world position matters, so this shader declares ONLY location 0 even though the
// pipeline still binds the full Vertex.Layout (stride 60, all five attributes). A vertex attribute declared
// in the pipeline but NOT consumed by the shader is legal and silent in Vulkan — it is the reverse
// (consuming an undeclared attribute) that validation rejects. Reusing the mesh vertex buffer verbatim keeps
// the shadow pass allocation-free (no position-only geometry copy).
layout(location = 0) in vec3 inPosition;

// 128 bytes of push constants (no descriptor set — architect decision 7). Same proj*view convention as the
// camera: lightViewProj is the row-vector view·proj uploaded column-major, so `lightViewProj * model * pos`
// reproduces `p · model · view · proj` and bakes the Vulkan Y-flip + Z[0,1] (OrthographicVulkan).
layout(push_constant) uniform PushConstants {
    mat4 lightViewProj; // offset 0
    mat4 model;         // offset 64
} push;

void main() {
    gl_Position = push.lightViewProj * push.model * vec4(inPosition, 1.0);
}
