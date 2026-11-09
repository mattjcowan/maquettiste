using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Generation;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Tests.Loading;
using Maquettiste.Engine.Tests.Processes;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Model;

/// <summary>
/// The contract of relations with several types, part 2 (several-types-design.md section 19.6): a relation mapping's <c>ends</c> entry gains
/// <c>foreignKeys</c>, <c>types</c>, <c>idColumn</c>, <c>typeColumn</c> and <c>referencesColumn</c> and requires only <c>end</c>; a relation
/// mapping gains <c>constants</c>, whose column is a column id only; an entity binding gains <c>discriminatorValue</c>. Today's files keep
/// their bytes, a one-type entry with no foreign key reports MQ4009 instead of failing the schema, and deleting a designed junction's keys
/// keeps today's plans. Over the showcase fixture, whose <c>uses in main</c> mapping names the designed junction <c>repair_job_parts</c>.
/// </summary>
public sealed class RelationStorageContractTests
{
    private const string Main = "01K70SPKCH3XJRR0BM82TM9GT4";
    private const string Uses = "01K70SPKCHD0ZK77WGWJ9WYYJA";
    private const string UsesMapping = "01K70SPKCJVGR468TPX8FE1QBQ";
    private const string Junction = "01K70SPKCJQ89DBZ9DCDV48FDY";
    private const string JobKey = "01K70SPKCJ6E1E6R9R00DNX8G5";
    private const string PartKey = "01K70SPKCJVN650YPMXRGG409D";
    private const string JobColumn = "01K70SPKCJ21BNX5AMPPYE7A9Y";
    private const string PartColumn = "01K70SPKCJCA69QHK6K0XRT7W6";
    private const string QuantityColumn = "01K70SPKCJ8CNYM265MND8T3JQ";
    private const string JobEnd = "01K70SPKCHSEVMEAGBWTH2243Q";
    private const string PartEnd = "01K70SPKCH2QTS9K3MT4R8ZY9N";
    private const string Entity = "01JENT00000000000000000701";
    private const string MappingPath = "model/mappings/uses-in-main.json";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static IReadOnlyList<Diagnostic> Evaluate(string schema, string json)
    {
        using var document = JsonDocument.Parse(json);
        return TestServices.Schemas.Evaluate(schema, document.RootElement, "model/x.json");
    }

    private static string MappingWith(string ends, string extra = "") =>
        $$"""{"kind":"mapping","id":"{{UsesMapping}}","name":"uses in main","database":"{{Main}}","relation":"{{Uses}}","junctionTable":"{{Junction}}","ends":[{{ends}}]{{extra}}}""";

    private static void Valid(string schema, string json) =>
        Assert.True(Evaluate(schema, json) is { Count: 0 }, string.Join("; ", Evaluate(schema, json).Select(d => d.JsonPointer + " " + d.Message)));

    private static void Invalid(string schema, string json, string pointer)
    {
        var found = Evaluate(schema, json);
        Assert.True(found.Any(d => d.Rule == "MQ1002" && (d.JsonPointer ?? "").StartsWith(pointer, StringComparison.Ordinal)),
            pointer + ": " + string.Join("; ", found.Select(d => d.JsonPointer + " " + d.Message)));
    }

    private static string[] Strings(JsonNode? node) => [.. node!.AsArray().Select(n => n!.GetValue<string>())];

    // ---- schema ----

