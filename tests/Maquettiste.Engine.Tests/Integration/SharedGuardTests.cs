using System.Text.Json;
using System.Text.Json.Nodes;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Loading;
using Maquettiste.Engine.Pipeline;
using Maquettiste.Engine.Processes;
using Maquettiste.Engine.Resolution;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Integration;

/// <summary>
/// Library processes (next-phase-2-design.md section 4.4), on a copy of the billing fixture: an invented library BillingChecks with a guard
/// amount_positive, an action notify and a gate two_signers, used by an invented lifecycle-free flow InvoiceFlow and an invented PaymentFlow.
/// </summary>
[Collection(IntegrationCollection.Name)]
public sealed class SharedGuardTests
{
    private const string Library = "01JRSG00000000000000000001";
    private const string Guard = "01JRSG00000000000000000002";
    private const string Action = "01JRSG00000000000000000003";
    private const string Gate = "01JRSG00000000000000000004";
    private const string Meaning = "01JRSG00000000000000000005";
    private const string Controller = "01JRSG00000000000000000006";
    private const string Auditor = "01JRSG00000000000000000007";
    private const string InvoiceFlow = "01JRSG0000000000000000AA00";
    private const string PaymentFlow = "01JRSG0000000000000000BB00";
    private const string LibraryPath = ".maquettiste/model/processes/billing-checks.json";
    private const string InvoicePath = ".maquettiste/model/processes/invoice-flow.json";
    private const string PaymentPath = ".maquettiste/model/processes/payment-flow.json";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Id(string prefix, int n) => prefix[..^2] + n.ToString("D2", System.Globalization.CultureInfo.InvariantCulture);

    private const string LibraryBody = $$"""
        {
          "kind": "process", "id": "{{Library}}", "name": "BillingChecks", "use": "library",
          "guards": [ { "id": "{{Guard}}", "name": "amount_positive", "expression": "context.amount > 0" } ],
          "actions": [ { "id": "{{Action}}", "name": "notify", "expression": "({ notified: true })" } ],
          "gates": [ { "id": "{{Gate}}", "name": "two_signers", "required": 2, "signers": ["{{Controller}}", "{{Auditor}}"],
            "meanings": [ { "id": "{{Meaning}}", "name": "approved" } ] } ]
        }
        """;

    /// <summary>A flow Open to Done on go, guarded by the library's guard, running its action on entering Done; each flow has its own context.</summary>
    private static string Flow(string id, string name, string guard = Guard, string transitionExtra = "", string extra = "") => $$"""
        {
          "kind": "process", "id": "{{id}}", "name": "{{name}}",
          "context": [ { "id": "{{Id(id, 11)}}", "name": "amount", "type": "decimal" }, { "id": "{{Id(id, 12)}}", "name": "notified", "type": "bool" } ],
          "events": [ { "id": "{{Id(id, 13)}}", "name": "go" } ],
          "states": [
            { "id": "{{Id(id, 14)}}", "name": "Open" },
            { "id": "{{Id(id, 15)}}", "name": "Done", "type": "final", "entry": ["{{Action}}"] }
          ],
          "initial": "{{Id(id, 14)}}",
          "transitions": [ { "id": "{{Id(id, 16)}}", "source": "{{Id(id, 14)}}", "trigger": "event", "event": "{{Id(id, 13)}}", "guard": "{{guard}}", "targets": ["{{Id(id, 15)}}"]{{transitionExtra}} } ]{{extra}}
        }
        """;

