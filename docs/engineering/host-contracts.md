# Host contracts

What `static-site-hosting` gives the editor's C# functions, and what that requires of `Maquettiste.Engine` so the functions stay thin. Every name below is taken from the host's source at tag `v0.3.0`, `src/StaticSiteHost.Abstractions` (package `StaticSiteHost.Abstractions` 0.3.0, with its XML documentation) and the host README. 0.3.0 adds tool calling to `IAiChat` over `v0.2.0` (commit `a74bc5d`); everything else this file relies on is unchanged, and code written against 0.2.0 compiles and behaves the same. If a name here disagrees with that source, the source wins; fix this file.

## Part 1. What the host provides

### 1.1 Discovery and compilation

- **Where.** A top-level `_functions/` folder in the deployed zip. Only `.cs` and `.linq` files directly inside it compile; other files are ignored with a warning. They become the site's complete set of functions. If they fail to compile, nothing is deployed and the live functions keep answering. Only an administrator may deploy a zip with `_functions/`.
- **How.** The host generates `functions.csproj` (a library, assembly `SiteFunctions`) and runs `dotnet publish --configuration Release` with `-p:ImportDirectoryBuildProps=false` and friends, then loads the output into a collectible `AssemblyLoadContext` (`FunctionLoadContext`, `isCollectible: true`). One build at a time. Package restore uses `NUGET_PACKAGES=/data/nuget`; the host writes no `NuGet.config`, so extra sources come from the SDK's normal config chain.
- **Directives** (a `.cs` file; a `.linq` file says the same in its XML header):

| Directive | Effect |
| --- | --- |
| `#:sdk Microsoft.NET.Sdk.Web` | accepted; ASP.NET Core is always referenced |
| `#:package Name@Version` | NuGet package restored on the server |
| `#:property Name=Value` | MSBuild property |
| `#:project …` | refused |

- **Abstractions.** `#:package StaticSiteHost.Abstractions@*` and `using StaticSiteHost.Functions;`. The host never restores it: it swaps in a compile-only reference to its own copy (`AssemblyVersion` fixed at `1.0.0.0`), so the version in the file is ignored.
- **Several files** compile as one project. Two files pinning different versions of one package are refused; `*` accepts another file's pin. Duplicate class names fail with `CS0101`.
- **Limits** (`FunctionBundleBuilder.MaxFiles`, `FunctionBundleBuilder.MaxTotalBytes`, `FunctionSourceReader.MaxSourceBytes`):

| Limit | Value |
| --- | --- |
| Files | 50 |
| One file | 1 MB |
| All files | 4 MB |
| Entries in an uploaded `.zip` of functions | 500 |
| One build | 5 minutes |
| One editor test | 30 seconds |

  Restored packages do not count against these limits; only the source files do.
- **Handlers.** `public static` methods on a `public` class with ASP.NET's own route attributes (`[HttpGet("/path")]`, `[HttpPost]`, `[HttpPut]`, `[HttpPatch]`, `[HttpDelete]`, `[Route]`). `{name}` captures a segment, `{name?}` is optional. Return `IResult`, `string`, `int` (a status) or nothing (`204`), or a `Task`/`ValueTask` of one. Any other object is refused: serialize with `Results.Json(value, options)`, holding the `JsonSerializerOptions` in a static field of the function's own file (server-owned options would pin every old build in memory).
- **Parameters** are filled by type: `HttpContext`, `HttpRequest`, `HttpResponse`, `CancellationToken`, `ISite`, `ISiteVariables`, `IReadOnlyDictionary<string, string>` (all variables, secrets included), `DirectoryInfo` (the data folder), `IRealtime`, `IAiChat`, a plain `ILogger` (category `functions:<domain>`), `IServiceProvider`, any registered service, and simple values from the route then the query string. The request body is read by the handler itself.

### 1.2 Attributes

All in `StaticSiteHost.Functions`; the server finds them by name. A method has one role only; at most one method per hook attribute.

