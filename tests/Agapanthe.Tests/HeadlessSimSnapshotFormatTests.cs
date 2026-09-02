using System.Numerics;
using System.Security.Cryptography;
using Agapanthe.Core;
using Agapanthe.Engine;
using Agapanthe.World;

namespace Agapanthe.Tests;

/// <summary>
/// MP-0b W3: the format gate <c>samples/HeadlessSim</c>'s snapshot hash lived in only as prose (<c>CLAUDE.md</c>,
/// <c>AVANCEMENT.md</c>, the session-26 board) — no test or script asserted it. Builds the exact scene
/// <c>HeadlessSim/Program.cs</c>'s defaults produce (<c>--ticks 600 --bodies 8</c>), saves it, and pins the MD5.
/// A milestone that changes the snapshot format (this one) should not leave its own format gate living in a
/// paragraph. Re-derive with:
/// <code>dotnet run --project samples/HeadlessSim -c Debug -- --ticks 600 --bodies 8 --save &lt;path&gt;</code>
/// then <c>Get-FileHash -Algorithm MD5</c>, and verify JIT and a NativeAOT publish agree before repinning here.
/// </summary>
[Collection("World")]
public sealed class HeadlessSimSnapshotFormatTests
{
    private const int Ticks = 600;
    private const int Bodies = 8;
    private const float FixedDt = 1f / 60f;

    // Mirrors samples/HeadlessSim/Program.cs's BuildScene + main loop exactly (defaults: --ticks 600 --bodies 8).
    private static byte[] RunHeadlessSimScene()
    {
        using var world = new GameWorld();

        for (var i = 0; i < Bodies; i++)
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

        var host = SimulationHost.CreateDefault(world);
        var settings = new PhysicsSettings(new Vector3(0f, -9.81f, 0f), groundY: 0f, fixedDt: FixedDt);
        host.Add(Stage.Simulation, new PhysicsSystem(world, in settings));

        for (var i = 0; i < Ticks; i++)
        {
            host.BeginFrame();
            host.Tick(FixedDt);
            host.EndFrame();
        }

        using var ms = new MemoryStream();
        world.Save(ms);
        return ms.ToArray();
    }

    // Pinned 2026-09-02 (MP-0b W3, format v2): reproduced identically by `dotnet run` (JIT) and a NativeAOT
    // win-x64 publish of samples/HeadlessSim. Superseded 7c889fec0df503fe8137ef6c28c7751a (v1, 1852 bytes) — the
    // 16-byte UniverseId this milestone adds to the header accounts for the size delta (1852 -> 1868).
    private const string ExpectedMd5 = "7e8dc68f5a25914c84677a7a53ad3a58";
    private const int ExpectedByteLength = 1868;

    [Fact]
    public void HeadlessSimDefaultScene_SnapshotHash_MatchesPinnedValue()
    {
        var bytes = RunHeadlessSimScene();

        Assert.Equal(ExpectedByteLength, bytes.Length);
        var actualMd5 = Convert.ToHexStringLower(MD5.HashData(bytes));
        Assert.Equal(ExpectedMd5, actualMd5);
    }
}
