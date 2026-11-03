using System.Text.Json;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Expressions;
using Maquettiste.Engine.Resolution;
using Maquettiste.Testing;
using static Maquettiste.Engine.Tests.Expressions.ExpressionRuleTests;

namespace Maquettiste.Engine.Tests.Operations;

/// <summary>
/// Operation guards (next-phase-3-design.md section 3.1, "Operation guards", task H10), over the bindings fixture with an invented
/// operation reprice on Product: its input newPrice, its own error tooLow and the error stale its stereotype contributes. Guards resolve
/// typed, in the order they are checked, each with the error it reports and its lowered JavaScript, which only true passes; the engine
/// runs no operation, so packs render or run them.
/// </summary>
public sealed class OperationGuardTests
{
    private const string Reprice = "01K9GRD0000000000000000Q01";
    private const string NewPrice = "01K9GRD0000000000000000Q02";
    private const string Applied = "01K9GRD0000000000000000Q03";
    private const string TooLow = "01K9GRD0000000000000000Q04";
    private const string Positive = "01K9GRD0000000000000000Q05";
    private const string Changed = "01K9GRD0000000000000000Q06";
    private const string Stereotype = "01K9GRD0000000000000000S01";
    private const string Stale = "01K9GRD0000000000000000S02";
    private const string OperationPath = ".maquettiste/model/operations/reprice.json";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// The bindings fixture with the stereotype common-errors (the error stale) and the operation reprice on Product, whose guards are
    /// positive (<c>newPrice &gt; 0</c>, reporting tooLow) and changed (<c>newPrice &lt;&gt; subject.price</c>, reporting stale).
    /// </summary>
    private static TempRepo RepriceRepo(string? positiveError = TooLow, string changedName = "changed")
    {
        var repo = Repo();
        repo.WriteFile(".maquettiste/model/vocabularies/stereotypes/common-errors.json", $$"""
            {
              "kind": "stereotype",
              "id": "{{Stereotype}}",
              "key": "common-errors",
              "name": "CommonErrors",
              "appliesTo": ["operation"],
              "errors": [ { "id": "{{Stale}}", "name": "stale" } ]
            }
            """);
        var error = positiveError is null ? "" : $$""", "error": "{{positiveError}}" """;
        repo.WriteFile(OperationPath, $$"""
            {
              "kind": "operation",
              "id": "{{Reprice}}",
              "name": "reprice",
              "subject": "{{ProductId}}",
              "effect": "change",
              "input": [ { "id": "{{NewPrice}}", "name": "newPrice", "type": "decimal", "precision": 12, "scale": 2 } ],
              "outcomes": [ { "id": "{{Applied}}", "name": "applied" } ],
              "errors": [ { "id": "{{TooLow}}", "name": "tooLow", "input": "{{NewPrice}}" } ],
              "guards": [
                { "id": "{{Positive}}", "name": "positive", "condition": { "op": "gt", "args": [{ "path": ["{{NewPrice}}"] }, { "value": 0 }] }{{error}} },
                { "id": "{{Changed}}", "name": "{{changedName}}", "error": "{{Stale}}",
                  "condition": { "op": "ne", "args": [{ "path": ["{{NewPrice}}"] }, { "path": ["{{PriceId}}"] }] } }
              ],
              "stereotypes": ["common-errors"]
            }
            """);
        return repo;
    }

    private static async Task<IReadOnlyList<Diagnostic>> ValidateAsync(TempRepo repo) =>
        (await new ModelStore(repo.Options).ValidateAsync(ValidationScope.All, Ct)).Diagnostics;

    private static async Task<ROperation> ResolveAsync(TempRepo repo)
    {
        var store = new ModelStore(repo.Options);
        var model = await new ModelResolver(repo.Options).ResolveAsync(await store.GetSnapshotAsync(Ct), null, Ct);
        return model.Operations.Single(o => o.Id == Reprice);
    }

    private static string Describe(IEnumerable<Diagnostic> diagnostics) => string.Join("\n", diagnostics.Select(d => $"{d.Rule} {d.JsonPointer} {d.Message}"));

