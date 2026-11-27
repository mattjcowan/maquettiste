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

    /// <summary>
    /// Fold takes the open facts a baseline lacks, so a materialize's rekey fact can be folded before any run renders it: what each
    /// fact adopted stays on the kept fact.
    /// </summary>
    [Fact]
    public void Folding_two_open_rekey_facts_keeps_what_each_adopted()
    {
        var table = new MigrationRekey { Kind = "table", From = "01JFCT0000000000000000T001", To = "01JFCT0000000000000000T001", Name = "payment_methods" };
        var check = new MigrationRekey { Kind = "check", Table = "01JFCT0000000000000000T002", From = "01JFCT0000000000000000C001", To = "01JFCT0000000000000000C001", Name = "ck_payments_method_ref" };
        var a = Rekey("01JFCT00000000000000000031", First, Second) with { Adopted = [table] };
        var b = Rekey("01JFCT00000000000000000032", Second, Third, a.Id) with { Adopted = [check] };

        var plan = FactFolder.Fold([a, b], _ => true, Capture(Third), Dialect.PostgreSql);

        var kept = Assert.Single(plan.Changed, f => f.Id == b.Id);
        Assert.Equal([table, check], kept.Adopted);
    }

    /// <summary>
    /// <c>validate --against</c> a baseline taken before a materialize reads what the materialize adopted as already there, as the next
    /// schema diff does, so the comparison reports no change for it.
    /// </summary>
    [Fact]
    public void A_baseline_check_reads_what_a_materialize_adopted_as_already_there()
    {
        const string projected = "01JFCT0000000000000000P001@01SE0000000000000000000001", stored = "01JFCT0000000000000000T002";
        var check = new SnapshotCheck { Key = "ck:x", Name = "ck_payments_method_ref", Expression = "method IN ('card')" };
        var baseline = new PhysicalSnapshot
        {
            Database = Database, Name = "billing", Dialect = Dialect.PostgreSql,
            Tables = [new SnapshotTable
            {
                Key = projected, Name = "payments",
                Columns = [new SnapshotColumn { Key = "a1", Name = "method", Type = "reference", Length = 8, NativeType = "varchar(8)", ReferenceType = "rt", Strategy = "check" }],
            }],
        };
        var working = baseline with
        {
            Tables = [new SnapshotTable
            {
                Key = stored, Name = "payments", Checks = [check],
                Columns = [new SnapshotColumn { Key = "c1", Name = "method", Type = "string", Length = 8, NativeType = "varchar(8)" }],
            }],
        };
        var rekey = new Migration
        {
            Id = "01JFCT00000000000000000033", Name = "", Database = Database, Fact = MigrationFact.Rekey,
            Keys = [new MigrationRekey { Kind = "table", From = projected, To = stored }, new MigrationRekey { Kind = "column", Table = projected, From = "a1", To = "c1" }],
            Adopted = [new MigrationRekey { Kind = "check", Table = stored, From = "x", To = "x", Name = "ck_payments_method_ref" }],
        };

        // The materialize's rekey fact is one the baseline lacks: its stored reference column and adopted check need no fact.
        var today = BaselineCheck.ToTodayKeys(baseline, [rekey], _ => true);
        Assert.Empty(BaselineCheck.Check(today, working, [rekey], _ => true, Dialect.PostgreSql, Database));
        // Without the materialize, the same column is a change of type that needs its fact.
        var other = rekey with { Fact = MigrationFact.Rename, Keys = [], Adopted = [] };
        Assert.NotEmpty(BaselineCheck.Check(BaselineCheck.ToTodayKeys(baseline, [rekey], _ => true), working, [other], _ => true, Dialect.PostgreSql, Database));
    }
}
