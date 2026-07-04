#version 450

// Normal (location 2) exists in the vertex stream but is not consumed until M5 (lighting);
// the pipeline only declares the attributes read here (position, color, uv).
layout(location = 0) in vec3 inPosition;
layout(location = 1) in vec3 inColor;
layout(location = 3) in vec2 inUv;

layout(location = 0) out vec3 fragColor;
layout(location = 1) out vec2 fragUv;

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
    fragUv = inUv;
}
