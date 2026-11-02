using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Generation;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Tests.Loading;
using Maquettiste.Engine.Validation;
using Maquettiste.Testing;

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
    public async Task Removing_a_stereotype_column_drops_its_keyed_seed_columns_and_member_links()
    {
        // next-phase-2-design.md section 3.1 (D10): a seed names an inherited column by its keyed id <tableId>.<memberId>; removing the
        // member from its stereotype drops that column with its cells, and the redefines and excludes entries that name the removed
        // members, in the same save. Entries naming a member that stays are kept.
        var (h, store) = await OpenAsync(StampedTables);
        using var _ = h;
        await using var __ = store;
        var stamped = store.Current!.GetDocument(StampedId)!;

        var saved = await store.SaveAsync(StampedId, Edit(store, StampedId, n =>
        {
            n["columns"]!.AsArray().RemoveAt(1);
            n.Remove("indexes");
        }), stamped.Hash, ChangeSource.Editor, Ct);

        Assert.Equal(SaveOutcome.Saved, saved.Outcome);
        var model = store.Current!;
        var seed = model.Get<Seed>(ShelfSeed)!;
        Assert.Equal([ShelfId, ShelfLabelId], seed.Columns);
        Assert.Equal(["[1,\"top\"]", "[2,\"bottom\"]"], seed.Rows.Select(r => System.Text.Json.JsonSerializer.Serialize(r.Values)));
        var shelves = model.Get<Table>(ShelvesId)!;
        Assert.Equal([StampedAtId], shelves.Redefines.Select(r => r.Member));
        Assert.Empty(model.Get<Table>(RacksId)!.Excludes);
        Assert.Equal([StampedAtId], model.Get<Table>(BinsId)!.Excludes);
        Assert.Equal(
            new[] { StampedId, ShelfSeed, ShelvesId, RacksId }.Order(StringComparer.Ordinal),
            saved.Changes!.Changed.Where(c => c.Kind != "migration").Select(c => c.Id).Order(StringComparer.Ordinal));
        Assert.DoesNotContain(model.ReferencesTo(StampedById), r => true);
    }

    [Fact]
    public async Task Usage_lists_designed_columns_typed_by_the_type()
    {
        // next-phase-2-design.md section 3.2 (D24): the usage lists the designed, extra and inherited columns whose own typeRef names the
        // type, ordinal by table id then column id, with the effective storage choice in their database; the projected columns of
        // attributes typed by it stay covered by their attributes, and a column typed by another type is not listed.
        var (h, store) = await OpenAsync(TypedPantry);
        using var _ = h;
        await using var __ = store;
        Assert.DoesNotContain((await store.ValidateAsync(ValidationScope.All, Ct)).Diagnostics, d => d.Severity == DiagnosticSeverity.Error);

        var usage = (await store.GetReferenceTypeUsageAsync(UnitOfMeasure, Ct))!;

        Assert.Contains(usage.Usages, u => u.Attribute == PreferredUnit && u.Owner == Ingredient);
        var recipes = Recipe + "@" + PantryDatabaseId;
        Assert.Equal(
            new[]
            {
                (recipes, PantryStockUnitId, PantryDatabaseId, false),
                (PantryItemsId, PantryItemsId + "." + MeasuredUnitId, PantryDatabaseId, false),
                (PantryItemsId, PantryUnitId, PantryDatabaseId, true),
            },
            usage.Columns.Select(c => (c.Table, c.Column, c.Database, c.Required)));
        Assert.All(usage.Columns, c =>
        {
            Assert.Null(c.Domain);
            Assert.Equal("native", c.Storage?.Strategy);
            Assert.Null(c.BoundTable);
        });
        var allergen = (await store.GetReferenceTypeUsageAsync(Allergen, Ct))!;
        Assert.Equal([(PantryItemsId, PantryAllergenId)], allergen.Columns.Select(c => (c.Table, c.Column)));
        Assert.Equal("lookup-table", Assert.Single(allergen.Columns).Storage?.Strategy);
        Assert.Null(await store.GetReferenceTypeUsageAsync(PantryItemsId, Ct));
    }

    private const string PantryDatabaseId = "01JRDV00000000000000000001";
    private const string MeasuredId = "01JRDV00000000000000000010";
    private const string MeasuredUnitId = "01JRDV00000000000000000011";
    private const string PantryItemsId = "01JRDV00000000000000000020";
    private const string PantryUnitId = "01JRDV00000000000000000022";
    private const string PantryAllergenId = "01JRDV00000000000000000023";
    private const string RecipeOverlayId = "01JRDV00000000000000000030";
    private const string PantryStockUnitId = "01JRDV00000000000000000031";

    /// <summary>
    /// A database <c>pantry</c> where the designed table pantry_items types its unit by UnitOfMeasure and its allergen by Allergen, and
    /// inherits a nullable unit_code typed by UnitOfMeasure from the stereotype <c>measured</c>; Recipe alone maps there, and the overlay
    /// of its table adds an extra column stock_unit typed by UnitOfMeasure. Allergen states no storage; the project's lookup-table choice
    /// applies to it, and UnitOfMeasure's own <c>native</c> for the pantry to the other.
    /// </summary>
    private static void TypedPantry(LoaderHarness h)
    {
        h.Write("model/databases/pantry/database.json", $$"""
            { "kind": "database", "id": "{{PantryDatabaseId}}", "name": "pantry", "dialect": "postgresql", "byConvention": "none" }
            """);
        h.Write("model/mappings/recipe-in-pantry.json", $$"""
            { "kind": "mapping", "id": "01JRDV00000000000000000040", "name": "Recipe in pantry", "database": "{{PantryDatabaseId}}",
              "entity": "{{Recipe}}" }
            """);
        // The pantry stores units natively, which holds Ingredient's collection of units there, so MQ7006 stays quiet.
        var units = h.Model("model/reference-types/unit-of-measure.json");
        File.WriteAllText(units, File.ReadAllText(units).Replace("\"storage\": {", $$"""
            "storage": { "{{PantryDatabaseId}}": { "strategy": "native" },
            """, StringComparison.Ordinal));
        h.Write("model/vocabularies/stereotypes/measured.json", $$"""
            { "kind": "stereotype", "id": "{{MeasuredId}}", "key": "measured", "name": "Measured", "appliesTo": ["table"],
              "columns": [ { "id": "{{MeasuredUnitId}}", "name": "unit_code", "type": "string", "length": 8,
                             "typeRef": { "ref": "{{UnitOfMeasure}}" } } ] }
            """);
        h.Write("model/databases/pantry/tables/pantry-items.json", $$"""
            { "kind": "table", "id": "{{PantryItemsId}}", "name": "pantry_items", "database": "{{PantryDatabaseId}}", "stereotypes": ["measured"],
              "columns": [ { "id": "01JRDV00000000000000000021", "name": "id", "type": "uuid", "nullable": false },
                           { "id": "{{PantryUnitId}}", "name": "unit", "type": "string", "typeRef": { "ref": "{{UnitOfMeasure}}" }, "nullable": false },
                           { "id": "{{PantryAllergenId}}", "name": "allergen", "type": "string", "typeRef": { "ref": "{{Allergen}}" } } ],
              "primaryKey": { "columns": ["01JRDV00000000000000000021"] } }
            """);
        h.Write("model/databases/pantry/tables/recipes.json", $$"""
            { "kind": "table", "id": "{{RecipeOverlayId}}", "database": "{{PantryDatabaseId}}", "origin": "synthesized", "entity": "{{Recipe}}",
              "columns": [ { "id": "{{PantryStockUnitId}}", "name": "stock_unit", "type": "string", "length": 8,
                             "typeRef": { "ref": "{{UnitOfMeasure}}" } } ] }
            """);
    }

    private const string StoreDatabaseId = "01JRDT00000000000000000001";
    private const string StampedId = "01JRDT00000000000000000010";
    private const string StampedAtId = "01JRDT00000000000000000011";
    private const string StampedById = "01JRDT00000000000000000012";
    private const string StampedIndexId = "01JRDT00000000000000000013";
    private const string ShelvesId = "01JRDT00000000000000000020";
    private const string ShelfId = "01JRDT00000000000000000021";
    private const string ShelfLabelId = "01JRDT00000000000000000022";
    private const string RacksId = "01JRDT00000000000000000040";
    private const string BinsId = "01JRDT00000000000000000050";
    private const string ShelfSeed = "01JRDT00000000000000000030";

    /// <summary>
    /// A database <c>store</c> with a stereotype <c>stamped</c> (columns stamped_at and stamped_by, an index on stamped_by) carried by three
    /// designed tables: shelves redefines both columns and has a seed naming its inherited stamped_by, racks excludes stamped_by and the
    /// index, bins excludes stamped_at.
    /// </summary>
    private static void StampedTables(LoaderHarness h)
    {
        h.Write("model/databases/store/database.json", $$"""
            { "kind": "database", "id": "{{StoreDatabaseId}}", "name": "store", "dialect": "postgresql" }
            """);
        h.Write("model/vocabularies/stereotypes/stamped.json", $$"""
            { "kind": "stereotype", "id": "{{StampedId}}", "key": "stamped", "name": "Stamped", "appliesTo": ["table"],
              "columns": [ { "id": "{{StampedAtId}}", "name": "stamped_at", "type": "datetimeoffset" },
                           { "id": "{{StampedById}}", "name": "stamped_by", "type": "string", "length": 40 } ],
              "indexes": [ { "id": "{{StampedIndexId}}", "columns": [ { "column": "{{StampedById}}" } ] } ] }
            """);
        string Table(string id, string name, string links) => $$"""
            { "kind": "table", "id": "{{id}}", "name": "{{name}}", "database": "{{StoreDatabaseId}}", "stereotypes": ["stamped"],
              "columns": [ { "id": "{{id[..^1]}}1", "name": "id", "type": "int32", "nullable": false },
                           { "id": "{{id[..^1]}}2", "name": "label", "type": "string", "length": 40 } ],
              "primaryKey": { "columns": ["{{id[..^1]}}1"] }{{links}} }
            """;
        h.Write("model/databases/store/tables/shelves.json", Table(ShelvesId, "shelves", $$"""
            , "redefines": [ { "member": "{{StampedAtId}}", "comment": "When it was stamped." }, { "member": "{{StampedById}}", "comment": "Who stamped it." } ]
            """));
        h.Write("model/databases/store/tables/racks.json", Table(RacksId, "racks", $$""", "excludes": ["{{StampedById}}", "{{StampedIndexId}}"]"""));
        h.Write("model/databases/store/tables/bins.json", Table(BinsId, "bins", $$""", "excludes": ["{{StampedAtId}}"]"""));
        h.Write("model/seeds/shelves/shelves.json", $$"""
            { "kind": "seed", "id": "{{ShelfSeed}}", "name": "Shelves", "target": "{{ShelvesId}}",
              "columns": ["{{ShelfId}}", "{{ShelfLabelId}}", "{{ShelvesId}}.{{StampedById}}"],
              "rows": [ { "id": "01JRDT00000000000000000031", "values": [1, "top", "ann"] },
                        { "id": "01JRDT00000000000000000032", "values": [2, "bottom", "bo"] } ] }
            """);
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
    public async Task A_new_reference_type_seed_declares_code_label_and_description()
    {
        var (h, store) = await OpenAsync();
        using var _ = h;
        await using var __ = store;
        const string Speed = "01JRDF00000000000000000001";
        var type = Encoding.UTF8.GetBytes($$"""{ "kind": "reference-type", "id": "{{Speed}}", "name": "Speed", "code": { "id": "01JRDF00000000000000000002" }, "label": { "id": "01JRDF00000000000000000003" } }""");
        Assert.Equal(SaveOutcome.Saved, (await store.CreateAsync(type, ChangeSource.Editor, Ct)).Outcome);

        // create_element without columns gets the three built-ins; CreateSeedAsync makes one and then returns it.
        var bare = Encoding.UTF8.GetBytes($$"""{ "kind": "seed", "name": "SpeedRows", "target": "{{Speed}}", "rows": [] }""");
        var created = await store.CreateAsync(bare, ChangeSource.Cli, Ct);
        Assert.Equal(SaveOutcome.Saved, created.Outcome);
        Assert.Equal(["code", "label", "description"], store.Current!.Get<Seed>(created.Id!)!.Columns.Select(c => c.ToString()));
        var again = await store.CreateSeedAsync(Speed, ChangeSource.Cli, Ct);
        Assert.Equal(created.Id, again!.Id);
        Assert.Null(again.Changes);
        Assert.Null(await store.CreateSeedAsync(Recipe, ChangeSource.Cli, Ct));
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

    [Fact]
    public async Task A_uuid_code_is_a_uuid_in_canonical_form()
    {
        const string Batch = "01JRDW00000000000000000001";
        var (h, store) = await OpenAsync(harness =>
        {
            File.WriteAllText(harness.Model("model/reference-types/batch.json"),
                "{\n  \"$schema\": \"../../.schema/v1/reference-type.json\",\n  \"kind\": \"reference-type\",\n  \"id\": \"" + Batch
                + "\",\n  \"name\": \"Batch\",\n  \"code\": {\n    \"id\": \"01JRDW00000000000000000002\",\n    \"type\": \"uuid\"\n  },\n  \"label\": {\n    \"id\": \"01JRDW00000000000000000003\"\n  }\n}\n");
            Directory.CreateDirectory(harness.Model("model/seeds/batch"));
            File.WriteAllText(harness.Model("model/seeds/batch/batch.json"),
                "{\n  \"$schema\": \"../../../.schema/v1/seed.json\",\n  \"kind\": \"seed\",\n  \"id\": \"01JRDW00000000000000000010\",\n  \"name\": \"Batch\",\n  \"target\": \""
                + Batch + "\",\n  \"columns\": [\n    \"code\",\n    \"label\"\n  ],\n  \"rows\": [\n"
                + "    { \"id\": \"01JRDW00000000000000000011\", \"values\": [\"0190a3b4-5c6d-7e8f-9a0b-1c2d3e4f5a6b\", \"First\"] },\n"
                + "    { \"id\": \"01JRDW00000000000000000012\", \"values\": [\"0190A3B4-5C6D-7E8F-9A0B-1C2D3E4F5A6C\", \"Upper\"] },\n"
                + "    { \"id\": \"01JRDW00000000000000000013\", \"values\": [\"batch-3\", \"Not a UUID\"] },\n"
                + "    { \"id\": \"01JRDW00000000000000000014\", \"values\": [42, \"A number\"] }\n  ]\n}\n");
        });
        using var _ = h;
        await using var __ = store;

        var report = await store.ValidateAsync(ValidationScope.All, Ct);

        var found = WithoutCompleteness(report).Select(d => (d.Rule, d.JsonPointer ?? "", d.Severity)).ToList();
        Assert.Equal(
            new[]
            {
                ("MQ7013", "/rows/1/values/0", DiagnosticSeverity.Error),
                ("MQ7013", "/rows/2/values/0", DiagnosticSeverity.Error),
                ("MQ7004", "/rows/3/values/0", DiagnosticSeverity.Error),
            },
            found);
        var upper = Assert.Single(report.Diagnostics, d => d.JsonPointer == "/rows/1/values/0");
        Assert.Contains("write it as '0190a3b4-5c6d-7e8f-9a0b-1c2d3e4f5a6c'", upper.Message, StringComparison.Ordinal);
        Assert.Equal("uuid", store.Current!.Get<ReferenceType>(Batch)!.Code.Type);
    }

    private const string PinchRow = "01JB5GQAPENECFSECZP11HYGCP";

    /// <summary>Gives <c>UnitOfMeasure</c> former codes (next-phase-2-design.md section 3.5) in its file on disk.</summary>
    private static void FormerCodes(LoaderHarness harness, params (string Code, string ReplacedBy)[] codes)
    {
        var path = harness.Model("model/reference-types/unit-of-measure.json");
        var node = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        node["formerCodes"] = new JsonArray([.. codes.Select(c => (JsonNode)new JsonObject { ["code"] = c.Code, ["replacedBy"] = c.ReplacedBy })]);
        File.WriteAllBytes(path, TestServices.Json.Write(node, "reference-type.json", ".maquettiste/model/reference-types/unit-of-measure.json"));
    }

    [Fact]
    public async Task Import_skips_a_row_whose_code_is_a_former_code()
    {
        var (h, store) = await OpenAsync(harness => FormerCodes(harness, ("gram", GramRow)));
        using var _ = h;
        await using var __ = store;
        Assert.Empty(WithoutCompleteness(await store.ValidateAsync(ValidationScope.All, Ct)));
        const string Csv = "@code,@label,factor,symbol\ngram,Gram again,1,g\noz,Ounce,28.35,oz\n";

        var preview = (await store.ImportSeedCsvAsync(UnitSeed, Csv, false, true, null, ChangeSource.Cli, Ct))!.Preview;

        Assert.Equal(1, preview.Added);
        Assert.Empty(preview.Changed);
        var skipped = Assert.Single(preview.Diagnostics);
        Assert.Equal(("MQ7019", UnitSeed), (skipped.Rule, skipped.ElementId));
        Assert.Contains("Line 2: 'gram' is a former code of reference type UnitOfMeasure", skipped.Message, StringComparison.Ordinal);

        // Under replace, the row that replaced the former code counts as kept: only kg and pinch are left out.
        var replace = (await store.ImportSeedCsvAsync(UnitSeed, Csv, true, true, null, ChangeSource.Cli, Ct))!.Preview;
        Assert.Equal(2, replace.Removed + (replace.Blocked?.Count ?? 0));
        Assert.DoesNotContain(replace.Blocked ?? [], b => (string?)b["id"] == GramRow);

        var applied = await store.ImportSeedCsvAsync(UnitSeed, Csv, false, false, null, ChangeSource.Cli, Ct);

        Assert.Equal(SaveOutcome.Saved, applied!.Outcome);
        var codes = store.Current!.Get<Seed>(UnitSeed)!.Rows.Select(r => r.Values[0].GetString()).ToList();
        Assert.Equal(["kg", "g", "pinch", "oz"], codes);
    }

    [Fact]
    public async Task Deleting_a_row_that_a_former_code_points_to_is_refused()
    {
        var (h, store) = await OpenAsync(harness => FormerCodes(harness, ("dash", PinchRow)));
        using var _ = h;
        await using var __ = store;
        var model = store.Current!;
        var reference = Assert.Single(model.ReferencesTo(PinchRow));
        Assert.Equal((UnitOfMeasure, "/formerCodes/0/replacedBy", "replacedBy"), (reference.FromElementId, reference.JsonPointer, reference.Field));
        var seed = model.GetDocument(UnitSeed)!;

        var result = await store.SaveAsync(UnitSeed, Edit(store, UnitSeed, n => n["rows"]!.AsArray().RemoveAt(2)), seed.Hash, ChangeSource.Editor, Ct);

        Assert.Equal(SaveOutcome.Invalid, result.Outcome);
        var dangling = Assert.Single(result.Diagnostics, d => d.Rule == "MQ2001");
        Assert.Equal((UnitOfMeasure, "/formerCodes/0/replacedBy"), (dangling.ElementId, dangling.JsonPointer));
        Assert.Equal(3, store.Current!.Get<Seed>(UnitSeed)!.Rows.Count);
    }

    /// <summary>The fixture translates a few texts only: its per-shard completeness infos (MQ7204, MQ7206) are left out here.</summary>
    private static List<Diagnostic> WithoutCompleteness(ValidationReport report) =>
        [.. report.Diagnostics.Where(d => d.Rule is not ("MQ7204" or "MQ7206"))];
}
