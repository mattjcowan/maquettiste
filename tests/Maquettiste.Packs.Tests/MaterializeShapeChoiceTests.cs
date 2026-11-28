using System.Text;
using System.Text.Json.Nodes;
using Maquettiste.Engine;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Pipeline;
using Maquettiste.Testing;

namespace Maquettiste.Packs.Tests;

/// <summary>
/// "Create tables…" (materialize-tables) with its choices (retire-projection-design.md section 7): a hierarchy stored as one table, a
/// table per concrete entity or a table per type (the <c>hierarchy</c> parameter), a list stored in a table of its own or a JSON column
/// (<c>listStorage</c>), a many-to-many relation in a junction table with its relation mapping, the plan's choices, and the refusals.
/// Over the billing model's dialects variant before its tables are made, in its SQL Server database (<c>reporting</c>), where
/// CreditNote derives from Invoice and no entity is bound yet.
/// </summary>
public sealed class MaterializeShapeChoiceTests
{
    private const string Main = "01J92P0V2A0000000000000001";
    private const string Customer = "01J92P0V0ETQKXXP951CMMNHH3";
    private const string Invoice = "01J92P0V0FJ23CGSNKM7P1W5V7";
    private const string CreditNote = "01J92P0V2C0000000000000001";
    private const string InvoiceLine = "01J92P0V0GWFR78HZH0P8Z3GY7";
    private const string Payment = "01J92P0V0HEGSC6MW92CST5KA6";
    private const string Product = "01J92P0V0JR8BE8253SKT29ZG7";
    private const string Settles = "01J92P0V1DKHD5Q02DV6S5M53D";
    private const string Phones = "01K9MATER00000000000000001";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Op(string extra = "", params string[] entities) =>
        $$"""{"op":"materialize-tables","database":"{{Main}}","entities":[{{string.Join(",", (entities.Length == 0 ? [Customer, Invoice, CreditNote, InvoiceLine, Payment, Product] : entities).Select(e => "\"" + e + "\""))}}]{{extra}}}""";

    private static async Task<(BatchResult Result, ModelSnapshot Snapshot, int Errors, string Problems)> ApplyAsync(PackRepo repo, string operation)
    {
        await using var store = new ModelStore(repo.Repo.Options);
        var parsed = store.ParseBatch(Encoding.UTF8.GetBytes("{\"operations\":[" + operation + "]}"));
        Assert.Empty(parsed.Diagnostics);
        var result = await store.ApplyBatchAsync(parsed.Batch!, ChangeSource.Editor, Ct);
        var validation = await store.ValidateAsync(ValidationScope.All, Ct);
        var errors = validation.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        var problems = string.Join("\n", result.Items.SelectMany(i => i.Diagnostics).Concat(errors).Select(d => d.Rule + " " + d.Message));
        return (result, store.Current!, errors.Count, problems);
    }

    private static Table TableNamed(ModelSnapshot snapshot, string name) => snapshot.All<Table>().Single(t => t.Database == Main && t.Name == name);

    private static EntityBinding BindingOf(ModelSnapshot snapshot, string entity) => snapshot.Get<Entity>(entity)!.Bindings.Single(b => b.Database == Main);

    private static void AddPhoneNumbers(PackRepo repo) =>
        repo.EditJson(".maquettiste/model/entities/customer.json", e => e["attributes"]!.AsArray().Add(new JsonObject
        {
            ["id"] = Phones, ["name"] = "phoneNumbers", ["type"] = "string", ["length"] = 20, ["collection"] = true,
        }));

    [Fact]
    public async Task One_table_is_the_default_and_the_root_names_the_type_column()
    {
        using var repo = PackRepo.BillingDialectsWithoutTables();

        var (result, snapshot, errors, problems) = await ApplyAsync(repo, Op());

        Assert.True(result.Outcome == SaveOutcome.Saved && errors == 0, problems);
        var invoices = TableNamed(snapshot, "invoices");
        var type = invoices.Columns.Single(c => c.Name == "discriminator");
        var root = BindingOf(snapshot, Invoice);
        var derived = BindingOf(snapshot, CreditNote);
        Assert.Equal((invoices.Id, type.Id), (root.Source, root.DiscriminatorColumn));
        Assert.Equal((invoices.Id, (string?)null), (derived.Source, derived.DiscriminatorColumn));
        Assert.Contains(root.Columns, c => c.Column == invoices.Columns.Single(x => x.Name == "reason").Id && c.Status == BindingColumnStatus.Ignored);
        Assert.DoesNotContain(snapshot.All<Table>(), t => t.Name == "credit_notes");
    }

