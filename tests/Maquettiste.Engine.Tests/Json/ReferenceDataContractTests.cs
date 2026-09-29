using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Maquettiste.Engine.Json;
using Maquettiste.Engine.Loading;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Pipeline;
using Maquettiste.Engine.Tests.Loading;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Json;

/// <summary>
/// The contract of reference types, seeds and locale shards (reference-types-seeds-localization.md section 5 step 1, Appendix B):
/// the schemas accept the design's examples, the canonical writer lays seed rows out one per line, and a fixture with a reference
/// type, a seed and a locale shard loads without any diagnostic.
/// </summary>
public sealed class ReferenceDataContractTests
{
    private const string TypePath = ".maquettiste/model/reference-types/unit-of-measure.json";
    private const string SeedPath = ".maquettiste/model/seeds/unit-of-measure/unit-of-measure.json";
    private const string ShardPath = ".maquettiste/model/locales/fr/_reference-data.json";

    private static readonly ISchemaRegistry Schemas = TestServices.Schemas;
    private static readonly ICanonicalJson Canonical = TestServices.Json;

    private static string FixtureRoot => Fixtures.Path("models", "reference-data", ".maquettiste");

    private static byte[] FixtureBytes(string repoPath) => File.ReadAllBytes(Path.Combine(FixtureRoot, repoPath[".maquettiste/".Length..]));

    private static IReadOnlyList<string> Pointers(string schemaFile, string json)
    {
        using var doc = JsonDocument.Parse(json);
        return [.. Schemas.Evaluate(schemaFile, doc.RootElement, "model/x.json").Select(d => d.JsonPointer ?? "")];
    }

    private static string Write(string json, string schemaFile, string path) =>
        Encoding.UTF8.GetString(Canonical.Write(JsonNode.Parse(json)!, schemaFile, path));

    [Theory]
    [InlineData("reference-type.json", TypePath)]
    [InlineData("seed.json", SeedPath)]
    [InlineData("locale.json", ShardPath)]
    public void Design_examples_are_valid_and_canonical(string schemaFile, string repoPath)
    {
        var bytes = FixtureBytes(repoPath);

        Assert.Empty(Pointers(schemaFile, Encoding.UTF8.GetString(bytes)));
        Assert.True(Canonical.IsCanonical(bytes, schemaFile, repoPath));
        Assert.Equal(bytes, Canonical.Write(JsonNode.Parse(bytes)!, schemaFile, repoPath));
    }

    [Fact]
    public void Settings_with_localization_and_reference_data_are_valid() =>
        Assert.Empty(Pointers("maquettiste.json", File.ReadAllText(Path.Combine(FixtureRoot, "maquettiste.json"))));

    [Theory]
    [InlineData("""{"kind":"reference-type","id":"01JBM9S346Q3D25VT4F5V37E3S","name":"U","label":{"id":"01JBQKFBF5KZNWJ47TAN9ZT24M"}}""", "")]
    [InlineData("""{"kind":"reference-type","id":"01JBM9S346Q3D25VT4F5V37E3S","name":"U","code":{"id":"01JB3E28JT97KB6CQ643DZVMXX","type":"decimal"},"label":{"id":"01JBQKFBF5KZNWJ47TAN9ZT24M"}}""", "/code/type")]
    [InlineData("""{"kind":"reference-type","id":"01JBM9S346Q3D25VT4F5V37E3S","name":"U","code":{"id":"01JB3E28JT97KB6CQ643DZVMXX"},"label":{"id":"01JBQKFBF5KZNWJ47TAN9ZT24M"},"storage":{"main":{"strategy":"check"}}}""", "/storage")]
    [InlineData("""{"kind":"seed","id":"01JB9EE0ZBGJ09TQM83XSSSS6Y","name":"S","target":"01JBM9S346Q3D25VT4F5V37E3S","columns":[]}""", "/columns")]
    [InlineData("""{"kind":"seed","id":"01JB9EE0ZBGJ09TQM83XSSSS6Y","name":"S","target":"01JBM9S346Q3D25VT4F5V37E3S","columns":["code","code"]}""", "/columns")]
    [InlineData("""{"kind":"seed","id":"01JB9EE0ZBGJ09TQM83XSSSS6Y","name":"S","target":"01JBM9S346Q3D25VT4F5V37E3S","columns":["name"]}""", "/columns/0")]
    [InlineData("""{"kind":"seed","id":"01JB9EE0ZBGJ09TQM83XSSSS6Y","name":"S","target":"01JBM9S346Q3D25VT4F5V37E3S","columns":["code"],"rows":[{"values":["kg"]}]}""", "/rows/0")]
    [InlineData("""{"kind":"locale-shard","locale":"fr","scope":"somewhere"}""", "/scope")]
    [InlineData("""{"kind":"locale-shard","locale":"fr","scope":"root","entries":{"01JBM9S346Q3D25VT4F5V37E3S":{}}}""", "/entries/01JBM9S346Q3D25VT4F5V37E3S")]
    [InlineData("""{"kind":"locale-shard","locale":"fr","scope":"root","entries":{"01JBM9S346Q3D25VT4F5V37E3S":{"label":"x","src":{"label":"XYZ"}}}}""", "/entries/01JBM9S346Q3D25VT4F5V37E3S/src/label")]
    [InlineData("""{"kind":"locale-shard","locale":"fr","scope":"root","entries":{"01JBM9S346Q3D25VT4F5V37E3S":{"name":"x"}}}""", "/entries/01JBM9S346Q3D25VT4F5V37E3S/name")]
    public void Invalid_documents_are_rejected(string json, string pointer)
    {
        var kind = JsonNode.Parse(json)!["kind"]!.GetValue<string>();
        var schemaFile = kind == "locale-shard" ? "locale.json" : kind + ".json";
        Assert.Contains(pointer, Pointers(schemaFile, json));
    }

