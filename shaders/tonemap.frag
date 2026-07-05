#version 450

// Tonemap pass (M5, architect decision 3): resolves the linear HDR scene target (Rgba16Sfloat) into the
// sRGB swapchain. Chain: sample linear HDR -> exposure (linear multiplier) -> ACES filmic tonemap -> out.
//
// NO gamma/pow correction here. The swapchain image uses an sRGB format, so the hardware applies the sRGB
// OETF (the ~1/2.2 encode) automatically on store. A pow(1/2.2) in the shader would double-encode and
// wash the image out. We output LINEAR values in [0,1]; the format does the encode.
//
// Operator: ACES fitted (Narkowicz 2015, "ACES Filmic Tone Mapping Curve"). Chosen over Reinhard:
// Reinhard (x/(1+x)) desaturates colors and flattens the highlight rolloff (a "washed-out" look),
// whereas the ACES fit gives the filmic S-curve (crisp toe, punchy shoulder, higher perceived contrast)
// and matches the Khronos glTF Sample Viewer's default tonemap — the M5-09 visual-comparison reference.

layout(location = 0) in vec2 uv;
layout(location = 0) out vec4 outColor;

layout(set = 0, binding = 0) uniform sampler2D hdr;

layout(push_constant) uniform PushConstants {
    float exposure; // linear pre-tonemap multiplier (Renderer.Exposure; Sandbox binds +/- keys in log2 steps)
} pc;

// Narkowicz 2015 ACES approximation, applied per channel.
vec3 aces(vec3 x) {
    const float a = 2.51;
    const float b = 0.03;
    const float c = 2.43;
    const float d = 0.59;
    const float e = 0.14;
    return clamp((x * (a * x + b)) / (x * (c * x + d) + e), 0.0, 1.0);
}

void main() {
    vec3 hdrColor = texture(hdr, uv).rgb * pc.exposure;
    outColor = vec4(aces(hdrColor), 1.0);
}
