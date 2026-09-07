using Agapanthe.App;
using Agapanthe.Platform;
using Sandbox;

// Sandbox — the engine's reference application and integration test bed.
//
// AppHost (Agapanthe.App) owns the GPU + window bootstrap, the fixed-step frame loop, the strict-order teardown
// (the 0-leak gate) and the capture harness. SandboxGame carries the scene recipes; each ISceneRecipe builds one
// scene (spawn entities, register systems, frame the camera, wire input).
//
//   dotnet run --project samples/Sandbox                        -> AGAPANTHE_SCENE (default "model")
//   dotnet run --project samples/Sandbox -- MetalRoughSpheres.glb   -> model viewer, a fixture by bare name
//   AGAPANTHE_IBL_TEST=<prefix>                                  -> the standalone M7 IBL-generation tool

if (Environment.GetEnvironmentVariable("AGAPANTHE_IBL_TEST") is { Length: > 0 } iblPrefix)
{
    return IblTestTool.Run(iblPrefix);
}

return AppHost.RunClient(
    new SandboxGame(),
    new EngineWindowAdapter(new EngineWindow("Agapanthe Sandbox", 1280, 720)),
    args);
