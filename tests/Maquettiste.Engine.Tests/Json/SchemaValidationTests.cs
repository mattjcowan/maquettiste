using System.Text.Json;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Json;
using Maquettiste.Engine.Model;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Json;

public sealed class SchemaValidationTests
{
    private static readonly ISchemaRegistry Schemas = TestServices.Schemas;

    private static IReadOnlyList<Diagnostic> Evaluate(string schemaFile, string json, string path = "model/x.json")
    {
        using var doc = JsonDocument.Parse(json);
        return Schemas.Evaluate(schemaFile, doc.RootElement, path);
    }

    private static IReadOnlyList<Diagnostic> EvaluateFixture(string schemaFile, string fixture) =>
        Evaluate(schemaFile, File.ReadAllText(Fixtures.Path("spec-examples", fixture)), "tests/fixtures/spec-examples/" + fixture);

    [Fact]
    public void Spec_entity_example_is_valid() => Assert.Empty(EvaluateFixture("entity.json", "invoice.entity.json"));

    [Fact]
    public void Spec_relation_example_is_valid() => Assert.Empty(EvaluateFixture("relation.json", "membership.relation.json"));

    [Fact]
    public void Dangling_type_name_is_rejected()
    {
        var diagnostics = EvaluateFixture("entity.json", "dangling-type.entity.json");

        Assert.NotEmpty(diagnostics);
        Assert.All(diagnostics, d =>
        {
            Assert.Equal("MQ1002", d.Rule);
            Assert.Equal(DiagnosticSeverity.Error, d.Severity);
            Assert.Equal("tests/fixtures/spec-examples/dangling-type.entity.json", d.FilePath);
            Assert.Equal("01JAX3K9V2Q7M4T8W1Z5C6B0DE", d.ElementId);
        });
        Assert.Contains(diagnostics, d => d.JsonPointer == "/attributes/2/type");
    }

    [Fact]
    public void Phase_3_lifecycle_field_is_rejected()
    {
        var diagnostics = EvaluateFixture("entity.json", "invoice-with-lifecycle.entity.json");

        Assert.Contains(diagnostics, d => d.JsonPointer == "/lifecycle");
    }

    [Theory]
    [InlineData("""{"kind":"entity","id":"01jax3k9v2q7m4t8w1z5c6b0de","name":"Invoice"}""", "/id")]
    [InlineData("""{"kind":"entity","id":"01JAX3K9V2Q7M4T8W1Z5C6B0DL","name":"Invoice"}""", "/id")]
    [InlineData("""{"kind":"entity","id":"81JAX3K9V2Q7M4T8W1Z5C6B0DE","name":"Invoice"}""", "/id")]
    [InlineData("""{"kind":"entity","id":"01JAX3K9V2Q7M4T8W1Z5C6B0DE","name":"Invoice","colour":"red"}""", "/colour")]
    [InlineData("""{"kind":"entity","id":"01JAX3K9V2Q7M4T8W1Z5C6B0DE","name":"2Invoice"}""", "/name")]
    [InlineData("""{"kind":"table","id":"01JAX3K9V2Q7M4T8W1Z5C6B0DE","name":"Invoice"}""", "/kind")]
    [InlineData("""{"kind":"entity","id":"01JAX3K9V2Q7M4T8W1Z5C6B0DE","name":"Invoice","stereotypes":["Audited"]}""", "/stereotypes/0")]
    [InlineData("""{"kind":"entity","id":"01JAX3K9V2Q7M4T8W1Z5C6B0DE","name":"Invoice","key":{"attributes":[],"strategy":"uuid-v7"}}""", "/key/attributes")]
    public void Invalid_entities_are_rejected_at_the_offending_pointer(string json, string pointer)
    {
        var diagnostics = Evaluate("entity.json", json);

        Assert.Contains(diagnostics, d => d.JsonPointer == pointer);
    }

    [Fact]
    public void Missing_required_name_is_rejected()
    {
        var diagnostics = Evaluate("entity.json", """{"kind":"entity","id":"01JAX3K9V2Q7M4T8W1Z5C6B0DE"}""");

        Assert.Contains(diagnostics, d => d.Message.Contains("name", StringComparison.Ordinal));
    }

