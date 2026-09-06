using System.Runtime.InteropServices;
using Agapanthe.Core;
using Agapanthe.World;

namespace Agapanthe.Engine;

/// <summary>
/// A stamped, typed unit of intent (MP-0d). Blittable — it is the future network wire format, so the layout is
/// pinned by a test (<c>SimCommandTests</c>) and by <see cref="LayoutKind.Sequential"/>.
/// <para>
/// <b><see cref="Kind"/> is opaque to the engine.</b> The application defines its own <c>byte</c> constants
/// (a spawn kind, a move kind, …) and the fields are reinterpreted per kind — <see cref="Vector"/> is a world
/// position for a spawn kind, a movement direction for a move kind. Engine never branches on <see cref="Kind"/>;
/// it only stamps, orders and drains. This is the <c>GameWorld.StructuralCommand</c> fat-value-struct idiom
/// (no class hierarchy, no per-command allocation, no boxing) lifted to the public gameplay layer.
/// </para>
/// <para>
/// <see cref="Vector"/> is a <see cref="Double3"/>, not a <see cref="System.Numerics.Vector3"/>: every world
/// position has been <c>double</c> since Phase 2, and a command that cannot carry one is a dead-end to retrofit.
/// </para>
/// <para>
/// <c>OriginatorId</c> (peer attribution) is deliberately absent — <see cref="SimCommand"/> is a transient
/// value, not a persisted format, so adding it later is additive; peer identity has no designed shape yet (debt).
/// </para>
/// <para>
/// <b><see cref="Target"/> is left <c>default</c> by the declarative path</b> (<see cref="InputMap"/> +
/// <see cref="InputTranslation"/>): a client has no authority over <i>which</i> entity its intent addresses — the
/// connection determines that, at the apply point. Ownership routing (connection → owned entity) will live in
/// <see cref="SimulationHost.ApplyCommand"/>, never in the map, and the type system already enforces it —
/// <c>EntityRef</c> has only an internal constructor, so <c>Agapanthe.Engine</c> cannot fabricate a target.
/// Direct <see cref="SimCommandQueue.Enqueue"/> callers (e.g. a discrete key) may still set <see cref="Target"/>.
/// </para>
/// Size: 8 (<see cref="TargetTick"/>) + 1 (<see cref="Kind"/>) + 7 pad + 8 (<see cref="Target"/>) + 24
/// (<see cref="Vector"/>) + 4 (<see cref="Scalar"/>) + 4 (<see cref="Flags"/>) = <b>56 bytes</b>.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct SimCommand(
    long TargetTick,
    byte Kind,
    EntityRef Target,
    Double3 Vector,
    float Scalar,
    uint Flags);

/// <summary>
/// Applies one command. The application supplies it to <see cref="SimulationHost.ApplyCommand"/>; it validates
/// (budget, target liveness) then executes — the server-authoritative split: the client always emits the intent,
/// the apply point decides whether to honour it.
/// </summary>
public delegate void SimCommandHandler(in SimCommand command);
