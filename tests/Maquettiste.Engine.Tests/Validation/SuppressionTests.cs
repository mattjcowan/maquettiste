using System.Text;
using System.Text.Json.Nodes;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Snapshots;
using Maquettiste.Engine.Tests.Integration;
using Maquettiste.Engine.Validation;
using static Maquettiste.Engine.Tests.Validation.SuppressionHarness;

namespace Maquettiste.Engine.Tests.Validation;

/// <summary>Suppressions apply (next-phase-2-design.md section 5.6): to the validator's, the resolver's and the checks' findings, after
/// severities, with MQ5020 for an entry nothing matched.</summary>
public sealed class SuppressionTests
{
    private const string InvoiceOverlayColumn = "/columns/0";
    private const string TenantedPath = "model/vocabularies/stereotypes/tenanted.json";
    private const string TenantColumnId = "01K6BNE0000000000000000002";

    /// <summary>The notes table's tenant column, contributed by the tenanted stereotype: the table's key and the member's id.</summary>
    private const string NotesTenantKeyed = NotesId + "." + TenantColumnId;

    /// <summary>Notes carries a tenanted stereotype that contributes one column, with the suppressions given.</summary>
    private static async Task<E2ERepo> TenantedNotesAsync(IReadOnlyList<string> rules, string columnName, params (string Rule, string? Member)[] entries)
    {
        var repo = await CreateAsync(rules, null, (NotesPath, Carry("tenanted").Then(Suppress(entries))));
        Write(repo, TenantedPath, $$"""
            {
              "$schema": "../../../.schema/v1/stereotype.json",
              "kind": "stereotype",
              "id": "01K6BNE0000000000000000001",
              "key": "tenanted",
              "name": "Tenanted",
              "appliesTo": ["table"],
              "columns": [{ "id": "{{TenantColumnId}}", "name": "{{columnName}}", "type": "uuid", "nullable": true }]
            }
            """);
        await repo.RestartAsync();
        return repo;
    }

    /// <summary>A copy of a snapshot whose suppression lookup is not built yet (the store's validations build theirs).</summary>
    private static ModelSnapshot Fresh(ModelSnapshot model) => ModelSnapshot.CreateAfter(null, model.Documents, model.Settings, model.SettingsHash,
        model.Extensions, model.RuleScripts, model.Version, null, 1, Ct);

    [Fact]
    public async Task A_suppression_hides_a_finding_on_its_element_only()
    {
        await using var repo = await CreateAsync([EveryTableRule], null, (NotesPath, Suppress(("x/every-table", null))));
        var report = await ValidateAsync(repo);
        Assert.DoesNotContain(report.Diagnostics, d => d.Rule == "x/every-table" && d.FilePath == NotesFile);
        Assert.Contains(report.Diagnostics, d => d.Rule == "x/every-table" && d.FilePath != NotesFile);
        var moved = Assert.Single(report.Suppressed);
        Assert.Equal(("x/every-table", NotesFile, NotesId), (moved.Finding.Rule, moved.Finding.FilePath, moved.ElementId));
        Assert.Equal(report.Diagnostics.Count(d => d.Severity == DiagnosticSeverity.Error), report.Errors);
    }

    [Fact]
    public async Task A_member_limits_it_to_that_part_and_survives_a_move_in_its_array()
    {
        await using var repo = await CreateAsync([EveryColumnRule], null, (NotesPath, Suppress(("x/every-column", BodyColumnId))));
        var report = await ValidateAsync(repo);
        var moved = Assert.Single(report.Suppressed);
        Assert.Equal(BodyColumnId, moved.Finding.ElementId);
        Assert.Contains(report.Diagnostics, d => d.Rule == "x/every-column" && d.FilePath == NotesFile);
        var pointer = moved.Finding.JsonPointer;

        // The body column moves to the front of its array: the entry still matches it, at its new pointer.
        await repo.EditAsync(NotesId, node =>
        {
            var columns = node["columns"]!.AsArray();
            var body = columns.First(c => (string?)c!["id"] == BodyColumnId)!;
            columns.Remove(body);
            columns.Insert(0, body);
        });
        var after = await ValidateAsync(repo);
        var again = Assert.Single(after.Suppressed);
        Assert.Equal(BodyColumnId, again.Finding.ElementId);
        Assert.NotEqual(pointer, again.Finding.JsonPointer);
    }

