using System.Buffers.Binary;
using System.IO.Compression;
using System.Numerics;
using System.Text;
using Agapanthe.Core;

namespace Agapanthe.Assets.Scene;

/// <summary>
/// The <c>.agscene</c> cooked-scene blob (Contenu-3b, bumped v2→v3→v4 by Contenu-3c) — same container shape
/// as <c>.agmodel</c>: <c>magic "AGSC" | version u32 LE | uncompressedPayloadLen u32 LE</c>, then a raw
/// <see cref="DeflateStream"/> of the payload. Reader <b>public</b>; writer <b>internal</b> (producing blobs is
/// the cooker's job — IVT to <c>Agapanthe.Assets.Pipeline</c>). Every count is bounded against the bytes that
/// remain (by a per-block <c>minRecordBytes</c> divisor) before it drives an allocation; a corrupt blob throws
/// <see cref="AgSceneException"/>, never a half-built <see cref="SceneDefinition"/>.
/// <para>
/// v2 (Contenu-3c/3c-1) adds: a Newtonian attractor to <c>[physics]</c>, two fields to the <c>[fixed]</c> camera
/// variant, two no-payload <c>[environment]</c> modes, and a <c>[[system]]</c> section with
/// <see cref="SceneSystemKind.ProbeDrop"/>. v3 (Contenu-3c/3c-2) adds <see cref="SceneSystemKind.LandingChallenge"/>'s
/// fields to that same section. v4 (Contenu-3c/3c-3) adds <see cref="SceneSystemKind.DriveControl"/> — the first
/// kind that spawns nothing, so the `[[system]]` record's probe fields (`probeModelKeyIdx`/`probeLocalMesh`/
/// `probeLocalMat`/`probeRadius`) are now written/read as a fixed-but-possibly-sentinel head for every kind
/// rather than an always-meaningful one (`ProbeModel` on <see cref="SceneSystem"/> defaults to
/// <see cref="AssetKey.None"/>, key index 0). This codebase's precedent for growing a cooked format is a version
/// bump + drop the old reader entirely (see <c>.agmodel</c> v1→v2) — v1/v2/v3 throw <see cref="AgSceneException"/>
/// naming a re-cook, not an in-place upgrade.
/// </para>
/// </summary>
public static class AgSceneFormat
{
    private static ReadOnlySpan<byte> Magic => "AGSC"u8;
    public const uint Version = 4;
    private const int ContainerHeaderBytes = 4 + 4 + 4;
    private const long MaxPayloadBytes = 1L << 26; // 64 MiB — a 100×100 grid is ~400 KB before deflate

    /// <summary>Decodes a <c>.agscene</c> byte blob into a <see cref="SceneDefinition"/>.</summary>
    public static SceneDefinition Read(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < ContainerHeaderBytes)
        {
            throw new AgSceneException($".agscene is truncated: {bytes.Length} bytes, need {ContainerHeaderBytes} for the header.");
        }

        if (!bytes[..4].SequenceEqual(Magic))
        {
            throw new AgSceneException("Not a .agscene file (bad magic; expected 'AGSC').");
        }

