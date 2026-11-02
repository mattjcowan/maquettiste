using System.Text.Json.Nodes;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Tests.Loading;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Store;

/// <summary>
/// Validate reports what generation would: once the whole model validates without error, the resolver's findings (the resolved MQ4005
/// above all) join the report of <see cref="ModelStore.ValidateAsync"/>, which every host's validate goes through, positioned, scoped
/// like the load diagnostics, and computed once per snapshot.
/// </summary>
public sealed class ValidateResolverFindingsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static LoaderHarness Pinned()
    {
        var h = new LoaderHarness();
        h.CopyFixture("models", "billing");
        h.Repo.WriteFile(BillingEdits.InvoiceOverlayPath, BillingEdits.PinCustomerForeignKey(h.Repo.ReadFile(BillingEdits.InvoiceOverlayPath)));
        return h;
    }

    [Fact]
    public async Task A_clean_model_has_no_resolver_findings()
    {
        using var h = new LoaderHarness();
        h.CopyFixture("models", "billing");
        await using var store = new ModelStore(h.Options);
        var report = await store.ValidateAsync(ValidationScope.All, Ct);
        Assert.False(report.HasErrors, string.Join("\n", report.Diagnostics.Select(d => d.Rule + " " + d.Message)));
    }

    [Fact]
    public async Task A_foreign_key_pinned_by_an_overlay_is_MQ4005_in_the_validation_report_with_its_file_pointer_and_position()
    {
        using var h = Pinned();
        await using var store = new ModelStore(h.Options);

        var report = await store.ValidateAsync(ValidationScope.All, Ct);
        var d = Assert.Single(report.Diagnostics, x => x.Rule == "MQ4005");
        Assert.Equal(DiagnosticSeverity.Error, d.Severity);
        Assert.Equal(BillingEdits.InvoiceOverlayId, d.ElementId);
        Assert.Equal(BillingEdits.InvoiceOverlayPath, d.FilePath);
        Assert.Equal(BillingEdits.PinnedPointer, d.JsonPointer);
        Assert.Equal(BillingEdits.ForeignKeyMismatch, d.Message);
        Assert.True(d.Line > 0);
        Assert.True(report.HasErrors);
        Assert.Equal(1, report.Errors);

        // Scoped like the load diagnostics: the overlay's own scope carries it, another element's does not.
        var scoped = await store.ValidateAsync(new ValidationScope([BillingEdits.InvoiceOverlayId], IncludeReferrers: false), Ct);
        Assert.Contains(scoped.Diagnostics, x => x.Rule == "MQ4005");
        var other = await store.ValidateAsync(new ValidationScope(["01J92P0V0HEGSC6MW92CST5KA6"], IncludeReferrers: false), Ct);
        Assert.DoesNotContain(other.Diagnostics, x => x.Rule == "MQ4005");

        // The same snapshot again: the same single finding (computed once and kept with the snapshot).
        var again = await store.ValidateAsync(ValidationScope.All, Ct);
        Assert.Equal(report.Diagnostics, again.Diagnostics);
    }

    [Fact]
    public async Task Validate_reports_binding_drift()
    {
        // InvoiceNote's binding records the notes table as it is (what a by-name operation writes), then the body column is renamed.
        using var h = new LoaderHarness();
        h.CopyFixture("models", "billing");
        const string Entity = "model/entities/invoice-note.json";
        const string Notes = "model/databases/main/tables/notes.json";
        await using (var before = new ModelStore(h.Options))
        {
            var snapshot = await before.GetSnapshotAsync(Ct);
            var columns = BindingDrift.ColumnsOf(snapshot, snapshot.Get<Table>("01K6BND0000000000000000001")!);
            var entity = JsonNode.Parse(h.Read(Entity))!;
            var provenance = new JsonObject();
            foreach (var column in columns)
                provenance[column.Id] = column.Shape;
            entity["bindings"]![0]!["provenance"] = new JsonObject { ["columns"] = provenance };
            h.Write(Entity, entity.ToJsonString());
        }

        await using (var inStep = new ModelStore(h.Options))
        {
            var clean = await inStep.ValidateAsync(ValidationScope.All, Ct);
            Assert.DoesNotContain(clean.Diagnostics, d => d.Rule is "MQ4087" or "MQ4088");
        }

        h.Write(Notes, h.Read(Notes).Replace("\"name\": \"body\"", "\"name\": \"note_body\"", StringComparison.Ordinal));
        await using var store = new ModelStore(h.Options);
        var report = await store.ValidateAsync(ValidationScope.All, Ct);
        var d = Assert.Single(report.Diagnostics, x => x.Rule == "MQ4087");
        Assert.Equal(DiagnosticSeverity.Warning, d.Severity);
        Assert.Equal("01K6BND0000000000000000010", d.ElementId);
        Assert.Equal(".maquettiste/" + Entity, d.FilePath);
        Assert.Equal("/bindings/0", d.JsonPointer);
        Assert.True(d.Line > 0);
        Assert.Contains("'note_body' renamed (attribute 'body')", d.Message, StringComparison.Ordinal);
        Assert.False(report.HasErrors);
    }

    [Fact]
    public void A_resolver_finding_validation_already_reports_is_not_added_again()
    {
        var file = new Diagnostic("MQ4001", DiagnosticSeverity.Warning, "Table name 'x' is long.", "T1", "t.json", "/name", 3, 11);
        var same = file with { Line = null, Column = null };
        var unpointed = new Diagnostic("MQ4001", DiagnosticSeverity.Warning, "Table name 'x' in database 'main' is long.", "T1", "t.json", null, null, null);
        var elsewhere = unpointed with { ElementId = "T2" };
        var resolved = new Diagnostic("MQ4005", DiagnosticSeverity.Error, "m", "T1", "t.json", "/columns/0", null, null);
        var added = ModelStore.NewFindings([file], [same, unpointed, elsewhere, resolved]).ToList();
        // The exact repeat (same rule, element, file, pointer and message) and the unpointed one on the same element go.
        Assert.Equal([elsewhere, resolved], added);
    }
}
