#version 450

// Vulkan RenderDevice for Noesis (vertical slice) — Path_AA_Solid: same as noesis_solid.vert, plus
// the antialiasing coverage attribute (vertex format PosColorCoverage). NOT independently verified
// against a real Path_AA_Solid batch (this vertical slice's proven demo — a solid-color <Grid> —
// only ever exercised Path_Solid); the vertex-format table (Shader.Vertex.Format, verified in
// RenderDevice.cs) says PosColorCoverage adds one float coverage after the PosColor layout, and the
// uniform convention is assumed consistent with Path_Solid's (same "family", differing only in
// antialiasing) — flag this as unverified until a batch with actual PPAA content is observed.

layout(location = 0) in vec2 inPos;
layout(location = 1) in vec4 inColor;
layout(location = 2) in float inCoverage;

// Two bugs found live and fixed on noesis_solid.vert, both apply here too: (1) row_major on
// push_constant did not take effect with this shaderc/glslang version — the matrix arrives
// pre-transposed from VulkanRenderDevice.DrawBatch instead; (2) the matrix's Z row is OpenGL-
// convention ([-1,1]), clipped entirely by Vulkan's [0,1] near/far volume — gl_Position.z is
// force-zeroed below since this pipeline never depth-tests.
layout(push_constant) uniform PushConstants {
    mat4 projection;
} pc;

layout(location = 0) out vec4 vColor;

void main() {
    gl_Position = pc.projection * vec4(inPos, 0.0, 1.0);
    gl_Position.z = 0.0;
    float a = inColor.a * inCoverage;
    vColor = vec4(inColor.rgb * a, a);
}
