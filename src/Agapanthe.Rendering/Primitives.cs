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

    /// <summary>
    /// Unit UV-sphere centered at the origin, radius 1 (P3-M8 planetary bodies — the model transform scales it to a
    /// planet or the Sun). <paramref name="segments"/> longitude slices × <paramref name="rings"/> latitude stacks;
    /// the grid is <c>(rings+1)×(segments+1)</c> vertices with a duplicated longitude seam so U wraps 0→1 cleanly.
    /// Normals are analytic (= normalized position), tangents run along +U (increasing longitude), winding is CCW
    /// viewed from outside — same front-face convention as <see cref="Cube"/>. Color is white so the material albedo
    /// alone drives the look. Indices are <c>ushort</c>: the default 128×64 gives 8385 vertices, well under 65 535.
    /// The pole rows collapse to a single point, so the top/bottom triangle fans are degenerate (zero-area) — cheap
    /// and harmless; an icosphere would avoid them (deferred, backlog §5).
    /// </summary>
    public static (Vertex[] Vertices, ushort[] Indices) UvSphere(int segments = 128, int rings = 64)
    {
        if (segments < 3)
        {
            throw new ArgumentOutOfRangeException(nameof(segments), segments, "A sphere needs at least 3 longitude segments.");
        }

        if (rings < 2)
        {
            throw new ArgumentOutOfRangeException(nameof(rings), rings, "A sphere needs at least 2 latitude rings.");
        }

        // Indices are ushort, so the vertex grid must fit in 65 536. Fail loudly rather than let the (ushort) casts
        // below wrap silently into a corrupt mesh (audit P3-M8 🟡-1). The default 128×64 = 8385 is well clear.
        var vertexCount = (long)(rings + 1) * (segments + 1);
        if (vertexCount > ushort.MaxValue + 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(segments),
                $"Tessellation {segments}×{rings} yields {vertexCount} vertices, over the ushort index limit ({ushort.MaxValue + 1}).");
        }

        var cols = segments + 1;
        var vertices = new Vertex[(rings + 1) * cols];
        var color = new Vector3(1f, 1f, 1f);

        for (var i = 0; i <= rings; i++)
        {
            // phi: 0 at the +Y pole → PI at the -Y pole (latitude).
            var phi = MathF.PI * i / rings;
            var sinPhi = MathF.Sin(phi);
            var cosPhi = MathF.Cos(phi);

            for (var j = 0; j <= segments; j++)
            {
                // theta: 0 → 2PI around +Y (longitude). The last column (j == segments) repeats theta = 0 to close
                // the seam with its own U = 1, so texturing wraps without sharing vertices.
                var theta = MathF.Tau * j / segments;
                var sinTheta = MathF.Sin(theta);
                var cosTheta = MathF.Cos(theta);

                var position = new Vector3(sinPhi * cosTheta, cosPhi, sinPhi * sinTheta);
                // On the unit sphere the outward normal is the position itself.
                var normal = position;
                // dP/dtheta ∝ (-sinTheta, 0, cosTheta): the +U (longitude) direction. Handedness +1 so
                // bitangent = cross(N, T) points along +V (toward the -Y pole, increasing phi).
                var tangent = new Vector4(-sinTheta, 0f, cosTheta, 1f);
                var uv = new Vector2((float)j / segments, (float)i / rings);

                vertices[(i * cols) + j] = new Vertex(position, color, normal, uv, tangent);
            }
        }

        var indices = new ushort[rings * segments * 6];
        var n = 0;
        for (var i = 0; i < rings; i++)
        {
            for (var j = 0; j < segments; j++)
            {
                var tl = (ushort)((i * cols) + j);
                var tr = (ushort)((i * cols) + j + 1);
                var bl = (ushort)(((i + 1) * cols) + j);
                var br = (ushort)(((i + 1) * cols) + j + 1);

                // CCW seen from outside (geometric normal ∥ outward vertex normals).
                indices[n++] = tl;
                indices[n++] = br;
                indices[n++] = bl;
                indices[n++] = tl;
                indices[n++] = tr;
                indices[n++] = br;
            }
        }

        return (vertices, indices);
    }
}
