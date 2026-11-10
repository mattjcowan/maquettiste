using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Maquettiste.Engine.Resolution;
using Maquettiste.Engine.SchemaDiff;
using Maquettiste.Engine.Tests.Integration;
using Maquettiste.Testing;
using static Maquettiste.Engine.Tests.Integration.ReferenceDataRepo;

namespace Maquettiste.Engine.Tests.Resolution;

/// <summary>
/// The resolver over the reference-data fixture (reference-types-seeds-localization.md sections 1.4, 1.5 and 2.5): reference types,
/// rows, seeds and their orders, reference usages, storage choices, reference-typed columns, template-defined collections and the
/// schema snapshot's reference columns.
/// </summary>
public sealed class ReferenceDataResolutionTests
{
    private static async Task<ResolvedModel> ResolveAsync(Action<JsonObject>? settings = null, Action<ReferenceDataRepo>? prepare = null)
    {
        await using var repo = Create(settings, pack: false);
        if (prepare is not null)
            prepare(repo);
        return await repo.ResolveAsync();
    }

    private static RReferenceType Type(ResolvedModel model, string id) => Assert.IsType<RReferenceType>(model.Find(id));

    private static RAttribute Attr(REntity entity, string name) => entity.Attributes.Single(a => a.Name == name);

    [Fact]
    public async Task Reference_types_resolve_with_fields_rows_and_users()
    {
        var model = await ResolveAsync();

        Assert.Equal(["Allergen", "UnitOfMeasure"], model.ReferenceTypes.Select(t => t.Name));
        var unit = Type(model, UnitOfMeasure);
        Assert.Equal("Unit of measure", unit.DisplayName);
        Assert.Equal(("string", 8, "^[a-z0-9_]+$", "Code"), (unit.Code.Type, unit.Code.Length, unit.Code.Pattern, unit.Code.DisplayName));
        Assert.Equal(64, unit.Label.Length);
        Assert.Equal(["factor", "symbol"], unit.Attributes.Select(a => a.Name));
        Assert.Null(unit.Package);

        Assert.Equal(["kg", "g", "pinch"], unit.Rows.Select(r => r.Code));
        Assert.Equal([0, 1, 2], unit.Rows.Select(r => r.Order));
        var kg = unit.Rows[0];
        Assert.Equal(KgRow, kg.Id);
        Assert.Equal("Kilogram", kg.Label);
        Assert.Equal(1000L, kg.Values["factor"]);
        Assert.Equal("kg", kg.Values["symbol"]);
        Assert.Equal(0.36m, unit.Rows[2].Values["factor"]); // decimal text, never a double
        Assert.Null(unit.Rows[2].Values["symbol"]);
        Assert.Equal("UnitOfMeasure", kg.Seed.Name);
        Assert.Same(kg, model.Find(KgRow));
        Assert.Equal(["UnitOfMeasure"], unit.Seeds.Select(s => s.Name));
        Assert.Contains("e:" + UnitSeed, kg.Dependencies);

        // Used by: declared attributes only, by (owner id, order).
        Assert.Equal(["defaultUnit", "preferredUnit", "packUnits", "unit_of_measure"], unit.UsedBy.Select(a => a.Name));
        Assert.Equal(["parent", "allergens"], Type(model, Allergen).UsedBy.Select(a => a.Name));
        Assert.Contains("r:" + UnitOfMeasure, unit.UsedBy.MembershipKeys);
        Assert.Contains("k:reference-type", model.ReferenceTypes.MembershipKeys);
    }

    [Fact]
    public async Task Former_codes_resolve_to_the_replacing_rows()
    {
        // Sorted by code; an entry whose replacedBy names no row of the type is left out (next-phase-2-design.md section 3.5).
        var model = await ResolveAsync(prepare: repo => EditFile(repo, "model/reference-types/unit-of-measure.json", n =>
            n["formerCodes"] = new JsonArray(
                new JsonObject { ["code"] = "kilo", ["replacedBy"] = KgRow },
                new JsonObject { ["code"] = "gram", ["replacedBy"] = "01JBQY77ZXYYK596NGYA1DQ91K" },
                new JsonObject { ["code"] = "lost", ["replacedBy"] = "01JRDA00000000000000000011" })));
        var unit = Type(model, UnitOfMeasure);
        Maquettiste.Engine.Tests.Editor.ContractCheck.Validate(model, "formerCodeRecord");

        Assert.Equal(["gram", "kilo"], unit.FormerCodes.Select(f => f.Code));
        var kilo = unit.FormerCodes[1];
        Assert.Equal("former-code", kilo.Kind);
        Assert.Same(model.Find(KgRow), kilo.ReplacedBy);
        Assert.Equal("kg", kilo.ReplacedBy.Code);
        Assert.Contains("e:" + UnitOfMeasure, kilo.Dependencies);
        Assert.Contains("e:" + UnitSeed, kilo.Dependencies);
        Assert.Null(unit.RowOf("kilo")); // row finds live codes only
        Assert.Empty(Type(model, Allergen).FormerCodes);
    }

