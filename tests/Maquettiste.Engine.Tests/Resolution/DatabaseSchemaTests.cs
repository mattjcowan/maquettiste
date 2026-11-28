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
