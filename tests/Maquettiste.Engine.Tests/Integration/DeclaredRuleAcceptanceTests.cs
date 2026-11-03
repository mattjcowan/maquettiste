using System.Text.Json;
using System.Text.Json.Nodes;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Validation;
using static Maquettiste.Engine.Tests.Validation.SuppressionHarness;

namespace Maquettiste.Engine.Tests.Integration;

/// <summary>
/// Scope 8.3's done-when cases (next-phase-2-design.md section 5.6): a convention as a declaration instead of a script, the
/// contributed columns of 3.1 satisfying it, and a suppression with a reason accepting one exception, for a declared rule and for a
/// built-in rule.
/// </summary>
[Collection(IntegrationCollection.Name)]
public sealed class DeclaredRuleAcceptanceTests
{
    private const string Rule = "x/audited-tables-have-updated-at";
    private const string UpdatedAtColumnId = "01K6BNF0000000000000000001";

    private static Task<E2ERepo> AuditedNotesAsync(params (string Path, Action<JsonObject> Edit)[] more) =>
        CreateAsync([AuditRule], null, [(NotesPath, Carry("audited")), .. more]);

    [Fact]
    public async Task Every_audited_table_has_an_updated_at_column_is_a_declaration()
    {
        await using var repo = await AuditedNotesAsync();
        var report = await ValidateAsync(repo);
        var error = Assert.Single(report.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        Assert.Equal((Rule, NotesFile, "", "Table notes carries audited but has no updated_at column."),
            (error.Rule, error.FilePath, error.JsonPointer, error.Message));
        // The invoice register carries audited too and gets updated_at from Invoice's virtual attribute.
        Assert.Single(report.Diagnostics, d => d.Rule == Rule);

        var run = await repo.RunAsync();
        E2ERepo.AssertOutcome(RunOutcome.Invalid, run);
        Assert.Contains(run.Diagnostics, d => d.Rule == Rule);
    }

    [Fact]
    public async Task An_updated_at_inherited_from_audited_satisfies_the_rule()
    {
        await using var repo = await AuditedNotesAsync((AuditedPath, node => node["columns"] = new JsonArray(new JsonObject
        {
            ["id"] = UpdatedAtColumnId, ["name"] = "updated_at", ["type"] = "datetimeoffset", ["nullable"] = true,
        })));
        var report = await ValidateAsync(repo);
        Assert.DoesNotContain(report.Diagnostics, d => d.Rule == Rule);
        Assert.False(report.HasErrors, Show(report));

        // notes leaves the contributed column out: the finding is back.
        await repo.EditAsync(NotesId, node => node["excludes"] = new JsonArray(UpdatedAtColumnId));
        var excluded = await ValidateAsync(repo);
        Assert.Equal([NotesFile], excluded.Diagnostics.Where(d => d.Rule == Rule).Select(d => d.FilePath));
    }

    [Fact]
    public async Task A_suppression_with_a_reason_accepts_one_exception()
    {
        const string reason = "Notes are append-only; a row never changes after it is written.";
        await using var repo = await AuditedNotesAsync((NotesPath, node => node["suppressions"] = new JsonArray(new JsonObject
        {
            ["rule"] = Rule, ["reason"] = reason,
        })));
        var report = await ValidateAsync(repo);
        Assert.False(report.HasErrors, Show(report));
        var accepted = Assert.Single(report.Suppressed);
        Assert.Equal((Rule, NotesFile, reason, NotesId, "/suppressions/0"),
            (accepted.Finding.Rule, accepted.Finding.FilePath, accepted.Reason, accepted.ElementId, accepted.JsonPointer));

        var sarif = new MemoryStream();
        await SarifWriter.WriteAsync(sarif, report, "1.0.0", Ct);
        using (var log = JsonDocument.Parse(sarif.ToArray()))
        {
            var result = Assert.Single(log.RootElement.GetProperty("runs")[0].GetProperty("results").EnumerateArray(),
                r => r.GetProperty("ruleId").GetString() == Rule);
            Assert.Equal(reason, result.GetProperty("suppressions")[0].GetProperty("justification").GetString());
        }

        E2ERepo.AssertOutcome(RunOutcome.Succeeded, await repo.RunAsync());

        // A built-in rule: the retired status list's MQ2022 warning on Invoice's status attribute moves to suppressed.
        await repo.EditAsync("01J92P0V05WQDH2GZG539D9VG1", node => node["status"] = "retired");
        Assert.Contains((await ValidateAsync(repo)).Diagnostics, d => d.Rule == "MQ2022" && d.ElementId == InvoiceStatusAttributeId);
        await repo.EditAsync(E2ERepo.InvoiceId, node => node["suppressions"] = new JsonArray(new JsonObject
        {
            ["rule"] = "MQ2022", ["member"] = InvoiceStatusAttributeId, ["reason"] = "Kept until open invoices move to the new status list.",
        }));
        var retired = await ValidateAsync(repo);
        Assert.DoesNotContain(retired.Diagnostics, d => d.Rule == "MQ2022" && d.ElementId == InvoiceStatusAttributeId);
        Assert.Contains(retired.Suppressed, s => s.Finding.Rule == "MQ2022" && s.Finding.ElementId == InvoiceStatusAttributeId
            && s.ElementId == E2ERepo.InvoiceId && s.JsonPointer == "/suppressions/0");

        // notes drops audited: its entry matches nothing.
        await repo.EditAsync(NotesId, node => node.Remove("stereotypes"));
        var unused = Assert.Single((await ValidateAsync(repo)).Diagnostics, d => d.Rule == "MQ5020");
        Assert.Equal((NotesFile, "/suppressions/0", DiagnosticSeverity.Warning), (unused.FilePath, unused.JsonPointer, unused.Severity));
    }
}
