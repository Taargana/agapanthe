# Board — Club Architect packaged separation

Status: **in-progress** (DECOMPOSE & PLAN done, EXECUTE starting)
Spec: `docs/plans/2026-09-27-club-architect-package-separation-design.md` (approved, 3 review rounds, final 4.40/5 PASS)

## Project Conventions

- .NET 10 SDK-style projects, `Agapanthe.slnx`, `Directory.Build.props` at repo root.
- Test runner: xUnit, `tests/Agapanthe.Tests`, run via `dotnet test`.
- Build gate: `TreatWarningsAsErrors=true`, `IsAotCompatible=true` per shippable project.
- Commit convention: Conventional Commits style (`feat(scope): ...`), `Co-Authored-By: Claude Sonnet 5` trailer per this session's system reminder.
- Never commit/push without explicit user request (standing rule, this session).

## Rollback Point

Agapanthe: commit `82b67af` on `spike/noesis-probe` (untouched by this work — new branch `feat/package-separation` cut from `main` `91673ff`).
Club Architect: repo does not exist yet; nothing to roll back beyond deleting `D:\MyProjects\club-architect`.

## Task Graph

```
Wave 1 (parallel)              Wave 2 (parallel, dep W1)         Wave 3 (gate)        Wave 4          Wave 5 (gate)     Wave 6 (tail, sequential)
AW-001 Agapanthe branch   ──┐   AW-002 NBGV wiring          ──┐                                                        AW-009 Self Code Review
AW-006 ClubArch scaffold  ──┤   AW-003 IsPackable×12        ──┼─▶ AW-005 pack+verify ─▶ AW-007 CA exe ─▶ AW-008 verify ─▶ AW-010 Requirements Validation
                             │   AW-004 Graphics+shaders     ──┘                                                        AW-011 Full Project Verification
                             └───────────────────────────────────────────────────────────▶ (AW-007 also depends on AW-006)
```

## Tasks

| ID | Title | Type | Size | Deps | Status |
|---|---|---|---|---|---|
| AW-001 | Agapanthe: branch off `main` (`feat/package-separation`) | infra | S | — | pending |
| AW-006 | Club Architect: repo scaffold (git init, slnx, NuGet.config, Directory.Build.props) | infra | S | — | pending |
| AW-002 | Agapanthe: NBGV wiring (Directory.Build.props + version.json) | config | S | AW-001 | pending |
| AW-003 | Agapanthe: IsPackable=true + metadata on 12/13 projects (all but Graphics) | config | M | AW-001 | pending |
| AW-004 | Agapanthe: Agapanthe.Graphics.csproj — IsPackable=true + shaders contentFiles | config | S | AW-001 | pending |
| AW-005 | Agapanthe: commit, pack -c Debug, verify 13 nupkg + full test suite green | verify | gate | AW-002,003,004 | pending |
| AW-007 | Club Architect: minimal executable (csproj + IGame + Program.cs) | code | S | AW-005,006 | pending |
| AW-008 | Club Architect: restore/build/run/publish-AOT verification | verify | gate | AW-007 | pending |
| AW-009 | Self Code Review | tail | — | AW-008 | pending |
| AW-010 | Requirements Validation (D1-D11) | tail | — | AW-009 | pending |
| AW-011 | Full Project Verification | tail | — | AW-010 | pending |

## Per-task detail

See `docs/plans/2026-09-27-club-architect-package-separation-design.md` for the full decisions
(D1-D11). Per-task acceptance criteria as approved in the DECOMPOSE & PLAN gate:

**AW-001**: `git checkout main && git checkout -b feat/package-separation`. No push, no merge —
local branch only.

**AW-002**: `Directory.Build.props` gets `<IsPackable>false</IsPackable>` default +
`Nerdbank.GitVersioning` `PackageReference` (`PrivateAssets="All"`). New root `version.json`
(`"version": "0.1"`). Build must still succeed unchanged.

**AW-003**: `<IsPackable>true</IsPackable>` + short `<Description>` on: `Agapanthe.Core`, `World`,
`Rendering`, `Ui`, `Assets`, `Scene`, `Engine`, `Engine.Render`, `Audio`, `Platform`,
`Platform.App`, `App`.

**AW-004**: `Agapanthe.Graphics.csproj` — `IsPackable=true` + metadata + shader content item:
`Pack="true" PackagePath="contentFiles/any/any/shaders/" PackageCopyToOutput="true"`, explicitly
NO `CopyToOutputDirectory` (would double-copy into Sandbox/TopDown/tests' own `ProjectReference`
consumption and trip NETSDK1152).

**AW-005**: Commit Wave 2's changes (NBGV is commit-derived — packing from a dirty tree silently
reuses the last version). `dotnet pack Agapanthe.slnx -c Debug -o artifacts/package` → exactly 13
`.nupkg`, one shared version. `dotnet test tests/Agapanthe.Tests` → 1016 tests green (the known
`CopySyncStateTests` flake may need one rerun).

**AW-006**: `git init D:\MyProjects\club-architect`. `ClubArchitect.slnx`, `NuGet.config` (source
`agapanthe-local` → `D:\MyProjects\agapanthe\artifacts\package`), `Directory.Build.props`
(`net10.0`, `Nullable=enable`, `TreatWarningsAsErrors=true` — no `NoWarn` here, that's AW-007's).

**AW-007**: `src/ClubArchitect/ClubArchitect.csproj` (Exe, `PublishAot=true`, `PackageReference
Agapanthe.Platform.App` at AW-005's version, `NoWarn` = Sandbox's IL2104/IL3000/IL3002/IL3053
list). `ClubArchitectGame.cs` (`IGame`, one empty `ISceneRecipe`). `Program.cs` (minimal subset of
`samples/Sandbox/Program.cs`, no `AGAPANTHE_IBL_TEST` branch).

**AW-008**: `dotnet restore` → `obj/project.assets.json` shows all 13 Agapanthe deps as `"type":
"package"`, zero `"type": "project"`. `dotnet build` → `bin/<config>/net10.0/shaders/mesh.frag`
exists. `dotnet run` → window opens, ticks, `Escape` closes, console prints `ResourceTracker: no
leaks (...)` (no `F3` overlay expected — accepted debt, not a failure). `dotnet publish -r win-x64
--self-contained -p:PublishAot=true` succeeds.

**AW-009/010/011**: mandatory tail — code review against the spec, D1-D11 walk-through, final full
verification pass (Agapanthe suite + AW-008's checklist together).

## Deferred Work

Everything in the spec's "Explicitly deferred debt" section — cook tooling distribution, no debug
overlay/profiler in v1, `Agapanthe.Net`/`Agapanthe.Ui.Noesis` not packaged, no git remote for Club
Architect, no Release distribution story, the football-manager game's own design.
