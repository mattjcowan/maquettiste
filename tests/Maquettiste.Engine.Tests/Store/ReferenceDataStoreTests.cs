using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Generation;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Tests.Loading;
using Maquettiste.Engine.Validation;

namespace Maquettiste.Engine.Tests.Store;

/// <summary>
/// Loading, indexing, validation and the ownership rules of reference types and seeds (reference-types-seeds-localization.md
/// sections 2.2 and 2.7) over <c>tests/fixtures/models/reference-data</c>, with the real validator.
/// </summary>
public sealed class ReferenceDataStoreTests
{
    private const string UnitOfMeasure = "01JBM9S346Q3D25VT4F5V37E3S";
    private const string UnitSeed = "01JB9EE0ZBGJ09TQM83XSSSS6Y";
    private const string KgRow = "01JBS3C4DWA7N36096Q14DR9GP";
    private const string GramRow = "01JBQY77ZXYYK596NGYA1DQ91K";
    private const string Allergen = "01JRDA00000000000000000001";
    private const string NutRow = "01JRDA00000000000000000012";
    private const string Recipe = "01JRDE00000000000000000001";
    private const string Ingredient = "01JRDE00000000000000000002";
    private const string PreferredUnit = "01JRDE00000000000000000024";
    private const string Contains = "01JRDR00000000000000000001";
    private const string Substitutes = "01JRDR00000000000000000002";
    private const string SubstituteEnd = "01JRDR00000000000000000014";
    private const string RecipeSeed = "01JRDS00000000000000000001";
    private const string IngredientSeed = "01JRDS00000000000000000002";
    private const string ContainsSeed = "01JRDS00000000000000000003";
    private const string FlourRow = "01JRDS00000000000000000201";
    private const string ShardPath = "model/locales/fr/_reference-data.json";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<(LoaderHarness Harness, ModelStore Store)> OpenAsync(Action<LoaderHarness>? prepare = null)
    {
        var harness = new LoaderHarness();
        harness.CopyFixture("models", "reference-data");
        prepare?.Invoke(harness);
        var store = new ModelStore(harness.Options, harness.Services() with { Validator = EngineServices.Create(harness.Options).Validator });
        await store.LoadAsync(Ct);
        return (harness, store);
    }

    private static byte[] Edit(ModelStore store, string id, Action<JsonObject> change)
    {
        var node = JsonNode.Parse(store.Current!.GetDocument(id)!.Json.GetRawText())!.AsObject();
        change(node);
        return Encoding.UTF8.GetBytes(node.ToJsonString());
    }

    [Fact]
    public async Task Reference_data_fixture_validates_without_any_diagnostic()
    {
        var (h, store) = await OpenAsync();
        using var _ = h;
        await using var __ = store;

        var report = await store.ValidateAsync(ValidationScope.All, Ct);

        Assert.Empty(WithoutCompleteness(report));
    }

    [Fact]
    public async Task The_index_records_code_uses_end_cells_rows_and_owning_targets()
    {
        var (h, store) = await OpenAsync();
        using var _ = h;
        await using var __ = store;
        var model = store.Current!;

        string[] Uses(string id) => [.. model.ReferencesTo(id).Select(r => r.FromElementId + r.JsonPointer)];

        // Where-used of a row reaches defaults, allowedValues and seed cells, in path order.
        Assert.Equal(
            new[]
            {
                Ingredient + "/attributes/2/default", Ingredient + "/attributes/3/validation/allowedValues/1", Ingredient + "/attributes/5/default/0",
                Contains + "/attributes/1/default",
                ContainsSeed + "/rows/0/values/3",
                IngredientSeed + "/rows/0/values/2", IngredientSeed + "/rows/0/values/5/0", IngredientSeed + "/rows/1/values/2",
                IngredientSeed + "/rows/1/values/5/0", IngredientSeed + "/rows/2/values/2", IngredientSeed + "/rows/2/values/3",
                IngredientSeed + "/rows/3/values/2", IngredientSeed + "/rows/3/values/5/0",
            }.Order(StringComparer.Ordinal),
            Uses(GramRow).Order(StringComparer.Ordinal));
        Assert.Contains(Ingredient + "/attributes/3/validation/allowedValues/0", Uses(KgRow));
        Assert.Contains(IngredientSeed + "/rows/2/values/6", Uses(FlourRow)); // an end cell names a row id
        Assert.Contains(ContainsSeed + "/rows/0/values/1", Uses(FlourRow));
        Assert.Contains("01JRDA00000000000000000010/rows/2/values/2", Uses(NutRow)); // a row names another row of its own type

        var owning = Assert.Single(model.ReferencesTo(Ingredient), r => r.Owning);
        Assert.Equal((IngredientSeed, "/target", "target"), (owning.FromElementId, owning.JsonPointer, owning.Field));
        Assert.DoesNotContain(model.ReferencesTo(Ingredient), r => r.Owning && r.FromElementId != IngredientSeed);

        Assert.True(model.TryGetEntry(FlourRow, out var row));
        Assert.Equal(("row", IngredientSeed, "/rows/0"), (row.Kind, row.OwnerId, row.JsonPointer));
        Assert.True(model.TryGetEntry("01JB3E28JT97KB6CQ643DZVMXX", out var code));
        Assert.Equal(("reference-field", UnitOfMeasure, "/code"), (code.Kind, code.OwnerId, code.JsonPointer));

        var seedRow = Assert.Single(model.Summaries(), s => s.Id == IngredientSeed);
        Assert.Equal((Ingredient, 4, (int?)null), (seedRow.Target, seedRow.RowCount, seedRow.FieldCount));
        var typeRow = Assert.Single(model.Summaries(), s => s.Id == UnitOfMeasure);
        Assert.Equal(((string?)null, (int?)null, 2), (typeRow.Target, typeRow.RowCount, typeRow.FieldCount));
    }

