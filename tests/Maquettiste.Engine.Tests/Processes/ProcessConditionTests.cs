using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Expressions;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Processes;
using Maquettiste.Engine.Resolution;
using Maquettiste.Testing;
using static Maquettiste.Engine.Tests.Expressions.ExpressionRuleTests;

namespace Maquettiste.Engine.Tests.Processes;

/// <summary>
/// Process guards written in the expression language (next-phase-3-design.md section 3.1, "Process guards", task H8), over the bindings
/// fixture with an invented process InvoiceReview: each guard's condition compiles into the process's prepared script after the
/// runtime, and simulation, scenario replay and <c>verify_scenarios</c> run it through the interpreter. Only true passes, so a null
/// operand fails a guard; decimals compare exactly; <c>now()</c> is the simulation's clock; and the compiled forms re-key on a rename.
/// </summary>
public sealed class ProcessConditionTests
{
    private const string ProcessId = "01K9PCD0000000000000000P01";
    private const string IssuedOn = "01K9PCD0000000000000000A01";
    private const string DueOn = "01K9PCD0000000000000000A02";
    private const string Amount = "01K9PCD0000000000000000A03";
    private const string Limit = "01K9PCD0000000000000000A04";
    private const string Status = "01K9PCD0000000000000000A05";
    private const string Submit = "01K9PCD0000000000000000E01";
    private const string Check = "01K9PCD0000000000000000E02";
    private const string Close = "01K9PCD0000000000000000E03";
    private const string Escalate = "01K9PCD0000000000000000E04";
    private const string Expire = "01K9PCD0000000000000000E05";
    private const string DueAfterIssue = "01K9PCD0000000000000000G01";
    private const string Exact = "01K9PCD0000000000000000G02";
    private const string Closable = "01K9PCD0000000000000000G03";
    private const string Over = "01K9PCD0000000000000000G04";
    private const string Expired = "01K9PCD0000000000000000G05";
    private const string Open = "01K9PCD0000000000000000S01";
    private const string Submitted = "01K9PCD0000000000000000S02";
    private const string EnumId = "01K9PCD0000000000000000N01";
    private const string Closed = "01K9PCD0000000000000000N03";
    private const string ProcessPath = ".maquettiste/model/processes/invoice-review.json";
    private const string EnumPath = ".maquettiste/model/enums/review-status.json";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Path(string id) => $$"""{ "path": ["{{id}}"] }""";

    private static string Dec(string text) => $$"""{ "value": "{{text}}", "type": "decimal" }""";

    /// <summary>
    /// The bindings fixture with the enum ReviewStatus (Open, Closed) and the process InvoiceReview: from open, submit needs
    /// <c>issuedOn &lt;= dueOn</c>, check needs <c>amount + 0.2 = 0.3</c>, close needs <c>status = ReviewStatus.Closed</c>, escalate
    /// needs <c>amount &gt; limit</c> and expire needs <c>now() &gt;= datetimeoffset '2026-06-01T00:00Z'</c>.
    /// </summary>
    private static TempRepo ReviewRepo()
    {
        var repo = Repo();
        repo.WriteFile(EnumPath, $$"""
            {
              "kind": "enum",
              "id": "{{EnumId}}",
              "name": "ReviewStatus",
              "members": [ { "id": "01K9PCD0000000000000000N02", "name": "Open" }, { "id": "{{Closed}}", "name": "Closed" } ]
            }
            """);
        var transitions = new[] { (Submit, DueAfterIssue), (Check, Exact), (Close, Closable), (Escalate, Over), (Expire, Expired) }
            .Select((t, i) => $$"""{ "id": "01K9PCD0000000000000000T0{{i + 1}}", "source": "{{Open}}", "event": "{{t.Item1}}", "guard": "{{t.Item2}}", "targets": ["{{Submitted}}"] }""");
        repo.WriteFile(ProcessPath, $$"""
            {
              "kind": "process",
              "id": "{{ProcessId}}",
              "name": "InvoiceReview",
              "context": [
                { "id": "{{IssuedOn}}", "name": "issuedOn", "type": "date" },
                { "id": "{{DueOn}}", "name": "dueOn", "type": "date" },
                { "id": "{{Amount}}", "name": "amount", "type": "decimal" },
                { "id": "{{Limit}}", "name": "limit", "type": "decimal" },
                { "id": "{{Status}}", "name": "status", "type": { "ref": "{{EnumId}}" } }
              ],
              "events": [
                { "id": "{{Submit}}", "name": "submit" }, { "id": "{{Check}}", "name": "check" }, { "id": "{{Close}}", "name": "close" },
                { "id": "{{Escalate}}", "name": "escalate" }, { "id": "{{Expire}}", "name": "expire" }
              ],
              "guards": [
                { "id": "{{DueAfterIssue}}", "name": "dueAfterIssue", "condition": { "op": "le", "args": [{{Path(IssuedOn)}}, {{Path(DueOn)}}] } },
                { "id": "{{Exact}}", "name": "exact", "condition": { "op": "eq", "args": [{ "op": "add", "args": [{{Path(Amount)}}, {{Dec("0.2")}}] }, {{Dec("0.3")}}] } },
                { "id": "{{Closable}}", "name": "closable", "condition": { "op": "eq", "args": [{{Path(Status)}}, { "ref": "{{Closed}}" }] } },
                { "id": "{{Over}}", "name": "over", "condition": { "op": "gt", "args": [{{Path(Amount)}}, {{Path(Limit)}}] } },
                { "id": "{{Expired}}", "name": "expired",
                  "condition": { "op": "ge", "args": [{ "now": true }, { "value": "2026-06-01T00:00Z", "type": "datetimeoffset" }] } }
              ],
              "states": [ { "id": "{{Open}}", "name": "open" }, { "id": "{{Submitted}}", "name": "submitted" } ],
              "transitions": [ {{string.Join(", ", transitions)}} ]
            }
            """);
        return repo;
    }

