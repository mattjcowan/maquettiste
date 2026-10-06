# Maquettiste.Functions

The editor's server side: the static-site-hosting function handlers in `_functions/` that serve the `/api/*` contract
(`docs/api/openapi.yaml`, phase2-design.md §3) over the Maquettiste engine.

- `_functions/` holds exactly the files that go into the site zip. The host compiles them at deploy time against the engine
  packages baked into the image; `Directives.cs` carries the `#:package` lines (with the stamped engine version) and is excluded
  from this project's own build.
- `Maquettiste.Functions.csproj` compiles the rest of `_functions/*.cs` as a library, so the IDE, `dotnet build` and
  `tests/Maquettiste.Functions.Tests` see the same code.

Conventions every handler follows:

- The body runs inside `Api.GuardAsync` (or `Api.Guard`). The host catches a handler's exception itself and answers
  `500 text/plain`, so the contract's problems are produced here: `IOException` and `UnauthorizedAccessException` become
  503 `model-unavailable`, anything else 500 `internal` with the trace id, and the failure is logged.
- Errors are RFC 9457 problems with a stable `code` (`Api.Problem`); bodies are read with `Api.ReadJsonAsync` (4 MB cap,
  JSON only); writes need `If-Match` (`Api.TryGetIfMatch`).
- `SignInGate` (middleware, order 0) decides local trust, cookies and tokens before any handler runs.
- Realtime events stay under `Api.MaxEventBytes` (the host refuses payloads over 256 KB).
- The assistant (`AssistEndpoints.cs`, `AssistService.cs`; erratum E44) is the one place that talks to the host's `IAiChat`. Its
  handlers check the caller themselves (function calls bypass `[AiAccess]`), `POST /api/assist/chat` answers with server-sent events
  written by an `IResult`, and conversations and per-day token usage live under the cache folder (`<cache>/assist/<user hash>/`),
  never in the model. The loop follows the host's tool rules (host-contracts.md §1.3); the tools come from the engine's `AgentTools`.

- Snapshots (`SnapshotEndpoints.cs`; docs/engineering/snapshots.md): `/api/snapshots` over the engine's `SnapshotLibrary`. The model
  reads the editor needs take `?snapshot=<id>` (`SnapshotEndpoints.AsOfAsync` swaps in the snapshot's read-only store and generation
  service); `SignInGate` refuses `?snapshot=` on anything else (409 `snapshot-read-only` for a write, 400 `snapshot-unsupported`
  for another read), and lets `POST /api/snapshots/import` through with an `application/zip` body. An import body over the host's
  request bound (536,870,912 bytes in static-site-hosting 0.4.0) answers 413 with `tooLarge` and MQ1011, from `Content-Length` or
  from the reader's 413, instead of the 503 `model-unavailable` the reader's `IOException` would map to.

Test with `dotnet test tests/Maquettiste.Functions.Tests`; `EditorHost` runs requests through the gate and a reflection router
the way the host does, including its `500 text/plain` for an escaped exception (`EditorHost.HostFailures`).
