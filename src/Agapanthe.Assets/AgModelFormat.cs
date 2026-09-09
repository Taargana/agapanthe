using System.Buffers.Binary;
using System.IO.Compression;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Agapanthe.Assets.Model;

namespace Agapanthe.Assets;

/// <summary>
/// The <c>.agmodel</c> container (Contenu-2): a fully-decoded <see cref="ModelAsset"/> — SoA meshes, materials,
/// RGBA8 images — as a self-contained, Deflate-compressed, deterministic blob. The runtime never parses glTF:
/// <c>tools/AssetCooker</c> cooks a source model into one of these and an <c>AssetCatalog</c> reads it back.
/// <para>
/// Same stance as <see cref="Agapanthe.Assets.Font.FontAssetFormat"/> and the VS-1 world snapshot: binary and
/// blittable so a bulk copy needs neither reflection nor a source generator (AOT-safe by construction), and
/// <b>deterministic</b> — the same <see cref="ModelAsset"/> always yields the same bytes on one machine, which
/// turns "the cooker is reproducible" into a byte-comparison test. Little-endian is assumed (every target is LE)
/// and the scalars are written explicitly LE. <b>Cross-machine byte-reproducibility is NOT guaranteed</b> — it
/// depends on <see cref="DeflateStream"/> being stable across .NET patches; what is contractual is that the
/// decoded <see cref="ModelAsset"/> is identical (a per-blob hash pin makes a compression change visible).
/// </para>
/// <para>
/// Layout: <c>magic "AGMD" (4) | version u32 | uncompressedPayloadLen u32</c>, then a raw DEFLATE stream (to EOF)
/// of the payload:
/// <code>
/// modelNameLen u16 | model name UTF-8
/// meshCount u32
///   per mesh: vertexCount u32 | indexCount u32 | materialIndex i32 | streamMask u8
///             worldTransform f32*16
///             Positions f32*3*vertexCount  [ + Normals f32*3, Tangents f32*4, Uvs f32*2 when the mask bit is set ]
///             Indices u32*indexCount
/// materialCount u32
///   per material: BaseColorFactor f32*4 | Metallic f32 | Roughness f32 | NormalScale f32 | OcclusionStrength f32
///                 EmissiveFactor f32*3 | EmissiveStrength f32 | AlphaMode u8 | AlphaCutoff f32
///                 BaseColorImage i32 | NormalImage i32 | MetallicRoughnessImage i32 | OcclusionImage i32 | EmissiveImage i32
///                 WrapU u8 | WrapV u8 | MinFilter u8 | MagFilter u8
/// imageCount u32
///   per image: width u32 | height u32 | isSrgb u8 | Rgba8Pixels byte[width*height*4]
/// </code>
/// The mesh/material <c>Name</c> fields are diagnostics-only per the DTOs and are dropped; the model name is kept.
/// The <b>writer is internal</b> (only <c>Agapanthe.Assets.Pipeline</c> and the round-trip tests produce a blob);
/// the <b>reader is public</b> (the runtime path).
/// </para>
/// </summary>
public static class AgModelFormat
{
    private static ReadOnlySpan<byte> Magic => "AGMD"u8;

    /// <summary>Container version. Bump when the payload layout changes; <see cref="Read"/> refuses anything else
    /// outright, exactly like the VS-1 snapshot header.</summary>
    public const uint Version = 1;

    private const int ContainerHeaderBytes = 4 + 4 + 4; // magic | version | uncompressedPayloadLen

    private const byte StreamNormals = 1 << 0;
    private const byte StreamTangents = 1 << 1;
    private const byte StreamUvs = 1 << 2;

    /// <summary>
    /// Reads a model from a <c>.agmodel</c> byte blob. Every structural expectation is checked before it is
    /// trusted: a corrupt file throws <see cref="AgModelException"/> with a reason, never returns a half-built
    /// asset and never reads out of bounds.
    /// </summary>
    public static ModelAsset Read(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < ContainerHeaderBytes)
        {
            throw new AgModelException(
                $".agmodel is truncated: {bytes.Length} bytes, need at least {ContainerHeaderBytes} for the header.");
        }

        if (!bytes[..4].SequenceEqual(Magic))
        {
            throw new AgModelException("Not a .agmodel file (bad magic; expected 'AGMD').");
        }