    [Fact]
    public async Task A_table_per_type_keys_the_derived_table_by_its_base_and_its_binding_names_the_base_foreign_key()
    {
        using var repo = PackRepo.BillingDialectsWithoutTables();
        await using (var store = new ModelStore(repo.Repo.Options))
        {
            var plan = await store.PlanMaterializeAsync(new MaterializeRequest("materialize-tables", Main, [Customer, Invoice, CreditNote, InvoiceLine, Payment, Product])
            {
                Hierarchy = new Dictionary<string, string> { [Invoice] = "per-type" },
            }, Ct);
            var choice = Assert.Single(plan.Choices!.Hierarchies);
            Assert.Equal((Invoice, "Invoice", "per-type", "one-table"), (choice.Root, choice.RootName, choice.Shape, choice.Default));
            Assert.Equal(["Invoice", "CreditNote"], choice.Entities);
        }

        var (result, snapshot, errors, problems) = await ApplyAsync(repo, Op($$""","hierarchy":{"{{Invoice}}":"per-type"}"""));

        Assert.True(result.Outcome == SaveOutcome.Saved && errors == 0, problems);
        var invoices = TableNamed(snapshot, "invoices");
        var creditNotes = TableNamed(snapshot, "credit_notes");
        Assert.DoesNotContain(invoices.Columns, c => c.Name is "discriminator" or "reason");
        var baseKey = creditNotes.ForeignKeys.Single(f => f.ReferencesTable == invoices.Id);
        Assert.Equal(baseKey.Columns, creditNotes.PrimaryKey!.Columns);
        Assert.Equal(ReferentialAction.Cascade, baseKey.OnDelete);
        var derived = BindingOf(snapshot, CreditNote);
        Assert.Equal((creditNotes.Id, baseKey.Id), (derived.Source, derived.BaseForeignKey));
        Assert.DoesNotContain(derived.Fields, f => baseKey.Columns.Contains(f.Column));
        Assert.Null(BindingOf(snapshot, Invoice).DiscriminatorColumn);
    }

    [Fact]
    public async Task A_table_per_concrete_entity_maps_every_attribute_and_an_abstract_base_gets_no_table()
    {
        using var repo = PackRepo.BillingDialectsWithoutTables();
        repo.EditJson(".maquettiste/model/entities/credit-note.json", e => e["base"] = null);
        repo.Write(".maquettiste/model/entities/document.json", """
            { "kind": "entity", "id": "01K9MATER00000000000000020", "name": "Document", "package": "01J92P0V01KDRN8GX5PGYCNKSX", "abstract": true,
              "key": { "attributes": ["01K9MATER00000000000000021"], "strategy": "application" },
              "attributes": [ { "id": "01K9MATER00000000000000021", "name": "id", "type": "uuid", "required": true },
                              { "id": "01K9MATER00000000000000022", "name": "title", "type": "string", "length": 80, "required": true } ] }
            """);
        repo.EditJson(".maquettiste/model/entities/credit-note.json", e =>
        {
            e["base"] = "01K9MATER00000000000000020";
            e.AsObject().Remove("key");
        });

        var (result, snapshot, errors, problems) = await ApplyAsync(repo, Op("""
            ,"hierarchy":"per-concrete"
            """, "01K9MATER00000000000000020", CreditNote));

        Assert.True(result.Outcome == SaveOutcome.Saved && errors == 0, problems);
        Assert.DoesNotContain(snapshot.All<Table>(), t => t.Name == "documents");
        var creditNotes = TableNamed(snapshot, "credit_notes");
        Assert.Equal(["id", "title", "reason"], creditNotes.Columns.Select(c => c.Name));
        Assert.Equal(3, BindingOf(snapshot, CreditNote).Fields.Count);
        Assert.Empty(snapshot.Get<Entity>("01K9MATER00000000000000020")!.Bindings);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("json")]
    public async Task A_list_goes_to_a_child_table_with_a_child_tables_entry_or_to_a_json_column(string? storage)
    {
        using var repo = PackRepo.BillingDialectsWithoutTables();
        AddPhoneNumbers(repo);

        var (result, snapshot, errors, problems) = await ApplyAsync(repo, Op(storage is null ? "" : $$""","listStorage":{"{{Phones}}":"{{storage}}"}"""));

        Assert.True(result.Outcome == SaveOutcome.Saved && errors == 0, problems);
        var customers = TableNamed(snapshot, "customers");
        var binding = BindingOf(snapshot, Customer);
        if (storage == "json")
        {
            var column = customers.Columns.Single(c => c.Name == "phone_numbers");
            Assert.Equal("json", column.Type);
            Assert.Contains(binding.Fields, f => f.Attribute == Phones && f.Column == column.Id);
            Assert.Empty(binding.ChildTables);
            return;
        }

        var child = TableNamed(snapshot, "customers_phone_numbers");
        Assert.Equal(["customer_id", "position", "phone_number"], child.Columns.Select(c => c.Name));
        var entry = Assert.Single(binding.ChildTables);
        Assert.Equal((Phones, child.Id), (entry.Attribute, entry.Table));
        Assert.Equal(child.ForeignKeys.Single(f => f.ReferencesTable == customers.Id).Id, entry.OwnerForeignKey);
        Assert.Equal(child.Columns.Single(c => c.Name == "position").Id, entry.OrderColumn);
        Assert.Equal([(Phones, child.Columns.Single(c => c.Name == "phone_number").Id)], entry.Fields.Select(f => (f.Attribute, f.Column)));
    }

