using System.Numerics;

namespace Agapanthe.Rendering;

/// <summary>Built-in mesh geometry (CPU-side vertex/index data).</summary>
public static class Primitives
{
    /// <summary>
    /// Unit cube centered at the origin, edge length 1. 24 vertices (4 per face so each face
    /// has its own normal and flat color), 36 indices. Winding is counter-clockwise when the
    /// face is viewed from outside, matching the pipeline's front face.
    /// </summary>
    public static (Vertex[] Vertices, ushort[] Indices) Cube()
    {
        // Face definitions: normal, color, and the 4 corner positions in CCW order (outside view).
        var faces = new (Vector3 Normal, Vector3 Color, Vector3 A, Vector3 B, Vector3 C, Vector3 D)[]
        {
            // +X (red)
            (new(1, 0, 0), new(0.9f, 0.2f, 0.2f), new(0.5f, -0.5f, 0.5f), new(0.5f, -0.5f, -0.5f), new(0.5f, 0.5f, -0.5f), new(0.5f, 0.5f, 0.5f)),
            // -X (green)
            (new(-1, 0, 0), new(0.2f, 0.9f, 0.2f), new(-0.5f, -0.5f, -0.5f), new(-0.5f, -0.5f, 0.5f), new(-0.5f, 0.5f, 0.5f), new(-0.5f, 0.5f, -0.5f)),
            // +Y (blue)
            (new(0, 1, 0), new(0.2f, 0.4f, 0.9f), new(-0.5f, 0.5f, 0.5f), new(0.5f, 0.5f, 0.5f), new(0.5f, 0.5f, -0.5f), new(-0.5f, 0.5f, -0.5f)),
            // -Y (yellow)
            (new(0, -1, 0), new(0.9f, 0.9f, 0.2f), new(-0.5f, -0.5f, -0.5f), new(0.5f, -0.5f, -0.5f), new(0.5f, -0.5f, 0.5f), new(-0.5f, -0.5f, 0.5f)),
            // +Z (magenta)
            (new(0, 0, 1), new(0.9f, 0.2f, 0.9f), new(-0.5f, -0.5f, 0.5f), new(0.5f, -0.5f, 0.5f), new(0.5f, 0.5f, 0.5f), new(-0.5f, 0.5f, 0.5f)),
            // -Z (cyan)
            (new(0, 0, -1), new(0.2f, 0.9f, 0.9f), new(0.5f, -0.5f, -0.5f), new(-0.5f, -0.5f, -0.5f), new(-0.5f, 0.5f, -0.5f), new(0.5f, 0.5f, -0.5f)),
        };

        var vertices = new Vertex[faces.Length * 4];
        var indices = new ushort[faces.Length * 6];
        for (var f = 0; f < faces.Length; f++)
        {
            var face = faces[f];
            // Analytic tangent: U grows from corner A to corner B (uv 0,0 → 1,0), so the
            // tangent is the A→B edge. V grows A→D and the corners are laid out CCW, which
            // makes bitangent = cross(N, T) point along +V — glTF handedness w = +1.
            var tangent = new Vector4(Vector3.Normalize(face.B - face.A), 1f);
            var v = f * 4;
            vertices[v + 0] = new Vertex(face.A, face.Color, face.Normal, new Vector2(0, 0), tangent);
            vertices[v + 1] = new Vertex(face.B, face.Color, face.Normal, new Vector2(1, 0), tangent);
            vertices[v + 2] = new Vertex(face.C, face.Color, face.Normal, new Vector2(1, 1), tangent);
            vertices[v + 3] = new Vertex(face.D, face.Color, face.Normal, new Vector2(0, 1), tangent);

            var i = f * 6;
            indices[i + 0] = (ushort)(v + 0);
            indices[i + 1] = (ushort)(v + 1);
            indices[i + 2] = (ushort)(v + 2);
            indices[i + 3] = (ushort)(v + 0);
            indices[i + 4] = (ushort)(v + 2);
            indices[i + 5] = (ushort)(v + 3);
        }

        return (vertices, indices);
    }
}
