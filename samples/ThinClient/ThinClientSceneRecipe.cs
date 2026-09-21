using System.Numerics;
using Agapanthe.App;
using Agapanthe.Assets;
using Agapanthe.Assets.Model;
using Agapanthe.Core;
using Agapanthe.Engine;
using Agapanthe.Net;
using Agapanthe.Rendering;
using Agapanthe.World;
using LiteNetLib;
using LiteNetLib.Utils;
using Silk.NET.Input;

namespace ThinClient;

/// <summary>
/// Net-1 D7 — the client's "scene" is populated ENTIRELY by incoming network packets, never a cooked
/// <c>.agscene</c>. <see cref="Build"/> only opens the connection and wires the packet handlers; every entity
/// this world will ever contain is spawned from an <see cref="PacketCodec.ReadEntityIntroduce"/> packet.
/// <para>
/// <b>The client never simulates</b> (D1): it registers nothing on <c>Stage.Simulation</c>, and never calls
/// <see cref="GameWorld.SetBodyVelocity"/> or any physics API. <c>AppHost</c>'s own frame loop still calls
/// <c>SimulationHost.Tick</c> automatically every frame — that is harmless here (empty Input/Simulation stages,
/// and <c>PropagateTransforms</c> only ever iterates hierarchical <c>LocalTransform</c> nodes, which the network-
/// spawned drawables here never carry).
/// </para>
/// <para>
/// <b>A manual <see cref="GameWorld.FlushStructuralChanges"/> IS needed</b> right after every
/// <see cref="GameWorld.SpawnDeferred"/> (a live two-process run caught this): <see cref="Agapanthe.Net.NetChannel.PollEvents"/>
/// can drain an <c>EntityIntroduce</c> and a following <c>PositionUpdate</c> for the SAME entity in one
/// synchronous call — the server marks a static body dirty every tick unconditionally, so both packets can queue
/// back to back on the wire — with no automatic <c>Tick()</c> in between to apply the deferred spawn. Relying on
/// the next automatic <c>Tick()</c> alone left <c>SetDrawableTransform</c> throwing "does not name a live entity"
/// for an entity introduced and updated within the same <c>PollEvents()</c> batch.
/// </para>
/// </summary>
internal sealed class ThinClientSceneRecipe : ISceneRecipe
{
    public string Name => "thin-client";

    public void Build(SimSceneContext sim, PresentationSceneContext? presentationOrNull)
    {
        var presentation = presentationOrNull
            ?? throw new InvalidOperationException("ThinClient has no headless mode — it only ever renders.");

        var (host, port) = ParseHostPort(sim.Args);

        // No .agscene means no [environment]/[camera] block either (D7) — set both by hand, minimally: no
        // ambient/skybox (the network never carries lighting), fixed camera a few metres back from the origin
        // where the server places its one entity today.
        presentation.Renderer.SetEnvironment(BlackEnvironment.Build());
        presentation.Camera.Position = new Double3(0, 1.5, 5);
        presentation.Renderer.Lights.Ambient = new Vector3(0.15f, 0.15f, 0.15f);
        presentation.Renderer.Lights.Directional = new DirectionalLight
        {
            Direction = Vector3.Normalize(new Vector3(-0.4f, -1f, -0.3f)),
            Color = Vector3.One,
            Intensity = 3f,
        };

        var channel = new NetChannel();
        // No hook exists on ISceneRecipe/PresentationSceneContext to join AppHost's ordered GPU teardown (audit
        // finding — the socket previously outlived the whole process with no Dispose call at all); ProcessExit
        // is a safety net that at least stops the NetManager thread and disconnects the peer cleanly rather than
        // leaking both until the OS reclaims them.
        AppDomain.CurrentDomain.ProcessExit += (_, _) => channel.Dispose();
        var serverIdToLocal = new Dictionary<ulong, EntityRef>();
        var loadedAssets = new Dictionary<AssetKey, ImportedEntitySpec[]>();

        channel.Received += (_, reader) =>
        {
            var tag = reader.GetByte();
            switch (tag)
            {
                case NetProtocol.AssignEntityTag:
                    Console.WriteLine($"ThinClient: assigned pilot entity {reader.GetULong()}.");
                    break;
                case NetProtocol.EntityIntroduceTag:
                    Introduce(sim, presentation, loadedAssets, serverIdToLocal, PacketCodec.ReadEntityIntroduce(reader));
                    break;
                case NetProtocol.PositionUpdateTag:
                    ApplyUpdate(sim.World, serverIdToLocal, PacketCodec.ReadPositionUpdate(reader));
                    break;
                default:
                    Console.Error.WriteLine($"ThinClient: unexpected packet tag {tag}, dropping.");
                    break;
            }
        };

        var serverPeer = channel.Connect(host, port);
        Console.WriteLine($"ThinClient: connecting to {host}:{port}.");

        presentation.Window.Updated += _ =>
        {
            channel.PollEvents();

            var moveX = (presentation.Window.IsKeyDown(Key.D) ? 1f : 0f) - (presentation.Window.IsKeyDown(Key.A) ? 1f : 0f);
            var moveZ = (presentation.Window.IsKeyDown(Key.S) ? 1f : 0f) - (presentation.Window.IsKeyDown(Key.W) ? 1f : 0f);
            if (moveX == 0f && moveZ == 0f)
            {
                return;
            }

            // TargetTick is irrelevant here — DedicatedServer re-stamps it to its own current tick on receipt
            // (no clock sync in this sub-milestone, spec out-of-scope). Target stays default: the client has no
            // authority over which entity its intent addresses (SimCommand's own contract).
            var command = new SimCommand(0, NetProtocol.MoveKind, default, new Double3(moveX, 0, moveZ), 0f, 0u);
            var writer = new NetDataWriter();
            writer.Put(NetProtocol.SimCommandTag);
            PacketCodec.WriteSimCommand(writer, command);
            serverPeer.Send(writer, DeliveryMethod.ReliableOrdered);
        };
    }

