using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Validation;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Validation;

/// <summary>
/// The migration fact rules on in-memory models (next-phase-1-design.md section 3.2); the file fixture <c>migrations</c> covers each rule
/// once, these the cases around them: holds, widenings, other dialects, order and the files a save revisits.
/// </summary>
public sealed class MigrationRulesTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Task<ValidationReport> Validate(ModelSnapshot model, ValidationScope? scope = null) =>
        ValidationFixture.Validator().ValidateAsync(model, scope ?? ValidationScope.All, null, Ct);

    private static MigrationObject Column(string key, string name) =>
        new() { Kind = "column", Table = "01JENT00000000000000000001@01JDBS00000000000000000001", Key = key, TableName = "invoices", Name = name };

    private static Migration ChangeType(ModelBuilder b, string database, string from, string to, Func<Migration, Migration>? edit = null)
    {
        var fact = new Migration
        {
            Id = b.NewId(), Database = database, Fact = MigrationFact.ChangeType, Object = Column("01JATT00000000000000000001", "number"),
            From = new MigrationState { Type = "string", NativeType = from }, To = new MigrationState { Type = "string", NativeType = to },
        };
        return edit is null ? fact : edit(fact);
    }

    [Fact]
    public async Task Held_facts_skip_MQ4101_until_released()
    {
        var b = new ModelBuilder();
        var main = b.Database("main", Dialect.PostgreSql);
        var held = ChangeType(b, main.Id, "text", "varchar(20)", f => f with { Held = true });
        var released = ChangeType(b, main.Id, "text", "varchar(20)", f => f with { Held = true, Released = true, Object = Column("01JATT00000000000000000002", "code") });
        b.Add(held).Add(released);

        var report = await Validate(b.Build());

        Assert.DoesNotContain(report.Diagnostics, d => d.ElementId == held.Id);
        Assert.Equal(["/conversion", "/dataCheck"], report.Diagnostics.Where(d => d.ElementId == released.Id).Select(d => d.JsonPointer).Order(StringComparer.Ordinal));
        Assert.Equal("held", MigrationRules.State(held, Dialect.PostgreSql));
        Assert.Equal("pending", MigrationRules.State(released, Dialect.PostgreSql));
    }

    [Fact]
    public async Task A_widening_change_type_needs_a_conversion_and_no_data_check()
    {
        var b = new ModelBuilder();
        var main = b.Database("main", Dialect.PostgreSql);
        var fact = ChangeType(b, main.Id, "integer", "bigint");
        b.Add(fact);

        var d = Assert.Single((await Validate(b.Build())).Diagnostics);

        Assert.Equal(("MQ4101", "/conversion"), (d.Rule, d.JsonPointer));
        Assert.Equal(["conversion"], MigrationRules.Missing(fact, Dialect.PostgreSql));
    }

    [Fact]
    public async Task Complete_facts_validate_clean_and_name_their_state()
    {
        var b = new ModelBuilder();
        var main = b.Database("main", Dialect.PostgreSql);
        var narrowing = ChangeType(b, main.Id, "text", "varchar(20)", f => f with
        {
            Conversion = new MigrationConversion { Cast = true },
            DataCheck = new MigrationDataCheck { Query = new Dictionary<string, string> { ["*"] = "select 1 from {table} where length({column}) > 20" } },
        });
        var backfill = new Migration
        {
            Id = b.NewId(), Database = main.Id, Fact = MigrationFact.Backfill, Object = Column("01JATT00000000000000000003", "currency"),
            DataCheck = new MigrationDataCheck { Accept = "The column is new and every row gets its default." },
        };
        var drop = new Migration
        {
            Id = b.NewId(), Database = main.Id, Fact = MigrationFact.Drop, Object = Column("01JATT00000000000000000004", "legacy_code"),
            Acknowledge = "Nothing reads it.", Held = true, Released = true,
        };
        var raw = new Migration
        {
            Id = b.NewId(), Database = main.Id, Fact = MigrationFact.Raw, Phase = MigrationPhase.Before,
            Sql = new Dictionary<string, string> { ["postgresql"] = "create extension if not exists citext;" },
            SkipWhen = new Dictionary<string, string> { ["*"] = "select 1 from pg_extension where extname = 'citext'" },
        };
        b.Add(narrowing).Add(backfill).Add(drop).Add(raw);

        var report = await Validate(b.Build());

        Assert.Empty(report.Diagnostics);
        Assert.Equal(["complete", "complete", "released", "complete"], new[] { narrowing, backfill, drop, raw }.Select(f => MigrationRules.State(f, Dialect.PostgreSql)));
    }

    [Fact]
    public async Task Outside_PostgreSQL_a_narrowing_needs_no_data_check_and_reports_MQ4056()
    {
        var b = new ModelBuilder();
        var legacy = b.Database("legacy", Dialect.SqlServer);
        var fact = ChangeType(b, legacy.Id, "nvarchar(32)", "nvarchar(20)");
        b.Add(fact);

        var report = await Validate(b.Build());

        Assert.Equal([("MQ4056", "/fact"), ("MQ4101", "/conversion")], report.Diagnostics.Select(d => (d.Rule, d.JsonPointer!)).OrderBy(t => t.Rule, StringComparer.Ordinal));
        Assert.Equal(DiagnosticSeverity.Warning, report.Diagnostics.Single(d => d.Rule == "MQ4056").Severity);
    }

    [Fact]
    public async Task Outside_PostgreSQL_a_rekey_reports_MQ4056_for_its_ledger_row_only()
    {
        var b = new ModelBuilder();
        var legacy = b.Database("legacy", Dialect.SqlServer);
        var rekey = new Migration
        {
            Id = b.NewId(), Database = legacy.Id, Fact = MigrationFact.Rekey,
            Keys = [new MigrationRekey { Kind = "table", From = "01JENT00000000000000000001@" + legacy.Id, To = "01JTBL00000000000000000001", Name = "invoices" }],
        };
        b.Add(rekey);

        var warning = Assert.Single((await Validate(b.Build())).Diagnostics);

        // A rekey writes no DDL on any dialect, so the warning names the missing ledger row and no TODO comment.
        Assert.Equal(("MQ4056", "/fact", DiagnosticSeverity.Warning), (warning.Rule, warning.JsonPointer, warning.Severity));
        Assert.Contains("a rekey writes no DDL on any dialect", warning.Message, StringComparison.Ordinal);
        Assert.Contains("no ledger rows are written", warning.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("TODO", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_value_map_code_listed_twice_or_mapped_to_itself_is_MQ4106()
    {
        var b = new ModelBuilder();
        var main = b.Database("main", Dialect.PostgreSql);
        var map = new Migration
        {
            Id = b.NewId(), Database = main.Id, Fact = MigrationFact.ValueMap, Objects = [Column("01JATT00000000000000000006", "status")],
            ReferenceType = "01JFRT00000000000000000001",
            Values =
            [
                new MigrationValue { From = "draft", To = "open" },
                new MigrationValue { From = "draft", To = "pending" },
                new MigrationValue { From = "paid", To = "paid" },
            ],
        };
        var state = map with
        {
            Id = b.NewId(), Fact = MigrationFact.StateMap, ReferenceType = null, Process = "01JFPR00000000000000000001",
            Objects = [Column("01JATT00000000000000000007", "state")], Values = [new MigrationValue { From = "new", To = "open" }],
        };
        b.Add(map).Add(state);

        var report = await Validate(b.Build());

        Assert.Equal([("MQ4106", "/values/1"), ("MQ4106", "/values/2/to")],
            report.Diagnostics.Where(d => d.ElementId == map.Id).Select(d => (d.Rule, d.JsonPointer!)).Order());
        Assert.Contains("'draft' is listed twice", report.Diagnostics.Single(d => d.JsonPointer == "/values/1").Message, StringComparison.Ordinal);
        Assert.Contains("'paid' maps to itself", report.Diagnostics.Single(d => d.JsonPointer == "/values/2/to").Message, StringComparison.Ordinal);
        Assert.DoesNotContain(report.Diagnostics, d => d.ElementId == state.Id);
    }

    [Fact]
    public async Task A_change_type_from_a_native_type_to_a_keyword_is_classified_through_the_type_map()
    {
        var b = new ModelBuilder();
        var main = b.Database("main", Dialect.PostgreSql);
        var narrowing = ChangeType(b, main.Id, "text", "unused", f => f with { To = new MigrationState { Type = "string", Length = 20 } });
        var widening = ChangeType(b, main.Id, "unused", "text", f => f with
        {
            Object = Column("01JATT00000000000000000008", "code"), From = new MigrationState { Type = "string", Length = 20 },
        });
        b.Add(narrowing).Add(widening);

        var report = await Validate(b.Build());

        // text to varchar(20) narrows, so the fact needs a data check too; varchar(20) to text widens and needs only a conversion.
        Assert.Equal(["/conversion", "/dataCheck"], report.Diagnostics.Where(d => d.ElementId == narrowing.Id).Select(d => d.JsonPointer).Order(StringComparer.Ordinal));
        Assert.Equal(["conversion", "dataCheck"], MigrationRules.Missing(narrowing, Dialect.PostgreSql));
        Assert.Equal(["/conversion"], report.Diagnostics.Where(d => d.ElementId == widening.Id).Select(d => d.JsonPointer));
    }

    [Fact]
    public async Task Facts_ordered_by_after_may_change_one_object()
    {
        var b = new ModelBuilder();
        var main = b.Database("main", Dialect.PostgreSql);
        var first = new Migration
        {
            Id = b.NewId(), Database = main.Id, Fact = MigrationFact.Rename, Object = Column("01JATT00000000000000000005", "ref"),
            From = new MigrationState { Name = "ref" }, To = new MigrationState { Name = "reference" },
        };
        var second = first with { Id = b.NewId(), After = [first.Id], From = new MigrationState { Name = "reference" }, To = new MigrationState { Name = "ref_code" } };
        var third = first with { Id = b.NewId(), After = [second.Id], From = new MigrationState { Name = "ref_code" }, To = new MigrationState { Name = "code" } };
        b.Add(first).Add(second).Add(third);

        Assert.Empty((await Validate(b.Build())).Diagnostics);
    }

    [Fact]
    public async Task A_save_of_one_fact_revisits_the_other_facts_of_its_database()
    {
        var b = new ModelBuilder();
        var main = b.Database("main", Dialect.PostgreSql);
        var earlier = new Migration
        {
            Id = b.NewId(), Database = main.Id, Fact = MigrationFact.Rename, Object = Column("01JATT00000000000000000005", "ref"),
            From = new MigrationState { Name = "ref" }, To = new MigrationState { Name = "reference" },
        };
        var later = earlier with { Id = b.NewId(), To = new MigrationState { Name = "ref_code" } };
        b.Add(earlier).Add(later);

        // Validating only the earlier fact (what a save of it checks) still reports the conflict, which lands on the later one.
        var report = await Validate(b.Build(), new ValidationScope([earlier.Id]));

        var conflict = Assert.Single(report.Diagnostics);
        Assert.Equal(("MQ4107", later.Id, "/object"), (conflict.Rule, conflict.ElementId, conflict.JsonPointer));
        Assert.Contains(earlier.Id, conflict.Message, StringComparison.Ordinal);
    }
}