        var version = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(4, 4));
        if (version != Version)
        {
            throw new AgModelException($"Unsupported .agmodel version {version} (this build reads version {Version}).");
        }

        var uncompressedLen = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(8, 4));

        // Bound the claimed size BEFORE allocating the inflate buffer — a forged header must not force a huge
        // allocation. Two limits: the DEFLATE ratio (tops out near 1032:1) and a hard ceiling no legitimate
        // model approaches (a decoded 4K RGBA8 texture is 64 MB; 8 of them + geometry stays well under 1 GB).
        const long MaxPayloadBytes = 1L << 30; // 1 GiB
        var maxByRatio = 64L + ((long)(bytes.Length - ContainerHeaderBytes) * 1100);
        if (uncompressedLen > MaxPayloadBytes || uncompressedLen > maxByRatio)
        {
            throw new AgModelException(
                $".agmodel header claims a {uncompressedLen}-byte payload — implausible for {bytes.Length - ContainerHeaderBytes} compressed bytes (or over the {MaxPayloadBytes}-byte ceiling).");
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
                throw new AgModelException(
                    $".agmodel payload is longer than its header claims ({uncompressedLen} bytes).");
            }
        }
        catch (EndOfStreamException ex)
        {
            throw new AgModelException(
                $".agmodel payload is shorter than its header claims ({uncompressedLen} bytes).", ex);
        }
        catch (InvalidDataException ex)
        {
            throw new AgModelException(".agmodel payload is not a valid DEFLATE stream.", ex);
        }

        return payload;
    }

    private static ModelAsset ParsePayload(byte[] payload)
    {
        var cursor = new Reader(payload);

        var name = cursor.ReadString();

        var meshCount = cursor.ReadCount();
        var meshes = new MeshAsset[meshCount];
        for (var m = 0; m < meshCount; m++)
        {
            var vertexCount = (uint)cursor.ReadCount();
            var indexCount = (uint)cursor.ReadCount();
            var materialIndex = cursor.ReadI32();
            var streamMask = cursor.ReadByte();

            var world = cursor.ReadMatrix4x4();
            var positions = cursor.ReadVector3Array(vertexCount);
            var normals = (streamMask & StreamNormals) != 0 ? cursor.ReadVector3Array(vertexCount) : [];
            var tangents = (streamMask & StreamTangents) != 0 ? cursor.ReadVector4Array(vertexCount) : [];
            var uvs = (streamMask & StreamUvs) != 0 ? cursor.ReadVector2Array(vertexCount) : [];
            var indices = cursor.ReadU32Array(indexCount);

            meshes[m] = new MeshAsset
            {
                Positions = positions,
                Normals = normals,
                Tangents = tangents,
                Uvs = uvs,
                Indices = indices,
                MaterialIndex = materialIndex,
                WorldTransform = world,
            };
        }

        var materialCount = cursor.ReadCount();
        var materials = new MaterialAsset[materialCount];
        for (var i = 0; i < materialCount; i++)
        {
            materials[i] = new MaterialAsset
            {
                BaseColorFactor = cursor.ReadVector4(),
                MetallicFactor = cursor.ReadF32(),
                RoughnessFactor = cursor.ReadF32(),
                NormalScale = cursor.ReadF32(),
                OcclusionStrength = cursor.ReadF32(),
                EmissiveFactor = cursor.ReadVector3(),
                EmissiveStrength = cursor.ReadF32(),
                AlphaMode = (AlphaMode)cursor.ReadByte(),
                AlphaCutoff = cursor.ReadF32(),
                BaseColorImage = cursor.ReadI32(),
                NormalImage = cursor.ReadI32(),
                MetallicRoughnessImage = cursor.ReadI32(),
                OcclusionImage = cursor.ReadI32(),
                EmissiveImage = cursor.ReadI32(),
                TextureSettings = new TextureSettings(
                    (TextureWrap)cursor.ReadByte(),
                    (TextureWrap)cursor.ReadByte(),
                    (TextureFilter)cursor.ReadByte(),
                    (TextureFilter)cursor.ReadByte()),
            };
        }

        // Cross-references must resolve (MeshAsset.MaterialIndex points into Materials).
        foreach (var mesh in meshes)
        {
            if (mesh.MaterialIndex < -1 || mesh.MaterialIndex >= materialCount)
            {
                throw new AgModelException(
                    $".agmodel mesh references material {mesh.MaterialIndex}, but it has {materialCount} material(s).");
            }
        }

        var imageCount = cursor.ReadCount();
        foreach (var mat in materials)
        {
            foreach (var slot in (ReadOnlySpan<int>)
                     [mat.BaseColorImage, mat.NormalImage, mat.MetallicRoughnessImage, mat.OcclusionImage, mat.EmissiveImage])
            {
                if (slot < -1 || slot >= imageCount)
                {
                    throw new AgModelException(
                        $".agmodel material references image {slot}, but it has {imageCount} image(s).");
                }
            }
        }

        var images = new ImageAsset[imageCount];
        for (var i = 0; i < imageCount; i++)
        {
            var width = cursor.ReadU32();
            var height = cursor.ReadU32();
            var isSrgb = cursor.ReadByte() != 0;
            if (width == 0 || height == 0)
            {
                throw new AgModelException($".agmodel image {i} has non-positive dimensions {width}×{height}.");
            }

            var pixelBytes = (long)width * height * 4;
            images[i] = new ImageAsset
            {
                Rgba8Pixels = cursor.ReadBytes(pixelBytes),
                Width = (int)width,
                Height = (int)height,
                IsSrgb = isSrgb,
            };
        }

        cursor.RequireExhausted();

        // A degenerate mesh (non-finite positions/uvs, NaN transform) would propagate through the vertex buffer
        // where nothing downstream checks — surface it here, at the boundary.
        foreach (var mesh in meshes)
        {
            EnsureFinite(mesh.Positions, "position");
            EnsureFinite(mesh.Normals, "normal");
            EnsureFinite(mesh.Uvs, "uv");
            EnsureFinite(mesh.Tangents, "tangent");
        }

        return new ModelAsset
        {
            Meshes = meshes,
            Materials = materials,
            Images = images,
            Name = name,
        };
    }

    /// <summary>
    /// Writes a model to the <c>.agmodel</c> layout. <b>Internal</b>: producing blobs is the cooker's job.
    /// Deterministic on one machine — the same <see cref="ModelAsset"/> always yields the same bytes.
    /// </summary>
    internal static void WriteContainer(Stream stream, ModelAsset model)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(model);

        var payload = BuildPayload(model);

        stream.Write(Magic);
        WriteU32(stream, Version);
        WriteU32(stream, checked((uint)payload.Length));

        using var deflate = new DeflateStream(stream, CompressionLevel.Optimal, leaveOpen: true);
        deflate.Write(payload);
    }

    private static byte[] BuildPayload(ModelAsset model)
    {
        using var ms = new MemoryStream();

        WriteString(ms, model.Name);

        WriteU32(ms, checked((uint)model.Meshes.Count));
        foreach (var mesh in model.Meshes)
        {
            var vertexCount = mesh.Positions.Length;
            EnsureStreamLength(mesh.Normals, vertexCount, "Normals", mesh.Name);
            EnsureStreamLength(mesh.Tangents, vertexCount, "Tangents", mesh.Name);
            EnsureStreamLength(mesh.Uvs, vertexCount, "Uvs", mesh.Name);

            byte mask = 0;
            if (mesh.Normals.Length > 0)
            {
                mask |= StreamNormals;
            }

            if (mesh.Tangents.Length > 0)
            {
                mask |= StreamTangents;
            }

            if (mesh.Uvs.Length > 0)
            {
                mask |= StreamUvs;
            }

            WriteU32(ms, checked((uint)vertexCount));
            WriteU32(ms, checked((uint)mesh.Indices.Length));
            WriteI32(ms, mesh.MaterialIndex);
            ms.WriteByte(mask);
            WriteMatrix4x4(ms, mesh.WorldTransform);
            ms.Write(MemoryMarshal.AsBytes<Vector3>(mesh.Positions));
            if ((mask & StreamNormals) != 0)
            {
                ms.Write(MemoryMarshal.AsBytes<Vector3>(mesh.Normals));
            }

            if ((mask & StreamTangents) != 0)
            {
                ms.Write(MemoryMarshal.AsBytes<Vector4>(mesh.Tangents));
            }

            if ((mask & StreamUvs) != 0)
            {
                ms.Write(MemoryMarshal.AsBytes<Vector2>(mesh.Uvs));
            }

            ms.Write(MemoryMarshal.AsBytes<uint>(mesh.Indices));
        }

        WriteU32(ms, checked((uint)model.Materials.Count));
        foreach (var mat in model.Materials)
        {
            WriteVector4(ms, mat.BaseColorFactor);
            WriteF32(ms, mat.MetallicFactor);
            WriteF32(ms, mat.RoughnessFactor);
            WriteF32(ms, mat.NormalScale);
            WriteF32(ms, mat.OcclusionStrength);
            WriteVector3(ms, mat.EmissiveFactor);
            WriteF32(ms, mat.EmissiveStrength);
            ms.WriteByte((byte)mat.AlphaMode);
            WriteF32(ms, mat.AlphaCutoff);
            WriteI32(ms, mat.BaseColorImage);
            WriteI32(ms, mat.NormalImage);
            WriteI32(ms, mat.MetallicRoughnessImage);
            WriteI32(ms, mat.OcclusionImage);
            WriteI32(ms, mat.EmissiveImage);
            ms.WriteByte((byte)mat.TextureSettings.WrapU);
            ms.WriteByte((byte)mat.TextureSettings.WrapV);
            ms.WriteByte((byte)mat.TextureSettings.MinFilter);
            ms.WriteByte((byte)mat.TextureSettings.MagFilter);
        }

        WriteU32(ms, checked((uint)model.Images.Count));
        foreach (var image in model.Images)
        {
            if (image.Rgba8Pixels.Length != (long)image.Width * image.Height * 4)
            {
                throw new AgModelException(
                    $".agmodel writer: image {image.Width}×{image.Height} has {image.Rgba8Pixels.Length} bytes, "
                    + $"expected {(long)image.Width * image.Height * 4}.");
            }

            WriteU32(ms, checked((uint)image.Width));
            WriteU32(ms, checked((uint)image.Height));
            ms.WriteByte(image.IsSrgb ? (byte)1 : (byte)0);
            ms.Write(image.Rgba8Pixels);
        }

        return ms.ToArray();
    }

    private static void EnsureStreamLength<T>(T[] stream, int vertexCount, string name, string meshName)
    {
        if (stream.Length != 0 && stream.Length != vertexCount)
        {
            throw new AgModelException(
                $".agmodel writer: mesh '{meshName}' {name} has {stream.Length} entries, expected 0 or {vertexCount}.");
        }
    }

    private static void EnsureFinite(Vector3[] values, string what)
    {
        foreach (var v in values)
        {
            if (!float.IsFinite(v.X) || !float.IsFinite(v.Y) || !float.IsFinite(v.Z))
            {
                throw new AgModelException($".agmodel has a non-finite {what} ({v}).");
            }
        }
    }

    private static void EnsureFinite(Vector2[] values, string what)
    {
        foreach (var v in values)
        {
            if (!float.IsFinite(v.X) || !float.IsFinite(v.Y))
            {
                throw new AgModelException($".agmodel has a non-finite {what} ({v}).");
            }
        }
    }

    private static void EnsureFinite(Vector4[] values, string what)
    {
        foreach (var v in values)
        {
            if (!float.IsFinite(v.X) || !float.IsFinite(v.Y) || !float.IsFinite(v.Z) || !float.IsFinite(v.W))
            {
                throw new AgModelException($".agmodel has a non-finite {what} ({v}).");
            }
        }
    }

    // --- Write helpers (little-endian) ---------------------------------------------------------------------------

    private static void WriteU32(Stream s, uint value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, value);
        s.Write(buffer);
    }

    private static void WriteI32(Stream s, int value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(buffer, value);
        s.Write(buffer);
    }

    private static void WriteF32(Stream s, float value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteSingleLittleEndian(buffer, value);
        s.Write(buffer);
    }

    private static void WriteString(Stream s, string value)
    {
        var utf8 = System.Text.Encoding.UTF8.GetBytes(value ?? string.Empty);
        Span<byte> len = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(len, checked((ushort)utf8.Length));
        s.Write(len);
        s.Write(utf8);
    }

    private static void WriteVector2(Stream s, Vector2 v)
    {
        WriteF32(s, v.X);
        WriteF32(s, v.Y);
    }

    private static void WriteVector3(Stream s, Vector3 v)
    {
        WriteF32(s, v.X);
        WriteF32(s, v.Y);
        WriteF32(s, v.Z);
    }

    private static void WriteVector4(Stream s, Vector4 v)
    {
        WriteF32(s, v.X);
        WriteF32(s, v.Y);
        WriteF32(s, v.Z);
        WriteF32(s, v.W);
    }

    private static void WriteMatrix4x4(Stream s, Matrix4x4 m)
    {
        WriteF32(s, m.M11); WriteF32(s, m.M12); WriteF32(s, m.M13); WriteF32(s, m.M14);
        WriteF32(s, m.M21); WriteF32(s, m.M22); WriteF32(s, m.M23); WriteF32(s, m.M24);
        WriteF32(s, m.M31); WriteF32(s, m.M32); WriteF32(s, m.M33); WriteF32(s, m.M34);
        WriteF32(s, m.M41); WriteF32(s, m.M42); WriteF32(s, m.M43); WriteF32(s, m.M44);
    }

    // --- Read cursor -------------------------------------------------------------------------------------------

    private ref struct Reader(byte[] payload)
    {
        private readonly byte[] _payload = payload;
        private int _offset;

        private ReadOnlySpan<byte> Take(long count)
        {
            if (count < 0 || _offset + count > _payload.Length)
            {
                throw new AgModelException(
                    $".agmodel payload ends mid-record (need {count} bytes at offset {_offset}, have {_payload.Length}).");
            }

            var span = _payload.AsSpan(_offset, (int)count);
            _offset += (int)count;
            return span;
        }

        public uint ReadU32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));

        /// <summary>A count of items that follow: it can never exceed the bytes left (every item is ≥ 1 byte), so
        /// this bounds a forged count before it drives a <c>new T[count]</c> allocation.</summary>
        public int ReadCount()
        {
            var count = ReadU32();
            if (count > (uint)(_payload.Length - _offset))
            {
                throw new AgModelException(
                    $".agmodel record count {count} exceeds the {_payload.Length - _offset} bytes that remain.");
            }

            return (int)count;
        }

        public int ReadI32() => BinaryPrimitives.ReadInt32LittleEndian(Take(4));

        public float ReadF32() => BinaryPrimitives.ReadSingleLittleEndian(Take(4));

        public byte ReadByte() => Take(1)[0];

        public string ReadString()
        {
            var len = BinaryPrimitives.ReadUInt16LittleEndian(Take(2));
            return System.Text.Encoding.UTF8.GetString(Take(len));
        }

        public Vector2 ReadVector2() => new(ReadF32(), ReadF32());

        public Vector3 ReadVector3() => new(ReadF32(), ReadF32(), ReadF32());

        public Vector4 ReadVector4() => new(ReadF32(), ReadF32(), ReadF32(), ReadF32());

        public Matrix4x4 ReadMatrix4x4() => new(
            ReadF32(), ReadF32(), ReadF32(), ReadF32(),
            ReadF32(), ReadF32(), ReadF32(), ReadF32(),
            ReadF32(), ReadF32(), ReadF32(), ReadF32(),
            ReadF32(), ReadF32(), ReadF32(), ReadF32());

        // Take() FIRST (it bounds `count` against the payload and throws before any allocation), then materialise
        // from the validated span — a forged count can never drive a huge `new T[count]`.
        public Vector3[] ReadVector3Array(uint count)
            => MemoryMarshal.Cast<byte, Vector3>(Take((long)count * Unsafe.SizeOf<Vector3>())).ToArray();

        public Vector4[] ReadVector4Array(uint count)
            => MemoryMarshal.Cast<byte, Vector4>(Take((long)count * Unsafe.SizeOf<Vector4>())).ToArray();

        public Vector2[] ReadVector2Array(uint count)
            => MemoryMarshal.Cast<byte, Vector2>(Take((long)count * Unsafe.SizeOf<Vector2>())).ToArray();

        public uint[] ReadU32Array(uint count)
            => MemoryMarshal.Cast<byte, uint>(Take((long)count * 4)).ToArray();

        public byte[] ReadBytes(long count) => Take(count).ToArray();

        public void RequireExhausted()
        {
            if (_offset != _payload.Length)
            {
                throw new AgModelException(
                    $".agmodel payload has {_payload.Length - _offset} trailing byte(s) after the last record.");
            }
        }
    }
}