    [Fact]
    public void Relation_needs_at_least_two_ends_with_ids()
    {
        Assert.NotEmpty(Evaluate("relation.json", """{"kind":"relation","id":"01JAX4R0MEMBERSH1PRE1AT10N","name":"r","ends":[{"id":"01JAX4R3VSEREND0000000000A","entity":"01JAX4E0VSERENT1TY00000001","role":"a"}]}"""));
        Assert.NotEmpty(Evaluate("relation.json", """{"kind":"relation","id":"01JAX4R0MEMBERSH1PRE1AT10N","name":"r","ends":[{"entity":"01JAX4E0VSERENT1TY00000001","role":"a"},{"entity":"01JAX4E0VSERENT1TY00000001","role":"b"}]}"""));
        Assert.NotEmpty(Evaluate("relation.json", """{"kind":"relation","id":"01JAX4R0MEMBERSH1PRE1AT10N","name":"r","ends":[{"id":"01JAX4R3VSEREND0000000000A","entity":"01JAX4E0VSERENT1TY00000001","role":"a","max":2},{"id":"01JAX4R4TEAMEND0000000000A","entity":"01JAX4E0VSERENT1TY00000001","role":"b"}]}"""));
    }

    [Fact]
    public void Synthesized_table_overlay_may_omit_its_name_but_a_designed_table_may_not()
    {
        const string overlay = """{"kind":"table","id":"01JAX3K9V2Q7M4T8W1Z5C6B0DE","database":"01JAX3JZ0H6N2P5R8S1T4V7W9Y","origin":"synthesized","entity":"01JAX3KA1B2C3D4E5F6G7H8J9K","columns":[{"id":"01JAX3KB2C3D4E5F6G7H8J9KAM","attribute":"01JAX3KC3D4E5F6G7H8J9KAMBN","nativeType":"citext"}]}""";
        const string designed = """{"kind":"table","id":"01JAX3K9V2Q7M4T8W1Z5C6B0DE","database":"01JAX3JZ0H6N2P5R8S1T4V7W9Y"}""";
        const string designedColumnWithoutType = """{"kind":"table","id":"01JAX3K9V2Q7M4T8W1Z5C6B0DE","name":"t","database":"01JAX3JZ0H6N2P5R8S1T4V7W9Y","columns":[{"id":"01JAX3KB2C3D4E5F6G7H8J9KAM","name":"c"}]}""";

        Assert.Empty(Evaluate("table.json", overlay));
        Assert.NotEmpty(Evaluate("table.json", designed));
        Assert.NotEmpty(Evaluate("table.json", designedColumnWithoutType));
    }

    [Fact]
    public void Mapping_names_exactly_one_of_entity_and_relation()
    {
        const string head = """{"kind":"mapping","id":"01JAX3K9V2Q7M4T8W1Z5C6B0DE","name":"m","database":"01JAX3JZ0H6N2P5R8S1T4V7W9Y" """;

        Assert.Empty(Evaluate("mapping.json", head + ""","entity":"01JAX3KA1B2C3D4E5F6G7H8J9K"}"""));
        Assert.NotEmpty(Evaluate("mapping.json", head + "}"));
        Assert.NotEmpty(Evaluate("mapping.json", head + ""","entity":"01JAX3KA1B2C3D4E5F6G7H8J9K","relation":"01JAX3KB2C3D4E5F6G7H8J9KAM"}"""));
    }

    [Fact]
    public void Pair_units_need_a_companion()
    {
        const string pack = """{"name":"csharp-dapper","version":"1.0.0","engine":">=1.0 <2.0","units":[{"id":"entity","template":"entity.scriban","for":"each entity","mode":"pair"%s}]}""";

        Assert.NotEmpty(Evaluate("pack.json", pack.Replace("%s", "", StringComparison.Ordinal)));
        Assert.Empty(Evaluate("pack.json", pack.Replace("%s", ""","companion":{"template":"entity.partial.scriban","output":"x.cs"}""", StringComparison.Ordinal)));
        Assert.NotEmpty(Evaluate("pack.json", pack.Replace("each entity", "each process", StringComparison.Ordinal).Replace("%s", "", StringComparison.Ordinal)));
    }

    [Fact]
    public void Spec_pack_example_is_valid()
    {
        const string pack = """
            {
              "name": "sql-ddl", "version": "1.0.0", "engine": ">=1.0 <2.0",
              "parameters": { "schemaPerPackage": false },
              "units": [ { "id": "table", "template": "table.scriban", "for": "each table",
                "where": { "database": "main", "notTags": ["external"] },
                "output": "db/{{ table.schema }}/tables/{{ table.name }}.sql", "mode": "overwrite", "formatter": "sql" } ]
            }
            """;

        Assert.Empty(Evaluate("pack.json", pack));
    }

    [Fact]
    public void Settings_reject_unknown_convention_values()
    {
        Assert.Empty(Evaluate("maquettiste.json", """{"formatVersion":1,"conventions":{"tableCase":"snake","relationsWithAttributes":"promoted"}}"""));
        Assert.NotEmpty(Evaluate("maquettiste.json", """{"formatVersion":1,"conventions":{"tableCase":"shouting"}}"""));
        Assert.NotEmpty(Evaluate("maquettiste.json", """{"conventions":{}}"""));
    }

