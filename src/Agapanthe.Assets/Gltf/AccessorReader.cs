using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Agapanthe.Assets.Gltf;

// ---------------------------------------------------------------------------------------------
// Typed accessor decoding (M4-05): turns a glTF accessor index into a concrete geometry stream
// (Vector3[]/Vector4[]/Vector2[]/uint[]), resolving bufferView + accessor byte offsets, byteStride
// (interleaved layouts) and count against the raw buffer bytes exposed by GltfDocument.
//
// Scope (spec §3.6): float vertex attributes (POSITION/NORMAL/TANGENT/TEXCOORD_0) and u8/u16/u32
// scalar indices. Anything else — normalized integer attributes, sparse accessors (no bufferView),
// a type/componentType mismatch, or an out-of-bounds accessor — is a hard AssetException, never a
// silent fallback.
//
// Endianness: glTF buffers are little-endian and every target platform (x64/arm64 desktop) is LE,
// so the contiguous fast path reinterprets bytes with MemoryMarshal.Cast; the strided path reads
// element-by-element. Both assume the host is little-endian, which holds for our RIDs.
//
// It reads the internal GltfDocument.Root schema directly (same assembly/namespace) and pulls buffer
// payloads through the public GltfDocument.GetBufferData (cached per buffer).
// ---------------------------------------------------------------------------------------------

/// <summary>
/// Decodes glTF 2.0 accessors into typed CPU geometry arrays. Stateless beyond the document it
/// wraps; every read validates the accessor against the requested element shape and stays within
/// its bufferView.
/// </summary>
internal sealed class AccessorReader
{
    // GL component type constants (accessor.componentType).
    private const int ComponentByte = 5120;    // i8
    private const int ComponentUByte = 5121;   // u8
    private const int ComponentShort = 5122;   // i16
    private const int ComponentUShort = 5123;  // u16
    private const int ComponentUInt = 5125;    // u32
    private const int ComponentFloat = 5126;   // f32

    private readonly GltfDocument _document;

    public AccessorReader(GltfDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        _document = document;
    }

    /// <summary>Reads a <c>VEC2</c> float accessor (e.g. TEXCOORD_0). Length equals accessor.count.</summary>
    public Vector2[] ReadVec2(int accessorIndex) => ReadFloatVectors<Vector2>(accessorIndex, "VEC2");

    /// <summary>Reads a <c>VEC3</c> float accessor (e.g. POSITION, NORMAL). Length equals accessor.count.</summary>
    public Vector3[] ReadVec3(int accessorIndex) => ReadFloatVectors<Vector3>(accessorIndex, "VEC3");

    /// <summary>Reads a <c>VEC4</c> float accessor (e.g. TANGENT). Length equals accessor.count.</summary>
    public Vector4[] ReadVec4(int accessorIndex) => ReadFloatVectors<Vector4>(accessorIndex, "VEC4");

