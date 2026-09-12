using System.Numerics;
using Agapanthe.Assets;
using Agapanthe.Assets.Model;
using Tomlyn.Model;
using static Agapanthe.Assets.Pipeline.Scene.TomlHelpers;

namespace Agapanthe.Assets.Pipeline.Procedural;

/// <summary>
/// Contenu-3c-3 — cook-time ground-quad generator (the <c>drive</c> scene's floor). <see cref="Build"/>'s mesh/
/// image math is <c>Sandbox.ModelContent.BuildGroundModel</c>/<c>BuildGrassImage</c> moved verbatim (identical
/// algorithm — a CCW quad in the X/Z plane, a procedurally splatted+blurred grass albedo with a fixed seed for
/// reproducible captures). <see cref="GroundQuadGeneratorTests"/> (test project) proves byte-identical output at
/// a fixed size, the same "verified, not assumed" posture <see cref="UvSphereGenerator"/> established.
/// </summary>
internal static class GroundQuadGenerator
{
    private static readonly string[] Keys = ["generator", "size"];

    public static ModelAsset Build(TomlTable table, string path)
    {
        RejectUnknownKeys(table, path, "procedural (ground_quad)", Keys);

        var size = (float)(NumOpt(table, "size", path)
            ?? throw new AssetException($"'{path}': generator 'ground_quad' needs 'size'."));
        if (!float.IsFinite(size) || size <= 0f)
        {
            throw new AssetException($"'{path}': generator 'ground_quad' 'size' must be a positive finite number, got {size}.");
        }

        var h = size * 0.5f;
        var tiles = MathF.Max(size / 4f, 1f);
        var positions = new[] { new Vector3(-h, 0f, -h), new Vector3(h, 0f, -h), new Vector3(h, 0f, h), new Vector3(-h, 0f, h) };
        var (boundsCenter, boundsRadius) = MeshBounds.Compute(positions);
        var mesh = new MeshAsset
        {
            Positions = positions,
            Normals = [Vector3.UnitY, Vector3.UnitY, Vector3.UnitY, Vector3.UnitY],
            Uvs = [new(0f, 0f), new(tiles, 0f), new(tiles, tiles), new(0f, tiles)],
            Indices = [0, 2, 1, 0, 3, 2],
            MaterialIndex = 0,
            Name = "Ground",
            BoundsCenter = boundsCenter,
            BoundsRadius = boundsRadius,
        };

        var material = new MaterialAsset
        {
            BaseColorFactor = Vector4.One,
            BaseColorImage = 0,
            MetallicFactor = 0f,
            RoughnessFactor = 1f,
            Name = "GrassMaterial",
        };

        return new ModelAsset { Meshes = [mesh], Materials = [material], Images = [BuildGrassImage()], Name = "Ground" };
    }