    [Fact]
    public async Task A_self_referencing_type_resolves_its_refs_lazily_and_values_stay_codes()
    {
        var model = await ResolveAsync();
        var allergen = Type(model, Allergen);

        var treeNut = allergen.Rows.Single(r => Equals(r.Code, "tree_nut"));
        Assert.Equal("nut", treeNut.Values["parent"]); // the code, never the row
        var parent = Assert.IsType<RRow>(treeNut.Refs["parent"]);
        Assert.Equal("nut", parent.Code);
        Assert.Null(parent.Refs["parent"]);
        Assert.Same(allergen.RowOf("nut"), parent);
        Assert.Equal("reference", allergen.Attributes.Single().Type.Kind);
        Assert.Same(allergen, allergen.Attributes.Single().Type.ReferenceType);
    }

    [Fact]
    public async Task Attributes_typed_by_a_reference_type_carry_their_usage()
    {
        var model = await ResolveAsync();
        var ingredient = model.Entity("Ingredient");

        var defaultUnit = Attr(ingredient, "defaultUnit");
        Assert.Equal(("reference", "UnitOfMeasure", "string", 8), (defaultUnit.Type.Kind, defaultUnit.Type.Name, defaultUnit.Type.Builtin, defaultUnit.Length));
        var usage = defaultUnit.Reference!;
        Assert.Same(Type(model, UnitOfMeasure), usage.Type);
        Assert.False(usage.IsCollection);
        Assert.True(usage.Required);
        Assert.Equal("g", usage.DefaultRow!.Code);
        Assert.Equal(["kg", "g", "pinch"], usage.Allowed.Select(r => r.Code));
        Assert.Equal("check", usage.Storage["main"].Strategy);

        Assert.Equal(["kg", "g"], Attr(ingredient, "preferredUnit").Reference!.Allowed.Select(r => r.Code));
        var packUnits = Attr(ingredient, "packUnits").Reference!;
        Assert.True(packUnits.IsCollection);
        Assert.Equal("g", packUnits.DefaultRow!.Code);
        Assert.Contains("e:" + UnitSeed, packUnits.Dependencies);
        Assert.Contains(ResolveRun.ReferenceDataKey, packUnits.Storage["main"].Dependencies);
        Assert.Null(Attr(ingredient, "name").Reference);
        Assert.Equal("g", model.Relation("contains").Attributes.Single(a => a.Name == "unit_of_measure").Reference!.DefaultRow!.Code);
    }

    [Fact]
    public async Task Effective_storage_follows_type_then_database_then_project_then_template_defined()
    {
        var model = await ResolveAsync();
        var unit = Type(model, UnitOfMeasure);
        var allergen = Type(model, Allergen);
        string Row(RStorageChoice c) => $"{c.Strategy ?? "-"} {c.Source ?? "-"} {c.Collections}";

        Assert.Equal("check type True", Row(unit.Storage["main"]));        // type "*" beats everything
        Assert.Equal("check type True", Row(unit.Storage["reporting"]));
        Assert.Equal("lookup-table project True", Row(allergen.Storage["main"]));
        Assert.Equal("check database True", Row(allergen.Storage["reporting"]));
        Assert.Equal("CHECK (col IN (...codes))", unit.Storage["main"].Description);
        Assert.Empty(allergen.Storage["main"].Options);
        Assert.Contains(ResolveRun.ReferenceDataKey, allergen.Storage["main"].Dependencies);
        Assert.Contains("e:" + MainDatabaseId, allergen.Storage["main"].Dependencies);

        // A type entry for one database id beats its "*" entry; with no choice anywhere the template decides.
        var bare = await ResolveAsync(s =>
        {
            s.Remove("conventions");
            s.Remove("databases");
        }, repo => EditFile(repo, "model/reference-types/unit-of-measure.json", n =>
            n["storage"] = new JsonObject
            {
                ["*"] = new JsonObject { ["strategy"] = "check" },
                [ReportingDatabaseId] = new JsonObject { ["strategy"] = "native" },
            }));
        Assert.Equal("check type True", Row(Type(bare, UnitOfMeasure).Storage["main"]));
        Assert.Equal("native type False", Row(Type(bare, UnitOfMeasure).Storage["reporting"])); // native: no collections on sqlite
        Assert.Equal("- - False", Row(Type(bare, Allergen).Storage["main"]));
        Assert.Equal("- - False", Row(Type(bare, Allergen).Storage["reporting"]));
    }

