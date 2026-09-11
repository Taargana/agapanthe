using System.Numerics;
using Agapanthe.Assets;
using Agapanthe.Assets.Model;
using Agapanthe.Assets.Scene;
using Agapanthe.Core;
using Agapanthe.Scene;
using Agapanthe.World;

namespace Agapanthe.Tests;

/// <summary>
/// Contenu-3b — <see cref="SceneLoader.LoadHeadless"/> is the dedicated-server / <c>HeadlessSim --scene</c> entry
/// point: it populates the world with no GPU and returns the physics settings for the caller to attach.
/// </summary>
[Collection("World")]
public sealed class SceneLoaderHeadlessTests
{
    private static ModelAsset FakeModel(string name) => new()
    {
        Meshes = [new MeshAsset
        {
            Positions = [new(-1, -1, -1), new(1, 1, 1)], Indices = [0, 1, 0], MaterialIndex = 0,
            BoundsCenter = Vector3.Zero, BoundsRadius = 1.7320508f,
        }],
        Materials = [new MaterialAsset { Name = "m" }],
        Images = [],
        Name = name,
    };

    [Fact]
    public void LoadHeadless_PopulatesWorld_AndReturnsPhysicsForCallerToAttach()
    {
        using var world = new GameWorld();
        var def = new SceneDefinition
        {
            Name = "headless-default",
            WorldOrigin = Double3.Zero,
            Entities =
            [
                new SceneEntity
                {
                    Model = new AssetKey("models/x.glb"), LocalMesh = 0, LocalMat = 0, Position = new Double3(0, 4, 0),
                    Body = new SceneBody { InverseMass = 1f, Restitution = 0.4f, Radius = 1.7320508f },
                },
            ],
            Lights = [],
            Ambient = Vector3.Zero,
            Camera = new SceneCamera { Mode = SceneCameraMode.FrameBounds, FovY = 60f },
            Environment = new SceneEnvironment { Mode = SceneEnvironmentMode.None },
            Physics = new ScenePhysics { Gravity = new Vector3(0, -9.81f, 0), GroundY = 0f },
        };

        // The Func overload stands in for AssetCatalog.LoadModel (no cooked content needed here).
        var result = SceneMaterializer.Materialize(def, _ => FakeModel("models/x.glb"), world, 1f / 60f);

        Assert.Equal(1, world.LiveEntityCount);
        Assert.NotNull(result.Physics);
        Assert.Equal(0f, result.Physics!.Value.GroundY);
    }
}
