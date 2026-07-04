#version 450

// Mesh vertex shader (M4 glTF). It consumes all FIVE interleaved vertex attributes so the pipeline's
// full VertexLayout (position/color/normal/uv/tangent) has no declared-but-unread input — an unconsumed
// vertex attribute is a validation warning. Because the vertex and fragment stages are compiled
// separately, the shaderc optimiser cannot strip an output interface variable (it can't see the next
// stage), so writing normal/tangent into worldNormal/worldTangent keeps those inputs alive even though
// the M4 fragment shader ignores them (they are passthroughs reserved for M5 normal mapping / lighting).
layout(location = 0) in vec3 inPosition;
layout(location = 1) in vec3 inColor;
layout(location = 2) in vec3 inNormal;
layout(location = 3) in vec2 inUv;
layout(location = 4) in vec4 inTangent; // xyz = tangent, w = handedness (glTF)

layout(location = 0) out vec3 fragColor;
layout(location = 1) out vec2 fragUv;
// Reserved for M5 (normal mapping / lighting); the M4 fragment shader does not declare these inputs.
layout(location = 2) out vec3 worldNormal;
layout(location = 3) out vec4 worldTangent;

// Set 0 = per-frame camera (spec §3.4). System.Numerics row-vector matrices are read by std140 as the
// column-vector transpose, so the usual proj * view * model multiply order applies here (matches cube.vert).
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

    // M4 approximation: mat3(model) rather than the inverse-transpose. Correct for rotation and uniform
    // scale; non-uniform scale is deferred to M5, where lighting actually consumes the normal.
    mat3 normalMatrix = mat3(push.model);
    worldNormal = normalMatrix * inNormal;
    worldTangent = vec4(normalMatrix * inTangent.xyz, inTangent.w);
}