    [Fact]
    public async Task A_uuid_code_types_its_reference_columns_with_the_dialects_uuid_type()
    {
        var model = await ResolveAsync(prepare: repo => EditFile(repo, "model/reference-types/unit-of-measure.json", n =>
        {
            n["code"]!["type"] = "uuid";
            n["code"]!.AsObject().Remove("length");
            n["code"]!.AsObject().Remove("pattern");
        }));

        Assert.Equal("uuid", Type(model, UnitOfMeasure).Code.Type);
        var main = model.Db("main").Table("ingredients").Column("default_unit");
        var reporting = model.Db("reporting").Table("ingredients").Column("default_unit");
        Assert.Equal(("reference", "uuid", (int?)null), (main.Type, main.CodeType, main.Length));
        Assert.Equal(("uuid", "uuid"), (main.NativeType, reporting.CodeType));
        Assert.Equal("text", reporting.NativeType); // the sqlite dialect map
    }

    [Fact]
    public async Task A_single_reference_attribute_maps_to_one_reference_column_and_a_collection_to_none()
    {
        var model = await ResolveAsync();
        var main = model.Db("main");
        var ingredients = main.Table("ingredients");

        var column = ingredients.Column("default_unit");
        Assert.Equal(("reference", "string", 8, "check", false), (column.Type, column.CodeType, column.Length, column.Strategy, column.Nullable));
        Assert.Same(Type(model, UnitOfMeasure), column.ReferenceType);
        Assert.Equal("varchar(8)", column.NativeType);
        Assert.Equal("g", column.Default);
        Assert.Contains(ResolveRun.ReferenceDataKey, column.Dependencies);
        Assert.Contains("e:" + UnitOfMeasure, column.Dependencies);
        Assert.True(ingredients.Column("preferred_unit").Nullable);

        // Collections: no column, no child table; the mapping lists them for the templates.
        Assert.DoesNotContain(ingredients.Columns, c => c.Name is "allergens" or "pack_units");
        Assert.DoesNotContain(main.Tables, t => t.Name.Contains("allergen", StringComparison.Ordinal) || t.Name.Contains("unit", StringComparison.Ordinal));
        Assert.Equal(["allergens", "packUnits"], model.Entity("Ingredient").Mappings["main"].TemplateDefined.Select(a => a.Name));
        Assert.Empty(model.Entity("Recipe").Mappings["main"].TemplateDefined);

        var contains = model.Relation("contains").Mappings["main"];
        Assert.Empty(contains.TemplateDefined);
        var junction = contains.JunctionTable!;
        Assert.Equal("reference", junction.Column("unit_of_measure").Type);
        Assert.Equal("check", model.Db("reporting").Table("ingredients").Column("default_unit").Strategy);
    }

    [Fact]
    public async Task An_ignored_collection_is_not_template_defined()
    {
        var model = await ResolveAsync(prepare: repo => WriteFile(repo, "model/mappings/ingredient-main.json", new JsonObject
        {
            ["$schema"] = "../../.schema/v1/mapping.json", ["kind"] = "mapping", ["id"] = "01JRDM00000000000000000001",
            ["name"] = "Ingredient in main", ["database"] = MainDatabaseId, ["entity"] = Ingredient,
            ["attributes"] = new JsonArray(new JsonObject { ["attribute"] = "01JRDE00000000000000000025", ["ignore"] = true }),
        }, "mapping.json"));
        Assert.Equal(["packUnits"], model.Entity("Ingredient").Mappings["main"].TemplateDefined.Select(a => a.Name));
        Assert.Equal(["allergens", "packUnits"], model.Entity("Ingredient").Mappings["reporting"].TemplateDefined.Select(a => a.Name));
    }

