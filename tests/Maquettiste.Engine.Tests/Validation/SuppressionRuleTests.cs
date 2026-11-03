using System.Text;
using System.Text.Json.Nodes;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Tests.Integration;
using static Maquettiste.Engine.Tests.Validation.SuppressionHarness;

namespace Maquettiste.Engine.Tests.Validation;

/// <summary>The rules on an element's suppressions (next-phase-2-design.md section 5.6): MQ5018 and MQ5019, judged per element.</summary>
public sealed class SuppressionRuleTests
{
    private const string TenantedPath = "model/vocabularies/stereotypes/tenanted.json";
    private const string TenantColumnId = "01K6BNE0000000000000000002";

    private static List<string> At(ValidationReport report, string rule) =>
        [.. report.Diagnostics.Where(d => d.Rule == rule).Select(d => d.FilePath + " " + d.JsonPointer).Order(StringComparer.Ordinal)];

    [Fact]
    public async Task An_MQ1xxx_or_MQ7012_rule_is_MQ5018()
    {
        await using var repo = await CreateAsync([], null, (NotesPath, Suppress(("MQ1001", null), ("MQ7012", null), ("MQ5020", null))));
        var report = await ValidateAsync(repo);
        Assert.Equal([NotesFile + " /suppressions/0/rule", NotesFile + " /suppressions/1/rule", NotesFile + " /suppressions/2/rule"], At(report, "MQ5018"));
        Assert.All(report.Diagnostics.Where(d => d.Rule == "MQ5018"), d => Assert.Equal(DiagnosticSeverity.Error, d.Severity));
        Assert.Empty(At(report, "MQ5020"));
    }

    [Fact]
    public async Task An_unknown_rule_or_missing_member_is_MQ5019()
    {
        await using var repo = await CreateAsync([], null,
            (NotesPath, Suppress(("MQ9999", null), ("x/nothing", null), ("MQ2022", "01K6BND0000000000000000099"), ("MQ2022", BodyColumnId))));
        var report = await ValidateAsync(repo);
        Assert.Equal([NotesFile + " /suppressions/0/rule", NotesFile + " /suppressions/1/rule", NotesFile + " /suppressions/2/member"], At(report, "MQ5019"));
        Assert.Contains(report.Diagnostics, d => d.Rule == "MQ5019" && d.Message.Contains("'x/nothing'", StringComparison.Ordinal));
        Assert.Equal(DiagnosticSeverity.Warning, report.Diagnostics.First(d => d.Rule == "MQ5019").Severity);
    }

    [Fact]
    public async Task A_repeated_entry_is_MQ5019()
    {
        await using var repo = await CreateAsync([], null,
            (NotesPath, Suppress(("MQ2022", null), ("MQ2022", BodyColumnId), ("MQ2022", null), ("MQ2022", BodyColumnId))));
        var report = await ValidateAsync(repo);
        Assert.Equal([NotesFile + " /suppressions/2", NotesFile + " /suppressions/3"], At(report, "MQ5019"));
    }

    [Fact]
    public async Task A_member_may_be_an_inherited_or_virtual_member()
    {
        await using var repo = await CreateAsync([], null,
            // Invoice holds updatedAt through audited (a virtual attribute) and its own status.
            (InvoicePath, Suppress(("MQ2022", AuditedUpdatedAtId), ("MQ2022", InvoiceStatusAttributeId), ("MQ2022", "01K6BND0000000000000000099"))));
        // A derived entity holds its base's attributes and the base's stereotype attributes.
        Write(repo, "model/entities/credit-invoice.json", $$"""
            {
              "kind": "entity",
              "id": "01K6BNE0000000000000000010",
              "name": "CreditInvoice",
              "base": "{{E2ERepo.InvoiceId}}",
              "suppressions": [
                { "rule": "MQ2022", "member": "{{InvoiceStatusAttributeId}}", "reason": "Inherited from Invoice." },
                { "rule": "MQ2022", "member": "{{AuditedUpdatedAtId}}", "reason": "Virtual through Invoice." }
              ]
            }
            """);
        await repo.RestartAsync();
        var report = await ValidateAsync(repo);
        Assert.Equal([".maquettiste/" + InvoicePath + " /suppressions/2/member"], At(report, "MQ5019"));
    }

    [Fact]
    public async Task A_member_may_be_the_keyed_id_of_a_synthesized_tables_member()
    {
        await using var repo = await CreateAsync([], null,
            (RegisterPath, Carry("tenanted").Then(Suppress(("MQ2022", E2ERepo.InvoiceId + "@" + E2ERepo.MainDatabaseId + "." + TenantColumnId),
                ("MQ2022", NotesId + "." + TenantColumnId)))),
            (NotesPath, Carry("tenanted").Then(Suppress(("MQ2022", NotesId + "." + TenantColumnId), ("MQ2022", NotesId + "." + BodyColumnId)))));
        Write(repo, TenantedPath, $$"""
            {
              "$schema": "../../../.schema/v1/stereotype.json",
              "kind": "stereotype",
              "id": "01K6BNE0000000000000000001",
              "key": "tenanted",
              "name": "Tenanted",
              "appliesTo": ["table"],
              "columns": [{ "id": "{{TenantColumnId}}", "name": "tenant_id", "type": "uuid", "nullable": true }]
            }
            """);
        await repo.RestartAsync();
        var report = await ValidateAsync(repo);
        // The register's keyed id is its table key (entity@database) and the member; another table's keyed id is not the register's.
        Assert.Equal([".maquettiste/" + RegisterPath + " /suppressions/1/member", NotesFile + " /suppressions/1/member"], At(report, "MQ5019"));
    }

    [Fact]
    public async Task A_save_judges_x_ids_in_suppressions_against_the_loaded_rules()
    {
        await using var repo = await CreateAsync([EveryTableRule], null);
        Write(repo, "extensions/rules/script.js", """
            maquettiste.rule({
              id: "script-rule",
              kinds: ["entity"],
              check(element, model, report) {},
            });
            """);
        await repo.RestartAsync();

        var document = await repo.Store.GetElementAsync(NotesId, Ct);
        var node = JsonNode.Parse(document!.Json.GetRawText())!.AsObject();
        Suppress(("x/script-rule", null), ("x/every-table", null), ("x/nothing", null))(node);
        var saved = await repo.Store.SaveAsync(NotesId, Encoding.UTF8.GetBytes(node.ToJsonString()), document.Hash, ChangeSource.Editor, Ct);
        Assert.Equal(SaveOutcome.Saved, saved.Outcome);
        Assert.Equal(["/suppressions/2/rule"], saved.Diagnostics.Where(d => d.Rule == "MQ5019").Select(d => d.JsonPointer));
    }
}
