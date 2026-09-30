# Editor design (phase 2)

**Status (2026-09-29): phase 2 is complete and gate 2 passes** (README.md, "What is built"; `samples/reference-app/tools/gate2.sh`). What remains of the design documents is listed in their status columns and in HANDOFF.md; phase 3 starts from `phase-3-brief.md`.

The contract that the phase 2 workers implement against: the editor's C# functions (`src/Maquettiste.Functions`), the editor SPA (`src/editor`), the Docker image (`docker/`) and the gate 2 reference application (`samples/reference-app`). `SPEC.md` is the authority (S14 means its Section 14); `engine-design.md` stays binding for the engine, and `host-contracts.md` for what static-site-hosting 0.2.0 provides. The HTTP contract is `docs/api/openapi.yaml`; where this file and the OpenAPI file disagree, fix one of them in the same commit. Choices this file makes are listed in §10 with ids (PD1, PD2…). File, type, route and member names here are binding.

**Phase 2 scope (fixed).** In: the shell and design system (S14, S15), the Entities, Database, Mappings, Generate and Settings workspaces, the JSON API subset of S16 that they need, realtime sync and presence, token sign-in for non-loopback access, local mode in Docker, the development loop of S4, and gate 2 (S21: a 200-entity reference application's data layer modeled and generated end to end from the editor, and compiles). Out: processes (phase 3); Templates, Source control and Team workspaces, accounts, roles other than admin, invites, OIDC, soft locks, git writes, hosted mode, importers, AI assist, refactorings beyond rename and bulk edit (phase 4). The shell shows the out-of-scope workspaces as disabled rail entries.

## 1. Repository layout (phase 2 additions)

```
maquettiste/
├── docs/api/openapi.yaml               # the editor API, OpenAPI 3.1; source of the SPA's client and mocks
├── src/Maquettiste.Functions/
│   ├── Maquettiste.Functions.csproj    # library for IDE, build and tests; compiles _functions/*.cs except Directives.cs
│   ├── _functions/                     # exactly the files that go into the site zip (§3.1)
│   └── README.md
├── src/editor/                         # Vite + React SPA (§4)
├── tests/Maquettiste.Functions.Tests/  # xunit v3, StaticSiteHost.Functions.Testing fakes (§3.9)
├── docker/
│   ├── Dockerfile                      # FROM mattjcowan/static-site-hosting:0.2.0 (§6)
│   ├── entrypoint.sh                   # first-boot deploy of the bundled site zip
│   ├── closure/closure.csproj          # restores the engine's dependency closure into the local feed
│   ├── closure/Directory.Build.props   # `<Project />`, with Directory.Packages.props (`<Project />`) and .editorconfig
│   │                                   #   (`root = true`): keeps the repo's build settings out of closure.csproj (§6.1)
│   ├── compose.yaml                    # the S4 local-mode compose file
│   └── dev-billing.sh                  # copies the billing fixture to tmp/billing and starts the editor on it
├── samples/reference-app/              # gate 2 (§7); isolated from the repo's build settings the same way (§7.1)
└── .github/workflows/                  # editor.yml (SPA), functions tests join ci.yml, image.yml (CI smoke), publish-image.yml and publish-package.yml (version tags), gate2.yml
```

`maquettiste.slnx` gains the functions project and its test project. `Directory.Packages.props` gains `StaticSiteHost.Abstractions` (already pinned at 0.2.0) as a used reference and, for tests only, `YamlDotNet`. The engine is referenced as a `ProjectReference` in the repo and as `#:package` in the zip (§3.1).

## 2. How local mode fits together

1. `docker compose up` starts the image. The entrypoint (§6.2) computes the container's gateway address, deploys the bundled `site.zip` (SPA, `_functions/`, `_variables.json`, `_headers`) to the site `maquettiste.localhost` through the host's deploy API when the site is missing or older than the image, and runs the host.
2. The host compiles `_functions/` (restoring `Maquettiste.Engine` from the image's local NuGet source, offline), loads it, runs `[ConfigureServices]` and starts the two background services: the model watcher loads the model and publishes changes; the job worker runs plans and applies.
3. The browser opens `http://maquettiste.localhost:8080`. The request reaches the container from the Docker gateway (the `127.0.0.1` port binding is proxied). Kestrel listens dual-stack (`http://+:8080` binds `[::]`), so an IPv4 peer is reported IPv4-mapped: measured on Linux, `::ffff:172.17.0.1` for the gateway and `::ffff:127.0.0.1` from inside the container. The sign-in middleware normalises the address with `MapToIPv4` and treats loopback and the gateway as the local developer when the request names a local host and carries no forwarding header (§3.3, PD9), so the SPA loads with no login.
4. The SPA reads `/api/project`, `/api/model/index` and the elements it shows, connects to `/_host/realtime` through `/_host/site.js`, joins `editors`, and edits with `PUT` + `If-Match`. Every save, disk edit and CLI run comes back as `model.changed`, followed by `validation.completed`.
5. Generate queues a plan job (`POST /api/generate/plan`), follows `job.progress` in `job:{id}`, shows the plan's changes and per-file diffs, and applies it by id.

## 3. Functions (P2-F)

### 3.1 Files and limits

The site zip's `_functions/` holds the files below and nothing else. Host limits: 50 files, 1 MB each, 4 MB in all (restored packages do not count). Budget: 24 files, 40 KB each at most, 400 KB in all; a test enforces the host limits and the budget (§3.9).

| File | Holds |
| --- | --- |
| `Directives.cs` | Only directives: `#:package Maquettiste.Engine@<version>` and `#:package StaticSiteHost.Abstractions@*`. The committed file carries the version from `Directory.Build.props` (`1.0.0-alpha.1` today); `pack-site.mjs --engine-version <v>` rewrites that line in the zip's copy with the per-build engine version of the image (§6.1, PD24), and without the flag keeps the committed one. No other file has a `#:` line |
| `EditorSetup.cs` | `[ConfigureServices] EditorSetup.Configure`; `EditorSettings` (variables → `EngineOptions`, mode, cache path) |
| `EditorAuth.cs` | `EditorAuth` (local peers, token check, session cookie, sign-in throttle), `EditorUser(string Name, string DisplayName, string Role, string Via)` |
| `SignInGate.cs` | `[Middleware(Order = 0)] SignInGate.Run`, the inline sign-in page, origin and content-type checks |
| `RealtimeHooks.cs` | `[RealtimeConnect] RealtimeHooks.Connect`, `[RealtimeJoin] RealtimeHooks.Join`, `[AiAccess] RealtimeHooks.Ai` |
| `Api.cs` | `Api`: the static `JsonSerializerOptions` (`new(JsonSerializerDefaults.Web)`), `Json(value, status)`, `Problem(code, title, status)`, ETag format and `If-Match` parse, `ReadBodyAsync` (4 MB cap), `StatusOf(SaveOutcome, bool created)` |
| `SessionEndpoints.cs` `HealthEndpoints.cs` `ProjectEndpoints.cs` `ModelEndpoints.cs` `DiagramEndpoints.cs` `DatabaseEndpoints.cs` `ValidationEndpoints.cs` `GenerateEndpoints.cs` `JobEndpoints.cs` `TemplateEndpoints.cs` `PresenceEndpoints.cs` | The handlers of §3.7, one `public static class` per file |
| `EditorEvents.cs` | `EditorEvents` (publishes `model.changed`, `validation.completed`, `project.changed`, `job.*`, `presence.changed`), `JobCompletedEvent` and `JobCounts` (§3.6), `PresenceRegistry` |
| `ModelWatcher.cs` | `[BackgroundService] ModelWatcher.Run`, `[Every("30s")] ModelWatcher.Rescan` |
| `JobWorker.cs` | `[BackgroundService] JobWorker.Run` |
| `GitStatus.cs` | `GitStatusReader` (`git -C <repo> status --porcelain=v1 --branch -- .maquettiste`, 2 s timeout) |

Rules: every file carries its own `using` lines (the host adds LINQPad's default imports; the csproj does not, and code must compile in both); class names are unique across files (`CS0101`); handlers return `IResult`, never another object; no static mutable state (services hold state); no `async void`. `Maquettiste.Functions.csproj` is `Microsoft.NET.Sdk` with `FrameworkReference Microsoft.AspNetCore.App`, `Compile Remove="_functions/Directives.cs"`, `ProjectReference` to the engine and `PackageReference StaticSiteHost.Abstractions`; it inherits `Directory.Build.props` (warnings as errors, XML docs). The host strips `#:` directives itself; a normal project cannot compile them (CS9298), which is why they live in one file (PD19).

### 3.2 Site variables (`_variables.json`)

| Variable | Default | Use |
| --- | --- | --- |
| `MAQUETTISTE_MODE` | `${env:MAQUETTISTE_MODE}` | `local` (also when empty) or `hosted` (phase 4; phase 2 answers 503 `model-unavailable` in hosted mode) |
| `MAQUETTISTE_REPO_ROOT` | `${env:MAQUETTISTE_REPO_ROOT}` | `EngineOptions.RepoRoot` |
| `MAQUETTISTE_EDITOR_TOKEN` | `${env:MAQUETTISTE_EDITOR_TOKEN}`, `secret: true` | bearer token and cookie sign-in; empty = local peers only |
| `MAQUETTISTE_CACHE_DIR` | `${env:MAQUETTISTE_CACHE_DIR}` | index cache root on the host volume (image default `/data/maquettiste/cache`) |
| `MAQUETTISTE_LOCAL_PEERS` | `${env:MAQUETTISTE_LOCAL_PEERS}` | comma-separated IP addresses trusted as the local developer in local mode, parsed into `IPAddress` values (each normalised with `MapToIPv4` when IPv4-mapped); an entry that does not parse is logged once and ignored. `*` trusts every peer; it is safe only behind a loopback-only port binding and is not the documented default (§11, open point 3) |
| `MAQUETTISTE_LOCAL_TRUST` | `${env:MAQUETTISTE_LOCAL_TRUST}` | `on` (also when empty) or `off`; `off` turns local trust off, so every caller needs the token or the cookie (set it when the port is reachable through a reverse proxy) |
| `MAQUETTISTE_LOCAL_USER` | `local` | user name of the local developer (presence) |

`EditorSettings.From(ISiteVariables, DirectoryInfo data)` builds, without I/O: `EngineOptions { RepoRoot = MAQUETTISTE_REPO_ROOT, ModelRoot = data.FullName, CacheDirectory = <cache root>/<first 16 hex of SHA-256(full RepoRoot)>, MaxDegreeOfParallelism = 0 }`. The model root is the site's data folder, which the compose file bind-mounts onto `.maquettiste/` (S4); the engine then reports model paths with the `.maquettiste/` prefix (`ModelPaths.Prefix`). The cache root falls back to `<temp>/maquettiste-cache` outside the image. This settles the host-contracts open point on the cache location (PD10).

### 3.3 Sign-in gate

`SignInGate.Run(HttpContext context, Func<Task> next, EditorAuth auth)` runs on every request except `/_host/*` and `/healthz` (the host answers those first). In order:

1. **Identify.** `auth.Identify(context)` returns an `EditorUser` or `null`: (a) **local trust**: local mode, `MAQUETTISTE_LOCAL_TRUST` is not `off`, the remote address, normalised first (`if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();`, because Kestrel reports `::ffff:172.17.0.1` for the IPv4 gateway), is loopback or equal to a parsed `MAQUETTISTE_LOCAL_PEERS` entry, the `Host` header without its port is `localhost`, a `*.localhost` name, `127.0.0.1` or `[::1]`, and the request has none of `Forwarded`, `X-Forwarded-For`, `X-Forwarded-Host` or `X-Real-IP` → `local` user, role `admin`, via `local`. The last two conditions stop a reverse proxy on the same machine, which connects from loopback or the gateway, from signing every remote visitor in as admin: it either forwards a public host name or adds a forwarding header, and its visitors then need the token. A proxy that rewrites `Host` to `maquettiste.localhost` and strips those headers looks exactly like the browser, which is why `MAQUETTISTE_LOCAL_TRUST=off` exists; `EditorSetup.Configure` logs one warning when a token is set while local trust is on; (b) `Authorization: Bearer <t>` with `t` equal to the token (SHA-256 of both compared with `CryptographicOperations.FixedTimeEquals`) → user `token`, `admin`, via `token`; (c) cookie `mq_session` that unprotects (`IDataProtectionProvider.CreateProtector("Maquettiste.Session.v1")`), is younger than 7 days and carries the current token's fingerprint (first 16 hex of its SHA-256) → its user, via `cookie`. A present but wrong bearer token is 401 `bad-token` even for a local peer.
2. **Forgery checks.** For `POST`, `PUT`, `PATCH`, `DELETE`: an `Origin` header that is present and differs from `<scheme>://<Host>` is 403 `forbidden-origin`; a `POST` or `PUT` under `/api/` whose content type is not `application/json` is 415 `unsupported-media-type`, except the form post of `POST /api/session`. With these, a page on another origin cannot drive the credential-less local mode: JSON bodies and non-simple methods need a CORS preflight that the editor never grants.
3. **Anonymous paths.** `GET /api/health`, `GET` and `POST /api/session` continue without a user.
4. **Unauthenticated.** Other `/api/*` → 401 `unauthenticated` problem. Any other `GET`/`HEAD` → 200 with the inline sign-in page (self-contained HTML, `Content-Security-Policy: default-src 'none'; style-src 'unsafe-inline'; form-action 'self'`, one token field posting to `/api/session`). Anything else → 401.
5. **Admit.** `context.User = new ClaimsPrincipal(new ClaimsIdentity([name, role, "mq:via"], "maquettiste"))`, then `await next()`. Handlers call `Api.Require(context, "editor")`-style checks against the role order viewer < editor < maintainer < admin (phase 2 users are all admin, so the checks exist but never refuse).

`EditorAuth` keeps a per-address sliding window of failed sign-ins (5 per minute → 429 `too-many-attempts`); it reads variables at call time, so rotating the token takes effect at once and ends every cookie session. If the functions fail to load, the host answers 503 for the whole site because it has middleware (fail closed, S19).

### 3.4 Realtime and AI hooks

Hooks run without middleware and read the request themselves through the same `EditorAuth.Identify`. `Connect(HttpContext context, EditorAuth auth)` returns the user name or `null` (refused). `Join(HttpContext context, EditorAuth auth, string group)` admits an identified caller to `editors` and to `job:{id}` when `id` is a ULID (`^job:[0-7][0-9A-HJKMNP-TV-Z]{25}$`); anything else is refused. `Ai(HttpContext context, EditorAuth auth)` admits identified callers (the phase 2 SPA does not chat; the hook keeps `/_host/ai/chat` closed to others). Hooks register nothing on the `HttpContext`.

### 3.5 Services

`EditorSetup.Configure(IServiceCollection services, ISiteVariables variables, DirectoryInfo data)` registers singletons only: `EditorSettings`, its `EngineOptions`, `ModelStore` (`new ModelStore(options)`: no I/O, never throws for model content, host-contracts 6), `GenerationService(store, options)`, `JobQueue(generation, options, capacity: 16)`, `EditorAuth`, `EditorEvents`, `PresenceRegistry`, `GitStatusReader`. The container disposes `ModelStore` and `JobQueue` (`IAsyncDisposable`) during the host's drain. The editor test panel runs `Configure` per test, so singletons start over there; background services never start in tests.

### 3.6 Background services and publishing

**`ModelWatcher.Run(CancellationToken stoppingToken, ModelStore store, EditorEvents events, EditorSettings settings)`:** `await store.LoadAsync`; subscribe `store.OnChanged(events.OnModelChangedAsync)` (disposed in `finally`); start a `FileSystemWatcher` on the model root (subdirectories, all files, 64 KB buffer); collect paths, ignoring engine-owned ones (`.cache/`, `manifest/`, `snapshots/`, `.schema/`, dot-named staged files `.*.mq-*.tmp` and `.bak`), and after 250 ms without events call `store.RefreshAsync(paths)`. A watcher `Error` (buffer overflow, lost mount) calls `store.RescanAsync(false)` and marks health `polling` until a watcher restarts. Paths under `maquettiste.json`, `templates/` or `extensions/` also call `events.OnSettingsChangedAsync`: a settings, pack or extension change yields no element `ChangeSet` (`OnChanged` fires only for non-empty sets), yet it changes validation results, resolved tables and previews. The same method runs `events.RunValidationLoopAsync(stoppingToken)` beside the watcher loop. `ModelWatcher.Rescan(ModelStore store, PresenceRegistry presence)` (`[Every("30s")]`) calls `store.RescanAsync(false)` (stat-based, cheap when nothing changed; covers mounts that drop events) and prunes presence.

**`EditorEvents`:**
- `OnModelChangedAsync(ChangeSet set, CancellationToken ct)`: `realtime.PublishAsync("model.changed", set.TruncateTo(200 * 1024), Api.JsonOptions)`, then signals the validation loop. The engine's own writes are recognised by hash when the watcher reports them, so one save yields one event (host-contracts 19).
- `RunValidationLoopAsync`: waits for a signal, then 750 ms of quiet, runs `store.ValidateAsync(ValidationScope.All)` and publishes `validation.completed` with `report.TruncateTo(200 * 1024)` (counts stay whole; the SPA refetches when `truncated`). Whole-model validation keeps the problems list exact without tracking which referrers a scoped run covered (PD13).
- `OnSettingsChangedAsync(string settingsHash, CancellationToken ct)`: publishes `project.changed` (skipped when `settingsHash` equals the last one published, so a settings save and the watcher's report of the same write yield one event), then signals the validation loop. Called by the watcher's settings branch and by `ProjectEndpoints.SaveSettings` after a `saved` outcome.
- `PublishJobProgressAsync(JobInfo)` to `job:{id}`; `PublishPresenceAsync()` to `editors`.
- `PublishJobCompletedAsync(JobInfo)` to `job:{id}` publishes `JobCompletedEvent.From(job)`, a bounded summary, never the record: a finished `JobInfo` keeps `staleUnits`, `stalePaths`, `plan.request` and every diagnostic (the engine trims only `plan.units`, `plan.changes` and `result.changes`), so at S13 scale (100,000 files) a stale apply or a plan with per-file warnings runs to megabytes, and `IRealtime.PublishAsync` throws above 256 KB (`FakeRealtime` too). `public sealed record JobCompletedEvent(string Id, JobKind Kind, JobState State, RunOutcome? Outcome, string? PlanId, string? Error, JobCounts Counts, DateTimeOffset? FinishedUtc)` and `public sealed record JobCounts(int Changes, int StaleUnits, int StalePaths, int Errors, int Warnings)`: `Outcome` is `planResult.outcome` or `applyResult.outcome` (`null` when the job ended without a result), `PlanId` the plan a plan job produced (`null` for apply jobs), `Error` cut to 2,000 characters, counts taken from the full record the `OnCompleted` handler receives (`changes` of the plan or the run result; diagnostics of the plan or the run result by severity). The SPA reads `GET /api/jobs/{id}` and the plan for the rest (§4.3).

**`JobWorker.Run(CancellationToken stoppingToken, JobQueue queue, EditorEvents events)`:** subscribes `queue.OnProgress` (per job, publish when 250 ms passed since the last, the stage changed, or `done == total`) and `queue.OnCompleted` (`events.PublishJobCompletedAsync` with the full record the handler receives, which the summary's counts need; nothing large is published), then `await queue.RunAsync(stoppingToken)`. On a redeploy the host cancels it; a running job stays `running` and the next build resumes it through the run journal (engine Jobs README).

### 3.7 Handlers

One handler maps to one engine call (two where noted). Every body that mirrors an engine record is `Api.Json(record, status)` with the static Web options. Responses with a hash carry `ETag: "<hash>"`. Unknown ids are 404 problems except where the engine's own record answers (`SaveResult` with `not-found`).

| operationId | Route | Handler | Engine call | Statuses |
| --- | --- | --- | --- | --- |
| getEditorHealth | `GET /api/health` | `HealthEndpoints.Get` | `ModelStore.Current` + worker and watcher state | 200 |
| getSession, signIn, signOut | `GET`, `POST`, `DELETE /api/session` | `SessionEndpoints.Get`, `SignIn`, `SignOut` | none | 200/401; 200/303/401/429; 204 |
| getProject | `GET /api/project` | `ProjectEndpoints.Get` | `GetSnapshotAsync` (settings, `SettingsHash`, extensions, databases) + `GetPacksAsync` (E2) + `GitStatusReader` | 200/503 |
| getSettings, saveSettings | `GET`, `PUT /api/project/settings` | `ProjectEndpoints.GetSettings`, `SaveSettings` | `GetSettingsAsync`, `SaveSettingsAsync` (E3); on `saved`, `events.OnSettingsChangedAsync` | 200; 200/409/422/428 |
| getModelIndex | `GET /api/model/index` | `ModelEndpoints.Index` | `GetIndexAsync` | 200/503 |
| createElement | `POST /api/model/elements` | `ModelEndpoints.Create` | `CreateAsync(body, Editor)` | 201/422/400 |
| getElement | `GET /api/model/elements/{id}` | `ModelEndpoints.Get` | `GetElementAsync` (a sub-element id returns its owner) | 200/304/404 |
| saveElement | `PUT /api/model/elements/{id}` | `ModelEndpoints.Save` | `SaveAsync(id, body, If-Match, Editor)` | 200/409/422/404/428 |
| deleteElement | `DELETE /api/model/elements/{id}?resolution=` | `ModelEndpoints.Delete` (takes `string? resolution`, below) | `DeleteAsync(id, If-Match, resolution, Editor)` | 200/409/422/404/428/400 |
| applyBatch | `POST /api/model/batch` | `ModelEndpoints.Batch` | `ParseBatch`, then `ApplyBatchAsync(batch, Editor)` | 200/409/404/422 |
| getReferences | `GET /api/model/references/{id}` | `ModelEndpoints.References` | `GetElementAsync` (404) + `GetReferencesAsync` | 200/404 |
| validate | `POST /api/validate` | `ValidationEndpoints.Validate` | `ValidateAsync(scope ?? All)` | 200/400 |
| getDiagram, saveDiagram | `GET`, `PUT /api/diagrams/{id}` | `DiagramEndpoints.Get`, `Save` | `GetElementAsync` (404 `not-a-diagram` unless kind is diagram), `SaveAsync` | as elements |
| getDatabaseView | `GET /api/databases/{id}/view` | `DatabaseEndpoints.View` | `GetDatabaseViewAsync` (E1) | 200/404 |
| startPlan | `POST /api/generate/plan` | `GenerateEndpoints.Plan` | `TryEnqueue(new JobRequest(Plan, request with { IncludeDiffs = false, StageBarriers = false, Lock = Wait }, null))` | 202/503 |
| getPlan | `GET /api/generate/plan/{id}?units=` | `GenerateEndpoints.GetPlan` | `GetPlanAsync`; `units` emptied unless `units=true` | 200/404 |
| getPlanDiff | `GET /api/generate/plan/{id}/diff?path=` | `GenerateEndpoints.Diff` | `GetPlanDiffAsync` → `text/x-diff` | 200/404 |
| startApply | `POST /api/generate/apply` | `GenerateEndpoints.Apply` | `TryEnqueue(new JobRequest(Apply, null, planId))` | 202/503 |
| listJobs, getJob, cancelJob | `GET /api/jobs`, `GET`, `DELETE /api/jobs/{id}` | `JobEndpoints.List`, `Get`, `Cancel` | `ListAsync`, `GetAsync`, `Cancel` (+ `GetAsync` for the body: 404 unknown, 409 `job-finished`) | 200; 200/404; 202/404/409 |
| previewTemplate | `POST /api/templates/preview` | `TemplateEndpoints.Preview` | `PreviewAsync(pack, unit, elementId)` | 200/400 |
| reportPresence | `PUT /api/presence` | `PresenceEndpoints.Report` | none: checks the connection belongs to the caller (`IRealtime.Connections`), stores, publishes | 204/404 |

`Api.StatusOf`: saved 200 (201 on create), conflict 409, invalid 422, not-found 404, referenced 409. A missing `If-Match` is 428; `If-Match` accepts `"<hash>"`, `W/"<hash>"` or the bare hash. `ReadBodyAsync` refuses bodies over 4 MB (400 `too-large`). Any exception becomes 500 `internal` with a trace id; the host logs it. `GET /api/model/index` sends `Cache-Control: no-cache` with an `ETag` (a hash of the index's rows, so it survives a restart) and answers 304 to a matching `If-None-Match` (E5e, `explorer-redesign.md` §4.1); element reads send `Cache-Control: no-store`. An `/api/*` path no handler matches falls through to the host's SPA fallback (`index.html`); the SPA's client treats an HTML answer from `/api/` as an error (`getPlanDiff`'s `text/x-diff` is the one non-JSON success, §4.3). `ModelEndpoints.Delete` binds `resolution` as `string?` and maps it itself: absent or `refuse` → `DeleteResolution.Refuse`, `remove-references` → `RemoveReferences`, anything else → 400 `bad-request`. The host binds enum parameters with `Enum.TryParse(ignoreCase: true)` (`FunctionRouter`), which knows member names (`RemoveReferences`), not the engine's kebab-case JSON names, so a `DeleteResolution` parameter would answer 400 to the documented value. `SaveResult.id` echoes the id the request named even when it is not a ULID (the engine sets it before the MQ1006 `invalid` check), so the contract types it as any string.

### 3.8 Engine additions (E1–E4)

Phase 1's public API lacks three calls and two index fields the phase 2 workspaces need. P2-F adds them first (E1–E4), in one engine commit that also updates `engine-design.md` §15, with tests in `tests/Maquettiste.Engine.Tests/Editor/`. The records are public, serialize with Web defaults without converters, and are mirrored in the OpenAPI file (`x-engine-addition`).

```csharp
namespace Maquettiste.Engine;
// E1: GenerationService.GetDatabaseViewAsync — the resolved physical model of one database (Database and Mappings workspaces)
public Task<DatabaseViewResult> GetDatabaseViewAsync(string databaseId, CancellationToken ct);   // load, validate, resolve; no lock, no writes
public sealed record DatabaseViewResult(DatabaseView? View, IReadOnlyList<Diagnostic> Diagnostics);   // View null when the model has errors
public sealed record DatabaseView(string Id, string Name, string Dialect, string? Version, string? DefaultSchema, IReadOnlyList<TableView> Tables);
public sealed record TableView(string Key, string Name, string? Schema, string Origin, string? EntityId, string? RelationId, bool IsJunction,
    bool IsLookup, string? Comment, IReadOnlyList<ColumnView> Columns, KeyView? PrimaryKey, IReadOnlyList<KeyView> Uniques,
    IReadOnlyList<ForeignKeyView> ForeignKeys, IReadOnlyList<IndexView> Indexes);          // from RTable; Origin synthesized|designed|imported
public sealed record ColumnView(string Key, string Name, string Type, string NativeType, int? Length, int? Precision, int? Scale, bool Nullable,
    string? DefaultSql, bool Identity, string? Computed, string? AttributeId, string? AttributePath, bool IsPrimaryKey, bool IsForeignKey,
    bool IsDiscriminator, int Position);                                                   // from RColumn
public sealed record KeyView(string Name, IReadOnlyList<string> Columns);                  // column keys
public sealed record ForeignKeyView(string Name, IReadOnlyList<string> Columns, string ReferencedTable, IReadOnlyList<string> ReferencedColumns,
    string OnDelete, string OnUpdate, string? RelationId, string? EndId);
public sealed record IndexView(string Name, IReadOnlyList<IndexColumnView> Columns, bool Unique, string? Where);
public sealed record IndexColumnView(string Column, bool Descending);
// E2: GenerationService.GetPacksAsync — every pack under templates/, enabled or not, with load diagnostics
public Task<PackListResult> GetPacksAsync(CancellationToken ct);
public sealed record PackListResult(IReadOnlyList<PackManifest> Packs, IReadOnlyList<Diagnostic> Diagnostics);
// E3: ModelStore settings read and write (maquettiste.json: conventions, type maps, outputs, packs)
public Task<SettingsDocument> GetSettingsAsync(CancellationToken ct);
public Task<SettingsSaveResult> SaveSettingsAsync(ReadOnlyMemory<byte> json, string expectedHash, ChangeSource source, CancellationToken ct);
public sealed record SettingsDocument(ProjectSettings Settings, string Path, string Hash, JsonElement Json);
public sealed record SettingsSaveResult(SaveOutcome Outcome, string? Hash, SettingsDocument? Current, IReadOnlyList<Diagnostic> Diagnostics);
// E4: ElementSummary gains the element's category and stereotypes (explorer chips, §4.8); ModelIndexer fills them from ElementBase
public sealed record ElementSummary(string Id, string Kind, string Name, string? Package, IReadOnlyList<string> Tags, string Hash, string Path,
    string? Category, IReadOnlyList<string> Stereotypes);   // Model/Documents.cs; Category is the category-tree node id
```

E3 follows the element save rules: schema (`maquettiste.json`), canonical bytes, expected hash against disk, atomic write under the store's write gate, reload, and `Invalid` when the file fails its schema or the reloaded model reports errors the change introduced (the element-save rule). E4 appends two parameters to an existing record: `ModelIndexer` builds every summary from the in-memory element (both call sites), nothing persists summaries, and only engine tests construct them. Without E4 the explorer's category and stereotype filters would need one element read per entity, which S13 rules out (summaries up front, element files on demand). Resolved objects reference each other, so E1 projects them into flat records rather than serializing R-types (the same cycle W8 reported for `SchemaDiffResult`).

### 3.9 Testing the functions

`tests/Maquettiste.Functions.Tests` (xunit v3) references `Maquettiste.Functions.csproj`, calls handlers as static methods with `DefaultHttpContext` requests, and uses `StaticSiteHost.Functions.Testing` (in the `StaticSiteHost.Abstractions` package): `FakeSite` with `Data` set to a temp copy of `tests/fixtures/models/billing/.maquettiste` (plus `packs/sql-ddl` and `packs/csharp-dapper` under `templates/`), `FakeSiteVariables`, and `FakeRealtime` (assert `Published` events and connect fake connections for presence and join). Required tests:

- **Gate:** local peer, bearer, cookie, wrong token, expired cookie, rotated token, foreign `Origin`, form post, text/plain POST, the sign-in page for `GET /`, anonymous paths; local trust with the IPv4-mapped addresses Kestrel reports (`::ffff:127.0.0.1`, and `::ffff:172.18.0.1` with `MAQUETTISTE_LOCAL_PEERS=172.18.0.1`), refused for a local peer whose `Host` is not local or that sends `X-Forwarded-For` or `Forwarded`, and refused under `MAQUETTISTE_LOCAL_TRUST=off`; an unparsable peer entry; the three hooks with the same cases.
- **Handlers:** every row of §3.7, each status it lists, ETag and `If-Match` forms, 4 MB cap, `units=true`, `resolution=refuse`, `remove-references` and an unknown value (400), a create whose body id is not a ULID (422, `id` echoed), cancel of queued and finished jobs.
- **Events:** a save publishes one `model.changed` and one `validation.completed`; a disk edit through `RefreshAsync` publishes `source: disk`; truncation above 200 KB; a settings save, and a disk edit of `maquettiste.json` reported by the watcher, each publish one `project.changed` and then a `validation.completed`; job progress throttling; `job.completed` is the `JobCompletedEvent` summary, and for an apply job whose result holds 100,000 stale paths (and a plan with 100,000 warnings) it stays under 256 KB with the counts exact.
- **Contract:** every `[Http*]` route and method in `_functions/` equals an operation of `docs/api/openapi.yaml` and vice versa (YamlDotNet), leaving out operations marked `x-maquettiste-handler: host` (`/healthz`, answered by the host), and each handler's JSON body in these tests validates against the operation's response schema (JsonSchema.Net, with `schemas/v1/*.json` registered for the external refs); `getPlanDiff` is checked for `Content-Type: text/x-diff` instead. Published realtime payloads validate against the `Realtime*` schemas the same way.
- **Bundle:** `_functions/` has at most 50 files, none over 1 MB, 4 MB in all (and the 24-file, 400 KB budget); only `Directives.cs` has `#:` lines and it pins the version in `Directory.Build.props` (`pack-site.mjs --engine-version` rewrites only that line, which a test of the script checks); no `#:project`; class names are unique.
- **Recording:** with `MAQUETTISTE_RECORD=1`, the handler tests write their real responses (project, index, elements, plan, job, diff, preview, database view) to `src/editor/src/mocks/recorded/` for the SPA's mocks (§4.4).

Unit tests stay Docker-free. `image.yml` adds a smoke job: build the image, start it with `--network none` over a copy of the billing fixture, wait for `/api/health` `ok` (proves the offline first build), and run `curl` checks of `/api/project`, a save, a plan and an apply. On that fresh volume `/data/sites` and `/data/sites/maquettiste.localhost` must be owned by `app` (§6.1). A second job checks upgrades: start the image on a named volume, read `/api/health` `engineBuild`, append a comment line to an engine source file, rebuild, start the new image on the same volume, and assert that `engineBuild` changed and equals the new image's `/opt/maquettiste/engine.version` (the persistent NuGet cache must not keep the old engine, PD24).

## 4. Editor SPA (P2-E)

### 4.1 Stack

Exact versions are pinned in `src/editor/package.json` and `package-lock.json`; the table names the line.

| Area | Choice |
| --- | --- |
| Build and language | Node 22 LTS, Vite 6, React 19, TypeScript 5 (`strict`), ESLint 9, Prettier |
| Styling and primitives | Tailwind CSS 4 (CSS-first `@theme`), shadcn/ui components on Radix copied into `src/components/ui`, Lucide icons, `react-resizable-panels`, `cmdk` (command palette) |
| Canvas and layout | `@xyflow/react` 12 (React Flow); `elkjs` in a web worker; `@dagrejs/dagre` fallback only (§4.9); `html-to-image` for SVG and PNG export |
| Code | `monaco-editor` bundled locally with `@monaco-editor/react` (`loader.config({ monaco })`, no CDN: local mode may be offline) |
| Data | TanStack Query 5, TanStack Table 8, TanStack Virtual 3; `openapi-fetch` over types from `openapi-typescript` 7 |
| State | Zustand 5 for editor state (§4.6) |
| Routing | React Router 7 (library mode) |
| Ids | `ulid` (uppercase Crockford) for new elements and sub-elements |
| Mocks | MSW 2, `@mswjs/source` (OpenAPI baseline), `openapi-msw` (typed stateful handlers) |
| Tests | Vitest 3 (jsdom), Testing Library, `ajv` 8 (2020-12) for contract tests, Playwright with `@axe-core/playwright` |
| Fonts | `@fontsource-variable/inter`, `@fontsource-variable/jetbrains-mono` (OFL, self-hosted) |

### 4.2 Source layout

```
src/editor/
├── index.html  vite.config.ts  tsconfig.json  playwright.config.ts  package.json
│                                       # vite.config.ts: server.fs.allow (§4.4) and the dev:live proxy (§5)
├── public/_headers                     # /assets/** immutable; /index.html no-cache
├── scripts/
│   ├── gen-api.mjs                     # openapi.yaml → src/api/schema.d.ts (committed; CI fails on a diff)
│   ├── gen-mocks.mjs                   # redocly bundle → src/mocks/openapi.json for @mswjs/source and ajv
│   ├── pack-site.mjs                   # dist + _functions + _variables.json + _headers → site.zip (stamps Directives.cs)
│   └── deploy.mjs                      # POST site.zip to the host; --watch for the development loop (§5)
├── src/
│   ├── main.tsx  app/                  # providers, router, shell (top bar, rail, explorer, inspector, bottom panel)
│   ├── api/                            # schema.d.ts (generated), client.ts, queries.ts (query keys, hooks, cache patching)
│   ├── realtime/                       # RealtimeClient interface, host.ts (site.realtime), mock.ts, events.ts (typed from webhooks)
│   ├── state/                          # editor store (Zustand), drafts, undo
│   ├── design/                         # tokens.css, theme.ts, density, categorical palette
│   ├── components/ui/                  # shadcn/ui, restyled through tokens only
│   ├── canvas/                         # EntityNode, RelationEdge, TableNode, ForeignKeyEdge, layout worker client
│   ├── inspector/  explorer/  problems/  diff/  palette/
│   ├── workspaces/entities|database|mappings|generate|settings/
│   └── mocks/                          # browser.ts, node.ts, openapi.json, handlers/, model/ (MockModel), recorded/
└── tests/e2e/                          # Playwright specs; projects: mock (default) and live
```

### 4.3 API client and cache

*Status (2026-09-29): built as described, with the explorer's additions. The index is sent `Cache-Control: no-cache` with an ETag (not `no-store`), so the refetch's `If-None-Match` gets a 304; with a content locale the SPA sends `?locale=` and the ETag varies with it. Added keys: `["tables", dbId, "detail", key]` (E5f table detail, refetched by the debounced table invalidation), `["localization"]` and `["translations", locale, "owner" | "shard", …]` (reference-types-seeds-localization.md §3.9), patched from the `translations` of `model.changed`. Index patches from `ElementChange.summary` (E5d) are built (explorer-redesign.md step 12).*

`client.ts` creates `createClient<paths>({ baseUrl: "" })` with middleware that sends `Content-Type: application/json`, turns `application/problem+json` into a typed `ApiProblem` (by `code`), rejects any other non-JSON answer from `/api/` (the SPA fallback's HTML for an unknown path) except `text/x-diff` from `getPlanDiff`, which is read with `parseAs: "text"`, and exposes `ETag`. Query keys: `["project"]`, `["settings"]`, `["index"]` (sent with `If-None-Match`, 304 keeps the rows it has; no structural sharing), `["element", id]` (read in batches of 200 with `POST /api/model/elements/read`, or one GET each against a server without it), `["tables", dbId]` (E5c), `["references", id]`, `["validation"]`, `["databaseView", id]`, `["preview", pack, unit, elementId]`, `["plan", id]`, `["job", id]`, `["jobs"]`. The SPA edits `ElementDocument.json` and sends it back; `element` (the typed record, defaults filled in) only feeds read-only displays of effective values. New attributes, relation ends, keys and columns get ULIDs in the SPA (the engine assigns only a missing top-level id).

Realtime patches the cache. A successful save or batch patches its index rows at once from `SaveResult.current` (kind, name, package, tags, category, stereotypes from `json`; hash and path). `model.changed` then compares each `ElementChange` with the index: a row with the same hash is the echo of the SPA's own write and costs nothing (an event for an id with a save in flight is compared after that save answers). When every `ElementChange` carries its `summary` (E5d), the index is patched in place with no request, and a cached `["element", id]` older than the change is invalidated. Without summaries (an older server), a change from anywhere else (another window, a disk edit, the CLI), which may be a rename, a retag or a new element, cannot be patched in: an unknown id or a changed hash invalidates `["index"]` (debounced 250 ms; the refetch sends `If-None-Match`) and `["element", id]`. A change to a kind that shapes tables invalidates `["tables"]` on its own 1.5 s debounce, and the tree keeps the last complete tables while an answer is partial. Deleted ids are dropped from the index and their element queries removed; `truncated` invalidates `["index"]`. Every `model.changed` also invalidates `["databaseView"]` (all databases, by prefix) and `["preview"]`, because any element can change the resolved tables. `validation.completed` replaces `["validation"]` (refetch with `POST /api/validate` when `truncated`). `project.changed` invalidates `["project"]`, `["settings"]`, `["databaseView"]` and `["preview"]`, because conventions and type maps change every resolved table; the re-validation it triggers arrives as `validation.completed` (§3.6). `job.progress` writes `["job", id]`. `job.completed` carries a bounded summary (§3.6), not the record: it invalidates `["job", id]` and `["jobs"]`, and the SPA reads `GET /api/jobs/{id}` and, for a plan job, the plan. A finished job is reported by its run outcome (`outcome` in the event, `planResult.outcome` or `applyResult.outcome` in the record), never by `state`: `state` is `succeeded` whenever the run completed, so a `stale`, `invalid`, `conflicts`, `drift` or `busy` run is `succeeded` too (`JobQueue` maps only `cancelled` and `failed` to other states).

### 4.4 Mocks

The SPA runs and is tested with no backend. Mock mode (`npm run dev`, Vitest, Playwright project `mock`) starts MSW with two layers:

1. **Baseline, generated.** `gen-mocks.mjs` bundles `docs/api/openapi.yaml` (Redocly) into `src/mocks/openapi.json`; `fromOpenApi(openapi.json)` from `@mswjs/source` answers every operation with its first example, so each endpoint answers from the day it is added to the contract.
2. **Stateful, typed.** `openapi-msw` handlers typed by `paths` override the baseline for the model, diagrams, validation, generation and jobs. They run on `MockModel`, an in-memory model seeded from `tests/fixtures/models/billing/.maquettiste/**/*.json` (`import.meta.glob` with `?raw`; the fixture lies outside `src/editor`, see below): index, get, create, save with `If-Match` and 409, delete with reference checks, batch, references (by scanning ids), diagrams. Hashes are SHA-256 of the mock's own JSON text (not the engine's canonical bytes; nothing may compare them with real hashes). Plans, diffs, previews and database views replay the engine responses recorded by the functions tests (`src/mocks/recorded/`, §3.9); jobs advance on timers and publish through `MockRealtime`.

**Serving the fixture in dev.** Vite's dev server refuses `/@fs/` and `?raw` requests for files outside `server.fs.allow` (403), and the default allow list is the workspace root, which here is `src/editor` itself: the repo has no workspace marker (`pnpm-workspace.yaml`, `lerna.json`, a root `package.json` with `workspaces`), and `.git` does not count. `vite build` and `vite preview` do not check it, so a preview-only test would pass while `npm run dev` had no model. `vite.config.ts` therefore sets `server: { fs: { allow: [searchForWorkspaceRoot(process.cwd()), path.resolve(__dirname, "../../tests/fixtures")] } }`, and a dev-server smoke test guards it (§4.10).

A Vitest contract suite sends every stateful handler through its operations and validates each response with ajv against `openapi.json`, so the mocks cannot drift from the contract. `?mock=conflict`, `?mock=slow` and `?mock=empty` query flags select scenarios for tests and demos.

### 4.5 Realtime client

`RealtimeClient { on, off, join, leave, state, connectionId, onStateChange }` with two implementations. `HostRealtime` injects `<script src="/_host/site.js">` at startup (not in `index.html`, so mock mode never requests it) and wraps `window.site.realtime`; `MockRealtime` is an in-memory bus the mock handlers publish to. At startup the SPA joins `editors`; after `POST /api/generate/plan` or `/apply` it joins `job:{id}` and then reads `GET /api/jobs/{id}` once, closing the race with events sent before the join. It leaves `job:{id}` on `job.completed`, when that read already shows the job finished, and when the view that started the job unmounts: the host allows 100 groups per connection (`RealtimeMaxGroupsPerConnection`), and a long session runs more jobs than that. On every transition to `connected` it re-sends `PUT /api/presence` and refetches `["index"]` and `["validation"]` (events may have been missed while reconnecting). On `site.deployed` it reloads at once when no draft is unsaved; otherwise it shows a "new version" banner with a reload button. Event payload types come from the OpenAPI `webhooks` section.

### 4.6 Editor state, drafts and undo

Server state lives only in TanStack Query. `useEditorStore` (Zustand, sliced) holds: workspace, selection (ids), active diagram, panel sizes and collapsed state, bottom-panel tab, theme and density (both mirrored to `localStorage` keys `mq.theme`, `mq.density`), command palette state, drafts and the undo stacks. The URL carries workspace, diagram or database and `?sel=<id>` so views are linkable.

- **Drafts.** An inspector or grid edit updates `drafts[id] = { baseHash, json }` at once and the canvas renders the draft; after 600 ms idle (or on blur, Enter, or leaving the element) the draft is saved with `If-Match: baseHash`. One save per element is in flight at a time; edits made meanwhile stay in the draft. On `saved`, `["element", id]` is seeded from `current` and the draft's `baseHash` becomes `SaveResult.hash`; the draft is cleared when it still equals what was sent, and otherwise saved next with that hash, so the editor's own write never comes back as a conflict. `invalid` keeps it and shows the diagnostics inline. `conflict` opens the conflict dialog: a Monaco diff of the draft against `current.json` (the disk version), with "Keep mine" (retry with the new hash), "Take theirs" and manual merge.
- **Undo and redo.** Each successful save pushes `{ ids, before[], after[], afterHashes[] }`. Undo sends the `before` documents as one `POST /api/model/batch` with `expectedHash` = the `after` hashes (creates become deletes and deletes become creates); a conflict leaves the stacks unchanged and says which element changed since. Bulk edits (multi-select, apply a stereotype to 50 entities) and canvas creates that also add a diagram member are one batch.

### 4.7 Design system

`src/design/tokens.css` defines the S15 tokens as CSS variables on `:root` (light) and `[data-theme="dark"]`, named `--mq-bg-app`, `--mq-bg-surface`, `--mq-bg-raised`, `--mq-bg-canvas`, `--mq-border-default`, `--mq-border-strong`, `--mq-text-primary`, `--mq-text-secondary`, `--mq-accent`, `--mq-accent-subtle`, `--mq-status-success|warning|danger`, plus two tokens S15 lacks, `--mq-accent-foreground` (text and icons on an accent fill) and `--mq-border-input` (the outline that identifies an input, select, checkbox or other control), with the S15 values except where they fail the contrast test below. S15 allows that: its table is "proposed", and it requires WCAG 2.2 AA for every text and status pairing. Measured against S15: light `status.warning` #B25E00 is 4.28:1 on `bg.app`, 4.44 on `bg.canvas` and 4.08 on `accent.subtle`; light `status.success` #1E7F4F is 4.36 on `accent.subtle`; white on the dark `accent` #4C8DF6 (a primary button label) is 3.26; `border.default` and `border.strong` are 1.36 and 1.87 on light `bg.surface` (1.31 and 1.71 dark), below the 3:1 WCAG 1.4.11 needs for a control's identifying outline. Phase 2 values: light `status.warning` #9E5400 (4.92 to 5.64 on every light background), light `status.success` #1B7348 (5.10 to 5.85), `accent-foreground` light #FFFFFF (5.99) and dark #0F1115 (5.80), `border-input` light #7D8694 (3.21 to 3.68) and dark #6B7482 (3.21 to 4.00). `border.default` and `border.strong` stay for dividers and panel edges, which 1.4.11 does not cover; every other S15 value passes unchanged (§11, open point 12); `@theme inline` exposes them as Tailwind utilities (`bg-surface`, `border-default`, `text-secondary`, `ring-accent`). Also: `--mq-cat-1` … `--mq-cat-8` (categorical, proposed light `#0F7B7B #6E56CF #C2570C #B0368C #3A7D1E #946800 #0A72A0 #A34D3C`, dark `#3CC4C4 #A08CF2 #F08C4A #E26BBE #74C257 #D9A93A #4CB8DD #E08A77`), radii 6 px (controls) and 8 px (panels, cards), spacing on a 4 px grid, type scale 11/12/13/14/16/20/24 with 13 as base, weights 400/500/600, motion 120 to 180 ms ease-out (zero under `prefers-reduced-motion`), row height `--mq-row-h` 32 px (`data-density="compact"`, default) or 40 px (`comfortable`). Theme follows `prefers-color-scheme` unless overridden. Rules: no hex value outside `tokens.css` (lint rule), borders rather than shadows except floating layers, one accent for action and selection, status colors only for status, no gradients. A Vitest test computes WCAG 2.2 contrast in both themes: every text and status color against every background it can sit on (`bg.app`, `bg.surface`, `bg.raised`, `bg.canvas`, `accent.subtle`) and `accent-foreground` against `accent`, at 4.5:1; `border-input` and `accent` (also the focus ring) against the same backgrounds, at 3:1; every categorical color against `bg.surface` and `bg.canvas`, at 3:1. The proposed categorical values are adjusted until it passes.

### 4.8 Shell and workspaces

*Status (2026-09-29): the shell below is the first phase 2 build and is superseded by explorer-redesign.md §1.0 to §3.6, which is built: the rail lists the explorers Domain model, Reference data, Databases, Diagrams and Generate, with Settings and the account menu at its foot, and shows one explorer at a time (a second can be pinned beside it); the explorer is `explorer/tree.ts` with search operators and qualifiers, filter chips, scopes, favorites and recent; "workspaces" are called screens in the UI, and every word comes from `model/labels.ts`. The diagram picker's package view is "All of <domain>" (at most 300 entities). New since: the Reference data screen, the element editors with preview tabs and General mode, the domain editor with domain-scoped Tags and Categories, creation menus and a first-run panel, and Settings › Locales with the Content switcher when two or more locales are declared. What remains is listed in explorer-redesign.md §6 and reference-types-seeds-localization.md §5.*

**Shell (S14).** A 44 px top bar: project name, git branch and changed model files from `project.git` (read-only), the command palette (Ctrl/Cmd+K: go to element across the index, run commands such as Plan, Apply, New entity, Toggle theme), theme switch, the signed-in user and connection state. Left: a 48 px workspace rail (Entities, Database, Mappings, Generate, Settings; Processes, Templates, Source control and Team disabled) and a resizable explorer (240 to 480 px): the index grouped by package, virtualized (TanStack Virtual), filterable by tag, category and stereotype chips (from the summary's `tags`, `category` and `stereotypes`, E4) and by text, with presence dots from `presence.changed`. Right: the resizable inspector (320 to 560 px) for the selection, with custom properties rendered from the extension schemas that apply (kinds and stereotypes) by a small in-house JSON-Schema form (string, number, integer, boolean, enum, arrays of those). Bottom: Problems (from `["validation"]`, grouped by element, click to select and focus the JSON pointer), Output (job progress and results, each finished job labelled by its run outcome, not its `state`, §4.3) and Diff. Everything is reachable by keyboard; focus order is rail, explorer, center, inspector, bottom; `F6` cycles regions.

**Entities.** A diagram picker (the model's diagrams plus a virtual "Package: <name>" view of all entities in a package). React Flow canvas: `EntityNode` cards (4 px left edge in the category color, name at weight 600, attribute rows with the type in monospace right-aligned, key and required icons, stereotype badges, tag chips, a danger badge with the error count, collapsed state from the member), `RelationEdge` (label on a pill, multiplicities in UML notation or crow's feet, composition and aggregation diamonds, relation-attribute badge). Per-diagram display options (all attributes, keys only, names only; notation) are kept per browser in phase 2 (PD18). Interactions: drag to move (diagram `PUT`, 500 ms debounce; on 409 re-apply positions onto the disk version and retry once); New entity (batch: create entity + add member); connect two cards to create a relation (dialog: name, kind, roles, multiplicities, on delete); Add related to depth N; Auto-layout; minimap; export SVG and PNG; `onlyRenderVisibleElements` for large diagrams. The attribute grid (TanStack Table, spreadsheet keys: Enter edits, Tab moves, Ctrl+Enter adds a row) edits the selected entity and stays in sync with its card through the draft.

**Database.** A database picker; `GET /api/databases/{id}/view` drawn as `TableNode`s (columns with PK and FK icons, `nativeType` in monospace, synthesized tables in muted text) and `ForeignKeyEdge`s, auto-laid out, positions per browser. The dialect selector edits the database element. The DDL preview (Monaco, read-only, SQL) calls `POST /api/templates/preview` with `sql-ddl`/`schema` and the database id, or `sql-ddl`/`table` and the selected table's key, again 400 ms after any `model.changed`; without the `sql-ddl` pack it says which pack to install.

**Mappings.** For the selected entity and database: the entity's attributes on the left and its table's columns (from the database view) on the right, joined by `attributeId`/`attributePath`; convention-derived columns muted, columns overridden by the entity's mapping element highlighted with the accent. Editing an override (column name, storage, ignore, prefix; table, inheritance and shape for the entity) creates or saves the `mapping` element for that entity and database.

**Generate.** Pack and root selection, then Plan: progress from `job.progress` (stage, done of total, current path) with Cancel. The plan view counts files by `FileChangeKind` and pack, lists `changes` in a virtualized table filtered by kind and pack, and flags hand edits and conflicts. Selecting a file loads `GET …/diff?path=` into the Diff tab, rendered by the in-house unified-diff viewer (split or inline, virtualized, syntax colors by extension; PD16). Apply queues the plan by id; the result is the apply job's `applyResult.outcome` (read from `GET /api/jobs/{id}` after `job.completed`): `succeeded`, or `stale` (lists `staleUnits` and `stalePaths`, offers Re-plan), `invalid` (shows the diagnostics) or `conflicts`, each shown as a failure although the job's `state` is `succeeded`. Run history lists `GET /api/jobs`.

**Settings.** Tags (the tag vocabulary element), categories (tree editor over the category tree), stereotypes (list and form), and conventions (the `Conventions` members for the project and per database, with the inherited value as placeholder) saved through `PUT /api/project/settings`. Type maps, output allowlist and formatters are shown read-only in phase 2.

### 4.9 Layout

Auto-layout uses elkjs `layered` (direction right, orthogonal edge routing, node spacing 48, layer spacing 96) in a web worker: `new ELK({ workerFactory: () => new Worker(new URL("elkjs/lib/elk-worker.min.js", import.meta.url)) })`. Node sizes come from React Flow's measured dimensions; the result is applied, then saved as one diagram `PUT`. Only if creating the worker fails or ELK rejects, the same interface falls back to dagre on the main thread, logged once (PD2). The layout module takes a `LayoutEngine` interface so Vitest can test callers without workers; a Playwright test runs real ELK on the billing diagram and on a 200-entity diagram (under 2 s).

### 4.10 SPA tests

Vitest: store and draft logic, cache patching from each realtime event, API client error mapping, the design-token contrast test, component tests for the inspector, attribute grid, diff viewer and conflict dialog, and the mock contract suite. Playwright project `mock` (CI on every push, `vite preview` in mock mode): open the editor, select Invoice, rename an attribute in the grid and see the card change, create an entity and a relation on the canvas, auto-layout, resolve a conflict, plan and apply with job progress, open a diff, switch theme and density, keyboard-only navigation of the shell, and an axe scan of every workspace in both themes (no serious or critical violations). A `dev` smoke spec starts the Vite dev server itself (`vite`, not `vite preview`, because only dev enforces `server.fs.allow`), opens `/` and sees the Invoice card on "Billing overview". Project `live` runs the same specs against the image (§6) in `image.yml` and `gate2.yml`, plus one spec through `npm run dev:live` that saves an attribute (proves the §5 proxy passes the gate's origin check).

## 5. Development loop (S4)

| Command (in `src/editor`) | What runs | Backend |
| --- | --- | --- |
| `npm run dev` | Vite dev server on `http://localhost:5173` with HMR, MSW in the browser | none (mocks) |
| `npm run dev:live` | Vite dev server proxying `/api` and `/_host` (WebSockets included) to `http://127.0.0.1:8080` as `Host: maquettiste.localhost:8080`, with `Origin` rewritten (below) | the container |
| `npm run deploy:watch` | `vite build --watch --mode host` plus `deploy.mjs --watch` | the container, serving real releases |

**`dev:live` proxy.** Two things rule out a plain `target: "http://maquettiste.localhost:8080", changeOrigin: true`. Node's resolver does not special-case `*.localhost` as browsers and curl 7.85+ do (`dns.lookup("maquettiste.localhost")` is `ENOTFOUND` on a stock Linux). And `changeOrigin` rewrites only `Host`: the browser still sends `Origin: http://localhost:5173` on every `POST`, `PUT` and `DELETE`, which the gate refuses with 403 `forbidden-origin` (§3.3). So `vite.config.ts` proxies to `http://127.0.0.1:8080` with `headers: { host: "maquettiste.localhost:8080" }`, and in `configure(proxy)` sets `origin` to `http://maquettiste.localhost:8080` on `proxyReq` and `proxyReqWs` when the request has one. The gate is unchanged: the rewrite happens only in the developer's own dev server, whose requests reach the container from the gateway with a local `Host`, so local trust applies.

`deploy.mjs` watches `dist/` and `src/Maquettiste.Functions/_functions/`, waits 300 ms after the last change, runs `pack-site.mjs` and posts the zip to `${MAQUETTISTE_HOST_URL:-http://localhost:8080}/api/v1/sites/maquettiste.localhost/deploy` (`Content-Type: application/zip`, `X-Api-Key` from `MAQUETTISTE_DEPLOY_KEY`, else from `docker compose -f ../../docker/compose.yaml --project-directory "${MAQUETTISTE_PROJECT_DIR:-../../tmp/billing}" exec -T maquettiste cat /data/maquettiste/deploy.key`, paths relative to `src/editor`, where the script runs; `MAQUETTISTE_PROJECT_DIR` names the folder the editor was started on). `pack-site.mjs` is called with `--engine-version` set to the version the running image carries (`MAQUETTISTE_ENGINE_VERSION`, else the same `exec` reading `/opt/maquettiste/engine.version`), because the image's local feed holds only that version (PD24). It includes `_functions/` only when their content hash changed since its last deploy, because a zip without `_functions/` keeps the live functions and skips a rebuild. A deploy takes a second or two; the host sends `site.deployed` and the editor reloads (§4.5). The site zip holds `index.html`, `assets/`, `_headers`, `_variables.json` (§3.2) and `_functions/`. No `_redirects` is needed: the host falls back to `index.html` for unknown paths.

## 6. Docker image (P2-F)

### 6.1 Dockerfile

```dockerfile
FROM mcr.microsoft.com/dotnet/sdk:10.0.109 AS engine        # matches global.json (rollForward disable)
WORKDIR /src
COPY . .
# PD24: one package version per engine build, <Directory.Build.props version>.b<12 hex of the engine's inputs>
RUN base=$(dotnet msbuild src/Maquettiste.Engine -getProperty:PackageVersion) \
 && sum=$(find src/Maquettiste.Engine schemas/v1 Directory.Build.props Directory.Packages.props -type f \
          -not -path '*/bin/*' -not -path '*/obj/*' | LC_ALL=C sort | xargs sha256sum | sha256sum | cut -c1-12) \
 && echo "$base.b$sum" > /engine.version \
 && dotnet pack src/Maquettiste.Engine -c Release -o /feed -p:Version="$(cat /engine.version)" \
 && dotnet restore docker/closure/closure.csproj -p:EngineVersion="$(cat /engine.version)" \
      --packages /tmp/pkgs --source /feed --source https://api.nuget.org/v3/index.json \
 && find /tmp/pkgs -name '*.nupkg' -exec cp {} /feed/ \; \
 && sh docker/closure/nuget-config.sh /feed > /feed.NuGet.Config

FROM node:22-bookworm-slim AS editor
WORKDIR /src
COPY . .
COPY --from=engine /engine.version /engine.version
RUN cd src/editor && npm ci && npm run build \
 && node scripts/pack-site.mjs --out /out/site.zip --engine-version "$(cat /engine.version)"

FROM mattjcowan/static-site-hosting:0.2.0                   # the runtime-functions image (tags latest, 0.2.0, 0.2 carry the .NET SDK)
USER root
# git, curl and setpriv are already in the base image; the mkdir/chown makes the model mount's parents app-owned (below)
RUN apt-get update && apt-get install -y --no-install-recommends jq && rm -rf /var/lib/apt/lists/* \
 && git config --system --add safe.directory '*' \
 && mkdir -p /data/sites/maquettiste.localhost/data /data/maquettiste/cache && chown -R app:app /data
COPY --from=engine /feed /opt/maquettiste/nuget
COPY --from=engine /engine.version /opt/maquettiste/engine.version
COPY --from=engine --chown=app:app /feed.NuGet.Config /home/app/.nuget/NuGet/NuGet.Config
COPY --from=editor /out/site.zip /opt/maquettiste/site.zip
COPY docker/entrypoint.sh /opt/maquettiste/entrypoint.sh
RUN sha256sum /opt/maquettiste/site.zip | cut -c1-64 > /opt/maquettiste/site.sha256 && chmod 755 /opt/maquettiste/entrypoint.sh
ENV MAQUETTISTE_MODE=local MAQUETTISTE_REPO_ROOT=/repo MAQUETTISTE_CACHE_DIR=/data/maquettiste/cache \
    DOTNET_gcServer=1 DOTNET_GCDynamicAdaptationMode=0 DOTNET_TieredPGO=0
USER app
ENTRYPOINT ["/opt/maquettiste/entrypoint.sh"]
```

**Volume ownership.** The `mkdir`/`chown` line is required. The compose file mounts an empty named volume on `/data` and the model on `/data/sites/maquettiste.localhost/data`; Docker creates the missing parents of a mount point as root, and the base image creates only `/data` and `/data/nuget`. Without the line, `/data/sites` and `/data/sites/maquettiste.localhost` are `root 755` on a new volume, the host (UID 1654) cannot write `.staging/`, `releases/`, `site.json` or `functions/` in the site folder, and the first deploy fails. Reproduced with a local build of static-site-hosting 0.2.0 (`mkdir …/.staging`: Permission denied); with the line, a new volume is copied up from the image with both folders owned by `app`, and the same `mkdir` succeeds. The base image declares `VOLUME ["/data"]`. BuildKit, the default builder since Docker 23, keeps changes made to a declared volume path in later steps, but the legacy builder drops them, so the image must be built with BuildKit. Volumes created by an image without the line are caught by the entrypoint (§6.2).

**Engine version (PD24).** The base image sets `NUGET_PACKAGES=/data/nuget`, on the persistent volume, and NuGet never re-extracts an id and version it already holds. With one fixed version (`1.0.0-alpha.1`), an upgraded image would compile the functions against the cached old `Maquettiste.Engine.dll`: either `CS1061` for a new member (the host keeps the old functions live) or, worse, the old engine behaviour with no error. So the engine stage stamps a version derived from the engine's inputs (build metadata after `+` would not count, NuGet ignores it for identity), `pack-site.mjs` writes the same version into the zip's `Directives.cs`, and a changed engine therefore changes both the package identity and `site.sha256`, which triggers the redeploy.

**Closure project.** `docker/closure/closure.csproj` (`net10.0`) references `Maquettiste.Engine` with `Version="$(EngineVersion)"`, so the restore collects its whole dependency closure. `docker/closure/` holds `Directory.Build.props` and `Directory.Packages.props` containing `<Project />` and an `.editorconfig` with `root = true`: otherwise the project inherits the repo's central package management (a `Version` attribute is `NU1008`), lock files, warnings as errors and analyzer rules, which is why phase 1's `CompileTests.WriteProject` isolates generated projects the same way. `docker/closure/nuget-config.sh` (POSIX shell) writes the NuGet config. The generated `NuGet.Config` lists two sources, `maquettiste` (`/opt/maquettiste/nuget`) and `nuget.org`, with `packageSourceMapping` that maps every package id in the local feed (`Maquettiste.Engine`, Scriban, Jint, JsonSchema.Net, Ulid and their dependencies, exact ids) to `maquettiste` and `*` to `nuget.org`. The host writes no `NuGet.config` and restores with the SDK's normal config chain, so the `app` user's config is found; the mapping makes the functions' restore offline while other sites on the same host still reach nuget.org. The GC variables give the host process the settings under which the S13 incremental budget was measured (host-contracts, open point on runtime settings); P2-F re-measures the budget inside the image. The base image sets `DOTNET_USE_POLLING_FILE_WATCHER=true`, which affects only `PhysicalFileProvider`, not the `FileSystemWatcher` the model watcher uses.

### 6.2 Entrypoint

`entrypoint.sh` (POSIX sh, runs as `app`):

1. `mkdir -p /data/maquettiste/cache /data/sites/maquettiste.localhost`, then check that `/data/sites` and `/data/sites/maquettiste.localhost` are writable (`[ -w … ]`). If not, exit 1 with `maquettiste: /data/sites/maquettiste.localhost is not writable by uid 1654; the host volume was created by an older image. Recreate it with 'docker compose … down -v' (this keeps the model, which is on the bind mount, and drops the host's users and keys)`. If `MAQUETTISTE_LOCAL_PEERS` is unset, set it to the default gateway read from `/proc/net/route` (the row whose destination is `00000000`; the gateway is little-endian hex, `010011AC` → `172.17.0.1`). The dotted IPv4 form is what the functions compare against after normalising the IPv4-mapped peer (§3.3).
2. `needs_deploy` when `/data/maquettiste/deployed.sha256` differs from `/opt/maquettiste/site.sha256` (a missing site, or one older than the image). When it is set, remove the cached engine versions other than the image's from `/data/nuget/maquettiste.engine/` (housekeeping; PD24 already makes a new engine a new version).
3. **Deploy key (host 0.2.0 shim, PD20).** The host has no bootstrap API key. When `needs_deploy` and `/data/maquettiste/deploy.key` is missing: if `/data/config/users.json` does not exist, start the host once in the background, wait for `/healthz`, stop it (the bootstrap administrator now exists). Then, with the host stopped, append to `/data/config/apikeys.json` (jq, written to a temp file and renamed) a record `{ id, userId, name: "maquettiste-entrypoint", secretHash, display, createdUtc, lastUsedUtc: null, expiresUtc: null, revoked: false }`: `id` 12 and `secret` 43 alphanumeric characters from `/dev/urandom`, `userId` the `id` of the user with `isBootstrap: true`, `secretHash` the lowercase hex SHA-256 of the secret, `display` `sshost_<id>_••••••••`. Write `sshost_<id>_<secret>` to `/data/maquettiste/deploy.key` with mode 600. The host reads these files lazily and caches them, so the write happens only while it is stopped.
4. Start `dotnet /app/StaticSiteHost.dll` in the background, forwarding `TERM` and `INT` to it.
5. When `needs_deploy`: wait for `/healthz`, then `curl -fsS -H "X-Api-Key: $(cat deploy.key)" -H "Content-Type: application/zip" -H "X-Archive-Name: site.zip" --data-binary @/opt/maquettiste/site.zip http://127.0.0.1:8080/api/v1/sites/maquettiste.localhost/deploy` (with `-H "Host: $SiteHosting__ManagementHosts__0"` when that variable is set: the host reads only `SiteHosting:ManagementHosts`, `MANAGEMENT_HOST` is merely the name the host's own compose file maps onto it, and once the list is non-empty the host answers `127.0.0.1` as an unknown site, 404 "No site here"). On success write `deployed.sha256`; on failure log the host's answer and keep serving (the next start retries). The first functions build restores from the local feed and takes up to a minute.
6. `wait` for the host and exit with its status.

### 6.3 Compose file and file ownership

`docker/compose.yaml` is the S4 file with two additions: `image: ${MAQUETTISTE_IMAGE:-mattjcowan/maquettiste:latest}` and a health check (`curl -fsS http://127.0.0.1:8080/healthz`). It binds `127.0.0.1:8080:8080`, mounts the `maquettiste-host` volume on `/data`, `./.maquettiste` on `/data/sites/maquettiste.localhost/data` and `./` on `/repo`, and passes `MAQUETTISTE_REPO_ROOT=/repo` and `MAQUETTISTE_EDITOR_TOKEN`. The container runs as UID 1654. On Linux the two bind mounts must be writable by it: `setfacl -R -m "u:1654:rwX,d:u:1654:rwX,u:$(id -u):rwX,d:u:$(id -u):rwX" .maquettiste <output roots>` keeps the developer's ownership (open the base bits first, `chmod -R u+rwX,go+rX`, because a default ACL copies the folder's own bits, and a folder from `mktemp` is 0700); new files are still owned by 1654, and the default entry for the developer's own UID is what lets them edit and delete files and folders the editor creates (without it those are `755` and read-only to the developer). Where ACLs are unavailable, delete such files as root in a container: `docker run --rm --user 0 -v "$PWD:/w" --entrypoint rm <image> -rf /w/<path>`. Running as the developer's UID was an open point (§11, question 4). *Status (round 6, 2026-09-29): built.* The compose file starts the container as root (`user: "0:0"`) with `MAQUETTISTE_UID` and `MAQUETTISTE_GID` (default 1654); the entrypoint refuses UID 0 and non-numeric ids, hands its own volume (`/data`, never the bind mounts) and `/home/app` to that user and runs the editor as it with `setpriv`. With the two variables exported (`id -u`, `id -g`) no ACL or `chmod` is needed, on Linux and on the Mac, whose Docker file sharing does not map the container's user (the live test's "Permission denied"). Unset, the editor runs as 1654 and the ACL recipe above still applies. `docker/smoke.sh` checks both runs.

**Deleting the site deletes the model.** The host's site delete (management UI or API) runs `Directory.Delete(<site folder>, recursive: true)` (`SiteStore.DeleteAsync`). That descends into the bind mount and deletes every file of the working tree's `.maquettiste/` before failing on the mount point itself, and the host logs the failure and reports the delete as done. A site rename (`Directory.Move`) fails on the same mount. Host 0.2.0 has no way to protect a site: `SiteRecord` has no description or lock to mark it with. So the image's README and a comment in `docker/compose.yaml` say never to delete or rename `maquettiste.localhost` from the host's UI, the entrypoint logs the same warning at every start, recovery is `git checkout -- .maquettiste` (uncommitted model edits are lost), and §11 open point 11 asks the host to skip mount points.

### 6.4 Opening the editor on the billing fixture

```bash
# prerequisites: Docker 24+, the repo checked out; no .NET or Node needed on the machine
docker build -f docker/Dockerfile -t mattjcowan/maquettiste:dev .
docker/dev-billing.sh        # does the four steps below
#   rm -rf tmp/billing && mkdir -p tmp/billing && cp -r tests/fixtures/models/billing/. tmp/billing/
#   mkdir -p tmp/billing/.maquettiste/templates && cp -r packs/sql-ddl packs/csharp-dapper tmp/billing/.maquettiste/templates/
#   [ "$(uname)" = Linux ] && setfacl -R -m "u:1654:rwX,d:u:1654:rwX,u:$(id -u):rwX,d:u:$(id -u):rwX" tmp/billing
#   MAQUETTISTE_IMAGE=mattjcowan/maquettiste:dev docker compose -f docker/compose.yaml --project-directory tmp/billing up -d
docker compose -f docker/compose.yaml --project-directory tmp/billing logs -f   # until "maquettiste: deployed" and the functions build
curl -fsS http://127.0.0.1:8080/healthz && curl -fsS --resolve maquettiste.localhost:8080:127.0.0.1 http://maquettiste.localhost:8080/api/health
# open http://maquettiste.localhost:8080 — no login; Entities shows "Billing overview"
```

`tmp/` is gitignored, so the fixture itself is never edited. `docker compose … down` stops it; `down -v` also drops the host volume (next start deploys again). The SPA alone, with no Docker at all: `cd src/editor && npm ci && npm run dev`, then open `http://localhost:5173` (mock mode on the same billing fixture).

## 7. Reference application (P2-R)

### 7.1 Contents

```
samples/reference-app/
├── README.md
├── domain/*.yaml                  # the domain description the seed script turns into model batches
├── tools/seed.mjs                 # builds the model through a running editor's API (bearer token), batch by batch
├── .maquettiste/                  # the committed model, sql-ddl and csharp-dapper packs (copies of packs/, checked equal in CI)
├── db/main/                       # committed root: generated DDL and migrations
├── src/ReferenceApp.Data/         # ReferenceApp.Data.csproj (net10.0, Dapper, Npgsql); Generated/ is a built root; partials hand-written
└── Directory.Build.props  Directory.Packages.props  .editorconfig   # `<Project />`, `<Project />`, `root = true`
```

The three isolation files keep the repo's central package management, lock files, XML-doc rule (CS1591 as an error outside `tests/` and `bench/`) and analyzer errors away from the sample, as in `docker/closure/` (§6.1). `ReferenceApp.Data.csproj` therefore pins `Dapper` and `Npgsql` with `Version` attributes and sets its own `Nullable` and `ImplicitUsings`; generated public types need no XML docs. The repo's `global.json` still applies.

**Domain: a B2B wholesale distributor** ("Northwind Operations"): 200 entities in 12 packages, plus about 8 value objects (Money, Address, EmailAddress, PhoneNumber, Dimensions, Weight, DateRange, Percentage), 25 enums, about 300 relations, stereotypes `aggregate-root`, `audited`, `soft-delete`, `reference-data`, a category tree with one category per package, and tags.

| Package | Entities | Package | Entities |
| --- | --- | --- | --- |
| Identity and access | 12 | Inventory and warehousing | 24 |
| Parties and CRM | 22 | Fulfillment and shipping | 18 |
| Catalog | 24 | Purchasing and suppliers | 18 |
| Pricing and promotions | 16 | Billing and payments | 20 |
| Sales orders | 20 | Returns and service | 10 |
| Finance and ledger | 10 | Reference data | 6 |

It must exercise every mapping shape the packs support: associations with foreign keys, compositions with ordered children, many-to-many with and without relation attributes (junction tables), self-references (category tree, employee manager), TPH inheritance (Party → Customer, Supplier, Carrier), TPT inheritance (Payment → CardPayment, BankTransfer), value-object embedding and a value-object collection table, enum lookups and string storage, a designed table overlay, alternate keys, indexes and a sequence. One database, `main`, PostgreSQL 16.

### 7.2 Gate 2 check (`.github/workflows/gate2.yml`)

1. `maquettiste validate` on `samples/reference-app` exits 0 (no errors).
2. **From the editor.** Start the image over a copy of the sample; Playwright project `live` runs `gate2.spec.ts`: open the editor, open an entity in the Entities workspace, add an attribute through the grid, rename a relation in the inspector, save; in Generate, plan all packs, open a diff, apply, wait for `job.completed`, and assert that its `outcome` (the job's `applyResult.outcome`) is `succeeded`. The job's `state` is not checked: it is `succeeded` for any apply that ran to completion, a `stale`, `invalid` or `conflicts` one included.
3. `maquettiste generate --check` on the copy exits 0: the editor's apply equals the CLI's output (no drift).
4. `dotnet build src/ReferenceApp.Data -warnaserror` in the copy exits 0: the generated C# compiles. The copy is `tmp/gate2/`, a copy of `samples/reference-app/` inside the checkout (`tmp/` is gitignored), isolation files included, so it builds exactly as the sample does.
5. `psql -v ON_ERROR_STOP=1 -f db/main/schema.sql` against a `postgres:16` service container exits 0: the generated DDL applies.

The committed model itself is authored through the editor API: `tools/seed.mjs` posts one `POST /api/model/batch` per package (then relations, mappings and diagrams) to a running editor, so every model file is written by the editor's store, never by hand (PD21).

## 8. Workstreams

| Workstream | Owns | Implements | Consumes |
| --- | --- | --- | --- |
| P2-F Functions and Docker | `src/Maquettiste.Functions/`, `tests/Maquettiste.Functions.Tests/`, `docker/`, `.github/workflows/image.yml`, the functions job in `ci.yml`, the engine additions E1–E4 (`GenerationService.cs`, `ModelStore.cs`, `Model/Documents.cs` and `Model/ModelIndexer.cs` edits and new files in `src/Maquettiste.Engine/Editor/`, tests in `tests/Maquettiste.Engine.Tests/Editor/`, `engine-design.md` §15) | §3, §6; `src/editor/src/mocks/recorded/` (written by its recording tests) | engine public API; `docs/api/openapi.yaml`; host 0.2.0 |
| P2-E Editor SPA | `src/editor/` (except `src/mocks/recorded/`), `.github/workflows/editor.yml` | §4, §5 (`pack-site.mjs`, `deploy.mjs`) | `docs/api/openapi.yaml`; the billing fixture; P2-F's recorded responses; the image for `live` |
| P2-R Reference app | `samples/reference-app/`, `.github/workflows/gate2.yml`, and `src/editor/tests/e2e/gate2.spec.ts` (the only file it adds under `src/editor/`) | §7 | the CLI, the image, the SPA |

`docs/api/openapi.yaml` is shared: a change to it lands in its own commit before the code that uses it, and regenerates `src/editor/src/api/schema.d.ts` in the same commit. Merge order: P2-F's engine additions (E1–E4) first; then P2-F functions and Docker and P2-E in parallel (P2-E starts on day one against the mocks: a testable UI exists as soon as the shell and the Entities workspace run in mock mode); then P2-R, whose seeding needs the image; `gate2.yml` last.

## 9. CI

`ci.yml` gains the functions tests (Ubuntu and macOS). `editor.yml`: `npm ci`, `gen-api` with a diff check, lint, type check, Vitest, Playwright `mock`, `vite build`. `image.yml`: build the image, the offline smoke test of §3.9, Playwright `live`; on `v*` tags publish `mattjcowan/maquettiste:<version>` and `latest`. `gate2.yml`: §7.2, on pushes to `main` and on demand.

## 10. Decisions

| Id | Decision | Why |
| --- | --- | --- |
| PD1 | UI primitives: shadcn/ui on Radix with Tailwind 4, restyled only through the S15 tokens (settles the S22 question; Ark UI with Park UI dropped) | Largest ecosystem, copy-in components that the token layer fully controls, React 19 support; `cmdk`, resizable panels and forms fit it |
| PD2 | Layout engine: elkjs in a web worker; dagre only if the worker or ELK fails (settles the S22 question) | ELK handles ports, orthogonal routing and nesting that phase 3 statecharts need; EPL-2.0 used unmodified is compatible (S20); the worker keeps the UI responsive |
| PD3 | Editor state in Zustand; server state only in TanStack Query | Small, typed, no provider tree; one source of truth for server data; XState stays for phase 3 statechart semantics |
| PD4 | API client: `openapi-typescript` types plus `openapi-fetch`, generated from `docs/api/openapi.yaml` and committed | One contract, no hand-written DTOs, zero runtime codegen; a CI diff check keeps it current |
| PD5 | Mocks: MSW with an OpenAPI-generated baseline, a typed stateful layer over the billing fixture, replayed engine recordings, and an ajv contract suite | The SPA runs and is testable in a browser with no backend, and cannot drift from the contract |
| PD6 | Serving: Vite dev server with mocks (default) or a proxy to the container in development; in the host, a static release from `site.zip` with the host's `index.html` fallback, `_headers` for caching, and `deploy:watch` for fast releases | S4's loop; no host change needed |
| PD7 | Realtime events documented as OpenAPI `webhooks` plus `x-maquettiste-realtime` | Client generators emit typed payloads; nothing is POSTed |
| PD8 | `GET /api/model/elements/{id}` returns the engine's `ElementDocument` unchanged; the SPA edits `json` and sends it back | One handler, one call, exact mirror; `json` is what `SaveAsync` accepts |
| PD9 | Local trust: a peer that is loopback or in `MAQUETTISTE_LOCAL_PEERS` (defaulting to the container gateway), compared after `MapToIPv4`, with a local `Host` and no forwarding header, unless `MAQUETTISTE_LOCAL_TRUST=off`; plus Origin and content-type checks | Behind a `127.0.0.1` binding the container sees the gateway, not loopback, and Kestrel's dual-stack socket reports it IPv4-mapped (measured on Linux: `::ffff:172.17.0.1`); the `Host` and forwarding-header conditions keep a same-host reverse proxy from making every visitor admin; the checks stop other web pages from using the credential-less mode |
| PD10 | Model root is `ISite.Data` (the bind mount); the index cache lives under `MAQUETTISTE_CACHE_DIR` on the host volume, keyed by repo path | S4; settles the host-contracts cache open point |
| PD11 | Phase 2 sign-in is the editor token exchanged for a data-protected cookie bound to the token's fingerprint | No accounts until phase 4; rotation ends sessions; the cookie also authorizes the hub |
| PD12 | Problems other than engine outcomes are RFC 9457 documents with a stable `code` | `Results.Problem` is built in; the SPA switches on `code` |
| PD13 | `validation.completed` carries a whole-model report, debounced 750 ms after changes | Exact problems list without referrer bookkeeping; validation is milliseconds at gate 2 scale (revisit at gate 4) |
| PD14 | Presence through `PUT /api/presence` and a server registry | The browser cannot publish; the server checks the connection belongs to the caller |
| PD15 | Engine additions E1–E4 (database view, pack list, settings read and write, category and stereotypes in `ElementSummary`) | The Database, Mappings and Settings workspaces and the explorer filters need them; resolved R-types cannot be serialized |
| PD16 | Per-file diffs: the engine's unified diff rendered by an in-house viewer; Monaco for JSON, SQL and conflict views | The engine offers unified diffs, not both sides; no extra dependency |
| PD17 | Sub-element ids (attributes, ends, keys, columns) are ULIDs made in the SPA | The engine assigns only a missing top-level id |
| PD18 | Per-diagram display options kept per browser in phase 2 | The diagram schema has no place for them yet (§11) |
| PD19 | `#:package` directives live only in `_functions/Directives.cs`; the csproj excludes it | A normal project cannot compile `#:` lines (CS9298); tests and IDEs build the rest unchanged |
| PD20 | First-boot deploy through a key the entrypoint seeds into the host's `apikeys.json` while the host is stopped | Host 0.2.0 has no seed feature or bootstrap key; replace with a host feature when one exists (§11) |
| PD21 | Gate 2's model is seeded through the editor API and exercised end to end by a Playwright walk | 200 entities cannot be drawn by hand in CI; every file still comes from the editor's store |
| PD22 | NuGet: the engine's closure in `/opt/maquettiste/nuget` with package source mapping in the `app` user's config | Offline first build without cutting other sites off nuget.org |
| PD23 | Server GC with dynamic adaptation and tiered PGO off, set in the image | The settings the incremental budget was measured under |
| PD24 | The image packs the engine as `<Directory.Build.props version>.b<12 hex of the engine's inputs>` and stamps the same version into the zip's `Directives.cs` | The host's NuGet cache (`NUGET_PACKAGES=/data/nuget`) is on the persistent volume and never re-extracts a version it holds, so a fixed version would keep the old engine after an image upgrade |

## 11. Open points for the owner

1. **Gate 2 reading.** Is "modeled … from the editor" met by API seeding through the editor plus the Playwright walk (PD21), or must the model be built in the UI by a person?
2. **First boot.** Keep the `apikeys.json` shim (couples the image to host 0.2.0's file format), or add a `SeedSites` (or bootstrap API key) feature to static-site-hosting 0.3.0 and drop it? Recommended: the host feature.
3. **Local trust on Docker Desktop.** The gateway rule is measured on Linux Docker Engine; P2-F must confirm the peer address on Docker Desktop (macOS, Windows), where port forwarding may show all traffic from one internal address. Recommended: document the observed address as the Docker Desktop value of `MAQUETTISTE_LOCAL_PEERS`, and not `*`. The container cannot see how its port is bound, so `*` makes every caller that sends a local `Host` an admin whenever the port is published beyond `127.0.0.1`; the `Host` and forwarding-header conditions (§3.3) do not stop a caller that forges them. Keep `*` at all?
4. **Container user.** Stay UID 1654 with ACLs, or support `PUID`/`PGID` (the container starts as root, remaps `app`, and drops privileges with `setpriv`) so generated files belong to the developer? *Answered by the build (round 6):* `MAQUETTISTE_UID`/`MAQUETTISTE_GID`, see §6.3; the Mac run is still to be verified on a Mac.
5. **Engine edits.** E1–E4 touch `GenerationService.cs`, `ModelStore.cs`, `Model/Documents.cs` and `Model/ModelIndexer.cs` (W6 and W1 files) while other workstreams change engine code; confirm P2-F owns these edits and the merge order.
6. **Diagram display options.** Add a `display` object to `diagram.json` (schema change, format version stays 1 if optional) or keep them per browser?
7. **Settings scope.** Phase 2 edits conventions and vocabularies only; confirm type maps, outputs and formatters stay read-only until phase 4. *Status (round 6):* Settings also has **General** (the project `name`, `branding.icon` stored through `PUT /api/project/branding/icon`, `branding.colors.light` and `.dark`; rules MQ8001 to MQ8003; none of it reaches generated output) and **Locales**; type maps, outputs and formatters stay read-only.
8. **Vite line.** The SPEC pins Vite 6 while Vite 7 is current; keep 6 or move?
9. **Schema lint.** Redocly reports 15 `no-required-schema-properties-undefined` warnings in `schemas/v1/table.json` and `batch.json` (`required` inside `if`/`then`); keep the style or restate those schemas?
10. **Names.** Reserve `maquettiste` on Docker Hub (the image name `mattjcowan/maquettiste`), npm and NuGet before `image.yml` publishes (S22).
11. **Site delete and the model mount.** Host 0.2.0's site delete recursively deletes the bind-mounted `.maquettiste/` (§6.3). Ask static-site-hosting 0.3.0 to stop at mount points when deleting or renaming a site folder (or to let a site be marked undeletable), and drop the warning when it does?
12. **S15 token table.** Adopt the adjusted light `status.warning` (#9E5400) and `status.success` (#1B7348), and the new `accent.foreground` and `border.input` tokens (§4.7), into S15's proposed table, so the spec and `tokens.css` agree.