    [Fact]
    public async Task Seeds_resolve_columns_cells_refs_and_orders()
    {
        var model = await ResolveAsync();

        Assert.Equal(["Allergen", "Ingredient", "Recipe", "UnitOfMeasure", "contains"], model.Seeds.Select(s => s.Name));
        // Depth 0: Allergen, Recipe, UnitOfMeasure; Ingredient names allergens and units; contains names ingredients and recipes.
        Assert.Equal(["Allergen", "Recipe", "UnitOfMeasure", "Ingredient", "contains"], model.SeedsInOrder.Select(s => s.Name));

        var ingredient = model.Seeds.Single(s => s.Name == "Ingredient");
        Assert.Same(model.Entity("Ingredient"), ingredient.Target);
        Assert.Equal(["sku:attribute", "name:attribute", "defaultUnit:attribute", "preferredUnit:attribute", "allergens:attribute", "packUnits:attribute", "substitute:end"],
            ingredient.Columns.Select(c => c.Name + ":" + c.Kind));
        Assert.Equal([ingredient], model.Entity("Ingredient").Seeds);
        var spelt = ingredient.Rows[2];
        Assert.Equal("01JRDS00000000000000000201", spelt.Values["substitute"]);
        Assert.Equal(ImmutableArray.Create<object?>("gluten"), spelt.Values["allergens"]);
        Assert.Equal("flour", Assert.IsType<RSeedRow>(spelt.Refs["substitute"]).Values["sku"]);
        Assert.Equal(["gluten"], ((ImmutableArray<object?>)spelt.Refs["allergens"]!).Cast<RRow>().Select(r => r.Code));
        Assert.Null(ingredient.Rows[1].Values["preferredUnit"]);
        Assert.Null(ingredient.Rows[0].Values["substitute"]); // trailing null trimmed in the file

        var contains = model.Seeds.Single(s => s.Name == "contains");
        Assert.Equal([contains], model.Relation("contains").Seeds);
        var first = contains.Rows[0];
        Assert.Equal((500L, "g"), (first.Values["quantity"], first.Values["unit_of_measure"]));
        Assert.Equal(0.01m, contains.Rows[1].Values["quantity"]);
        Assert.Equal("bread", Assert.IsType<RSeedRow>(first.Refs["recipe"]).Values["code"]);
        Assert.Equal("g", Assert.IsType<RRow>(first.Refs["unit_of_measure"]).Code);
        Assert.Same(model.Find(ContainsSeed), contains);
        Assert.Contains("k:seed", model.Seeds.MembershipKeys);
    }

    [Fact]
    public async Task Ordered_rows_put_a_named_row_first_and_break_a_cycle_at_file_order()
    {
        var model = await ResolveAsync(prepare: repo => EditFile(repo, "model/seeds/allergen/allergen.json", n =>
        {
            var rows = n["rows"]!.AsArray();
            var treeNut = rows[2]!.DeepClone();
            rows.RemoveAt(2);
            rows.Insert(0, treeNut);
        }));
        var seed = model.Seeds.Single(s => s.Name == "Allergen");
        Assert.Equal(["tree_nut", "gluten", "nut"], seed.Rows.Select(r => r.Values["code"]));
        Assert.Equal(["gluten", "nut", "tree_nut"], seed.OrderedRows.Select(r => r.Values["code"]));
        Assert.Equal(["tree_nut", "gluten", "nut"], Type(model, Allergen).Rows.Select(r => r.Code)); // type rows keep file order

        var cyclic = await ResolveAsync(prepare: repo => EditFile(repo, "model/seeds/allergen/allergen.json", n =>
        {
            var rows = n["rows"]!.AsArray();
            rows[1]!["values"]!.AsArray().Add("tree_nut"); // nut -> tree_nut -> nut
        }));
        Assert.Equal(["gluten", "nut", "tree_nut"], cyclic.Seeds.Single(s => s.Name == "Allergen").OrderedRows.Select(r => r.Values["code"]));
    }

