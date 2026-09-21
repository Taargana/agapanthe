using System.Numerics;
using Agapanthe.Core;
using Agapanthe.Engine;
using LiteNetLib.Utils;

namespace Agapanthe.Net;

/// <summary>
/// Encodes/decodes the two wire shapes Net-1 needs (spec D9) plus <see cref="SimCommand"/> — pure functions over
/// LiteNetLib's <see cref="NetDataWriter"/>/<see cref="NetDataReader"/>, no socket, no <c>NetManager</c>. Kept
/// separate from the transport (<see cref="NetChannel"/>) so the wire format itself is unit-testable without a
/// live connection.
/// </summary>
public static class PacketCodec
{
    /// <summary>
    /// Introduces a drawable to a peer for the first time: its <see cref="DrawableTransform.GlobalId"/>, its
    /// asset identity (so the receiver can resolve GPU handles via its own <c>AssetCatalog</c>, Contenu-1/2 —
    /// the exact mechanism a snapshot reload already uses), and its initial transform. Variable-length — an
    /// <see cref="AssetKey"/> is a string, not blittable, unlike <see cref="WritePositionUpdate"/>'s fixed shape.
    /// </summary>
    public static void WriteEntityIntroduce(NetDataWriter writer, in DrawableTransform transform)
    {
        writer.Put(transform.GlobalId);
        writer.Put(transform.AssetIdentity.IsNone ? string.Empty : transform.AssetIdentity.Key.Value ?? string.Empty);
        writer.Put(transform.AssetIdentity.LocalMesh);
        writer.Put(transform.AssetIdentity.LocalMat);
        WriteTransform(writer, transform);
    }

    public static DrawableTransform ReadEntityIntroduce(NetDataReader reader)
    {
        var globalId = reader.GetULong();
        var keyValue = reader.GetString();
        var localMesh = reader.GetInt();
        var localMat = reader.GetInt();
        var assetIdentity = keyValue.Length == 0
            ? MeshRefKey.None
            : new MeshRefKey(new AssetKey(keyValue), localMesh, localMat);
        var (position, rotation, scale) = ReadTransform(reader);
        return new DrawableTransform(globalId, assetIdentity, position, rotation, scale);
    }

    /// <summary>
    /// Updates an ALREADY-INTRODUCED drawable's transform only — the hot per-tick path (spec D9). No asset
    /// identity on the wire; a receiver applying this to an unknown <see cref="DrawableTransform.GlobalId"/> is a
    /// protocol violation (the caller must have seen an <see cref="EntityIntroduce"/>-shaped packet for it first).
    /// </summary>
    public static void WritePositionUpdate(NetDataWriter writer, in DrawableTransform transform)
    {
        writer.Put(transform.GlobalId);
        WriteTransform(writer, transform);
    }

    /// <summary>Reads a position-only update. <see cref="DrawableTransform.AssetIdentity"/> on the result is
    /// always <see cref="MeshRefKey.None"/> — the caller already knows it from the entity's introduction and
    /// must not overwrite what it has on record with this.</summary>
    public static DrawableTransform ReadPositionUpdate(NetDataReader reader)
    {
        var globalId = reader.GetULong();
        var (position, rotation, scale) = ReadTransform(reader);
        return new DrawableTransform(globalId, MeshRefKey.None, position, rotation, scale);
    }

    private static void WriteTransform(NetDataWriter writer, in DrawableTransform transform)
    {
        writer.Put(transform.Position.X);
        writer.Put(transform.Position.Y);
        writer.Put(transform.Position.Z);
        writer.Put(transform.Rotation.X);
        writer.Put(transform.Rotation.Y);
        writer.Put(transform.Rotation.Z);
        writer.Put(transform.Rotation.W);
        writer.Put(transform.Scale);
    }

    private static (Double3 Position, Quaternion Rotation, float Scale) ReadTransform(NetDataReader reader)
    {
        var position = new Double3(reader.GetDouble(), reader.GetDouble(), reader.GetDouble());
        var rotation = new Quaternion(reader.GetFloat(), reader.GetFloat(), reader.GetFloat(), reader.GetFloat());
        var scale = reader.GetFloat();
        return (position, rotation, scale);
    }

    /// <summary>
    /// A client's intent (Net-1 D1/D4): <see cref="SimCommand.Target"/> is never sent — per <see cref="SimCommand"/>'s
    /// own contract, a client has no authority over which entity its intent addresses (the connection determines
    /// that at the server's apply point), and <c>EntityRef.Id</c> is internal to <c>Agapanthe.World</c> — this
    /// project deliberately never needs to see it.
    /// </summary>
    public static void WriteSimCommand(NetDataWriter writer, in SimCommand command)
    {
        writer.Put(command.TargetTick);
        writer.Put(command.Kind);
        writer.Put(command.Vector.X);
        writer.Put(command.Vector.Y);
        writer.Put(command.Vector.Z);
        writer.Put(command.Scalar);
        writer.Put(command.Flags);
    }

    /// <summary>Reconstructs a <see cref="SimCommand"/> with <c>Target = default</c> — the server's
    /// <c>SimulationHost.ApplyCommand</c> is the only place ownership routing may fill it in.</summary>
    public static SimCommand ReadSimCommand(NetDataReader reader)
    {
        var targetTick = reader.GetLong();
        var kind = reader.GetByte();
        var vector = new Double3(reader.GetDouble(), reader.GetDouble(), reader.GetDouble());
        var scalar = reader.GetFloat();
        var flags = reader.GetUInt();
        return new SimCommand(targetTick, kind, default, vector, scalar, flags);
    }
}
