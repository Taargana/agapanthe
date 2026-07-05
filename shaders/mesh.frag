#version 450

// Mesh fragment shader (M5): full metallic-roughness PBR. Cook-Torrance specular (GGX distribution, Smith
// height-correlated visibility, Schlick Fresnel) + energy-conserving Lambert diffuse, tangent-space normal
// mapping, metallic/roughness/AO/emissive maps, one directional key light plus up to four point lights, and
// a constant ambient placeholder (real IBL is M7). Output is LINEAR HDR with no clamp/tonemap — the tonemap
// pass (Renderer decision 3) owns exposure + ACES + the sRGB encode.
//
// Set 1 is unchanged from M4 (MaterialLayout, frozen 6-binding shape). Set 0 gains binding 1 (lights).

const float PI = 3.14159265359;

layout(location = 0) in vec3 fragColor;    // per-vertex color (glTF COLOR_0, default white)
layout(location = 1) in vec2 fragUv;
layout(location = 2) in vec3 worldNormal;
layout(location = 3) in vec4 worldTangent;
layout(location = 4) in vec3 worldPos;

layout(location = 0) out vec4 outColor;

// --- Set 0: per-frame camera + lights -----------------------------------------------------------------
// CameraUbo: byte-identical to mesh.vert and to Agapanthe.Rendering.CameraUniforms (144 bytes).
layout(set = 0, binding = 0) uniform CameraUbo {
    mat4 view;
    mat4 proj;
    vec4 position; // xyz = eye position (world space), w = padding
} camera;

// LightsUbo: byte-identical to Agapanthe.Rendering.LightsUniforms (std140, 176 bytes = 11 vec4).
//   offset  0  dirDirection      xyz = normalized travel direction (source -> surface), w padding
//   offset 16  dirColorIntensity rgb = color, w = intensity
//   offset 32  ambientPointCount rgb = constant ambient, w = active point count (read as int)
//   offset 48  pointData[8]      pairs: [2i] = xyz position / w range, [2i+1] = rgb color / w intensity
// The point lights are declared as a plain vec4[8] array rather than eight named vec4 fields BY DESIGN:
// GLSL cannot index named struct members, and a std140 vec4[N] array has 16-byte-aligned contiguous
// elements — byte-identical to the eight named Point{i}PositionRange/ColorIntensity fields (48 + 8*16 = 176).
layout(set = 0, binding = 1) uniform LightsUbo {
    vec4 dirDirection;
    vec4 dirColorIntensity;
    vec4 ambientPointCount;
    vec4 pointData[8];
} lights;

// --- Set 1: per-material PBR (MaterialLayout, frozen) --------------------------------------------------
layout(set = 1, binding = 0) uniform sampler2D baseColorTex;        // sRGB
layout(set = 1, binding = 1) uniform sampler2D normalTex;           // linear tangent-space normal map
layout(set = 1, binding = 2) uniform sampler2D metallicRoughnessTex; // linear, B = metallic, G = roughness
layout(set = 1, binding = 3) uniform sampler2D occlusionTex;        // linear, R = AO
layout(set = 1, binding = 4) uniform sampler2D emissiveTex;         // sRGB

// Must match Agapanthe.Rendering.MaterialUniforms (std140, four vec4 at offsets 0/16/32/48).
layout(set = 1, binding = 5) uniform MaterialUbo {
    vec4 baseColorFactor;        // linear RGBA tint
    vec4 mrno;                   // x metallic, y roughness, z normalScale, w occlusionStrength
    vec4 emissiveFactorStrength; // xyz emissive color, w strength
    vec4 alphaCutoffFlags;       // x cutoff, y alphaMode (0 = Opaque, 1 = Mask), zw reserved
} material;

// --- BRDF terms ---------------------------------------------------------------------------------------

// GGX / Trowbridge-Reitz normal distribution. alpha = roughness^2 (perceptual -> physical remap).
float distributionGGX(float NdotH, float alpha) {
    float a2 = alpha * alpha;
    float d = NdotH * NdotH * (a2 - 1.0) + 1.0;
    return a2 / (PI * d * d);
}

// Smith height-correlated visibility (Heitz 2014). This is the geometry term ALREADY DIVIDED by the
// 4*NdotL*NdotV Cook-Torrance denominator, so specular = D * V * F (no separate /(4 NdotL NdotV)):
//   V = 0.5 / (NdotL * lambda_v + NdotV * lambda_l)
//   lambda_v = sqrt(NdotV^2 (1-a^2) + a^2),  lambda_l = sqrt(NdotL^2 (1-a^2) + a^2)
float visibilitySmith(float NdotL, float NdotV, float alpha) {
    float a2 = alpha * alpha;
    float lambdaV = NdotL * sqrt(NdotV * NdotV * (1.0 - a2) + a2);
    float lambdaL = NdotV * sqrt(NdotL * NdotL * (1.0 - a2) + a2);
    return 0.5 / max(lambdaV + lambdaL, 1e-5);
}

// Schlick Fresnel.
vec3 fresnelSchlick(float cosTheta, vec3 f0) {
    float m = clamp(1.0 - cosTheta, 0.0, 1.0);
    float m2 = m * m;
    return f0 + (1.0 - f0) * (m2 * m2 * m); // (1-cos)^5
}