    /// <summary>
    /// Reads a <c>SCALAR</c> index accessor of unsigned bytes (5121), shorts (5123) or ints (5125),
    /// widening every value to <see cref="uint"/> (the DTO's uniform index type). Length equals
    /// accessor.count.
    /// </summary>
    /// <exception cref="AssetException">
    /// The accessor is not SCALAR, uses a signed/float component type, is sparse, or overruns its view.
    /// </exception>
    public uint[] ReadIndices(int accessorIndex)
    {
        GltfAccessor accessor = GetAccessor(accessorIndex);
        RequireType(accessor, "SCALAR", accessorIndex);

        int componentSize = accessor.ComponentType switch
        {
            ComponentUByte => 1,
            ComponentUShort => 2,
            ComponentUInt => 4,
            _ => throw new AssetException(_document.SourcePath,
                $"accessor {accessorIndex} indices use componentType {ComponentTypeName(accessor.ComponentType)}; " +
                "only 5121 (u8), 5123 (u16) or 5125 (u32) are supported for indices."),
        };

        ReadOnlySpan<byte> span = GetElementSpan(accessor, accessorIndex, componentSize, out int stride, out int count);
        var result = new uint[count];

        switch (accessor.ComponentType)
        {
            case ComponentUByte:
                for (int i = 0; i < count; i++)
                {
                    result[i] = span[i * stride];
                }
                break;

            case ComponentUShort:
                for (int i = 0; i < count; i++)
                {
                    result[i] = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(i * stride, 2));
                }
                break;

            default: // ComponentUInt
                for (int i = 0; i < count; i++)
                {
                    result[i] = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(i * stride, 4));
                }
                break;
        }

        return result;
    }

    /// <summary>
    /// Reads a float accessor of the given glTF element type into a packed unmanaged struct array.
    /// <typeparamref name="T"/> is a <c>System.Numerics</c> vector whose byte size matches the element
    /// (Vector2=8, Vector3=12, Vector4=16). Contiguous data (stride == element size) is reinterpreted in
    /// bulk; interleaved data (byteStride) is read element-by-element.
    /// </summary>
    private T[] ReadFloatVectors<T>(int accessorIndex, string expectedType) where T : unmanaged
    {
        GltfAccessor accessor = GetAccessor(accessorIndex);
        RequireType(accessor, expectedType, accessorIndex);

        if (accessor.ComponentType != ComponentFloat)
        {
            throw new AssetException(_document.SourcePath,
                $"accessor {accessorIndex} has componentType {ComponentTypeName(accessor.ComponentType)} but a float (5126) " +
                $"{expectedType} was expected (normalized integer attributes are not supported in phase 1).");
        }

        int elementSize = Unsafe.SizeOf<T>(); // 8 / 12 / 16, equal to componentCount * sizeof(float)
        ReadOnlySpan<byte> span = GetElementSpan(accessor, accessorIndex, elementSize, out int stride, out int count);

        var result = new T[count];
        if (count == 0)
        {
            return result;
        }

        if (stride == elementSize)
        {
            // Tightly packed: the span is exactly count*elementSize bytes — reinterpret and bulk-copy.
            MemoryMarshal.Cast<byte, T>(span).CopyTo(result);
        }
        else
        {
            // Interleaved: pick each element out of its stride slot.
            for (int i = 0; i < count; i++)
            {
                result[i] = MemoryMarshal.Read<T>(span.Slice(i * stride, elementSize));
            }
        }

        return result;
    }

    /// <summary>
    /// Validates the accessor's bufferView, computes the effective stride, bounds-checks the whole
    /// run against the bufferView's byteLength (and the view against its buffer), and returns the byte
    /// span that begins at the accessor's first element and covers exactly the bytes it addresses.
    /// </summary>
    private ReadOnlySpan<byte> GetElementSpan(GltfAccessor accessor, int accessorIndex, int elementSize,
        out int stride, out int count)
    {
        count = accessor.Count;
        if (count < 0)
        {
            throw new AssetException(_document.SourcePath, $"accessor {accessorIndex} has a negative count ({count}).");
        }

        if (accessor.BufferView is not int bufferViewIndex)
        {
            // No bufferView == a sparse accessor (data supplied only via the "sparse" object), which is
            // outside the phase-1 subset.
            throw new AssetException(_document.SourcePath,
                $"accessor {accessorIndex} has no bufferView (sparse accessors are not supported).");
        }

        GltfBufferView view = GetBufferView(bufferViewIndex, accessorIndex);
        ReadOnlyMemory<byte> buffer = _document.GetBufferData(view.Buffer);

        // The bufferView window must sit inside its buffer.
        if (view.ByteOffset < 0 || view.ByteLength < 0 ||
            (long)view.ByteOffset + view.ByteLength > buffer.Length)
        {
            throw new AssetException(_document.SourcePath,
                $"bufferView {bufferViewIndex} (offset {view.ByteOffset}, length {view.ByteLength}) " +
                $"exceeds buffer {view.Buffer} of {buffer.Length} bytes.");
        }

        // byteStride absent/0 means tightly packed (stride == element size).
        stride = view.ByteStride is > 0 ? view.ByteStride.Value : elementSize;
        if (stride < elementSize)
        {
            throw new AssetException(_document.SourcePath,
                $"bufferView {bufferViewIndex} byteStride {stride} is smaller than accessor {accessorIndex}'s element size {elementSize}.");
        }

        if (accessor.ByteOffset < 0)
        {
            throw new AssetException(_document.SourcePath, $"accessor {accessorIndex} has a negative byteOffset ({accessor.ByteOffset}).");
        }

        if (count == 0)
        {
            return ReadOnlySpan<byte>.Empty;
        }

        // Bytes addressed within the view: from the accessor's start to the end of the last element.
        long span = (long)accessor.ByteOffset + (long)(count - 1) * stride + elementSize;
        if (span > view.ByteLength)
        {
            throw new AssetException(_document.SourcePath,
                $"accessor {accessorIndex} reads {span} bytes at byteStride {stride} (count {count}, element {elementSize}, " +
                $"offset {accessor.ByteOffset}) but bufferView {bufferViewIndex} is only {view.ByteLength} bytes.");
        }

        int start = view.ByteOffset + accessor.ByteOffset;
        int length = (count - 1) * stride + elementSize;
        return buffer.Span.Slice(start, length);
    }

    private GltfAccessor GetAccessor(int accessorIndex)
    {
        GltfAccessor[]? accessors = _document.Root.Accessors;
        if (accessors is null || (uint)accessorIndex >= (uint)accessors.Length)
        {
            throw new AssetException(_document.SourcePath,
                $"accessor index {accessorIndex} out of range (document has {accessors?.Length ?? 0} accessor(s)).");
        }

        return accessors[accessorIndex];
    }

    private GltfBufferView GetBufferView(int bufferViewIndex, int accessorIndex)
    {
        GltfBufferView[]? views = _document.Root.BufferViews;
        if (views is null || (uint)bufferViewIndex >= (uint)views.Length)
        {
            throw new AssetException(_document.SourcePath,
                $"accessor {accessorIndex} references bufferView {bufferViewIndex}, out of range " +
                $"(document has {views?.Length ?? 0} bufferView(s)).");
        }

        return views[bufferViewIndex];
    }

    private void RequireType(GltfAccessor accessor, string expectedType, int accessorIndex)
    {
        if (!string.Equals(accessor.Type, expectedType, StringComparison.Ordinal))
        {
            throw new AssetException(_document.SourcePath,
                $"accessor {accessorIndex} has type '{accessor.Type ?? "(null)"}' but '{expectedType}' was expected.");
        }
    }

    private static string ComponentTypeName(int componentType) => componentType switch
    {
        ComponentByte => "5120 (i8)",
        ComponentUByte => "5121 (u8)",
        ComponentShort => "5122 (i16)",
        ComponentUShort => "5123 (u16)",
        ComponentUInt => "5125 (u32)",
        ComponentFloat => "5126 (f32)",
        _ => componentType.ToString(),
    };
}
