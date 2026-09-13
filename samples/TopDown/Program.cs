using Agapanthe.App;
using Agapanthe.Platform;
using Agapanthe.Platform.App;
using TopDown;

// TopDown — Slice-2, the 2nd dissimilar slice (backlog §4quater): a top-down orthographic app, proving
// AppHost/IGame/SceneRecipe are reusable outside samples/Sandbox, and that Camera's new orthographic
// projection mode actually works end to end.
//
//   dotnet run --project samples/TopDown   -> AGAPANTHE_SCENE (default "topdown")

return AppHost.RunClient(
    new TopDownGame(),
    new EngineWindowAdapter(new EngineWindow("Agapanthe TopDown", 1280, 720)),
    args);
