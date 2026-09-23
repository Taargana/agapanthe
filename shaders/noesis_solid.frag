#version 450

// Shared fragment shader for noesis_solid.vert (Path_Solid) and noesis_solid_aa.vert (Path_AA_Solid):
// both already resolve to a premultiplied colour in the vertex stage (coverage folded into alpha for
// the AA variant), so the fragment stage has nothing shader-family-specific left to do.

layout(location = 0) in vec4 vColor;
layout(location = 0) out vec4 outColor;

void main() {
    outColor = vColor;
}
