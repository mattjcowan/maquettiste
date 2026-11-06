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
/// operand fails a guard; decimals compare exactly; <c>now()</c> is the simulation's clock; and the compiled forms re-key on a rename. A
/// second invented process, Settlement, covers 64-bit integers, payloads, value objects, JavaScript beside conditions and a context
/// attribute named event, which no condition may read.
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

    /// <summary>
    /// Simulates one event, with its payload (attribute id to JSON value) when given, from a start context and clock; the event's trace.
    /// </summary>
    private static async Task<StepTrace> SimulateAsync(ModelStore store, string @event, string context, string at = "2026-01-01T00:00:00Z", string? payload = null,
        string process = ProcessId)
    {
        var step = payload is null
            ? $$"""{ "input": "event", "event": "{{@event}}" }"""
            : $$"""{ "input": "event", "event": "{{@event}}", "payload": {{payload}} }""";
        var request = new SimulateRequest
        {
            Start = JsonDocument.Parse($$"""{ "context": {{context}}, "at": "{{at}}" }""").RootElement.Clone(),
            Steps = [JsonDocument.Parse(step).RootElement.Clone()],
        };
        var call = await store.SimulateProcessAsync(process, request, Ct);
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

        // One lowering: the process's script holds the resolved condition's JavaScript, and loads after the runtime every process shares.
        var compiled = ProcessExpressions.Get(snapshot, snapshot.Get<Process>(ProcessId)!);
        Assert.Equal([DueAfterIssue, Exact, Closable, Over, Expired], compiled.Conditions);
        Assert.Equal(2, compiled.Scripts.Count);
        Assert.Same(ExprRuntime.Prepared, compiled.Scripts[0]);
        Assert.DoesNotContain("const mq", compiled.Script, StringComparison.Ordinal);
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

    [Fact]
    public async Task A_condition_that_does_not_type_resolves_a_stub_as_simulation_treats_it()
    {
        using var repo = ReviewRepo();
        Edit(repo, ProcessPath, p => p["guards"]![0]!["condition"] = JsonNode.Parse($$"""{ "op": "le", "args": [{{Path(IssuedOn)}}, {{Path(Amount)}}] }"""));
        await using var store = new ModelStore(repo.Options);
        Assert.Contains((await store.ValidateAsync(ValidationScope.All, Ct)).Diagnostics, d => d.Rule == "MQ3103" && d.JsonPointer == "/guards/0/condition");

        // Nothing runs: the record says so, and simulation asks for an assume value (MQ9306), as for a guard with neither form.
        var model = await new ModelResolver(repo.Options).ResolveAsync(await store.GetSnapshotAsync(Ct), null, Ct);
        var guard = model.Processes.Single(p => p.Id == ProcessId).Guards.Single(g => g.Id == DueAfterIssue);
        Assert.Null(guard.Expression);
        Assert.Null(guard.Condition);
        Assert.True(guard.IsStub);
        var record = (ProcessRecord)ResolvedRecords.Entries(model, "processes", null).Single(e => e.Id == ProcessId).Project();
        Assert.True(record.Guards.Single(g => g.Id == DueAfterIssue).IsStub);
        var trace = await SimulateAsync(store, Submit, $$"""{ "{{IssuedOn}}": "2026-01-01", "{{Amount}}": 1 }""");
        Assert.Equal(GuardSource.Missing, Assert.Single(trace.Guards).Source);
        Assert.Contains(trace.Diagnostics, d => d.Rule == "MQ9306");
    }

    [Fact]
    public async Task A_sandbox_that_fails_to_load_is_the_evaluations_MQ9503_not_an_exception()
    {
        // Twenty statements are enough for the sandbox's own setup but not for loading the runtime: the session opens, and each
        // evaluation reports the load.
        using var repo = ReviewRepo();
        await using var store = new ModelStore(repo.Options);
        var snapshot = await store.GetSnapshotAsync(Ct);
        var compiled = ProcessExpressions.Get(snapshot, snapshot.Get<Process>(ProcessId)!);
        using var session = compiled.Open(1, Ct, ProcessExpressions.Limits with { ScriptStatements = 20 });
        using var context = JsonDocument.Parse($$"""{ "{{IssuedOn}}": "2026-01-01", "{{DueOn}}": "2026-02-01" }""");
        using var @event = JsonDocument.Parse("{}");
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var result = session.EvaluateCondition(DueAfterIssue, "Guard 'dueAfterIssue'", context.RootElement, @event.RootElement, "2026-01-01T00:00:00Z", "seed", Ct);
            Assert.Null(result.Value);
            Assert.Equal("MQ9503", result.Diagnostic?.Rule);
            Assert.Contains("failed while loading", result.Diagnostic!.Message, StringComparison.Ordinal);
        }
    }

    private const string Settlement = "01K9PCX0000000000000000P01";
    private const string EventContext = "01K9PCX0000000000000000A01";
    private const string Due = "01K9PCX0000000000000000A02";
    private const string Big = "01K9PCX0000000000000000A03";
    private const string Ceiling = "01K9PCX0000000000000000A04";
    private const string Total = "01K9PCX0000000000000000A05";
    private const string Paid = "01K9PCX0000000000000000A06";
    private const string Go = "01K9PCX0000000000000000E01";
    private const string Spin = "01K9PCX0000000000000000E02";
    private const string Compare = "01K9PCX0000000000000000E03";
    private const string Pay = "01K9PCX0000000000000000E04";
    private const string Nested = "01K9PCX0000000000000000E05";
    private const string Stall = "01K9PCX0000000000000000E06";
    private const string ReadsEvent = "01K9PCX0000000000000000G01";
    private const string Owed = "01K9PCX0000000000000000G02";
    private const string BigOverCeiling = "01K9PCX0000000000000000G03";
    private const string PaidEnough = "01K9PCX0000000000000000G04";
    private const string TotalPositive = "01K9PCX0000000000000000G05";
    private const string Runaway = "01K9PCX0000000000000000G06";
    private const string Loop = "01K9PCX0000000000000000X01";
    private const string Pending = "01K9PCX0000000000000000S01";
    private const string Settled = "01K9PCX0000000000000000S02";
    private const string Money = "01K9PCX0000000000000000V01";
    private const string MoneyAmount = "01K9PCX0000000000000000V02";

    /// <summary>
    /// The bindings fixture with the value object Money and the process Settlement, whose context holds an attribute named event: from
    /// pending, go needs <c>event = 'go'</c> (that attribute), spin needs <c>due &gt; 0</c> and runs a JavaScript action that never ends,
    /// compare needs <c>big &gt; ceiling</c> (two int64s), pay needs its payload's <c>event.paid &gt; 0.5</c>, nested needs
    /// <c>total.amount &gt; 0</c> (a Money) and stall has a JavaScript guard that never ends.
    /// </summary>
    private static TempRepo SettlementRepo()
    {
        var repo = Repo();
        repo.WriteFile(".maquettiste/model/value-objects/money.json", $$"""
            {
              "kind": "value-object",
              "id": "{{Money}}",
              "name": "Money",
              "attributes": [
                { "id": "{{MoneyAmount}}", "name": "amount", "type": "decimal", "required": true },
                { "id": "01K9PCX0000000000000000V03", "name": "currency", "type": "string" }
              ]
            }
            """);
        var loop = $$""", "actions": ["{{Loop}}"]""";
        var transitions = new[] { (Go, ReadsEvent), (Spin, Owed), (Compare, BigOverCeiling), (Pay, PaidEnough), (Nested, TotalPositive), (Stall, Runaway) }
            .Select((t, i) => $$"""{ "id": "01K9PCX0000000000000000T0{{i + 1}}", "source": "{{Pending}}", "event": "{{t.Item1}}", "guard": "{{t.Item2}}", "targets": ["{{Settled}}"]{{(t.Item1 == Spin ? loop : "")}} }""");
        repo.WriteFile(".maquettiste/model/processes/settlement.json", $$"""
            {
              "kind": "process",
              "id": "{{Settlement}}",
              "name": "Settlement",
              "context": [
                { "id": "{{EventContext}}", "name": "event", "type": "string" },
                { "id": "{{Due}}", "name": "due", "type": "decimal" },
                { "id": "{{Big}}", "name": "big", "type": "int64" },
                { "id": "{{Ceiling}}", "name": "ceiling", "type": "int64" },
                { "id": "{{Total}}", "name": "total", "type": { "ref": "{{Money}}" } }
              ],
              "events": [
                { "id": "{{Go}}", "name": "go" }, { "id": "{{Spin}}", "name": "spin" }, { "id": "{{Compare}}", "name": "compare" },
                { "id": "{{Pay}}", "name": "pay", "payload": [ { "id": "{{Paid}}", "name": "paid", "type": "decimal" } ] },
                { "id": "{{Nested}}", "name": "nested" }, { "id": "{{Stall}}", "name": "stall" }
              ],
              "guards": [
                { "id": "{{ReadsEvent}}", "name": "readsEvent", "condition": { "op": "eq", "args": [{{Path(EventContext)}}, { "value": "go" }] } },
                { "id": "{{Owed}}", "name": "owed", "condition": { "op": "gt", "args": [{{Path(Due)}}, { "value": 0 }] } },
                { "id": "{{BigOverCeiling}}", "name": "bigOverCeiling", "condition": { "op": "gt", "args": [{{Path(Big)}}, {{Path(Ceiling)}}] } },
                { "id": "{{PaidEnough}}", "name": "paidEnough", "condition": { "op": "gt", "args": [{{Path(Paid)}}, {{Dec("0.5")}}] } },
                { "id": "{{TotalPositive}}", "name": "totalPositive", "condition": { "op": "gt", "args": [{{Path(Total + "." + MoneyAmount)}}, { "value": 0 }] } },
                { "id": "{{Runaway}}", "name": "runaway", "expression": "(() => { while (true) {} })()" }
              ],
              "actions": [ { "id": "{{Loop}}", "name": "loop", "expression": "(() => { while (true) {} })()" } ],
              "states": [ { "id": "{{Pending}}", "name": "pending" }, { "id": "{{Settled}}", "name": "settled" } ],
              "transitions": [ {{string.Join(", ", transitions)}} ]
            }
            """);
        return repo;
    }

    [Fact]
    public async Task A_condition_that_reads_a_context_attribute_named_event_is_MQ3102_and_its_guard_a_stub()
    {
        // A condition's row holds the payload under event, so the read would see the payload: the guard is refused, not misread.
        using var repo = SettlementRepo();
        await using var store = new ModelStore(repo.Options);
        var report = await store.ValidateAsync(ValidationScope.All, Ct);
        var found = report.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error || d.Rule.StartsWith("MQ31", StringComparison.Ordinal)).ToList();
        var refused = Assert.Single(found);
        Assert.Equal(("MQ3102", "/guards/0/condition"), (refused.Rule, refused.JsonPointer));
        Assert.Contains("'event'", refused.Message, StringComparison.Ordinal);

        var snapshot = await store.GetSnapshotAsync(Ct);
        var compiled = ProcessExpressions.Get(snapshot, snapshot.Get<Process>(Settlement)!);
        Assert.Equal([Owed, BigOverCeiling, PaidEnough, TotalPositive], compiled.Conditions.Order(StringComparer.Ordinal));
        Assert.DoesNotContain(ReadsEvent, compiled.Compiled);
        Assert.Contains(Runaway, compiled.Compiled);
        Assert.Contains(Loop, compiled.Compiled);

        // Every process with conditions, of any model, loads the one prepared runtime.
        using var other = ReviewRepo();
        await using var otherStore = new ModelStore(other.Options);
        var review = await otherStore.GetSnapshotAsync(Ct);
        Assert.Same(compiled.Scripts[0], ProcessExpressions.Get(review, review.Get<Process>(ProcessId)!).Scripts[0]);

        var model = await new ModelResolver(repo.Options).ResolveAsync(snapshot, null, Ct);
        var guard = model.Processes.Single(p => p.Id == Settlement).Guards.Single(g => g.Id == ReadsEvent);
        Assert.Null(guard.Condition);
        Assert.True(guard.IsStub);

        var trace = await SimulateAsync(store, Go, $$"""{ "{{EventContext}}": "go" }""", process: Settlement);
        Assert.False(trace.Accepted);
        Assert.Equal(GuardSource.Missing, Assert.Single(trace.Guards).Source);
    }

    [Fact]
    public async Task An_int64_beyond_2_to_the_53_compares_exactly()
    {
        using var repo = SettlementRepo();
        await using var store = new ModelStore(repo.Options);
        Assert.True((await SimulateAsync(store, Compare, $$"""{ "{{Big}}": 9007199254740993, "{{Ceiling}}": 9007199254740992 }""", process: Settlement)).Accepted);
        Assert.False((await SimulateAsync(store, Compare, $$"""{ "{{Big}}": 9007199254740992, "{{Ceiling}}": 9007199254740992 }""", process: Settlement)).Accepted);
    }

    [Fact]
    public async Task A_payload_attribute_is_read_under_event_in_simulation_and_scenario_replay()
    {
        using var repo = SettlementRepo();
        repo.WriteFile(".maquettiste/model/scenarios/settlement/pays.json", $$"""
            {
              "kind": "scenario",
              "id": "01K9PCX0000000000000000C01",
              "name": "Pays",
              "process": "{{Settlement}}",
              "start": { "at": "2026-07-01T00:00:00Z" },
              "steps": [ { "id": "01K9PCX0000000000000000C02", "event": "{{Pay}}", "payload": { "{{Paid}}": 0.6 }, "expect": { "accepted": true, "states": ["{{Settled}}"] } } ]
            }
            """);
        await using var store = new ModelStore(repo.Options);
        Assert.True((await SimulateAsync(store, Pay, "{}", payload: $$"""{ "{{Paid}}": 0.6 }""", process: Settlement)).Accepted);
        Assert.False((await SimulateAsync(store, Pay, "{}", payload: $$"""{ "{{Paid}}": 0.4 }""", process: Settlement)).Accepted);
        Assert.False((await SimulateAsync(store, Pay, "{}", process: Settlement)).Accepted);

        // A replay answers the same each time.
        for (var run = 0; run < 2; run++)
        {
            var verified = await store.VerifyScenariosAsync(Settlement, null, Ct);
            Assert.True(verified.Status == ProcessCallStatus.Ok, verified.Detail);
            var result = Assert.Single(verified.Value!.Results);
            Assert.True(result.Passed, result.Failure?.Message);
        }
    }

    [Fact]
    public async Task A_value_object_context_attribute_reads_its_member()
    {
        using var repo = SettlementRepo();
        await using var store = new ModelStore(repo.Options);
        Assert.True((await SimulateAsync(store, Nested, $$"""{ "{{Total}}": { "amount": 1.5, "currency": "USD" } }""", process: Settlement)).Accepted);
        Assert.False((await SimulateAsync(store, Nested, $$"""{ "{{Total}}": { "amount": 0, "currency": "USD" } }""", process: Settlement)).Accepted);
        Assert.False((await SimulateAsync(store, Nested, $$"""{ "{{Total}}": null }""", process: Settlement)).Accepted);
    }

    [Fact]
    public async Task Runaway_JavaScript_beside_conditions_stops_at_the_statement_limit()
    {
        // A process with conditions runs without the wall clock, so the statement limit alone stops a JavaScript guard or action.
        using var repo = SettlementRepo();
        await using var store = new ModelStore(repo.Options);
        var action = await SimulateAsync(store, Spin, $$"""{ "{{Due}}": 1 }""", process: Settlement);
        Assert.Contains(action.Diagnostics, d => d.Rule == "MQ9503" && d.Message.Contains("statement limit", StringComparison.Ordinal));
        var guard = await SimulateAsync(store, Stall, "{}", process: Settlement);
        Assert.False(guard.Accepted);
        Assert.Contains(guard.Diagnostics, d => d.Rule == "MQ9503" && d.Message.Contains("statement limit", StringComparison.Ordinal));
    }

    // ---- guards that read the object (lifecycle-design.md decisions 20 to 22) ----

    internal const string Fulfilment = "01K9PSB0000000000000000P01";
    internal const string DocumentEntity = "01K9PSB0000000000000000E01";
    internal const string OrderEntity = "01K9PSB0000000000000000E02";
    internal const string CustomerEntity = "01K9PSB0000000000000000E03";
    internal const string ReturnNoteEntity = "01K9PSB0000000000000000E04";
    internal const string DocumentTotal = "01K9PSB0000000000000000A12";
    internal const string OrderStatus = "01K9PSB0000000000000000A21";
    internal const string OrderRush = "01K9PSB0000000000000000A22";
    internal const string CustomerVip = "01K9PSB0000000000000000A32";
    internal const string NoteRush = "01K9PSB0000000000000000A42";
    internal const string TrackedAt = "01K9PSB0000000000000000A91";
    internal const string DocumentCustomer = "01K9PSB0000000000000000R01";
    internal const string CustomerEnd = "01K9PSB0000000000000000R03";
    internal const string SubjectContext = "01K9PSB0000000000000000A52";
    internal const string OpenState = "01K9PSB0000000000000000S01";
    internal const string DoneState = "01K9PSB0000000000000000S02";
    internal const string ObjectProcessFile = ".maquettiste/model/processes/fulfilment.json";

    /// <summary>The id of the i-th guard of <see cref="ObjectRepo"/>, counted from 1.</summary>
    internal static string GuardId(int i) => "01K9PSB0000000000000000G" + i.ToString("D2", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The id of the event the i-th guard of <see cref="ObjectRepo"/> guards, counted from 1.</summary>
    internal static string EventId(int i) => "01K9PSB0000000000000000V" + i.ToString("D2", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// The bindings fixture with an invented lifecycle Fulfilment over Order (bound to its plain string status) and, with
    /// <paramref name="returnNotes"/>, ReturnNote too. Both derive from Document (<c>total</c>, of default <paramref name="totalDefault"/>
    /// when given, and a to-one navigation <c>customer</c> to Customer, whose <c>vip</c> defaults to false); each has its own <c>rush</c>,
    /// two attributes that only share a name. Order applies the stereotype Tracked (<c>trackedAt</c>). The context holds <c>limit</c> and,
    /// with <paramref name="subjectContext"/>, a string named <c>subject</c>. Guard i, a condition written as text, guards event i from
    /// open to done.
    /// </summary>
    internal static TempRepo ObjectRepo(IReadOnlyList<string> guards, bool returnNotes = false, string? totalDefault = null, bool subjectContext = false)
    {
        var repo = Repo();
        const string Shop = "01K6BFX0000000000000000001";
        repo.WriteFile(".maquettiste/model/vocabularies/stereotypes/tracked.json", $$"""
            { "kind": "stereotype", "id": "01K9PSB0000000000000000T91", "key": "tracked", "name": "Tracked", "appliesTo": ["entity"],
              "attributes": [ { "id": "{{TrackedAt}}", "name": "trackedAt", "type": "datetimeoffset" } ] }
            """);
        var total = totalDefault is null ? "" : $$""", "default": {{totalDefault}}""";
        repo.WriteFile(".maquettiste/model/entities/document.json", $$"""
            { "kind": "entity", "id": "{{DocumentEntity}}", "name": "Document", "package": "{{Shop}}", "key": { "attributes": ["01K9PSB0000000000000000A11"] },
              "attributes": [ { "id": "01K9PSB0000000000000000A11", "name": "id", "type": "uuid", "required": true },
                              { "id": "{{DocumentTotal}}", "name": "total", "type": "decimal"{{total}} } ] }
            """);
        repo.WriteFile(".maquettiste/model/entities/order.json", $$"""
            { "kind": "entity", "id": "{{OrderEntity}}", "name": "Order", "package": "{{Shop}}", "base": "{{DocumentEntity}}", "stereotypes": ["tracked"],
              "attributes": [ { "id": "{{OrderStatus}}", "name": "status", "type": "string", "length": 20 },
                              { "id": "{{OrderRush}}", "name": "rush", "type": "bool", "default": false } ] }
            """);
        repo.WriteFile(".maquettiste/model/entities/return-note.json", $$"""
            { "kind": "entity", "id": "{{ReturnNoteEntity}}", "name": "ReturnNote", "package": "{{Shop}}", "base": "{{DocumentEntity}}",
              "attributes": [ { "id": "{{NoteRush}}", "name": "rush", "type": "bool" } ] }
            """);
        repo.WriteFile(".maquettiste/model/entities/customer.json", $$"""
            { "kind": "entity", "id": "{{CustomerEntity}}", "name": "Customer", "package": "{{Shop}}", "key": { "attributes": ["01K9PSB0000000000000000A31"] },
              "attributes": [ { "id": "01K9PSB0000000000000000A31", "name": "id", "type": "uuid", "required": true },
                              { "id": "{{CustomerVip}}", "name": "vip", "type": "bool", "default": false } ] }
            """);
        repo.WriteFile(".maquettiste/model/relations/document-customer.json", $$"""
            { "kind": "relation", "id": "{{DocumentCustomer}}", "name": "DocumentCustomer",
              "ends": [ { "id": "01K9PSB0000000000000000R02", "entity": "{{DocumentEntity}}", "role": "documents" },
                        { "id": "{{CustomerEnd}}", "entity": "{{CustomerEntity}}", "role": "customer", "navigation": "customer", "max": 1 } ] }
            """);
        var subjects = $$"""{ "id": "01K9PSB0000000000000000B01", "entity": "{{OrderEntity}}", "boundAttribute": "{{OrderStatus}}" }"""
            + (returnNotes ? $$""", { "id": "01K9PSB0000000000000000B02", "entity": "{{ReturnNoteEntity}}" }""" : "");
        var context = """{ "id": "01K9PSB0000000000000000A51", "name": "limit", "type": "decimal", "default": 0 }"""
            + (subjectContext ? $$""", { "id": "{{SubjectContext}}", "name": "subject", "type": "string" }""" : "");
        var events = guards.Select((_, i) => $$"""{ "id": "{{EventId(i + 1)}}", "name": "e{{i + 1}}" }""");
        var guardJson = guards.Select((g, i) => $$"""{ "id": "{{GuardId(i + 1)}}", "name": "g{{i + 1}}", "condition": { "text": {{JsonSerializer.Serialize(g)}} } }""");
        var transitions = guards.Select((_, i) =>
            $$"""{ "id": "01K9PSB0000000000000000X{{i + 1:D2}}", "source": "{{OpenState}}", "event": "{{EventId(i + 1)}}", "guard": "{{GuardId(i + 1)}}", "targets": ["{{DoneState}}"] }""");
        repo.WriteFile(ObjectProcessFile, $$"""
            {
              "kind": "process", "id": "{{Fulfilment}}", "name": "Fulfilment", "package": "{{Shop}}", "use": "lifecycle",
              "subjects": [ {{subjects}} ],
              "context": [ {{context}} ],
              "events": [ {{string.Join(", ", events)}} ],
              "guards": [ {{string.Join(", ", guardJson)}} ],
              "states": [ { "id": "{{OpenState}}", "name": "open" }, { "id": "{{DoneState}}", "name": "done", "type": "final" } ],
              "initial": "{{OpenState}}",
              "transitions": [ {{string.Join(", ", transitions)}} ]
            }
            """);
        return repo;
    }

    /// <summary>The findings of a model that matter here: errors and the expression rules', but MQ3108 (the guards are written as text).</summary>
    private static async Task<List<Diagnostic>> FindingsAsync(ModelStore store) =>
        [.. (await store.ValidateAsync(ValidationScope.All, Ct)).Diagnostics
            .Where(d => d.Rule != "MQ3108" && (d.Severity == DiagnosticSeverity.Error || d.Rule.StartsWith("MQ31", StringComparison.Ordinal)))];

    private static string Describe(IEnumerable<Diagnostic> diagnostics) => string.Join("\n", diagnostics.Select(d => $"{d.Rule} {d.JsonPointer} {d.Message}"));

    /// <summary>Simulates guard i's event with some objects (a JSON array whose first object is the subject), or none; whether it was accepted.</summary>
    internal static async Task<bool> AcceptsAsync(ModelStore store, int guard, string? objects, string? context = null)
    {
        var list = objects is null ? null : JsonDocument.Parse(objects).RootElement.EnumerateArray().Select(o => o.Clone()).ToList();
        var request = new SimulateRequest
        {
            Start = context is null ? null : JsonDocument.Parse($$"""{ "context": {{context}} }""").RootElement.Clone(),
            Objects = list,
            Subject = list?[0].GetProperty("id").GetString(),
            Steps = [JsonDocument.Parse($$"""{ "input": "event", "event": "{{EventId(guard)}}" }""").RootElement.Clone()],
        };
        var call = await store.SimulateProcessAsync(Fulfilment, request, Ct);
        Assert.True(call.Status == ProcessCallStatus.Ok, call.Detail);
        return call.Value!.Trace[^1].Accepted;
    }

    /// <summary>An order, with some values and links, and a VIP customer, as a run's objects.</summary>
    internal static string OrderAndCustomer(string values = "", string links = "") =>
        $$"""[ { "id": "01K9PSB0000000000000000K01", "name": "order", "entity": "{{OrderEntity}}", "values": { {{values}} }, "links": [ {{links}} ] },"""
        + $$"""  { "id": "01K9PSB0000000000000000K02", "name": "customer", "entity": "{{CustomerEntity}}", "values": { "{{CustomerVip}}": true } } ]""";

    /// <summary>One value of an object, an attribute id and its JSON.</summary>
    internal static string Given(string attribute, string json) => "\"" + attribute + "\": " + json;

    /// <summary>The link from the order to the customer of <see cref="OrderAndCustomer"/>.</summary>
    internal static string CustomerLink => $$"""{ "end": "{{CustomerEnd}}", "object": "01K9PSB0000000000000000K02" }""";

    [Fact]
    public async Task A_guard_reads_the_objects_own_and_inherited_attributes_and_a_to_one_navigation()
    {
        using var repo = ObjectRepo(["subject.rush", "subject.total > 0", "subject.customer.vip", "limit > 0"]);
        await using var store = new ModelStore(repo.Options);
        var findings = await FindingsAsync(store);
        Assert.True(findings.Count == 0, Describe(findings));

        var model = await new ModelResolver(repo.Options).ResolveAsync(await store.GetSnapshotAsync(Ct), null, Ct);
        var process = model.Processes.Single(p => p.Id == Fulfilment);
        RGuard Guard(int i) => process.Guards.Single(g => g.Id == GuardId(i));
        REntity Entity(string id) => model.Entities.Single(e => e.Id == id);

        // Each path ends at the declaring entity's own attribute: Order's rush, Document's total (not Order's copy), Customer's vip.
        Assert.Equal("subject.rush", Guard(1).Condition!.Text);
        Assert.Same(Entity(OrderEntity).OwnAttributes.Single(a => a.Id == OrderRush), Guard(1).Condition!.Target);
        var positive = Guard(2).Condition!;
        Assert.Equal(("subject.total > 0", "bool"), (positive.Text, positive.Type.Kind));
        Assert.Same(Entity(DocumentEntity).OwnAttributes.Single(a => a.Id == DocumentTotal), positive.Args[0].Target);
        Assert.Contains("mq.at(row, \"subject\", \"total\")", positive.JavaScript, StringComparison.Ordinal);
        Assert.Equal("subject.customer.vip", Guard(3).Condition!.Text);
        Assert.Same(Entity(CustomerEntity).OwnAttributes.Single(a => a.Id == CustomerVip), Guard(3).Condition!.Target);

        // A guard that reads the object depends on the subject's entity and its bases, and on the relation it follows and its far end; one
        // that does not keeps the keys it had.
        Assert.Contains("e:" + OrderEntity, Guard(2).Dependencies);
        Assert.Contains("e:" + DocumentEntity, Guard(2).Dependencies);
        Assert.Contains("e:" + DocumentCustomer, Guard(3).Dependencies);
        Assert.Contains("e:" + CustomerEntity, Guard(3).Dependencies);
        Assert.DoesNotContain("e:" + DocumentEntity, Guard(4).Dependencies);
        Assert.DoesNotContain("e:" + CustomerEntity, Guard(4).Dependencies);
    }

    [Fact]
    public async Task With_several_subjects_a_guard_reads_what_their_shared_base_holds()
    {
        using var repo = ObjectRepo(["subject.total > 0", "subject.customer.vip", "subject.rush"], returnNotes: true);
        await using var store = new ModelStore(repo.Options);

        // Order's rush and ReturnNote's rush only share a name: neither is what every subject holds under one id.
        var refused = Assert.Single(await FindingsAsync(store));
        Assert.Equal(("MQ3102", "/guards/2/condition"), (refused.Rule, refused.JsonPointer));

        var model = await new ModelResolver(repo.Options).ResolveAsync(await store.GetSnapshotAsync(Ct), null, Ct);
        var process = model.Processes.Single(p => p.Id == Fulfilment);
        var positive = process.Guards.Single(g => g.Id == GuardId(1));
        Assert.Same(model.Entities.Single(e => e.Id == DocumentEntity).OwnAttributes.Single(a => a.Id == DocumentTotal), positive.Condition!.Args[0].Target);
        Assert.Contains("e:" + OrderEntity, positive.Dependencies);
        Assert.Contains("e:" + ReturnNoteEntity, positive.Dependencies);
        Assert.Contains("e:" + DocumentEntity, positive.Dependencies);
        Assert.NotNull(process.Guards.Single(g => g.Id == GuardId(2)).Condition);
        Assert.True(process.Guards.Single(g => g.Id == GuardId(3)).IsStub);

        // A return note runs the same guard, reading the base's attribute.
        var note = $$"""[ { "id": "01K9PSB0000000000000000K03", "name": "note", "entity": "{{ReturnNoteEntity}}", "values": { "{{DocumentTotal}}": 3 } } ]""";
        Assert.True(await AcceptsAsync(store, 1, note));
        Assert.False(await AcceptsAsync(store, 1, note.Replace(": 3 }", ": 0 }", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task The_bound_attribute_is_out_of_scope_and_a_stereotypes_attribute_in_it()
    {
        using var repo = ObjectRepo(["subject.trackedAt is not null", "subject.status = 'open'", "subject.rush"]);
        // The bound attribute read by id, as a tree: the typer refuses it as the parser refuses its name.
        repo.WriteFile(ObjectProcessFile, repo.ReadFile(ObjectProcessFile).Replace("""{ "text": "subject.rush" }""",
            $$"""{ "op": "isNotNull", "args": [{ "path": ["{{OrderStatus}}"] }] }""", StringComparison.Ordinal));
        await using var store = new ModelStore(repo.Options);
        var findings = await FindingsAsync(store);
        // A stereotype's attribute that the subject carries is in scope (stereotype-sets-design.md decision 7).
        Assert.True(findings.Count == 2 && findings.All(d => d.Rule == "MQ3102"), Describe(findings));
        Assert.Equal(["/guards/1/condition", "/guards/2/condition/args/0"], findings.Select(d => d.JsonPointer).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task A_context_attribute_named_subject_keeps_its_meaning()
    {
        using var repo = ObjectRepo(["subject = 'go'", "subject.total > 0"], subjectContext: true);
        await using var store = new ModelStore(repo.Options);
        var refused = Assert.Single(await FindingsAsync(store));
        Assert.Equal(("MQ3102", "/guards/1/condition"), (refused.Rule, refused.JsonPointer));

        var model = await new ModelResolver(repo.Options).ResolveAsync(await store.GetSnapshotAsync(Ct), null, Ct);
        var process = model.Processes.Single(p => p.Id == Fulfilment);
        var reads = process.Guards.Single(g => g.Id == GuardId(1));
        Assert.Same(process.Context.Single(a => a.Id == SubjectContext), reads.Condition!.Args[0].Target);
        Assert.DoesNotContain("e:" + DocumentEntity, reads.Dependencies);

        // The guard reads the context's value, with or without an object.
        Assert.True(await AcceptsAsync(store, 1, null, $$"""{ "{{SubjectContext}}": "go" }"""));
        Assert.False(await AcceptsAsync(store, 1, null, $$"""{ "{{SubjectContext}}": "stay" }"""));
        Assert.True(await AcceptsAsync(store, 1, OrderAndCustomer(Given(DocumentTotal, "5")), $$"""{ "{{SubjectContext}}": "go" }"""));
    }

    [Fact]
    public async Task Simulation_reads_the_object_its_defaults_and_its_link()
    {
        using var repo = ObjectRepo(["subject.total > 0", "subject.customer.vip", "subject.rush = false"], totalDefault: "1");
        await using var store = new ModelStore(repo.Options);

        // By the object's values; a null fails, since only true passes.
        Assert.True(await AcceptsAsync(store, 1, OrderAndCustomer(Given(DocumentTotal, "2.5"))));
        Assert.False(await AcceptsAsync(store, 1, OrderAndCustomer(Given(DocumentTotal, "0"))));
        Assert.False(await AcceptsAsync(store, 1, OrderAndCustomer(Given(DocumentTotal, "null"))));
        // By defaults: a value not given, and no objects at all, read Document.total's default (1) and Order.rush's (false).
        Assert.True(await AcceptsAsync(store, 1, OrderAndCustomer()));
        Assert.True(await AcceptsAsync(store, 1, null));
        Assert.True(await AcceptsAsync(store, 3, null));
        // Through the link: with no linked customer the path reads null.
        Assert.True(await AcceptsAsync(store, 2, OrderAndCustomer(links: CustomerLink)));
        Assert.False(await AcceptsAsync(store, 2, OrderAndCustomer()));
        Assert.False(await AcceptsAsync(store, 2, null));
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