    [Fact]
    public void The_schema_titles_orders_and_loosens_the_ends_entry_and_places_the_new_keys()
    {
        var mapping = JsonNode.Parse(File.ReadAllText(Path.Combine(Fixtures.RepoRoot, "schemas", "v1", "mapping.json")))!;
        var entry = mapping["properties"]!["ends"]!["items"]!;
        Assert.Equal(["end", "foreignKey", "foreignKeys", "types", "idColumn", "typeColumn", "referencesColumn"], Strings(entry["x-order"]));
        Assert.Equal(["end"], Strings(entry["required"]));
        Assert.Equal("End type storage", entry["properties"]!["types"]!["items"]!["title"]!.GetValue<string>());
        Assert.Equal(["entity", "foreignKey", "referencesColumn"], Strings(entry["properties"]!["types"]!["items"]!["x-order"]));
        foreach (var list in new[] { entry["properties"]!["foreignKeys"]!, entry["properties"]!["types"]!, mapping["properties"]!["constants"]! })
            Assert.Equal("[]", list["default"]!.ToJsonString());
        var order = Strings(mapping["x-order"]);
        Assert.Equal(Array.IndexOf(order, "ends") + 1, Array.IndexOf(order, "constants"));
        Assert.Equal(["column", "value"], Strings(mapping["properties"]!["constants"]!["items"]!["x-order"]));
        Assert.Equal("common.json#/$defs/id", mapping["properties"]!["constants"]!["items"]!["properties"]!["column"]!["$ref"]!.GetValue<string>());

        var binding = JsonNode.Parse(File.ReadAllText(Path.Combine(Fixtures.RepoRoot, "schemas", "v1", "entity.json")))!["properties"]!["bindings"]!["items"]!;
        var bindingOrder = Strings(binding["x-order"]);
        Assert.Equal(Array.IndexOf(bindingOrder, "delete") + 1, Array.IndexOf(bindingOrder, "discriminatorValue"));
        Assert.Equal("""["string","integer"]""", binding["properties"]!["discriminatorValue"]!["type"]!.ToJsonString());
    }

    [Fact]
    public void Each_new_key_fits_the_schema_and_a_malformed_one_does_not()
    {
        // The four ways, and an entry with only its end (decision 38: one way per entry is MQ4009, not the schema).
        Valid("mapping.json", MappingWith($$"""{"end":"{{JobEnd}}"}"""));
        Valid("mapping.json", MappingWith($$"""{"end":"{{JobEnd}}","foreignKey":"{{JobKey}}"}"""));
        Valid("mapping.json", MappingWith($$"""{"end":"{{JobEnd}}","foreignKeys":["{{JobKey}}","{{PartKey}}"]}"""));
        Valid("mapping.json", MappingWith($$"""{"end":"{{JobEnd}}","types":[{"entity":"{{Entity}}","foreignKey":"{{JobKey}}"}]}"""));
        Valid("mapping.json", MappingWith($$"""{"end":"{{JobEnd}}","idColumn":"{{JobColumn}}","typeColumn":"{{PartColumn}}","referencesColumn":"{{QuantityColumn}}"}"""));
        Valid("mapping.json", MappingWith($$"""{"end":"{{JobEnd}}","idColumn":"{{JobColumn}}","types":[{"entity":"{{Entity}}","referencesColumn":"{{Entity}}.uid@{{Main}}"}]}"""));
        // Constants and an attribute column on a relation mapping.
        Valid("mapping.json", MappingWith($$"""{"end":"{{JobEnd}}","foreignKey":"{{JobKey}}"}""",
            $$""","attributes":[{"attribute":"{{Entity}}","column":"{{QuantityColumn}}"}],"constants":[{"column":"{{PartColumn}}","value":"AttachedTo"},{"column":"{{JobColumn}}","value":17},{"column":"{{QuantityColumn}}"}]"""));

        Invalid("mapping.json", MappingWith($$"""{"end":"{{JobEnd}}","foreignKeys":[]}"""), "/ends/0/foreignKeys");
        Invalid("mapping.json", MappingWith($$"""{"end":"{{JobEnd}}","foreignKeys":["{{JobKey}}","{{JobKey}}"]}"""), "/ends/0/foreignKeys");
        Invalid("mapping.json", MappingWith($$"""{"end":"{{JobEnd}}","types":[]}"""), "/ends/0/types");
        Invalid("mapping.json", MappingWith($$"""{"end":"{{JobEnd}}","types":[{"entity":"{{Entity}}"}]}"""), "/ends/0/types/0");
        Invalid("mapping.json", MappingWith($$"""{"end":"{{JobEnd}}","types":[{"entity":"{{Entity}}","foreignKey":"{{JobKey}}","referencesColumn":"{{JobColumn}}"}]}"""), "/ends/0/types/0");
        Invalid("mapping.json", MappingWith($$"""{"end":"{{JobEnd}}","types":[{"foreignKey":"{{JobKey}}"}]}"""), "/ends/0/types/0");
        Invalid("mapping.json", MappingWith($$"""{"end":"{{JobEnd}}","idColumn":"attached_id"}"""), "/ends/0/idColumn");
        Invalid("mapping.json", MappingWith($$"""{"end":"{{JobEnd}}","typeStorage":"type-and-id"}"""), "/ends/0");
        Invalid("mapping.json", MappingWith($$"""{"foreignKey":"{{JobKey}}"}"""), "/ends/0");
        // A constant's column is a column id only (decision 44): a physical name, as a binding's constant may take, fails.
        Invalid("mapping.json", MappingWith("", ""","constants":[{"column":"relation_code","value":"AttachedTo"}]"""), "/constants/0/column");
        Invalid("mapping.json", MappingWith("", ""","constants":[{"value":"AttachedTo"}]"""), "/constants/0");
        Invalid("mapping.json", MappingWith("", $$$""","constants":[{"column":"{{{PartColumn}}}","value":{"a":1}}]"""), "/constants/0/value");
        Invalid("mapping.json", MappingWith("", ""","typeStorage":"type-and-id" """), "");

        // A binding's type code is a string or an integer (decision 42).
        static string EntityWith(string value) =>
            $$"""{"kind":"entity","id":"{{Entity}}","name":"Part","bindings":[{"id":"01JBND00000000000000000701","database":"{{Main}}","source":"{{Junction}}","discriminatorValue":{{value}}}]}""";
        Valid("entity.json", EntityWith("\"P\""));
        Valid("entity.json", EntityWith("4"));
        Invalid("entity.json", EntityWith("true"), "/bindings/0/discriminatorValue");
        Invalid("entity.json", EntityWith("1.5"), "/bindings/0/discriminatorValue");
    }

