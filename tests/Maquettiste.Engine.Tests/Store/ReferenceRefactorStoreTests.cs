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
/// The reference-type refactors (next-phase-2-design.md section 3.5, "The four operations") over a copy of
/// <c>tests/fixtures/models/reference-data</c>, with the real validator: each writes through the batch, keeps the old code as a former
/// code, and answers its usage on the item of the type it names.
/// </summary>
public sealed class ReferenceRefactorStoreTests
{
    private const string UnitOfMeasure = "01JBM9S346Q3D25VT4F5V37E3S";
    private const string UnitSeed = "01JB9EE0ZBGJ09TQM83XSSSS6Y";
    private const string KgRow = "01JBS3C4DWA7N36096Q14DR9GP";
    private const string GramRow = "01JBQY77ZXYYK596NGYA1DQ91K";
    private const string Allergen = "01JRDA00000000000000000001";
    private const string AllergenSeed = "01JRDA00000000000000000010";
    private const string NutRow = "01JRDA00000000000000000012";
    private const string TreeNutRow = "01JRDA00000000000000000013";
    private const string Recipe = "01JRDE00000000000000000001";
    private const string Ingredient = "01JRDE00000000000000000002";
    private const string IngredientSeed = "01JRDS00000000000000000002";
    private const string Contains = "01JRDR00000000000000000001";
    private const string ContainsSeed = "01JRDS00000000000000000003";
    private const string ShardPath = "model/locales/fr/_reference-data.json";

    // The type merged away in the merge cases: Intolerance, codes gluten (Allergen has it), lactose (moves) and peanut (mapped to nut).
    private const string Intolerance = "01JRDJ00000000000000000001";
    private const string IntoleranceSeed = "01JRDJ00000000000000000010";
    private const string GlutenIntolerance = "01JRDJ00000000000000000011";
    private const string Lactose = "01JRDJ00000000000000000012";
    private const string Peanut = "01JRDJ00000000000000000013";
    private const string RecipeIntolerance = "01JRDJ00000000000000000021";

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

    private static Task<BatchResult> ApplyAsync(ModelStore store, string operations) => store.ApplyBatchAsync(Parse(store, operations), ChangeSource.Editor, Ct);

    private static ModelBatch Parse(ModelStore store, string operations)
    {
        var parsed = store.ParseBatch(Encoding.UTF8.GetBytes("{ \"operations\": [" + operations + "] }"));
        Assert.True(parsed.Batch is not null, string.Join("; ", parsed.Diagnostics.Select(d => $"{d.Rule} {d.JsonPointer} {d.Message}")));
        return parsed.Batch;
    }

    private static string Failures(BatchResult result) =>
        string.Join("; ", result.Items.SelectMany(i => i.Diagnostics).Select(d => $"{d.Rule} {d.ElementId} {d.JsonPointer} {d.Message}"))
        + string.Join("; ", result.Items.SelectMany(i => i.Referrers).Select(r => $"referenced by {r.FromElementId}{r.JsonPointer}"));

    /// <summary>The batch's one MQ7020, at the operation, after a refusal that wrote nothing.</summary>
    private static Diagnostic Refusal(BatchResult result, int operation = 0)
    {
        Assert.Equal(SaveOutcome.Invalid, result.Outcome);
        var refusal = Assert.Single(result.Items[operation].Diagnostics);
        Assert.Equal(("MQ7020", "/operations/" + operation), (refusal.Rule, refusal.JsonPointer));
        return refusal;
    }

    /// <summary>The fixture translates a few texts only: its per-shard completeness infos (MQ7204, MQ7206) are left out here.</summary>
    private static async Task<List<Diagnostic>> ProblemsAsync(ModelStore store) =>
        [.. (await store.ValidateAsync(ValidationScope.All, Ct)).Diagnostics.Where(d => d.Rule is not ("MQ7204" or "MQ7206"))];

    private static string Text(ModelStore store, string id, string pointer)
    {
        JsonNode? current = JsonNode.Parse(store.Current!.GetDocument(id)!.Json.GetRawText());
        foreach (var segment in pointer.Split('/').Skip(1))
            current = current is JsonArray array ? array[int.Parse(segment, System.Globalization.CultureInfo.InvariantCulture)] : current![segment];
        return current!.GetValue<string>();
    }

