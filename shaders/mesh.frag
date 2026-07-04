#version 450

// Mesh fragment shader (M4 glTF): base color texture x baseColorFactor x vertex color, with alpha-mask
// discard. The set-1 layout is frozen in its final metallic-roughness PBR shape (MaterialLayout,
// architect decision session 3): bindings 1-4 (normal / metallic-roughness / occlusion / emissive) and
// the metallic/roughness/emissive UBO fields are declared here to document the frozen interface, but are
// unused in M4 — M5 activates normal mapping and PBR lighting without any descriptor-layout change.
// (A binding declared in the pipeline layout but not read by the shader is legal; the shaderc optimiser
// may drop the unread samplers from this stage's SPIR-V, which is fine — the layout is a valid superset.)
layout(location = 0) in vec3 fragColor;
layout(location = 1) in vec2 fragUv;
layout(location = 0) out vec4 outColor;

layout(set = 1, binding = 0) uniform sampler2D baseColorTex;
layout(set = 1, binding = 1) uniform sampler2D normalTex;             // M5
layout(set = 1, binding = 2) uniform sampler2D metallicRoughnessTex; // M5
layout(set = 1, binding = 3) uniform sampler2D occlusionTex;         // M5
layout(set = 1, binding = 4) uniform sampler2D emissiveTex;          // M5

// Must match Agapanthe.Rendering.MaterialUniforms (std140, four vec4 at offsets 0/16/32/48).
layout(set = 1, binding = 5) uniform MaterialUbo {
    vec4 baseColorFactor;        // linear RGBA tint
    vec4 mrno;                   // x metallic, y roughness, z normalScale, w occlusionStrength (M5)
    vec4 emissiveFactorStrength; // xyz emissive color, w strength (M5)
    vec4 alphaCutoffFlags;       // x cutoff, y alphaMode (0 = Opaque, 1 = Mask), zw reserved
} material;

void main() {
    vec4 base = texture(baseColorTex, fragUv) * material.baseColorFactor * vec4(fragColor, 1.0);

    // glTF alphaMode MASK: discard fragments below the cutoff. OPAQUE (mode 0) keeps every fragment.
    if (material.alphaCutoffFlags.y == 1.0 && base.a < material.alphaCutoffFlags.x) {
        discard;
    }

    outColor = base;
}
