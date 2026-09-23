#version 450

// Vulkan RenderDevice for Noesis (vertical slice, branch spike/noesis-probe) — Path_Solid.
//
// Vertex format and uniform layout were NOT documented anywhere accessible (Noesis's reference
// shader source lives only in the Native SDK, a separate download from the Managed/NuGet packages
// this project uses) — reverse-engineered empirically instead of guessed, via a throwaway
// LoggingRenderDevice substituted for the real backend in tools/NoesisAotProbe, dumping raw Batch
// fields for a solid-color <Grid>:
//   - exactly one batch, Shader=Path_Solid, RenderState.BlendMode=Src (opaque)
//   - vertex = 12 bytes: vec2 position in RAW PIXEL SPACE (not NDC) + 4 unorm bytes color, straight
//     (non-premultiplied) RGBA — confirmed byte-for-byte: Red background produced (255,0,0,255)
//   - VertexUniform0 = 16 floats forming a standard orthographic pixel-to-NDC projection matrix
//     (diagonal terms 2/width, -2/height — the Y flip because pixel space grows downward and NDC
//     grows upward), PixelUniform0/1 and VertexUniform1 all empty
//   - the 16 floats are ROW-major (verified by hand: interpreting them as 4 rows of 4, row·vec4,
//     reproduces the expected 2/w*x-1, 1-2/h*y result; interpreting as GLSL's native column-major
//     layout does not).
//
// TWO bugs found live (branch spike/noesis-probe), both against this exact matrix:
//
// Bug 1 — majorness: a `layout(push_constant, row_major)` qualifier on this block did NOT actually
// flip the multiplication semantics with this shaderc/glslang version. Fixed by transposing the
// matrix on the CPU (System.Numerics.Matrix4x4.Transpose, in VulkanRenderDevice.DrawBatch) and using
// a plain default (column-major) mat4 here — transpose(M) stored column-major computes the same
// result as M applied row-major.
//
// Bug 2 — depth convention: with bug 1 fixed, X/Y were correct but the result STILL rendered nothing.
// The matrix's Z row (`0,0,2,-1`) is Noesis's OpenGL-convention row, producing clip-space Z in
// [-1,1] (z_ndc = -1 for every vertex here, since the model-space z input is always 0). Vulkan's clip
// volume requires Z in [0,1] — every vertex landed outside it, so the hardware clipper discarded the
// ENTIRE quad before the fragment shader ever ran (this is geometric near/far clipping, a completely
// separate stage from depth TESTING — DepthTest=false and no depth attachment do not disable it).
// Fixed by forcing gl_Position.z to a constant valid value below: this pipeline never depth-tests, so
// the actual Z value is irrelevant as long as it is not clipped.

layout(location = 0) in vec2 inPos;
layout(location = 1) in vec4 inColor;

layout(push_constant) uniform PushConstants {
    mat4 projection; // already transposed on the CPU side — see VulkanRenderDevice.DrawBatch
} pc;

layout(location = 0) out vec4 vColor;

void main() {
    gl_Position = pc.projection * vec4(inPos, 0.0, 1.0);
    // Bug 2 fix (see header): discard the OpenGL-convention Z entirely — this pipeline never
    // depth-tests (DepthTest=false, no depth attachment), so any value inside Vulkan's [0,1] near/far
    // clip range is fine.
    gl_Position.z = 0.0;

    // Premultiply unconditionally: the pipeline always blends PremultipliedAlpha (Agapanthe.Graphics
    // has no dynamic blend state — it is baked per-pipeline), which degrades to a plain overwrite
    // when alpha == 1 (Noesis's "Src"/opaque case, the only one this vertical slice has observed) and
    // is also correct for "SrcOver" batches this shader may see later without needing a second pipeline.
    vColor = vec4(inColor.rgb * inColor.a, inColor.a);
}