    [Theory]
    [InlineData(""","referenceData":{"groupBy":"tag:"}""", "/referenceData/groupBy")]
    [InlineData(""","referenceData":{"strategies":{"s":{"collections":{"db2":true}}}}""", "/referenceData/strategies/s/collections")]
    [InlineData(""","localization":{"locales":["fr"]}""", "/localization")]
    [InlineData(""","localization":{"defaultLocale":"fr_FR"}""", "/localization/defaultLocale")]
    [InlineData(""","conventions":{"referenceStorage":{"strategy":""}}""", "/conventions/referenceStorage/strategy")]
    public void Invalid_settings_are_rejected(string fields, string pointer) =>
        Assert.Contains(pointer, Pointers("maquettiste.json", """{"formatVersion":1""" + fields + "}"));

    [Fact]
    public void Batch_translate_requires_its_fields_and_is_refused_until_its_handler_lands()
    {
        Assert.Empty(Pointers("batch.json", """{"operations":[{"op":"translate","locale":"fr","id":"01JBM9S346Q3D25VT4F5V37E3S","field":"displayName","value":"Unité"}]}"""));
        Assert.Empty(Pointers("batch.json", """{"operations":[{"op":"translate","locale":"fr","id":"01JBM9S346Q3D25VT4F5V37E3S","field":"description","value":null}]}"""));
        Assert.NotEmpty(Pointers("batch.json", """{"operations":[{"op":"translate","locale":"fr","id":"01JBM9S346Q3D25VT4F5V37E3S","field":"name","value":"x"}]}"""));
        Assert.NotEmpty(Pointers("batch.json", """{"operations":[{"op":"translate","id":"01JBM9S346Q3D25VT4F5V37E3S","field":"label","value":"x"}]}"""));

        var parsed = new BatchParser(Schemas, Canonical).Parse("""{"operations":[{"op":"translate","locale":"fr","id":"01JBM9S346Q3D25VT4F5V37E3S","field":"label","value":"x"}]}"""u8);
        Assert.Null(parsed.Batch);
        Assert.Equal("/operations/0/op", Assert.Single(parsed.Diagnostics).JsonPointer);
    }

    [Fact]
    public void Snapshot_columns_admit_reference_types()
    {
        const string json = """
            {"formatVersion":1,"database":"01JBR2K8Q6W4T9V3X5Z7N1M0PD","dialect":"postgresql","tables":[{"key":"t","name":"t","columns":[
              {"key":"c","name":"unit","type":"reference","length":8,"referenceType":"01JBM9S346Q3D25VT4F5V37E3S","strategy":"check"}]}]}
            """;
        var pointers = Pointers("snapshot.json", json);
        Assert.DoesNotContain(pointers, p => p.Contains("/columns/", StringComparison.Ordinal));
        Assert.Contains("/tables/0/columns/0/type", Pointers("snapshot.json", json.Replace("\"reference\"", "\"lookup\"", StringComparison.Ordinal)));
    }