    /// <summary>Simulates one event from a start context (attribute id to JSON value) and clock; the event's trace.</summary>
    private static async Task<StepTrace> SimulateAsync(ModelStore store, string @event, string context, string at = "2026-01-01T00:00:00Z")
    {
        var request = new SimulateRequest
        {
            Start = JsonDocument.Parse($$"""{ "context": {{context}}, "at": "{{at}}" }""").RootElement.Clone(),
            Steps = [JsonDocument.Parse($$"""{ "input": "event", "event": "{{@event}}" }""").RootElement.Clone()],
        };
        var call = await store.SimulateProcessAsync(ProcessId, request, Ct);
        Assert.True(call.Status == ProcessCallStatus.Ok, call.Detail);
        return call.Value!.Trace[^1];
    }

    [Fact]
    public async Task The_conditions_validate_and_resolve_to_the_JavaScript_simulation_runs()
    {
        using var repo = ReviewRepo();
        await using var store = new ModelStore(repo.Options);
        var report = await store.ValidateAsync(ValidationScope.All, Ct);
        Assert.DoesNotContain(report.Diagnostics, d => d.Severity == DiagnosticSeverity.Error || d.Rule.StartsWith("MQ31", StringComparison.Ordinal));

        var snapshot = await store.GetSnapshotAsync(Ct);
        var model = await new ModelResolver(repo.Options).ResolveAsync(snapshot, null, Ct);
        var process = model.Processes.Single(p => p.Id == ProcessId);
        var guard = process.Guards.Single(g => g.Id == DueAfterIssue);
        Assert.False(guard.IsStub);
        Assert.Null(guard.Expression);
        var condition = Assert.IsType<RExpression>(guard.Condition);
        Assert.Equal(("issuedOn <= dueOn", "bool", true), (condition.Text, condition.Type.Kind, condition.Nullable));
        Assert.Same(process.Context.Single(a => a.Id == IssuedOn), condition.Args[0].Target);
        Assert.Equal("(row) => mq.holds(mq.le(mq.date(mq.at(row, \"issuedOn\")), mq.date(mq.at(row, \"dueOn\"))))", condition.JavaScript);
        Assert.Equal("status = ReviewStatus.Closed", process.Guards.Single(g => g.Id == Closable).Condition!.Text);

        // One lowering: the prepared script holds the resolved condition's JavaScript after the runtime.
        var compiled = ProcessExpressions.Get(snapshot, snapshot.Get<Process>(ProcessId)!);
        Assert.Equal([DueAfterIssue, Exact, Closable, Over, Expired], compiled.Conditions);
        Assert.StartsWith(ExprRuntime.Source, compiled.Script, StringComparison.Ordinal);
        Assert.Contains(condition.JavaScript!, compiled.Script, StringComparison.Ordinal);

        // The editor's record carries the condition.
        var record = (ProcessRecord)ResolvedRecords.Entries(model, "processes", null).Single(e => e.Id == ProcessId).Project();
        var guardRecord = record.Guards.Single(g => g.Id == DueAfterIssue);
        Assert.Equal(("issuedOn <= dueOn", "bool", false), (guardRecord.Condition!.Text, guardRecord.Condition.Type, guardRecord.IsStub));
    }

