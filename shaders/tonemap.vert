#version 450

// Fullscreen-triangle vertex shader (M5 tonemap pass, architect decision 3). No vertex buffer: the
// three vertices are generated from gl_VertexIndex and the pipeline declares a null VertexLayout
// (Draw(3)). One oversized triangle covers the whole viewport (its far corners sit outside NDC and the
// rasterizer clips to the screen) — cheaper than a two-triangle quad and free of a diagonal seam.
//
// Clip-space positions (Vulkan NDC: x,y in [-1,1], y DOWN):
//   idx 0 -> (-1,-1)   idx 1 -> ( 3,-1)   idx 2 -> (-1, 3)
//
// UV convention (uv = pos*0.5 + 0.5, i.e. the raw [0,2] values below):
//   idx 0 -> (0,0)   idx 1 -> (2,0)   idx 2 -> (0,2)
// NO V flip. Vulkan NDC is y-down and the HDR target was rendered under the same y-down viewport
// (CommandList.SetViewportScissor keeps a top-left origin), so screen-top (NDC y=-1) maps to uv.y=0 =
// the top texel row of the HDR image, which holds the top of the scene. Sampling is therefore 1:1 with
// rendering. Verified empirically at run: DamagedHelmet / BoxTextured render upright, matching the M4
// single-pass output (no upside-down image).

layout(location = 0) out vec2 uv;

void main() {
    // Bit trick: idx 0/1/2 -> (0,0)/(2,0)/(0,2). uv spans [0,2]; the on-screen half maps to [0,1].
    uv = vec2((gl_VertexIndex << 1) & 2, gl_VertexIndex & 2);
    gl_Position = vec4(uv * 2.0 - 1.0, 0.0, 1.0);
}
