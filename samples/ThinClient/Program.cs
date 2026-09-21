using Agapanthe.App;
using Agapanthe.Platform;
using Agapanthe.Platform.App;
using ThinClient;

// ThinClient (Net-1) — renders whatever the dedicated server tells it; never simulates locally (D1/D7).
//
//   dotnet run --project samples/ThinClient -- [--host <name>] [--port <n>]   (defaults: localhost, 29400)

return AppHost.RunClient(
    new ThinClientGame(),
    new EngineWindowAdapter(new EngineWindow("Agapanthe ThinClient", 1280, 720)),
    args);