    [Fact]
    public void Seed_rows_are_written_one_per_line_with_trailing_nulls_trimmed_and_numbers_normalized()
    {
        const string input = """
            {"kind":"seed","id":"01JB9EE0ZBGJ09TQM83XSSSS6Y","name":"UnitOfMeasure","target":"01JBM9S346Q3D25VT4F5V37E3S",
             "columns":["code","label","01JBNPZX45HY43KWJRP1XPA7Z3","01JBDJ8FSSZ5AWSH8VHTPRE95B"],
             "rows":[
               {"values":["kg","Kilogram",1e3,"kg"],"id":"01JBS3C4DWA7N36096Q14DR9GP"},
               {"id":"01JBQY77ZXYYK596NGYA1DQ91K","values":["g","Gram",1000.0,null]},
               {"id":"01JB5GQAPENECFSECZP11HYGCP","values":["pinch","Pinch",0.360,null,null]},
               {"id":"01JBDJ8FSSZ5AWSH8VHTPRE95C","values":[null,null]},
               {"id":"01JBDJ8FSSZ5AWSH8VHTPRE95D","values":[{"currency":"EUR","amount":5.50},["a","b"],-0.0,1.5E-3,"é\"q"]}
             ]}
            """;

        var text = Write(input, "seed.json", SeedPath);

        const string expected = """
            {
              "$schema": "../../../.schema/v1/seed.json",
              "kind": "seed",
              "id": "01JB9EE0ZBGJ09TQM83XSSSS6Y",
              "name": "UnitOfMeasure",
              "target": "01JBM9S346Q3D25VT4F5V37E3S",
              "columns": [
                "code",
                "label",
                "01JBNPZX45HY43KWJRP1XPA7Z3",
                "01JBDJ8FSSZ5AWSH8VHTPRE95B"
              ],
              "rows": [
                { "id": "01JBS3C4DWA7N36096Q14DR9GP", "values": ["kg", "Kilogram", 1000, "kg"] },
                { "id": "01JBQY77ZXYYK596NGYA1DQ91K", "values": ["g", "Gram", 1000] },
                { "id": "01JB5GQAPENECFSECZP11HYGCP", "values": ["pinch", "Pinch", 0.36] },
                { "id": "01JBDJ8FSSZ5AWSH8VHTPRE95C" },
                { "id": "01JBDJ8FSSZ5AWSH8VHTPRE95D", "values": [{ "amount": 5.5, "currency": "EUR" }, ["a", "b"], 0, 0.0015, "é\"q"] }
              ]
            }

            """;
        Assert.Equal(expected.ReplaceLineEndings("\n"), text);
        var bytes = Encoding.UTF8.GetBytes(text);
        Assert.True(Canonical.IsCanonical(bytes, "seed.json", SeedPath));
        Assert.Equal(text, Write(text, "seed.json", SeedPath));

        // A row form other than the canonical one is not canonical.
        Assert.False(Canonical.IsCanonical(Encoding.UTF8.GetBytes(text.Replace("[\"g\", \"Gram\"", "[\"g\",\"Gram\"", StringComparison.Ordinal)), "seed.json", SeedPath));
        Assert.False(Canonical.IsCanonical(Encoding.UTF8.GetBytes(text.Replace("1000]", "1000.0]", StringComparison.Ordinal)), "seed.json", SeedPath));
    }

    [Fact]
    public void A_seed_record_serializes_to_the_same_bytes_as_its_file()
    {
        var bytes = FixtureBytes(SeedPath);
        var seed = ElementReader.Read<Seed>(JsonDocument.Parse(bytes).RootElement);

        Assert.Equal(3, seed.Rows.Count);
        Assert.Equal(3, seed.Rows[2].Values.Count);
        Assert.Equal(bytes, Canonical.Serialize<Element>(seed, "seed.json", SeedPath));
    }

    [Fact]
    public void A_locale_shard_round_trips_with_entries_sorted_and_fields_in_order()
    {
        const string input = """
            {"entries":{"01JBS3C4DWA7N36096Q14DR9GP":{"src":{"label":"a93f0d7b"},"label":"Kilogramme"},
              "01JBM9S346Q3D25VT4F5V37E3S":{"src":{"pluralName":"0d4b7e11","displayName":"5c1e9a02"},"pluralName":"Unités de mesure","displayName":"Unité de mesure"}},
             "scope":"reference-data","locale":"fr","kind":"locale-shard"}
            """;

        var text = Write(input, "locale.json", ShardPath);

        Assert.Equal(FixtureBytes(ShardPath), Encoding.UTF8.GetBytes(text));
        var shard = ElementReader.Read<LocaleShard>(JsonDocument.Parse(text).RootElement);
        Assert.Equal("Kilogramme", shard.Entries["01JBS3C4DWA7N36096Q14DR9GP"].Label);
        Assert.Equal(Encoding.UTF8.GetBytes(text), Canonical.Serialize(shard, "locale.json", ShardPath));
    }

