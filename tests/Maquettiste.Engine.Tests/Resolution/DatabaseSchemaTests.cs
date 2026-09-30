using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Tests.Store;
using Maquettiste.Engine.Tests.Validation;
using Maquettiste.Engine.Validation;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Resolution;

/// <summary>
/// Database schemas (erratum E26): where a conventional table goes (the entity's mapping, else the nearest convention package
/// entry up the package tree, else the default schema), MQ4014, the canonical form of a convention entry, and the batch schema
/// operations with MQ4015.
/// </summary>
public sealed class DatabaseSchemaTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<IReadOnlyList<Diagnostic>> Diagnostics(ModelSnapshot model) =>
        (await ValidationFixture.Validator().ValidateAsync(model, ValidationScope.All, null, Ct)).Diagnostics;

    private static Dictionary<string, string?> Placement(ModelBuilder b) =>
        ResolutionKit.Resolve(b).Db("main").Tables.ToDictionary(t => t.Name, t => t.Schema, StringComparer.Ordinal);

    [Fact]
    public void The_mapping_wins_then_the_nearest_package_entry_then_the_default()
    {
        var b = new ModelBuilder(71);
        var sales = b.Package("Sales");
        var orders = b.Package("Orders", sales);
        var returns = b.Package("Returns", sales);
        var ops = b.Package("Ops");
        b.Entity("Order", orders).Key("id", "uuid");
        b.Entity("Refund", returns).Key("id", "uuid");
        b.Entity("Log", ops).Key("id", "uuid");
        var audit = b.Entity("Audit", ops).Key("id", "uuid");
        var main = b.Database("main", Dialect.PostgreSql).ByConvention(ConventionMapping.All);
        var salesSchema = main.Schema("sales");
        var ordersSchema = main.Schema("orders");
        var auditSchema = main.Schema("audit");
        main.Package(sales, salesSchema).Package(orders, ordersSchema).Packages(ops);
        b.Mapping(main, audit).Schema(auditSchema);

        var placed = Placement(b);

        Assert.Equal("orders", placed["orders"]); // the nearest entry (Orders) wins over its parent's (Sales)
        Assert.Equal("sales", placed["refunds"]); // no entry for Returns: its parent's
        Assert.Equal("public", placed["logs"]); // an entry without a schema: the default
        Assert.Equal("audit", placed["audits"]); // the mapping wins over the package entry
    }

    [Fact]
    public void Without_schemas_placement_is_unchanged()
    {
        var b = new ModelBuilder(72);
        var sales = b.Package("Sales");
        b.Entity("Order", sales).Key("id", "uuid");
        b.Database("main", Dialect.SqlServer).Packages(sales);
        Assert.Equal("dbo", Placement(b)["orders"]);
    }

    [Fact]
    public async Task A_schema_the_database_does_not_declare_is_MQ4014()
    {
        var b = new ModelBuilder(73);
        var sales = b.Package("Sales");
        var order = b.Entity("Order", sales).Key("id", "uuid");
        var main = b.Database("main", Dialect.PostgreSql).ByConvention(ConventionMapping.Packages);
        var other = b.Database("other", Dialect.PostgreSql);
        var foreign = other.Schema("elsewhere");
        main.Package(sales, foreign);
        b.Mapping(main, order).Schema(foreign);

        var found = (await Diagnostics(b.Build())).Where(d => d.Rule == "MQ4014").OrderBy(d => d.JsonPointer, StringComparer.Ordinal).ToList();

        Assert.Equal(["/packages/0/schema", "/schema"], found.Select(d => d.JsonPointer));
        Assert.All(found, d => Assert.Equal(DiagnosticSeverity.Error, d.Severity));
    }

    [Fact]
    public void A_convention_entry_without_a_schema_is_written_as_the_package_id()
    {
        const string Package = "01J92P0V0FJ23CGSNKM7P1W5V7";
        const string Schema = "01J92P0V1RC04SKQ5353EAKHG2";
        var json = $$"""{"kind":"database","id":"01J92P0V1QRN2181XM2ZWE02W4","name":"main","dialect":"postgresql","byConvention":"packages","packages":[{"package":"{{Package}}"},{"package":"{{Package}}","schema":"{{Schema}}"}]}""";

        var written = Encoding.UTF8.GetString(TestServices.Json.Write(JsonNode.Parse(json)!, "database.json", ".maquettiste/model/databases/main/database.json"));
        var read = JsonSerializer.Deserialize<Database>(written, EngineJson.Options)!;

        Assert.Contains("\"packages\": [\n    \"" + Package + "\",\n    {\n      \"package\": \"" + Package + "\",\n      \"schema\": \"" + Schema + "\"\n    }\n  ]", written, StringComparison.Ordinal);
        Assert.Equal([new ConventionPackage { Package = Package }, new ConventionPackage { Package = Package, Schema = Schema }], read.Packages);
        Assert.Contains("\"packages\":[\"" + Package + "\",{\"package\":\"" + Package + "\",\"schema\":\"" + Schema + "\"}]", JsonSerializer.Serialize(read, EngineJson.Options), StringComparison.Ordinal);
    }

    private static ModelBatch Batch(params BatchOperation[] operations) => new(operations);

    private static BatchOperation Op(BatchOp op, string database, string? schema = null, string? name = null, string? target = null, string? @default = null) =>
        new(op, database, null, null, Schema: schema, Name: name, Target: target, Default: @default);

    [Fact]
    public async Task Add_rename_and_set_default_edit_the_database_and_the_default_follows_a_rename()
    {
        await using var s = await BillingStore.OpenAsync();
        var db = s.Id("database", "main");

        var added = await s.Store.ApplyBatchAsync(Batch(Op(BatchOp.AddSchema, db, name: "archive"), Op(BatchOp.RenameSchema, db, schema: "01J92P0V1RC04SKQ5353EAKHG2", name: "ledger")), ChangeSource.Editor, Ct);
        Assert.Equal(SaveOutcome.Saved, added.Outcome);
        var database = s.Model.Get<Database>(db)!;
        Assert.Equal(["ledger", "archive"], database.Schemas.Select(x => x.Name));
        Assert.Equal("ledger", database.DefaultSchema);

        var archive = database.Schemas[1].Id;
        var defaulted = await s.Store.ApplyBatchAsync(Batch(Op(BatchOp.SetDefaultSchema, db, schema: archive)), ChangeSource.Editor, Ct);
        Assert.Equal(SaveOutcome.Saved, defaulted.Outcome);
        Assert.Equal("archive", s.Model.Get<Database>(db)!.DefaultSchema);

        var taken = await s.Store.ApplyBatchAsync(Batch(Op(BatchOp.AddSchema, db, name: "Ledger")), ChangeSource.Editor, Ct);
        Assert.Equal(SaveOutcome.Invalid, taken.Outcome);
        Assert.Equal("MQ4015", Assert.Single(taken.Items[0].Diagnostics).Rule);
    }

    [Fact]
    public async Task Remove_refuses_while_something_lives_there_and_moves_it_to_a_target()
    {
        await using var s = await BillingStore.OpenAsync();
        var db = s.Id("database", "main");
        const string Billing = "01J92P0V1RC04SKQ5353EAKHG2";
        Assert.Equal(SaveOutcome.Saved, (await s.Store.ApplyBatchAsync(Batch(Op(BatchOp.AddSchema, db, schema: "01J92P0V1RC04SKQ5353EAKHG3", name: "archive")), ChangeSource.Editor, Ct)).Outcome);
        const string Archive = "01J92P0V1RC04SKQ5353EAKHG3";
        var before = s.Files();

        var isDefault = await s.Store.ApplyBatchAsync(Batch(Op(BatchOp.RemoveSchema, db, schema: Billing, target: Archive)), ChangeSource.Editor, Ct);
        Assert.Equal(SaveOutcome.Invalid, isDefault.Outcome);
        Assert.Contains("default", Assert.Single(isDefault.Items[0].Diagnostics).Message, StringComparison.Ordinal);

        var occupied = await s.Store.ApplyBatchAsync(Batch(Op(BatchOp.RemoveSchema, db, schema: Billing, @default: Archive)), ChangeSource.Editor, Ct);
        var refusal = Assert.Single(occupied.Items[0].Diagnostics);
        Assert.Equal(("MQ4015", "/operations/0"), (refusal.Rule, refusal.JsonPointer));
        Assert.Contains("view 'outstanding_invoices'", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("sequence 'invoice_number_seq'", refusal.Message, StringComparison.Ordinal);
        Assert.Equal(before, s.Files());

        var moved = await s.Store.ApplyBatchAsync(Batch(Op(BatchOp.RemoveSchema, db, schema: Billing, target: Archive, @default: Archive)), ChangeSource.Editor, Ct);
        Assert.Equal(SaveOutcome.Saved, moved.Outcome);
        var database = s.Model.Get<Database>(db)!;
        Assert.Equal(["archive"], database.Schemas.Select(x => x.Name));
        Assert.Equal("archive", database.DefaultSchema);
        Assert.All(s.Model.All<View>().Cast<Element>().Concat(s.Model.All<Sequence>()), e => Assert.Equal(Archive, e is View v ? v.Schema : ((Sequence)e).Schema));
        Assert.DoesNotContain(await Diagnostics(s.Model), d => d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public async Task Removing_a_schema_that_a_convention_entry_names_moves_the_entry()
    {
        await using var s = await BillingStore.OpenAsync();
        var main = s.Doc("database", "main");
        var billingPackage = s.Id("package", "Billing");
        const string Billing = "01J92P0V1RC04SKQ5353EAKHG2";
        var scoped = await s.Store.SaveAsync(main.Element.Id, BillingStore.Edit(main, n =>
        {
            n["schemas"]!.AsArray().Add(new JsonObject { ["id"] = "01J92P0V1RC04SKQ5353EAKHG3", ["name"] = "archive" });
            n["packages"] = new JsonArray(new JsonObject { ["package"] = billingPackage, ["schema"] = "01J92P0V1RC04SKQ5353EAKHG3" });
        }), main.Hash, ChangeSource.Editor, Ct);
        Assert.Equal(SaveOutcome.Saved, scoped.Outcome);

        var refused = await s.Store.ApplyBatchAsync(Batch(Op(BatchOp.RemoveSchema, s.Id("database", "main"), schema: "01J92P0V1RC04SKQ5353EAKHG3")), ChangeSource.Editor, Ct);
        Assert.Contains("convention entry 'Billing'", Assert.Single(refused.Items[0].Diagnostics).Message, StringComparison.Ordinal);

        var moved = await s.Store.ApplyBatchAsync(Batch(Op(BatchOp.RemoveSchema, s.Id("database", "main"), schema: "01J92P0V1RC04SKQ5353EAKHG3", target: Billing)), ChangeSource.Editor, Ct);
        Assert.Equal(SaveOutcome.Saved, moved.Outcome);
        Assert.Equal([new ConventionPackage { Package = billingPackage, Schema = Billing }], s.Model.Get<Database>(s.Id("database", "main"))!.Packages);
    }

    [Fact]
    public async Task ParseBatch_reads_the_schema_operations()
    {
        await using var s = await BillingStore.OpenAsync(load: false);
        var parsed = s.Store.ParseBatch("""
            { "operations": [
              { "op": "add-schema", "id": "01J92P0V1QRN2181XM2ZWE02W4", "name": "archive" },
              { "op": "remove-schema", "id": "01J92P0V1QRN2181XM2ZWE02W4", "schema": "01J92P0V1RC04SKQ5353EAKHG2", "target": "01J92P0V1RC04SKQ5353EAKHG3", "default": "01J92P0V1RC04SKQ5353EAKHG3" },
              { "op": "rename-schema", "id": "01J92P0V1QRN2181XM2ZWE02W4", "schema": "01J92P0V1RC04SKQ5353EAKHG2" }
            ] }
            """u8);

        var missingName = Assert.Single(parsed.Diagnostics);
        Assert.Equal("MQ1002", missingName.Rule);
        Assert.StartsWith("/operations/2", missingName.JsonPointer, StringComparison.Ordinal);
    }
}
