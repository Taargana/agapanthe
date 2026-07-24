#version 450

// Skybox vertex shader (M7-05): draws the environment cubemap as the scene background. A vertex-buffer-less
// fullscreen triangle (gl_VertexIndex 0..2) emitted at the FAR plane, so with depth-write OFF it survives only
// where no geometry wrote depth (the cleared background). Under reversed-Z (P3-M8) the far plane is z = 0 and the
// depth test is GreaterOrEqual against a 0-cleared buffer. The per-pixel view ray is reconstructed from the
// projection and the view ROTATION only.

layout(set = 0, binding = 0) uniform CameraUbo {
    mat4 view;
    mat4 proj;
    vec4 position; // xyz = eye position (unused here; the block is shared with mesh.vert/frag)
} camera;

layout(location = 0) out vec3 worldDir;

void main() {
    // (0,0) (2,0) (0,2) in UV -> a triangle covering the [-1,1] NDC square.
    vec2 uv = vec2((gl_VertexIndex << 1) & 2, gl_VertexIndex & 2);
    vec2 ndc = uv * 2.0 - 1.0;
    gl_Position = vec4(ndc, 0.0, 1.0); // reversed-Z: z/w = 0 -> far plane

    // The background is a DIRECTIONAL effect: the ray depends only on where the camera looks, never on where it
    // IS. Reconstruct it in two independent steps so it does NOT depend on the eye position — which, under M4's
    // quantized origin, sits up to a cell (1024 m) from the frame origin. The old form (unproject to a world
    // POINT, then subtract the eye) did that subtraction at eye magnitude, so a distant eye lost precision and
    // the sampled cubemap texel drifted. Here: unproject the clip point to VIEW space with the inverse
    // projection (a direction from the view origin), then rotate view->world with the view's rotation transpose
    // (mat3(view) is world->view rotation; its transpose is the inverse). No translation, no eye — origin-exact.
    vec4 viewPos = inverse(camera.proj) * vec4(ndc, 0.0, 1.0);
    vec3 viewDir = viewPos.xyz / viewPos.w;
    worldDir = transpose(mat3(camera.view)) * viewDir;
}
