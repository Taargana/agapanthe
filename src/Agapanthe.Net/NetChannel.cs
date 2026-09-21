using LiteNetLib;
using LiteNetLib.Utils;

namespace Agapanthe.Net;

/// <summary>
/// A thin wrapper around LiteNetLib's <see cref="NetManager"/>/<see cref="EventBasedNetListener"/> — the one
/// place this project touches sockets at all, so <see cref="PacketCodec"/> stays testable without a live
/// connection. Verified empirically (Net-1 spec D3) to publish NativeAOT-clean and connect/accept identically
/// under JIT and NativeAOT.
/// </summary>
public sealed class NetChannel : IDisposable
{
    private const string ConnectionKey = "agapanthe-net-1";

    private readonly EventBasedNetListener _listener = new();
    private readonly NetManager _manager;
    private bool _disposed;

    /// <summary>Raised once per received payload, with the peer it arrived from. A handler that throws (e.g. a
    /// malformed/truncated packet reaching <c>PacketCodec</c>) is caught and logged here, never left to unwind
    /// out of <see cref="PollEvents"/> — an untrusted remote peer must not be able to kill the owning process by
    /// sending bad bytes.</summary>
    public event Action<NetPeer, NetDataReader>? Received;

    /// <summary>Raised when a peer finishes connecting (server: an incoming client; client: the server).</summary>
    public event Action<NetPeer>? PeerConnected;

    /// <summary>Incremented every time a <see cref="Received"/> handler throws (Release-visible signal, patron
    /// <c>SanitisedInputCount</c>/<c>DiscardedCommandCount</c> — never a silent swallow).</summary>
    public long MalformedPacketCount { get; private set; }

    private bool _listening;

    public NetChannel()
    {
        _manager = new NetManager(_listener);
        _listener.NetworkReceiveEvent += (peer, reader, _, _) =>
        {
            try
            {
                Received?.Invoke(peer, reader);
            }
            catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException))
            {
                MalformedPacketCount++;
                Console.Error.WriteLine($"NetChannel: dropping malformed packet from {peer}: {ex.Message}");
            }
            finally
            {
                reader.Recycle();
            }
        };
        _listener.PeerConnectedEvent += peer => PeerConnected?.Invoke(peer);
    }

    /// <summary>Starts listening for incoming connections on <paramref name="port"/> (server role). Accepts
    /// only peers presenting <see cref="ConnectionKey"/> — a placeholder handshake secret; Net-1 has no real
    /// authentication (deferred, see spec's out-of-scope list — multi-client/peer identity is later work).
    /// Idempotent: a second call does not double-subscribe <c>ConnectionRequestEvent</c> (which would otherwise
    /// double-accept every incoming request).</summary>
    public void Listen(int port)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_listening)
        {
            return;
        }

        _listening = true;
        _listener.ConnectionRequestEvent += request => request.AcceptIfKey(ConnectionKey);
        _manager.Start(port);
    }

    /// <summary>Connects to a server (client role) — starts the underlying manager with no listen port if it
    /// is not already running (a server-role channel that also wants to dial out would already be started by
    /// <see cref="Listen"/>).</summary>
    public NetPeer Connect(string host, int port)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_manager.IsRunning)
        {
            _manager.Start();
        }

        return _manager.Connect(host, port, ConnectionKey);
    }

    /// <summary>Drains queued network events (connects, disconnects, received payloads) — call once per tick,
    /// from the owner thread, exactly like <see cref="Agapanthe.Engine.SimCommandQueue.DrainUpTo"/>'s own
    /// single-threaded contract. A no-op once disposed, rather than throwing — a caller's event loop may still
    /// tick once more during teardown before it notices.</summary>
    public void PollEvents()
    {
        if (_disposed)
        {
            return;
        }

        _manager.PollEvents();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _manager.DisconnectAll();
        _manager.Stop();
        Received = null;
        PeerConnected = null;
    }
}