    [Fact]
    public async Task A_tree_guard_with_a_null_operand_fails()
    {
        using var repo = ReviewRepo();
        await using var store = new ModelStore(repo.Options);

        // JavaScript reads null as 0 in an ordering; the core answers unknown, and only true passes a guard.
        var refused = await SimulateAsync(store, Submit, $$"""{ "{{IssuedOn}}": "2026-01-01", "{{DueOn}}": null }""");
        Assert.False(refused.Accepted);
        var evaluation = Assert.Single(refused.Guards);
        Assert.Equal((DueAfterIssue, (bool?)false, GuardSource.Expression), (evaluation.Guard, evaluation.Result, evaluation.Source));
        Assert.Empty(refused.Diagnostics);

        var missing = await SimulateAsync(store, Submit, $$"""{ "{{IssuedOn}}": "2026-01-01" }""");
        Assert.False(missing.Accepted);

        var accepted = await SimulateAsync(store, Submit, $$"""{ "{{IssuedOn}}": "2026-01-01", "{{DueOn}}": "2026-02-01" }""");
        Assert.True(accepted.Accepted);
        Assert.Equal([Submitted], accepted.Configuration);
    }

    [Fact]
    public async Task Decimals_compare_exactly()
    {
        using var repo = ReviewRepo();
        await using var store = new ModelStore(repo.Options);

        // 0.1 + 0.2 is 0.3, which it is not in binary doubles.
        Assert.True((await SimulateAsync(store, Check, $$"""{ "{{Amount}}": 0.1 }""")).Accepted);
        Assert.False((await SimulateAsync(store, Check, $$"""{ "{{Amount}}": 0.11 }""")).Accepted);

        // Two decimals a double cannot tell apart.
        Assert.True((await SimulateAsync(store, Escalate, $$"""{ "{{Amount}}": 12345678901234567.01, "{{Limit}}": 12345678901234567.00 }""")).Accepted);
        Assert.False((await SimulateAsync(store, Escalate, $$"""{ "{{Amount}}": 12345678901234567.00, "{{Limit}}": 12345678901234567.00 }""")).Accepted);
    }

