using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Json;
using Maquettiste.Engine.Loading;
using Maquettiste.Engine.Model;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Validation;

/// <summary>
/// The guards of retiring projection (retire-projection-design.md section 9): a file with a key 0.29.0 removed does not load, with one
/// MQ4145 naming the key and what replaces it; <c>"byConvention": "none"</c> still loads; a database file with no <c>byConvention</c> is
/// MQ4146 while an entity is not bound to it; and a baseline or snapshot taken before 0.29.0 is read with the removed keys dropped.
/// </summary>
public sealed class RetiredProjectionTests
{
    private const string Db = "01J92P0V1QRN2181XM2ZWE02W4";

    private static IReadOnlyList<Diagnostic> Evaluate(string schemaFile, string json)
    {
        using var doc = JsonDocument.Parse(json);
        return TestServices.Schemas.Evaluate(schemaFile, doc.RootElement, "model/x.json");
    }

    private const string Database = """{ "kind": "database", "id": "01J92P0V1QRN2181XM2ZWE02W4", "name": "main", "dialect": "postgresql" }""";

    private const string RelationMapping = """
        { "kind": "mapping", "id": "01K6RPJ0000000000000000001", "name": "places in main", "database": "01J92P0V1QRN2181XM2ZWE02W4",
          "relation": "01K6RPJ0000000000000000002", "foreignKey": "01K6RPJ0000000000000000003" }
        """;

    private const string DesignedTable = """
        { "kind": "table", "id": "01K6RPJ0000000000000000004", "name": "customers", "database": "01J92P0V1QRN2181XM2ZWE02W4",
          "columns": [ { "id": "01K6RPJ0000000000000000005", "name": "id", "type": "uuid" } ] }
        """;

    private static string With(string json, Action<JsonObject> change)
    {
        var node = JsonNode.Parse(json)!.AsObject();
        change(node);
        return node.ToJsonString();
    }

    [Fact]
    public void Files_without_the_removed_keys_load()
    {
        Assert.Empty(Evaluate("database.json", Database));
        Assert.Empty(Evaluate("database.json", With(Database, d => d["byConvention"] = "none")));
        Assert.Empty(Evaluate("mapping.json", RelationMapping));
        Assert.Empty(Evaluate("table.json", DesignedTable));
    }

    public static TheoryData<string, string, string, string> RemovedKeys() => new()
    {
        { "database.json", With(Database, d => d["byConvention"] = "all"), "/byConvention", "write \"byConvention\": \"none\"" },
        { "database.json", With(Database, d => d["packages"] = new JsonArray("01K6RPJ0000000000000000009")), "/packages", "bind each entity to its table" },
        { "mapping.json", With(RelationMapping, m => { m.Remove("relation"); m.Remove("foreignKey"); m["entity"] = "01K6RPJ0000000000000000008"; }), "/entity", "An entity's binding names the table" },
        { "mapping.json", With(RelationMapping, m => m["attributes"] = JsonNode.Parse("""[{ "attribute": "01K6RPJ0000000000000000007", "storage": "json" }]""")), "/attributes/0/storage", "childTables" },
        { "mapping.json", With(RelationMapping, m => m["inheritance"] = "tpt"), "/inheritance", "baseForeignKey" },
        { "mapping.json", With(RelationMapping, m => { m["shape"] = "promoted"; m["promotedName"] = "Membership"; }), "/shape", "an entity of its own with a relation to each end" },
        { "table.json", With(DesignedTable, t => { t["origin"] = "synthesized"; t["entity"] = "01K6RPJ0000000000000000008"; t.Remove("name"); }), "/origin", "designed table with its full definition" },
        { "table.json", With(DesignedTable, t => t["columns"]![0]!["attribute"] = "01K6RPJ0000000000000000007"), "/columns/0/attribute", "Write the table's columns" },
    };

