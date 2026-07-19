#version 450

// Shadow depth pass (M6): renders scene geometry from the directional light's viewpoint into a depth-only
// D32 target. Only the world position matters, so this shader declares ONLY location 0 even though the
// pipeline still binds the full Vertex.Layout (stride 60, all five attributes). A vertex attribute declared
// in the pipeline but NOT consumed by the shader is legal and silent in Vulkan — it is the reverse
// (consuming an undeclared attribute) that validation rejects. Reusing the mesh vertex buffer verbatim keeps
// the shadow pass allocation-free (no position-only geometry copy).
layout(location = 0) in vec3 inPosition;

// lightViewProj travels as a 64-byte push constant (constant across the pass). Same proj*view convention as the
// camera: it is the row-vector view·proj uploaded column-major, so `lightViewProj * model * pos` reproduces
// `p · model · view · proj` and bakes the Vulkan Y-flip + Z[0,1] (OrthographicVulkan).
// batchOffset (P3-M4) is pushed after lightViewProj (offset 64): a draw-indirect command cannot carry a
// firstInstance without the drawIndirectFirstInstance feature, so the batch's start offset into the instance SSBO
// travels here and is added to gl_InstanceIndex.
layout(push_constant) uniform PushConstants {
    mat4 lightViewProj;              // offset 0
    layout(offset = 64) uint batchOffset;
} push;

// Set 0, binding 0 = per-instance model matrices (P3-M1): the renderer compacts the visible casters' baked
// matrices here in sorted order. std430 mat4[] is 64-byte-strided, matching the CPU Matrix4x4[].
layout(std430, set = 0, binding = 0) readonly buffer InstanceTransforms {
    mat4 model[];
} instances;

void main() {
    gl_Position = push.lightViewProj * instances.model[gl_InstanceIndex + push.batchOffset] * vec4(inPosition, 1.0);
}