    private static async Task<E2ERepo> CopyAsync(string? library = LibraryBody, string? invoice = null, string? payment = null, string? pack = null, Action<E2ERepo>? more = null)
    {
        var repo = E2ERepo.Create(demo: false);
        repo.Repo.WriteFile(".maquettiste/model/actors/controller.json", $$"""{ "kind": "actor", "id": "{{Controller}}", "name": "Controller", "type": "person" }""");
        repo.Repo.WriteFile(".maquettiste/model/actors/auditor.json", $$"""{ "kind": "actor", "id": "{{Auditor}}", "name": "Auditor", "type": "person" }""");
        if (library is not null)
            repo.Repo.WriteFile(LibraryPath, library);
        repo.Repo.WriteFile(InvoicePath, invoice ?? Flow(InvoiceFlow, "InvoiceFlow", transitionExtra: $""", "gatePolicy": "{Gate}" """));
        repo.Repo.WriteFile(PaymentPath, payment ?? Flow(PaymentFlow, "PaymentFlow"));
        if (pack is not null)
        {
            var folder = Path.Combine(repo.Repo.ModelRoot, "templates", "flows");
            Directory.CreateDirectory(folder);
            await File.WriteAllTextAsync(Path.Combine(folder, "pack.json"), """
                {"name": "flows", "version": "1.0.0", "engine": ">=1.0", "units": [
                  {"id": "flow", "template": "flow.scriban", "for": "each process", "output": "src/Generated/flows/{{ process.name }}.txt"}]}
                """, Ct);
            await File.WriteAllTextAsync(Path.Combine(folder, "flow.scriban"), pack, Ct);
        }

        more?.Invoke(repo);
        await repo.RestartAsync();
        return repo;
    }

    private static async Task<IReadOnlyList<Diagnostic>> FindingsAsync(E2ERepo repo) =>
        (await repo.Store.ValidateAsync(ValidationScope.All, Ct)).Diagnostics;