    [Fact]
    public void A_constants_column_is_a_column_reference_while_a_bindings_constant_is_keyed()
    {
        var constant = typeof(MappingConstant).GetProperty(nameof(MappingConstant.Column))!.GetCustomAttribute<ElementRefAttribute>()!;
        Assert.False(constant.Keyed);
        Assert.Equal(["column"], constant.IndexKinds);
        Assert.True(typeof(BindingConstant).GetProperty(nameof(BindingConstant.Column))!.GetCustomAttribute<ElementRefAttribute>()!.Keyed);
        foreach (var name in new[] { nameof(RelationEndMapping.IdColumn), nameof(RelationEndMapping.TypeColumn) })
            Assert.Equal(["column"], typeof(RelationEndMapping).GetProperty(name)!.GetCustomAttribute<ElementRefAttribute>()!.IndexKinds);
        Assert.True(typeof(RelationEndMapping).GetProperty(nameof(RelationEndMapping.ReferencesColumn))!.GetCustomAttribute<ElementRefAttribute>()!.Keyed);
        Assert.True(typeof(RelationEndTypeStorage).GetProperty(nameof(RelationEndTypeStorage.ReferencesColumn))!.GetCustomAttribute<ElementRefAttribute>()!.Keyed);
    }

    // ---- today's files ----

    [Fact]
    public void Todays_mapping_and_entity_files_keep_their_bytes()
    {
        foreach (var fixture in new[] { "billing", "showcase" })
        {
            using var h = new LoaderHarness();
            h.CopyFixture("models", fixture);
            var model = h.Model("model");
            var checkedFiles = 0;
            foreach (var (folder, schema) in new[] { ("mappings", "mapping.json"), ("entities", "entity.json") })
            {
                if (!Directory.Exists(Path.Combine(model, folder)))
                    continue;
                foreach (var file in Directory.EnumerateFiles(Path.Combine(model, folder), "*.json", SearchOption.AllDirectories))
                {
                    var path = "model/" + Path.GetRelativePath(model, file).Replace(Path.DirectorySeparatorChar, '/');
                    var text = h.Read(path);
                    Assert.Equal(text, Encoding.UTF8.GetString(TestServices.Json.Write(JsonNode.Parse(text)!, schema, ".maquettiste/" + path)));
                    checkedFiles++;
                }
            }

            Assert.True(checkedFiles > 0, fixture);
        }
    }

    // ---- the new keys load ----

