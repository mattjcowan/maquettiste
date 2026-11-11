using System.Text;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Model;

namespace Maquettiste.Engine.Tests.Editor;

/// <summary>
/// The model half of queries written as SQL text and named queries (sql-queries-design.md decisions 1, 7, 8, 10, 12 and 17): the two forms
/// the schema keeps apart, the canonical file, <c>reads</c> and <c>writes</c> as references, and deletes that keep a SQL text.
/// </summary>
public sealed class SqlTextQueryModelTests
{
    private const string SqlQueryId = "01KSQ000000000000000000001";
    private const string NamedQueryId = "01KSQ000000000000000000002";
    private const string InvoicesByCustomerId = "01K6QRY0000000000000000001";
    private const string RevenueByMonthId = "01K6QRY0000000000000000002";
    private const string FindCustomersWithIssuedInvoicesId = "01K6QRY0000000000000000003";

    private static CancellationToken Ct => EditorRepo.Ct;

    private static async Task<SaveResult> CreateAsync(EditorRepo r, string element)
    {
        var parsed = r.Store.ParseBatch(Encoding.UTF8.GetBytes($$"""{ "operations": [ { "op": "create", "element": {{element}} } ] }"""));
        Assert.True(parsed.Batch is not null, string.Join("; ", parsed.Diagnostics.Select(d => d.Message)));
        var applied = await r.Store.ApplyBatchAsync(parsed.Batch, ChangeSource.Editor, Ct);
        return new SaveResult(applied.Outcome, [.. applied.Items.SelectMany(i => i.Diagnostics)]);
    }

    private static string SqlQuery(string extra = "") => $$"""
        { "kind": "query", "id": "{{SqlQueryId}}", "name": "MarkOverdueInvoices", "database": "{{EditorRepo.MainDatabaseId}}",
          "parameters": [ { "name": "asOf", "type": "date" } ],
          "sql": { "postgresql": "UPDATE billing.invoices SET status = 'V'\nWHERE due_on < :asOf\nRETURNING id" },
          "columns": [ { "name": "id", "type": "uuid", "nullable": false }, { "name": "note", "type": "string", "nullable": true, "description": "Free text." } ],
          "reads": [ "{{EditorRepo.OverlayTableId}}" ], "writes": [ "{{EditorRepo.OverlayTableId}}" ]{{extra}} }
        """;

    private sealed record SaveResult(SaveOutcome Outcome, IReadOnlyList<Diagnostic> Diagnostics)
    {
        public string Messages => string.Join("; ", Diagnostics.Select(d => $"{d.Rule} {d.JsonPointer} {d.Message}"));
    }

