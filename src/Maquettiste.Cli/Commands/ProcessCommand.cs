using System.Globalization;
using System.Text;
using System.Text.Json;
using Maquettiste.Engine;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Processes;

namespace Maquettiste.Cli.Commands;

/// <summary>
/// <c>maquettiste process simulate|record|verify|export|import|sync-enum</c> (phase-3-design.md section 4.4): the process operations of
/// the editor API from the command line. A process is named by id, name or model path; writes preview unless <c>--apply</c>.
/// </summary>
/// <remarks>
/// Exit codes follow the other verbs: 0 done, 1 a failing scenario, an invalid draft or import, or a refused sync, 2 a <c>--check</c>
/// preview that would change something, 3 a file that changed since it was read, 4 an argument that names nothing (a usage error).
/// </remarks>
internal static class ProcessCommand
{
    private const string Verbs = "simulate, record, verify, export, import or sync-enum";

    // The API's bodies (every member written, nulls included), indented with \n line ends.
    private static readonly JsonSerializerOptions Output = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        NewLine = "\n",
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Runs <c>process &lt;verb&gt;</c>.</summary>
    /// <param name="context">The global context.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The exit code.</returns>
    public static async Task<int> RunAsync(GlobalContext context, CancellationToken ct)
    {
        var line = context.Line;
        var verb = line.Positionals.Count > 1 ? line.Positionals[1] : throw new UsageException($"'process' needs a verb: {Verbs}.");
        switch (verb)
        {
            case "simulate":
                line.Expect("process simulate", 3, "--inputs", "--scenario", "--from", "--format");
                break;
            case "record":
                line.Expect("process record", 4, "--inputs", "--apply", "--format");
                break;
            case "verify":
                line.Expect("process verify", Math.Max(2, line.Positionals.Count), "--format");
                break;
            case "export":
                line.Expect("process export", 3, "--format", "--out");
                break;
            case "import":
                line.Expect("process import", 3, "--domain", "--name", "--use", "--subject", "--into", "--apply", "--format");
                break;
            case "sync-enum":
                line.Expect("process sync-enum", 3, "--apply", "--check", "--format");
                break;
            default:
                throw new UsageException($"Unknown process verb '{verb}': use {Verbs}.");
        }

        var json = verb != "export" && line.Choice("--format", "text", "text", "json") == "json";
        if (verb == "export")
            line.Choice("--format", XStateProjection.Format, XStateProjection.Format);
        if (line.Has("--apply") && line.Has("--check"))
            throw new UsageException("--apply contradicts --check.");
        if (await ProjectGuard.RepoAsync(context).ConfigureAwait(false) is not { } repo)
            return Program.ExitCodes.Invalid;

        var store = new ModelStore(context.EngineOptions(repo));
        await using (store.ConfigureAwait(false))
        {
            await store.LoadAsync(ct).ConfigureAwait(false);
            var snapshot = await store.GetSnapshotAsync(ct).ConfigureAwait(false);
            return verb switch
            {
                "simulate" => await SimulateAsync(context, store, Resolve(snapshot, line.Positionals[2]), json, ct).ConfigureAwait(false),
                "record" => await RecordAsync(context, store, Resolve(snapshot, line.Positionals[2]), line.Positionals[3], json, ct).ConfigureAwait(false),
                "verify" => await VerifyAsync(context, store, snapshot, [.. line.Positionals.Skip(2)], json, ct).ConfigureAwait(false),
                "export" => await ExportAsync(context, store, Resolve(snapshot, line.Positionals[2]), ct).ConfigureAwait(false),
                "import" => await ImportAsync(context, store, snapshot, line.Positionals[2], json, ct).ConfigureAwait(false),
                _ => await SyncEnumAsync(context, store, Resolve(snapshot, line.Positionals[2]), json, ct).ConfigureAwait(false),
            };
        }
    }

    /// <summary>A process by id, name or model path; an argument that names none or several is a usage error (exit 4).</summary>
    private static Process Resolve(ModelSnapshot snapshot, string key)
    {
        var matches = ProcessApi.Resolve(snapshot, key);
        return matches.Count == 1
            ? matches[0]
            : throw new UsageException(matches.Count == 0
                ? $"no process has the id, name or path '{key}'."
                : $"'{key}' names {matches.Count} processes; pass one id: {string.Join(", ", matches.Select(m => m.Id + " (" + m.Name + ")"))}.");
    }

