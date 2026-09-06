using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Agapanthe.Core;
using Agapanthe.Engine;

namespace Agapanthe.Tests;

/// <summary>
/// MP-0d W1 — <see cref="SimCommand"/> / <see cref="InputSnapshot"/> are the future network wire format, so their
/// size and blittability are pinned: a field-type or field-order change that would break the wire must break a test.
/// </summary>
public sealed class SimCommandTests
{
    [Fact]
    public void SimCommand_Is56Bytes()
        => Assert.Equal(56, Unsafe.SizeOf<SimCommand>());

    [Fact]
    public void InputSnapshot_Is40Bytes()
        => Assert.Equal(40, Unsafe.SizeOf<InputSnapshot>());

    [Fact]
    public void SimCommand_HoldsNoManagedReferences()
        => Assert.False(RuntimeHelpers.IsReferenceOrContainsReferences<SimCommand>());

    [Fact]
    public void InputSnapshot_HoldsNoManagedReferences()
        => Assert.False(RuntimeHelpers.IsReferenceOrContainsReferences<InputSnapshot>());

    // Size + "no managed refs" alone would pass if two same-width fields were swapped — and that is exactly the
    // plausible slip on a wire format. Assert the actual byte offsets (audit LL F4).
    [Fact]
    public void SimCommand_FieldByteOffsets_ArePinned()
    {
        var cmd = new SimCommand(
            TargetTick: 0x0102030405060708L, Kind: 0xAB, Target: default,
            Vector: new Double3(1.0, 2.0, 3.0), Scalar: 5f, Flags: 0xDEADBEEFu);

        Span<byte> bytes = stackalloc byte[Unsafe.SizeOf<SimCommand>()];
        MemoryMarshal.Write(bytes, in cmd);

        Assert.Equal(0x08, bytes[0]);                                  // TargetTick @0 (little-endian low byte)
        Assert.Equal(0xAB, bytes[8]);                                  // Kind @8
        Assert.Equal(0UL, BitConverter.ToUInt64(bytes[16..]));         // Target (default EntityRef) @16
        Assert.Equal(1.0, BitConverter.ToDouble(bytes[24..]));         // Vector.X @24
        Assert.Equal(3.0, BitConverter.ToDouble(bytes[40..]));         // Vector.Z @40
        Assert.Equal(5f, BitConverter.ToSingle(bytes[48..]));          // Scalar @48
        Assert.Equal(0xDEADBEEFu, BitConverter.ToUInt32(bytes[52..])); // Flags @52
    }

    [Fact]
    public void InputSnapshot_FieldByteOffsets_ArePinned()
    {
        var snap = new InputSnapshot { Held = 0x11, Pressed = 0x22, Released = 0x33 };
        snap.Axes[0] = 1f;
        snap.Axes[3] = 4f;

        Span<byte> bytes = stackalloc byte[Unsafe.SizeOf<InputSnapshot>()];
        MemoryMarshal.Write(bytes, in snap);

        Assert.Equal(0x11, bytes[0]);                          // Held @0
        Assert.Equal(0x22, bytes[8]);                          // Pressed @8
        Assert.Equal(0x33, bytes[16]);                         // Released @16
        Assert.Equal(1f, BitConverter.ToSingle(bytes[24..]));  // Axes[0] @24
        Assert.Equal(4f, BitConverter.ToSingle(bytes[36..]));  // Axes[3] @36
    }

    [Fact]
    public void InputAxes_IndexesFourFloats()
    {
        var axes = default(InputAxes);
        axes[0] = 1f;
        axes[1] = -1f;
        axes[2] = 0.5f;
        axes[3] = -0.25f;

        Assert.Equal(1f, axes[0]);
        Assert.Equal(-1f, axes[1]);
        Assert.Equal(0.5f, axes[2]);
        Assert.Equal(-0.25f, axes[3]);
    }
}
