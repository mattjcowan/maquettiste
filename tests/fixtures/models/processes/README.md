# The gate 3 fixture

Two processes with parallel states and gates (phase-3-design.md section 8.1), their 14 scenarios, the actors, and the entities
the stores use, mapped explicitly to the PostgreSQL database `main`: `SalesOrderLifecycle` (a lifecycle of `SalesOrder`, bound
to `SalesOrder.status`) and `PurchaseApproval` (an orchestration of `PurchaseRequest`). Beside the model, a C# solution holds what
the example packs cannot generate: the hand-written halves of the generated pairs.

| Path | Holds |
| --- | --- |
| `.maquettiste/` | The model and the settings: `csharp-dapper` writes under `src/` (`generatedFolder` `Processes.Data/Generated`, built; `partialFolder` and `endpointsFolder` `Processes.Data/Custom`, committed; `testsFolder` `Processes.Tests/Generated`, built), `sql-ddl` under `db/`, `process-docs` under `docs/` (committed) |
| `src/Processes.slnx` | The solution: `Processes.Data` and `Processes.Tests` |
| `src/Processes.Data/Custom/` | The companions the pack wrote once, committed so a fresh clone builds, with the fixture's code: the `notShipped` guard (`SalesOrderLifecycleHandlers.cs`: no Shipped state is active), the `checkBudget` service (at most 10,000; the budget scenarios start above it) and the `createPurchaseOrder` service with the compliance review hook (`PurchaseApprovalServices.cs`), the stores over the generated repositories, each refusing a save over a version it did not load (`SalesOrderLifecycleStore.cs`: `SalesOrder` with its `configuration`, `SalesOrderHistory`; `PurchaseApprovalStore.cs`: `ProcessInstance`, `PurchaseRequest`; both: `GateSignature`), the `GateSignatures.From` helper beside the `GateSignature` companion (`Purchasing/GateSignature.cs`), and the pipeline's authorization hook (`Dispatch/Pipeline.cs`, which lets a caller act as any actor it names). `Runtime/ProcessHost.cs`, the other entity companions (`ProcessInstance`, `PurchaseRequest`, `SalesOrder`, `SalesOrderHistory`), the machine companions, `PurchaseApprovalHandlers.cs` and `SalesOrderLifecycleServices.cs` are as the pack wrote them. The endpoint files with their regions (`Processes/*/Endpoints/`) are generated here too but not committed |
| `src/Processes.Tests/ScenarioHost.cs` | What the generated scenario tests run against: the dispatcher over both processes, on an in-memory SQLite database built from `db/main/schema.sql`, with the fixture's services, which must agree with each service result a scenario reports; with `MAQUETTISTE_SCENARIO_TRACE` naming a folder it writes what every command returned, which `ProcessTests` compares with the engine's replay |
| `tools/gate3.sh` | The gate 3 script |
| `Directory.Build.props`, `Directory.Packages.props`, `.editorconfig` | Isolation: the repository's build settings stop here (the repository's `global.json` still applies) |

Everything `maquettiste generate` writes is gitignored here (`db/`, `docs/`, the two `Generated/` folders, the endpoint files with
their regions under `Custom/Processes/*/Endpoints/`, the manifests and snapshots), so the fixture is generated into a copy.

## Build and test

```sh
# From the repository root: a copy of the fixture with the schemas and the three packs its settings name, generated, built
# and tested.
mkdir -p tmp/processes && cp -a tests/fixtures/models/processes/. tmp/processes/
mkdir -p tmp/processes/.maquettiste/.schema && cp -r schemas/v1 tmp/processes/.maquettiste/.schema/v1
mkdir -p tmp/processes/.maquettiste/templates
cp -r packs/csharp-dapper packs/sql-ddl packs/process-docs tmp/processes/.maquettiste/templates/
dotnet build -c Release src/Maquettiste.Cli
dotnet src/Maquettiste.Cli/bin/Release/net10.0/Maquettiste.Cli.dll --repo tmp/processes generate
dotnet build tmp/processes/src/Processes.slnx -warnaserror
dotnet test tmp/processes/src/Processes.slnx --no-build
```

The 14 generated tests (one per scenario) send every step through the dispatcher to the generated interpreter on a manual clock
and assert the acceptance (a refusal with the engine's reason), the gate audit outcomes, the active states, the changed context
and the outcome. `tests/Maquettiste.Packs.Tests/ProcessTests.cs` does the same in a temporary copy, compares what the running
generated interpreter returned after every step with the engine's replay, checks the goldens under
`tests/fixtures/golden/*/processes/`, and shows that the two cancel scenarios fail when `notShipped` answers the opposite.

## Gate 3

```sh
docker build -f docker/Dockerfile -t mattjcowan/maquettiste:dev .
tests/fixtures/models/processes/tools/gate3.sh            # every step, then down
tests/fixtures/models/processes/tools/gate3.sh walk test  # some steps (after prepare)
```

Steps, each timed, the first failure stops the run: `prepare` (the fixture copied to `tmp/gate3` without generated output,
the schemas and packs added, the image started on port 8098 as compose project `pr-gate3`), `walk` (Playwright,
`src/editor/tests/e2e/gate3.spec.ts`: a path of each process simulated in the panel and recorded as `RecordedInTheEditor`,
then plan and apply), `validate` (criterion 1), `verify` (2, 3: all 16 scenarios pass, the two recorded ones included),
`generate` (4, 6: `--check` clean, `--jobs 1` and `--jobs N` byte-identical and equal to the editor's apply, exports
identical), `build` (4), `test` (5: 16 generated tests pass), `roundtrip` (7: XState export and import `--into` byte for
byte), `bench` (8: `Maquettiste.Bench time-processes`, no budget missed; binding locally, advisory on a GitHub-hosted runner),
`down`. Environment: `MAQUETTISTE_IMAGE`, `MAQUETTISTE_PORT`, `GATE3_DIR`, `MAQUETTISTE_EDITOR_TOKEN`, `GATE3_BENCH_ARGS`,
`GATE3_BENCH_ADVISORY` (see the script's header).
`.github/workflows/gate3.yml` runs the same steps.