    [Fact]
    public async Task The_snapshot_records_a_reference_column_as_type_reference_with_its_strategy()
    {
        var model = await ResolveAsync();
        var snapshot = SnapshotCapture.Capture(model.Db("main"), 1, ct: TestContext.Current.CancellationToken);
        var column = snapshot.Tables.Single(t => t.Name == "ingredients").Columns.Single(c => c.Name == "default_unit");
        Assert.Equal(("reference", 8, UnitOfMeasure, "check"), (column.Type, column.Length, column.ReferenceType, column.Strategy));
        var sku = snapshot.Tables.Single(t => t.Name == "ingredients").Columns.Single(c => c.Name == "sku");
        Assert.Null(sku.ReferenceType);
        var json = Encoding.UTF8.GetString(TestServices.Json.Serialize(SnapshotCapture.Sorted(snapshot), "snapshot.json", "snapshots/main.json"));
        Assert.Contains("\"referenceType\": \"" + UnitOfMeasure + "\"", json, StringComparison.Ordinal);
        Assert.Contains("\"strategy\": \"check\"", json, StringComparison.Ordinal);
        Assert.Contains("\"type\": \"reference\"", json, StringComparison.Ordinal);

        // A strategy change is an Altered column with a strategy property change; the code facets ride along the same way.
        var changed = await ResolveAsync(prepare: repo => EditFile(repo, "model/reference-types/unit-of-measure.json", n =>
        {
            n["storage"]!["*"]!["strategy"] = "lookup-table";
            n["code"]!["length"] = 12;
        }));
        var diff = SchemaDiffer.Compare(snapshot, SnapshotCapture.Capture(changed.Db("main"), 2, ct: TestContext.Current.CancellationToken), changed.Db("main"), ct: TestContext.Current.CancellationToken);
        var ingredients = diff.Tables.Single(t => t.Table?.Name == "ingredients");
        var change = ingredients.Columns.Single(c => c.Key.EndsWith("01JRDE00000000000000000023", StringComparison.Ordinal));
        Assert.Equal(ChangeKind.Altered, change.Kind);
        Assert.Contains(change.Changes, p => p.Property == "strategy" && Equals(p.Old, "check") && Equals(p.New, "lookup-table"));
        Assert.Contains(change.Changes, p => p.Property == "length" && Equals(p.New, 12));
    }

    [Fact]
    public async Task Stable_ids_follow_the_row_key_in_database_terms()
    {
        // A stable id (next-phase-2-design.md section 3.4) names the row key's values in database terms: a uuid in lowercase with
        // hyphens, a decimal without trailing zeros, an integer in plain digits, a string as it is, joined by U+001F.
        const string Table = "01JRDT00000000000000000001", Seed = "01JRDT00000000000000000010";
        await using var repo = Create(pack: false);
        await repo.CreateAsync($$"""
            {"kind":"table","id":"{{Table}}","name":"price_points","database":"{{MainDatabaseId}}","columns":[
              {"id":"01JRDT00000000000000000002","name":"id","type":"uuid","nullable":false,"stableId":true},
              {"id":"01JRDT00000000000000000003","name":"owner","type":"uuid","nullable":false},
              {"id":"01JRDT00000000000000000004","name":"amount","type":"decimal","precision":10,"scale":2,"nullable":false},
              {"id":"01JRDT00000000000000000005","name":"tier","type":"int32","nullable":false},
              {"id":"01JRDT00000000000000000006","name":"region","type":"string","length":8,"nullable":false},
              {"id":"01JRDT00000000000000000008","name":"label","type":"string","length":40}],
             "primaryKey":{"columns":["01JRDT00000000000000000002"]},
             "uniques":[{"id":"01JRDT00000000000000000007","columns":["01JRDT00000000000000000003","01JRDT00000000000000000004","01JRDT00000000000000000005","01JRDT00000000000000000006"]}]}
            """);
        await repo.CreateAsync($$"""
            {"kind":"seed","id":"{{Seed}}","name":"pricePoints","target":"{{Table}}","key":"01JRDT00000000000000000007",
             "columns":["01JRDT00000000000000000003","01JRDT00000000000000000004","01JRDT00000000000000000005","01JRDT00000000000000000006","01JRDT00000000000000000008"],
             "rows":[{"id":"01JRDT00000000000000000011","values":["0193A3C2-0000-7000-8000-00000000000A",12.50,3,"EU","Standard"]},
               {"id":"01JRDT00000000000000000012","values":["0193a3c2-0000-7000-8000-00000000000a",12.5,3,"US"]}]}
            """);

        static IReadOnlyList<RDataRow> Rows(ResolvedModel model) =>
            model.Databases.Single(d => d.Id == MainDatabaseId).SeedTables.Single(t => t.Table.Name == "price_points").Rows;
        var rows = Rows(await repo.ResolveAsync());
        Assert.Equal(new object?[] { "0193A3C2-0000-7000-8000-00000000000A", 12.50m, 3L, "EU" }, rows[0].Key); // the key as the cells give it
        Assert.Equal("0193a3c2-0000-7000-8000-00000000000a\u001F12.5\u001F3\u001FEU",
            Maquettiste.Engine.Model.StableIds.KeyText(["0193a3c2-0000-7000-8000-00000000000a", 12.50m, 3L, "EU"]));
        string eu = Maquettiste.Engine.Model.StableIds.Of(Table, ["0193a3c2-0000-7000-8000-00000000000a", "12.5", "3", "EU"]);
        string us = Maquettiste.Engine.Model.StableIds.Of(Table, ["0193a3c2-0000-7000-8000-00000000000a", "12.5", "3", "US"]);
        Assert.Equal([eu, us], rows.Select(r => (string?)r.Values["id"]));

        // A cell outside the key leaves the id alone; a key cell gives the row another id.
        await repo.EditAsync(Seed, n => n["rows"]![0]!["values"]![4] = "Premium");
        Assert.Equal([eu, us], Rows(await repo.ResolveAsync()).Select(r => (string?)r.Values["id"]));
        await repo.EditAsync(Seed, n => n["rows"]![1]!["values"]![3] = "APAC");
        Assert.Equal([eu, Maquettiste.Engine.Model.StableIds.Of(Table, ["0193a3c2-0000-7000-8000-00000000000a", "12.5", "3", "APAC"])],
            Rows(await repo.ResolveAsync()).Select(r => (string?)r.Values["id"]));
    }

