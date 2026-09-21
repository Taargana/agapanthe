using System.Numerics;
using Agapanthe.Core;
using Agapanthe.Engine;
using Agapanthe.Net;
using LiteNetLib.Utils;

namespace Agapanthe.Tests;

/// <summary>
/// Net-1 — <see cref="PacketCodec"/>'s pure wire round-trips: no socket, no <c>NetManager</c>, mirrors
/// <c>HeadlessSimSnapshotFormatTests</c>'s style (format correctness is independent of transport).
/// </summary>
public sealed class PacketCodecTests
{
    private static readonly DrawableTransform Sample = new(
        GlobalId: 42,
        AssetIdentity: new MeshRefKey(new AssetKey("models/helmet"), 2, 3),
        Position: new Double3(1.5, -2.25, 100_000.125),
        Rotation: Quaternion.CreateFromYawPitchRoll(0.3f, 0.1f, -0.2f),
        Scale: 1.5f);

    [Fact]
    public void EntityIntroduce_RoundTrips()
    {
        var writer = new NetDataWriter();
        PacketCodec.WriteEntityIntroduce(writer, Sample);

        var reader = new NetDataReader(writer.CopyData());
        var result = PacketCodec.ReadEntityIntroduce(reader);

        Assert.Equal(Sample, result);
    }

    [Fact]
    public void EntityIntroduce_RoundTrips_ForAssetKeyNone()
    {
        var noneIdentity = Sample with { AssetIdentity = MeshRefKey.None };
        var writer = new NetDataWriter();
        PacketCodec.WriteEntityIntroduce(writer, noneIdentity);

        var reader = new NetDataReader(writer.CopyData());
        var result = PacketCodec.ReadEntityIntroduce(reader);

        Assert.Equal(noneIdentity, result);
    }

    [Fact]
    public void PositionUpdate_RoundTrips_ButNeverCarriesAssetIdentity()
    {
        var writer = new NetDataWriter();
        PacketCodec.WritePositionUpdate(writer, Sample);

        var reader = new NetDataReader(writer.CopyData());
        var result = PacketCodec.ReadPositionUpdate(reader);

        Assert.Equal(Sample.GlobalId, result.GlobalId);
        Assert.Equal(Sample.Position, result.Position);
        Assert.Equal(Sample.Rotation, result.Rotation);
        Assert.Equal(Sample.Scale, result.Scale);
        Assert.Equal(MeshRefKey.None, result.AssetIdentity); // never on the wire for an update — spec D9
    }

    [Fact]
    public void SimCommand_RoundTrips_WithTargetAlwaysDefault()
    {
        // Target is never sent (SimCommand's own contract — a client has no authority over which entity its
        // intent addresses); EntityRef.Id is internal to Agapanthe.World, and Agapanthe.Net deliberately never
        // needs to see it.
        var command = new SimCommand(TargetTick: 123, Kind: 7, Target: default, Vector: new Double3(1, 2, 3), Scalar: 0.5f, Flags: 0xABCDu);
        var writer = new NetDataWriter();
        PacketCodec.WriteSimCommand(writer, command);

        var reader = new NetDataReader(writer.CopyData());
        var result = PacketCodec.ReadSimCommand(reader);

        Assert.Equal(command.TargetTick, result.TargetTick);
        Assert.Equal(command.Kind, result.Kind);
        Assert.Equal(command.Vector, result.Vector);
        Assert.Equal(command.Scalar, result.Scalar);
        Assert.Equal(command.Flags, result.Flags);
        Assert.True(result.Target.IsNone);
    }
}