| Attribute | Shape | Notes |
| --- | --- | --- |
| `[Middleware(Order = 0)]` (`int Order { get; init; }`) | `public static Task Name(HttpContext context, Func<Task> next, …)`; `Task`, `ValueTask` or `void`, never `async void` | Runs on every request, static files included, but never for `/_host/` (realtime, AI). Lower `Order` runs first. May set `HttpContext.User` for later handlers. If functions with middleware fail to load, the whole site answers `503` |
| `[ConfigureServices]` | `public static void Name(IServiceCollection services, …)`, not `async` | May also take `ISite`, `ISiteVariables`, `IRealtime`, `IAiChat`, `DirectoryInfo`, `IReadOnlyDictionary<string, string>`, `ILogger`, `CancellationToken`. The collection already holds `ILoggerFactory`, `ILogger<T>`, `ILogger`, `IDataProtectionProvider`, `IHttpClientFactory`, `TimeProvider`, `ISite`, `ISiteVariables`, `IRealtime`, `IAiChat`. Built with `ValidateScopes = true, ValidateOnBuild = true`. `AddHostedService` does nothing |
| `[BackgroundService]` | `public static Task Name(CancellationToken stoppingToken, …)`; `Task` or `ValueTask` | Runs while the functions are live; gets the root `IServiceProvider`. On throw, restarted after 1 s, doubling to 1 min. Returning ends it until the next load |
| `[Schedule("0 3 * * *")]` (`ScheduleAttribute(string cron)`, `string Cron`, `bool RunOnStart`) | `public static Task/ValueTask/void Name(…)` | Five-field cron, UTC. Runs never overlap (a due run is skipped); missed runs are not made up. One DI scope per run |
| `[Every("10m")]` (`EveryAttribute(string interval)`, `string Interval`, `bool RunOnStart`) | as `[Schedule]` | `30s`, `5m`, `2h`, `1d` or a `TimeSpan`; 10 s to 365 days |
| `[RealtimeConnect]` | returns `string?`, `bool`, or `Task`/`ValueTask` of either | A string is the connection's user (used by `PublishToUserAsync`); `null`, `""` or `false` refuses |
| `[RealtimeJoin]` | must take `string group`; returns `bool`, `Task<bool>` or `ValueTask<bool>` | Runs on every join, rejoins included |
| `[AiAccess]` | returns `bool`, `Task<bool>` or `ValueTask<bool>` | Decides `/_host/ai/chat`; runs after the body was read and the limiter counted |

Hooks run without middleware, so they never see the `HttpContext.User` that middleware set: each hook reads the session cookie itself. A hook that throws, or belongs to functions that failed to load, refuses. Hooks must register nothing on the `HttpContext`.

### 1.3 Interfaces

```csharp
public interface ISite
{
    string Domain { get; }
    DirectoryInfo Data { get; }            // /data/sites/<domain>/data; never served; survives deploys and rollbacks
    ISiteVariables Variables { get; }
    IServiceProvider Services { get; }     // request scope in a request; root provider in jobs and background services
    IRealtime Realtime { get; }
    IAiChat Ai { get; }
}

public interface ISiteVariables
{
    string? this[string name] { get; }
    bool TryGet(string name, [MaybeNullWhen(false)] out string value);
    string Get(string name, string fallback = "");
    IReadOnlyDictionary<string, string> All { get; }
    bool IsPublic(string name);
}
```

- `SiteHttpContextExtensions`: `ISite Site(this HttpContext context)`, `bool TryGetSite(this HttpContext context, out ISite? site)`, `void UseSite(this HttpContext context, ISite site)`, `const string ItemKey = "StaticSiteHost.Site"`.
- `ISite.Services` throws `InvalidOperationException` while `[ConfigureServices]` methods run. After the functions are unloaded, every `ISite` member but `Domain` throws, and so do the variables, realtime and AI it handed out.
- In a request, variables are read once at request start; outside a request, each read sees the latest values.
- The data folder is created on first use. Helper code finds its path in `HttpContext.Items["StaticSiteHost.DataDirectory"]`.

```csharp
public interface IRealtime
{
    int ConnectionCount { get; }
    IReadOnlyList<RealtimeConnection> Connections { get; }
    IReadOnlyList<string> Groups { get; }
    IReadOnlyList<RealtimeConnection> Members(string group);
    Task PublishAsync(string eventName, JsonElement? payload = null, CancellationToken ct = default);
    Task PublishToGroupAsync(string group, string eventName, JsonElement? payload = null, CancellationToken ct = default);
    Task PublishToUserAsync(string user, string eventName, JsonElement? payload = null, CancellationToken ct = default);
    Task PublishToConnectionAsync(string connectionId, string eventName, JsonElement? payload = null, CancellationToken ct = default);
    Task AddToGroupAsync(string connectionId, string group, CancellationToken ct = default);
    Task RemoveFromGroupAsync(string connectionId, string group, CancellationToken ct = default);
    Task RemoveGroupAsync(string group, CancellationToken ct = default);
    Task DisconnectAsync(string connectionId, CancellationToken ct = default);
}
public sealed record RealtimeConnection(string Id, string? User, DateTimeOffset ConnectedUtc, IReadOnlyList<string> Groups);
```

