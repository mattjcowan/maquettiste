using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Migrations;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Tests.Validation;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Migrations;

/// <summary>
/// A <c>rekey</c> fact that moves a SQL object's id (next-phase-1-design.md section 3.4): MQ4106 takes the kind, the baseline is
/// read with the object under its new key (so the diff sees the same object and only the ledger row moves), and fold composes two
/// such moves into one.
/// </summary>
public sealed class SqlObjectRekeyTests
{
    private const string Database = "01SE0000000000000000000001";
    private const string First = "01SE0000000000000000000023";
    private const string Second = "01SE0000000000000000000030";
    private const string Third = "01SE0000000000000000000031";

    private static Migration Rekey(string id, string from, string to, params string[] after) => new()
    {
        Id = id, Name = "", Database = Database, Fact = MigrationFact.Rekey, After = after,
        Keys = [new MigrationRekey { Kind = "sql-object", From = from, To = to, Name = "invoice_audit" }],
    };

    private static PhysicalSnapshot Capture(params string[] objectKeys) => new()
    {
        Database = Database, Name = "billing", Dialect = Dialect.PostgreSql,
        Objects = [.. objectKeys.Select(k => new SnapshotDefinition { Key = k, Name = "invoice_audit", Schema = "billing", Kind = "script", Definition = "phase: after\napply: once\nbody: select 1;" })],
    };

    [Fact]
    public async Task A_rekey_takes_sql_object_keys_and_still_refuses_a_view()
    {
        var b = new ModelBuilder();
        var main = b.Database("main", Dialect.PostgreSql);
        var objects = Rekey(b.NewId(), First, Second) with { Database = main.Id };
        var view = objects with
        {
            Id = b.NewId(), Keys = [new MigrationRekey { Kind = "view", From = "01SE0000000000000000000040", To = "01SE0000000000000000000041" }],
        };
        b.Add(objects).Add(view);

        var report = await ValidationFixture.Validator().ValidateAsync(b.Build(), ValidationScope.All, null, TestContext.Current.CancellationToken);

        Assert.DoesNotContain(report.Diagnostics, d => d.ElementId == objects.Id);
        var d = Assert.Single(report.Diagnostics, d => d.ElementId == view.Id);
        Assert.Equal(("MQ4106", "/keys/0/kind"), (d.Rule, d.JsonPointer));
        Assert.Contains("tables, columns, sequences and SQL objects", d.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_sql_object_key_moves_in_the_baseline_and_two_moves_fold_into_one()
    {
        var a = Rekey("01JFCT00000000000000000031", First, Second);
        var b = Rekey("01JFCT00000000000000000032", Second, Third, a.Id);

        // Read under today's keys, the baseline holds the object under its last id; a key already taken does not move.
        var today = BaselineCheck.ToTodayKeys(Capture(First), [a, b], _ => true);
        Assert.Equal([Third], today.Objects.Select(o => o.Key));
        Assert.Equal([First, Second], BaselineCheck.ToTodayKeys(Capture(First, Second), [a], _ => true).Objects.Select(o => o.Key));
        Assert.Equal((null, Third), FactKeys.Moves([a, b]).Current(-1, "sql-object", null, First));

        // Fold keeps one rekey from the first id to the last.
        var plan = FactFolder.Fold([a, b], _ => true, Capture(Third), Dialect.PostgreSql);
        var kept = Assert.Single(plan.Changed, f => f.Id == b.Id);
        var key = Assert.Single(kept.Keys);
        Assert.Equal(("sql-object", First, Third, "invoice_audit"), (key.Kind, key.From, key.To, key.Name));
        Assert.Equal([a.Id], plan.Deleted);
    }
}
