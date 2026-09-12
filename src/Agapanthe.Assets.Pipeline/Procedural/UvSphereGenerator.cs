using System.Numerics;
using Agapanthe.Assets;
using Agapanthe.Assets.Model;
using Tomlyn.Model;
using static Agapanthe.Assets.Pipeline.Scene.TomlHelpers;

namespace Agapanthe.Assets.Pipeline.Procedural;

/// <summary>
/// Contenu-3c — cook-time UV-sphere generator (planet/sun/probe/beacon). The tessellation math is copied
/// verbatim from <c>Agapanthe.Rendering.Primitives.UvSphere</c> (via <c>Sandbox.PlanetContent.BuildSphereModel</c>'s
/// conversion into a <see cref="ModelAsset"/>) — same algorithm, written directly into the mesh's SoA arrays
/// instead of an intermediate <c>Vertex[]</c>, since <c>Agapanthe.Assets.Pipeline</c> (cook-side) must not
/// reference <c>Agapanthe.Rendering</c> (that would drag Graphics/Silk.NET into the cook tool). Bounds are filled
/// via <see cref="MeshBounds"/>, matching every other cooked mesh.
/// <see cref="UvSphereGeneratorTests"/> (test project) asserts this produces the exact same geometry as the
/// original — the "copied verbatim" claim is verified, not assumed (Contenu-3b's own audit flagged an
/// unverified verbatim-copy claim as a finding; this generator closes that class of gap proactively).
/// </summary>
internal static class UvSphereGenerator
{
    private static readonly string[] Keys =
    [
        "generator", "radius", "segments", "rings", "base_color", "metallic", "roughness",
        "emissive", "emissive_strength", "name",
    ];

    public static ModelAsset Build(TomlTable table, string path)
    {
        RejectUnknownKeys(table, path, "procedural (uv_sphere)", Keys);

        var radius = (float)(NumOpt(table, "radius", path)
            ?? throw new AssetException($"'{path}': generator 'uv_sphere' needs 'radius'."));
        var segments = (int)(NumOpt(table, "segments", path) ?? 128);
        var rings = (int)(NumOpt(table, "rings", path) ?? 64);
        var name = Str(table, "name", path) ?? "ProceduralSphere";

        var baseColor = table.ContainsKey("base_color") ? Vec4(table, "base_color", path) : Vector4.One;
        var metallic = (float)(NumOpt(table, "metallic", path) ?? 1.0);
        var roughness = (float)(NumOpt(table, "roughness", path) ?? 1.0);
        var emissive = Vec3Opt(table, "emissive", path) ?? Vector3.Zero;
        var emissiveStrength = (float)(NumOpt(table, "emissive_strength", path) ?? 1.0);

        var mesh = BuildSphereMesh(radius, name);
        var material = new MaterialAsset
        {
            BaseColorFactor = baseColor,
            MetallicFactor = metallic,
            RoughnessFactor = roughness,
            EmissiveFactor = emissive,
            EmissiveStrength = emissiveStrength,
            Name = name,
        };

        return new ModelAsset { Meshes = [mesh], Materials = [material], Images = [], Name = name };

        MeshAsset BuildSphereMesh(float r, string meshName)
        {
            if (segments < 3)
            {
                throw new AssetException($"'{path}': uv_sphere 'segments' must be >= 3, got {segments}.");
            }

            if (rings < 2)
            {
                throw new AssetException($"'{path}': uv_sphere 'rings' must be >= 2, got {rings}.");
            }

            // Same ushort-index-space guard as Primitives.UvSphere (P3-M8 audit 🟡-1): fail loudly rather than
            // let a (ushort) cast wrap silently into a corrupt mesh.
            var vertexCount = (long)(rings + 1) * (segments + 1);
            if (vertexCount > ushort.MaxValue + 1)
            {
                throw new AssetException(
                    $"'{path}': uv_sphere tessellation {segments}×{rings} yields {vertexCount} vertices, "
                    + $"over the ushort index limit ({ushort.MaxValue + 1}).");
            }

            var cols = segments + 1;
            var count = (rings + 1) * cols;
            var positions = new Vector3[count];
            var normals = new Vector3[count];
            var tangents = new Vector4[count];
            var uvs = new Vector2[count];

            for (var i = 0; i <= rings; i++)
            {
                var phi = MathF.PI * i / rings;
                var sinPhi = MathF.Sin(phi);
                var cosPhi = MathF.Cos(phi);

                for (var j = 0; j <= segments; j++)
                {
                    var theta = MathF.Tau * j / segments;
                    var sinTheta = MathF.Sin(theta);
                    var cosTheta = MathF.Cos(theta);

                    var unit = new Vector3(sinPhi * cosTheta, cosPhi, sinPhi * sinTheta);
                    var idx = (i * cols) + j;
                    positions[idx] = unit * r;
                    normals[idx] = unit; // outward normal = unit position, radius-independent
                    tangents[idx] = new Vector4(-sinTheta, 0f, cosTheta, 1f);
                    uvs[idx] = new Vector2((float)j / segments, (float)i / rings);
                }
            }

            var indices = new uint[rings * segments * 6];
            var n = 0;
            for (var i = 0; i < rings; i++)
            {
                for (var j = 0; j < segments; j++)
                {
                    var tl = (uint)((i * cols) + j);
                    var tr = (uint)((i * cols) + j + 1);
                    var bl = (uint)(((i + 1) * cols) + j);
                    var br = (uint)(((i + 1) * cols) + j + 1);

                    indices[n++] = tl;
                    indices[n++] = br;
                    indices[n++] = bl;
                    indices[n++] = tl;
                    indices[n++] = tr;
                    indices[n++] = br;
                }
            }

            var (boundsCenter, boundsRadius) = MeshBounds.Compute(positions);
            return new MeshAsset
            {
                Positions = positions, Normals = normals, Tangents = tangents, Uvs = uvs, Indices = indices,
                MaterialIndex = 0, Name = meshName, BoundsCenter = boundsCenter, BoundsRadius = boundsRadius,
            };
        }
    }

    private static Vector4 Vec4(TomlTable t, string key, string path)
    {
        var f = Floats(t, key, path, 4);
        return new Vector4(f[0], f[1], f[2], f[3]);
    }
}