    [Fact]
    public async Task A_sql_text_query_saves_in_canonical_order_and_its_tables_are_references()
    {
        await using var r = EditorRepo.Create(packs: false);
        var saved = await CreateAsync(r, SqlQuery());
        Assert.True(saved.Outcome == SaveOutcome.Saved, saved.Messages);

        var query = r.Store.Current!.Get<Query>(SqlQueryId)!;
        Assert.True(query.IsSqlText);
        Assert.Null(query.From);
        Assert.Empty(query.Select);
        Assert.Equal([false, true], query.Columns.Select(c => c.Nullable));
        Assert.Equal([EditorRepo.OverlayTableId], query.Reads);
        Assert.Equal([EditorRepo.OverlayTableId], query.Writes);
        Assert.Equal(["/reads/0", "/writes/0"], r.Store.Current.ReferencesTo(EditorRepo.OverlayTableId)
            .Where(x => x.FromElementId == SqlQueryId).Select(x => x.JsonPointer).Order(StringComparer.Ordinal));

        // Keys in the schema's order; nullable: true is the default and goes.
        var file = File.ReadAllText(r.Repo.PathOf(r.Store.Current.GetDocument(SqlQueryId)!.Path));
        Assert.Equal(
            ["\"parameters\"", "\"sql\"", "\"columns\"", "\"reads\"", "\"writes\""],
            new[] { "\"parameters\"", "\"sql\"", "\"columns\"", "\"reads\"", "\"writes\"" }.OrderBy(k => file.IndexOf(k, StringComparison.Ordinal)));
        Assert.Contains("\"nullable\": false", file, StringComparison.Ordinal);
        Assert.DoesNotContain("\"nullable\": true", file, StringComparison.Ordinal);
        Assert.Contains("\"description\": \"Free text.\"", file, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(""", "from": { "source": "01J92P0V1T0J6RH4MY9H81NYB4" }""")]
    [InlineData(""", "entity": "01J92P0V0FJ23CGSNKM7P1W5V7" """)]
    [InlineData(""", "orderBy": [ { "expression": { "column": "id" } } ]""")]
    public async Task A_file_that_mixes_the_two_forms_is_refused(string extra)
    {
        await using var r = EditorRepo.Create(packs: false);
        var saved = await CreateAsync(r, SqlQuery(extra));
        Assert.NotEqual(SaveOutcome.Saved, saved.Outcome);
        Assert.Contains(saved.Diagnostics, d => d.Rule == "MQ1002");
    }

    [Fact]
    public async Task A_query_with_neither_form_or_a_built_query_with_reads_is_refused()
    {
        await using var r = EditorRepo.Create(packs: false);
        var neither = await CreateAsync(r, $$"""{ "kind": "query", "id": "{{SqlQueryId}}", "name": "Nothing", "database": "{{EditorRepo.MainDatabaseId}}" }""");
        var built = await CreateAsync(r, $$"""
            { "kind": "query", "id": "{{SqlQueryId}}", "name": "Built", "database": "{{EditorRepo.MainDatabaseId}}",
              "from": { "source": "{{EditorRepo.OverlayTableId}}", "alias": "i" }, "select": [ { "name": "id", "expression": { "column": "i.id" } } ],
              "reads": [ "{{EditorRepo.OverlayTableId}}" ] }
            """);
        Assert.Contains(neither.Diagnostics, d => d.Rule == "MQ1002");
        Assert.Contains(built.Diagnostics, d => d.Rule == "MQ1002");
    }

    [Fact]
    public async Task A_built_query_with_a_recursive_named_query_loads_and_drops_its_defaults()
    {
        await using var r = EditorRepo.Create(packs: false);
        var saved = await CreateAsync(r, $$"""
            { "kind": "query", "id": "{{NamedQueryId}}", "name": "Chain", "database": "{{EditorRepo.MainDatabaseId}}",
              "with": [ { "name": "chain",
                "from": { "source": "{{EditorRepo.OverlayTableId}}", "alias": "c" },
                "select": [ { "name": "id", "expression": { "column": "c.id" } } ],
                "recursive": { "union": "all", "from": { "source": "chain", "alias": "ch" },
                  "joins": [ { "source": "{{EditorRepo.OverlayTableId}}", "alias": "p", "on": { "op": "eq", "left": { "column": "p.id" }, "right": { "column": "ch.id" } } } ],
                  "select": [ { "name": "id", "expression": { "column": "p.id" } } ] } } ],
              "from": { "source": "chain", "alias": "a" },
              "select": [ { "name": "id", "expression": { "column": "a.id" } } ] }
            """);
        Assert.Equal(SaveOutcome.Saved, saved.Outcome);
        var query = r.Store.Current!.Get<Query>(NamedQueryId)!;
        var named = Assert.Single(query.With);
        Assert.Equal(("chain", QueryUnion.All, "chain"), (named.Name, named.Recursive!.Union, named.Recursive.From.Source));
        Assert.Equal("chain", query.From!.Source);
        Assert.False(query.IsSqlText);

        var file = File.ReadAllText(r.Repo.PathOf(r.Store.Current.GetDocument(NamedQueryId)!.Path));
        Assert.DoesNotContain("\"union\"", file, StringComparison.Ordinal);
        Assert.True(file.IndexOf("\"with\"", StringComparison.Ordinal) < file.IndexOf("\"from\": {\n    \"source\": \"chain\"", StringComparison.Ordinal));

        // A name is no id: only the table the named query reads is a reference.
        Assert.All(r.Store.Current.ReferencesFrom(NamedQueryId).Where(x => x.Field == "source"), x => Assert.Equal(EditorRepo.OverlayTableId, x.ToId));
    }

    [Theory]
    [InlineData(DeleteResolution.RemoveReferences)]
    [InlineData(DeleteResolution.DeleteDependents)]
    public async Task Deleting_a_table_a_sql_text_names_removes_the_entries_keeps_the_query_and_warns(DeleteResolution resolution)
    {
        await using var r = EditorRepo.Create(packs: false);
        Assert.Equal(SaveOutcome.Saved, (await CreateAsync(r, SqlQuery())).Outcome);
        var table = (await r.Store.GetElementAsync(EditorRepo.OverlayTableId, Ct))!;

        var refused = await r.Store.DeleteAsync(table.Element.Id, table.Hash, DeleteResolution.Refuse, ChangeSource.Editor, Ct);
        Assert.Contains(refused.Referrers, x => x.FromElementId == SqlQueryId);

        var plan = await r.Store.GetDeletePlanAsync([EditorRepo.OverlayTableId], resolution, Ct);
        Assert.DoesNotContain(plan.Deletes, d => d.Id == SqlQueryId);
        Assert.DoesNotContain(plan.Refused, d => d.Id == SqlQueryId);
        Assert.Equal(["/reads/0", "/writes/0"], plan.Removes.Where(x => x.Id == SqlQueryId).Select(x => x.Pointer));
        Assert.Contains(plan.Warnings, w => w.Message == "Query MarkOverdueInvoices's SQL text may still name table Invoice register; check the text.");

        // The built queries keep today's behavior: refused with remove-references, deleted whole with delete-dependents.
        string[] built = [InvoicesByCustomerId, RevenueByMonthId, FindCustomersWithIssuedInvoicesId];
        if (resolution == DeleteResolution.RemoveReferences)
        {
            Assert.Equal(built, plan.Refused.Select(x => x.Id!).Distinct().Order(StringComparer.Ordinal));
            return;
        }

        Assert.Equal(built, plan.Deletes.Select(d => d.Id).Order(StringComparer.Ordinal));
        var result = await r.Store.DeleteAsync(table.Element.Id, table.Hash, resolution, ChangeSource.Editor, Ct);
        Assert.Equal(SaveOutcome.Saved, result.Outcome);
        var kept = r.Store.Current!.Get<Query>(SqlQueryId)!;
        Assert.Empty(kept.Reads);
        Assert.Empty(kept.Writes);
        Assert.Contains("UPDATE billing.invoices", kept.Sql!["postgresql"], StringComparison.Ordinal);
        var validation = await r.Store.ValidateAsync(ValidationScope.All, Ct);
        Assert.DoesNotContain(validation.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
    }
}
