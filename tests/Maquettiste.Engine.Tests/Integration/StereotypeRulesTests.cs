using System.Text;
using System.Text.Json.Nodes;
using Maquettiste.Engine.Diagnostics;

namespace Maquettiste.Engine.Tests.Integration;

/// <summary>
/// The acceptance tests of next-phase-1-design.md section 4.2 (scope 3.2): stereotypes of one group exclude each other on an
/// element, and a stereotype's typed parameters check the values of the elements that carry it, with a pointer to the value.
/// </summary>
[Collection(IntegrationCollection.Name)]
public sealed class StereotypeRulesTests
{
    private const string InvoiceRegisterTableId = "01J92P0V1T0J6RH4MY9H81NYB4";
    private const string SoftDeleteId = "01J92P0V28YYY43REGJ9RXZ662";
    private const string AuditedId = "01J92P0V25BX5TMQ8N9F6AJE61";
    private const string HardDeleteId = "01K9XST0000000000000000001";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Soft_delete_and_hard_delete_in_one_group_on_a_table_is_an_error()
    {
        await using var repo = E2ERepo.Create(demo: false);
        await repo.EditAsync(SoftDeleteId, n =>
        {
            n["appliesTo"] = new JsonArray("entity", "table");
            n["group"] = "deletion";
        });
        await repo.CreateElementAsync($$"""
            { "kind": "stereotype", "id": "{{HardDeleteId}}", "key": "hard-delete", "name": "Hard delete", "appliesTo": ["entity", "table"],
              "group": "deletion" }
            """);
        var before = repo.Repo.ListFiles().ToDictionary(p => p, repo.Repo.ReadFile, StringComparer.Ordinal);

        var saved = await SaveAsync(repo, InvoiceRegisterTableId, n => n["stereotypes"] = new JsonArray("audited", "soft-delete", "hard-delete"));

        Assert.Equal(SaveOutcome.Invalid, saved.Outcome);
        var finding = Assert.Single(saved.Diagnostics, d => d.Rule == "MQ2013");
        Assert.Equal((DiagnosticSeverity.Error, InvoiceRegisterTableId, "/stereotypes/2"), (finding.Severity, finding.ElementId, finding.JsonPointer));
        Assert.Equal(".maquettiste/model/databases/main/tables/01j92p0v1t0j6rh4my9h81nyb4.json", finding.FilePath);
        Assert.Contains("'hard-delete' is in group 'deletion'", finding.Message, StringComparison.Ordinal);
        Assert.Equal(before, repo.Repo.ListFiles().ToDictionary(p => p, repo.Repo.ReadFile, StringComparer.Ordinal));

        // One of the group is fine.
        Assert.Equal(SaveOutcome.Saved, (await SaveAsync(repo, InvoiceRegisterTableId, n => n["stereotypes"] = new JsonArray("audited", "hard-delete"))).Outcome);
    }

    [Fact]
    public async Task A_wrong_typed_retention_days_on_an_audited_table_is_an_error_with_a_pointer()
    {
        await using var repo = E2ERepo.Create(demo: false);
        // The default 2555 passes its own property schema, so the stereotype saves clean.
        var stereotype = await repo.EditAsync(AuditedId, n => n["parameters"] = JsonNode.Parse(
            """{ "properties": { "retentionDays": { "type": "integer", "minimum": 1 } } }"""));
        Assert.DoesNotContain(stereotype.Diagnostics, d => d.Rule == "MQ2018");

        var saved = await SaveAsync(repo, InvoiceRegisterTableId, n => n["properties"] = new JsonObject { ["retentionDays"] = "seven years" });

        Assert.Equal(SaveOutcome.Invalid, saved.Outcome);
        var finding = Assert.Single(saved.Diagnostics, d => d.Rule == "MQ2018");
        Assert.Equal((DiagnosticSeverity.Error, InvoiceRegisterTableId, "/properties/retentionDays"), (finding.Severity, finding.ElementId, finding.JsonPointer));
        Assert.Contains("Stereotype 'audited' parameters: properties/retentionDays", finding.Message, StringComparison.Ordinal);

        // A whole number passes, and the model stays clean.
        Assert.Equal(SaveOutcome.Saved, (await SaveAsync(repo, InvoiceRegisterTableId, n => n["properties"] = new JsonObject { ["retentionDays"] = 3650 })).Outcome);
        var report = await repo.Store.ValidateAsync(ValidationScope.All, Ct);
        Assert.DoesNotContain(report.Diagnostics, d => d.Rule is "MQ2018" or "MQ2019");
    }

    private static async Task<SaveResult> SaveAsync(E2ERepo repo, string id, Action<JsonObject> edit)
    {
        var document = (await repo.Store.GetElementAsync(id, Ct))!;
        var node = JsonNode.Parse(document.Json.GetRawText())!.AsObject();
        edit(node);
        return await repo.Store.SaveAsync(id, Encoding.UTF8.GetBytes(node.ToJsonString()), document.Hash, ChangeSource.Editor, Ct);
    }
}