    [Fact]
    public async Task A_many_to_many_relation_gets_a_designed_junction_table_and_its_mapping_names_it()
    {
        using var repo = PackRepo.BillingDialectsWithoutTables();

        var (result, snapshot, errors, problems) = await ApplyAsync(repo, Op());

        Assert.True(result.Outcome == SaveOutcome.Saved && errors == 0, problems);
        var junction = TableNamed(snapshot, "payment_invoice");
        var mapping = snapshot.All<Mapping>().Single(m => m.Database == Main && m.Relation == Settles);
        Assert.Equal(junction.Id, mapping.JunctionTable);
        Assert.Null(mapping.Shape);
        Assert.Equal(junction.ForeignKeys.Select(f => f.Id).Order(StringComparer.Ordinal), mapping.Ends.Select(e => e.ForeignKey!).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task A_hierarchy_is_named_whole_and_unknown_choices_are_refused()
    {
        using var repo = PackRepo.BillingDialectsWithoutTables();
        await using var store = new ModelStore(repo.Repo.Options);

        async Task<string> Refusal(MaterializeRequest request)
        {
            var plan = await store.PlanMaterializeAsync(request, Ct);
            Assert.False(plan.Valid);
            return Assert.Single(plan.Diagnostics).Message;
        }

        Assert.Contains("name every entity of the hierarchy", await Refusal(new MaterializeRequest("materialize-tables", Main, [CreditNote])), StringComparison.Ordinal);
        Assert.Contains("is not one-table, per-concrete or per-type", await Refusal(new MaterializeRequest("materialize-tables", Main, [Invoice, CreditNote])
        {
            Hierarchy = new Dictionary<string, string> { ["*"] = "tpt" },
        }), StringComparison.Ordinal);
        Assert.Contains("is not the root entity of a hierarchy", await Refusal(new MaterializeRequest("materialize-tables", Main, [Invoice, CreditNote])
        {
            Hierarchy = new Dictionary<string, string> { [CreditNote] = "per-type" },
        }), StringComparison.Ordinal);
        Assert.Contains("is not a list attribute", await Refusal(new MaterializeRequest("materialize-tables", Main, [Invoice, CreditNote])
        {
            ListStorage = new Dictionary<string, string> { [Invoice] = "json" },
        }), StringComparison.Ordinal);
    }

    private const string Local = "01J92P0V2A0000000000000002";
    private const string PaymentMethod = "01J92P0V2F0000000000000001";
    private const string Places = "01J92P0V1ACKN3G6TK3NJDTM82";

    [Fact]
    public async Task A_reference_column_under_a_check_gets_the_check_the_pack_wrote_declared_by_the_table()
    {
        using var repo = PackRepo.BillingDialectsWithoutTables();

        var (result, snapshot, errors, problems) = await ApplyAsync(repo, Op());

        Assert.True(result.Outcome == SaveOutcome.Saved && errors == 0, problems);
        var payments = TableNamed(snapshot, "payments");
        var method = payments.Columns.Single(c => c.Name == "method");
        var check = Assert.Single(payments.Checks, c => c.Name == "ck_payments_method_ref");
        Assert.Equal(method.Id, check.Column);
        Assert.Contains("method IN (", Assert.Single(check.Expression.Values), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_reference_column_under_a_lookup_table_gets_a_foreign_key_into_the_lookup_table_now_designed_and_bound()
    {
        using var repo = PackRepo.BillingDialectsWithoutTables();

        var (result, snapshot, errors, problems) = await ApplyAsync(repo, Op().Replace(Main, Local, StringComparison.Ordinal));

        Assert.True(result.Outcome == SaveOutcome.Saved && errors == 0, problems);
        var lookup = snapshot.All<Table>().Single(t => t.Database == Local && t.Name == "payment_methods");
        Assert.Equal(["code", "label"], lookup.Columns.Select(c => c.Name).Take(2));
        var payments = snapshot.All<Table>().Single(t => t.Database == Local && t.Name == "payments");
        var key = Assert.Single(payments.ForeignKeys, f => f.Name == "fk_payments_method_ref");
        Assert.Equal((lookup.Id, lookup.Columns.Single(c => c.Name == "code").Id), (key.ReferencesTable, key.ReferencesColumns.Single()));
        // The type's rows now go into the designed table: the type is bound to it in that database.
        var binding = Assert.Single(snapshot.Get<ReferenceType>(PaymentMethod)!.Bindings, b => b.Database == Local);
        Assert.Equal(lookup.Id, binding.Table);
        Assert.Contains(binding.Fields, f => f.Attribute == "code");
    }

    [Fact]
    public async Task A_postgresql_enum_column_keeps_its_type_through_a_database_type_and_is_refused_without_one()
    {
        using var repo = PackRepo.ReferenceData("native");
        repo.EditJson(".maquettiste/model/reference-types/unit-of-measure.json", t => t.AsObject().Remove("storage"));
        const string db = PackRepo.MainDatabaseId;
        var entities = string.Join(",", new[] { "01JRDE00000000000000000001", "01JRDE00000000000000000002" }.Select(e => "\"" + e + "\""));
        string Op(string extra = "") => $$"""{"op":"materialize-tables","database":"{{db}}","entities":[{{entities}}]{{extra}}}""";

        // A list kept in an array of the enum type has no binding form; a column of the enum type needs a database type to name it.
        var (list, _, _, listProblems) = await ApplyAsync(repo, Op());
        Assert.NotEqual(SaveOutcome.Saved, list.Outcome);
        Assert.Contains("array of the PostgreSQL enum type", listProblems, StringComparison.Ordinal);
        var (column, _, _, columnProblems) = await ApplyAsync(repo, Op(""","listStorage":"json" """));
        Assert.NotEqual(SaveOutcome.Saved, column.Outcome);
        Assert.Contains("'unit_of_measure_t'", columnProblems, StringComparison.Ordinal);

        repo.Write(".maquettiste/model/databases/main/types/unit-of-measure-t.json", $$"""
            { "kind": "database-type", "id": "01K9MATER00000000000000040", "name": "unit_of_measure_t", "database": "{{db}}", "typeKind": "enum",
              "members": ["kg", "g", "l", "ml", "pc"] }
            """);
        var (saved, snapshot, _, problems) = await ApplyAsync(repo, Op(""","listStorage":"json" """));

        Assert.True(saved.Outcome == SaveOutcome.Saved, problems);
        var ingredients = snapshot.All<Table>().Single(t => t.Database == db && t.Name == "ingredients");
        Assert.Equal("unit_of_measure_t", ingredients.Columns.Single(c => c.Name == "default_unit").NativeType);
    }

    [Fact]
    public async Task A_list_attribute_of_a_relation_kept_in_a_table_is_refused_by_name()
    {
        using var repo = PackRepo.BillingDialectsWithoutTables();
        repo.EditJson(".maquettiste/model/relations/settles.json", r => r["attributes"]!.AsArray().Add(new JsonObject
        {
            ["id"] = "01K9MATER00000000000000050", ["name"] = "remarks", ["type"] = "string", ["length"] = 40, ["collection"] = true,
        }));

        var (result, _, _, problems) = await ApplyAsync(repo, Op());

        Assert.NotEqual(SaveOutcome.Saved, result.Outcome);
        Assert.Contains("Attribute 'remarks' of relation 'settles' is stored in a table of its own", problems, StringComparison.Ordinal);
    }

    /// <summary>
    /// A value object inside a list's items is embedded in the list's child table, never a table inside it (the projection made none
    /// either; the designer refuses one if it ever arises), and the child table entry maps its members with paths of depth three.
    /// </summary>
    [Fact]
    public async Task A_value_object_inside_a_list_item_is_embedded_in_the_child_table()
    {
        using var repo = PackRepo.BillingDialectsWithoutTables();
        repo.Write(".maquettiste/model/types/price-line.json", """
            { "kind": "value-object", "id": "01K9MATER00000000000000060", "name": "PriceLine", "package": "01J92P0V01KDRN8GX5PGYCNKSX",
              "attributes": [ { "id": "01K9MATER00000000000000061", "name": "label", "type": "string", "length": 40 },
                              { "id": "01K9MATER00000000000000062", "name": "price", "type": { "ref": "01J92P0V0304M76KA84NE4D1TE" } } ] }
            """);
        repo.EditJson(".maquettiste/model/entities/customer.json", e => e["attributes"]!.AsArray().Add(new JsonObject
        {
            ["id"] = "01K9MATER00000000000000063", ["name"] = "priceLines", ["type"] = new JsonObject { ["ref"] = "01K9MATER00000000000000060" }, ["collection"] = true,
        }));

        var (result, snapshot, errors, problems) = await ApplyAsync(repo, Op());

        Assert.True(result.Outcome == SaveOutcome.Saved && errors == 0, problems);
        var child = TableNamed(snapshot, "customers_price_lines");
        Assert.Equal(["customer_id", "position", "label", "price_amount", "price_currency"], child.Columns.Select(c => c.Name));
        var entry = Assert.Single(BindingOf(snapshot, Customer).ChildTables);
        Assert.Contains(entry.Fields, f => f.Attribute == "01K9MATER00000000000000063.01K9MATER00000000000000062.01J92P0V08P9BVJAPQ793XYNTQ");
    }

    [Fact]
    public async Task A_relation_attribute_on_a_foreign_key_is_named_in_the_relation_mapping()
    {
        using var repo = PackRepo.BillingDialectsWithoutTables();
        // The relation keeps its foreign key (a relation with attributes is a junction unless its mapping says otherwise).
        repo.Write(".maquettiste/model/mappings/places-reporting.json", $$"""
            { "kind": "mapping", "id": "01K9MATER00000000000000072", "name": "places in reporting", "database": "{{Main}}", "relation": "{{Places}}", "shape": "foreign-key" }
            """);
        repo.EditJson(".maquettiste/model/relations/places.json", r => r["attributes"] = new JsonArray(new JsonObject
        {
            ["id"] = "01K9MATER00000000000000070", ["name"] = "channel", ["type"] = "string", ["length"] = 20,
        }));

        var (result, snapshot, errors, problems) = await ApplyAsync(repo, Op());

        Assert.True(result.Outcome == SaveOutcome.Saved && errors == 0, problems);
        var invoices = TableNamed(snapshot, "invoices");
        var channel = invoices.Columns.Single(c => c.Name == "channel");
        var mapping = snapshot.All<Mapping>().Single(m => m.Database == Main && m.Relation == Places);
        Assert.Equal(invoices.ForeignKeys.Single(f => f.ReferencesTable == TableNamed(snapshot, "customers").Id).Id, mapping.ForeignKey);
        var attribute = Assert.Single(mapping.Attributes);
        Assert.Equal(("01K9MATER00000000000000070", channel.Id), (attribute.Attribute, attribute.Column));
    }
}