    /// <summary>A seamless, tileable grass albedo, generated once at cook time (512², sRGB, fixed seed so captures
    /// stay reproducible). Verbatim from <c>Sandbox.ModelContent.BuildGrassImage</c>.</summary>
    private static ImageAsset BuildGrassImage()
    {
        const int Size = 512;
        var linear = new float[Size * Size * 3];
        var rng = new Random(1337);

        Span<(float X, float Y, float Amp, float Radius)> clumps = stackalloc (float, float, float, float)[24];
        for (var i = 0; i < clumps.Length; i++)
        {
            clumps[i] = (
                (float)rng.NextDouble() * Size,
                (float)rng.NextDouble() * Size,
                ((float)rng.NextDouble() * 2f) - 1f,
                40f + ((float)rng.NextDouble() * 100f));
        }

        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                var patch = 0f;
                foreach (var c in clumps)
                {
                    var dx = WrappedDelta(x - c.X, Size);
                    var dy = WrappedDelta(y - c.Y, Size);
                    var d2 = ((dx * dx) + (dy * dy)) / (c.Radius * c.Radius);
                    patch += c.Amp * MathF.Exp(-d2);
                }

                var shade = Math.Clamp(0.5f + (patch * 0.22f), 0f, 1f);
                var o = (((y * Size) + x) * 3);
                linear[o + 0] = 0.055f + (0.035f * shade);
                linear[o + 1] = 0.135f + (0.12f * shade);
                linear[o + 2] = 0.042f + (0.04f * shade);
            }
        }

        for (var i = 0; i < 14000; i++)
        {
            var x0 = (float)rng.NextDouble() * Size;
            var y0 = (float)rng.NextDouble() * Size;
            var angle = ((float)rng.NextDouble() * MathF.PI) - (MathF.PI * 0.5f);
            var length = 5f + ((float)rng.NextDouble() * 8f);
            var tint = ((float)rng.NextDouble() * 2f) - 1f;
            var dx = MathF.Sin(angle);
            var dy = -MathF.Cos(angle);

            for (var t = 0f; t < length; t += 0.35f)
            {
                var fade = (1f - (t / length)) * 0.2f;
                Splat(linear, Size, x0 + (dx * t), y0 + (dy * t), tint * fade);
            }
        }

        Blur(linear, Size);

        var pixels = new byte[Size * Size * 4];
        for (var i = 0; i < Size * Size; i++)
        {
            pixels[(i * 4) + 0] = LinearToSrgb(linear[(i * 3) + 0]);
            pixels[(i * 4) + 1] = LinearToSrgb(linear[(i * 3) + 1]);
            pixels[(i * 4) + 2] = LinearToSrgb(linear[(i * 3) + 2]);
            pixels[(i * 4) + 3] = 255;
        }

        return new ImageAsset { Rgba8Pixels = pixels, Width = Size, Height = Size, IsSrgb = true };

        static void Splat(float[] buffer, int size, float x, float y, float amount)
        {
            var ix = (int)MathF.Floor(x);
            var iy = (int)MathF.Floor(y);
            var fx = x - ix;
            var fy = y - iy;

            for (var j = 0; j <= 1; j++)
            {
                for (var i = 0; i <= 1; i++)
                {
                    var w = (i == 0 ? 1f - fx : fx) * (j == 0 ? 1f - fy : fy);
                    var px = ((ix + i) % size + size) % size;
                    var py = ((iy + j) % size + size) % size;
                    var o = (((py * size) + px) * 3);
                    buffer[o + 0] = MathF.Max(0f, buffer[o + 0] * (1f + (amount * w)));
                    buffer[o + 1] = MathF.Max(0f, buffer[o + 1] * (1f + (amount * w * 1.4f)));
                    buffer[o + 2] = MathF.Max(0f, buffer[o + 2] * (1f + (amount * w)));
                }
            }
        }

        static void Blur(float[] buffer, int size)
        {
            var source = (float[])buffer.Clone();
            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    var o = (((y * size) + x) * 3);
                    for (var c = 0; c < 3; c++)
                    {
                        var sum = 0f;
                        for (var j = -1; j <= 1; j++)
                        {
                            for (var i = -1; i <= 1; i++)
                            {
                                var px = ((x + i) % size + size) % size;
                                var py = ((y + j) % size + size) % size;
                                sum += source[(((py * size) + px) * 3) + c];
                            }
                        }

                        buffer[o + c] = sum / 9f;
                    }
                }
            }
        }

        static float WrappedDelta(float d, int size)
        {
            var half = size * 0.5f;
            if (d > half)
            {
                d -= size;
            }
            else if (d < -half)
            {
                d += size;
            }

            return d;
        }

        static byte LinearToSrgb(float c)
        {
            c = Math.Clamp(c, 0f, 1f);
            var s = c <= 0.0031308f ? c * 12.92f : (1.055f * MathF.Pow(c, 1f / 2.4f)) - 0.055f;
            return (byte)Math.Clamp(MathF.Round(s * 255f), 0f, 255f);
        }
    }
}