    private static void Introduce(
        SimSceneContext sim,
        PresentationSceneContext presentation,
        Dictionary<AssetKey, ImportedEntitySpec[]> loadedAssets,
        Dictionary<ulong, EntityRef> serverIdToLocal,
        DrawableTransform transform)
    {
        if (transform.AssetIdentity.IsNone)
        {
            Console.Error.WriteLine($"ThinClient: entity {transform.GlobalId} introduced with no asset identity, dropping.");
            return;
        }

        var key = transform.AssetIdentity.Key;
        if (!loadedAssets.TryGetValue(key, out var cachedSpecs))
        {
            ModelAsset model;
            try
            {
                model = sim.Catalog.LoadModel(key);
            }
            catch (AssetException ex)
            {
                Console.Error.WriteLine($"ThinClient: cannot resolve asset '{key}': {ex.Message}");
                return;
            }

            var (_, loaded) = presentation.Registry.Load(presentation.Device, model, presentation.Renderer.MaterialSetLayout, key);
            loadedAssets[key] = loaded;
            cachedSpecs = loaded;
        }

        // A LocalMesh naming no mesh in the CLIENT's own copy of this asset is a content mismatch between server
        // and client (different cook, different content/ tree) — not a value to silently clamp into range
        // (audit finding: SceneMaterializer already treats the same field as fatal for the same reason, Contenu-3b).
        var localMesh = transform.AssetIdentity.LocalMesh;
        if (localMesh < 0 || localMesh >= cachedSpecs.Length)
        {
            Console.Error.WriteLine(
                $"ThinClient: entity {transform.GlobalId} names LocalMesh {localMesh} for '{key}', which has " +
                $"{cachedSpecs.Length} mesh(es) client-side — content mismatch, dropping.");
            return;
        }

        var baseSpec = cachedSpecs[localMesh];

        var spec = new ImportedEntitySpec(
            baseSpec.Mesh,
            baseSpec.Material,
            transform.Position,
            Matrix4x4.CreateScale(transform.Scale) * Matrix4x4.CreateFromQuaternion(transform.Rotation),
            baseSpec.BoundsCenter,
            baseSpec.BoundsRadius,
            baseSpec.Order,
            transform.AssetIdentity);

        var local = sim.World.SpawnDeferred(spec);
        // Flush NOW, not at the next automatic Tick(): NetChannel.PollEvents() can drain an EntityIntroduce and a
        // following PositionUpdate for the SAME entity in one synchronous call (the server marks a static body
        // dirty every tick, unconditionally — see GameWorld.Physics.cs — so introduce and update can queue back
        // to back on the wire), with no Tick() in between to apply the deferred spawn. A live two-process run
        // caught this: SetDrawableTransform on the still-deferred entity threw "does not name a live entity."
        sim.World.FlushStructuralChanges();
        serverIdToLocal[transform.GlobalId] = local;
        Console.WriteLine($"ThinClient: introduced entity {transform.GlobalId} ('{key}').");
    }

    private static void ApplyUpdate(GameWorld world, Dictionary<ulong, EntityRef> serverIdToLocal, DrawableTransform transform)
    {
        if (!serverIdToLocal.TryGetValue(transform.GlobalId, out var local))
        {
            Console.Error.WriteLine(
                $"ThinClient: PositionUpdate for entity {transform.GlobalId}, never introduced — dropping " +
                "(protocol violation: DedicatedServer should always send EntityIntroduce first).");
            return;
        }

        world.SetDrawableTransform(local, transform);
    }

    private static (string Host, int Port) ParseHostPort(string[] args)
    {
        var host = "localhost";
        var port = 29_400;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--host" && i + 1 < args.Length)
            {
                host = args[++i];
            }
            else if (args[i] == "--port" && i + 1 < args.Length && int.TryParse(args[i + 1], out var p))
            {
                port = p;
                i++;
            }
        }

        return (host, port);
    }
}