    [Theory]
    [InlineData("1e3", "1000")]
    [InlineData("1000.0", "1000")]
    [InlineData("0.360", "0.36")]
    [InlineData("-0.0", "0")]
    [InlineData("1.5E-3", "0.0015")]
    [InlineData("-12.5e1", "-125")]
    [InlineData("100", "100")]
    [InlineData("0", "0")]
    public void Seed_cell_numbers_take_their_shortest_plain_decimal_text(string input, string expected) =>
        Assert.Equal(expected, CanonicalJson.CanonicalNumber(input));

    [Fact]
    public async Task Reference_data_fixture_loads_without_any_diagnostic()
    {
        using var harness = new LoaderHarness();
        harness.CopyFixture("models", "reference-data");

        var result = await harness.NewLoader().LoadAsync(new LoadRequest(null, null, false), null, TestContext.Current.CancellationToken);
        var model = result.Snapshot;

        Assert.Empty(model.LoadDiagnostics);
        var type = Assert.Single(model.All<ReferenceType>(), t => t.Name == "UnitOfMeasure");
        var seed = Assert.Single(model.All<Seed>(), s => s.Name == "UnitOfMeasure");
        Assert.Equal(TypePath, model.GetDocument(type.Id)!.Path);
        Assert.Equal(SeedPath, model.GetDocument(seed.Id)!.Path);
        Assert.Equal(type.Id, seed.Target);
        Assert.Equal(8, type.Code.Length);
        Assert.Equal("check", type.Storage["*"].Strategy);
        Assert.Equal(11, model.Documents.Count); // the locale shard is not an element
        Assert.Equal("fr", model.Settings.Localization!.Fallbacks["fr-CA"][0]);
        Assert.True(model.Settings.ReferenceData.Strategies["lookup-table"].Collections.For("sqlite"));
        Assert.False(model.Settings.ReferenceData.Strategies["native"].Collections.For("sqlite"));
        Assert.True(model.Settings.ReferenceData.Strategies["native"].Collections.For("postgresql"));
        Assert.False(model.Settings.ReferenceData.Strategies["check"].Collections.For("postgresql"));
        Assert.Equal("lookup-table", model.Settings.Conventions.ReferenceStorage!.Strategy);
    }

    [Fact]
    public async Task A_malformed_locale_shard_is_reported_and_not_read_as_an_element()
    {
        using var harness = new LoaderHarness();
        harness.CopyFixture("models", "reference-data");
        await File.WriteAllTextAsync(harness.Model("model/locales/fr/_reference-data.json"), """{"kind":"locale-shard","locale":"fr","scope":"nowhere"}""", TestContext.Current.CancellationToken);

        var result = await harness.NewLoader().LoadAsync(new LoadRequest(null, null, false), null, TestContext.Current.CancellationToken);

        var diagnostics = result.Snapshot.LoadDiagnostics.Where(d => d.Rule == "MQ1002").ToList();
        Assert.NotEmpty(diagnostics);
        Assert.All(diagnostics, d => Assert.Equal((ShardPath, "/scope"), (d.FilePath, d.JsonPointer)));
        Assert.Equal(11, result.Snapshot.Documents.Count);
    }

    [Fact]
    public void Seeds_live_in_a_folder_named_after_their_target()
    {
        var seed = new Seed { Id = "01JB9EE0ZBGJ09TQM83XSSSS6Y", Name = "Demo", Target = "01JBM9S346Q3D25VT4F5V37E3S", Columns = ["code"] };

        Assert.Equal("model/seeds/unit-of-measure", ModelPaths.ConventionalFolder(seed, _ => null, _ => "unit-of-measure"));
        Assert.True(ModelPaths.MatchesConvention(seed, "model/seeds/unit-of-measure/demo.json", "model/seeds/unit-of-measure"));
        Assert.True(ModelPaths.MatchesConvention(seed, "model/seeds/unit-of-measure-v37e3s/demo.json", "model/seeds/unit-of-measure"));
        Assert.False(ModelPaths.MatchesConvention(seed, "model/seeds/other/demo.json", "model/seeds/unit-of-measure"));
        Assert.Equal(ModelFileKind.LocaleShard, ModelPaths.Classify("model/locales/fr/billing/catalog.json"));
        Assert.Equal(ModelFileKind.Element, ModelPaths.Classify("model/seeds/unit-of-measure/demo.json"));
    }
}
