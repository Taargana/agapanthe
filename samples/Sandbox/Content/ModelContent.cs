using System.Numerics;
using Agapanthe.Assets;
using Agapanthe.Assets.Model;
using Agapanthe.Core;
using Agapanthe.World;

namespace Sandbox;

/// <summary>Content generation + scene layout for the <c>model</c> / <c>grid:</c> / <c>drop:</c> family, lifted
/// verbatim from the pre-extraction <c>Program.cs</c>.</summary>
internal static class ModelContent
{
    /// <summary>Resolves the CLI arg to a content <see cref="AssetKey"/> (Contenu-2): no arg → the default
    /// <c>models/DamagedHelmet.glb</c>; a bare name <c>X</c> → <c>models/X</c> or <c>models/X.glb</c>; a
    /// <c>models/…</c> arg → verbatim. Throws if the resulting key is not in the cooked content manifest.</summary>
    public static AssetKey ResolveModelKey(string[] args, AssetCatalog catalog)
    {
        if (args.Length == 0)
        {
            return new AssetKey("models/DamagedHelmet.glb");
        }

        var arg = args[0].Replace('\\', '/').Trim();
        // AssetKey is case-sensitive (Contenu-1) — match the prefix the same way.
        foreach (var candidate in arg.StartsWith("models/", StringComparison.Ordinal)
                     ? [arg]
                     : new[] { "models/" + arg, "models/" + arg + ".glb" })
        {
            AssetKey key;
            try
            {
                key = new AssetKey(candidate);
            }
            catch (Exception ex) when (ex is ArgumentException or FormatException)
            {
                continue;
            }

            if (catalog.Contains(key))
            {
                return key;
            }
        }

        throw new InvalidOperationException(
            $"Sandbox: model '{args[0]}' is not in the content manifest — add it under content/models/ and rebuild.");
    }

    public static void LogModelStats(ModelAsset model, string path)
    {
        var triangles = 0L;
        foreach (var mesh in model.Meshes)
        {
            triangles += mesh.Indices.Length / 3;
        }

        Log.Info($"Sandbox: loaded '{path}' — {model.Meshes.Count} mesh(es), {model.Materials.Count} material(s), " +
                 $"{model.Images.Count} image(s), {triangles} triangle(s).");
    }

    /// <summary>Parses <c>grid:NxN</c> or <c>grid:N</c> into (rows, cols). Anything else → (1, 1) = the model once.</summary>
    public static (int Rows, int Cols) ParseGrid(string? spec)
    {
        if (spec is null || !spec.StartsWith("grid:", StringComparison.OrdinalIgnoreCase))
        {
            return (1, 1);
        }

        var dims = spec[5..].Split('x');
        if (dims.Length == 1 && int.TryParse(dims[0], out var n) && n > 0)
        {
            return (n, n);
        }

        if (dims.Length == 2 && int.TryParse(dims[0], out var r) && int.TryParse(dims[1], out var c) && r > 0 && c > 0)
        {
            return (r, c);
        }

        Log.Warn($"Sandbox: could not parse AGAPANTHE_SCENE='{spec}' (expected grid:N or grid:RxC); using one model.");
        return (1, 1);
    }