    [Fact]
    public async Task Renaming_a_code_is_one_batch_over_the_reverse_index()
    {
        var (h, store) = await OpenAsync();
        using var _ = h;
        await using var __ = store;
        var model = store.Current!;
        var owners = model.ReferencesTo(GramRow).Select(r => r.FromElementId).Append(UnitSeed).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        var operations = owners.Select(id =>
        {
            var document = model.GetDocument(id)!;
            var text = document.Json.GetRawText().Replace("\"g\"", "\"gram\"", StringComparison.Ordinal);
            using var json = System.Text.Json.JsonDocument.Parse(text);
            return new BatchOperation(BatchOp.Update, id, document.Hash, json.RootElement.Clone());
        }).ToList();

        var result = await store.ApplyBatchAsync(new ModelBatch(operations), ChangeSource.Editor, Ct);

        Assert.Equal(SaveOutcome.Saved, result.Outcome);
        string[] Pointers(ModelSnapshot m) => [.. m.ReferencesTo(GramRow).Select(r => r.FromElementId + r.JsonPointer).Order(StringComparer.Ordinal)];
        Assert.Equal(Pointers(model), Pointers(store.Current!));
        Assert.Empty(WithoutCompleteness(await store.ValidateAsync(ValidationScope.All, Ct)));
    }

    [Fact]
    public async Task A_code_change_without_its_uses_is_refused()
    {
        var (h, store) = await OpenAsync();
        using var _ = h;
        await using var __ = store;
        var seed = store.Current!.GetDocument(UnitSeed)!;
        var bytes = Encoding.UTF8.GetBytes(seed.Json.GetRawText().Replace("\"g\", \"Gram\"", "\"gr\", \"Gram\"", StringComparison.Ordinal)
            .Replace("[\"g\",\"Gram\"", "[\"gr\",\"Gram\"", StringComparison.Ordinal));

        var result = await store.SaveAsync(UnitSeed, bytes, seed.Hash, ChangeSource.Editor, Ct);

        Assert.Equal(SaveOutcome.Invalid, result.Outcome);
        Assert.Contains(result.Diagnostics, d => d.Rule == "MQ3019");
        Assert.Contains(result.Diagnostics, d => d.Rule == "MQ7009");
    }

