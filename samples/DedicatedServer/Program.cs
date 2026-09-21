using System.Diagnostics;
using System.Numerics;
using Agapanthe.Core;
using Agapanthe.Engine;
using Agapanthe.Net;
using Agapanthe.World;
using LiteNetLib;
using LiteNetLib.Utils;

// DedicatedServer (Net-1) — the seed of a real dedicated server. Same composition-root pattern as HeadlessSim
// (GameWorld + SimulationHost + PhysicsSystem, no window, no Vulkan device, no Silk.NET): the same claim MP-0a's
// headline artifact made, extended to a process that actually serves a live remote peer instead of running a
// fixed tick count and exiting.
//
// Protocol (spec D9, single client — D8), tags in Agapanthe.Net.NetProtocol (single source of truth — an audit
// finding flagged that the tags used to be redeclared identically in both this file and ThinClient's, a silent
// wire-mismatch risk):
//   AssignEntity     server -> client, once, on connect: the pilotable entity's GlobalId
//   EntityIntroduce  server -> client, once per drawable — either on connect (a full-world catch-up sweep, see
//                    below) or the first time it becomes dirty after that
//   PositionUpdate   server -> client, every tick a previously-introduced drawable is dirty
//   SimCommand       client -> server: intent: TargetTick is re-stamped to the server's own current tick on
//                    receipt (no clock sync in this sub-milestone — prediction/reconciliation is later work,
//                    out of scope per the spec). Vector is validated (finite, magnitude-clamped) before it ever
//                    reaches the simulation: an audit finding noted an unchecked NaN/huge vector from the wire
//                    would poison a body's Velocity irrecoverably and bypass the server's own speed cap.
//
// Runs until Ctrl+C (SIGINT/SIGTERM). Exit code 0 on clean shutdown, 1 on a usage error.

const float MoveSpeed = 5f;
const int MaxDirtyPerTick = 256; // bounds the per-tick scratch buffer; a wider wave is spread over following ticks

var port = 29_400;
for (var i = 0; i < args.Length; i++)
{
    if (args[i] == "--port" && i + 1 < args.Length)
    {
        if (!int.TryParse(args[++i], out port))
        {
            Console.Error.WriteLine("DedicatedServer: --port must be an integer.");
            return 1;
        }
    }
    else
    {
        Console.Error.WriteLine($"DedicatedServer: unknown argument '{args[i]}'. Usage: DedicatedServer [--port N]");
        return 1;
    }
}

using var world = new GameWorld();

// The single pilotable entity (D8) — a drawable, so it can be introduced/updated over the wire like anything
// else. Mesh/material handles are never dereferenced server-side (mirrors HeadlessSim's own BuildScene remark);
// the AssetKey is what the wire actually carries.
// A real cooked model, not a made-up key: ThinClient resolves this AssetKey through its own AssetCatalog
// (Contenu-1/2), so it must name content the shared content/ tree actually cooks.
var pilotSpec = new ImportedEntitySpec(
    new MeshHandle(0, 1), new MaterialHandle(0, 1), Double3.Zero, Matrix4x4.Identity, Vector3.Zero, 1f, 0u,
    new MeshRefKey(new AssetKey("models/DamagedHelmet.glb"), 0, 0));
var pilot = world.SpawnBody(in pilotSpec, Vector3.Zero, inverseMass: 1f, restitution: 0f, radius: 1f);
world.FlushStructuralChanges();
var pilotId = world.GetGlobalId(pilot);

var host = SimulationHost.CreateDefault(world);
var fixedDt = host.Settings.FixedDeltaSeconds;
var physicsSettings = new PhysicsSettings(Vector3.Zero, groundY: -100_000f, fixedDt: fixedDt);
host.Add(Stage.Simulation, new PhysicsSystem(world, in physicsSettings));

host.ApplyCommand = (in SimCommand cmd) =>
{
    if (!world.IsAlive(pilot))
    {
        return;
    }

    if (cmd.Kind != NetProtocol.MoveKind)
    {
        return;
    }

    var vector = cmd.Vector;
    if (!double.IsFinite(vector.X) || !double.IsFinite(vector.Y) || !double.IsFinite(vector.Z))
    {
        Console.Error.WriteLine("DedicatedServer: discarding SimCommand with a non-finite vector.");
        return;
    }

    // The client sends intent, not velocity — the server owns the actual speed cap (MoveSpeed), so an
    // uncooperative or buggy client cannot request an arbitrary magnitude (audit finding: a raw wire vector
    // reached SetBodyVelocity unclamped, letting `Vector = (1e9, 0, 0)` teleport the pilot).
    var intent = vector.ToVector3(Double3.Zero);
    if (intent != Vector3.Zero)
    {
        intent = Vector3.Normalize(intent);
    }

    world.SetBodyVelocity(pilot, intent * MoveSpeed);
};

