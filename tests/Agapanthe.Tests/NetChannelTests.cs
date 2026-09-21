using Agapanthe.Core;
using Agapanthe.Net;
using LiteNetLib;
using LiteNetLib.Utils;

namespace Agapanthe.Tests;

/// <summary>
/// Net-1 — <see cref="NetChannel"/> over real localhost sockets: the scratch verification done manually this
/// session (LiteNetLib connects/accepts identically under JIT and NativeAOT), now committed as a real test, plus
/// an actual payload round-trip (the manual scratch check stopped at the handshake).
/// </summary>
public sealed class NetChannelTests
{
    [Fact]
    public void ServerAndClient_ConnectAndExchangeAPayload_OverLocalhost()
    {
        using var server = new NetChannel();
        using var client = new NetChannel();

        DrawableTransform? received = null;
        NetPeer? serverPeer = null;
        server.PeerConnected += peer => serverPeer = peer;
        server.Received += (_, reader) => received = PacketCodec.ReadPositionUpdate(reader);

        server.Listen(29_050);
        var clientPeer = client.Connect("localhost", 29_050);

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (serverPeer is null && DateTime.UtcNow < deadline)
        {
            server.PollEvents();
            client.PollEvents();
            Thread.Sleep(10);
        }

        Assert.NotNull(serverPeer);

        var sample = new DrawableTransform(
            GlobalId: 7,
            AssetIdentity: MeshRefKey.None,
            Position: new Double3(1, 2, 3),
            Rotation: System.Numerics.Quaternion.Identity,
            Scale: 1f);
        var writer = new NetDataWriter();
        PacketCodec.WritePositionUpdate(writer, sample);
        clientPeer.Send(writer, DeliveryMethod.ReliableOrdered);

        deadline = DateTime.UtcNow.AddSeconds(5);
        while (received is null && DateTime.UtcNow < deadline)
        {
            server.PollEvents();
            client.PollEvents();
            Thread.Sleep(10);
        }

        Assert.NotNull(received);
        Assert.Equal(sample.GlobalId, received!.Value.GlobalId);
        Assert.Equal(sample.Position, received.Value.Position);
    }

    [Fact]
    public void MalformedPacket_IsDroppedRatherThanCrashingTheChannel()
    {
        // Audit finding (csharp-lowlevel F1): an unhandled read past the end of a truncated/forged datagram used
        // to unwind straight out of PollEvents() — on DedicatedServer that kills the one process every connected
        // client depends on. A single untrusted peer must never be able to do that.
        using var server = new NetChannel();
        using var client = new NetChannel();

        NetPeer? serverPeer = null;
        var goodPacketsReceived = 0;
        server.PeerConnected += peer => serverPeer = peer;
        server.Received += (_, reader) =>
        {
            // Deliberately reads past what a 1-byte malformed packet provides — the handler itself throwing is
            // exactly the scenario under test (PacketCodec's real read methods behave the same way on truncated
            // input, verified separately).
            reader.GetULong();
            goodPacketsReceived++;
        };

        server.Listen(29_051);
        var clientPeer = client.Connect("localhost", 29_051);

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (serverPeer is null && DateTime.UtcNow < deadline)
        {
            server.PollEvents();
            client.PollEvents();
            Thread.Sleep(10);
        }

        Assert.NotNull(serverPeer);

        // A malformed (too-short) packet, then a well-formed one right after: the first must be dropped, not
        // bring down the channel or block the second from arriving.
        var malformed = new NetDataWriter();
        malformed.Put((byte)1);
        clientPeer.Send(malformed, DeliveryMethod.ReliableOrdered);

        var wellFormed = new NetDataWriter();
        wellFormed.Put(42UL);
        clientPeer.Send(wellFormed, DeliveryMethod.ReliableOrdered);

        deadline = DateTime.UtcNow.AddSeconds(5);
        while (goodPacketsReceived == 0 && DateTime.UtcNow < deadline)
        {
            server.PollEvents();
            client.PollEvents();
            Thread.Sleep(10);
        }

        Assert.Equal(1, goodPacketsReceived);
        Assert.Equal(1, server.MalformedPacketCount);
    }
}