        var version = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(4, 4));
        if (version != Version)
        {
            throw new AgSceneException(version switch
            {
                1 => "The .agscene is format v1, which predates Contenu-3c (attractor physics, baked Fixed camera "
                     + "fields, ProceduralSky/Black environments, scene systems). There is no in-place upgrade — re-run the asset cook.",
                2 => "The .agscene is format v2, which predates the LandingChallenge scene system (Contenu-3c-2). "
                     + "There is no in-place upgrade — re-run the asset cook.",
                3 => "The .agscene is format v3, which predates the DriveControl scene system (Contenu-3c-3). "
                     + "There is no in-place upgrade — re-run the asset cook.",
                _ => $"Unsupported .agscene version {version} (this build reads version {Version}).",
            });
        }

        var uncompressedLen = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(8, 4));
        var maxByRatio = 64L + ((long)(bytes.Length - ContainerHeaderBytes) * 1100);
        if (uncompressedLen > MaxPayloadBytes || uncompressedLen > maxByRatio)
        {
            throw new AgSceneException(
                $".agscene header claims a {uncompressedLen}-byte payload — implausible for {bytes.Length - ContainerHeaderBytes} compressed bytes.");
        }

        var payload = Inflate(bytes[ContainerHeaderBytes..], uncompressedLen);
        return ParsePayload(payload);
    }

    private static byte[] Inflate(ReadOnlySpan<byte> compressed, uint uncompressedLen)
    {
        var payload = new byte[uncompressedLen];
        try
        {
            using var source = new MemoryStream(compressed.ToArray(), writable: false);
            using var deflate = new DeflateStream(source, CompressionMode.Decompress);
            deflate.ReadExactly(payload);
            if (deflate.ReadByte() != -1)
            {
                throw new AgSceneException($".agscene payload is longer than its header claims ({uncompressedLen} bytes).");
            }
        }
        catch (EndOfStreamException ex)
        {
            throw new AgSceneException($".agscene payload is shorter than its header claims ({uncompressedLen} bytes).", ex);
        }
        catch (InvalidDataException ex)
        {
            throw new AgSceneException(".agscene payload is not a valid DEFLATE stream.", ex);
        }

        return payload;
    }

    private static SceneDefinition ParsePayload(byte[] payload)
    {
        var r = new Reader(payload);

        var name = r.ReadString();
        var worldOrigin = r.ReadDouble3();

        var keyCount = r.ReadCount(minRecordBytes: 2); // shortest key: a 2-byte "empty string" length prefix
        var keyTable = new AssetKey[keyCount];
        string? prev = null;
        for (var k = 0; k < keyCount; k++)
        {
            var s = r.ReadString();
            if (k == 0)
            {
                if (s.Length != 0)
                {
                    throw new AgSceneException($".agscene key table entry 0 must be the empty sentinel, got '{s}'.");
                }

                keyTable[0] = AssetKey.None;
                prev = string.Empty;
                continue;
            }

            if (prev is not null && string.CompareOrdinal(prev, s) >= 0)
            {
                throw new AgSceneException($".agscene key table is not strictly ascending at entry {k} ('{prev}' then '{s}').");
            }

            try
            {
                keyTable[k] = new AssetKey(s);
            }
            catch (Exception ex) when (ex is ArgumentException or FormatException)
            {
                throw new AgSceneException($".agscene key table entry {k} is not a valid AssetKey.", ex);
            }

            prev = s;
        }

        AssetKey Key(uint idx) => idx < (uint)keyTable.Length
            ? keyTable[idx]
            : throw new AgSceneException($".agscene key index {idx} is beyond the {keyTable.Length}-entry key table.");

        // Shortest entity: keyIdx(4) + localMesh(4) + localMat(4) + position(24) + rotation(16) + scale(4) + flags(1),
        // no body block.
        var entityCount = r.ReadCount(minRecordBytes: 57);
        var entities = new SceneEntity[entityCount];
        for (var e = 0; e < entityCount; e++)
        {
            var keyIdx = r.ReadU32();
            var localMesh = (int)r.ReadU32();
            var localMat = (int)r.ReadU32();
            var position = r.ReadDouble3();
            var rotation = r.ReadQuaternion();
            var scale = r.ReadF32();
            var flags = r.ReadByte();
            SceneBody? body = null;
            if ((flags & 0b10) != 0)
            {
                body = new SceneBody
                {
                    InverseMass = r.ReadF32(),
                    Restitution = r.ReadF32(),
                    Radius = r.ReadF32(),
                    Velocity = r.ReadVector3(),
                };
            }

            entities[e] = new SceneEntity
            {
                Model = Key(keyIdx),
                LocalMesh = localMesh,
                LocalMat = localMat,
                Position = position,
                Rotation = rotation,
                Scale = scale,
                CastsShadow = (flags & 0b01) != 0,
                Body = body,
            };
        }

        // Shortest light: kind(1) + color(12) + intensity(4) + direction(12) — the directional variant.
        var lightCount = r.ReadCount(minRecordBytes: 29);
        var lights = new SceneLight[lightCount];
        for (var l = 0; l < lightCount; l++)
        {
            var kind = (SceneLightKind)r.ReadByte();
            var color = r.ReadVector3();
            var intensity = r.ReadF32();
            lights[l] = kind switch
            {
                SceneLightKind.Directional => new SceneLight
                {
                    Kind = kind, Color = color, Intensity = intensity, Direction = r.ReadVector3(),
                },
                SceneLightKind.Point => new SceneLight
                {
                    Kind = kind, Color = color, Intensity = intensity, Position = r.ReadDouble3(), Range = r.ReadF32(),
                },
                _ => throw new AgSceneException($".agscene light {l} has unknown kind {(byte)kind}."),
            };
        }

        var ambient = r.ReadVector3();

        var camMode = (SceneCameraMode)r.ReadByte();
        var fovY = r.ReadF32();
        var freeFly = r.ReadByte() != 0;
        var camera = camMode switch
        {
            SceneCameraMode.FrameBounds => new SceneCamera
            {
                Mode = camMode, FovY = fovY, FreeFly = freeFly, ViewDir = r.ReadVector3(), DistanceMul = r.ReadF32(),
            },
            SceneCameraMode.Fixed => new SceneCamera
            {
                Mode = camMode, FovY = fovY, FreeFly = freeFly,
                Position = r.ReadDouble3(), Yaw = r.ReadF32(), Pitch = r.ReadF32(), Near = r.ReadF32(), Far = r.ReadF32(),
                MoveSpeed = r.ReadF32(), ShadowDistance = r.ReadF32(), // v2 (Contenu-3c)
            },
            _ => throw new AgSceneException($".agscene camera has unknown mode {(byte)camMode}."),
        };

        var envMode = (SceneEnvironmentMode)r.ReadByte();
        var environment = envMode switch
        {
            SceneEnvironmentMode.None => new SceneEnvironment { Mode = envMode },
            SceneEnvironmentMode.HdriPath => new SceneEnvironment { Mode = envMode, HdriPath = r.ReadString() },
            SceneEnvironmentMode.ProceduralSky => new SceneEnvironment { Mode = envMode }, // v2, no payload
            SceneEnvironmentMode.Black => new SceneEnvironment { Mode = envMode },          // v2, no payload
            _ => throw new AgSceneException($".agscene environment has unknown mode {(byte)envMode}."),
        };

        ScenePhysics? physics = r.ReadByte() != 0
            ? new ScenePhysics
            {
                Gravity = r.ReadVector3(), GroundY = r.ReadF32(),
                Mu = r.ReadF64(), AttractorCenter = r.ReadDouble3(), SurfaceRadius = r.ReadF64(), // v2 (Contenu-3c)
            }
            : null;

        SceneRestore? restore = r.ReadByte() != 0
            ? new SceneRestore { SnapshotPath = r.ReadString() }
            : null;

        // v2/v3/v4 (Contenu-3c): scene systems, appended after [restore]. 25 = DriveControl's record size (v4),
        // the FLOOR across variants, not any single "shortest kind" claim frozen at one point in time — kind(1)
        // + probeModelKeyIdx(4) + probeLocalMesh(4) + probeLocalMat(4) + probeRadius(4) + controlledEntityIndex(4)
        // + moveSpeed(4) = 25; ProbeDrop is 45, LandingChallenge 75+. DriveControl became the floor precisely
        // because it's the first non-spawning kind (3c-2 audit F2) — a future kind shorter than 25 bytes must
        // lower this constant again, or the count-forging guard below weakens.
        var systemCount = r.ReadCount(minRecordBytes: 25);
        var systems = new SceneSystem[systemCount];
        for (var s = 0; s < systemCount; s++)
        {
            var kind = (SceneSystemKind)r.ReadByte();
            var probeModel = Key(r.ReadU32());
            var probeLocalMesh = (int)r.ReadU32();
            var probeLocalMat = (int)r.ReadU32();
            var probeRadius = r.ReadF32();
            systems[s] = kind switch
            {
                SceneSystemKind.ProbeDrop => new SceneSystem
                {
                    Kind = kind, ProbeModel = probeModel, ProbeLocalMesh = probeLocalMesh, ProbeLocalMat = probeLocalMat,
                    ProbeRadius = probeRadius, Every = (int)r.ReadU32(), Centre = r.ReadDouble3(),
                },
                SceneSystemKind.LandingChallenge => new SceneSystem // v3 (Contenu-3c-2)
                {
                    Kind = kind, ProbeModel = probeModel, ProbeLocalMesh = probeLocalMesh, ProbeLocalMat = probeLocalMat,
                    ProbeRadius = probeRadius, ZoneCenter = r.ReadDouble3(), ZoneRadius = r.ReadF64(),
                    SurfaceBand = r.ReadF64(), DropHeight = r.ReadF64(), TargetCount = (int)r.ReadU32(),
                    ShotBudget = (int)r.ReadU32(), QuicksavePath = r.ReadString(),
                },
                SceneSystemKind.DriveControl => new SceneSystem // v4 (Contenu-3c-3) — probeModel is AssetKey.None,
                    // key index 0 (the sentinel), for this non-spawning kind; the shared head above still reads
                    // uniformly, just discards it here.
                {
                    Kind = kind, ControlledEntityIndex = (int)r.ReadU32(), MoveSpeed = r.ReadF32(),
                },
                _ => throw new AgSceneException($".agscene system {s} has unknown kind {(byte)kind}."),
            };
        }

        r.RequireExhausted();

        return new SceneDefinition
        {
            Name = name,
            WorldOrigin = worldOrigin,
            Entities = entities,
            Lights = lights,
            Ambient = ambient,
            Camera = camera,
            Environment = environment,
            Physics = physics,
            Restore = restore,
            Systems = systems,
        };
    }

    // --- writer (internal — the cooker's job) ----------------------------------------------------------------

    internal static void WriteContainer(Stream stream, SceneDefinition def)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(def);

        var payload = BuildPayload(def);
        stream.Write(Magic);
        WriteU32(stream, Version);
        WriteU32(stream, checked((uint)payload.Length));
        using var deflate = new DeflateStream(stream, CompressionLevel.Optimal, leaveOpen: true);
        deflate.Write(payload);
    }

    private static byte[] BuildPayload(SceneDefinition def)
    {
        using var ms = new MemoryStream();

        WriteString(ms, def.Name);
        WriteDouble3(ms, def.WorldOrigin);

        // Key table: entry 0 = "" sentinel, then the distinct non-None keys — entities AND (v2, Contenu-3c) scene
        // systems' probe models, ordinal-ascending. A probe-only model (referenced by no SceneEntity) still needs
        // a slot here so the client uploads it (ClientScenePresenter walks MaterializeResult.Models, which
        // SceneMaterializer populates from every key this table can name).
        var keys = def.Entities.Select(e => e.Model).Concat(def.Systems.Select(s => s.ProbeModel))
            .Where(k => !k.IsNone).Select(k => k.Value!)
            .Distinct(StringComparer.Ordinal).OrderBy(s => s, StringComparer.Ordinal).ToArray();
        var keyToIdx = new Dictionary<string, uint>(StringComparer.Ordinal);
        WriteU32(ms, checked((uint)(keys.Length + 1)));
        WriteString(ms, string.Empty);
        for (var i = 0; i < keys.Length; i++)
        {
            WriteString(ms, keys[i]);
            keyToIdx[keys[i]] = (uint)(i + 1);
        }

        WriteU32(ms, checked((uint)def.Entities.Count));
        foreach (var e in def.Entities)
        {
            WriteU32(ms, e.Model.IsNone ? 0u : keyToIdx[e.Model.Value!]);
            WriteU32(ms, checked((uint)e.LocalMesh));
            WriteU32(ms, checked((uint)e.LocalMat));
            WriteDouble3(ms, e.Position);
            WriteQuaternion(ms, e.Rotation);
            WriteF32(ms, e.Scale);
            byte flags = 0;
            if (e.CastsShadow) flags |= 0b01;
            if (e.Body is not null) flags |= 0b10;
            ms.WriteByte(flags);
            if (e.Body is { } b)
            {
                WriteF32(ms, b.InverseMass);
                WriteF32(ms, b.Restitution);
                WriteF32(ms, b.Radius);
                WriteVector3(ms, b.Velocity);
            }
        }

        WriteU32(ms, checked((uint)def.Lights.Count));
        foreach (var l in def.Lights)
        {
            ms.WriteByte((byte)l.Kind);
            WriteVector3(ms, l.Color);
            WriteF32(ms, l.Intensity);
            if (l.Kind == SceneLightKind.Directional)
            {
                WriteVector3(ms, l.Direction);
            }
            else
            {
                WriteDouble3(ms, l.Position);
                WriteF32(ms, l.Range);
            }
        }

        WriteVector3(ms, def.Ambient);

        ms.WriteByte((byte)def.Camera.Mode);
        WriteF32(ms, def.Camera.FovY);
        ms.WriteByte((byte)(def.Camera.FreeFly ? 1 : 0));
        if (def.Camera.Mode == SceneCameraMode.FrameBounds)
        {
            WriteVector3(ms, def.Camera.ViewDir);
            WriteF32(ms, def.Camera.DistanceMul);
        }
        else
        {
            WriteDouble3(ms, def.Camera.Position);
            WriteF32(ms, def.Camera.Yaw);
            WriteF32(ms, def.Camera.Pitch);
            WriteF32(ms, def.Camera.Near);
            WriteF32(ms, def.Camera.Far);
            WriteF32(ms, def.Camera.MoveSpeed);       // v2 (Contenu-3c)
            WriteF32(ms, def.Camera.ShadowDistance);  // v2 (Contenu-3c)
        }

        ms.WriteByte((byte)def.Environment.Mode);
        if (def.Environment.Mode == SceneEnvironmentMode.HdriPath)
        {
            WriteString(ms, def.Environment.HdriPath);
        }

        // ProceduralSky / Black (v2, Contenu-3c) carry no payload.

        ms.WriteByte((byte)(def.Physics is null ? 0 : 1));
        if (def.Physics is { } ph)
        {
            WriteVector3(ms, ph.Gravity);
            WriteF32(ms, ph.GroundY);
            WriteF64(ms, ph.Mu);                  // v2 (Contenu-3c)
            WriteDouble3(ms, ph.AttractorCenter);  // v2
            WriteF64(ms, ph.SurfaceRadius);        // v2
        }

        ms.WriteByte((byte)(def.Restore is null ? 0 : 1));
        if (def.Restore is { } rs)
        {
            WriteString(ms, rs.SnapshotPath);
        }

        // v2/v3/v4 (Contenu-3c): scene systems, appended after [restore].
        WriteU32(ms, checked((uint)def.Systems.Count));
        foreach (var sys in def.Systems)
        {
            ms.WriteByte((byte)sys.Kind);
            WriteU32(ms, sys.ProbeModel.IsNone ? 0u : keyToIdx[sys.ProbeModel.Value!]);
            WriteU32(ms, checked((uint)sys.ProbeLocalMesh));
            WriteU32(ms, checked((uint)sys.ProbeLocalMat));
            WriteF32(ms, sys.ProbeRadius);
            switch (sys.Kind)
            {
                case SceneSystemKind.ProbeDrop:
                    WriteU32(ms, checked((uint)sys.Every));
                    WriteDouble3(ms, sys.Centre);
                    break;
                case SceneSystemKind.LandingChallenge: // v3 (Contenu-3c-2)
                    WriteDouble3(ms, sys.ZoneCenter);
                    WriteF64(ms, sys.ZoneRadius);
                    WriteF64(ms, sys.SurfaceBand);
                    WriteF64(ms, sys.DropHeight);
                    WriteU32(ms, checked((uint)sys.TargetCount));
                    WriteU32(ms, checked((uint)sys.ShotBudget));
                    WriteString(ms, sys.QuicksavePath);
                    break;
                case SceneSystemKind.DriveControl: // v4 (Contenu-3c-3)
                    WriteU32(ms, checked((uint)sys.ControlledEntityIndex));
                    WriteF32(ms, sys.MoveSpeed);
                    break;
                default:
                    throw new AgSceneException($"unknown scene system kind {sys.Kind}.");
            }
        }

        return ms.ToArray();
    }

    private static void WriteU32(Stream s, uint v)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(b, v);
        s.Write(b);
    }

    private static void WriteF32(Stream s, float v)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteSingleLittleEndian(b, v);
        s.Write(b);
    }

    private static void WriteF64(Stream s, double v)
    {
        Span<byte> b = stackalloc byte[8];
        BinaryPrimitives.WriteDoubleLittleEndian(b, v);
        s.Write(b);
    }

    private static void WriteString(Stream s, string v)
    {
        var utf8 = Encoding.UTF8.GetBytes(v ?? string.Empty);
        Span<byte> len = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(len, checked((ushort)utf8.Length));
        s.Write(len);
        s.Write(utf8);
    }

    private static void WriteVector3(Stream s, Vector3 v)
    {
        WriteF32(s, v.X);
        WriteF32(s, v.Y);
        WriteF32(s, v.Z);
    }

    private static void WriteDouble3(Stream s, Double3 v)
    {
        WriteF64(s, v.X);
        WriteF64(s, v.Y);
        WriteF64(s, v.Z);
    }

    private static void WriteQuaternion(Stream s, Quaternion q)
    {
        WriteF32(s, q.X);
        WriteF32(s, q.Y);
        WriteF32(s, q.Z);
        WriteF32(s, q.W);
    }

    private ref struct Reader(byte[] payload)
    {
        private readonly byte[] _payload = payload;
        private int _offset;

        private ReadOnlySpan<byte> Take(long count)
        {
            if (count < 0 || _offset + count > _payload.Length)
            {
                throw new AgSceneException(
                    $".agscene payload ends mid-record (need {count} bytes at offset {_offset}, have {_payload.Length}).");
            }

            var span = _payload.AsSpan(_offset, (int)count);
            _offset += (int)count;
            return span;
        }

        public uint ReadU32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));

        /// <summary>Reads a count and bounds it against the bytes that remain — <b>including</b>
        /// <paramref name="minRecordBytes"/>, the smallest a following record can possibly be. Bounding by total
        /// remaining bytes alone (the naive check) still lets a forged count size a `new T[count]` allocation of
        /// reference-typed records far beyond what the compressed blob could ever contain (a few KB of payload
        /// forging a many-hundred-MB array of class instances) — the loop then throws on the first out-of-bounds
        /// field read, but only after the allocation already happened.</summary>
        public int ReadCount(int minRecordBytes)
        {
            var count = ReadU32();
            var remaining = (uint)(_payload.Length - _offset);
            if (count > remaining || (minRecordBytes > 0 && count > remaining / (uint)minRecordBytes))
            {
                throw new AgSceneException(
                    $".agscene record count {count} exceeds what {remaining} remaining bytes could possibly hold "
                    + $"({minRecordBytes} bytes/record minimum).");
            }

            return (int)count;
        }

        public float ReadF32() => BinaryPrimitives.ReadSingleLittleEndian(Take(4));

        public double ReadF64() => BinaryPrimitives.ReadDoubleLittleEndian(Take(8));

        public byte ReadByte() => Take(1)[0];

        public string ReadString()
        {
            var len = BinaryPrimitives.ReadUInt16LittleEndian(Take(2));
            return Encoding.UTF8.GetString(Take(len));
        }

        public Vector3 ReadVector3() => new(ReadF32(), ReadF32(), ReadF32());

        public Double3 ReadDouble3() => new(ReadF64(), ReadF64(), ReadF64());

        public Quaternion ReadQuaternion() => new(ReadF32(), ReadF32(), ReadF32(), ReadF32());

        public void RequireExhausted()
        {
            if (_offset != _payload.Length)
            {
                throw new AgSceneException($".agscene payload has {_payload.Length - _offset} trailing bytes.");
            }
        }
    }
}
