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

// LightsUbo: byte-identical to Agapanthe.Rendering.LightsUniforms (std140, 240 bytes = 11 vec4 + 1 mat4).
//   offset   0  dirDirection      xyz = normalized travel direction (source -> surface), w padding
//   offset  16  dirColorIntensity rgb = color, w = intensity
//   offset  32  ambientPointCount rgb = constant ambient, w = active point count (read as int)
//   offset  48  pointData[8]      pairs: [2i] = xyz position / w range, [2i+1] = rgb color / w intensity
//   offset 176  lightViewProj     directional shadow map view·proj (world -> light clip, Y-flip + Z[0,1] baked)
// The point lights are declared as a plain vec4[8] array rather than eight named vec4 fields BY DESIGN:
// GLSL cannot index named struct members, and a std140 vec4[N] array has 16-byte-aligned contiguous
// elements — byte-identical to the eight named Point{i}PositionRange/ColorIntensity fields (48 + 8*16 = 176).
layout(set = 0, binding = 1) uniform LightsUbo {
    vec4 dirDirection;
    vec4 dirColorIntensity;
    vec4 ambientPointCount;
    vec4 pointData[8];
    mat4 lightViewProj;
} lights;

// Set 0, binding 2 = the directional shadow map (M6). A PLAIN sampler2D, not a sampler2DShadow: MoltenVK's
// VK_KHR_portability_subset reports mutableComparisonSamplers = FALSE, so a hardware comparison sampler bound
// through a descriptor write is rejected on Apple silicon. Instead we read the stored depth and compare it in
// directionalShadow (manual 3x3 PCF). The sampler is Nearest (linear would blend raw depth before the compare)
// with ClampToEdge; out-of-frustum lookups are rejected in the shader, so we never rely on the border color.
layout(set = 0, binding = 2) uniform sampler2D shadowMap;

// Set 0, bindings 3-5 = image-based lighting (M7). Generated once by IblGenerator from the HDRI:
//   3 irradiance cubemap (diffuse ambient), 4 prefiltered specular cubemap (roughness across its mip chain),
//   5 environment BRDF LUT (RG = split-sum scale/bias). Together they replace the constant ambient placeholder.
layout(set = 0, binding = 3) uniform samplerCube irradianceMap;
layout(set = 0, binding = 4) uniform samplerCube prefilteredMap;
layout(set = 0, binding = 5) uniform sampler2D brdfLut;

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

// Debug visualization selector (0 = PBR); pushed by the Renderer after the vertex-stage model
// matrix. Rendering intermediate quantities as colors is the fastest way to localize a shading
// bug: a broken input shows exactly where and which.
layout(push_constant) uniform PushConstants {
    layout(offset = 64) int debugView;
} push;

const int DEBUG_NONE = 0;
const int DEBUG_SHADED_NORMAL = 1;   // final N (normal map applied), 0.5+0.5
const int DEBUG_GEOMETRIC_NORMAL = 2; // interpolated vertex normal only
const int DEBUG_BASECOLOR = 3;
const int DEBUG_METALLIC = 4;
const int DEBUG_ROUGHNESS = 5;
const int DEBUG_OCCLUSION = 6;
const int DEBUG_TANGENT = 7;          // xyz 0.5+0.5, handedness = green/red tint on w
const int DEBUG_KEY_NDOTL = 8;        // NdotL of the directional light (final N)
const int DEBUG_SHADOW = 9;           // directional shadow factor in greyscale (1 = lit, 0 = shadowed)

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