    [Fact]
    public async Task A_mapping_with_the_new_keys_loads_round_trips_and_indexes_its_references()
    {
        using var h = new LoaderHarness();
        h.CopyFixture("models", "showcase");
        h.Write(MappingPath, MappingWith(
            $$"""{"end":"{{PartEnd}}","referencesColumn":"{{QuantityColumn}}","typeColumn":"{{PartColumn}}","idColumn":"{{JobColumn}}"},{"end":"{{JobEnd}}","types":[{"foreignKey":"{{PartKey}}","entity":"{{Entity}}"}],"foreignKeys":["{{JobKey}}"]}""",
            $$""","constants":[{"value":17,"column":"{{QuantityColumn}}"}]"""));
        await using var store = h.NewStore();
        await store.LoadAsync(Ct);
        var model = store.Current!;
        Assert.DoesNotContain(model.LoadDiagnostics, d => d.Severity == DiagnosticSeverity.Error && (d.FilePath ?? "").Contains("uses-in-main", StringComparison.Ordinal));

        // The canonical writer puts the keys in schema order and keeps them there.
        var canonical = Encoding.UTF8.GetString(TestServices.Json.Write(JsonNode.Parse(h.Read(MappingPath))!, "mapping.json", ".maquettiste/" + MappingPath));
        Assert.Equal(canonical, Encoding.UTF8.GetString(TestServices.Json.Write(JsonNode.Parse(canonical)!, "mapping.json", ".maquettiste/" + MappingPath)));
        var written = JsonNode.Parse(canonical)!;
        Assert.Equal(["end", "idColumn", "typeColumn", "referencesColumn"], written["ends"]![0]!.AsObject().Select(p => p.Key));
        Assert.Equal(["end", "foreignKeys", "types"], written["ends"]![1]!.AsObject().Select(p => p.Key));
        Assert.Equal(["entity", "foreignKey"], written["ends"]![1]!["types"]![0]!.AsObject().Select(p => p.Key));
        Assert.Equal(["ends", "constants"], written.AsObject().Select(p => p.Key).Where(k => k is "ends" or "constants"));
        Assert.Equal($$"""[{"column":"{{QuantityColumn}}","value":17}]""", written["constants"]!.ToJsonString());

        // The records.
        var mapping = model.Get<Mapping>(UsesMapping)!;
        var (id, keys) = (mapping.Ends[0], mapping.Ends[1]);
        Assert.Equal(((string?)null, JobColumn, PartColumn, QuantityColumn, true), (id.ForeignKey, id.IdColumn, id.TypeColumn, id.ReferencesColumn, id.HasWay));
        Assert.Empty(id.ForeignKeys);
        Assert.Empty(id.Types);
        Assert.Equal([JobKey], keys.ForeignKeys);
        Assert.Equal([(Entity, (string?)PartKey, (string?)null)], keys.Types.Select(t => (t.Entity, t.ForeignKey, t.ReferencesColumn)));
        Assert.Equal(17, Assert.Single(mapping.Constants) is { Column: QuantityColumn, Value: { } value } ? value.GetInt32() : -1);
        Assert.False(new RelationEndMapping { End = JobEnd }.HasWay);
        Assert.True(new RelationEndMapping { End = JobEnd, Types = [new RelationEndTypeStorage { Entity = Entity, ForeignKey = JobKey }] }.HasWay);
        Assert.False(new RelationEndMapping { End = JobEnd, Types = [new RelationEndTypeStorage { Entity = Entity, ReferencesColumn = JobColumn }] }.HasWay);

        // Each new key is a reference at its pointer: keys, columns and the type's entity.
        bool Refers(string target, string pointer) => model.ReferencesTo(target).Any(r => r.FromElementId == UsesMapping && r.JsonPointer == pointer);
        Assert.True(Refers(JobKey, "/ends/1/foreignKeys/0"));
        Assert.True(Refers(PartKey, "/ends/1/types/0/foreignKey"));
        Assert.True(Refers(Entity, "/ends/1/types/0/entity"));
        Assert.True(Refers(JobColumn, "/ends/0/idColumn"));
        Assert.True(Refers(PartColumn, "/ends/0/typeColumn"));
        Assert.True(Refers(QuantityColumn, "/ends/0/referencesColumn"));
        Assert.True(Refers(QuantityColumn, "/constants/0/column"));
    }

    // ---- one-type relations stay safe ----

