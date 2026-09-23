#version 450

// Composites the Noesis-rendered offscreen texture into the main frame (vertical slice demo). Fullscreen
// triangle, same technique and the same "no V flip" convention as tonemap.vert — the Noesis render
// target is populated through the same CommandList.SetViewportScissor top-left-origin path.

layout(location = 0) out vec2 uv;

void main() {
    uv = vec2((gl_VertexIndex << 1) & 2, gl_VertexIndex & 2);
    gl_Position = vec4(uv * 2.0 - 1.0, 0.0, 1.0);
}