    /// <summary>Parses <c>drop:N</c> into a body count. Anything else → 0 (not a drop scene).</summary>
    public static int ParseDrop(string? spec)
    {
        if (spec is null || !spec.StartsWith("drop:", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        if (int.TryParse(spec[5..], out var n) && n > 0)
        {
            return n;
        }

        Log.Warn($"Sandbox: could not parse AGAPANTHE_SCENE='{spec}' (expected drop:N); using one model.");
        return 0;
    }

    /// <summary>The model's world-space diagonal, from the base specs. Used to space grid cells so copies do not
    /// overlap. Differences cancel any world origin baked into the positions.</summary>
    public static double ModelDiagonal(ImportedEntitySpec[] specs)
    {
        if (specs.Length == 0)
        {
            return 1d;
        }

        var min = new Double3(double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity);
        var max = new Double3(double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity);
        foreach (var s in specs)
        {
            var r = s.BoundsRadius * MathHelpers.MaxStretch(s.RotationScale);
            var c = s.Position + new Double3(Vector3.Transform(s.BoundsCenter, s.RotationScale));
            var rr = new Double3(r, r, r);
            min = Double3.Min(min, c - rr);
            max = Double3.Max(max, c + rr);
        }

        return Math.Max(Double3.Distance(min, max), 1d);
    }

    /// <summary>A grass-covered ground quad in the X/Z plane, centred on its own local origin (world position comes
    /// from the origin handed to ResourceRegistry.Load). Wound CCW seen from above, normal +Y, UVs tiled.</summary>
    public static ModelAsset BuildGroundModel(float size)
    {
        var h = size * 0.5f;
        var tiles = MathF.Max(size / 4f, 1f);
        var mesh = new MeshAsset
        {
            Positions = [new(-h, 0f, -h), new(h, 0f, -h), new(h, 0f, h), new(-h, 0f, h)],
            Normals = [Vector3.UnitY, Vector3.UnitY, Vector3.UnitY, Vector3.UnitY],
            Uvs = [new(0f, 0f), new(tiles, 0f), new(tiles, tiles), new(0f, tiles)],
            Indices = [0, 2, 1, 0, 3, 2],
            MaterialIndex = 0,
            Name = "Ground",
        };

        var material = new MaterialAsset
        {
            BaseColorFactor = Vector4.One,
            BaseColorImage = 0,
            MetallicFactor = 0f,
            RoughnessFactor = 1f,
            Name = "GrassMaterial",
        };

        return new ModelAsset
        {
            Meshes = [mesh],
            Materials = [material],
            Images = [BuildGrassImage()],
            Name = "Ground",
        };
    }

    /// <summary>An outdoor sky as an equirectangular HDR, generated at load — physically consistent with the sun
    /// the scene uses (<paramref name="sunDirection"/> is the light's propagation direction, so the sun disc goes
    /// at -sunDirection). Linear radiance, values well above 1 around the sun.</summary>
    public static HdrImageAsset BuildSkyEnvironment(Vector3 sunDirection)
    {
        const int Width = 512;
        const int Height = 256;
        var pixels = new float[Width * Height * 4];

        var sun = sunDirection.LengthSquared() > 1e-8f
            ? Vector3.Normalize(-sunDirection)
            : Vector3.UnitY;

        var zenith = new Vector3(0.10f, 0.24f, 0.68f);
        var horizon = new Vector3(0.66f, 0.76f, 0.92f);
        var distantGround = new Vector3(0.10f, 0.17f, 0.06f);

        for (var y = 0; y < Height; y++)
        {
            var theta = (y + 0.5f) / Height * MathF.PI;
            var sinTheta = MathF.Sin(theta);
            var cosTheta = MathF.Cos(theta);

            for (var x = 0; x < Width; x++)
            {
                var phi = ((x + 0.5f) / Width * 2f * MathF.PI) - MathF.PI;
                var dir = new Vector3(sinTheta * MathF.Sin(phi), cosTheta, -sinTheta * MathF.Cos(phi));

                Vector3 color;
                if (cosTheta >= 0f)
                {
                    var t = MathF.Pow(1f - cosTheta, 2.5f);
                    color = Vector3.Lerp(zenith, horizon, t) * 1.6f;
                }
                else
                {
                    var t = MathF.Pow(1f + cosTheta, 0.6f);
                    color = Vector3.Lerp(distantGround * 0.55f, Vector3.Lerp(distantGround, horizon, 0.35f), t);
                }

                var cosToSun = Vector3.Dot(dir, sun);
                if (cosToSun > 0.9986f)
                {
                    color += new Vector3(120f, 112f, 96f);
                }
                else if (cosToSun > 0.9f)
                {
                    var glow = MathF.Pow((cosToSun - 0.9f) / 0.0986f, 4f);
                    color += new Vector3(6f, 5.4f, 4.4f) * glow;
                }

                var o = (((y * Width) + x) * 4);
                pixels[o + 0] = color.X;
                pixels[o + 1] = color.Y;
                pixels[o + 2] = color.Z;
                pixels[o + 3] = 1f;
            }
        }

        return new HdrImageAsset { RgbaPixels = pixels, Width = Width, Height = Height };
    }

    /// <summary>A seamless, tileable grass albedo, generated once at load (512², sRGB, fixed seed so captures stay
    /// reproducible). Built to minify gracefully: linear-float accumulation, soft (anti-aliased) blade splats,
    /// modest contrast, and a wrapped blur so the finest frequencies are gone before the mip chain exists.</summary>
    public static ImageAsset BuildGrassImage()
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

    /// <summary>Spawns rows×cols copies of the model, centred on the base position, spaced on the X/Z plane.</summary>
    public static void SpawnGrid(GameWorld world, ImportedEntitySpec[] specs, int rows, int cols, double spacing)
    {
        var halfR = (rows - 1) * 0.5;
        var halfC = (cols - 1) * 0.5;
        for (var r = 0; r < rows; r++)
        {
            for (var c = 0; c < cols; c++)
            {
                var offset = new Double3((c - halfC) * spacing, 0, (r - halfR) * spacing);
                var cell = (r * cols) + c;
                var orderBase = (uint)(cell * specs.Length);
                foreach (var s in specs)
                {
                    world.SpawnImported(new ImportedEntitySpec(
                        s.Mesh, s.Material, s.Position + offset, s.RotationScale,
                        s.BoundsCenter, s.BoundsRadius, orderBase + s.Order, s.Identity)); // Contenu-3a: keep asset identity
                }
            }
        }
    }

    /// <summary>Spawns N physics BODIES as a cube cluster above the ground (P3-M3). Returns the ground Y: one
    /// radius below the model's natural centre. Deterministic by index — the reproducible-capture gate.</summary>
    public static double SpawnDropScene(GameWorld world, ImportedEntitySpec[] specs, int n, Double3 worldOrigin)
    {
        var radius = 1f;
        foreach (var s in specs)
        {
            radius = MathF.Max(radius, s.BoundsRadius * MathHelpers.MaxStretch(s.RotationScale));
        }

        var side = Math.Max(1, (int)Math.Ceiling(Math.Cbrt(n)));
        var hspacing = radius * 2.1;
        var vspacing = radius * 2.2;
        var half = (side - 1) * 0.5;
        var perLayer = side * side;
        var groundY = worldOrigin.Y - radius;

        for (var i = 0; i < n; i++)
        {
            var layer = i / perLayer;
            var inLayer = i % perLayer;
            var cx = inLayer % side;
            var cz = inLayer / side;
            var h = Hash(i);
            var jx = (((h & 0xFFFF) / 65535.0) - 0.5) * radius * 0.4;
            var jz = ((((h >> 16) & 0xFFFF) / 65535.0) - 0.5) * radius * 0.4;
            var offset = new Double3(
                ((cx - half) * hspacing) + jx,
                (radius * 4.0) + (layer * vspacing),
                ((cz - half) * hspacing) + jz);

            var orderBase = (uint)(i * specs.Length);
            foreach (var s in specs)
            {
                var bodyRadius = s.BoundsRadius * MathHelpers.MaxStretch(s.RotationScale);
                world.SpawnBody(
                    new ImportedEntitySpec(
                        s.Mesh, s.Material, s.Position + offset, s.RotationScale,
                        s.BoundsCenter, s.BoundsRadius, orderBase + s.Order, s.Identity), // Contenu-3a: keep asset identity
                    velocity: Vector3.Zero, inverseMass: 1f, restitution: 0.3f, radius: bodyRadius);
            }
        }

        return groundY;

        static uint Hash(int i)
        {
            var x = (uint)i * 2654435761u;
            x ^= x >> 15;
            x *= 2246822519u;
            x ^= x >> 13;
            return x;
        }
    }
}
