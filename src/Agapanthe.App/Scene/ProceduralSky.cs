using System.Numerics;
using Agapanthe.Assets.Model;

namespace Agapanthe.App;

/// <summary>
/// Contenu-3c — the procedural outdoor-sky environment (relocated verbatim from
/// <c>Sandbox.ModelContent.BuildSkyEnvironment</c> so <see cref="ClientScenePresenter"/> — not Sandbox-private
/// code — can reach it for the <c>drive</c> scene). GPU-free, pure math: an equirectangular gradient sky +
/// a bright sun disc/glow along <paramref name="sunDirection"/>. Stays runtime code, not a cooked blob — a real
/// <c>AssetKind.Environment</c>/<c>.agenv</c> is Contenu-2b's scope, not duplicated here.
/// </summary>
public static class ProceduralSky
{
    public static HdrImageAsset Build(Vector3 sunDirection)
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
}
