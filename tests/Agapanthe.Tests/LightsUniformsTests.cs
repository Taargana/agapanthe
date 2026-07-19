using System.Numerics;
using System.Runtime.InteropServices;
using Agapanthe.Core;
using Agapanthe.Rendering;

namespace Agapanthe.Tests;

/// <summary>std140 layout contracts for the set-0 uniform blocks (camera + lights, M5).</summary>
public sealed class LightsUniformsTests
{
    // The CSM cascade block every test packs (P3-M5): four matrices + their split depths.
    private static readonly Matrix4x4[] Identity4 =
        [Matrix4x4.Identity, Matrix4x4.Identity, Matrix4x4.Identity, Matrix4x4.Identity];

    [Fact]
    public void LightsUniforms_Is464BytesWithCascadesAt176()
    {
        // 3 header vec4s + 4 point lights x 2 vec4s = 11 x 16 = 176, then FOUR mat4 cascades (P3-M5, 4 x 64 = 256)
        // = 432, then the vec4 of cascade split depths = 448, then the vec4 of shadow params = 464. The shader declares
        // the matching std140 block; any drift breaks the GPU read silently. 464 is a multiple of 16 — no trailing pad.
        Assert.Equal(464, Marshal.SizeOf<LightsUniforms>());
        Assert.Equal(0, (int)Marshal.OffsetOf<LightsUniforms>(nameof(LightsUniforms.DirectionalDirection)));
        Assert.Equal(16, (int)Marshal.OffsetOf<LightsUniforms>(nameof(LightsUniforms.DirectionalColorIntensity)));
        Assert.Equal(32, (int)Marshal.OffsetOf<LightsUniforms>(nameof(LightsUniforms.AmbientPointCount)));
        Assert.Equal(48, (int)Marshal.OffsetOf<LightsUniforms>(nameof(LightsUniforms.Point0PositionRange)));
        Assert.Equal(160, (int)Marshal.OffsetOf<LightsUniforms>(nameof(LightsUniforms.Point3ColorIntensity)));
        Assert.Equal(176, (int)Marshal.OffsetOf<LightsUniforms>(nameof(LightsUniforms.LightViewProj0)));
        Assert.Equal(240, (int)Marshal.OffsetOf<LightsUniforms>(nameof(LightsUniforms.LightViewProj1)));
        Assert.Equal(304, (int)Marshal.OffsetOf<LightsUniforms>(nameof(LightsUniforms.LightViewProj2)));
        Assert.Equal(368, (int)Marshal.OffsetOf<LightsUniforms>(nameof(LightsUniforms.LightViewProj3)));
        Assert.Equal(432, (int)Marshal.OffsetOf<LightsUniforms>(nameof(LightsUniforms.CascadeSplits)));
        Assert.Equal(448, (int)Marshal.OffsetOf<LightsUniforms>(nameof(LightsUniforms.ShadowParams)));
    }

    [Fact]
    public void LightsUniforms_StoresEachCascadeVerbatim()
    {
        var lights = new SceneLights();
        var cascades = new Matrix4x4[4];
        for (var c = 0; c < 4; c++)
        {
            cascades[c] = Matrix4x4.CreateTranslation(c + 1, c + 2, c + 3);
        }

        var splits = new Vector4(8f, 25f, 70f, 200f);
        var packed = new LightsUniforms(lights, cascades, splits, float.MaxValue, Double3.Zero);

        Assert.Equal(cascades[0], packed.LightViewProj0);
        Assert.Equal(cascades[1], packed.LightViewProj1);
        Assert.Equal(cascades[2], packed.LightViewProj2);
        Assert.Equal(cascades[3], packed.LightViewProj3);
        Assert.Equal(splits, packed.CascadeSplits);
    }