- `RealtimeExtensions` adds `PublishAsync<T>(this IRealtime realtime, string eventName, T payload, JsonSerializerOptions options, CancellationToken ct = default)` and the same generic overload for group, user and connection.
- Event and group names match `^[A-Za-z0-9_.:-]{1,64}$` (so `job:{ulid}` is valid). A payload over 256 KB of JSON throws `ArgumentException`. Limits: 1,000 connections and 1,000 groups per site, 100 groups per connection, 20 connections per client address. Groups live in memory; pages rejoin after a restart.
- The host publishes `site.deployed` with `{ release, functions, source }` (`deploy`, `rollback` or `functions`) after each go-live.

```csharp
public interface IAiChat
{
    bool IsConfigured { get; }
    string? Model { get; }
    Task<AiChatResponse> CompleteAsync(AiChatRequest request, CancellationToken ct = default);
    IAsyncEnumerable<AiChatChunk> StreamAsync(AiChatRequest request, CancellationToken ct = default);
}
```

- `AiChatRequest` has `required IReadOnlyList<AiMessage> Messages`, `string? System`, `string? Model`, `int? MaxTokens`, `double? Temperature`, and since 0.3.0 `IReadOnlyList<AiTool>? Tools` (at most 128, functions only) and `AiToolChoice? ToolChoice` (`AiToolChoice.Auto`, the default, `None`, `Required`, `Tool("name")`). Records: `AiMessage(string Role, string Content)` with `AiMessage.User(…)`, `.Assistant(…)`, `.System(…)`, `.AssistantToolCalls(text, calls)` and `.ToolResult(toolCallId, content, isError)` (roles `user`, `assistant`, `system`, `tool`; the tool members are `ToolCalls`, `ToolCallId`, `IsError`); `AiTool(string Name, string Description, JsonElement InputSchema)` (name `^[A-Za-z0-9_-]{1,64}$`, unique; the schema a JSON object); `AiToolCall(string Id, string Name, JsonElement Arguments)`; `AiChatResponse(string Text, string Model, int InputTokens, int OutputTokens, string? StopReason)` with `IReadOnlyList<AiToolCall> ToolCalls` (empty for a text answer) and `StopReason` `tool_calls` (`AiChatResponse.ToolCallsStopReason`) for a turn of calls from either provider kind; `AiChatChunk(string? Text, AiChatResponse? Final)`. `StreamAsync` streams the text; the calls arrive whole in the last chunk's `Final.ToolCalls` (no partial tool-call chunks). Each `JsonElement` is copied when the record is made. Failures throw `AiChatException` (`int? StatusCode`: the provider's status, null when there was no answer; a model that cannot use tools usually fails with 400). Calls from functions bypass `[AiAccess]` and the visitor limits, so a function that chats checks its caller itself (the assistant's `/api/assist/*` do).
- The rules a tool-calling loop follows; the host checks them before any provider call and throws `ArgumentException` naming the rule: (1) append `AiMessage.AssistantToolCalls(answer.Text, answer.ToolCalls)` and then one `ToolResult` per call, immediately, with nothing between (not even a system message); (2) keep sending `Tools` on every turn once the conversation has calls in it, and force a text answer with `ToolChoice = AiToolChoice.None` instead of dropping them; (3) `call.Arguments.ValueKind == JsonValueKind.String` means the model wrote input that is not JSON: answer it with an error `ToolResult` and go on; (4) `ToolCalls` empty with `StopReason` `length` or `max_tokens` means the model ran out of tokens part way through its calls and all of them were dropped: raise `MaxTokens` or tell the user, never treat it as the answer; (5) cap the turns and count `InputTokens + OutputTokens` against a budget, since every turn resends the whole conversation.
- Limits: `SiteHosting:AiMaxRequestBytes` (64 KB) and the 64-message cap apply only to browsers at `/_host/ai/chat`; functions have no request size limit of their own (the provider's context window is the limit). `SiteHosting:AiTimeoutSeconds` (120 by default) applies per call, and while streaming it measures the silence between events. `IAiChat.IsConfigured` and `IAiChat.Model` are all a function sees of the provider; its kind and name are not exposed.
- Test helpers (`StaticSiteHost.Functions.Testing`): `FakeAiChat` with `Reply(text)`, `ReplyToolCall(name, arguments)` and `ReplyToolCalls(text?, params AiToolCall[])` (ids `call_1`, `call_2`, ...), `Requests` (every request sent), `IsConfigured` and `Model` (`fake-model`); it checks every request against the host's rules. Token counts are word counts.

### 1.4 Site variables

- Declared in `_variables.json` at the zip root: a default string, or an object with `default`, `description`, `public`, `secret`, `required`. Values set on the site live in `site.json` and survive deploys.
- `${env:NAME}` in a value or default is replaced from the server's environment when read. Only an administrator may deploy a default using it; a member's deploy keeps the variable and drops that default. Secrets are encrypted with the host's data-protection keys and never sent back.
- Limits: names `[A-Za-z_][A-Za-z0-9_]*`, 1 to 64 characters, case-sensitive; 200 declared; 8 KB per value.

### 1.5 Lifecycle and stop grace

- Functions with a background service or a job load as soon as the server listens, and again after every deploy, rollback or rename; others load on first request.
- **Replacement order** (`FunctionHost.StopGrace = 15 s`, `FunctionHost.DrainGrace = 30 s`): the new build goes live first; then the old build's token is cancelled and its background services and jobs get **15 seconds** to stop; then in-flight requests get 30 seconds; then its services are disposed; then it is unloaded. The deploy does not wait. So **the old and new builds overlap** for up to 15 seconds of background work.
- The host removes process-wide event handlers and timers left by a replaced build and clears ADO.NET pools; it warns if a build is still in memory after five minutes.
- The editor's test panel runs `[ConfigureServices]` for every test (singletons start over) and never starts background services or jobs.

### 1.6 Disk, network and deploy

- `/data/sites/<domain>/data/` is the data folder; `/data/config/` is host configuration; `/data/tmp/` is cleared at startup. The container runs as UID 1654.
- `GET /healthz` answers `{ "status": "ok" }` on any host. Deploys go to `POST /api/v1/sites/{domain}/deploy` on the management host with `X-Api-Key`.

## Part 2. Requirements on the engine

Each line is a requirement on `Maquettiste.Engine`. The functions layer should be route parsing, auth, and one call into the engine per handler.

**Packaging and isolation**

1. The engine targets `net10.0` and references neither `StaticSiteHost.Abstractions` nor `Microsoft.AspNetCore.App`; the functions adapt engine types to the host.
2. `#:package Maquettiste.Engine@<exact version>` must be all a function file needs: no build targets, no native assets, and a dependency closure that restores from one offline NuGet source.
3. No static mutable state, no process-wide event subscriptions, no static timers or thread pools, so the collectible load context unloads cleanly.
4. The engine never reads the current directory or environment variables; model root, repo root, index-cache folder and journal folder are explicit options (`EngineOptions`). The output allowlist is repo configuration, not a host option: it comes from `outputs.allow` and `outputs.deny` in the repo's `maquettiste.json` (SPEC Section 4), and the path policy confines every output to the repo root whatever the allowlist says.
5. Every public result type serializes with `System.Text.Json` and `JsonSerializerDefaults.Web` without custom converters, so a function's own static options suffice.

**Model store**

6. `ModelStore` is constructible synchronously, without I/O, and never throws for model content, because `[ConfigureServices]` is synchronous and a throwing singleton breaks every request.
7. Loading is explicit and async (`LoadAsync(CancellationToken)`, also triggered on first read), idempotent under concurrent callers.
8. `ModelStore` is thread-safe: reads return immutable snapshots of the index (summaries, element by id, references) without blocking writers.
9. `ModelStore` depends on no scoped service, starts no threads of its own, and implements `IAsyncDisposable` that completes well inside the 30-second drain.
10. The index-cache folder is an option independent of the model root, so the cache can live on the host volume while the model sits on a bind mount.

**Element saves**

11. Every element exposes a content hash of its canonical bytes, usable verbatim as an ETag value.
12. A save takes the expected hash and returns a result (`Saved` with new hash and diagnostics, `Conflict` with the current hash and current element, `Invalid` with diagnostics) instead of throwing, so the handler maps it to 200, 409 or 422.
13. Create returns the new id and hash; delete returns `Referenced` with the referrers unless the request names a resolution.
14. Every save writes canonical JSON only, under the model root only.

**Atomic batches**

15. `ApplyBatchAsync` validates the whole batch in memory first, checks each element's expected hash, then stages every file as a temp file on the same volume and renames into place; on any failure the repo is unchanged.
16. A batch parses from untrusted JSON (editor, refactoring or AI proposal) with schema validation before any disk access, so `/api/model/batch` and `/api/assist` share one path.

**File watcher hook**

17. `ModelStore.RefreshAsync(IReadOnlyCollection<string> paths, CancellationToken)` updates the index for changed paths and returns a `ChangeSet` (changed and deleted ids, new hashes, source).
18. `ModelStore.RescanAsync(CancellationToken)` reconciles the whole folder by content hash, for mounts where watch events are lost, and is cheap when nothing changed.
19. The engine's own writes return their `ChangeSet` and are recognised by hash when the watcher reports them, so one save produces one `model.changed`.
20. A `ChangeSet` or diagnostics summary can be cut to fit the 256 KB realtime payload limit, with a flag saying it was truncated and the editor must refetch (`ChangeSet.TruncateTo` and `ValidationReport.TruncateTo`, both setting `Truncated`; a report keeps its counts).

**Validation results**

21. A diagnostic is a plain record: rule code, severity, message, element id, file path, JSON pointer, optional line and column; the same records feed the API, `validate --format json` and SARIF.
22. `ValidateAsync(scope, CancellationToken)` returns diagnostics grouped by element plus counts by severity, and is cancellable.

**Jobs**

23. The engine provides the job model: a ULID `JobId`, states `Queued`, `Running`, `Succeeded`, `Failed`, `Cancelled`, progress, result or error, and queue position.
24. The engine provides the queue (bounded, one running job) with `TryEnqueue`, `GetAsync(id, ct)`, `ListAsync(ct)`, `Cancel(id)` and `RunAsync(CancellationToken)`, so the `[BackgroundService]` job worker is a single awaited call and no request thread blocks on disk. Job records carry queued, started and finished timestamps.
25. Progress is reported through `IProgress<T>` with stage, done, total and current path, at file granularity; the functions throttle and publish it to `job:{id}`.
26. Cancellation is observed between files and returns within one second, well inside the 15-second stop grace, with the journal consistent.
27. Job records for finished jobs persist in the cache folder, so `GET /api/jobs/{id}` still answers after a redeploy.
28. A run holds an exclusive OS file lock in the journal folder, because the old and new builds overlap for up to 15 seconds; a second holder waits or reports `Busy`.

**Plan, apply and the run journal**

29. `PlanAsync` returns a plan with a ULID id, per-unit input hashes, files added, changed and deleted, and hand edits, and persists it so a new `ModelStore` can load it by id.
30. `ApplyAsync(planId)` recomputes every unit's input hash and re-hashes every planned path, and returns `Stale` with the changed units and paths, writing nothing, if any differs; otherwise it applies the stored bytes without rendering again and touches no path outside the plan.
31. The plan offers a unified diff for one path without rendering the whole plan again.
32. The run journal records each file as it is written and is flushed per file; the manifest is updated per pack as each pack completes.
33. A run that finds an unfinished journal resumes it, and files listed in the journal are never reported as hand edits.
34. Every generated write goes through the output allowlist; `.git/` and `.maquettiste/` are always denied.

**Preview**

35. `PreviewAsync(unit, elementId, CancellationToken)` renders one unit with no writes and returns output plus diagnostics.

## Open points

- **Index cache location in local mode.** The only folder the host hands the functions is `ISite.Data`, which Section 4 bind-mounts onto `.maquettiste/`. Section 4 wants the cache on the host volume, not the mount, but the host has no second per-site folder. The functions need a path from a site variable (for example under `/data/config/`) and requirement 10 must hold. Phase 1 is unaffected, since the CLI passes its own paths.
- **Offline first build.** The host writes no `NuGet.config`. The image's local source for `Maquettiste.Engine` must be registered in the `app` user's NuGet config (or a `NuGet.Config` in `/data`, above the generated project in `/data/sites/<domain>/functions/<id>/`), and that must be tested before phase 2.
- **Realtime payload size.** Section 16's `validation.completed` "diagnostics by element" will exceed 256 KB on a large model; requirement 20 assumes a summary plus refetch.
- **Runtime settings for the incremental budget.** The engine cannot choose its host process's GC or JIT settings. SPEC Section 13's 2 s incremental budget (the editor's and watch mode's runs) was measured by the benchmark (`bench/README.md`, "Runtime settings") under three configurations of the same process: the CLI's settings (server GC, no dynamic adaptation, tiered PGO off) about 1.3 to 1.6 s, about 1.35 s with eight GC heaps but with GC pauses that pushed 2 runs in 8 just over 2 s; the .NET defaults with server GC (dynamic adaptation and tiered PGO on) about 1.8 s; workstation GC about 3.2 s, over budget. So the process that runs the functions must use server GC, or the budget must be re-measured under the host's configuration before phase 2.