    // Foreign keys by natural key (next-phase-2-design.md section 3.4).
    private static string Nk(int n) => "01JRDN000000000000000000" + n.ToString("D2", CultureInfo.InvariantCulture);

    /// <summary>
    /// customers (a stable id, found by email), invoices (a stable id found by number; customer_id through the entry
    /// fk_invoices_customer, nullable) and lines (a stable id found by (invoice_id, line_no), invoice_id through the entry
    /// fk_lines_invoice), each with a seed; the invoices seed is named aInvoices, so by name it comes before customers.
    /// </summary>
    private static async Task<ReferenceDataRepo> NaturalKeysAsync()
    {
        var repo = Create(pack: false);
        await repo.CreateAsync($$"""
            {"kind":"table","id":"{{Nk(1)}}","name":"customers","database":"{{MainDatabaseId}}","columns":[
              {"id":"{{Nk(2)}}","name":"id","type":"uuid","nullable":false,"stableId":true},
              {"id":"{{Nk(3)}}","name":"email","type":"string","length":254,"nullable":false}],
             "primaryKey":{"columns":["{{Nk(2)}}"]},"uniques":[{"id":"{{Nk(4)}}","name":"uq_customers_email","columns":["{{Nk(3)}}"]}]}
            """);
        await repo.CreateAsync($$"""
            {"kind":"table","id":"{{Nk(10)}}","name":"invoices","database":"{{MainDatabaseId}}","columns":[
              {"id":"{{Nk(11)}}","name":"id","type":"uuid","nullable":false,"stableId":true},
              {"id":"{{Nk(12)}}","name":"number","type":"string","length":32,"nullable":false},
              {"id":"{{Nk(13)}}","name":"customer_id","type":"uuid"}],
             "primaryKey":{"columns":["{{Nk(11)}}"]},"uniques":[{"id":"{{Nk(14)}}","name":"uq_invoices_number","columns":["{{Nk(12)}}"]}],
             "foreignKeys":[{"id":"{{Nk(15)}}","name":"fk_invoices_customer","columns":["{{Nk(13)}}"],"referencesTable":"{{Nk(1)}}"}]}
            """);
        await repo.CreateAsync($$"""
            {"kind":"table","id":"{{Nk(20)}}","name":"lines","database":"{{MainDatabaseId}}","columns":[
              {"id":"{{Nk(21)}}","name":"id","type":"uuid","nullable":false,"stableId":true},
              {"id":"{{Nk(22)}}","name":"invoice_id","type":"uuid","nullable":false},
              {"id":"{{Nk(23)}}","name":"line_no","type":"int32","nullable":false}],
             "primaryKey":{"columns":["{{Nk(21)}}"]},"uniques":[{"id":"{{Nk(24)}}","name":"uq_lines_invoice_line","columns":["{{Nk(22)}}","{{Nk(23)}}"]}],
             "foreignKeys":[{"id":"{{Nk(25)}}","name":"fk_lines_invoice","columns":["{{Nk(22)}}"],"referencesTable":"{{Nk(10)}}"}]}
            """);
        await repo.CreateAsync($$"""
            {"kind":"seed","id":"{{Nk(30)}}","name":"customers","target":"{{Nk(1)}}","key":"{{Nk(4)}}","columns":["{{Nk(3)}}"],
             "rows":[{"id":"{{Nk(31)}}","values":["ada@example.com"]},{"id":"{{Nk(32)}}","values":["grace@example.com"]}]}
            """);
        await repo.CreateAsync($$"""
            {"kind":"seed","id":"{{Nk(40)}}","name":"aInvoices","target":"{{Nk(10)}}","key":"{{Nk(14)}}","columns":["{{Nk(12)}}","{{Nk(15)}}"],
             "rows":[{"id":"{{Nk(41)}}","values":["INV-1","ada@example.com"]},{"id":"{{Nk(42)}}","values":["INV-2"]}]}
            """);
        await repo.CreateAsync($$"""
            {"kind":"seed","id":"{{Nk(50)}}","name":"lines","target":"{{Nk(20)}}","key":"{{Nk(24)}}","columns":["{{Nk(25)}}","{{Nk(23)}}"],
             "rows":[{"id":"{{Nk(51)}}","values":["INV-1",1]},{"id":"{{Nk(52)}}","values":["INV-1",2]}]}
            """);
        return repo;
    }

