using System.Numerics;
using Agapanthe.Core;
using Agapanthe.Engine;
using Agapanthe.World;

// HeadlessSim (MP-0a) — the simulation, running with no window, no Vulkan device and no Silk.NET.
//
// This is the seed of the dedicated server. Everything it does, a client does too: the SAME GameWorld, the SAME
// SimulationHost, the SAME PhysicsSystem, in the same stages and closed by the same structural barrier. What a
// client adds is the render half, which lives in a different assembly this project does not reference.
//
// Modes:
//   --ticks N              how many simulation steps to run (default 600)
//   --save <path>          write a VS-1 snapshot of the final state
//   --load <path>          start from a snapshot instead of the built-in scene
//   --bodies N             built-in scene size (default 8)
// Exit code 0 on success, 1 on a usage or I/O error.

const int DefaultTicks = 600;
const int DefaultBodies = 8;
const float FixedDt = 1f / 60f;

var ticks = DefaultTicks;
var bodies = DefaultBodies;
string? savePath = null;
string? loadPath = null;

for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--ticks" when i + 1 < args.Length:
            if (!int.TryParse(args[++i], out ticks) || ticks <= 0)
            {
                Console.Error.WriteLine("HeadlessSim: --ticks must be a positive integer.");
                return 1;
            }

            break;
        case "--bodies" when i + 1 < args.Length:
            if (!int.TryParse(args[++i], out bodies) || bodies <= 0)
            {
                Console.Error.WriteLine("HeadlessSim: --bodies must be a positive integer.");
                return 1;
            }

            break;
        case "--save" when i + 1 < args.Length:
            savePath = args[++i];
            if (string.IsNullOrWhiteSpace(savePath))
            {
                Console.Error.WriteLine("HeadlessSim: --save needs a path.");
                return 1;
            }

            break;
        case "--load" when i + 1 < args.Length:
            loadPath = args[++i];
            if (string.IsNullOrWhiteSpace(loadPath))
            {
                Console.Error.WriteLine("HeadlessSim: --load needs a path.");
                return 1;
            }

            break;
        default:
            Console.Error.WriteLine(
                $"HeadlessSim: unknown or incomplete argument '{args[i]}'. "
                + "Usage: HeadlessSim [--ticks N] [--bodies N] [--load <snapshot>] [--save <snapshot>]");
            return 1;
    }
}

// --bodies only describes the built-in scene; silently ignoring it next to --load would leave someone wondering
// why their entity count is wrong.
if (loadPath is not null && bodies != DefaultBodies)
{
    Console.Error.WriteLine("HeadlessSim: --bodies applies to the built-in scene and conflicts with --load.");
    return 1;
}

try
{
    using var world = new GameWorld();

    if (loadPath is not null)
    {
        using var input = File.OpenRead(loadPath);
        world.Load(input);
        Console.WriteLine($"HeadlessSim: loaded '{loadPath}' ({world.LiveEntityCount} entities).");
    }
    else
    {
        BuildScene(world, bodies);
        Console.WriteLine($"HeadlessSim: built a scene of {world.LiveEntityCount} entities.");
    }

    // The engine's default simulation schedule, plus physics. Note what is absent: no Renderer, no ResourceRegistry,
    // no Camera, no swapchain — none of which SimulationHost can even name.
    var host = SimulationHost.CreateDefault(world);
    var settings = new PhysicsSettings(new Vector3(0f, -9.81f, 0f), groundY: 0f, fixedDt: FixedDt);
    host.Add(Stage.Simulation, new PhysicsSystem(world, in settings));

    // One tick per "frame" here. A real server will grow an accumulator; BeginFrame stays outside that inner loop,
    // which is exactly why SimulationHost separates it from Tick.
    for (var i = 0; i < ticks; i++)
    {
        host.BeginFrame();
        host.Tick(FixedDt);
        host.EndFrame();
    }

    Console.WriteLine(
        $"HeadlessSim: ran {host.FrameIndex} ticks, {world.LiveEntityCount} entities alive, "
        + $"last frame {host.LastFrameMs:F3} ms / {host.LastFrameAllocatedBytes} B.");

    if (savePath is not null)
    {
        using var output = File.Create(savePath);
        world.Save(output);
        Console.WriteLine($"HeadlessSim: saved '{savePath}'.");
    }
}
catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
    or NotSupportedException or WorldSerializationException)
{
    Console.Error.WriteLine($"HeadlessSim: {ex.GetType().Name}: {ex.Message}");
    return 1;
}

Console.WriteLine("HeadlessSim: PASS — simulation ran to completion with no GPU.");
return 0;

// Falling bodies over a ground plane, plus a small transform hierarchy so PropagateTransforms has work to do. The
// mesh and material handles are never dereferenced by the World (it only sorts and batches by them), so default
// handles are honest here rather than a stub: a server genuinely has no meshes.
static void BuildScene(GameWorld world, int bodies)
{
    for (var i = 0; i < bodies; i++)
    {
        var spec = new ImportedEntitySpec(
            new MeshHandle(0, 1), new MaterialHandle(0, 1),
            new Double3(i * 0.9, 4 + (i * 1.7), 0), Matrix4x4.Identity, Vector3.Zero, 1f, (uint)i);
        world.SpawnBody(in spec, new Vector3(0.05f * i, 0f, 0f), inverseMass: 1f, restitution: 0.4f, radius: 1f);
    }

    var root = world.Spawn(new Double3(40, 8, 0), Quaternion.Identity, 1f);
    var mid = world.Spawn(new Double3(1, 0, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.4f), 1f, root);
    world.Spawn(new Double3(0, 1, 0), Quaternion.Identity, 2f, mid);
    world.FlushStructuralChanges();
}