    private static async Task<int> SimulateAsync(GlobalContext context, ModelStore store, Process process, bool json, CancellationToken ct)
    {
        var inputs = await InputsAsync(context, ct).ConfigureAwait(false);
        if (inputs is null)
            return Program.ExitCodes.Invalid;
        var scenario = context.Line.Value("--scenario") ?? inputs.Scenario;
        if (scenario is not null)
        {
            var snapshot = await store.GetSnapshotAsync(ct).ConfigureAwait(false);
            scenario = snapshot.All<Scenario>().FirstOrDefault(s => s.Process == process.Id && (s.Id == scenario || s.Name == scenario))?.Id
                ?? throw new UsageException($"no scenario of process '{process.Name}' has the id or name '{scenario}'.");
        }

        var from = context.Line.Int("--from", 0);
        var call = await store.SimulateProcessAsync(process.Id, new SimulateRequest { Start = inputs.Start, Steps = inputs.Steps, Scenario = scenario, From = from }, ct)
            .ConfigureAwait(false);
        if (await FailedAsync(context, call).ConfigureAwait(false) is { } failed)
            return failed;
        var result = call.Value!;
        if (json)
        {
            await context.Out.WriteLineAsync(JsonSerializer.Serialize(result, Output)).ConfigureAwait(false);
        }
        else
        {
            var chart = StatechartModel.Get(process, result.ProcessHash);
            var text = new StringBuilder();
            foreach (var trace in result.Trace)
            {
                text.Append(trace.Index < 0 ? "start" : "[" + trace.Index.ToString(CultureInfo.InvariantCulture) + "] " + Describe(chart, trace.Input!))
                    .Append(trace.Accepted ? "" : " refused (" + trace.Refusal + ")").Append(" -> ").Append(Paths(chart, trace.Configuration)).Append('\n');
                foreach (var d in trace.Diagnostics)
                    text.Append("    ").Append(DiagnosticOutput.Line(d)).Append('\n');
            }

            text.Append("configuration: ").Append(Paths(chart, result.Configuration)).Append(result.Final ? " (final)" : "").Append('\n');
            foreach (var trigger in result.Enabled)
            {
                var what = trigger.Event is { } e && chart.Events.TryGetValue(e, out var ev) ? ev.Name
                    : trigger.Invoke is { } i && chart.Invokes.TryGetValue(i, out var inv) ? inv.Invoke.Name : "";
                text.Append("enabled: ").Append(trigger.Trigger).Append(what.Length > 0 ? " " + what : "")
                    .Append(trigger.Gate is { } g ? string.Create(CultureInfo.InvariantCulture, $" (gate {g.Have}/{g.Need})") : "")
                    .Append(trigger.GuardUnknown ? " (needs assume)" : "").Append('\n');
            }

            text.Append("clock: ").Append(result.Clock).Append('\n');
            await context.Out.WriteAsync(text.ToString()).ConfigureAwait(false);
        }

        return result.Diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error) ? Program.ExitCodes.Invalid : Program.ExitCodes.Success;
    }

    private static async Task<int> RecordAsync(GlobalContext context, ModelStore store, Process process, string name, bool json, CancellationToken ct)
    {
        var inputs = await InputsAsync(context, ct).ConfigureAwait(false);
        if (inputs is null)
            return Program.ExitCodes.Invalid;
        var apply = context.Line.Has("--apply");
        var call = await store.RecordScenarioAsync(process.Id, new RecordScenarioRequest { Name = name, Start = inputs.Start, Steps = inputs.Steps, Outcome = inputs.Outcome },
            !apply, ChangeSource.Cli, ct).ConfigureAwait(false);
        if (await FailedAsync(context, call).ConfigureAwait(false) is { } failed)
            return failed;
        var result = call.Value!;
        await context.Out.WriteLineAsync(json ? JsonSerializer.Serialize(result, Output) : JsonSerializer.Serialize(result.Element, Output)).ConfigureAwait(false);
        await context.Error.WriteLineAsync(apply
            ? $"maquettiste: recorded scenario {name} ({result.Id}) of {process.Name}."
            : $"maquettiste: scenario {name} of {process.Name} (preview); run with --apply to write it.").ConfigureAwait(false);
        return Program.ExitCodes.Success;
    }

    private static async Task<int> VerifyAsync(GlobalContext context, ModelStore store, ModelSnapshot snapshot, IReadOnlyList<string> keys, bool json, CancellationToken ct)
    {
        var processes = keys.Count == 0 ? [.. snapshot.All<Process>().OrderBy(p => p.Name, StringComparer.Ordinal).ThenBy(p => p.Id, StringComparer.Ordinal)]
            : keys.Select(k => Resolve(snapshot, k)).DistinctBy(p => p.Id).ToList();
        var results = new List<VerifyResult>();
        foreach (var process in processes)
            results.Add((await store.VerifyScenariosAsync(process.Id, null, ct).ConfigureAwait(false)).Value!);
        var total = results.Sum(r => r.Results.Count);
        var failed = results.Sum(r => r.Results.Count(s => !s.Passed));
        if (json)
        {
            await context.Out.WriteLineAsync(JsonSerializer.Serialize(new { processes = results, scenarios = total, failed }, Output)).ConfigureAwait(false);
        }
        else
        {
            var text = new StringBuilder();
            foreach (var (result, process) in results.Zip(processes))
            {
                foreach (var s in result.Results)
                {
                    text.Append(s.Passed ? "pass  " : "FAIL  ").Append(process.Name).Append('/').Append(s.Name)
                        .Append(string.Create(CultureInfo.InvariantCulture, $" ({s.Steps} steps)"));
                    if (s.Failure is { } f)
                        text.Append(string.Create(CultureInfo.InvariantCulture, $": {f.Rule} at {(f.Step < 0 ? "outcome" : "step " + f.Step)}: {f.Message}"));
                    text.Append('\n');
                }
            }

            text.Append(string.Create(CultureInfo.InvariantCulture, $"{total} scenarios, {total - failed} passed, {failed} failed\n"));
            await context.Out.WriteAsync(text.ToString()).ConfigureAwait(false);
        }

        return failed > 0 ? Program.ExitCodes.Invalid : Program.ExitCodes.Success;
    }

    private static async Task<int> ExportAsync(GlobalContext context, ModelStore store, Process process, CancellationToken ct)
    {
        var call = await store.ExportProcessAsync(process.Id, ct).ConfigureAwait(false);
        foreach (var d in call.Value!.Diagnostics.Where(d => d.Rule != "MQ9406"))
            await context.Error.WriteLineAsync(DiagnosticOutput.Line(d)).ConfigureAwait(false);
        await CliFiles.EmitAsync(context, call.Value.Json, ct).ConfigureAwait(false);
        return Program.ExitCodes.Success;
    }

    private static async Task<int> ImportAsync(GlobalContext context, ModelStore store, ModelSnapshot snapshot, string file, bool json, CancellationToken ct)
    {
        var line = context.Line;
        var into = line.Value("--into") is { } key ? Resolve(snapshot, key) : null;
        if (into is null && line.Value("--domain") is null)
            throw new UsageException("'process import' needs --domain <package> for a new process, or --into <process>.");
        if (await CliFiles.ReadAsync(context, file, ct).ConfigureAwait(false) is not { } text)
            return Program.ExitCodes.Invalid;
        var apply = line.Has("--apply");
        var request = new ProcessImportRequest
        {
            Config = JsonSerializer.SerializeToElement(text),
            Package = line.Value("--domain"),
            Name = line.Value("--name"),
            Use = line.Value("--use"),
            Subject = line.Value("--subject"),
            Into = into?.Id,
            ExpectedHash = into is null ? null : snapshot.GetDocument(into.Id)!.Hash,
        };
        var call = await store.ImportProcessAsync(request, !apply, ChangeSource.Cli, ct).ConfigureAwait(false);
        if (call.Value is null && await FailedAsync(context, call).ConfigureAwait(false) is { } failed)
            return failed;
        var result = call.Value!;
        if (json)
        {
            await context.Out.WriteLineAsync(JsonSerializer.Serialize(result, Output)).ConfigureAwait(false);
        }
        else
        {
            foreach (var d in result.Diagnostics)
                await context.Out.WriteLineAsync(DiagnosticOutput.Line(d)).ConfigureAwait(false);
        }

        var name = result.Document is { } doc && doc.TryGetProperty("name", out var n) ? n.GetString() : into?.Name;
        var summary = string.Create(CultureInfo.InvariantCulture, $"import {name}: {result.Created.Count} created, {result.Removed.Count} removed");
        switch (call.Status)
        {
            case ProcessCallStatus.Conflict:
                await context.Error.WriteLineAsync($"maquettiste: {summary}; {call.Detail}").ConfigureAwait(false);
                return Program.ExitCodes.Conflicts;
            case ProcessCallStatus.Invalid:
                await context.Error.WriteLineAsync($"maquettiste: {summary}; {call.Detail}").ConfigureAwait(false);
                return Program.ExitCodes.Invalid;
        }

        if (!apply && result.Diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error))
        {
            await context.Error.WriteLineAsync($"maquettiste: {summary}; the import has errors.").ConfigureAwait(false);
            return Program.ExitCodes.Invalid;
        }

        await context.Error.WriteLineAsync($"maquettiste: {summary}{(apply ? $" (written, {result.Id})." : "; run with --apply to write it.")}").ConfigureAwait(false);
        return Program.ExitCodes.Success;
    }

    private static async Task<int> SyncEnumAsync(GlobalContext context, ModelStore store, Process process, bool json, CancellationToken ct)
    {
        var apply = context.Line.Has("--apply");
        var call = await store.SyncEnumAsync(process.Id, !apply, null, ChangeSource.Cli, ct).ConfigureAwait(false);
        if (call.Value is null && await FailedAsync(context, call).ConfigureAwait(false) is { } failed)
            return failed;
        var result = call.Value!;
        if (json)
        {
            await context.Out.WriteLineAsync(JsonSerializer.Serialize(result, Output)).ConfigureAwait(false);
        }
        else
        {
            var text = new StringBuilder();
            foreach (var m in result.Added)
                text.Append("added     ").Append(m).Append('\n');
            foreach (var m in result.Removed)
                text.Append("removed   ").Append(m).Append('\n');
            if (result.Reordered)
                text.Append("reordered members follow the state order\n");
            foreach (var r in result.Refused)
                text.Append("refused   ").Append(r.Member).Append(": still used by ").Append(string.Join(", ", r.ReferencedBy)).Append('\n');
            foreach (var d in result.Diagnostics)
                text.Append(DiagnosticOutput.Line(d)).Append('\n');
            await context.Out.WriteAsync(text.ToString()).ConfigureAwait(false);
        }

        var pending = result.Added.Count + result.Removed.Count + (result.Reordered ? 1 : 0);
        var summary = string.Create(CultureInfo.InvariantCulture,
            $"sync-enum {process.Name}: {result.Added.Count} added, {result.Removed.Count} removed, {(result.Reordered ? "reordered" : "same order")}, {result.Refused.Count} refused");
        if (call.Status == ProcessCallStatus.Conflict)
        {
            await context.Error.WriteLineAsync($"maquettiste: {summary}; {call.Detail}").ConfigureAwait(false);
            return Program.ExitCodes.Conflicts;
        }

        if (call.Status == ProcessCallStatus.Invalid || (!apply && result.Refused.Count > 0))
        {
            await context.Error.WriteLineAsync($"maquettiste: {summary}; {call.Detail ?? "change the uses of the refused members first."}").ConfigureAwait(false);
            return Program.ExitCodes.Invalid;
        }

        if (!apply && context.Line.Has("--check") && pending > 0)
        {
            await context.Error.WriteLineAsync($"maquettiste: {summary}; the enum is out of sync.").ConfigureAwait(false);
            return Program.ExitCodes.Drift;
        }

        await context.Error.WriteLineAsync($"maquettiste: {summary}{(apply ? (result.Applied ? " (written)." : " (nothing to write).") : pending > 0 ? "; run with --apply to write it." : ".")}")
            .ConfigureAwait(false);
        return Program.ExitCodes.Success;
    }

    /// <summary>The inputs file (<c>--inputs</c>, <c>-</c> for standard input): an array of steps, or <c>{ start, steps, scenario, outcome }</c>.</summary>
    private static async Task<InputsFile?> InputsAsync(GlobalContext context, CancellationToken ct)
    {
        var file = context.Line.Value("--inputs");
        if (file is null)
            return context.Line.Positionals[1] == "simulate" ? new InputsFile(null, [], null, null) : throw new UsageException("--inputs <file> is required.");
        string? text;
        if (file == "-")
        {
            var stream = context.Environment.OpenStandardInput?.Invoke() ?? throw new UsageException("standard input is not available; pass a file to --inputs.");
            using var reader = new StreamReader(stream, Encoding.UTF8);
            text = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
        }
        else
        {
            text = await CliFiles.ReadAsync(context, file, ct).ConfigureAwait(false);
        }

        if (text is null)
            return null;
        try
        {
            var root = JsonDocument.Parse(text).RootElement.Clone();
            if (root.ValueKind == JsonValueKind.Array)
                return new InputsFile(null, [.. root.EnumerateArray()], null, null);
            if (root.ValueKind == JsonValueKind.Object)
            {
                JsonElement? start = root.TryGetProperty("start", out var s) ? s : null;
                IReadOnlyList<JsonElement> steps = root.TryGetProperty("steps", out var st) && st.ValueKind == JsonValueKind.Array ? [.. st.EnumerateArray()] : [];
                return new InputsFile(start, steps, root.TryGetProperty("scenario", out var sc) ? sc.GetString() : null,
                    root.TryGetProperty("outcome", out var o) ? o.GetString() : null);
            }
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException)
        {
            await context.Error.WriteLineAsync($"maquettiste: {file} cannot be read: {e.Message}").ConfigureAwait(false);
            return null;
        }

        await context.Error.WriteLineAsync($"maquettiste: {file} must hold an array of steps or {{ start, steps }}.").ConfigureAwait(false);
        return null;
    }

    /// <summary>Reports a call that failed and returns its exit code, or null when it succeeded.</summary>
    private static async Task<int?> FailedAsync<T>(GlobalContext context, ProcessCall<T> call)
    {
        switch (call.Status)
        {
            case ProcessCallStatus.Ok:
                return null;
            case ProcessCallStatus.NotFound:
            case ProcessCallStatus.BadRequest:
                throw new UsageException(call.Detail ?? "the request is not valid.");
            case ProcessCallStatus.Conflict:
                await context.Error.WriteLineAsync("maquettiste: " + call.Detail).ConfigureAwait(false);
                return Program.ExitCodes.Conflicts;
            default:
                foreach (var d in call.Diagnostics)
                    await context.Error.WriteLineAsync(DiagnosticOutput.Line(d)).ConfigureAwait(false);
                await context.Error.WriteLineAsync("maquettiste: " + call.Detail).ConfigureAwait(false);
                return Program.ExitCodes.Invalid;
        }
    }

    private static string Describe(StatechartModel chart, ScenarioStep input) => input.Input switch
    {
        StepInput.Time => "time " + input.After,
        StepInput.InvokeDone or StepInput.InvokeError => (input.Input == StepInput.InvokeDone ? "invoke-done " : "invoke-error ")
            + (input.Invoke is { } i && chart.Invokes.TryGetValue(i, out var inv) ? inv.Invoke.Name : input.Invoke),
        _ => "event " + (input.Event is { } e && chart.Events.TryGetValue(e, out var ev) ? ev.Name : input.Event),
    };

    private static string Paths(StatechartModel chart, IReadOnlyList<string> states) =>
        states.Count == 0 ? "(none)" : string.Join(", ", states.Select(s => chart.ById.TryGetValue(s, out var state) ? state.Path : s));

    private sealed record InputsFile(JsonElement? Start, IReadOnlyList<JsonElement> Steps, string? Scenario, string? Outcome);
}
