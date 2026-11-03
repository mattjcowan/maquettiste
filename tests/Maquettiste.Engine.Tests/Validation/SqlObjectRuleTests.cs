using System.Collections.Immutable;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Model;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Validation;

/// <summary>
/// SQL object environments and run-once (next-phase-1-design.md section 3.4): MQ4069 (a view, routine or SQL object depends on a
/// SQL object that does not run in every environment it runs in; empty means every environment), MQ4070 (environments with phase
/// before) and MQ4056 (a run-once object outside PostgreSQL).
/// </summary>
public sealed class SqlObjectRuleTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly string[] Rules = ["MQ4056", "MQ4069", "MQ4070"];

    private static ImmutableDictionary<string, string> Sql(string text) => ImmutableDictionary<string, string>.Empty.Add("*", text);

    /// <summary>
    /// A database <c>billing</c> holding the SQL object <c>invoice_audit</c> and one dependent (<c>invoice_totals</c>) that the
    /// mistake shapes; returns the model and the id of the element the rule should name.
    /// </summary>
    private static (ModelBuilder Builder, string Subject) Model(string mistake)
    {
        var b = new ModelBuilder(seed: 270);
        var dialect = mistake.Contains("SQL Server", StringComparison.Ordinal) ? Dialect.SqlServer : Dialect.PostgreSql;
        var db = b.Database("billing", dialect);
        var dependency = b.NewId();
        var subject = b.NewId();
        SqlObject Object(string id, string name, IReadOnlyList<string> environments) => new()
        {
            Id = id, Name = name, Database = db.Id, ObjectKind = "trigger", Environments = environments, Body = Sql("select 1;"),
        };

        switch (mistake)
        {
            case "a view on a [dev] object":
                b.Add(Object(dependency, "invoice_audit", ["dev"]));
                b.Add(new View { Id = subject, Name = "invoice_totals", Database = db.Id, Body = Sql("select 1 as n"), DependsOn = [dependency] });
                break;
            case "a routine on a [dev] object":
                b.Add(Object(dependency, "invoice_audit", ["dev"]));
                b.Add(new Routine { Id = subject, Name = "invoice_totals", Database = db.Id, Body = Sql("select 1"), DependsOn = [dependency] });
                break;
            case "a [dev, test] object on a [dev] one":
                b.Add(Object(dependency, "invoice_audit", ["dev"]));
                b.Add(Object(subject, "invoice_totals", ["dev", "test"]) with { DependsOn = [dependency] });
                break;
            case "a [dev] object on a [dev, test] one":
                b.Add(Object(dependency, "invoice_audit", ["dev", "test"]));
                b.Add(Object(subject, "invoice_totals", ["dev"]) with { DependsOn = [dependency] });
                break;
            case "an object on one that runs everywhere":
                b.Add(Object(dependency, "invoice_audit", []));
                b.Add(Object(subject, "invoice_totals", ["dev"]) with { DependsOn = [dependency] });
                break;
            case "a [dev] object with phase before":
                b.Add(Object(subject, "invoice_totals", ["dev"]) with { Phase = SqlObjectPhase.Before });
                break;
            case "a run-once object on SQL Server":
                b.Add(Object(subject, "invoice_totals", []) with { Apply = SqlObjectApply.Once });
                break;
            case "a run-once object on PostgreSQL":
                b.Add(Object(subject, "invoice_totals", []) with { Apply = SqlObjectApply.Once });
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mistake), mistake, null);
        }

        return (b, subject);
    }

    [Theory]
    [InlineData("a view on a [dev] object", "MQ4069", "/dependsOn/0")]
    [InlineData("a routine on a [dev] object", "MQ4069", "/dependsOn/0")]
    [InlineData("a [dev, test] object on a [dev] one", "MQ4069", "/dependsOn/0")]
    [InlineData("a [dev] object on a [dev, test] one", "", "")]
    [InlineData("an object on one that runs everywhere", "", "")]
    [InlineData("a [dev] object with phase before", "MQ4070", "/phase")]
    [InlineData("a run-once object on SQL Server", "MQ4056", "/apply")]
    [InlineData("a run-once object on PostgreSQL", "", "")]
    public async Task MQ4069_MQ4070_and_run_once_MQ4056(string mistake, string rule, string pointer)
    {
        var (builder, subject) = Model(mistake);

        var findings = (await ValidationFixture.Validator().ValidateAsync(builder.Build(), ValidationScope.All, null, Ct)).Diagnostics
            .Where(d => Rules.Contains(d.Rule, StringComparer.Ordinal)).ToList();

        if (rule.Length == 0)
        {
            Assert.Empty(findings);
            return;
        }

        var finding = Assert.Single(findings);
        Assert.Equal((rule, pointer, subject), (finding.Rule, finding.JsonPointer, finding.ElementId));
        Assert.Contains("invoice_", finding.Message, StringComparison.Ordinal);
        if (mistake == "a [dev, test] object on a [dev] one")
            Assert.Contains("runs only in 'dev', while this object also runs in 'test'", finding.Message, StringComparison.Ordinal);
        if (mistake == "a view on a [dev] object")
            Assert.Contains("while this object runs in every environment", finding.Message, StringComparison.Ordinal);
    }
}
