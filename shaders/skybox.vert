#version 450

// Skybox vertex shader (M7-05): draws the environment cubemap as the scene background. A vertex-buffer-less
// fullscreen triangle (gl_VertexIndex 0..2) emitted at the FAR plane (z = w = 1), so with DepthTest
// LessOrEqual and depth-write OFF it survives only where no geometry wrote depth (the cleared background).
// The per-pixel view ray is reconstructed by unprojecting the far-plane point back to world space.

layout(set = 0, binding = 0) uniform CameraUbo {
    mat4 view;
    mat4 proj;
    vec4 position; // xyz = eye position (world space)
} camera;

layout(location = 0) out vec3 worldDir;

void main() {
    // (0,0) (2,0) (0,2) in UV -> a triangle covering the [-1,1] NDC square.
    vec2 uv = vec2((gl_VertexIndex << 1) & 2, gl_VertexIndex & 2);
    vec2 ndc = uv * 2.0 - 1.0;
    gl_Position = vec4(ndc, 1.0, 1.0); // z/w = 1 -> far plane (Vulkan Z[0,1])

    // std140 row-vector matrices are read transposed, so proj*view maps world->clip exactly like mesh.vert;
    // its inverse takes the far-plane clip point back to a world point, and the ray is that minus the eye.
    mat4 invViewProj = inverse(camera.proj * camera.view);
    vec4 world = invViewProj * vec4(ndc, 1.0, 1.0);
    worldDir = world.xyz / world.w - camera.position.xyz;
}