    [Theory]
    [MemberData(nameof(RemovedKeys))]
    public void A_removed_key_is_one_MQ4145_naming_what_replaces_it(string schemaFile, string json, string pointer, string replacement)
    {
        var d = Assert.Single(Evaluate(schemaFile, json));

        Assert.Equal(("MQ4145", DiagnosticSeverity.Error, pointer), (d.Rule, d.Severity, d.JsonPointer));
        Assert.Contains("removed in 0.29.0", d.Message, StringComparison.Ordinal);
        Assert.Contains(replacement, d.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_promoted_setting_is_MQ4145_on_the_settings()
    {
        var settings = """{ "formatVersion": 1, "name": "x", "conventions": { "relationsWithAttributes": "promoted" } }""";

        var d = Assert.Single(Evaluate("maquettiste.json", settings));

        Assert.Equal(("MQ4145", "/conventions/relationsWithAttributes"), (d.Rule, d.JsonPointer));
    }

    [Fact]
    public void MQ4145_and_MQ4146_cannot_be_turned_off_or_suppressed()
    {
        foreach (var rule in new[] { "MQ4145", "MQ4146" })
        {
            Assert.False(RuleCatalog.Get(rule).CanBeDisabled);
            Assert.False(Maquettiste.Engine.Validation.SuppressionRules.CanBeSuppressed(rule));
        }
    }

    private static async Task<IReadOnlyList<Diagnostic>> ValidateAsync(ModelBuilder b) =>
        (await ValidationFixture.Validator().ValidateAsync(b.Build(), ValidationScope.All, null, TestContext.Current.CancellationToken)).Diagnostics;

    [Fact]
    public async Task A_database_without_byConvention_is_warning_MQ4146_while_an_entity_is_not_bound_to_it()
    {
        var b = new ModelBuilder(seed: 145);
        var customer = b.Entity("Customer").Key("id", "uuid");
        b.Entity("Invoice").Key("id", "uuid");
        var main = b.Database("main", Dialect.PostgreSql).ByConvention(null);
        b.Table("customers", main).Column("id", "uuid", nullable: false).PrimaryKey("id").Binds(customer);

        var d = Assert.Single(await ValidateAsync(b), x => x.Rule == "MQ4146");

        Assert.Equal((DiagnosticSeverity.Warning, main.Id, ""), (d.Severity, d.ElementId, d.JsonPointer));
        Assert.Contains("Invoice", d.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Customer,", d.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_database_without_byConvention_is_fine_once_every_entity_is_bound_and_none_is_always_fine()
    {
        var bound = new ModelBuilder(seed: 146);
        var customer = bound.Entity("Customer").Key("id", "uuid");
        var main = bound.Database("main", Dialect.PostgreSql).ByConvention(null);
        bound.Table("customers", main).Column("id", "uuid", nullable: false).PrimaryKey("id").Binds(customer);
        Assert.DoesNotContain(await ValidateAsync(bound), x => x.Rule == "MQ4146");

        var none = new ModelBuilder(seed: 147);
        none.Entity("Customer").Key("id", "uuid");
        none.Database("main", Dialect.PostgreSql).ByConvention(ConventionMapping.None);
        Assert.DoesNotContain(await ValidateAsync(none), x => x.Rule == "MQ4146");
    }

    [Theory]
    [InlineData("designed")]
    [InlineData("imported")]
    public void A_table_whose_only_removed_key_is_a_designed_or_imported_origin_is_told_to_delete_it(string origin)
    {
        using var document = JsonDocument.Parse("{ \"kind\": \"table\", \"id\": \"t\", \"name\": \"notes\", \"origin\": \"" + origin + "\" }");
        var pointers = RemovedProjectionKeys.Find("table.json", document.RootElement);
        Assert.Equal(["/origin"], pointers);
        var finding = RemovedProjectionKeys.Finding("table.json", document.RootElement, pointers, "t", "t.json");
        Assert.Equal("/origin Table 'notes' still has origin \"" + origin + "\", which was removed in 0.29.0: every table is a designed table now, so the key "
            + "says nothing. Delete the key.", finding.Message);

        // An overlay still gets the overlay message.
        using var overlay = JsonDocument.Parse("""{ "kind": "table", "id": "t", "name": "notes", "origin": "synthesized" }""");
        Assert.Contains("an overlay no longer adjusts", RemovedProjectionKeys.Finding("table.json", overlay.RootElement, ["/origin"], "t", "t.json").Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_baseline_reads_old_files_with_the_removed_keys_dropped()
    {
        var mapping = JsonNode.Parse("""{ "kind": "mapping", "id": "m", "name": "m", "database": "d", "entity": "e", "table": "t" }""")!.AsObject();
        Assert.Equal(new StripResult(false, StripLayout.LaidOut), RemovedProjectionKeys.Strip("mapping.json", mapping));

        var overlay = JsonNode.Parse("""{ "kind": "table", "id": "t", "database": "d", "origin": "synthesized", "entity": "e" }""")!.AsObject();
        Assert.Equal(new StripResult(false, StripLayout.LaidOut), RemovedProjectionKeys.Strip("table.json", overlay));

        var relation = JsonNode.Parse("""
            { "kind": "mapping", "id": "m", "name": "m", "database": "d", "relation": "r", "ignore": false,
              "attributes": [{ "attribute": "a", "column": "c", "storage": "json" }] }
            """)!.AsObject();
        Assert.Equal(new StripResult(true, StripLayout.LaidOut), RemovedProjectionKeys.Strip("mapping.json", relation));
        Assert.Equal("""{"kind":"mapping","id":"m","name":"m","database":"d","relation":"r","attributes":[{"attribute":"a","column":"c"}]}""", relation.ToJsonString());

        var database = JsonNode.Parse("""{ "kind": "database", "id": "d", "name": "main", "dialect": "postgresql", "packages": ["p"] }""")!.AsObject();
        Assert.Equal(new StripResult(true, StripLayout.LaidOut), RemovedProjectionKeys.Strip("database.json", database));
        Assert.Equal("""{"kind":"database","id":"d","name":"main","dialect":"postgresql","byConvention":"none"}""", database.ToJsonString());

        var designed = JsonNode.Parse("""{ "kind": "table", "id": "t", "database": "d", "name": "t", "origin": "designed" }""")!.AsObject();
        Assert.Equal(new StripResult(true, StripLayout.None), RemovedProjectionKeys.Strip("table.json", designed));
        var none = JsonNode.Parse("""{ "kind": "database", "id": "d", "name": "main", "dialect": "postgresql", "byConvention": "none" }""")!.AsObject();
        Assert.Equal(new StripResult(true, StripLayout.None), RemovedProjectionKeys.Strip("database.json", none));
        var missing = JsonNode.Parse("""{ "kind": "database", "id": "d", "name": "main", "dialect": "postgresql" }""")!.AsObject();
        Assert.Equal(new StripResult(true, StripLayout.NoConvention), RemovedProjectionKeys.Strip("database.json", missing));
    }

    [Fact]
    public async Task A_read_only_store_over_an_old_folder_loads_its_files_without_the_removed_keys()
    {
        using var repo = new TempRepo();
        repo.WriteFile(".maquettiste/model/databases/main/database.json",
            """{ "kind": "database", "id": "01J92P0V1QRN2181XM2ZWE02W4", "name": "main", "dialect": "postgresql", "byConvention": "all" }""");
        repo.WriteFile(".maquettiste/model/mappings/customer.json",
            """{ "kind": "mapping", "id": "01K6RPJ0000000000000000001", "name": "c", "database": "01J92P0V1QRN2181XM2ZWE02W4", "entity": "01K6RPJ0000000000000000008" }""");
        var store = new TolerantDocumentStore(new ReadOnlyFolderDocumentStore(new ModelPaths(repo.Options)));

        var database = await store.ReadAsync("model/databases/main/database.json", TestContext.Current.CancellationToken);
        var mapping = await store.ReadAsync("model/mappings/customer.json", TestContext.Current.CancellationToken);

        Assert.NotNull(database);
        Assert.Empty(Evaluate("database.json", Encoding.UTF8.GetString(database)));
        Assert.Null(mapping);
    }
}