// One light's outgoing radiance contribution for surface (N, V, NdotV, albedo, metallic, alpha, f0).
vec3 shade(vec3 L, vec3 radiance, vec3 N, vec3 V, float NdotV,
           vec3 albedo, float metallic, float alpha, vec3 f0) {
    float NdotL = clamp(dot(N, L), 0.0, 1.0);
    if (NdotL <= 0.0) {
        // Back-facing light: contributes nothing — and skipping early both saves the whole BRDF
        // and dodges the L == -V antipode where normalize(L+V) would produce NaN (audit M5).
        return vec3(0.0);
    }

    vec3 H = normalize(L + V);
    float NdotH = clamp(dot(N, H), 0.0, 1.0);
    float VdotH = clamp(dot(V, H), 0.0, 1.0);

    float D = distributionGGX(NdotH, alpha);
    float Vis = visibilitySmith(NdotL, NdotV, alpha);
    vec3  F = fresnelSchlick(VdotH, f0);

    vec3 specular = D * Vis * F;

    // Energy conservation: light not reflected specularly (1 - F) is diffused, and metals have no diffuse.
    vec3 kd = (1.0 - F) * (1.0 - metallic);
    vec3 diffuse = kd * albedo / PI;

    return (diffuse + specular) * radiance * NdotL;
}

void main() {
    // --- Albedo + alpha mask (unchanged M4 semantics) -------------------------------------------------
    vec4 base = texture(baseColorTex, fragUv) * material.baseColorFactor * vec4(fragColor, 1.0);
    if (material.alphaCutoffFlags.y == 1.0 && base.a < material.alphaCutoffFlags.x) {
        discard;
    }
    vec3 albedo = base.rgb;

    // --- Tangent-space normal mapping (TBN) -----------------------------------------------------------
    // T re-orthogonalized against N (Gram-Schmidt); B from the glTF handedness (worldTangent.w = +/-1).
    // When the tangent is (near-)parallel to N — e.g. the +/-X faces of a mesh carrying the
    // placeholder tangent (1,0,0,1) — the Gram-Schmidt residual collapses to zero and
    // normalize(0) would poison the whole lighting with NaN (0*NaN = NaN, so even a neutral
    // normal map cannot recover). Fall back to any axis perpendicular to N (audit M5, MOYEN-1).
    vec3 N = normalize(worldNormal);
    vec3 tRes = worldTangent.xyz - N * dot(N, worldTangent.xyz);
    vec3 T = dot(tRes, tRes) > 1e-8
        ? normalize(tRes)
        : normalize(cross(N, abs(N.x) < 0.9 ? vec3(1.0, 0.0, 0.0) : vec3(0.0, 1.0, 0.0)));
    vec3 B = worldTangent.w * cross(N, T);
    vec3 nTs = texture(normalTex, fragUv).xyz * 2.0 - 1.0;
    nTs.xy *= material.mrno.z; // normalScale
    N = normalize(mat3(T, B, N) * nTs);

    // --- Metallic / roughness -------------------------------------------------------------------------
    vec4 mr = texture(metallicRoughnessTex, fragUv);
    float metallic = mr.b * material.mrno.x;
    // Roughness floor: sub-0.045 roughness produces near-mirror specular aliasing (specular firefly) that
    // the visibility term cannot integrate at one sample; clamp to a safe minimum.
    float roughness = clamp(mr.g * material.mrno.y, 0.045, 1.0);
    float alpha = roughness * roughness;

    // --- View vector + shared BRDF constants ----------------------------------------------------------
    vec3 V = normalize(camera.position.xyz - worldPos);
    float NdotV = max(dot(N, V), 1e-4); // epsilon guard against NaN at grazing angles
    vec3 f0 = mix(vec3(0.04), albedo, metallic); // dielectric 4% reflectance, metals tint with albedo

    // --- Direct lighting ------------------------------------------------------------------------------
    vec3 Lo = vec3(0.0);

    // Directional key light: L = surface -> light = -travelDirection. radiance = color * intensity.
    {
        vec3 L = -normalize(lights.dirDirection.xyz);
        vec3 radiance = lights.dirColorIntensity.rgb * lights.dirColorIntensity.w;
        Lo += shade(L, radiance, N, V, NdotV, albedo, metallic, alpha, f0);
    }

    // Point lights: inverse-square falloff with the glTF KHR_lights_punctual range window.
    int pointCount = int(lights.ambientPointCount.w);
    for (int i = 0; i < pointCount; ++i) {
        vec3 posRange = lights.pointData[2 * i].xyz;
        float range = lights.pointData[2 * i].w;
        vec3 colorIntensity = lights.pointData[2 * i + 1].rgb;
        float intensity = lights.pointData[2 * i + 1].w;

        vec3 toLight = posRange - worldPos;
        float dist = length(toLight);
        vec3 L = toLight / max(dist, 1e-4);

        // KHR_lights_punctual: attenuation = 1/d^2, optionally windowed to fade to 0 at `range`:
        //   window = range > 0 ? clamp(1 - (d/range)^4, 0, 1)^2 : 1
        float d2 = max(dist * dist, 1e-6);
        float attenuation = 1.0 / d2;
        if (range > 0.0) {
            float f = clamp(1.0 - pow(dist / range, 4.0), 0.0, 1.0);
            attenuation *= f * f;
        }

        vec3 radiance = colorIntensity * intensity * attenuation;
        Lo += shade(L, radiance, N, V, NdotV, albedo, metallic, alpha, f0);
    }

    // --- Ambient (IBL placeholder) + AO ---------------------------------------------------------------
    // AO occludes only the ambient/indirect term (standard); direct light is unshadowed by the AO map.
    float ao = mix(1.0, texture(occlusionTex, fragUv).r, material.mrno.w);
    vec3 ambient = lights.ambientPointCount.rgb * albedo * ao;

    // --- Emissive (added HDR, no tonemap here) --------------------------------------------------------
    vec3 emissive = texture(emissiveTex, fragUv).rgb * material.emissiveFactorStrength.rgb * material.emissiveFactorStrength.w;

    vec3 color = Lo + ambient + emissive;
    outColor = vec4(color, base.a);
}