    private static RSeedTable SeedTable(ResolvedModel model, string name) =>
        model.Databases.Single(d => d.Id == MainDatabaseId).SeedTables.Single(t => t.Table.Name == name);

    [Fact]
    public async Task Data_rows_carry_refs_by_foreign_key_name()
    {
        await using var repo = await NaturalKeysAsync();
        var model = await repo.ResolveAsync();

        var seed = model.Seeds.Single(x => x.Id == Nk(40));
        Assert.Equal(("foreign-key", "fk_invoices_customer"), (seed.Columns[1].Kind, seed.Columns[1].ForeignKey!.Name));
        var invoices = SeedTable(model, "invoices").Rows;
        var ada = SeedTable(model, "customers").Rows[0];
        var link = Assert.Single(invoices[0].Refs).Value;
        Assert.Equal(("fk_invoices_customer", Nk(41) + "/fk_invoices_customer", "data-ref"), (invoices[0].Refs.Keys.Single(), link.Id, link.Kind));
        Assert.Same(ada, link.Row);
        Assert.Equal(ada.Values["id"], invoices[0].Values["customer_id"]);
        // A row's dependencies cover the seeds its lookups read.
        Assert.Contains("e:" + Nk(30), invoices[0].Dependencies);
        Assert.Contains("e:" + Nk(30), SeedTable(model, "invoices").Dependencies);

        // A null cell names no row: no ref, and the column is null.
        Assert.Empty(invoices[1].Refs);
        Assert.True(invoices[1].Values.ContainsKey("customer_id"));
        Assert.Null(invoices[1].Values["customer_id"]);
    }

    [Fact]
    public async Task A_stable_id_in_a_key_part_comes_from_the_referenced_row()
    {
        await using var repo = await NaturalKeysAsync();
        var model = await repo.ResolveAsync();

        var invoice = Maquettiste.Engine.Model.StableIds.Of(Nk(10), ["INV-1"]);
        var lines = SeedTable(model, "lines");
        Assert.Equal(["invoice_id", "line_no"], lines.KeyColumns.Select(c => c.Name));
        Assert.Equal(new object?[] { invoice, 1L }, lines.Rows[0].Key);
        Assert.Equal([Maquettiste.Engine.Model.StableIds.Of(Nk(20), [invoice, "1"]), Maquettiste.Engine.Model.StableIds.Of(Nk(20), [invoice, "2"])],
            lines.Rows.Select(r => r.Values["id"]));
    }

    [Fact]
    public async Task Stable_ids_do_not_depend_on_table_order()
    {
        await using var repo = await NaturalKeysAsync();

        // One seed's rows built alone, before anything else of the database, reach the referenced rows on demand.
        var alone = await repo.ResolveAsync();
        var main = alone.Databases.Single(d => d.Id == MainDatabaseId);
        var first = main.DataRowsOf(alone.Seeds.Single(x => x.Id == Nk(50)));
        var all = SeedTable(await repo.ResolveAsync(), "lines").Rows;
        Assert.Equal(all.Select(r => (r.Id, r.Values["id"], r.Values["invoice_id"])), first.Select(r => (r.Id, r.Values["id"], r.Values["invoice_id"])));
        // The invoice the first line names was completed on the way, with its own stable id and its customer's.
        var invoice = first[0].Refs["fk_lines_invoice"].Row!;
        Assert.Equal(Maquettiste.Engine.Model.StableIds.Of(Nk(10), ["INV-1"]), invoice.Values["id"]);
        Assert.Equal(Maquettiste.Engine.Model.StableIds.Of(Nk(1), ["ada@example.com"]), invoice.Values["customer_id"]);
    }

