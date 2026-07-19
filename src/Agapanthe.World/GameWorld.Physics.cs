using System.Numerics;
using Agapanthe.Core;
using Arch.Core;
using Arch.Core.Extensions;

namespace Agapanthe.World;

// P3-M3 physics (v1): deterministic linear rigid bodies. Lives in GameWorld — like Propagate/Aggregate/Collect —
// so it reaches the internal components without exposing the ECS. Runs in the engine's Simulation stage, before
// PostSimulation re-derives world transforms and bounds from the positions it writes.
//
// One step: GATHER the bodies into flat scratch (index-addressable, grown never re-alloc'd) while integrating
// under gravity; broadphase them on a uniform grid (linked-list buckets in a reused dictionary); collect the
// overlapping pairs and RESOLVE them in an order sorted by GlobalId (never Arch's chunk order — the same tie-break
// lesson as the SortKey); then SCATTER the results back, applying the ground half-space last so a body pushed by a
// neighbour cannot end the step below the floor. Zero-alloc in steady state.
public sealed partial class GameWorld
{
    // A body is a drawable (renderable) PLUS velocity + rigid-body payload. Its own archetype, so the physics
    // query never touches a plain drawable and a plain drawable keeps its exact P3-M1 archetype (captures byte-
    // identical). GlobalId is included so contact resolution orders pairs deterministically.
    private static readonly QueryDescription BodyDesc =
        new QueryDescription().WithAll<GlobalId, WorldPosition, Velocity, RigidBody>();

    // --- Reused physics scratch (grown on demand, never re-allocated per frame) --------------------------------
    private Entity[] _pEntity = System.Array.Empty<Entity>();
    private Double3[] _pPos = System.Array.Empty<Double3>();
    private Vector3[] _pVel = System.Array.Empty<Vector3>();
    private float[] _pInvMass = System.Array.Empty<float>();
    private float[] _pRadius = System.Array.Empty<float>();
    private float[] _pRest = System.Array.Empty<float>();
    private ulong[] _pGid = System.Array.Empty<ulong>();
    private long[] _pCellX = System.Array.Empty<long>();
    private long[] _pCellY = System.Array.Empty<long>();
    private long[] _pCellZ = System.Array.Empty<long>();
    private int[] _cellNext = System.Array.Empty<int>();
    private readonly Dictionary<long, int> _cellHead = new();
    private ulong[] _pairKey = System.Array.Empty<ulong>();
    private long[] _pairPacked = System.Array.Empty<long>();

    // Positional-correction tunables (Baumgarte): leave a small overlap ("slop") uncorrected to avoid jitter, and
    // resolve only a fraction of the rest each step so a stack settles instead of exploding.
    private const double CorrectionSlop = 0.005;
    private const double CorrectionPercent = 0.8;

