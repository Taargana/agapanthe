#version 450

// Normal (location 2) and uv (location 3) exist in the vertex stream but are not consumed
// until M5/M3; the pipeline only declares the attributes read here.
layout(location = 0) in vec3 inPosition;
layout(location = 1) in vec3 inColor;

layout(location = 0) out vec3 fragColor;

// Set 0 = per-frame data (spec §3.4). Matrices are uploaded from System.Numerics
// row-vector form; std140 column-major reads them as the column-vector transpose,
// so the usual proj * view * model order applies here.
layout(set = 0, binding = 0) uniform CameraUbo {
    mat4 view;
    mat4 proj;
} camera;

layout(push_constant) uniform PushConstants {
    mat4 model;
} push;

void main() {
    gl_Position = camera.proj * camera.view * push.model * vec4(inPosition, 1.0);
    fragColor = inColor;
}