    private static string Errors(IEnumerable<Diagnostic> found) =>
        string.Join("; ", found.Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => d.Rule + " " + d.JsonPointer + " " + d.Message));

    private static async Task<ResolvedModel> ResolveAsync(E2ERepo repo) =>
        await new ModelResolver(repo.Repo.Options).ResolveAsync(await repo.Store.GetSnapshotAsync(Ct), null, Ct);

    private static async Task<StepTrace> SimulateAsync(E2ERepo repo, string flow, decimal amount, string input = "go")
    {
        var context = "{ \"" + Id(flow, 11) + "\": " + amount.ToString(System.Globalization.CultureInfo.InvariantCulture) + " }";
        var call = await repo.Store.SimulateProcessAsync(flow, new SimulateRequest
        {
            Start = JsonDocument.Parse($$"""{ "context": {{context}} }""").RootElement.Clone(),
            Steps = [JsonDocument.Parse($$"""{ "input": "event", "event": "{{Id(flow, 13)}}" }""").RootElement.Clone()],
        }, Ct);
        Assert.True(call.Status == ProcessCallStatus.Ok, call.Detail);
        return call.Value!.Trace[^1];
    }

    // ---- Acceptance

    [Fact]
    public async Task An_amount_positive_guard_exists_once_and_two_processes_use_it()
    {
        // A gate needs its signers on the event, so PaymentFlow alone has none here; both share the guard and the action.
        await using var repo = await CopyAsync(invoice: Flow(InvoiceFlow, "InvoiceFlow"));
        Assert.Equal("", Errors(await FindingsAsync(repo)));

        var references = await repo.Store.GetReferencesAsync(Guard, Ct);
        Assert.Equal([Id(InvoiceFlow, 16), Id(PaymentFlow, 16)], references!.Select(r => r.FromId).Order(StringComparer.Ordinal));

        foreach (var flow in new[] { InvoiceFlow, PaymentFlow })
        {
            Assert.False((await SimulateAsync(repo, flow, 0)).Accepted);
            Assert.True((await SimulateAsync(repo, flow, 10)).Accepted);
        }

        var guard = (await ResolveAsync(repo)).ProcessLibraries.Single().Guards.Single();
        Assert.Equal([Id(InvoiceFlow, 16), Id(PaymentFlow, 16)], guard.UsedBy.Select(t => t.Id));

        var document = await repo.Store.GetElementAsync(Library, Ct);
        var removed = (JsonObject)JsonNode.Parse(document!.Json.GetRawText())!;
        removed["guards"] = new JsonArray();
        var refused = await repo.Store.SaveAsync(Library, System.Text.Encoding.UTF8.GetBytes(removed.ToJsonString()), document.Hash, ChangeSource.Editor, Ct);
        Assert.NotEqual(SaveOutcome.Saved, refused.Outcome);
    }

    // ---- Rules

    [Fact]
    public async Task A_library_holding_what_runs_is_MQ9020_at_each_place()
    {
        await using var repo = await CopyAsync(library: $$"""
            {
              "kind": "process", "id": "{{Library}}", "name": "BillingChecks", "use": "library", "subject": "{{E2ERepo.InvoiceId}}", "initial": "{{Id(Library, 41)}}",
              "context": [ { "id": "{{Id(Library, 42)}}", "name": "amount", "type": "decimal" } ],
              "events": [ { "id": "{{Id(Library, 43)}}", "name": "go" } ],
              "guards": [ { "id": "{{Guard}}", "name": "amount_positive", "condition": { "value": true } } ],
              "actions": [ { "id": "{{Action}}", "name": "notify", "raises": ["{{Id(Library, 43)}}"] } ],
              "states": [ { "id": "{{Id(Library, 41)}}", "name": "Open" } ],
              "transitions": [ { "id": "{{Id(Library, 44)}}", "source": "{{Id(Library, 41)}}", "trigger": "always", "targets": [] } ]
            }
            """, invoice: Flow(InvoiceFlow, "InvoiceFlow", guard: Id(InvoiceFlow, 17), extra: $$""", "guards": [ { "id": "{{Id(InvoiceFlow, 17)}}", "name": "own" } ]"""));

        var found = (await FindingsAsync(repo)).Where(d => d.Rule == "MQ9020").Select(d => d.JsonPointer!).Order(StringComparer.Ordinal);

        Assert.Equal(["/actions/0/raises", "/context", "/events", "/guards/0/condition", "/initial", "/states", "/subject", "/transitions"], found);
    }

    [Fact]
    public async Task A_process_without_states_that_is_not_a_library_or_that_lists_gates_is_MQ9020()
    {
        await using var repo = await CopyAsync(payment: $$"""
            { "kind": "process", "id": "{{PaymentFlow}}", "name": "PaymentFlow",
              "gates": [ { "id": "{{Id(PaymentFlow, 31)}}", "name": "sign", "signers": ["{{Controller}}"], "meanings": [ { "id": "{{Id(PaymentFlow, 32)}}", "name": "ok" } ] } ] }
            """);

        Assert.Equal(["/gates", "/states"], (await FindingsAsync(repo)).Where(d => d.Rule == "MQ9020").Select(d => d.JsonPointer!).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Gate_and_gate_policy_on_one_transition_is_MQ9021()
    {
        await using var repo = await CopyAsync(invoice: Flow(InvoiceFlow, "InvoiceFlow", transitionExtra: $$"""
            , "gatePolicy": "{{Gate}}", "gate": { "id": "{{Id(InvoiceFlow, 31)}}", "name": "own", "signers": ["{{Controller}}"], "meanings": [ { "id": "{{Id(InvoiceFlow, 32)}}", "name": "ok" } ] }
            """));

        Assert.Equal("/transitions/0/gatePolicy", Assert.Single(await FindingsAsync(repo), d => d.Rule == "MQ9021").JsonPointer);
    }

    [Fact]
    public async Task A_gate_policy_naming_an_inline_gate_is_MQ9021()
    {
        await using var repo = await CopyAsync(
            payment: Flow(PaymentFlow, "PaymentFlow", transitionExtra: $$"""
                , "gate": { "id": "{{Id(PaymentFlow, 31)}}", "name": "own", "signers": ["{{Controller}}"], "meanings": [ { "id": "{{Id(PaymentFlow, 32)}}", "name": "ok" } ] }
                """),
            invoice: Flow(InvoiceFlow, "InvoiceFlow", transitionExtra: $""", "gatePolicy": "{Id(PaymentFlow, 31)}" """));

        Assert.Contains("no library holds", Assert.Single(await FindingsAsync(repo), d => d.Rule == "MQ9021").Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_invoke_or_a_scenario_naming_a_library_is_MQ9022()
    {
        await using var repo = await CopyAsync(
            payment: $$"""
                { "kind": "process", "id": "{{PaymentFlow}}", "name": "PaymentFlow",
                  "states": [ { "id": "{{Id(PaymentFlow, 14)}}", "name": "Open", "invoke": [ { "id": "{{Id(PaymentFlow, 31)}}", "name": "run", "type": "process", "process": "{{Library}}" } ] } ] }
                """,
            more: r => r.Repo.WriteFile(".maquettiste/model/scenarios/on-library.json", $$"""
                { "kind": "scenario", "id": "{{Id(Library, 51)}}", "name": "on_library", "process": "{{Library}}", "steps": [ { "id": "{{Id(Library, 52)}}", "input": "event", "event": "{{Id(Library, 53)}}" } ] }
                """));

        var all = await FindingsAsync(repo);
        Assert.True(all.Any(d => d.Rule == "MQ9022" && d.JsonPointer == "/process"), Errors(all));
        Assert.Equal(["/process", "/states/0/invoke/0/process"], all.Where(d => d.Rule == "MQ9022").Select(d => d.JsonPointer!).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task A_guard_of_another_running_process_is_still_MQ9014_and_a_librarys_is_not()
    {
        await using var repo = await CopyAsync(
            payment: Flow(PaymentFlow, "PaymentFlow", extra: $$""", "guards": [ { "id": "{{Id(PaymentFlow, 31)}}", "name": "own", "expression": "true" } ]"""),
            invoice: Flow(InvoiceFlow, "InvoiceFlow", guard: Id(PaymentFlow, 31)));

        var found = (await FindingsAsync(repo)).Where(d => d.Rule == "MQ9014").ToList();

        Assert.Equal("/transitions/0/guard", Assert.Single(found).JsonPointer);
        Assert.Equal(Id(InvoiceFlow, 16), found[0].ElementId);
    }

    [Fact]
    public async Task A_gate_policy_counts_for_MQ9103()
    {
        // On a trigger that is not an event, and beside an inline gate on one source and event.
        var second = $$"""
            ,{ "id": "{{Id(InvoiceFlow, 33)}}", "source": "{{Id(InvoiceFlow, 14)}}", "trigger": "event", "event": "{{Id(InvoiceFlow, 13)}}", "targets": ["{{Id(InvoiceFlow, 15)}}"],
               "gate": { "id": "{{Id(InvoiceFlow, 31)}}", "name": "own", "signers": ["{{Controller}}"], "meanings": [ { "id": "{{Id(InvoiceFlow, 32)}}", "name": "ok" } ] } }
            """;
        var invoice = Flow(InvoiceFlow, "InvoiceFlow", transitionExtra: $""", "gatePolicy": "{Gate}" """).Replace("} ]\n}", "}" + second + " ]\n}", StringComparison.Ordinal);
        var always = Flow(PaymentFlow, "PaymentFlow", transitionExtra: $""", "gatePolicy": "{Gate}" """).Replace("\"trigger\": \"event\", \"event\": \"" + Id(PaymentFlow, 13) + "\"", "\"trigger\": \"always\"", StringComparison.Ordinal);
        await using var repo = await CopyAsync(invoice: invoice, payment: always);

        var found = (await FindingsAsync(repo)).Where(d => d.Rule == "MQ9103").ToList();

        Assert.Contains(found, d => d.ElementId == Id(PaymentFlow, 16) && d.JsonPointer == "/transitions/0/gatePolicy");
        Assert.Contains(found, d => d.Message.Contains("both carry a gate", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_unused_library_member_is_MQ9013_on_the_library_and_a_used_one_is_not()
    {
        await using var repo = await CopyAsync(payment: Flow(PaymentFlow, "PaymentFlow", guard: Id(PaymentFlow, 31), extra: $$""", "guards": [ { "id": "{{Id(PaymentFlow, 31)}}", "name": "own", "expression": "true" } ]""").Replace(Action, Id(PaymentFlow, 32), StringComparison.Ordinal).Replace("\"guards\": [", $$"""
            "actions": [ { "id": "{{Id(PaymentFlow, 32)}}", "name": "act" } ], "guards": [
            """, StringComparison.Ordinal),
            invoice: Flow(InvoiceFlow, "InvoiceFlow", guard: Id(InvoiceFlow, 31), extra: $$""", "guards": [ { "id": "{{Id(InvoiceFlow, 31)}}", "name": "own", "expression": "true" } ]"""));

        // The action is used through InvoiceFlow's entry; the guard and the gate by nobody.
        var found = (await FindingsAsync(repo)).Where(d => d.Rule == "MQ9013" && d.ElementId is Guard or Action or Gate).ToList();

        Assert.Equal([Guard, Gate], found.Select(d => d.ElementId!).OrderBy(id => id != Guard));
    }

    [Fact]
    public async Task A_custom_property_linking_to_a_library_guard_does_not_count_as_using_it()
    {
        await using var repo = await CopyAsync(
            payment: Flow(PaymentFlow, "PaymentFlow", guard: Id(PaymentFlow, 31), extra: $$""", "properties": { "checks": "{{Guard}}" }, "guards": [ { "id": "{{Id(PaymentFlow, 31)}}", "name": "own", "expression": "true" } ]"""),
            invoice: Flow(InvoiceFlow, "InvoiceFlow", guard: Id(InvoiceFlow, 31), extra: $$""", "guards": [ { "id": "{{Id(InvoiceFlow, 31)}}", "name": "own", "expression": "true" } ]"""),
            more: r => r.Repo.WriteFile(".maquettiste/extensions/links.json", """
                { "name": "links", "appliesTo": { "kinds": ["process"] }, "properties": { "checks": { "type": "string", "x-ref": { "kind": "guard", "optional": true } } } }
                """));

        Assert.Contains(await FindingsAsync(repo), d => d.Rule == "MQ9013" && d.ElementId == Guard);
    }

    [Fact]
    public async Task A_used_library_name_clashing_with_an_own_guard_is_MQ3001()
    {
        await using var repo = await CopyAsync(payment: Flow(PaymentFlow, "PaymentFlow", extra: $$""", "guards": [ { "id": "{{Id(PaymentFlow, 31)}}", "name": "amount_positive", "expression": "true" } ]"""));

        var clash = Assert.Single(await FindingsAsync(repo), d => d.Rule == "MQ3001");

        Assert.Equal("/transitions/0/guard", clash.JsonPointer);
        Assert.Contains("from a library", clash.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_library_is_not_judged_as_a_process_that_runs_and_its_gate_rules_and_stereotypes_still_apply()
    {
        await using var repo = await CopyAsync(library: $$"""
            { "kind": "process", "id": "{{Library}}", "name": "BillingChecks", "use": "library",
              "guards": [ { "id": "{{Guard}}", "name": "amount_positive", "expression": "context.amount > 0" } ],
              "actions": [ { "id": "{{Action}}", "name": "notify" } ],
              "gates": [ { "id": "{{Gate}}", "name": "two_signers", "required": 3, "signers": ["{{Controller}}"], "requiredActors": ["{{Auditor}}"], "stereotypes": ["no-such-mark"],
                "meanings": [ { "id": "{{Meaning}}", "name": "approved" } ] } ] }
            """, invoice: Flow(InvoiceFlow, "InvoiceFlow", transitionExtra: $""", "gatePolicy": "{Gate}" """));

        var found = (await FindingsAsync(repo)).Where(d => d.FilePath!.EndsWith("billing-checks.json", StringComparison.Ordinal) && d.Severity == DiagnosticSeverity.Error).Select(d => d.Rule).Order(StringComparer.Ordinal);

        Assert.Equal(["MQ2003", "MQ9101", "MQ9102"], found);
    }

    [Fact]
    public async Task A_library_expression_that_does_not_parse_is_MQ9501_on_the_library()
    {
        await using var repo = await CopyAsync(library: LibraryBody.Replace("context.amount > 0", "context.amount >", StringComparison.Ordinal));

        var found = Assert.Single(await FindingsAsync(repo), d => d.Rule == "MQ9501");

        Assert.EndsWith("billing-checks.json", found.FilePath, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Saving_a_library_revalidates_the_processes_that_use_its_gates()
    {
        // The event may only be raised by the controller, so a signer the library adds no longer fits the using process (MQ9105).
        var invoice = Flow(InvoiceFlow, "InvoiceFlow", transitionExtra: $""", "gatePolicy": "{Gate}" """).Replace("\"name\": \"go\"", $"\"name\": \"go\", \"actors\": [\"{Controller}\"]", StringComparison.Ordinal);
        var body = LibraryBody.Replace($"\"signers\": [\"{Controller}\", \"{Auditor}\"]", $"\"signers\": [\"{Controller}\"], \"allowRepeatSigner\": true", StringComparison.Ordinal);
        await using var repo = await CopyAsync(library: body, invoice: invoice);
        Assert.DoesNotContain(await FindingsAsync(repo), d => d.Rule == "MQ9105");

        var document = await repo.Store.GetElementAsync(Library, Ct);
        var node = (JsonObject)JsonNode.Parse(document!.Json.GetRawText())!;
        node["gates"]![0]!["signers"] = new JsonArray(Controller, Auditor);
        var saved = await repo.Store.SaveAsync(Library, System.Text.Encoding.UTF8.GetBytes(node.ToJsonString()), document.Hash, ChangeSource.Editor, Ct);

        Assert.Contains(saved.Diagnostics, d => d.Rule == "MQ9105");
    }

    // ---- Running

    [Fact]
    public async Task A_library_guard_runs_with_the_using_process_context_and_its_action_changes_that_context()
    {
        await using var repo = await CopyAsync(invoice: Flow(InvoiceFlow, "InvoiceFlow"));

        var step = await SimulateAsync(repo, PaymentFlow, 10);

        Assert.True(step.Accepted);
        Assert.Equal(JsonValueKind.True, step.Changed[Id(PaymentFlow, 12)].ValueKind);
    }

    [Fact]
    public async Task A_gate_policy_counts_signatures_per_transition()
    {
        await using var repo = await CopyAsync(payment: Flow(PaymentFlow, "PaymentFlow", transitionExtra: $""", "gatePolicy": "{Gate}" """));
        Assert.Equal("", Errors(await FindingsAsync(repo)));

        async Task<(bool, bool)> SignAsync(string flow, params string[] signers)
        {
            var steps = signers.Select(s => JsonDocument.Parse($$"""{ "input": "event", "event": "{{Id(flow, 13)}}", "actor": "{{s}}", "signer": "{{s}}", "meaning": "{{Meaning}}" }""").RootElement.Clone()).ToList();
            var call = await repo.Store.SimulateProcessAsync(flow, new SimulateRequest
            {
                Start = JsonDocument.Parse($$"""{ "context": { "{{Id(flow, 11)}}": 10 } }""").RootElement.Clone(),
                Steps = steps,
            }, Ct);
            Assert.True(call.Status == ProcessCallStatus.Ok, call.Detail);
            return (call.Value!.Trace[^1].Final, call.Value.Trace[^1].Accepted);
        }

        // The controller's signature in one flow does not count in the other, so each needs its own two.
        Assert.False((await SignAsync(InvoiceFlow, Controller)).Item1);
        Assert.False((await SignAsync(PaymentFlow, Controller)).Item1);
        Assert.True((await SignAsync(InvoiceFlow, Controller, Auditor)).Item1);
        Assert.True((await SignAsync(PaymentFlow, Controller, Auditor)).Item1);
    }

    [Fact]
    public async Task A_library_edit_changes_what_the_chart_runs_though_the_process_file_did_not_change()
    {
        await using var repo = await CopyAsync(invoice: Flow(InvoiceFlow, "InvoiceFlow"));
        Assert.True((await SimulateAsync(repo, PaymentFlow, 10)).Accepted);

        await repo.EditAsync(Library, library => library["guards"]![0]!["expression"] = "context.amount > 100");

        Assert.False((await SimulateAsync(repo, PaymentFlow, 10)).Accepted);
        Assert.True((await SimulateAsync(repo, PaymentFlow, 101)).Accepted);
    }

    [Fact]
    public async Task Simulate_record_export_and_enum_sync_refuse_a_library()
    {
        await using var repo = await CopyAsync();

        var simulate = await repo.Store.SimulateProcessAsync(Library, new SimulateRequest(), Ct);
        var record = await repo.Store.RecordScenarioAsync(Library, new RecordScenarioRequest { Name = "x", Steps = [] }, dryRun: true, ChangeSource.Editor, Ct);
        var export = await repo.Store.ExportProcessAsync(Library, Ct);
        var sync = await repo.Store.SyncEnumAsync(Library, dryRun: true, null, ChangeSource.Editor, Ct);

        Assert.All(new[] { simulate.Status, record.Status, export.Status }, s => Assert.Equal(ProcessCallStatus.BadRequest, s));
        Assert.Contains("is a library", simulate.Detail, StringComparison.Ordinal);
        Assert.Equal("MQ9019", Assert.Single(sync.Value!.Diagnostics).Rule);
    }

    [Fact]
    public async Task Export_writes_a_library_guard_by_name_like_a_local_one()
    {
        await using var repo = await CopyAsync(invoice: Flow(InvoiceFlow, "InvoiceFlow"));

        var export = await repo.Store.ExportProcessAsync(PaymentFlow, Ct);

        Assert.Contains("amount_positive", export.Value!.Json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_step_meaning_of_a_gate_policy_replays()
    {
        await using var repo = await CopyAsync(more: r => r.Repo.WriteFile(".maquettiste/model/scenarios/invoice-signed.json", $$"""
            { "kind": "scenario", "id": "{{Id(Library, 61)}}", "name": "signed", "process": "{{InvoiceFlow}}", "outcome": "final",
              "start": { "context": { "{{Id(InvoiceFlow, 11)}}": 10 } },
              "steps": [
                { "id": "{{Id(Library, 62)}}", "input": "event", "event": "{{Id(InvoiceFlow, 13)}}", "actor": "{{Controller}}", "signer": "c1", "meaning": "{{Meaning}}" },
                { "id": "{{Id(Library, 63)}}", "input": "event", "event": "{{Id(InvoiceFlow, 13)}}", "actor": "{{Auditor}}", "signer": "a1", "meaning": "{{Meaning}}" }
              ] }
            """));

        Assert.Equal("", Errors(await FindingsAsync(repo)));
        var verified = await repo.Store.VerifyScenariosAsync(InvoiceFlow, null, Ct);
        Assert.True(Assert.Single(verified.Value!.Results).Passed);
    }

    // ---- The resolved model

    [Fact]
    public async Task Libraries_resolve_apart_from_processes_and_each_using_process_lists_what_it_names()
    {
        await using var repo = await CopyAsync();

        var model = await ResolveAsync(repo);

        Assert.Equal(["InvoiceFlow", "PaymentFlow"], model.Processes.Select(p => p.Name));
        var library = Assert.Single(model.ProcessLibraries);
        Assert.Equal("library", library.Use);
        var invoice = model.Processes.Single(p => p.Name == "InvoiceFlow");
        var payment = model.Processes.Single(p => p.Name == "PaymentFlow");
        Assert.Same(library, Assert.Single(invoice.Libraries));
        Assert.Same(library.Guards.Single(), invoice.Guards.Single());
        Assert.Same(library.Actions.Single(), payment.Actions.Single());
        Assert.Same(library.Guards.Single(), invoice.Transitions.Single().Guard);
        Assert.Same(library.Gates.Single(), invoice.Gates.Single());
        Assert.Empty(payment.Gates);
    }

    [Fact]
    public async Task A_library_gate_has_no_transition_and_lists_its_users_across_processes()
    {
        await using var repo = await CopyAsync(payment: Flow(PaymentFlow, "PaymentFlow", transitionExtra: $""", "gatePolicy": "{Gate}" """));

        var model = await ResolveAsync(repo);

        var gate = model.ProcessLibraries.Single().Gates.Single();
        Assert.Null(gate.Transition);
        Assert.Equal([Id(InvoiceFlow, 16), Id(PaymentFlow, 16)], gate.UsedBy.Select(t => t.Id));
        var action = model.ProcessLibraries.Single().Actions.Single();
        Assert.Equal([Id(InvoiceFlow, 15), Id(PaymentFlow, 15)], action.UsedBy.Select(u => u.Id));
    }

    [Fact]
    public async Task A_page_of_a_process_renders_again_when_its_library_changes_and_each_process_is_all_that_runs()
    {
        await using var repo = await CopyAsync(pack: "{{ for t in process.transitions }}{{ t.guard.name }}={{ t.guard.expression }}\n{{ end }}");
        await repo.ApplyAsync();
        Assert.Equal("amount_positive=context.amount > 0\n", repo.Repo.ReadFile("src/Generated/flows/PaymentFlow.txt"));
        Assert.False(repo.Repo.Exists("src/Generated/flows/BillingChecks.txt"));

        await repo.EditAsync(Library, library => library["guards"]![0]!["expression"] = "context.amount > 100");
        await repo.ApplyAsync();

        Assert.Equal("amount_positive=context.amount > 100\n", repo.Repo.ReadFile("src/Generated/flows/PaymentFlow.txt"));
        Assert.Equal("amount_positive=context.amount > 100\n", repo.Repo.ReadFile("src/Generated/flows/InvoiceFlow.txt"));
    }

    [Fact]
    public async Task A_library_row_says_library_and_its_gate_has_a_name_form()
    {
        await using var repo = await CopyAsync();

        var index = await repo.Store.GetSnapshotAsync(Ct);
        Assert.Equal("library", index.Summaries().Single(s => s.Id == Library).Use);

        // The form of a library's gate, on a new process in a batch.
        const string third = "01JRSG0000000000000000CC00";
        var body = Flow(third, "ThirdFlow", transitionExtra: ", \"gatePolicy\": \"@gate:BillingChecks.two_signers\" ").Replace(Guard, "@guard:BillingChecks.amount_positive", StringComparison.Ordinal);
        var parsed = repo.Store.ParseBatch(System.Text.Encoding.UTF8.GetBytes($$"""{ "operations": [ { "op": "create", "element": {{body}} } ] }"""));
        Assert.Empty(parsed.Diagnostics);
        var applied = await repo.Store.ApplyBatchAsync(parsed.Batch!, ChangeSource.Editor, Ct);
        Assert.True(applied.Outcome == SaveOutcome.Saved, string.Join("; ", applied.Items.SelectMany(i => i.Diagnostics).Select(d => d.Rule + " " + d.JsonPointer + " " + d.Message)));
        var written = (await repo.Store.GetElementAsync(third, Ct))!.Json.GetProperty("transitions")[0];
        Assert.Equal(Gate, written.GetProperty("gatePolicy").GetString());
        Assert.Equal(Guard, written.GetProperty("guard").GetString());
    }

    [Fact]
    public async Task A_library_written_back_has_no_states_key()
    {
        await using var repo = await CopyAsync();

        await repo.EditAsync(Library, _ => { });

        var written = JsonNode.Parse(repo.Repo.ReadFile(LibraryPath))!.AsObject();
        Assert.False(written.ContainsKey("states"));
        Assert.Equal("library", (string?)written["use"]);
    }

    [Fact]
    public async Task A_model_with_no_library_uses_the_process_object_itself()
    {
        await using var repo = await CopyAsync(library: null, invoice: Flow(InvoiceFlow, "InvoiceFlow", guard: Id(InvoiceFlow, 31), extra: $$""", "guards": [ { "id": "{{Id(InvoiceFlow, 31)}}", "name": "own" } ]""").Replace(Action, Id(InvoiceFlow, 32), StringComparison.Ordinal).Replace("\"guards\": [", $$"""
            "actions": [ { "id": "{{Id(InvoiceFlow, 32)}}", "name": "act" } ], "guards": [
            """, StringComparison.Ordinal));
        var snapshot = await repo.Store.GetSnapshotAsync(Ct);
        var process = snapshot.Get<Maquettiste.Engine.Model.Process>(InvoiceFlow)!;

        Assert.Same(process, ProcessLibraries.Effective(snapshot, process));
        Assert.Empty(ProcessLibraries.UsesOf(snapshot, process));
    }
}