    [Fact]
    public async Task Now_is_the_simulations_clock_in_simulation_and_scenario_verification()
    {
        using var repo = ReviewRepo();
        repo.WriteFile(".maquettiste/model/scenarios/invoice-review/expires.json", $$"""
            {
              "kind": "scenario",
              "id": "01K9PCD0000000000000000C01",
              "name": "Expires",
              "process": "{{ProcessId}}",
              "start": { "at": "2026-07-01T00:00:00Z" },
              "steps": [ { "id": "01K9PCD0000000000000000C02", "event": "{{Expire}}", "expect": { "accepted": true, "states": ["{{Submitted}}"] } } ]
            }
            """);
        await using var store = new ModelStore(repo.Options);

        Assert.False((await SimulateAsync(store, Expire, "{}", at: "2026-05-31T23:59:59Z")).Accepted);
        Assert.True((await SimulateAsync(store, Expire, "{}", at: "2026-06-01T00:00:00Z")).Accepted);

        var verified = await store.VerifyScenariosAsync(ProcessId, null, Ct);
        Assert.True(verified.Status == ProcessCallStatus.Ok, verified.Detail);
        var result = Assert.Single(verified.Value!.Results);
        Assert.True(result.Passed, result.Failure?.Message);
        var report = await store.ValidateAsync(ValidationScope.All, Ct);
        Assert.DoesNotContain(report.Diagnostics, d => d.Rule.StartsWith("MQ93", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_cache_re_keys_on_a_rename()
    {
        using var repo = ReviewRepo();
        await using var store = new ModelStore(repo.Options);
        var before = await store.GetSnapshotAsync(Ct);
        var first = ProcessExpressions.Get(before, before.Get<Process>(ProcessId)!);
        Assert.Same(first, ProcessExpressions.Get(before, before.Get<Process>(ProcessId)!));
        Assert.True(ProcessExpressions.IsCached(before, before.Get<Process>(ProcessId)!));
        Assert.Contains("\"amount\"", first.Script, StringComparison.Ordinal);

        // A context attribute renamed: the trees hold its id, the JavaScript its name.
        var renamed = await SaveAsync(store, ProcessId, ProcessPath, repo, p => p["context"]![2]!["name"] = "total");
        var process = renamed.Get<Process>(ProcessId)!;
        Assert.False(ProcessExpressions.IsCached(renamed, process));
        var second = ProcessExpressions.Get(renamed, process);
        Assert.NotSame(first, second);
        Assert.Contains("\"total\"", second.Script, StringComparison.Ordinal);
        Assert.DoesNotContain("\"amount\"", second.Script, StringComparison.Ordinal);
        Assert.True((await SimulateAsync(store, Check, $$"""{ "{{Amount}}": 0.1 }""")).Accepted);

        // An enum member renamed in its own file: the process file is unchanged, the literal its condition compares with is not.
        Assert.False((await SimulateAsync(store, Close, $$"""{ "{{Status}}": "Done" }""")).Accepted);
        var member = await SaveAsync(store, EnumId, EnumPath, repo, e => e["members"]![1]!["name"] = "Done");
        Assert.Same(process, member.Get<Process>(ProcessId));
        Assert.False(ProcessExpressions.IsCached(member, process));
        var third = ProcessExpressions.Get(member, member.Get<Process>(ProcessId)!);
        Assert.NotSame(second, third);
        Assert.Contains("\"Done\"", third.Script, StringComparison.Ordinal);
        Assert.True((await SimulateAsync(store, Close, $$"""{ "{{Status}}": "Done" }""")).Accepted);
        Assert.False((await SimulateAsync(store, Close, $$"""{ "{{Status}}": "Closed" }""")).Accepted);
    }

    [Fact]
    public async Task A_guard_with_neither_form_stays_a_stub_and_a_JavaScript_guard_keeps_its_rules()
    {
        using var repo = ReviewRepo();
        Edit(repo, ProcessPath, p =>
        {
            var guards = p["guards"]!.AsArray();
            guards[0]!.AsObject().Remove("condition");
            guards[1]!.AsObject().Remove("condition");
            guards[1]!["expression"] = "context.amount + 0.2 === 0.3";
        });
        await using var store = new ModelStore(repo.Options);
        var snapshot = await store.GetSnapshotAsync(Ct);
        var compiled = ProcessExpressions.Get(snapshot, snapshot.Get<Process>(ProcessId)!);
        Assert.Equal([Exact], compiled.Compiled.Except(compiled.Conditions));

        // The stub needs an assume value (MQ9306); JavaScript reads 0.1 + 0.2 as a double.
        var stub = await SimulateAsync(store, Submit, "{}");
        Assert.Equal(GuardSource.Missing, Assert.Single(stub.Guards).Source);
        Assert.False((await SimulateAsync(store, Check, $$"""{ "{{Amount}}": 0.1 }""")).Accepted);
    }

    /// <summary>Saves an edit of one element through the store; the new snapshot.</summary>
    private static async Task<ModelSnapshot> SaveAsync(ModelStore store, string id, string path, TempRepo repo, Action<JsonObject> edit)
    {
        var current = (await store.GetElementAsync(id, Ct))!;
        var node = JsonNode.Parse(repo.ReadFile(path))!.AsObject();
        edit(node);
        var saved = await store.SaveAsync(id, Encoding.UTF8.GetBytes(node.ToJsonString()), current.Hash, ChangeSource.Editor, Ct);
        Assert.True(saved.Outcome == SaveOutcome.Saved, string.Join("; ", saved.Diagnostics.Select(d => d.Rule + " " + d.Message)));
        return await store.GetSnapshotAsync(Ct);
    }
}
