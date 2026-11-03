using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Tests.Resolution;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Ledger;

/// <summary>MQ4067 (next-phase-1-design.md section 3.3): the ledger's schema is also a schema of a database's modeled objects.</summary>
public sealed class LedgerRuleTests
{
    public static TheoryData<string, string?> Cases() => new()
    {
        { "declared", "/schemas/1/name" },
        { "table", null },
        { "configured", "/schemas/0/name" },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void A_shared_ledger_schema_is_MQ4067(string how, string? pointer)
    {
        var model = new ModelBuilder();
        var main = model.Database("main", Dialect.PostgreSql);
        var notes = model.Table("notes", main).Column("id", "int64").PrimaryKey("id");
        switch (how)
        {
            case "declared":
                // A declared schema, even one no object uses yet.
                main.Schema("billing");
                main.Schema("migration_ledger");
                break;
            case "table":
                // A schema only a table names: the database's default schema, undeclared.
                main.DefaultSchema("migration_ledger");
                break;
            case "configured":
                var billing = main.Schema("billing");
                notes.Edit(t => t with { Schema = billing });
                model.Settings(s => s with { Migrations = s.Migrations with { LedgerSchema = "billing" } });
                break;
        }

        // A database without a ledger never reports it.
        var local = model.Database("local", Dialect.Sqlite);
        local.Schema("migration_ledger");

        var resolved = ResolutionKit.Resolve(model);
        var diagnostic = Assert.Single(resolved.Diagnostics, d => d.Rule == "MQ4067");
        Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
        Assert.Equal(main.Id, diagnostic.ElementId);
        Assert.NotNull(diagnostic.FilePath);
        Assert.Equal(pointer, diagnostic.JsonPointer);
        Assert.Contains("database 'main'", diagnostic.Message, StringComparison.Ordinal);
        if (how == "table")
            Assert.Contains("table 'migration_ledger.notes'", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_ledger_schema_of_its_own_and_an_off_rule_report_nothing()
    {
        var model = new ModelBuilder();
        var main = model.Database("main", Dialect.PostgreSql);
        var billing = main.Schema("billing");
        model.Table("notes", main).Column("id", "int64").PrimaryKey("id").Edit(t => t with { Schema = billing });
        Assert.DoesNotContain(ResolutionKit.Resolve(model).Diagnostics, d => d.Rule == "MQ4067");

        main.Schema("migration_ledger");
        Assert.Contains(ResolutionKit.Resolve(model).Diagnostics, d => d.Rule == "MQ4067");
        model.Settings(s => s with { Validation = s.Validation with { Rules = new Dictionary<string, string> { ["MQ4067"] = "off" } } });
        Assert.DoesNotContain(ResolutionKit.Resolve(model).Diagnostics, d => d.Rule == "MQ4067");
    }
}