    [Fact]
    public async Task A_one_type_entry_with_no_foreign_key_reports_MQ4009_with_the_missing_key_words_and_resolves()
    {
        using var h = new LoaderHarness();
        h.CopyFixture("models", "showcase");
        h.Write(MappingPath, MappingWith($$"""{"end":"{{JobEnd}}"},{"end":"{{PartEnd}}","foreignKey":"{{PartKey}}"}""", ""","shape":"junction" """));
        await using var store = new ModelStore(h.Options, h.Services() with { Validator = EngineServices.Create(h.Options).Validator });
        await store.LoadAsync(Ct);
        var model = store.Current!;
        Assert.DoesNotContain(model.LoadDiagnostics, d => d.Rule == "MQ1002");

        var found = await ActorKit.ValidateAsync(h, model);
        var mq4009 = Assert.Single(found, d => d.Rule == "MQ4009" && d.ElementId == UsesMapping);
        Assert.Equal(("/ends/0", "End entry 0 names no 'foreignKey'; junction table 'repair_job_parts' is designed, so the entry needs the foreign key that references its end's table."),
            (mq4009.JsonPointer, mq4009.Message));
        Assert.DoesNotContain(found, d => d.Rule is "MQ4011" or "MQ1002" && d.ElementId == UsesMapping);

        // The resolver skips the entry instead of throwing; the other end keeps its key.
        var resolved = Resolution.ResolutionKit.Resolve(model);
        Assert.Contains(resolved.Relations, r => r.Id == Uses);
    }

    [Fact]
    public async Task An_ignored_one_type_mapping_still_checks_its_entries()
    {
        // The schema required each entry's foreignKey on an ignored mapping too (MQ1002); the entry checks run before ignore stops the rest.
        using var h = new LoaderHarness();
        h.CopyFixture("models", "showcase");
        h.Write(MappingPath, MappingWith($$"""{"end":"{{JobEnd}}"},{"end":"{{PartEnd}}","foreignKey":"{{PartKey}}","idColumn":"{{JobColumn}}"}""", ""","ignore":true"""));
        await using var store = new ModelStore(h.Options, h.Services() with { Validator = EngineServices.Create(h.Options).Validator });
        await store.LoadAsync(Ct);
        var found = await ActorKit.ValidateAsync(h, store.Current!);
        Assert.Equal(
            [
                ("/ends/0", "End entry 0 names no 'foreignKey'; junction table 'repair_job_parts' is designed, so the entry needs the foreign key that references its end's table."),
                ("/ends/1/idColumn", "End entry 1 of relation 'uses' sets 'idColumn', which needs a relation with an end of several types."),
            ],
            found.Where(d => d.Rule == "MQ4009" && d.ElementId == UsesMapping).Select(d => (d.JsonPointer, d.Message)).Order());
    }

    [Fact]
    public async Task Deleting_a_designed_junctions_keys_keeps_todays_plans()
    {
        using var h = new LoaderHarness();
        h.CopyFixture("models", "showcase");
        await using var store = h.NewStore();
        await store.LoadAsync(Ct);

        // remove-references refuses each entry's key with the words of a required member; delete-dependents removes each entry whole.
        var clear = await store.GetDeletePlanAsync([Junction], DeleteResolution.RemoveReferences, Ct);
        Assert.Equal(SaveOutcome.Invalid, clear.Outcome);
        Assert.Equal(
            [
                ("/ends/0/foreignKey", "its reference to key fk_repair_job_parts_repair_job_id of table repair_job_parts at /ends/0/foreignKey: the reference is required, so it cannot be cleared.", "MQ2001"),
                ("/ends/1/foreignKey", "its reference to key fk_repair_job_parts_part_id of table repair_job_parts at /ends/1/foreignKey: the reference is required, so it cannot be cleared.", "MQ2001"),
            ],
            clear.Refused.Where(r => r.Id == UsesMapping).Select(r => (r.Pointer, r.Why, r.Rule)).Order());

        var cascade = await store.GetDeletePlanAsync([Junction], DeleteResolution.DeleteDependents, Ct);
        Assert.Equal(SaveOutcome.Saved, cascade.Outcome);
        Assert.Equal(["/ends/0", "/ends/1"], cascade.Removes.Where(r => r.Id == UsesMapping).Select(r => r.Pointer).Order(StringComparer.Ordinal));
        Assert.DoesNotContain(cascade.Deletes, d => d.Id == UsesMapping);
    }
}