    [Fact]
    public async Task A_suppressed_error_does_not_refuse_a_save()
    {
        await using var repo = await CreateAsync([], null);
        var document = await repo.Store.GetElementAsync(NotesId, Ct);
        var node = JsonNode.Parse(document!.Json.GetRawText())!.AsObject();
        node["columns"]!.AsArray().Add(new JsonObject { ["id"] = "01K6BNE0000000000000000020", ["name"] = "body", ["type"] = "text" });
        var refused = await repo.Store.SaveAsync(NotesId, Encoding.UTF8.GetBytes(node.ToJsonString()), document.Hash, ChangeSource.Editor, Ct);
        Assert.Equal(SaveOutcome.Invalid, refused.Outcome);
        Assert.Contains(refused.Diagnostics, d => d.Rule == "MQ4003");

        Suppress(("MQ4003", null))(node);
        var saved = await repo.Store.SaveAsync(NotesId, Encoding.UTF8.GetBytes(node.ToJsonString()), document.Hash, ChangeSource.Editor, Ct);
        Assert.Equal(SaveOutcome.Saved, saved.Outcome);
        Assert.DoesNotContain(saved.Diagnostics, d => d.Rule == "MQ4003");
        var report = await repo.Store.ValidateAsync(new ValidationScope([NotesId]), Ct);
        Assert.Contains(report.Suppressed, s => s.Finding.Rule == "MQ4003" && s.Finding.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public async Task Resolver_and_check_findings_are_suppressed_too()
    {
        const string longId = "01K6BNE0000000000000000030";
        var longName = "a" + new string('b', 70);
        await using var repo = await CreateAsync([EveryTableRule], null,
            ("model/entities/customer.json", node =>
            {
                node["attributes"]!.AsArray().Add(new JsonObject { ["id"] = longId, ["name"] = longName, ["type"] = "string" });
                // The resolver reports a conventional column name on the entity, without a pointer: the whole element accepts it.
                Suppress(("MQ4001", null))(node);
            }),
            (NotesPath, Suppress(("x/every-table", null))));
        var report = await ValidateAsync(repo);
        Assert.False(report.Diagnostics.Any(d => d.Rule == "MQ4001"), Show(report));
        Assert.Contains(report.Suppressed, s => s.Finding.Rule == "MQ4001" && s.ElementId == E2ERepo.CustomerId);
        Assert.Contains(report.Suppressed, s => s.Finding.Rule == "x/every-table" && s.ElementId == NotesId);
        Assert.DoesNotContain(report.Diagnostics, d => d.Rule == "MQ5020");
        // Positioned as reported findings are: a finding with a pointer gets its line.
        Assert.All(report.Suppressed, s => Assert.Equal(s.Finding.JsonPointer is not null, s.Finding.Line is not null));
    }

    [Fact]
    public async Task Suppressions_apply_after_severity_overrides()
    {
        await using var repo = await CreateAsync([EveryTableRule],
            settings => settings["validation"] = new JsonObject { ["rules"] = new JsonObject { ["x/every-table"] = "warning" } },
            (NotesPath, Suppress(("x/every-table", null))));
        var report = await ValidateAsync(repo);
        var moved = Assert.Single(report.Suppressed);
        Assert.Equal(DiagnosticSeverity.Warning, moved.Finding.Severity);
    }

    [Fact]
    public async Task An_unused_suppression_is_MQ5020_only_when_the_model_resolved()
    {
        await using var repo = await CreateAsync([EveryTableRule], null, (NotesPath, Suppress(("x/every-table", null), ("MQ2022", null))));
        var report = await ValidateAsync(repo);
        var unused = Assert.Single(report.Diagnostics, d => d.Rule == "MQ5020");
        Assert.Equal((NotesFile, "/suppressions/1", DiagnosticSeverity.Warning), (unused.FilePath, unused.JsonPointer, unused.Severity));
        Assert.NotNull(unused.Line);
        Assert.Equal(report.Diagnostics.Where(d => d.Rule == "MQ5020"), (await ValidateAsync(repo)).Diagnostics.Where(d => d.Rule == "MQ5020"));

        // A save validates some elements: no MQ5020.
        var scoped = await repo.Store.ValidateAsync(new ValidationScope([NotesId]), Ct);
        Assert.DoesNotContain(scoped.Diagnostics, d => d.Rule == "MQ5020");

        // An error stops resolution: the declared rule is not judged, and no entry is called unused.
        Edit(repo, "model/entities/customer.json", node => node["attributes"]!.AsArray().Add(new JsonObject
        {
            ["id"] = "01K6BNE0000000000000000040", ["name"] = "name", ["type"] = "string",
        }));
        await repo.RestartAsync();
        var broken = await ValidateAsync(repo);
        Assert.True(broken.HasErrors, Show(broken));
        Assert.DoesNotContain(broken.Diagnostics, d => d.Rule == "MQ5020");
    }

    [Fact]
    public async Task Suppressed_findings_carry_their_reason_and_entry()
    {
        await using var repo = await CreateAsync([EveryTableRule], null, (NotesPath, Suppress(("MQ2022", null), ("x/every-table", null))));
        var report = await ValidateAsync(repo);
        var moved = Assert.Single(report.Suppressed);
        Assert.Equal(("Accepted: x/every-table.", NotesId, "/suppressions/1"), (moved.Reason, moved.ElementId, moved.JsonPointer));
        Assert.Equal("Table notes fails.", moved.Finding.Message);
        Assert.NotNull(moved.Finding.Line);
        Assert.Equal(report.Errors, report.Diagnostics.Count(d => d.Severity == DiagnosticSeverity.Error));

        // A truncated report drops the suppressed findings before any diagnostic.
        var truncated = report.TruncateTo(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(report with { Suppressed = [] },
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)).Length + 20);
        Assert.Empty(truncated.Suppressed);
        Assert.Equal(report.Diagnostics, truncated.Diagnostics);
        Assert.True(truncated.Truncated);
    }

    [Fact]
    public async Task Validate_against_a_baseline_suppresses_its_findings_and_judges_MQ5020_after_them()
    {
        await using var repo = await CreateAsync([], E2ERepo.RecordOff);
        await repo.EditAsync(RegisterId, e => e["columns"]![0]!["nativeType"] = "text");
        var against = (await new SnapshotLibrary(repo.Store).CreateAsync(new SnapshotCreateRequest("Base"), Ct)).Id;
        string? column = null;
        await repo.EditAsync(RegisterId, e =>
        {
            e["columns"]![0]!["nativeType"] = "varchar(20)";
            column = (string?)e["columns"]![0]!["id"];
            Suppress(("MQ4102", column))(e);
        });

        using (var baseline = await ModelBaseline.OpenAsync(repo.Store, against, repo.Repo.RepoRoot, allowFolders: true, library: null, Ct))
        {
            var report = await repo.Store.ValidateAgainstAsync(baseline, Ct);
            Assert.DoesNotContain(report.Diagnostics, d => d.Rule is "MQ4102" or "MQ5020");
            var moved = Assert.Single(report.Suppressed);
            Assert.Equal(("MQ4102", InvoiceOverlayColumn, "/suppressions/0"), (moved.Finding.Rule, moved.Finding.JsonPointer, moved.JsonPointer));
        }

        // Without the baseline nothing matches the entry.
        var plain = await ValidateAsync(repo);
        Assert.Equal(["/suppressions/0"], plain.Diagnostics.Where(d => d.Rule == "MQ5020").Select(d => d.JsonPointer));
    }

    [Fact]
    public async Task A_declared_rule_finding_on_an_inherited_column_is_suppressed_by_its_keyed_id()
    {
        await using var repo = await TenantedNotesAsync([EveryColumnRule], "tenant_id", ("x/every-column", NotesTenantKeyed));
        var report = await ValidateAsync(repo);
        var moved = Assert.Single(report.Suppressed);
        Assert.Equal(("x/every-column", NotesFile, NotesTenantKeyed, NotesId), (moved.Finding.Rule, moved.Finding.FilePath, moved.Finding.ElementId, moved.ElementId));
        Assert.DoesNotContain(report.Diagnostics, d => d.ElementId == NotesTenantKeyed);
        // The table's own column is still reported, and the entry is neither unknown nor unused.
        Assert.Contains(report.Diagnostics, d => d.Rule == "x/every-column" && d.ElementId == BodyColumnId);
        Assert.DoesNotContain(report.Diagnostics, d => d.Rule is "MQ5019" or "MQ5020");
    }

    [Fact]
    public async Task A_resolver_finding_on_an_inherited_column_is_suppressed_by_its_keyed_id()
    {
        // The contributed column's name is longer than the dialect allows: the resolver reports it on the table that inherits it.
        await using var repo = await TenantedNotesAsync([], "tenant_" + new string('x', 70), ("MQ4001", NotesTenantKeyed));
        var report = await ValidateAsync(repo);
        Assert.False(report.HasErrors, Show(report));
        var moved = Assert.Single(report.Suppressed);
        Assert.Equal(("MQ4001", NotesFile, NotesTenantKeyed, NotesId), (moved.Finding.Rule, moved.Finding.FilePath, moved.Finding.ElementId, moved.ElementId));
        Assert.DoesNotContain(report.Diagnostics, d => d.Rule is "MQ5019" or "MQ5020");
    }

    [Fact]
    public async Task A_model_without_suppressions_reads_no_document_for_them()
    {
        // Counted, not timed: the index walk counts the entries, so a model without any shares the empty lookup and reads no document.
        await using var repo = await CreateAsync([EveryTableRule], null);
        Assert.Empty((await ValidateAsync(repo)).Suppressed);
        var plain = Fresh(repo.Store.Current!);
        Assert.Equal(0, plain.SuppressionCount);
        Suppressions.DocumentsRead = 0;
        Assert.Same(Suppressions.None, Suppressions.Of(plain));
        Assert.Equal(0, Suppressions.DocumentsRead);

        // A save adds one entry (the patched index counts it): the lookup reads every document once. Removing it shares the empty one again.
        await repo.EditAsync(NotesId, Suppress(("x/every-table", null)));
        Assert.True(repo.Store.Current!.Index.Patched);
        Assert.Equal(1, repo.Store.Current!.SuppressionCount);
        var one = Fresh(repo.Store.Current!);
        Suppressions.DocumentsRead = 0;
        Assert.False(Suppressions.Of(one).IsEmpty);
        Assert.Equal(one.Documents.Count, Suppressions.DocumentsRead);

        await repo.EditAsync(NotesId, node => node.Remove("suppressions"));
        Assert.Equal(0, repo.Store.Current!.SuppressionCount);
        Suppressions.DocumentsRead = 0;
        Assert.Same(Suppressions.None, Suppressions.Of(Fresh(repo.Store.Current!)));
        Assert.Equal(0, Suppressions.DocumentsRead);
    }
}