    [Fact]
    public async Task Insert_order_follows_natural_key_references()
    {
        await using var repo = await NaturalKeysAsync();
        // A category names its parent by code, in the same table; the file lists a child before its parent.
        await repo.CreateAsync($$"""
            {"kind":"table","id":"{{Nk(60)}}","name":"categories","database":"{{MainDatabaseId}}","columns":[
              {"id":"{{Nk(61)}}","name":"id","type":"uuid","nullable":false,"stableId":true},
              {"id":"{{Nk(62)}}","name":"code","type":"string","length":16,"nullable":false},
              {"id":"{{Nk(63)}}","name":"parent_id","type":"uuid"}],
             "primaryKey":{"columns":["{{Nk(61)}}"]},"uniques":[{"id":"{{Nk(64)}}","name":"uq_categories_code","columns":["{{Nk(62)}}"]}],
             "foreignKeys":[{"id":"{{Nk(65)}}","name":"fk_categories_parent","columns":["{{Nk(63)}}"],"referencesTable":"{{Nk(60)}}"}]}
            """);
        await repo.CreateAsync($$"""
            {"kind":"seed","id":"{{Nk(70)}}","name":"categories","target":"{{Nk(60)}}","key":"{{Nk(64)}}","columns":["{{Nk(62)}}","{{Nk(65)}}"],
             "rows":[{"id":"{{Nk(71)}}","values":["child","root"]},{"id":"{{Nk(72)}}","values":["root"]},{"id":"{{Nk(73)}}","values":["grandchild","child"]}]}
            """);
        var model = await repo.ResolveAsync();

        var order = model.SeedsInOrder.Select(x => x.Name).ToList();
        Assert.True(order.IndexOf("customers") < order.IndexOf("aInvoices") && order.IndexOf("aInvoices") < order.IndexOf("lines"), string.Join(", ", order));
        Assert.Equal([Nk(72), Nk(71), Nk(73)], model.Seeds.Single(x => x.Id == Nk(70)).OrderedRows.Select(r => r.Id));
        var categories = SeedTable(model, "categories").Rows;
        Assert.Equal(["root", "child", "grandchild"], categories.Select(r => r.Values["code"]));
        Assert.Equal(Maquettiste.Engine.Model.StableIds.Of(Nk(60), ["root"]), categories[1].Values["parent_id"]);
    }

    [Fact]
    public async Task Two_cold_resolutions_are_identical()
    {
        var a = Dump(await ResolveAsync());
        var b = Dump(await ResolveAsync());
        Assert.Equal(a, b);
        Assert.Contains("seed contains", a, StringComparison.Ordinal);
    }

    private static string Dump(ResolvedModel model)
    {
        var sb = new StringBuilder();
        foreach (var type in model.ReferenceTypes)
        {
            sb.Append("type ").Append(type.Name).Append(' ').Append(string.Join(',', type.Storage.Select(p => p.Key + "=" + p.Value.Strategy))).Append('\n');
            foreach (var row in type.Rows)
                sb.Append("  ").Append(row.Id).Append(' ').Append(row.Code).Append(' ').Append(string.Join(',', row.Values.Select(p => p.Key + "=" + Text(p.Value)))).Append('\n');
        }

        foreach (var seed in model.SeedsInOrder)
        {
            sb.Append("seed ").Append(seed.Name).Append('\n');
            foreach (var row in seed.OrderedRows)
                sb.Append("  ").Append(row.Id).Append(' ').Append(string.Join(',', row.Values.Select(p => p.Key + "=" + Text(p.Value)))).Append('\n');
        }

        sb.Append(ResolutionKit.Dump(model));
        return sb.ToString();
    }

    private static string Text(object? value) => value switch
    {
        null => "-",
        ImmutableArray<object?> list => "[" + string.Join(',', list.Select(Text)) + "]",
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "",
    };

    private static void EditFile(ReferenceDataRepo repo, string modelPath, Action<JsonObject> edit)
    {
        var file = Path.Combine(repo.Repo.ModelRoot, modelPath.Replace('/', Path.DirectorySeparatorChar));
        var node = JsonNode.Parse(File.ReadAllBytes(file))!.AsObject();
        edit(node);
        var schema = node["kind"]!.GetValue<string>() + ".json";
        File.WriteAllBytes(file, TestServices.Json.Write(node, schema, modelPath));
    }

    private static void WriteFile(ReferenceDataRepo repo, string modelPath, JsonObject node, string schema)
    {
        var file = Path.Combine(repo.Repo.ModelRoot, modelPath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllBytes(file, TestServices.Json.Write(node, schema, modelPath));
    }
}