    [Fact]
    public void Batch_update_needs_an_id_hash_and_element()
    {
        const string hash = "0000000000000000000000000000000000000000000000000000000000000000";

        Assert.Empty(Evaluate("batch.json", $$$"""{"operations":[{"op":"update","id":"01JAX3K9V2Q7M4T8W1Z5C6B0DE","expectedHash":"{{{hash}}}","element":{"kind":"entity"}}]}"""));
        Assert.NotEmpty(Evaluate("batch.json", """{"operations":[{"op":"update","element":{"kind":"entity"}}]}"""));
        Assert.NotEmpty(Evaluate("batch.json", """{"operations":[{"op":"create"}]}"""));
    }

    [Fact]
    public void Every_element_kind_has_a_schema_whose_kind_constant_matches()
    {
        foreach (var kind in KindInfo.All)
        {
            using var doc = JsonDocument.Parse(Schemas.GetFileBytes(kind.SchemaFile));
            var constant = doc.RootElement.GetProperty("properties").GetProperty("kind").GetProperty("const").GetString();
            Assert.Equal(kind.Name, constant);
        }
    }

    [Fact]
    public void Embedded_schemas_are_the_files_under_schemas_v1()
    {
        var onDisk = Directory.GetFiles(Path.Combine(Fixtures.RepoRoot, "schemas", "v1"), "*.json").Select(p => Path.GetFileName(p)).Order(StringComparer.Ordinal).ToList();

        Assert.Equal(onDisk, Schemas.FileNames);
        foreach (var name in onDisk)
            Assert.Equal(File.ReadAllBytes(Path.Combine(Fixtures.RepoRoot, "schemas", "v1", name)), Schemas.GetFileBytes(name).ToArray());
        Assert.Equal(KindInfo.All.Select(k => k.SchemaFile).Concat(KindInfo.DocumentSchemaFiles).Order(StringComparer.Ordinal), Schemas.FileNames);
    }
    [Fact]
    public void Verbatim_spec_examples_fail_only_for_the_documented_adaptations()
    {
        // SPEC Sections 6 and 7, copied verbatim. The phase 1 schemas reject them for exactly three documented reasons
        // (docs/engineering/spec-errata.md E1 to E3): illustrative ids that are not ULIDs (D1), the phase 3 lifecycle field (D7)
        // and relation ends without an id (D33). Any other failure means the schemas drifted from the SPEC.
        static IReadOnlyList<string> Pointers(string schema, string file) =>
            [.. EvaluateFixture(schema, "verbatim/" + file).Select(d => d.JsonPointer!).Distinct().Order(StringComparer.Ordinal)];

        Assert.Equal(
            ["/attributes/2/type", "/attributes/2/type/ref", "/attributes/3/type", "/attributes/3/type/ref", "/category", "/lifecycle"],
            Pointers("entity.json", "invoice.entity.json"));
        Assert.Equal(
            ["/attributes/0/id", "/attributes/1/id", "/ends/0", "/ends/0/entity", "/ends/1", "/ends/1/entity", "/id"],
            Pointers("relation.json", "membership.relation.json"));
    }

    [Theory]
    [InlineData("PII")]
    [InlineData("team:billing")]
    [InlineData("v2.1")]
    [InlineData("billing")]
    public void Tags_are_free_form_labels(string tag) =>
        Assert.Empty(Evaluate("entity.json", $$"""{"kind":"entity","id":"01JAX3K9V2Q7M4T8W1Z5C6B0DE","name":"Invoice","tags":["{{tag}}"]}"""));

    [Theory]
    [InlineData("")]
    [InlineData("two words")]
    [InlineData("tab\\there")]
    public void Tags_reject_empty_labels_whitespace_and_control_characters(string tag) =>
        Assert.Contains(
            Evaluate("entity.json", $$"""{"kind":"entity","id":"01JAX3K9V2Q7M4T8W1Z5C6B0DE","name":"Invoice","tags":["{{tag}}"]}"""),
            d => d.JsonPointer == "/tags/0");