    [Fact]
    public void LightsUniforms_FewerCascadesThanSlots_RepeatsTheLast()
    {
        // A short cascade span must not leave identity matrices in the tail slots: a fragment that selected one
        // would sample a garbage tile. The packer repeats the last real cascade instead.
        var lights = new SceneLights();
        var one = new[] { Matrix4x4.CreateTranslation(5, 6, 7) };

        var packed = new LightsUniforms(lights, one, Vector4.Zero, float.MaxValue, Double3.Zero);

        Assert.Equal(one[0], packed.LightViewProj0);
        Assert.Equal(one[0], packed.LightViewProj1);
        Assert.Equal(one[0], packed.LightViewProj2);
        Assert.Equal(one[0], packed.LightViewProj3);
    }

    [Fact]
    public void CameraUniforms_Is144BytesWithPositionAt128()
    {
        Assert.Equal(144, Marshal.SizeOf<CameraUniforms>());
        Assert.Equal(128, (int)Marshal.OffsetOf<CameraUniforms>(nameof(CameraUniforms.Position)));
    }

    [Fact]
    public void LightsUniforms_NormalizesDirectionAndPacksCount()
    {
        var lights = new SceneLights
        {
            Directional = new DirectionalLight
            {
                Direction = new Vector3(0f, -2f, 0f), // non-unit on purpose
                Color = new Vector3(1f, 0.9f, 0.8f),
                Intensity = 3f,
            },
            Ambient = new Vector3(0.05f, 0.05f, 0.05f),
        };
        lights.Points[0] = new PointLight
        {
            Position = new Double3(1, 2, 3),
            Color = Vector3.One,
            Intensity = 10f,
            Range = 25f,
        };
        lights.PointCount = 1;

        var packed = new LightsUniforms(lights, Identity4, Vector4.Zero, float.MaxValue, Double3.Zero);

        Assert.Equal(new Vector4(0f, -1f, 0f, 0f), packed.DirectionalDirection); // normalized
        Assert.Equal(3f, packed.DirectionalColorIntensity.W);
        Assert.Equal(1f, packed.AmbientPointCount.W); // count in w
        Assert.Equal(new Vector4(1f, 2f, 3f, 25f), packed.Point0PositionRange);
        Assert.Equal(10f, packed.Point0ColorIntensity.W);
    }

    [Fact]
    public void SceneLights_PointCountClampsToCapacity()
    {
        var lights = new SceneLights { PointCount = 99 };
        Assert.Equal(SceneLights.MaxPointLights, lights.PointCount);
        lights.PointCount = -3;
        Assert.Equal(0, lights.PointCount);
    }

    [Fact]
    public void LightsUniforms_ZeroDirectionFallsBackToStraightDown()
    {
        var lights = new SceneLights
        {
            Directional = new DirectionalLight { Direction = Vector3.Zero, Color = Vector3.One, Intensity = 1f },
        };

        var packed = new LightsUniforms(lights, Identity4, Vector4.Zero, float.MaxValue, Double3.Zero);
        Assert.Equal(new Vector4(0f, -1f, 0f, 0f), packed.DirectionalDirection);
    }

    [Fact]
    public void LightsUniforms_PacksPointLightsRelativeToTheOrigin()
    {
        // Point lights are stored in double and narrowed against THIS frame's origin (spec §3.3). Packed in
        // absolute float they would light the wrong place as soon as the camera is far from the world origin —
        // and the error would grow as the camera moves, i.e. the light would drift.
        var lights = new SceneLights();
        lights.Points[0] = new PointLight
        {
            Position = new Double3(10_000_002, 5, -3),
            Color = Vector3.One,
            Intensity = 1f,
            Range = 25f,
        };
        lights.PointCount = 1;

        var origin = new Double3(10_000_000, 0, 0);
        var packed = new LightsUniforms(lights, Identity4, Vector4.Zero, float.MaxValue, origin);

        // Exactly the offset from the eye — a float can represent (2, 5, -3) precisely, while 10_000_002 alone
        // is already quantized to the metre.
        Assert.Equal(new Vector4(2f, 5f, -3f, 25f), packed.Point0PositionRange);
    }
}