// Roughness-aware Schlick Fresnel for the IBL ambient (Karis): the reflectance ceiling is raised toward
// (1 - roughness) so rough surfaces don't reflect a hard white rim at grazing angles.
vec3 fresnelSchlickRoughness(float cosTheta, vec3 f0, float roughness) {
    float m = clamp(1.0 - cosTheta, 0.0, 1.0);
    float m2 = m * m;
    return f0 + (max(vec3(1.0 - roughness), f0) - f0) * (m2 * m2 * m);
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

// --- Directional shadow (M6) --------------------------------------------------------------------------
// Manual PCF 3x3 lookup of the directional shadow map. Projects the world position into the light's clip
// space, derives the [0,1] shadow-map UV and reference depth, then averages nine manually-compared taps.
// Returns 1 = fully lit, 0 = fully shadowed. The test `reference <= stored` passes when the fragment's depth
// is at/in front of the stored occluder depth = lit. The occluder depth already carries the shadow pipeline's
// slope-scaled bias (a rasterizer state, applied when the shadow map was rendered), so no extra shader bias
// is needed. (A hardware sampler2DShadow comparator would do this test in-sampler, but MoltenVK forbids
// comparison samplers here — see the binding-2 declaration.)
float directionalShadow(vec3 wp) {
    // lightViewProj bakes the Vulkan Y-flip and Z[0,1] (OrthographicVulkan). Ortho w is 1, but the perspective
    // divide is kept so this stays correct if a perspective light frustum is ever substituted.
    vec4 clip = lights.lightViewProj * vec4(wp, 1.0);
    vec3 ndc = clip.xyz / clip.w;

    // Outside the light's depth range: nothing was rendered there, so treat as unshadowed (lit).
    if (ndc.z < 0.0 || ndc.z > 1.0) {
        return 1.0;
    }

    // NDC xy in [-1,1] (Vulkan y-down) -> shadow-map UV in [0,1]. The y-down clip space already matches the
    // top-left texel origin, so v = ndc.y*0.5+0.5 directly, WITHOUT a 1-v flip (the ortho baked the flip).
    vec2 uv = ndc.xy * 0.5 + 0.5;

    // Outside the light frustum in XY: unshadowed. The plain sampler's border is opaque black (depth 0), which
    // would read as a spurious occluder, so reject here rather than leaning on the border color.
    if (uv.x < 0.0 || uv.x > 1.0 || uv.y < 0.0 || uv.y > 1.0) {
        return 1.0;
    }

    float reference = ndc.z;
    vec2 texel = 1.0 / vec2(textureSize(shadowMap, 0));

    // 5x5 PCF, bilinearly weighted per tap. Two things fix the stair-stepped edge a plain 3x3 leaves behind: the
    // wider kernel gives 26 penumbra levels instead of 10, and weighting each tap by its bilinear coverage of the
    // sample point (rather than averaging 25 equal binary results) makes the transition continuous as the shadow
    // slides across a texel — a nearest-sampled binary compare cannot be smoothed any other way, because the
    // sampler must not blend raw depths before the comparison.
    vec2 texelPos = uv / texel - 0.5;
    vec2 frac = fract(texelPos);
    vec2 base = (floor(texelPos) + 0.5) * texel;

    float sum = 0.0;
    float weightSum = 0.0;
    for (int y = -2; y <= 2; ++y) {
        for (int x = -2; x <= 2; ++x) {
            vec2 offset = vec2(x, y);
            // Bilinear coverage of this tap: 1 at the sample point, falling to 0 one texel away.
            vec2 w = max(vec2(0.0), 1.0 - abs(offset - frac));
            float weight = w.x * w.y + 0.25; // + a flat floor so the outer taps still soften the edge
            float stored = texture(shadowMap, base + offset * texel).r;
            sum += weight * (reference <= stored ? 1.0 : 0.0);
            weightSum += weight;
        }
    }

    return sum / weightSum;
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
    vec3 geometricN = N;
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

    // Directional shadow factor (M6): applied to the directional key light ONLY — point lights and the
    // ambient term are unshadowed (single-cascade directional shadow map, spec §6 M6). Computed once here
    // so the DEBUG_SHADOW visualization can echo the exact value fed into the lighting.
    float shadow = directionalShadow(worldPos);

    // Directional key light: L = surface -> light = -travelDirection. radiance = color * intensity.
    {
        vec3 L = -normalize(lights.dirDirection.xyz);
        vec3 radiance = lights.dirColorIntensity.rgb * lights.dirColorIntensity.w;
        Lo += shade(L, radiance, N, V, NdotV, albedo, metallic, alpha, f0) * shadow;
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

    // --- Ambient (image-based lighting) + AO ----------------------------------------------------------
    // AO occludes only the ambient/indirect term (standard); direct light is unshadowed by the AO map.
    // IBL (M7) replaces the old constant-ambient placeholder: the diffuse term comes from the irradiance
    // cubemap, the specular term from the split-sum of the prefiltered environment (sampled at a roughness-
    // driven mip) and the BRDF LUT. This is what finally gives metals something to reflect in unlit areas.
    float ao = mix(1.0, texture(occlusionTex, fragUv).r, material.mrno.w);

    vec3 F_ambient = fresnelSchlickRoughness(NdotV, f0, roughness);
    vec3 kdAmbient = (1.0 - F_ambient) * (1.0 - metallic); // metals have no diffuse
    vec3 diffuseIbl = texture(irradianceMap, N).rgb * albedo;

    // Prefiltered specular: the mip encodes roughness (mip 0 = mirror, last mip = fully rough).
    float maxMip = float(textureQueryLevels(prefilteredMap) - 1);
    vec3 R = reflect(-V, N);
    vec3 prefilteredColor = textureLod(prefilteredMap, R, roughness * maxMip).rgb;
    vec2 envBrdf = texture(brdfLut, vec2(NdotV, roughness)).rg;
    vec3 specularIbl = prefilteredColor * (f0 * envBrdf.x + envBrdf.y);

    vec3 ambient = (kdAmbient * diffuseIbl + specularIbl) * ao;

    // --- Emissive (added HDR, no tonemap here) --------------------------------------------------------
    vec3 emissive = texture(emissiveTex, fragUv).rgb * material.emissiveFactorStrength.rgb * material.emissiveFactorStrength.w;

    vec3 color = Lo + ambient + emissive;

    // Debug visualizations bypass the lighting result. Values are divided by the exposure the
    // tonemap will multiply back, so debug colors reach the screen (almost) linearly readable.
    if (push.debugView != DEBUG_NONE) {
        vec3 debug =
            push.debugView == DEBUG_SHADED_NORMAL    ? N * 0.5 + 0.5
          : push.debugView == DEBUG_GEOMETRIC_NORMAL ? geometricN * 0.5 + 0.5
          : push.debugView == DEBUG_BASECOLOR        ? albedo
          : push.debugView == DEBUG_METALLIC         ? vec3(metallic)
          : push.debugView == DEBUG_ROUGHNESS        ? vec3(roughness)
          : push.debugView == DEBUG_OCCLUSION        ? vec3(ao)
          : push.debugView == DEBUG_TANGENT          ? mix(vec3(1, 0, 0), vec3(0, 1, 0), step(0.0, worldTangent.w)) * (T * 0.5 + 0.5)
          : push.debugView == DEBUG_KEY_NDOTL        ? vec3(max(dot(N, -normalize(lights.dirDirection.xyz)), 0.0))
          : /* DEBUG_SHADOW */                         vec3(shadow);
        outColor = vec4(debug, 1.0);
        return;
    }

    outColor = vec4(color, base.a);
}
