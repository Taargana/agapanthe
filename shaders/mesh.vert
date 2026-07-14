#version 450

// Mesh vertex shader (M5 PBR). It consumes all FIVE interleaved vertex attributes so the pipeline's
// full VertexLayout (position/color/normal/uv/tangent) has no declared-but-unread input — an unconsumed
// vertex attribute is a validation warning. Position, color, normal, uv and tangent are all forwarded to
// the fragment stage, which now runs the full Cook-Torrance lighting (normal mapping, view vector, etc.).
layout(location = 0) in vec3 inPosition;
layout(location = 1) in vec3 inColor;
layout(location = 2) in vec3 inNormal;
layout(location = 3) in vec2 inUv;
layout(location = 4) in vec4 inTangent; // xyz = tangent, w = handedness (glTF)

layout(location = 0) out vec3 fragColor;
layout(location = 1) out vec2 fragUv;
layout(location = 2) out vec3 worldNormal;   // world-space geometric normal (not yet normalized)
layout(location = 3) out vec4 worldTangent;  // xyz world-space tangent, w = handedness (passthrough)
layout(location = 4) out vec3 worldPos;      // world-space vertex position (for L / V in the frag stage)

// Set 0, binding 0 = per-frame camera (spec §3.4). Must be byte-identical to Agapanthe.Rendering.CameraUniforms
// (144 bytes: mat4 view @0, mat4 proj @64, vec4 position @128). System.Numerics row-vector matrices are read
// by std140 as their column-vector transpose, so the usual proj * view * model multiply order applies here.
// The block is declared identically in mesh.frag: a UBO block used in both stages must match exactly, so we
// declare `position` here too even though the vertex stage does not read it.
layout(set = 0, binding = 0) uniform CameraUbo {
    mat4 view;
    mat4 proj;
    vec4 position; // xyz = eye position (world space), w = padding
} camera;

// Set 0, binding 6 = per-instance model matrices (P3-M1). The renderer compacts the visible entities' baked
// (camera-relative) matrices into this storage buffer each frame, in sorted order; the draw offsets gl_InstanceIndex
// via firstInstance so instances.model[gl_InstanceIndex] is this instance's matrix. Read-only in the vertex stage
// (Vulkan-core, no feature). std430: a mat4[] is tightly packed at 64-byte stride, matching the CPU Matrix4x4[].
layout(std430, set = 0, binding = 6) readonly buffer InstanceTransforms {
    mat4 model[];
} instances;

void main() {
    mat4 model = instances.model[gl_InstanceIndex];
    vec4 world = model * vec4(inPosition, 1.0);
    gl_Position = camera.proj * camera.view * world;
    worldPos = world.xyz;

    fragColor = inColor;
    fragUv = inUv;

    // Normal matrix = inverse-transpose of the model's linear part (architect decision 5). Unlike mat3(model),
    // it transforms normals correctly under non-uniform scale (a pure rotation/uniform-scale reduces to
    // mat3(model) up to a scalar, which the fragment stage re-normalizes away). We transform the tangent with
    // the same matrix — strictly a tangent is a direction and would want mat3(model), but the difference is a
    // per-vertex scalar the fragment stage removes via normalize + Gram-Schmidt, so one matrix suffices.
    mat3 normalMatrix = transpose(inverse(mat3(model)));
    worldNormal = normalMatrix * inNormal;
    worldTangent = vec4(normalMatrix * inTangent.xyz, inTangent.w);
}
