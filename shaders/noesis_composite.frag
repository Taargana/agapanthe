#version 450

// Composites the Noesis-rendered texture over the already-drawn frame (vertical slice demo). The
// texture already holds premultiplied colour (VulkanRenderDevice's solid-fill shaders premultiply
// before writing it), so this just samples and lets the pipeline's PremultipliedAlpha blend state do
// the rest — same idiom as ui.frag.

layout(location = 0) in vec2 uv;
layout(location = 0) out vec4 outColor;

layout(set = 0, binding = 0) uniform sampler2D source;

void main() {
    outColor = texture(source, uv);
}