    [Fact]
    public async Task Guards_resolve_typed_in_order_with_the_error_each_reports()
    {
        using var repo = RepriceRepo();
        var diagnostics = await ValidateAsync(repo);
        Assert.True(!diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error || d.Rule.StartsWith("MQ31", StringComparison.Ordinal)), Describe(diagnostics));

        var reprice = await ResolveAsync(repo);
        Assert.Equal(["positive", "changed"], reprice.Guards.Select(g => g.Name));
        Assert.Equal([TooLow, Reprice + "." + Stale], reprice.Guards.Select(g => g.Error?.Id));
        Assert.Same(reprice.Errors.Single(e => e.Id == TooLow), reprice.Guards[0].Error);
        Assert.All(reprice.Guards, g => Assert.Same(reprice, g.Operation));

        var positive = reprice.Guards[0].Condition!;
        Assert.Equal(("newPrice > 0", "bool", true), (positive.Text, positive.Type.Kind, positive.Nullable));
        Assert.Same(reprice.Input.Single(), positive.Args[0].Target);
        Assert.Equal("(row) => mq.holds(mq.gt(mq.dec(mq.at(row, \"newPrice\"), 2), mq.dec(\"0\")))", positive.JavaScript);

        var changed = reprice.Guards[1].Condition!;
        Assert.Equal("newPrice <> subject.price", changed.Text);
        Assert.Same(reprice.Subject!.Attributes.Single(a => a.Name == "price"), changed.Args[1].Target);
        Assert.Equal("(row) => mq.holds(mq.ne(mq.dec(mq.at(row, \"newPrice\"), 2), mq.dec(mq.at(row, \"subject\", \"price\"), 2)))", changed.JavaScript);
    }

    [Fact]
    public async Task Only_true_passes_a_guard_so_a_null_operand_fails_it()
    {
        using var repo = RepriceRepo();
        var reprice = await ResolveAsync(repo);
        using var evaluator = ExprEvaluator.Create(reprice.Guards.Select(g => KeyValuePair.Create(g.Name, g.Condition!.JavaScript!)), ct: Ct);
        bool Holds(string guard, string row) => evaluator.Evaluate(guard, JsonDocument.Parse(row).RootElement, Ct) is true;

        Assert.True(Holds("positive", """{ "newPrice": 0.01 }"""));
        Assert.False(Holds("positive", """{ "newPrice": 0 }"""));
        Assert.False(Holds("positive", """{ "newPrice": null }"""));
        Assert.False(Holds("positive", "{}"));
        Assert.True(Holds("changed", """{ "newPrice": 10.01, "subject": { "price": 10.00 } }"""));
        Assert.False(Holds("changed", """{ "newPrice": 10.00, "subject": { "price": 10 } }"""));
        Assert.False(Holds("changed", """{ "newPrice": 10.00, "subject": { "price": null } }"""));
    }

    private const string Supplies = "01K9GRD0000000000000000R01";
    private const string SuppliedEnd = "01K9GRD0000000000000000R02";
    private const string SupplierEnd = "01K9GRD0000000000000000R03";
    private const string Reachable = "01K9GRD0000000000000000Q07";
    private const string CustomerId = "01K6BFX0000000000000000032";
    private const string EmailId = "01K6BFX0000000000000000030";

    /// <summary>
    /// <see cref="RepriceRepo"/> with a relation from Product to its supplier, a Customer (<paramref name="supplierMax"/> at the supplier's
    /// end), and a third guard reachable (<c>subject.supplier.email is not null</c>) that reads through it.
    /// </summary>
    private static TempRepo SupplierRepo(string supplierMax = "1")
    {
        var repo = RepriceRepo();
        repo.WriteFile(".maquettiste/model/relations/supplies.json", $$"""
            {
              "kind": "relation",
              "id": "{{Supplies}}",
              "name": "supplies",
              "ends": [
                { "id": "{{SuppliedEnd}}", "entity": "{{ProductId}}", "role": "products" },
                { "id": "{{SupplierEnd}}", "entity": "{{CustomerId}}", "role": "supplier", "navigation": "supplier", "max": {{supplierMax}} }
              ]
            }
            """);
        var text = repo.ReadFile(OperationPath);
        repo.WriteFile(OperationPath, text.Replace("""
              ],
              "stereotypes"
            """, $$"""
            ,
                { "id": "{{Reachable}}", "name": "reachable", "condition": { "op": "isNotNull", "args": [{ "path": ["{{SupplierEnd}}", "{{EmailId}}"] }] } }
              ],
              "stereotypes"
            """, StringComparison.Ordinal));
        return repo;
    }

    [Fact]
    public async Task A_guard_reads_through_the_subjects_to_one_navigation()
    {
        using var repo = SupplierRepo();
        Assert.DoesNotContain(await ValidateAsync(repo), d => d.Rule.StartsWith("MQ31", StringComparison.Ordinal));
        var guard = (await ResolveAsync(repo)).Guards.Single(g => g.Name == "reachable");
        Assert.Equal("subject.supplier.email is not null", guard.Condition!.Text);
        Assert.Contains("e:" + CustomerId, guard.Dependencies);
        Assert.Contains("e:" + Supplies, guard.Dependencies);

        using var evaluator = ExprEvaluator.Create([KeyValuePair.Create("reachable", guard.Condition.JavaScript!)], ct: Ct);
        bool Holds(string row) => evaluator.Evaluate("reachable", JsonDocument.Parse(row).RootElement, Ct) is true;
        Assert.True(Holds("""{ "subject": { "supplier": { "email": "a@b.c" } } }"""));
        Assert.False(Holds("""{ "subject": { "supplier": { "email": null } } }"""));
        Assert.False(Holds("""{ "subject": { "supplier": null } }"""));
    }

    [Fact]
    public async Task A_guard_through_a_to_many_navigation_is_MQ3102()
    {
        using var repo = SupplierRepo(supplierMax: "\"*\"");
        var diagnostics = await ValidateAsync(repo);
        Assert.Contains(diagnostics, d => d.Rule == "MQ3102" && d.JsonPointer == "/guards/2/condition/args/0" && d.Message.Contains("many", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_operation_record_lists_its_guards()
    {
        using var repo = RepriceRepo();
        var store = new ModelStore(repo.Options);
        var model = await new ModelResolver(repo.Options).ResolveAsync(await store.GetSnapshotAsync(Ct), null, Ct);

        var record = (OperationRecord)ResolvedRecords.Entries(model, "operations", null).Single(e => e.Id == Reprice).Project();
        Assert.Equal([("positive", "newPrice > 0", TooLow), ("changed", "newPrice <> subject.price", Reprice + "." + Stale)],
            record.Guards!.Select(g => (g.Name, g.Condition!.Text, g.Error)));
        Assert.Equal("bool", record.Guards![0].Condition!.Type);
        var json = JsonSerializer.SerializeToNode(record, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal("""{"op":"gt","args":[{"path":["01K9GRD0000000000000000Q02"]},{"value":0}]}""", json["guards"]![0]!["condition"]!["tree"]!.ToJsonString());
    }

    [Fact]
    public async Task An_error_the_operation_does_not_have_is_MQ3111_once_and_resolves_to_none()
    {
        using var repo = RepriceRepo(positiveError: Applied);
        var diagnostics = await ValidateAsync(repo);
        var found = Assert.Single(diagnostics, d => d.Rule == "MQ3111");
        Assert.Equal("/guards/0/error", found.JsonPointer);
        Assert.Null((await ResolveAsync(repo)).Guards[0].Error);
    }

    [Fact]
    public async Task Guard_names_are_identifiers_unique_in_the_operation()
    {
        using var repo = RepriceRepo(changedName: "positive");
        var diagnostics = await ValidateAsync(repo);
        Assert.Contains(diagnostics, d => d is { Rule: "MQ3001", JsonPointer: "/guards/1/name" });
    }

    [Fact]
    public async Task A_guard_without_an_error_resolves_with_none()
    {
        using var repo = RepriceRepo(positiveError: null);
        Assert.DoesNotContain(await ValidateAsync(repo), d => d.Rule == "MQ3111");
        var reprice = await ResolveAsync(repo);
        Assert.Null(reprice.Guards[0].Error);
        Assert.Equal("newPrice > 0", reprice.Guards[0].Condition!.Text);
    }
}
