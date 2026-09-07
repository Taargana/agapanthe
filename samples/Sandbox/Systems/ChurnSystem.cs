using System.Numerics;
using Agapanthe.Core;
using Agapanthe.Engine;
using Agapanthe.World;

namespace Sandbox;

// Churn stress (P3-M2 F2), as a Simulation system: each frame spawn a small hierarchy and, once the ring is full,
// despawn the oldest root (cascading to its children). The scheduler's end-of-Simulation barrier applies the
// structural changes; there is no manual flush. Nodes carry no MeshRef, so they never touch what is drawn.
internal sealed class ChurnSystem(GameWorld world, int perFrame) : ISystem
{
    private readonly Queue<EntityRef> _roots = new();

    public void Execute(in TickContext ctx)
    {
        for (var i = 0; i < perFrame; i++)
        {
            var root = world.Spawn(Double3.Zero, Quaternion.Identity, 1f);
            world.Spawn(new Double3(0, 1, 0), Quaternion.Identity, 1f, root);
            world.Spawn(new Double3(1, 0, 0), Quaternion.Identity, 1f, root);
            _roots.Enqueue(root);
        }

        while (_roots.Count > perFrame * 8)
        {
            world.Despawn(_roots.Dequeue());
        }
    }
}