    [Fact]
    public void Empty_navigation_means_not_navigable_and_is_valid()
    {
        const string relation = """{"kind":"relation","id":"01JAX4R0MEMBERSH1PRE1AT10N","name":"r","ends":[{"id":"01JAX4R3VSEREND0000000000A","entity":"01JAX4E0VSERENT1TY00000001","role":"a","navigation":""},{"id":"01JAX4R4TEAMEND0000000000A","entity":"01JAX4E0VSERENT1TY00000001","role":"b","navigation":"bees"}]}""";

        Assert.Empty(Evaluate("relation.json", relation));
        Assert.NotEmpty(Evaluate("relation.json", relation.Replace("\"bees\"", "\"2bees\"", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData(""","origin":"synthesized","entity":"01JAX3KA1B2C3D4E5F6G7H8J9K" """, true)]
    [InlineData(""","origin":"synthesized","entity":"01JAX3KA1B2C3D4E5F6G7H8J9K","attribute":"01JAX3KB2C3D4E5F6G7H8J9KAM" """, true)]
    [InlineData(""","origin":"synthesized","relation":"01JAX3KA1B2C3D4E5F6G7H8J9K" """, true)]
    [InlineData(""","origin":"synthesized","enum":"01JAX3KA1B2C3D4E5F6G7H8J9K" """, true)]
    [InlineData(""","origin":"synthesized" """, false)]
    [InlineData(""","origin":"synthesized","entity":"01JAX3KA1B2C3D4E5F6G7H8J9K","relation":"01JAX3KB2C3D4E5F6G7H8J9KAM" """, false)]
    [InlineData(""","origin":"synthesized","attribute":"01JAX3KB2C3D4E5F6G7H8J9KAM","enum":"01JAX3KA1B2C3D4E5F6G7H8J9K" """, false)]
    [InlineData(""","name":"t","entity":"01JAX3KA1B2C3D4E5F6G7H8J9K" """, false)]
    [InlineData(""","name":"t","origin":"imported","enum":"01JAX3KA1B2C3D4E5F6G7H8J9K" """, false)]
    [InlineData(""","name":"t" """, true)]
    public void Synthesized_tables_target_exactly_one_entity_relation_or_enum_and_others_target_none(string fields, bool valid)
    {
        var diagnostics = Evaluate("table.json", """{"kind":"table","id":"01JAX3K9V2Q7M4T8W1Z5C6B0DE","database":"01JAX3JZ0H6N2P5R8S1T4V7W9Y" """ + fields + "}");

        Assert.Equal(valid, diagnostics.Count == 0);
    }

    [Fact]
    public void Mapping_binds_a_relation_to_existing_foreign_keys()
    {
        const string mapping = """
            {"kind":"mapping","id":"01JAX3K9V2Q7M4T8W1Z5C6B0DE","name":"m","database":"01JAX3JZ0H6N2P5R8S1T4V7W9Y","relation":"01JAX3KA1B2C3D4E5F6G7H8J9K",
             "shape":"junction","junctionTable":"01JAX3KB2C3D4E5F6G7H8J9KAM",
             "ends":[{"end":"01JAX3KC3D4E5F6G7H8J9KAMBN","foreignKey":"01JAX3KD4E5F6G7H8J9KAMBNCP"}]}
            """;

        Assert.Empty(Evaluate("mapping.json", mapping));
        Assert.Empty(Evaluate("mapping.json", """{"kind":"mapping","id":"01JAX3K9V2Q7M4T8W1Z5C6B0DE","name":"m","database":"01JAX3JZ0H6N2P5R8S1T4V7W9Y","relation":"01JAX3KA1B2C3D4E5F6G7H8J9K","foreignKey":"01JAX3KD4E5F6G7H8J9KAMBNCP"}"""));
        Assert.NotEmpty(Evaluate("mapping.json", mapping.Replace("\"foreignKey\"", "\"fk\"", StringComparison.Ordinal)));
    }

    [Fact]
    public void Stereotypes_need_an_immutable_key_and_may_have_any_label_as_name()
    {
        Assert.Empty(Evaluate("stereotype.json", """{"kind":"stereotype","id":"01JAX3K9V2Q7M4T8W1Z5C6B0DE","key":"soft-delete","name":"Soft delete"}"""));
        Assert.NotEmpty(Evaluate("stereotype.json", """{"kind":"stereotype","id":"01JAX3K9V2Q7M4T8W1Z5C6B0DE","name":"soft-delete"}"""));
        Assert.NotEmpty(Evaluate("stereotype.json", """{"kind":"stereotype","id":"01JAX3K9V2Q7M4T8W1Z5C6B0DE","key":"Soft Delete","name":"x"}"""));
    }

    [Fact]
    public void Output_allowlist_is_named_allow_as_in_the_spec()
    {
        Assert.Empty(Evaluate("maquettiste.json", """{"formatVersion":1,"outputs":{"allow":[{"path":"db","commit":true}],"deny":["db/tmp/**"]}}"""));
        Assert.NotEmpty(Evaluate("maquettiste.json", """{"formatVersion":1,"outputs":{"roots":[{"path":"db"}]}}"""));
    }
}
