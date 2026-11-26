using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Model;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Editor;

/// <summary>
/// MQ4127: a query's <c>entity</c>, <c>collections</c> and a select field's <c>attribute</c> were removed in 0.28.0. A file that still has
/// one is one error per file at the first such key, naming the replacement, in place of the schema violations; the file does not load,
/// a save is refused, and <c>validation.rules</c> cannot change the error.
/// </summary>
public sealed class RemovedQueryKeysTests
{
    private const string FindCustomersId = "01K6QRY0000000000000000003";
    private const string FindCustomers = ".maquettiste/model/databases/main/queries/find-customers-with-issued-invoices.json";
    private const string CustomerId = "01J92P0V0ETQKXXP951CMMNHH3";
    private const string CustomerIdAttribute = "01J92P0V0KGPC29TQQG8R57EBM";

    private static CancellationToken Ct => EditorRepo.Ct;

    private static IReadOnlyList<Diagnostic> Evaluate(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return TestServices.Schemas.Evaluate("query.json", doc.RootElement, "model/q.json");
    }

    private const string Query = """
        { "kind": "query", "id": "01K6QRY0000000000000000009", "name": "Old", "database": "01J92P0V1QRN2181XM2ZWE02W4",
          "from": { "source": "01J92P0V1T0J6RH4MY9H81NYB4", "alias": "i" },
          "select": [ { "name": "id", "expression": { "column": "i.id" } } ] }
        """;

    private static string With(Action<JsonObject> change)
    {
        var node = JsonNode.Parse(Query)!.AsObject();
        change(node);
        return node.ToJsonString();
    }

    [Fact]
    public void A_query_without_the_removed_keys_is_valid() => Assert.Empty(Evaluate(Query));

    [Fact]
    public void An_entity_is_one_MQ4127_error_at_entity_that_names_the_binding_way()
    {
        var d = Assert.Single(Evaluate(With(q => q["entity"] = CustomerId)));

        Assert.Equal(("MQ4127", DiagnosticSeverity.Error, "/entity"), (d.Rule, d.Severity, d.JsonPointer));
        Assert.Contains("Query 'Old' still has an entity as the shape of its rows (entity), which were removed in 0.28.0", d.Message, StringComparison.Ordinal);
        Assert.Contains("bind the entity to the query (its binding's source names the query", d.Message, StringComparison.Ordinal);
        Assert.Contains("composition (entity.parts)", d.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Collections_and_field_attributes_are_named_too_and_only_once_per_file()
    {
        var collections = Assert.Single(Evaluate(With(q => q["collections"] = JsonNode.Parse("""
            [ { "attribute": "lines", "entity": "01J92P0V0GWFR78HZH0P8Z3GY7", "query": { "from": { "source": "x", "alias": "l" } } } ]
            """))));
        Assert.Equal(("MQ4127", "/collections"), (collections.Rule, collections.JsonPointer));
        Assert.Contains("still has collections, which", collections.Message, StringComparison.Ordinal);

        // A field that fills an attribute has no name: the missing name is part of the same finding, not a schema violation of its own.
        var field = Assert.Single(Evaluate(With(q => q["select"] = JsonNode.Parse($$"""[ { "attribute": "{{CustomerIdAttribute}}", "expression": { "column": "i.id" } } ]"""))));
        Assert.Equal(("MQ4127", "/select/0/attribute"), (field.Rule, field.JsonPointer));
        Assert.Contains("a select field that fills an attribute (attribute)", field.Message, StringComparison.Ordinal);

        var all = Assert.Single(Evaluate(With(q =>
        {
            q["entity"] = CustomerId;
            q["collections"] = JsonNode.Parse("""[ { "attribute": "x", "query": { "from": { "source": "x" } } } ]""");
            q["select"] = JsonNode.Parse($$"""
                [ { "attribute": "{{CustomerIdAttribute}}", "expression": { "column": "i.id" } },
                  { "name": "n", "attribute": "{{CustomerIdAttribute}}", "expression": { "column": "i.id" } } ]
                """);
        })));
        Assert.Equal("/entity", all.JsonPointer);
        Assert.Contains("an entity as the shape of its rows (entity), collections and 2 select fields that fill attributes (attribute)", all.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Another_schema_violation_beside_a_removed_key_is_still_MQ1002()
    {
        var found = Evaluate(With(q =>
        {
            q["entity"] = CustomerId;
            q["distinct"] = "yes";
        }));

        Assert.Equal([("MQ1002", "/distinct"), ("MQ4127", "/entity")], found.Select(d => (d.Rule, d.JsonPointer)).OrderBy(x => x.Rule, StringComparer.Ordinal));
    }

    [Fact]
    public async Task A_file_with_a_removed_key_does_not_load_and_validation_rules_cannot_turn_it_off()
    {
        await using var r = EditorRepo.Create(packs: false);
        r.EditSettingsOnDisk(s => s["validation"] = new JsonObject { ["rules"] = new JsonObject { ["MQ4127"] = "off" } });
        var path = r.Repo.PathOf(FindCustomers);
        var node = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        node["entity"] = CustomerId;
        File.WriteAllText(path, node.ToJsonString(), new UTF8Encoding(false));

        var report = await r.Store.ValidateAsync(ValidationScope.All, Ct);

        var d = Assert.Single(report.Diagnostics, d => d.FilePath == FindCustomers);
        Assert.Equal(("MQ4127", DiagnosticSeverity.Error, "/entity"), (d.Rule, d.Severity, d.JsonPointer));
        Assert.Null(r.Store.Current!.Get<Query>(FindCustomersId));
        Assert.False(RuleCatalog.Get("MQ4127").CanBeDisabled);
    }

    [Fact]
    public async Task A_save_with_a_removed_key_is_refused_with_MQ4127()
    {
        await using var r = EditorRepo.Create(packs: false);
        var parsed = r.Store.ParseBatch(Encoding.UTF8.GetBytes($$"""{ "operations": [ { "op": "create", "element": {{With(q => q["entity"] = CustomerId)}} } ] }"""));
        Assert.True(parsed.Batch is not null, string.Join("; ", parsed.Diagnostics.Select(x => x.Message)));

        var applied = await r.Store.ApplyBatchAsync(parsed.Batch, ChangeSource.Editor, Ct);

        Assert.NotEqual(SaveOutcome.Saved, applied.Outcome);
        Assert.Contains(applied.Items.SelectMany(i => i.Diagnostics), x => x.Rule == "MQ4127");
    }
}