    [Fact]
    public async Task Deleting_a_target_deletes_its_seeds_and_their_translations()
    {
        var (h, store) = await OpenAsync(harness =>
        {
            // Translations of the relation, one of its attributes and one of its seed's rows, beside the existing entries.
            var shard = JsonNode.Parse(File.ReadAllText(harness.Model(ShardPath)))!.AsObject();
            var entries = shard["entries"]!.AsObject();
            entries[Contains] = new JsonObject { ["displayName"] = "contient" };
            entries["01JRDR00000000000000000021"] = new JsonObject { ["displayName"] = "quantité" };
            entries["01JRDS00000000000000000301"] = new JsonObject { ["label"] = "ligne" };
            File.WriteAllText(harness.Model(ShardPath), shard.ToJsonString());
        });
        using var _ = h;
        await using var __ = store;
        var relation = store.Current!.GetDocument(Contains)!;

        var result = await store.DeleteAsync(Contains, relation.Hash, DeleteResolution.Refuse, ChangeSource.Editor, Ct);

        Assert.Equal(SaveOutcome.Saved, result.Outcome);
        Assert.Contains(ContainsSeed, result.Changes!.Deleted);
        Assert.False(h.Exists("model/seeds/contains/contains.json"));
        Assert.Null(store.Current!.GetDocument(ContainsSeed));
        var shardAfter = JsonNode.Parse(h.Read(ShardPath))!["entries"]!.AsObject();
        Assert.Equal([UnitOfMeasure, KgRow], shardAfter.Select(kv => kv.Key).Order(StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task Rows_named_by_other_seeds_refuse_their_targets_delete()
    {
        var (h, store) = await OpenAsync();
        using var _ = h;
        await using var __ = store;
        var recipe = store.Current!.GetDocument(Recipe)!;

        var refused = await store.DeleteAsync(Recipe, recipe.Hash, DeleteResolution.Refuse, ChangeSource.Editor, Ct);

        Assert.Equal(SaveOutcome.Referenced, refused.Outcome);
        Assert.DoesNotContain(refused.Referrers, r => r.Owning || r.FromElementId == RecipeSeed);
        Assert.Contains(refused.Referrers, r => r.FromElementId == ContainsSeed && r.JsonPointer == "/rows/0/values/0");
        Assert.True(h.Exists("model/seeds/recipe/recipe.json"));

        // Clearing the references instead leaves required end cells empty: the delete is invalid.
        var cleared = await store.DeleteAsync(Recipe, recipe.Hash, DeleteResolution.RemoveReferences, ChangeSource.Editor, Ct);
        Assert.NotEqual(SaveOutcome.Saved, cleared.Outcome);
        Assert.True(h.Exists("model/entities/recipe.json"));
    }

    [Fact]
    public async Task Removing_an_attribute_or_an_end_drops_its_seed_column_in_the_same_save()
    {
        var (h, store) = await OpenAsync();
        using var _ = h;
        await using var __ = store;
        var ingredient = store.Current!.GetDocument(Ingredient)!;

        var saved = await store.SaveAsync(Ingredient, Edit(store, Ingredient, n => n["attributes"]!.AsArray().RemoveAt(3)), ingredient.Hash, ChangeSource.Editor, Ct);

        Assert.Equal(SaveOutcome.Saved, saved.Outcome);
        var seed = store.Current!.Get<Seed>(IngredientSeed)!;
        Assert.DoesNotContain(PreferredUnit, seed.Columns);
        Assert.Equal(6, seed.Columns.Count);
        Assert.Equal("[\"salt\",\"Salt\",\"g\",[],[\"g\"]]", System.Text.Json.JsonSerializer.Serialize(seed.Rows[1].Values));

        var substitutes = store.Current!.GetDocument(Substitutes)!;
        var deleted = await store.DeleteAsync(Substitutes, substitutes.Hash, DeleteResolution.Refuse, ChangeSource.Editor, Ct);

        Assert.Equal(SaveOutcome.Saved, deleted.Outcome);
        seed = store.Current!.Get<Seed>(IngredientSeed)!;
        Assert.DoesNotContain(SubstituteEnd, seed.Columns);
        Assert.Equal(5, seed.Rows[2].Values.Count);
        Assert.Empty(WithoutCompleteness(await store.ValidateAsync(ValidationScope.All, Ct)));
    }

    [Fact]
    public async Task Reference_types_and_seeds_are_created_and_saved_with_etags_in_batches()
    {
        var (h, store) = await OpenAsync();
        using var _ = h;
        await using var __ = store;
        const string Season = "01JRDT00000000000000000001";
        const string SeasonSeed = "01JRDT00000000000000000010";
        var type = JsonNode.Parse($$"""{ "kind": "reference-type", "id": "{{Season}}", "name": "Season", "code": { "id": "01JRDT00000000000000000002", "length": 8 }, "label": { "id": "01JRDT00000000000000000003", "length": 32 } }""");
        var seed = JsonNode.Parse($$"""{ "kind": "seed", "id": "{{SeasonSeed}}", "name": "Season", "target": "{{Season}}", "columns": ["code", "label"], "rows": [ { "id": "01JRDT00000000000000000011", "values": ["spring", "Spring"] } ] }""");
        static System.Text.Json.JsonElement Json(JsonNode node) => System.Text.Json.JsonDocument.Parse(node.ToJsonString()).RootElement.Clone();

        var created = await store.ApplyBatchAsync(new ModelBatch(
            [new BatchOperation(BatchOp.Create, null, null, Json(type!)), new BatchOperation(BatchOp.Create, null, null, Json(seed!))]), ChangeSource.Editor, Ct);

        Assert.Equal(SaveOutcome.Saved, created.Outcome);
        Assert.True(h.Exists("model/reference-types/season.json"));
        Assert.True(h.Exists("model/seeds/season/season.json"));
        var document = store.Current!.GetDocument(SeasonSeed)!;
        var edited = Edit(store, SeasonSeed, n => n["rows"]!.AsArray().Add(JsonNode.Parse("""{ "id": "01JRDT00000000000000000012", "values": ["summer", "Summer"] }""")));

        var stale = await store.SaveAsync(SeasonSeed, edited, "0000000000000000", ChangeSource.Editor, Ct);
        var saved = await store.SaveAsync(SeasonSeed, edited, document.Hash, ChangeSource.Editor, Ct);

        Assert.Equal(SaveOutcome.Conflict, stale.Outcome);
        Assert.Equal(SaveOutcome.Saved, saved.Outcome);
        Assert.Contains("{ \"id\": \"01JRDT00000000000000000012\", \"values\": [\"summer\", \"Summer\"] }", h.Read("model/seeds/season/season.json"), StringComparison.Ordinal);
        Assert.Equal(2, store.Current!.Summaries().Single(s => s.Id == SeasonSeed).RowCount);
    }

    [Fact]
    public async Task Patched_and_full_indexes_agree_on_code_references()
    {
        var (h, store) = await OpenAsync();
        using var _ = h;
        await using var __ = store;
        const string Pantry = "01JRDE00000000000000000003";
        var entity = Encoding.UTF8.GetBytes($$"""{ "kind": "entity", "id": "{{Pantry}}", "name": "Pantry", "key": { "attributes": ["01JRDE00000000000000000031"], "strategy": "ulid" }, "attributes": [ { "id": "01JRDE00000000000000000031", "name": "id", "type": "ulid", "required": true }, { "id": "01JRDE00000000000000000032", "name": "unit", "type": { "ref": "{{UnitOfMeasure}}" }, "default": "kg" } ] }""");

        var created = await store.CreateAsync(entity, ChangeSource.Editor, Ct);

        Assert.Equal(SaveOutcome.Saved, created.Outcome);
        Assert.True(store.Current!.Index.Patched); // a change that touches no seed, reference type or seed column is patched
        Assert.Contains(store.Current!.ReferencesTo(KgRow), r => r.FromElementId == Pantry && r.JsonPointer == "/attributes/1/default");
        AssertSameReferences(store.Current!);

        var seed = store.Current!.GetDocument(IngredientSeed)!;
        var saved = await store.SaveAsync(IngredientSeed, Edit(store, IngredientSeed, n => n["rows"]!.AsArray().RemoveAt(3)), seed.Hash, ChangeSource.Editor, Ct);

        Assert.Equal(SaveOutcome.Saved, saved.Outcome);
        Assert.False(store.Current!.Index.Patched); // a seed change rebuilds in full
        AssertSameReferences(store.Current!);
    }

    private static void AssertSameReferences(ModelSnapshot model)
    {
        var full = ModelSnapshot.Create(model.Documents, model.Settings, model.SettingsHash, [], [], 1);
        Assert.Equal(full.Summaries(), model.Summaries());
        foreach (var document in model.Documents)
        {
            Assert.Equal(full.ReferencesFrom(document.Element.Id), model.ReferencesFrom(document.Element.Id));
            foreach (var reference in model.ReferencesFrom(document.Element.Id))
                Assert.Equal(full.ReferencesTo(reference.ToId), model.ReferencesTo(reference.ToId));
        }
    }

    [Fact]
    public async Task A_seed_of_bulk_size_is_reported()
    {
        var (h, store) = await OpenAsync(harness =>
        {
            var rows = string.Join(",\n", Enumerable.Range(0, ReferenceDataRules.MaxRows + 1).Select(i =>
                $"    {{ \"id\": \"01JBBK{i.ToString("D20", CultureInfo.InvariantCulture)}\", \"values\": [\"c{i.ToString(CultureInfo.InvariantCulture)}\", \"L\"] }}"));
            File.WriteAllText(harness.Model("model/seeds/allergen/bulk.json"),
                "{\n  \"$schema\": \"../../../.schema/v1/seed.json\",\n  \"kind\": \"seed\",\n  \"id\": \"01JBBJ00000000000000000000\",\n  \"name\": \"Bulk\",\n  \"target\": \""
                + Allergen + "\",\n  \"columns\": [\n    \"code\",\n    \"label\"\n  ],\n  \"rows\": [\n" + rows + "\n  ]\n}\n");
        });
        using var _ = h;
        await using var __ = store;

        var report = await store.ValidateAsync(ValidationScope.All, Ct);

        var d = Assert.Single(WithoutCompleteness(report));
        Assert.Equal(("MQ7104", DiagnosticSeverity.Warning, "/rows"), (d.Rule, d.Severity, d.JsonPointer));
    }

    /// <summary>The fixture translates a few texts only: its per-shard completeness infos (MQ7204, MQ7206) are left out here.</summary>
    private static List<Diagnostic> WithoutCompleteness(ValidationReport report) =>
        [.. report.Diagnostics.Where(d => d.Rule is not ("MQ7204" or "MQ7206"))];
}