using var channel = new NetChannel();
NetPeer? client = null;
var introducedToClient = new HashSet<ulong>();

channel.PeerConnected += peer =>
{
    client = peer;
    introducedToClient.Clear();

    var assign = new NetDataWriter();
    assign.Put(NetProtocol.AssignEntityTag);
    assign.Put(pilotId);
    peer.Send(assign, DeliveryMethod.ReliableOrdered);

    // Late-join catch-up (audit finding, engine-architect F1): a client that connects after the world has
    // settled must not depend on the dirty stream alone — a drawable that never moves again is never marked
    // dirty, so it would never be introduced. Sweep the entire live world once, independent of dirty state,
    // exactly as a real dedicated server must for any peer joining mid-session.
    foreach (var transform in world.SnapshotAllDrawables())
    {
        var introduce = new NetDataWriter();
        introduce.Put(NetProtocol.EntityIntroduceTag);
        PacketCodec.WriteEntityIntroduce(introduce, transform);
        peer.Send(introduce, DeliveryMethod.ReliableOrdered);
        introducedToClient.Add(transform.GlobalId);
    }

    Console.WriteLine($"DedicatedServer: client connected, assigned pilot entity {pilotId}.");
};

channel.Received += (_, reader) =>
{
    var tag = reader.GetByte();
    if (tag != NetProtocol.SimCommandTag)
    {
        Console.Error.WriteLine($"DedicatedServer: received unexpected packet tag {tag}, dropping.");
        return;
    }

    var command = PacketCodec.ReadSimCommand(reader);
    // No clock sync in this sub-milestone (spec out-of-scope: prediction/reconciliation) — the server is the
    // only source of tick truth, so it re-stamps on receipt rather than trusting the wire's TargetTick.
    host.Commands.Enqueue(command with { TargetTick = host.TickIndex });
};

channel.Listen(port);
Console.WriteLine($"DedicatedServer: listening on port {port}. Ctrl+C to stop.");

var running = true;
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    running = false;
};

var dirtyBuffer = new DrawableTransform[MaxDirtyPerTick];
var reusableWriter = new NetDataWriter();

// MP-0c's FixedTimestepAccumulator, not a hand-rolled Thread.Sleep(fixedDt*1000): the server is the sole time
// authority for every client, so it is the one host that most needs to not drift behind wall-clock (audit
// finding — the previous loop advanced simulated time by a constant fixedDt per iteration regardless of how
// long the iteration actually took, so real cadence and simulated cadence silently diverged under any load).
var accumulator = new FixedTimestepAccumulator(fixedDt);
var clock = Stopwatch.StartNew();
var lastElapsed = clock.Elapsed;

while (running)
{
    channel.PollEvents();

    var now = clock.Elapsed;
    var wallClockDelta = (float)(now - lastElapsed).TotalSeconds;
    lastElapsed = now;
    accumulator.AdvanceFrame(host, wallClockDelta);
    host.EndFrame();

    // Drained every tick regardless of connection state, not only "if a client is connected" (audit finding,
    // engine-architect F2): GlobalIds are never reused (MP-0b), so a server that ran for hours before its first
    // connection would otherwise accumulate one id per ever-dirtied entity in _networkDirtyIds forever. Draining
    // unconditionally keeps the set's size bounded by "dirty since last tick", never by uptime.
    var count = world.DrainDirtyDrawables(dirtyBuffer);
    if (client is { ConnectionState: ConnectionState.Connected } peer)
    {
        for (var i = 0; i < count; i++)
        {
            var transform = dirtyBuffer[i];
            reusableWriter.Reset();
            if (introducedToClient.Add(transform.GlobalId))
            {
                reusableWriter.Put(NetProtocol.EntityIntroduceTag);
                PacketCodec.WriteEntityIntroduce(reusableWriter, transform);
            }
            else
            {
                reusableWriter.Put(NetProtocol.PositionUpdateTag);
                PacketCodec.WritePositionUpdate(reusableWriter, transform);
            }

            peer.Send(reusableWriter, DeliveryMethod.ReliableOrdered);
        }
    }

    // A short sleep, not none: AdvanceFrame already catches up on however much wall-clock time actually
    // elapsed, so this only needs to keep the loop from busy-spinning between iterations, not to hit fixedDt
    // exactly (Windows' ~15 ms timer granularity makes that unreliable anyway — the accumulator, not the sleep,
    // is what keeps simulated time honest).
    Thread.Sleep(1);
}

Console.WriteLine("DedicatedServer: shutting down.");
return 0;
