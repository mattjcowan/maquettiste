using System.Text.Json.Nodes;
using Maquettiste.Engine;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Model;

namespace Maquettiste.Packs.Tests;

/// <summary>
/// The acceptance checks of sql-queries-design.md section 21 that read the model, over the sql-queries fixture (the generated code
/// is in <see cref="GoldenTests"/>, <see cref="CompileTests"/> and <see cref="SqlQueryServerTests"/>): the light checks of MQ4098,
/// a delete that keeps both SQL texts, and <c>union: "distinct"</c> refused on SQL Server (MQ4099).
/// </summary>
public sealed class SqlQueryAcceptanceTests
{
    private const string Queries = ".maquettiste/model/databases/main/queries/";
    private const string Tables = ".maquettiste/model/databases/main/tables/";
    private const string CategoryAncestors = "01K9Q550000000000000000040";
    private const string TopCustomersByRegion = "01K9Q550000000000000000041";
    private const string MarkOverdueInvoices = "01K9Q550000000000000000042";
    private const string Invoices = "01K9Q550000000000000000030";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// The findings of a full validation, without MQ1008 (the temporary repo has no schema copies) and MQ1003 (a test's edit leaves the
    /// file out of canonical form).
    /// </summary>
    private static async Task<List<Diagnostic>> FindingsAsync(PackRepo repo)
    {
        await using var store = new ModelStore(repo.Repo.Options);
        var report = await store.ValidateAsync(ValidationScope.All, Ct);
        return [.. report.Diagnostics.Where(d => d.Rule is not ("MQ1008" or "MQ1003"))];
    }

    private static string Describe(IEnumerable<Diagnostic> findings) =>
        string.Join('\n', findings.Select(d => $"{d.Rule} {d.ElementId} {d.JsonPointer}: {d.Message}"));

    private static List<(string Rule, string? Pointer)> Of(IEnumerable<Diagnostic> findings, string id, string rule) =>
        [.. findings.Where(d => d.ElementId == id && d.Rule == rule).Select(d => (d.Rule, d.JsonPointer))];

    [Fact]
    public async Task The_fixture_validates_without_a_finding()
    {
        using var repo = PackRepo.SqlQueries();
        var findings = await FindingsAsync(repo);
        Assert.True(findings.Count == 0, Describe(findings));
    }

    [Fact]
    public async Task Renaming_customers_warns_that_the_sql_text_still_names_it()
    {
        using var repo = PackRepo.SqlQueries();
        repo.EditJson(Tables + "customers.json", table => table["name"] = "clients");
        var findings = await FindingsAsync(repo);
        Assert.True(Of(findings, TopCustomersByRegion, "MQ4098") is [(_, "/reads/1")], Describe(findings));
        Assert.DoesNotContain(findings, d => d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public async Task A_command_behind_a_leading_with_without_writes_and_a_select_without_columns_are_MQ4098()
    {
        using var repo = PackRepo.SqlQueries();
        repo.EditJson(Queries + "mark-overdue-invoices.json", query =>
        {
            query["sql"] = new JsonObject
            {
                ["*"] = "WITH due AS (SELECT id FROM invoices WHERE status = 'issued' AND due_on < :asOf)\nUPDATE invoices SET status = 'overdue' WHERE id IN (SELECT id FROM due)",
            };
            query.AsObject().Remove("writes");
            query["reads"] = new JsonArray(Invoices);
        });
        var findings = await FindingsAsync(repo);
        Assert.True(Of(findings, MarkOverdueInvoices, "MQ4098") is [(_, "/sql/*")], Describe(findings));

        repo.EditJson(Queries + "mark-overdue-invoices.json", query => query["sql"] = new JsonObject { ["*"] = "SELECT id FROM invoices WHERE due_on < :asOf" });
        findings = await FindingsAsync(repo);
        var select = Assert.Single(findings, d => d.ElementId == MarkOverdueInvoices && d.Rule == "MQ4098");
        Assert.Equal("/sql/*", select.JsonPointer);
        Assert.Contains("columns", select.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_declared_table_matches_its_name_in_the_text_ignoring_case()
    {
        // The table is now named Invoices; both texts write invoices.
        using var repo = PackRepo.SqlQueries();
        repo.EditJson(Tables + "invoices.json", table => table["name"] = "Invoices");
        var findings = await FindingsAsync(repo);
        Assert.DoesNotContain(findings, d => d.Rule == "MQ4098");
    }

    [Fact]
    public async Task Deleting_invoices_with_remove_references_keeps_both_sql_texts_and_warns()
    {
        using var repo = PackRepo.SqlQueries();
        await using (var store = new ModelStore(repo.Repo.Options))
        {
            var plan = await store.GetDeletePlanAsync([Invoices], DeleteResolution.RemoveReferences, Ct);
            Assert.Empty(plan.Deletes);
            Assert.Empty(plan.Refused);
            Assert.Equal([(TopCustomersByRegion, "/reads/0"), (MarkOverdueInvoices, "/writes/0")],
                plan.Removes.Select(x => (x.Id, x.Pointer)).Order());
            Assert.Equal(2, plan.Warnings.Count);
            Assert.Contains(plan.Warnings, w => w.Message.Contains("MarkOverdueInvoices", StringComparison.Ordinal) && w.Message.Contains("invoices", StringComparison.Ordinal));
            Assert.Contains(plan.Warnings, w => w.Message.Contains("TopCustomersByRegion", StringComparison.Ordinal) && w.Message.Contains("invoices", StringComparison.Ordinal));

            var table = (await store.GetElementAsync(Invoices, Ct))!;
            var deleted = await store.DeleteAsync(Invoices, table.Hash, DeleteResolution.RemoveReferences, ChangeSource.Editor, Ct);
            Assert.Equal(SaveOutcome.Saved, deleted.Outcome);
        }

        Assert.True(File.Exists(repo.PathOf(Queries + "mark-overdue-invoices.json")));
        Assert.True(File.Exists(repo.PathOf(Queries + "top-customers-by-region.json")));
        Assert.Contains("UPDATE invoices", repo.Read(Queries + "mark-overdue-invoices.json"), StringComparison.Ordinal);
        // The only error is the drop's migration fact asking to be acknowledged (MQ4101), as for any table dropped.
        var findings = await FindingsAsync(repo);
        Assert.True(findings.Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => d.Rule).SequenceEqual(["MQ4101"]), Describe(findings));

        // The command now declares no written table, which its UPDATE needs.
        Assert.True(Of(findings, MarkOverdueInvoices, "MQ4098") is [(_, "/sql/*")], Describe(findings));
    }

    [Fact]
    public async Task Union_distinct_in_the_repeating_part_is_MQ4099_on_sql_server_only()
    {
        static void Distinct(JsonNode query) => query["with"]![0]!["recursive"]!["union"] = "distinct";

        using var postgres = PackRepo.SqlQueries();
        postgres.EditJson(Queries + "category-ancestors.json", Distinct);
        var findings = await FindingsAsync(postgres);
        Assert.True(findings.Count == 0, Describe(findings));

        using var sqlServer = PackRepo.SqlQueries("sqlserver");
        sqlServer.EditJson(Queries + "category-ancestors.json", Distinct);
        findings = await FindingsAsync(sqlServer);
        var refused = Assert.Single(findings, d => d.ElementId == CategoryAncestors && d.Rule == "MQ4099");
        Assert.Equal(DiagnosticSeverity.Error, refused.Severity);
    }
}