    /// <summary>
    /// Spawns a physics body: a baked drawable (exactly like <see cref="SpawnImported"/>) that additionally carries
    /// <see cref="Velocity"/> and <see cref="RigidBody"/>. Immediate (a LOAD-time seam, like SpawnImported), and
    /// returns the stable handle so a caller (or a test) can read it back. <paramref name="inverseMass"/> = 0 makes
    /// it immovable; <paramref name="radius"/> is the collision radius in world metres.
    /// </summary>
    public EntityRef SpawnBody(
        in ImportedEntitySpec spec, Vector3 velocity, float inverseMass, float restitution, float radius)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        AssertOwnerThread();
        var id = _nextGlobalId++;
        AssertNoTranslation(spec.RotationScale);
        var entity = _world.Create(
            new GlobalId { Value = id },
            new WorldTransform { Value = spec.RotationScale },
            new WorldPosition { Value = spec.Position },
            new MeshRef { Mesh = spec.Mesh, Material = spec.Material },
            new Bounds { Center = spec.BoundsCenter, Radius = spec.BoundsRadius },
            new RenderOrder { Value = spec.Order },
            new Velocity { Linear = velocity },
            new RigidBody { InverseMass = inverseMass, Restitution = restitution, Radius = radius });
        _live[id] = entity;
        return new EntityRef(id);
    }

    /// <summary>
    /// Advances every rigid body by one FIXED step (spec §2): semi-implicit Euler under gravity, sphere-sphere
    /// collision, and ground half-space collision. Deterministic (fixed dt + GlobalId-ordered contact resolution)
    /// and zero-alloc in steady state.
    /// </summary>
    public void StepPhysics(in PhysicsSettings settings)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        AssertOwnerThread();

        var dt = settings.FixedDt;
        var gravity = settings.Gravity;

        // Pass 1 — gather every body into flat scratch and integrate it (velocity then position: symplectic Euler).
        // maxRadius drives the broadphase cell size. Chunk order is stable within a frame (no structural change), so
        // the flat index k is a consistent addressing of the bodies for this whole step.
        var count = 0;
        var maxRadius = 0f;
        foreach (ref var chunk in _world.Query(in BodyDesc))
        {
            var entities = chunk.Entities;
            var ids = chunk.GetSpan<GlobalId>();
            var positions = chunk.GetSpan<WorldPosition>();
            var velocities = chunk.GetSpan<Velocity>();
            var bodies = chunk.GetSpan<RigidBody>();
            var n = chunk.Count;
            EnsureCapacity(count + n);
            for (var i = 0; i < n; i++)
            {
                var invMass = bodies[i].InverseMass;
                var v = velocities[i].Linear;
                var pos = positions[i].Value;
                if (invMass != 0f)
                {
                    v += gravity * dt;
                    pos += new Double3(v * dt);
                }

                _pEntity[count] = entities[i];
                _pGid[count] = ids[i].Value;
                _pVel[count] = v;
                _pPos[count] = pos;
                _pInvMass[count] = invMass;
                _pRest[count] = bodies[i].Restitution;
                var r = bodies[i].Radius;
                _pRadius[count] = r;
                if (r > maxRadius)
                {
                    maxRadius = r;
                }

                count++;
            }
        }

        // Pass 2 — sphere-sphere collision (skipped for a lone body or degenerate radii).
        if (count > 1 && maxRadius > 0f)
        {
            ResolveBodyContacts(count, maxRadius);
        }

        // Pass 3 — scatter back, applying the ground half-space LAST so a body shoved into the floor this step is
        // lifted out before it is written. Below restSpeed the normal velocity is zeroed so a resting body does not
        // micro-bounce forever (scaled to what gravity re-adds in a couple of steps: works for any gravity/dt).
        var groundY = settings.GroundY;
        var restSpeed = 2f * MathF.Abs(gravity.Y) * dt;
        for (var k = 0; k < count; k++)
        {
            if (_pInvMass[k] != 0f)
            {
                var pos = _pPos[k];
                var v = _pVel[k];
                var r = _pRadius[k];
                if (pos.Y - r < groundY)
                {
                    pos = new Double3(pos.X, groundY + r, pos.Z);
                    if (v.Y < 0f)
                    {
                        v.Y = -v.Y * _pRest[k];
                        if (v.Y < restSpeed)
                        {
                            v.Y = 0f;
                        }
                    }
                }

                _pPos[k] = pos;
                _pVel[k] = v;
            }

            _pEntity[k].Set(new WorldPosition { Value = _pPos[k] });
            _pEntity[k].Set(new Velocity { Linear = _pVel[k] });
        }
    }

    // Uniform-grid broadphase + sphere-sphere narrowphase + impulse/positional resolution, on the flat scratch.
    // Cell size 2·maxRadius guarantees any colliding pair shares a cell or an immediate (27-)neighbour, so the
    // 3×3×3 stencil is exhaustive. Pairs are collected once (higher-GlobalId side owns the pair) then resolved in
    // GlobalId order so the outcome never depends on Arch's iteration order.
    private void ResolveBodyContacts(int count, float maxRadius)
    {
        var cellSize = 2.0 * maxRadius;
        var invCell = 1.0 / cellSize;

        // Pre-grow to the worst case (every body in its own cell) so a scene that DISPERSES after the alloc-measure
        // warmup — bodies spreading into more distinct cells than at spawn — cannot trigger a dictionary resize on a
        // later frame and break the 0-alloc gate. Clear keeps the capacity, so this pays once (audit W2, low-level #2).
        _cellHead.EnsureCapacity(count);
        _cellHead.Clear();
        for (var k = 0; k < count; k++)
        {
            var cx = (long)Math.Floor(_pPos[k].X * invCell);
            var cy = (long)Math.Floor(_pPos[k].Y * invCell);
            var cz = (long)Math.Floor(_pPos[k].Z * invCell);
            _pCellX[k] = cx;
            _pCellY[k] = cy;
            _pCellZ[k] = cz;
            var key = CellHash(cx, cy, cz);
            _cellNext[k] = _cellHead.TryGetValue(key, out var head) ? head : -1;
            _cellHead[key] = k;
        }

        // Collect overlapping pairs. The higher-GlobalId body owns the pair (gid[j] < gid[k]): each unordered pair
        // is seen exactly once, independent of which cell the stencil walks first.
        var pairCount = 0;
        for (var k = 0; k < count; k++)
        {
            for (var dz = -1L; dz <= 1L; dz++)
            {
                for (var dy = -1L; dy <= 1L; dy++)
                {
                    for (var dx = -1L; dx <= 1L; dx++)
                    {
                        var key = CellHash(_pCellX[k] + dx, _pCellY[k] + dy, _pCellZ[k] + dz);
                        if (!_cellHead.TryGetValue(key, out var j))
                        {
                            continue;
                        }

                        for (; j != -1; j = _cellNext[j])
                        {
                            if (_pGid[j] >= _pGid[k])
                            {
                                continue; // dedup: only the smaller-GlobalId neighbour, once
                            }

                            var sumR = _pRadius[k] + _pRadius[j];
                            if (Double3.DistanceSquared(_pPos[k], _pPos[j]) >= (double)sumR * sumR)
                            {
                                continue; // hash collisions land here too — the overlap test filters them out
                            }

                            if (_pInvMass[k] == 0f && _pInvMass[j] == 0f)
                            {
                                continue; // two immovable bodies: nothing to resolve
                            }

                            EnsurePairCapacity(pairCount + 1);
                            // Sort key: smaller GlobalId in the high bits, larger in the low bits → total order by
                            // (min,max) GlobalId. Assumes GlobalId < 2^32 (holds for any run this milestone targets;
                            // the id is a per-run counter from 1).
                            _pairKey[pairCount] = (_pGid[j] << 32) | (uint)_pGid[k];
                            _pairPacked[pairCount] = ((long)k << 32) | (uint)j;
                            pairCount++;
                        }
                    }
                }
            }
        }

        if (pairCount == 0)
        {
            return;
        }

        System.Array.Sort(_pairKey, _pairPacked, 0, pairCount);

        for (var p = 0; p < pairCount; p++)
        {
            var packed = _pairPacked[p];
            var k = (int)(packed >> 32);
            var j = (int)(uint)packed;
            ResolvePair(k, j);
        }
    }

    private void ResolvePair(int k, int j)
    {
        var invA = _pInvMass[k];
        var invB = _pInvMass[j];
        var invSum = invA + invB;
        if (invSum == 0f)
        {
            return;
        }

        var delta = _pPos[k] - _pPos[j];
        var dist = Math.Sqrt(Double3.DistanceSquared(_pPos[k], _pPos[j]));
        var sumR = _pRadius[k] + _pRadius[j];

        // Contact normal from j toward k. Coincident centres (dist 0) have no defined normal → pick +Y so the
        // outcome stays deterministic and the pair still separates.
        Double3 normal;
        double penetration;
        if (dist > 1e-9)
        {
            normal = delta * (1.0 / dist);
            penetration = sumR - dist;
        }
        else
        {
            normal = new Double3(0, 1, 0);
            penetration = sumR;
        }

        // Impulse: reflect the approaching relative velocity along the normal, split by inverse mass. Restitution is
        // the softer of the two (a bouncy ball on clay barely bounces).
        var nf = new Vector3((float)normal.X, (float)normal.Y, (float)normal.Z);
        var relative = _pVel[k] - _pVel[j];
        var velAlongNormal = Vector3.Dot(relative, nf);
        if (velAlongNormal < 0f) // approaching
        {
            var e = MathF.Min(_pRest[k], _pRest[j]);
            var impulseMag = -(1f + e) * velAlongNormal / invSum;
            var impulse = nf * impulseMag;
            _pVel[k] += impulse * invA;
            _pVel[j] -= impulse * invB;
        }

        // Positional correction (Baumgarte): push the pair apart by the penetration beyond the slop, weighted by
        // inverse mass, so a heavier body barely moves and an immovable one does not move at all.
        if (penetration > CorrectionSlop)
        {
            var correction = normal * ((penetration - CorrectionSlop) * CorrectionPercent / invSum);
            _pPos[k] += correction * invA;
            _pPos[j] -= correction * invB;
        }
    }

    private void EnsureCapacity(int needed)
    {
        if (_pEntity.Length >= needed)
        {
            return;
        }

        var cap = Math.Max(needed, Math.Max(16, _pEntity.Length * 2));
        System.Array.Resize(ref _pEntity, cap);
        System.Array.Resize(ref _pPos, cap);
        System.Array.Resize(ref _pVel, cap);
        System.Array.Resize(ref _pInvMass, cap);
        System.Array.Resize(ref _pRadius, cap);
        System.Array.Resize(ref _pRest, cap);
        System.Array.Resize(ref _pGid, cap);
        System.Array.Resize(ref _pCellX, cap);
        System.Array.Resize(ref _pCellY, cap);
        System.Array.Resize(ref _pCellZ, cap);
        System.Array.Resize(ref _cellNext, cap);
    }

    private void EnsurePairCapacity(int needed)
    {
        if (_pairKey.Length >= needed)
        {
            return;
        }

        var cap = Math.Max(needed, Math.Max(32, _pairKey.Length * 2));
        System.Array.Resize(ref _pairKey, cap);
        System.Array.Resize(ref _pairPacked, cap);
    }

    // Classic spatial-hash mix of the three integer cell coordinates. Collisions are harmless: the narrowphase
    // distance test rejects any body that a collision drags into the wrong bucket.
    private static long CellHash(long cx, long cy, long cz)
        => (cx * 73856093L) ^ (cy * 19349663L) ^ (cz * 83492791L);

    /// <summary>Test/inspection accessor: the body's current linear velocity.</summary>
    internal Vector3 GetVelocity(EntityRef entity) => Deref(entity).Get<Velocity>().Linear;
}
