#version 450

// ImGui debug-overlay fragment shader (D4). Real finding, live: this ImGui version's default font atlas is
// Rgba32 (confirmed by reflection on ImTextureDataPtr.Format), NOT the Alpha8 single-channel coverage atlas
// assumed at design time. RGB channels are constant/white everywhere (confirmed by dumping raw atlas bytes) —
// the real glyph SHAPE lives entirely in the ALPHA channel (confirmed by visualizing the alpha channel alone
// as grayscale — every earlier RGB-only dump looked like "solid boxes" because that IS the correct RGB value).
//
// Second, deeper bug found live AFTER that: `outColor = vColor * texture(...)` looked theoretically correct
// (full vec4 modulate) but still rendered solid "tofu" boxes. Root cause: the pipeline blends with
// BlendMode.PremultipliedAlpha, which requires outColor.rgb to already be scaled by outColor.a. Since the
// atlas RGB is a constant 1.0 regardless of coverage, `vColor.rgb * 1.0` stayed 1.0 (full white) even at
// zero coverage — a premultiplied blend then adds that full-white contribution unconditionally, on top of
// whatever alpha reached the alpha channel, producing a solid white box across the whole glyph cell.
// Fix: the sampled alpha (coverage) must scale BOTH channels, not just land in the output alpha slot.
layout(location = 0) in vec2 vUv;
layout(location = 1) in vec4 vColor;

layout(location = 0) out vec4 outColor;

layout(set = 0, binding = 0) uniform sampler2D fontAtlas;

void main() {
    float coverage = texture(fontAtlas, vUv).a;
    outColor = vec4(vColor.rgb * coverage, vColor.a * coverage);
}