    /// <summary>Every file under the model root with its bytes, for all-or-nothing checks.</summary>
    private static SortedDictionary<string, string> Files(LoaderHarness h)
    {
        var files = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(h.Repo.ModelRoot, "*", SearchOption.AllDirectories))
            files[Path.GetRelativePath(h.Repo.ModelRoot, file).Replace('\\', '/')] = Convert.ToHexString(File.ReadAllBytes(file));
        return files;
    }

    private static void WriteDocument(LoaderHarness h, string modelPath, string schema, string json) =>
        h.Write(modelPath, Encoding.UTF8.GetString(TestServices.Json.Write(JsonNode.Parse(json)!, schema, ".maquettiste/" + modelPath)));

    private static void EditDocument(LoaderHarness h, string modelPath, string schema, Action<JsonObject> change)
    {
        var node = JsonNode.Parse(h.Read(modelPath))!.AsObject();
        change(node);
        h.Write(modelPath, Encoding.UTF8.GetString(TestServices.Json.Write(node, schema, ".maquettiste/" + modelPath)));
    }

    private static void Translate(LoaderHarness h, params (string Id, string Label)[] labels)
    {
        var shard = JsonNode.Parse(h.Read(ShardPath))!.AsObject();
        foreach (var (id, label) in labels)
            shard["entries"]![id] = new JsonObject { ["label"] = label };
        h.Write(ShardPath, shard.ToJsonString());
    }

    /// <summary>
    /// Intolerance (gluten, lactose, peanut) and its seed (named <paramref name="seedName"/>), with an fr label on the lactose and peanut
    /// rows, and Recipe's attribute <c>intolerance</c> typed by it (default peanut, allowed peanut, lactose and gluten).
    /// </summary>
    private static void AddIntolerance(LoaderHarness h, string seedName = "Intolerances", string fields = "", string extraColumn = "", string lactoseExtra = "",
        string formerCodes = "")
    {
        WriteDocument(h, "model/reference-types/intolerance.json", "reference-type.json", $$"""
            { "kind": "reference-type", "id": "{{Intolerance}}", "name": "Intolerance",
              "code": { "id": "01JRDJ00000000000000000002", "length": 16 }, "label": { "id": "01JRDJ00000000000000000003", "length": 64 }
              {{fields}} {{formerCodes}} }
            """);
        WriteDocument(h, "model/seeds/intolerance/intolerances.json", "seed.json", $$"""
            { "kind": "seed", "id": "{{IntoleranceSeed}}", "name": "{{seedName}}", "target": "{{Intolerance}}", "columns": ["code", "label"{{extraColumn}}],
              "rows": [ { "id": "{{GlutenIntolerance}}", "values": ["gluten", "Gluten"] }, { "id": "{{Lactose}}", "values": ["lactose", "Lactose"{{lactoseExtra}}] },
                        { "id": "{{Peanut}}", "values": ["peanut", "Peanut"] } ] }
            """);
        EditDocument(h, "model/entities/recipe.json", "entity.json", recipe => recipe["attributes"]!.AsArray().Add(JsonNode.Parse($$"""
            { "id": "{{RecipeIntolerance}}", "name": "intolerance", "type": { "ref": "{{Intolerance}}" }, "default": "peanut",
              "validation": { "allowedValues": ["peanut", "lactose", "gluten"] } }
            """)));
        Translate(h, (Lactose, "Lactose"), (Peanut, "Arachide"));
    }

    [Fact]
    public async Task Rename_code_rewrites_uses_and_keeps_a_former_code()
    {
        var (h, store) = await OpenAsync();
        using var _ = h;
        await using var __ = store;
        var uses = store.Current!.ReferencesTo(GramRow).Select(r => r.FromElementId + r.JsonPointer).Order(StringComparer.Ordinal).ToList();

        var result = await ApplyAsync(store, $$"""{ "op": "rename-code", "id": "@reference-type:UnitOfMeasure", "code": "g", "to": "gram" }""");

        Assert.True(result.Outcome == SaveOutcome.Saved, Failures(result));
        var usage = Assert.Single(result.Items, i => i.Usage is not null);
        Assert.Equal(UnitOfMeasure, usage.Id);
        Assert.Equal((13, 0, 0, 0), (usage.Usage!.UseCount, usage.Usage.RowsMoved, usage.Usage.RowsRemoved, usage.Usage.TranslationsRemoved));
        Assert.Equal(uses, usage.Usage.Uses.Select(u => u.FromElementId + u.JsonPointer));
        Assert.All(usage.Usage.Uses, u => Assert.Equal(GramRow, u.ToId));

        var model = store.Current!;
        var former = Assert.Single(model.Get<ReferenceType>(UnitOfMeasure)!.FormerCodes);
        Assert.Equal(("g", GramRow), (former.Code, former.ReplacedBy));
        Assert.Equal("gram", Text(store, UnitSeed, "/rows/1/values/0"));
        Assert.Equal("gram", Text(store, Ingredient, "/attributes/2/default"));
        Assert.Equal("gram", Text(store, Ingredient, "/attributes/3/validation/allowedValues/1"));
        Assert.Equal("gram", Text(store, Ingredient, "/attributes/5/default/0"));
        Assert.Equal("gram", Text(store, Contains, "/attributes/1/default"));
        Assert.Equal("gram", Text(store, ContainsSeed, "/rows/0/values/3"));
        // The index finds the row again by its new code at every use, and through the former code that names it.
        Assert.Equal(uses.Append(UnitOfMeasure + "/formerCodes/0/replacedBy").Order(StringComparer.Ordinal),
            model.ReferencesTo(GramRow).Select(r => r.FromElementId + r.JsonPointer).Order(StringComparer.Ordinal));
        Assert.Empty(await ProblemsAsync(store));
    }

    [Fact]
    public async Task Rename_code_to_a_live_or_former_code_is_MQ7020()
    {
        var (h, store) = await OpenAsync(h => EditDocument(h, "model/reference-types/unit-of-measure.json", "reference-type.json",
            type => type["formerCodes"] = new JsonArray(new JsonObject { ["code"] = "lb", ["replacedBy"] = KgRow })));
        using var _ = h;
        await using var __ = store;
        var files = Files(h);

        string Rename(string code, string to) => $$"""{ "op": "rename-code", "id": "{{UnitOfMeasure}}", "code": "{{code}}", "to": "{{to}}" }""";

        Assert.Contains("'kg' is already a code", Refusal(await ApplyAsync(store, Rename("g", "kg"))).Message, StringComparison.Ordinal);
        Assert.Contains("'lb' is a former code", Refusal(await ApplyAsync(store, Rename("g", "lb"))).Message, StringComparison.Ordinal);
        Assert.Contains("does not fit the code", Refusal(await ApplyAsync(store, Rename("g", "Gram"))).Message, StringComparison.Ordinal);
        Assert.Contains("does not fit the code", Refusal(await ApplyAsync(store, Rename("g", "gram_unit"))).Message, StringComparison.Ordinal);
        Assert.Contains("'oz' is not a code", Refusal(await ApplyAsync(store, Rename("oz", "ounce"))).Message, StringComparison.Ordinal);
        Assert.Contains("already 'g'", Refusal(await ApplyAsync(store, Rename("g", "g"))).Message, StringComparison.Ordinal);
        Assert.Contains("is not a reference type", Refusal(await ApplyAsync(store, $$"""{ "op": "rename-code", "id": "{{Recipe}}", "code": "g", "to": "gram" }""")).Message,
            StringComparison.Ordinal);
        Assert.Equal(files, Files(h));
    }

    [Fact]
    public async Task Rename_code_of_a_scripted_row_is_MQ7020()
    {
        var (h, store) = await OpenAsync(h =>
        {
            h.Write("extensions/seeds/imperial-units.js", "maquettiste.rows((model, seed) => [{ code: \"oz\", label: \"Ounce\" }]);\n");
            WriteDocument(h, "model/seeds/unit-of-measure/imperial-units.json", "seed.json", $$"""
                { "kind": "seed", "id": "01JRDV00000000000000000001", "name": "ImperialUnits", "target": "{{UnitOfMeasure}}", "columns": ["code", "label"],
                  "rowsFrom": { "script": "seeds/imperial-units.js" } }
                """);
        });
        using var _ = h;
        await using var __ = store;

        var refusal = Refusal(await ApplyAsync(store, $$"""{ "op": "rename-code", "id": "{{UnitOfMeasure}}", "code": "oz", "to": "ounce" }"""));

        Assert.Contains("'oz' is not a code of a written row", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("'ImperialUnits' computes rows with a script", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Retire_code_removes_the_row_and_moves_x_ref_values()
    {
        var (h, store) = await OpenAsync(h =>
        {
            WriteDocument(h, "extensions/allergens.json", "extension.json", $$"""
                { "name": "allergens", "appliesTo": { "kinds": ["entity"] },
                  "properties": { "mainAllergen": { "type": "string", "x-ref": { "kind": "row", "of": "{{Allergen}}" } } } }
                """);
            EditDocument(h, "model/entities/ingredient.json", "entity.json", e => e["properties"] = new JsonObject { ["mainAllergen"] = TreeNutRow });
            Translate(h, (TreeNutRow, "Fruits à coque"));
        });
        using var _ = h;
        await using var __ = store;

        var result = await ApplyAsync(store, $$"""{ "op": "retire-code", "id": "{{Allergen}}", "code": "tree_nut", "to": "nut" }""");

        Assert.True(result.Outcome == SaveOutcome.Saved, Failures(result));
        var usage = Assert.Single(result.Items, i => i.Usage is not null).Usage!;
        Assert.Equal((0, 1, 1), (usage.RowsMoved, usage.RowsRemoved, usage.TranslationsRemoved));
        Assert.Equal([Ingredient + "/properties/mainAllergen", IngredientSeed + "/rows/3/values/4/0"], usage.Uses.Select(u => u.FromElementId + u.JsonPointer));
        var model = store.Current!;
        Assert.DoesNotContain(model.Get<Seed>(AllergenSeed)!.Rows, r => r.Id == TreeNutRow);
        Assert.Equal(NutRow, model.Get<Entity>(Ingredient)!.Properties["mainAllergen"].GetString());
        Assert.Equal("nut", Text(store, IngredientSeed, "/rows/3/values/4/0"));
        var former = Assert.Single(model.Get<ReferenceType>(Allergen)!.FormerCodes);
        Assert.Equal(("tree_nut", NutRow), (former.Code, former.ReplacedBy));
        Assert.False(JsonNode.Parse(h.Read(ShardPath))!["entries"]!.AsObject().ContainsKey(TreeNutRow));
        Assert.Empty(await ProblemsAsync(store));
    }

    [Fact]
    public async Task Retire_code_moves_the_former_codes_of_the_removed_row()
    {
        var (h, store) = await OpenAsync(h => EditDocument(h, "model/reference-types/allergen.json", "reference-type.json",
            type => type["formerCodes"] = new JsonArray(new JsonObject { ["code"] = "walnut", ["replacedBy"] = TreeNutRow })));
        using var _ = h;
        await using var __ = store;

        var result = await ApplyAsync(store, $$"""{ "op": "retire-code", "id": "{{Allergen}}", "code": "tree_nut", "to": "nut" }""");

        Assert.True(result.Outcome == SaveOutcome.Saved, Failures(result));
        Assert.Equal([("tree_nut", NutRow), ("walnut", NutRow)], store.Current!.Get<ReferenceType>(Allergen)!.FormerCodes.Select(f => (f.Code, f.ReplacedBy)));
        Assert.Contains(Assert.Single(result.Items, i => i.Usage is not null).Usage!.Uses, u => u.FromElementId == Allergen && u.JsonPointer == "/formerCodes/0/replacedBy");
        Assert.Empty(await ProblemsAsync(store));
    }

    [Fact]
    public async Task Merge_moves_unmatched_rows_with_their_ids_and_translations()
    {
        var (h, store) = await OpenAsync(h => AddIntolerance(h));
        using var _ = h;
        await using var __ = store;

        var result = await ApplyAsync(store, $$"""
            { "op": "merge-reference-types", "id": "@reference-type:Intolerance", "target": "@reference-type:Allergen", "codes": { "peanut": "nut" } }
            """);

        Assert.True(result.Outcome == SaveOutcome.Saved, Failures(result));
        var item = Assert.Single(result.Items, i => i.Usage is not null);
        Assert.Equal(Intolerance, item.Id);
        Assert.Equal((1, 2, 1), (item.Usage!.RowsMoved, item.Usage.RowsRemoved, item.Usage.TranslationsRemoved));
        var model = store.Current!;
        Assert.Null(model.GetDocument(Intolerance));
        Assert.False(h.Exists("model/reference-types/intolerance.json"));
        var seed = model.Get<Seed>(IntoleranceSeed)!;
        Assert.Equal(Allergen, seed.Target);
        Assert.Equal([Lactose], seed.Rows.Select(r => r.Id));
        Assert.True(h.Exists("model/seeds/allergen/intolerances.json"));
        Assert.False(h.Exists("model/seeds/intolerance/intolerances.json"));
        var entries = JsonNode.Parse(h.Read(ShardPath))!["entries"]!.AsObject();
        Assert.Equal("Lactose", (string)entries[Lactose]!["label"]!);
        Assert.False(entries.ContainsKey(Peanut));
        Assert.Equal([("peanut", NutRow)], model.Get<ReferenceType>(Allergen)!.FormerCodes.Select(f => (f.Code, f.ReplacedBy)));
        Assert.Empty(await ProblemsAsync(store));
    }

    [Fact]
    public async Task Merge_maps_codes_and_moves_every_reference_to_the_target()
    {
        var (h, store) = await OpenAsync(h => AddIntolerance(h,
            formerCodes: $$""", "formerCodes": [ { "code": "groundnut", "replacedBy": "{{Peanut}}" } ]"""));
        using var _ = h;
        await using var __ = store;

        var result = await ApplyAsync(store, $$"""{ "op": "merge-reference-types", "id": "{{Intolerance}}", "target": "{{Allergen}}", "codes": { "peanut": "nut" } }""");

        Assert.True(result.Outcome == SaveOutcome.Saved, Failures(result));
        var model = store.Current!;
        var attribute = model.Get<Entity>(Recipe)!.Attributes.Single(a => a.Id == RecipeIntolerance);
        Assert.Equal(Allergen, attribute.Type.Ref);
        Assert.Equal("nut", attribute.Default!.Value.GetString());
        Assert.Equal(["nut", "lactose", "gluten"], attribute.Validation!.AllowedValues.Select(v => v.GetString()));
        Assert.Equal([("groundnut", NutRow), ("peanut", NutRow)], model.Get<ReferenceType>(Allergen)!.FormerCodes.Select(f => (f.Code, f.ReplacedBy)));
        Assert.Empty(model.ReferencesTo(Intolerance));
        Assert.Contains(model.ReferencesTo(Lactose), r => r.FromElementId == Recipe);
        var usage = Assert.Single(result.Items, i => i.Usage is not null).Usage!;
        Assert.Equal(
            [Recipe + "/attributes/2/default", Recipe + "/attributes/2/type/ref", Recipe + "/attributes/2/validation/allowedValues/0"],
            usage.Uses.Select(u => u.FromElementId + u.JsonPointer));
        Assert.Empty(await ProblemsAsync(store));
    }

    [Fact]
    public async Task Merge_joins_a_seed_of_the_same_name()
    {
        var (h, store) = await OpenAsync(h => AddIntolerance(h, seedName: "Allergen"));
        using var _ = h;
        await using var __ = store;

        var result = await ApplyAsync(store, $$"""{ "op": "merge-reference-types", "id": "{{Intolerance}}", "target": "{{Allergen}}", "codes": { "peanut": "nut" } }""");

        Assert.True(result.Outcome == SaveOutcome.Saved, Failures(result));
        var model = store.Current!;
        Assert.Null(model.GetDocument(IntoleranceSeed));
        Assert.False(h.Exists("model/seeds/intolerance/intolerances.json"));
        var joined = model.Get<Seed>(AllergenSeed)!;
        Assert.Equal(Lactose, joined.Rows[^1].Id);
        Assert.Equal(["lactose", "Lactose"], joined.Rows[^1].Values.Select(v => v.GetString()));
        Assert.Equal("Lactose", (string)JsonNode.Parse(h.Read(ShardPath))!["entries"]![Lactose]!["label"]!);
        Assert.Empty(await ProblemsAsync(store));
    }

    [Fact]
    public async Task Merge_with_a_binding_or_a_missing_field_is_MQ7020()
    {
        var (h, store) = await OpenAsync(h => AddIntolerance(h,
            fields: """, "attributes": [ { "id": "01JRDJ00000000000000000004", "name": "severity", "type": "string", "length": 16 } ]""",
            extraColumn: ", \"01JRDJ00000000000000000004\"", lactoseExtra: ", \"high\""));
        using var _ = h;
        await using var __ = store;
        var files = Files(h);

        var missing = Refusal(await ApplyAsync(store, $$"""{ "op": "merge-reference-types", "id": "{{Intolerance}}", "target": "{{Allergen}}" }"""));

        Assert.Contains("holds a value for field 'severity'", missing.Message, StringComparison.Ordinal);
        Assert.Equal(files, Files(h));


        var mismatched = Refusal(await ApplyAsync(store, $$"""{ "op": "merge-reference-types", "id": "{{Intolerance}}", "target": "{{Allergen}}", "codes": { "lactose": "dairy" } }"""));
        Assert.Contains("'dairy'", mismatched.Message, StringComparison.Ordinal);
        Assert.Contains("cannot merge into itself",
            Refusal(await ApplyAsync(store, $$"""{ "op": "merge-reference-types", "id": "{{Allergen}}", "target": "{{Allergen}}" }""")).Message, StringComparison.Ordinal);

        // A type whose rows a bound table keeps does not merge: a merge moves rows between seeds only.
        var (bh, bound) = await OpenAsync(h => AddIntolerance(h, fields: """
            , "bindings": [ { "id": "01JRDJ00000000000000000031", "database": "01JRDJ00000000000000000032", "table": "01JRDJ00000000000000000033" } ]
            """));
        using var ___ = bh;
        await using var ____ = bound;
        Assert.Contains("keeps its rows in a bound table",
            Refusal(await ApplyAsync(bound, $$"""{ "op": "merge-reference-types", "id": "{{Intolerance}}", "target": "{{Allergen}}" }""")).Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rename_type_moves_its_file_and_seed_folder()
    {
        // A second seed keeps its rows in a CSV file beside it, which moves with it.
        var (h, store) = await OpenAsync(h =>
        {
            WriteDocument(h, "model/seeds/unit-of-measure/metric-units.json", "seed.json", $$"""
                { "kind": "seed", "id": "01JRDV00000000000000000002", "name": "MetricUnits", "target": "{{UnitOfMeasure}}",
                  "columns": ["code", "label", "01JBNPZX45HY43KWJRP1XPA7Z3"], "rowsFrom": { "file": "metric-units.csv" } }
                """);
            h.Write("model/seeds/unit-of-measure/metric-units.csv", "@id,code,label,01JBNPZX45HY43KWJRP1XPA7Z3\n01JRDV00000000000000000003,mg,Milligram,0.001\n");
        });
        using var _ = h;
        await using var __ = store;

        Assert.Contains("is named 'Allergen' already",
            Refusal(await ApplyAsync(store, $$"""{ "op": "rename-reference-type", "id": "{{UnitOfMeasure}}", "name": "allergen" }""")).Message, StringComparison.Ordinal);
        Assert.Contains("is not an element name",
            Refusal(await ApplyAsync(store, $$"""{ "op": "rename-reference-type", "id": "{{UnitOfMeasure}}", "name": "1Unit" }""")).Message, StringComparison.Ordinal);

        var result = await ApplyAsync(store, $$"""{ "op": "rename-reference-type", "id": "{{UnitOfMeasure}}", "name": "Unit" }""");

        Assert.True(result.Outcome == SaveOutcome.Saved, Failures(result));
        Assert.Equal("Unit", store.Current!.Get<ReferenceType>(UnitOfMeasure)!.Name);
        Assert.True(h.Exists("model/reference-types/unit.json"));
        Assert.False(h.Exists("model/reference-types/unit-of-measure.json"));
        Assert.True(h.Exists("model/seeds/unit/unit-of-measure.json"));
        Assert.True(h.Exists("model/seeds/unit/metric-units.json"));
        Assert.True(h.Exists("model/seeds/unit/metric-units.csv"));
        Assert.False(Directory.Exists(h.Model("model/seeds/unit-of-measure")) && Directory.EnumerateFiles(h.Model("model/seeds/unit-of-measure")).Any());
        Assert.Equal(["mg"], store.Current!.Get<Seed>("01JRDV00000000000000000002")!.Rows.Select(r => r.Values[0].GetString()));
        var usage = Assert.Single(result.Items, i => i.Usage is not null).Usage!;
        Assert.Equal((0, 0, 0, 0), (usage.UseCount, usage.RowsMoved, usage.RowsRemoved, usage.TranslationsRemoved));
        Assert.Empty(await ProblemsAsync(store));
    }

    [Fact]
    public async Task Two_refactors_of_one_type_in_a_batch_are_MQ7020()
    {
        var (h, store) = await OpenAsync();
        using var _ = h;
        await using var __ = store;
        var files = Files(h);

        var twice = await ApplyAsync(store, $$"""
            { "op": "rename-code", "id": "{{UnitOfMeasure}}", "code": "g", "to": "gram" },
            { "op": "rename-reference-type", "id": "{{UnitOfMeasure}}", "name": "Unit" }
            """);

        Assert.Contains("is refactored by operation 0 of this batch already", Refusal(twice, 1).Message, StringComparison.Ordinal);
        Assert.Equal(files, Files(h));

        // Two types in one batch share the documents both rewrite (the ingredient seed names units and allergens).
        var both = await ApplyAsync(store, $$"""
            { "op": "rename-code", "id": "{{UnitOfMeasure}}", "code": "g", "to": "gram" },
            { "op": "rename-code", "id": "{{Allergen}}", "code": "gluten", "to": "wheat" }
            """);

        Assert.True(both.Outcome == SaveOutcome.Saved, Failures(both));
        Assert.Equal("gram", Text(store, IngredientSeed, "/rows/0/values/2"));
        Assert.Equal("wheat", Text(store, IngredientSeed, "/rows/0/values/4/0"));
        Assert.Equal(2, both.Items.Count(i => i.Usage is not null));
        Assert.Empty(await ProblemsAsync(store));
    }

    [Fact]
    public async Task A_dry_run_writes_nothing_and_lists_usage()
    {
        var (h, store) = await OpenAsync();
        using var _ = h;
        await using var __ = store;
        var files = Files(h);

        var preview = await store.PreviewBatchAsync(Parse(store, $$"""{ "op": "rename-code", "id": "{{UnitOfMeasure}}", "code": "g", "to": "gram" }"""), Ct);

        Assert.True(preview.Outcome == SaveOutcome.Saved, string.Join("; ", preview.Items.SelectMany(i => i.Diagnostics).Select(d => d.Message)));
        Assert.Equal(files, Files(h));
        var item = Assert.Single(preview.Items, i => i.Usage is not null);
        Assert.Equal((UnitOfMeasure, 13), (item.Id, item.Usage!.UseCount));
        Assert.Equal(
            new[] { Contains, ContainsSeed, Ingredient, IngredientSeed, UnitOfMeasure, UnitSeed }.Order(StringComparer.Ordinal),
            preview.Files.Select(f => f.Id!).Order(StringComparer.Ordinal));
        Assert.Contains("\"formerCodes\"", Assert.Single(preview.Files, f => f.Id == UnitOfMeasure).After, StringComparison.Ordinal);
    }
}
