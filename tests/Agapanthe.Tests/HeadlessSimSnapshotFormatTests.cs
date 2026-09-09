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

    // MP-0d --drive gate (mirrors HeadlessSim/Program.cs RunDrive).
    private const byte DriveMoveKind = 1;
    private const byte DriveBrakeKind = 2;
    private const int DriveBrakeBit = 0;
    private const float DriveSpeed = 5f;

    // Mirrors samples/HeadlessSim/Program.cs's BuildScene + main loop exactly (defaults: --ticks 600 --bodies 8).
    private static byte[] RunHeadlessSimScene()
    {
        using var world = new GameWorld();

        for (var i = 0; i < Bodies; i++)
        {
            var spec = new ImportedEntitySpec(
                new MeshHandle(0, 1), new MaterialHandle(0, 1),
                new Double3(i * 0.9, 4 + (i * 1.7), 0), Matrix4x4.Identity, Vector3.Zero, 1f, (uint)i,
                new MeshRefKey(new AssetKey("headless/body"), 0, 0)); // mirrors HeadlessSim/Program.cs
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

    // Pinned 2026-09-09 (Contenu-3a, format v4 + audit fix): reproduced identically by `dotnet run` (JIT) and a
    // NativeAOT win-x64 publish of samples/HeadlessSim. Superseded b3fa79d8… (v4, all-None, 1842 bytes) after the
    // engine-architect finding — BuildScene's specs now carry a real AssetKey("headless/body"), so the headless
    // artifact's snapshot proves genuine asset identity. +15 bytes = the one key-table entry.
    private const string ExpectedMd5 = "6a13dd54c1db32d35a15332bff0395e7";
    private const int ExpectedByteLength = 1857;

    [Fact]
    public void HeadlessSimDefaultScene_SnapshotHash_MatchesPinnedValue()
    {
        var bytes = RunHeadlessSimScene();

        Assert.Equal(ExpectedByteLength, bytes.Length);
        var actualMd5 = Convert.ToHexStringLower(MD5.HashData(bytes));
        Assert.Equal(ExpectedMd5, actualMd5);
    }

    // Mirrors samples/HeadlessSim/Program.cs's RunDrive exactly (`--drive --ticks 600`): one zero-gravity body
    // steered by a scripted per-tick InputSnapshot through the MP-0d declarative translation + ApplyCommand.
    private static byte[] RunHeadlessSimDriveScene()
    {
        using var world = new GameWorld();

        var spec = new ImportedEntitySpec(
            new MeshHandle(0, 1), new MaterialHandle(0, 1), Double3.Zero, Matrix4x4.Identity, Vector3.Zero, 1f, 0u,
            new MeshRefKey(new AssetKey("headless/drive-body"), 0, 0)); // mirrors HeadlessSim/Program.cs RunDrive
        var body = world.SpawnBody(in spec, Vector3.Zero, inverseMass: 1f, restitution: 0f, radius: 1f);
        world.FlushStructuralChanges();

        var host = SimulationHost.CreateDefault(world);
        var settings = new PhysicsSettings(Vector3.Zero, groundY: -100_000f, fixedDt: FixedDt);
        host.Add(Stage.Simulation, new PhysicsSystem(world, in settings));

        var map = new InputMap();
        map.BindAxisVector(DriveMoveKind, axisX: 0, axisY: 1, axisZ: 2);
        map.BindButton(DriveBrakeBit, DriveBrakeKind, ButtonTrigger.OnPress);
        host.InputMap = map;

        host.ApplyCommand = (in SimCommand cmd) =>
        {
            if (!world.IsAlive(body))
            {
                return;
            }

            switch (cmd.Kind)
            {
                case DriveMoveKind:
                    world.SetBodyVelocity(body, cmd.Vector.ToVector3(Double3.Zero) * DriveSpeed);
                    break;
                case DriveBrakeKind:
                    world.SetBodyVelocity(body, Vector3.Zero);
                    break;
            }
        };

        host.SampleInput = () =>
        {
            var s = default(InputSnapshot);
            var t = host.TickIndex;
            s.Axes[0] = t < 60 ? 1f : t < 120 ? -1f : 0f;
            if (t == 120)
            {
                s.Pressed = 1UL << DriveBrakeBit;
            }

            return s;
        };

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

    // Pinned 2026-09-07 (Contenu-1, format v3). Reproduced identically by `dotnet run` (JIT) and a NativeAOT
    // win-x64 publish of samples/HeadlessSim (`--drive --ticks 600 --save`). Supersedes
    // 97e786f0455a53d856b9ba4affca1003 (v2, 208 bytes): +6 key table, -4 on the single body's MeshRef = 210.
    // Re-derive with: dotnet run --project samples/HeadlessSim -c Debug -- --drive --ticks 600 --save <path>
    // Pinned 2026-09-09 (Contenu-3a, format v4 + audit fix). Superseded f8120d25… (v4, all-None, 210 bytes) — the
    // RunDrive body now carries AssetKey("headless/drive-body"). +21 bytes = the one key-table entry.
    private const string ExpectedDriveMd5 = "f6053226f8c13b55589b29be103a8e66";
    private const int ExpectedDriveByteLength = 231;

    [Fact]
    public void HeadlessSimDriveScene_SnapshotHash_MatchesPinnedValue()
    {
        var bytes = RunHeadlessSimDriveScene();

        Assert.Equal(ExpectedDriveByteLength, bytes.Length);
        var actualMd5 = Convert.ToHexStringLower(MD5.HashData(bytes));
        Assert.Equal(ExpectedDriveMd5, actualMd5);
    }
}
