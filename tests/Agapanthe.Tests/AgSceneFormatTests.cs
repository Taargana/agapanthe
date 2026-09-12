using System.Buffers.Binary;
using System.Numerics;
using Agapanthe.Assets.Scene;
using Agapanthe.Core;

namespace Agapanthe.Tests;

/// <summary>Contenu-3b — the `.agscene` cooked-scene binary format: byte-identical round-trip and a corrupt blob
/// as a hard <see cref="AgSceneException"/>, never a half-built <see cref="SceneDefinition"/>.</summary>
public sealed class AgSceneFormatTests
{
    private static SceneDefinition Sample() => new()
    {
        Name = "sample",
        WorldOrigin = new Double3(10, 20, 30),
        Entities =
        [
            new SceneEntity
            {
                Model = new AssetKey("models/helmet.glb"), LocalMesh = 0, LocalMat = 1,
                Position = new Double3(1, 2, 3), Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.3f),
                Scale = 1.5f, CastsShadow = true,
            },
            new SceneEntity
            {
                Model = new AssetKey("models/a.glb"), LocalMesh = 2, LocalMat = 0,
                Position = new Double3(-5, 0, 5), CastsShadow = false,
                Body = new SceneBody { Velocity = new Vector3(0, -1, 0), InverseMass = 1f, Restitution = 0.3f, Radius = 0.5f },
            },
        ],
        Lights =
        [
            new SceneLight { Kind = SceneLightKind.Directional, Color = new Vector3(1, 0.96f, 0.9f), Intensity = 12f, Direction = new Vector3(0.4f, -0.7f, -0.6f) },
            new SceneLight { Kind = SceneLightKind.Point, Color = Vector3.One, Intensity = 3f, Position = new Double3(100, 50, 0), Range = 200f },
        ],
        Ambient = new Vector3(0.08f, 0.08f, 0.09f),
        Camera = new SceneCamera { Mode = SceneCameraMode.FrameBounds, FovY = 60f, FreeFly = true, ViewDir = new Vector3(0, 0.35f, 1f), DistanceMul = 1.5f },
        Environment = new SceneEnvironment { Mode = SceneEnvironmentMode.HdriPath, HdriPath = "models/studio_small_1k.hdr" },
        Physics = new ScenePhysics { Gravity = new Vector3(0, -9.81f, 0), GroundY = 0f },
        Restore = new SceneRestore { SnapshotPath = "challenge.save" },
        // Contenu-3c: a probe-only model, referenced by no SceneEntity — exercises the key-table extension.
        Systems =
        [
            new SceneSystem
            {
                Kind = SceneSystemKind.ProbeDrop, ProbeModel = new AssetKey("procedural/probe"),
                ProbeLocalMesh = 0, ProbeLocalMat = 0, ProbeRadius = 3f, Every = 30, Centre = new Double3(0, 100, 0),
            },
        ],
    };

    private static byte[] Write(SceneDefinition d)
    {
        using var ms = new MemoryStream();
        AgSceneFormat.WriteContainer(ms, d);
        return ms.ToArray();
    }

    [Fact]
    public void RoundTrip_IsByteIdentical_AndFieldByField()
    {
        var original = Sample();
        var bytes = Write(original);
        var restored = AgSceneFormat.Read(bytes);

        Assert.Equal(bytes, Write(restored)); // strongest check
        Assert.Equal(bytes, Write(Sample())); // deterministic

        Assert.Equal("sample", restored.Name);
        Assert.Equal(new Double3(10, 20, 30), restored.WorldOrigin);
        Assert.Equal(2, restored.Entities.Count);

        var e0 = restored.Entities[0];
        Assert.Equal(new AssetKey("models/helmet.glb"), e0.Model);
        Assert.Equal(0, e0.LocalMesh);
        Assert.Equal(1, e0.LocalMat);
        Assert.Equal(new Double3(1, 2, 3), e0.Position);
        Assert.Equal(1.5f, e0.Scale);
        Assert.True(e0.CastsShadow);
        Assert.Null(e0.Body);

        var e1 = restored.Entities[1];
        Assert.False(e1.CastsShadow);
        Assert.NotNull(e1.Body);
        Assert.Equal(0.3f, e1.Body!.Restitution);
        Assert.Equal(new Vector3(0, -1, 0), e1.Body.Velocity);

        Assert.Equal(2, restored.Lights.Count);
        Assert.Equal(SceneLightKind.Directional, restored.Lights[0].Kind);
        Assert.Equal(new Vector3(0.4f, -0.7f, -0.6f), restored.Lights[0].Direction);
        Assert.Equal(SceneLightKind.Point, restored.Lights[1].Kind);
        Assert.Equal(new Double3(100, 50, 0), restored.Lights[1].Position);
        Assert.Equal(200f, restored.Lights[1].Range);

        Assert.Equal(new Vector3(0.08f, 0.08f, 0.09f), restored.Ambient);
        Assert.Equal(SceneCameraMode.FrameBounds, restored.Camera.Mode);
        Assert.Equal(60f, restored.Camera.FovY);
        Assert.True(restored.Camera.FreeFly);
        Assert.Equal(SceneEnvironmentMode.HdriPath, restored.Environment.Mode);
        Assert.Equal("models/studio_small_1k.hdr", restored.Environment.HdriPath);
        Assert.Equal(new Vector3(0, -9.81f, 0), restored.Physics!.Gravity);
        Assert.Equal(0.0, restored.Physics!.Mu); // no attractor in this sample
        Assert.Equal("challenge.save", restored.Restore!.SnapshotPath);

        var sys = Assert.Single(restored.Systems);
        Assert.Equal(SceneSystemKind.ProbeDrop, sys.Kind);
        Assert.Equal(new AssetKey("procedural/probe"), sys.ProbeModel);
        Assert.Equal(3f, sys.ProbeRadius);
        Assert.Equal(30, sys.Every);
        Assert.Equal(new Double3(0, 100, 0), sys.Centre);
    }

    [Fact]
    public void RoundTrip_PhysicsWithAttractor()
    {
        var d = Sample() with
        {
            Physics = new ScenePhysics
            {
                Gravity = Vector3.Zero, GroundY = 0f,
                Mu = 2.03e14, AttractorCenter = new Double3(1, 2, 3), SurfaceRadius = 3185500.0,
            },
        };
        var restored = AgSceneFormat.Read(Write(d));
        Assert.Equal(2.03e14, restored.Physics!.Mu);
        Assert.Equal(new Double3(1, 2, 3), restored.Physics.AttractorCenter);
        Assert.Equal(3185500.0, restored.Physics.SurfaceRadius);
    }

    [Fact]
    public void RoundTrip_ProceduralSkyEnvironment()
    {
        var d = Sample() with { Environment = new SceneEnvironment { Mode = SceneEnvironmentMode.ProceduralSky } };
        var restored = AgSceneFormat.Read(Write(d));
        Assert.Equal(SceneEnvironmentMode.ProceduralSky, restored.Environment.Mode);
    }

    [Fact]
    public void RoundTrip_BlackEnvironment()
    {
        var d = Sample() with { Environment = new SceneEnvironment { Mode = SceneEnvironmentMode.Black } };
        var restored = AgSceneFormat.Read(Write(d));
        Assert.Equal(SceneEnvironmentMode.Black, restored.Environment.Mode);
    }

    [Fact]
    public void RoundTrip_NoSystems_IsEmptyNotNull()
    {
        var d = Sample() with { Systems = [] };
        var restored = AgSceneFormat.Read(Write(d));
        Assert.Empty(restored.Systems);
    }

    [Fact]
    public void RoundTrip_MinimalScene_NoPhysicsNoRestore()
    {
        var d = Sample() with { Physics = null, Restore = null, Environment = new SceneEnvironment { Mode = SceneEnvironmentMode.None } };
        var restored = AgSceneFormat.Read(Write(d));
        Assert.Null(restored.Physics);
        Assert.Null(restored.Restore);
        Assert.Equal(SceneEnvironmentMode.None, restored.Environment.Mode);
    }

    [Fact]
    public void RoundTrip_FixedCamera()
    {
        var d = Sample() with
        {
            Camera = new SceneCamera
            {
                Mode = SceneCameraMode.Fixed, FovY = 70f, FreeFly = false,
                Position = new Double3(0, 5, 10), Yaw = 0.1f, Pitch = -0.2f, Near = 1f, Far = 1000f,
                MoveSpeed = 20f, ShadowDistance = 1f,
            },
        };
        var restored = AgSceneFormat.Read(Write(d));
        Assert.Equal(SceneCameraMode.Fixed, restored.Camera.Mode);
        Assert.Equal(new Double3(0, 5, 10), restored.Camera.Position);
        Assert.Equal(1000f, restored.Camera.Far);
        Assert.Equal(20f, restored.Camera.MoveSpeed);
        Assert.Equal(1f, restored.Camera.ShadowDistance);
    }

    [Fact]
    public void Read_RejectsBadMagic()
    {
        var bytes = Write(Sample());
        bytes[0] ^= 0xFF;
        Assert.Throws<AgSceneException>(() => AgSceneFormat.Read(bytes));
    }

    [Fact]
    public void Read_RejectsUnknownVersion()
    {
        var bytes = Write(Sample());
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4, 4), 999);
        Assert.Throws<AgSceneException>(() => AgSceneFormat.Read(bytes));
    }

    [Fact]
    public void Read_RejectsV1WithARecookMessage()
    {
        var bytes = Write(Sample());
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4, 4), 1);
        var ex = Assert.Throws<AgSceneException>(() => AgSceneFormat.Read(bytes));
        Assert.Contains("re-run the asset cook", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Read_RejectsV2WithARecookMessage()
    {
        var bytes = Write(Sample());
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4, 4), 2);
        var ex = Assert.Throws<AgSceneException>(() => AgSceneFormat.Read(bytes));
        Assert.Contains("re-run the asset cook", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RoundTrip_LandingChallengeSystem()
    {
        var d = Sample() with
        {
            Systems =
            [
                new SceneSystem
                {
                    Kind = SceneSystemKind.LandingChallenge, ProbeModel = new AssetKey("procedural/probe"),
                    ProbeLocalMesh = 0, ProbeLocalMat = 0, ProbeRadius = 3f,
                    ZoneCenter = new Double3(10, 6_371_000, 0), ZoneRadius = 15.0, SurfaceBand = 9.0,
                    DropHeight = 120.0, TargetCount = 3, ShotBudget = 6, QuicksavePath = "challenge.save",
                },
            ],
        };
        var bytes = Write(d);
        var restored = AgSceneFormat.Read(bytes);

        Assert.Equal(bytes, Write(restored));
        var sys = Assert.Single(restored.Systems);
        Assert.Equal(SceneSystemKind.LandingChallenge, sys.Kind);
        Assert.Equal(new AssetKey("procedural/probe"), sys.ProbeModel);
        Assert.Equal(3f, sys.ProbeRadius);
        Assert.Equal(new Double3(10, 6_371_000, 0), sys.ZoneCenter);
        Assert.Equal(15.0, sys.ZoneRadius);
        Assert.Equal(9.0, sys.SurfaceBand);
        Assert.Equal(120.0, sys.DropHeight);
        Assert.Equal(3, sys.TargetCount);
        Assert.Equal(6, sys.ShotBudget);
        Assert.Equal("challenge.save", sys.QuicksavePath);
    }

    [Fact]
    public void Read_RejectsTruncated()
    {
        var bytes = Write(Sample());
        Assert.Throws<AgSceneException>(() => AgSceneFormat.Read(bytes.AsSpan(0, 6).ToArray()));
        Assert.Throws<AgSceneException>(() => AgSceneFormat.Read(bytes.AsSpan(0, bytes.Length - 4).ToArray()));
    }

    [Fact]
    public void Read_RejectsPayloadLenBeyondCeiling()
    {
        var bytes = Write(Sample());
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8, 4), uint.MaxValue);
        Assert.Throws<AgSceneException>(() => AgSceneFormat.Read(bytes));
    }

    // --- hostile raw payloads (audit: bring .agscene's negative-path coverage up to .agmodel's) --------------------

    // Wraps a raw payload in an AGSC container (deflated), mirroring AgModelFormatTests.Container — so a test can
    // craft a hostile payload directly rather than fighting the writer's field-by-field API.
    private static byte[] Container(byte[] payload)
    {
        using var ms = new MemoryStream();
        Span<byte> u32 = stackalloc byte[4];
        ms.Write("AGSC"u8);
        BinaryPrimitives.WriteUInt32LittleEndian(u32, AgSceneFormat.Version);
        ms.Write(u32);
        BinaryPrimitives.WriteUInt32LittleEndian(u32, (uint)payload.Length);
        ms.Write(u32);
        using (var deflate = new System.IO.Compression.DeflateStream(
                   ms, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
        {
            deflate.Write(payload);
        }

        return ms.ToArray();
    }

    [Fact]
    public void Read_RejectsForgedEntityCount_BeyondWhatRemainingBytesCouldHold()
    {
        // name(0) | worldOrigin(24=0) | keyCount=1 | sentinel(0) | entityCount=2, followed by only 100 bytes —
        // far short of what 2 SceneEntity records (57 B minimum each, a reference-typed array) need. The naive
        // "count <= total remaining bytes" check alone would pass this (2 <= 100); ReadCount's minRecordBytes
        // divisor must reject it before `new SceneEntity[2]` is even allocated.
        using var ms = new MemoryStream();
        Span<byte> u32 = stackalloc byte[4];
        ms.Write(new byte[2]);                                             // name length 0
        ms.Write(new byte[24]);                                            // worldOrigin
        BinaryPrimitives.WriteUInt32LittleEndian(u32, 1); ms.Write(u32);   // keyCount = 1 (sentinel only)
        ms.Write(new byte[2]);                                             // sentinel string length 0
        BinaryPrimitives.WriteUInt32LittleEndian(u32, 2); ms.Write(u32);   // entityCount = 2 (forged)
        ms.Write(new byte[100]);                                           // nowhere near 2×57 bytes

        Assert.Throws<AgSceneException>(() => AgSceneFormat.Read(Container(ms.ToArray())));
    }

    [Fact]
    public void Read_RejectsKeyTable_NotStrictlyAscending()
    {
        using var ms = new MemoryStream();
        Span<byte> u32 = stackalloc byte[4];
        ms.Write(new byte[2]);                                             // name length 0
        ms.Write(new byte[24]);                                            // worldOrigin
        BinaryPrimitives.WriteUInt32LittleEndian(u32, 3); ms.Write(u32);   // keyCount = 3
        WriteStr(ms, string.Empty);                                        // sentinel
        WriteStr(ms, "models/b.glb");
        WriteStr(ms, "models/a.glb");                                      // out of order

        Assert.Throws<AgSceneException>(() => AgSceneFormat.Read(Container(ms.ToArray())));
    }

    [Fact]
    public void Read_RejectsEntityKeyIndex_BeyondTheKeyTable()
    {
        using var ms = new MemoryStream();
        Span<byte> u32 = stackalloc byte[4];
        ms.Write(new byte[2]);
        ms.Write(new byte[24]);
        BinaryPrimitives.WriteUInt32LittleEndian(u32, 1); ms.Write(u32);   // keyCount = 1 (sentinel only)
        ms.Write(new byte[2]);
        BinaryPrimitives.WriteUInt32LittleEndian(u32, 1); ms.Write(u32);   // entityCount = 1
        BinaryPrimitives.WriteUInt32LittleEndian(u32, 5); ms.Write(u32);   // keyIdx = 5 — beyond the 1-entry table
        ms.Write(new byte[4 + 4]);                                        // localMesh, localMat
        ms.Write(new byte[24 + 16 + 4 + 1]);                              // position, rotation, scale, flags

        Assert.Throws<AgSceneException>(() => AgSceneFormat.Read(Container(ms.ToArray())));
    }

    [Fact]
    public void Read_RejectsUnknownLightKind()
    {
        var d = Sample() with
        {
            Lights = [new SceneLight { Kind = (SceneLightKind)200, Color = Vector3.One, Intensity = 1f }],
        };

        // Kind is written as a raw byte with no validation on write (writer trusts the DTO); the reader must catch
        // the out-of-range value on the way back in.
        Assert.Throws<AgSceneException>(() => AgSceneFormat.Read(Write(d)));
    }

    [Fact]
    public void Read_RejectsUnknownSystemKind()
    {
        var d = Sample() with
        {
            Systems = [new SceneSystem { Kind = (SceneSystemKind)200, ProbeModel = new AssetKey("procedural/probe"), ProbeRadius = 1f }],
        };

        Assert.Throws<AgSceneException>(() => AgSceneFormat.Read(Write(d)));
    }

    [Fact]
    public void Read_RejectsForgedSystemCount_BeyondWhatRemainingBytesCouldHold()
    {
        // A minimal, otherwise-valid v2 payload (no entities/lights/systems, FrameBounds camera, None env, no
        // physics/restore) followed by a forged systemCount far beyond what the trailing bytes could hold.
        using var ms = new MemoryStream();
        Span<byte> u32 = stackalloc byte[4];
        ms.Write(new byte[2]);                                             // name length 0
        ms.Write(new byte[24]);                                            // worldOrigin
        BinaryPrimitives.WriteUInt32LittleEndian(u32, 1); ms.Write(u32);   // keyCount = 1 (sentinel only)
        ms.Write(new byte[2]);                                             // sentinel string length 0
        BinaryPrimitives.WriteUInt32LittleEndian(u32, 0); ms.Write(u32);   // entityCount = 0
        BinaryPrimitives.WriteUInt32LittleEndian(u32, 0); ms.Write(u32);   // lightCount = 0
        ms.Write(new byte[12]);                                            // ambient
        ms.WriteByte((byte)SceneCameraMode.FrameBounds);
        ms.Write(new byte[4]);                                             // fovY
        ms.WriteByte(0);                                                   // freeFly
        ms.Write(new byte[12 + 4]);                                        // viewDir, distanceMul
        ms.WriteByte((byte)SceneEnvironmentMode.None);
        ms.WriteByte(0);                                                   // physics present = 0
        ms.WriteByte(0);                                                   // restore present = 0
        BinaryPrimitives.WriteUInt32LittleEndian(u32, 100); ms.Write(u32); // systemCount = 100 (forged)
        ms.Write(new byte[10]);                                            // nowhere near 100×45 bytes

        Assert.Throws<AgSceneException>(() => AgSceneFormat.Read(Container(ms.ToArray())));
    }

    [Fact]
    public void Read_RejectsTrailingBytes()
    {
        var bytes = Write(Sample());

        // Re-inflate, append a byte, re-deflate under a corrected header — RequireExhausted must catch it.
        var header = bytes.AsSpan(0, 12).ToArray();
        var declaredLen = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8, 4));
        using var inflate = new MemoryStream();
        using (var source = new MemoryStream(bytes, 12, bytes.Length - 12, writable: false))
        using (var deflate = new System.IO.Compression.DeflateStream(source, System.IO.Compression.CompressionMode.Decompress))
        {
            deflate.CopyTo(inflate);
        }

        var withTrailer = inflate.ToArray().Concat(new byte[] { 0 }).ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8, 4), declaredLen + 1);

        using var outMs = new MemoryStream();
        outMs.Write(header);
        using (var deflate = new System.IO.Compression.DeflateStream(outMs, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
        {
            deflate.Write(withTrailer);
        }

        Assert.Throws<AgSceneException>(() => AgSceneFormat.Read(outMs.ToArray()));
    }

    private static void WriteStr(Stream s, string v)
    {
        var utf8 = System.Text.Encoding.UTF8.GetBytes(v);
        Span<byte> len = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(len, (ushort)utf8.Length);
        s.Write(len);
        s.Write(utf8);
    }
}
