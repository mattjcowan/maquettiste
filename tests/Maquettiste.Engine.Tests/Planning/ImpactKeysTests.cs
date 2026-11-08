using System.Text;
using System.Text.Json.Nodes;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Planning;
using Maquettiste.Engine.Tests.Store;

namespace Maquettiste.Engine.Tests.Planning;

/// <summary>When a dry run's change touches a stored read key (next-phase-2-design.md section 6.1, decision 3).</summary>
public sealed class ImpactKeysTests
{
    private const string Invoice = "01J92P0V0FJ23CGSNKM7P1W5V7";
    private const string Number = "01J92P0V0R3VSP7D5238DTNZX1";
    private const string Customer = "01J92P0V0ETQKXXP951CMMNHH3";
    private const string InvoiceStatus = "01J92P0V05WQDH2GZG539D9VG1";
    private const string BillingPackage = "01J92P0V01KDRN8GX5PGYCNKSX";
    private const string Main = "01J92P0V1QRN2181XM2ZWE02W4";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Update(BillingStore s, string id, Action<JsonObject> change)
    {
        var document = s.Model.GetDocument(id)!;
        return $$"""{ "op": "update", "id": "{{id}}", "expectedHash": "{{document.Hash}}", "element": {{Encoding.UTF8.GetString(BillingStore.Edit(document, change))}} }""";
    }

    private static string Delete(BillingStore s, string id) =>
        $$"""{ "op": "delete", "id": "{{id}}", "expectedHash": "{{s.Model.GetDocument(id)!.Hash}}" }""";

    private static string Create(string element) => $$"""{ "op": "create", "element": {{element}} }""";

    /// <summary>The touched-key test of a dry run of the operations, as the generation service builds it.</summary>
    private static async Task<(ImpactKeys Keys, BatchPreview Preview)> KeysAsync(BillingStore s, params string[] operations)
    {
        var parsed = s.Store.ParseBatch(Encoding.UTF8.GetBytes($$"""{ "operations": [ {{string.Join(", ", operations)}} ] }"""));
        Assert.True(parsed.Batch is not null, string.Join("; ", parsed.Diagnostics.Select(d => $"{d.Rule} {d.JsonPointer} {d.Message}")));
        var run = await s.Store.PreviewBatchCoreAsync(parsed.Batch, Ct);
        Assert.True(run.Preview.Outcome == SaveOutcome.Saved,
            run.Preview.Outcome + ": " + string.Join("; ", run.Preview.Items.SelectMany(i => i.Diagnostics).Select(d => d.Rule + " " + d.Message)));
        var keys = new ImpactKeys(run.Before!, run.Candidate!, run.Changed, run.Preview.Files,
            run.Preview.Impact!.Databases.Select(d => d.Id), run.Preview.Facts);
        return (keys, run.Preview);
    }

    /// <summary>The test over one model with the given files written (the model itself unchanged).</summary>
    private static ImpactKeys Files(BillingStore s, params string[] paths) =>
        new(s.Model, s.Model, [], [.. paths.Select(p => new BatchPreviewFile(p, "changed", null, null, null, "h", "before", "after"))], [], []);

    [Fact]
    public async Task A_batch_that_only_adds_a_named_trigger_point_touches_the_trigger_points_naming_key()
    {
        await using var s = await BillingStore.OpenAsync();
        var (keys, _) = await KeysAsync(s, Create($$"""
            { "kind": "trigger-point", "name": "CustomerNumber", "package": "{{BillingPackage}}", "subjects": [ { "entity": "{{Customer}}" } ],
              "type": "action", "timing": "before", "verbs": [ "create" ] }
            """));
        Assert.True(keys.IsTouched("tn:trigger-points"));
        Assert.True(keys.IsTouched("k:trigger-point"));
    }

    // A library with one gate policy, with stages or with today's fields, and an orchestration whose one move uses it or not
    // (signoff-design.md section 7).
    private static string Policies(bool stages) => $$"""
        { "kind": "process", "id": "01J92P0VZZ00000000000000A1", "name": "Policies", "package": "{{BillingPackage}}", "use": "library",
          "gates": [ { "id": "01J92P0VZZ00000000000000A2", "name": "Release", "meanings": [ { "id": "01J92P0VZZ00000000000000A3", "name": "approved" } ],
            {{(stages
                ? """ "stages": [ { "id": "01J92P0VZZ00000000000000A4", "name": "Review", "signatures": [ { "id": "01J92P0VZZ00000000000000A5", "name": "Clerk", "signers": [ "01J92P0VZZ00000000000000A9" ] } ] } ] """
                : """ "signers": [ "01J92P0VZZ00000000000000A9" ] """)}} } ] }
        """;

    private const string Clerk = """{ "kind": "actor", "id": "01J92P0VZZ00000000000000A9", "name": "Clerk", "package": "01J92P0V01KDRN8GX5PGYCNKSX", "type": "role" }""";

    private static string Flow(bool usesPolicy) => $$"""
        { "kind": "process", "name": "Flow", "package": "{{BillingPackage}}", "use": "orchestration",
          "states": [ { "id": "01J92P0VZZ00000000000000B1", "name": "Open" }, { "id": "01J92P0VZZ00000000000000B2", "name": "Done" } ],
          "events": [ { "id": "01J92P0VZZ00000000000000B3", "name": "finish" } ],
          "transitions": [ { "id": "01J92P0VZZ00000000000000B4", "source": "01J92P0VZZ00000000000000B1", "event": "01J92P0VZZ00000000000000B3",
            "targets": [ "01J92P0VZZ00000000000000B2" ]{{(usesPolicy ? ", \"gatePolicy\": \"01J92P0VZZ00000000000000A2\"" : "")}} } ] }
        """;

    [Fact]
    public async Task The_approvals_naming_key_is_touched_only_when_a_process_starts_using_an_approval()
    {
        await using var s = await BillingStore.OpenAsync();
        // A process without approvals, and an unused gate policy with stages: a process changes, the key's hash does not.
        var (plain, _) = await KeysAsync(s, Create(Clerk), Create(Policies(stages: false)), Create(Flow(usesPolicy: true)));
        Assert.False(plain.IsTouched(ApprovalNaming.Key));
        var (unused, _) = await KeysAsync(s, Create(Clerk), Create(Policies(stages: true)), Create(Flow(usesPolicy: false)));
        Assert.False(unused.IsTouched(ApprovalNaming.Key));
        // A change to no process leaves it alone too.
        var (entity, _) = await KeysAsync(s, Update(s, Invoice, e => e["description"] = "A bill."));
        Assert.False(entity.IsTouched(ApprovalNaming.Key));

        // The first process that uses a gate policy with stages.
        var (first, _) = await KeysAsync(s, Create(Clerk), Create(Policies(stages: true)), Create(Flow(usesPolicy: true)));
        Assert.True(first.IsTouched(ApprovalNaming.Key));
    }

    [Fact]
    public async Task An_element_key_is_touched_when_its_document_is_written_or_deleted()
    {
        await using var s = await BillingStore.OpenAsync();
        var (written, _) = await KeysAsync(s, Update(s, Invoice, e => e["description"] = "A bill."));
        Assert.True(written.IsTouched("e:" + Invoice));
        Assert.True(written.IsTouched("e:" + Number)); // a sub-element's key reads its owner's document
        Assert.False(written.IsTouched("e:" + Customer));

        var diagram = s.Model.All<Diagram>()[0].Id;
        var (deleted, _) = await KeysAsync(s, Delete(s, diagram));
        Assert.True(deleted.IsTouched("e:" + diagram));
        Assert.False(deleted.IsTouched("e:" + Invoice));
    }

    [Fact]
    public async Task A_kind_set_key_is_touched_by_a_create_or_a_delete_only()
    {
        await using var s = await BillingStore.OpenAsync();
        var (updated, _) = await KeysAsync(s, Update(s, Invoice, e => e["name"] = "Bill"));
        Assert.False(updated.IsTouched("k:entity"));
        var (created, _) = await KeysAsync(s, Create("""{ "kind": "entity", "name": "Refund", "package": "01J92P0V01KDRN8GX5PGYCNKSX" }"""));
        Assert.True(created.IsTouched("k:entity"));
        Assert.False(created.IsTouched("k:enum"));
        Assert.False(created.IsTouched("k:not-a-kind"));
    }

    [Fact]
    public async Task A_referrers_key_is_touched_when_a_written_document_starts_or_stops_referencing()
    {
        await using var s = await BillingStore.OpenAsync();
        var (starts, _) = await KeysAsync(s, Create($$"""
            { "kind": "entity", "name": "StatusLog", "attributes": [ { "name": "status", "type": { "ref": "{{InvoiceStatus}}" } } ] }
            """));
        Assert.True(starts.IsTouched("r:" + InvoiceStatus));
        Assert.False(starts.IsTouched("r:" + Customer));

        var status = "01J92P0V0VB1Z49SWERMAGR4TV";
        var (stops, _) = await KeysAsync(s, Update(s, Invoice, e => e["attributes"]!.AsArray().Remove(e["attributes"]!.AsArray().Single(a => (string?)a!["id"] == status))));
        Assert.True(stops.IsTouched("r:" + InvoiceStatus));
    }

    [Fact]
    public async Task A_name_key_is_touched_when_the_ids_its_form_finds_change()
    {
        await using var s = await BillingStore.OpenAsync();
        var invoice = s.Model.Names("entity").Entries.Single(e => e.Id == Invoice);
        var customer = s.Model.Names("entity").Entries.Single(e => e.Id == Customer);
        var (keys, _) = await KeysAsync(s, Update(s, Invoice, e => e["name"] = "Bill"));
        Assert.True(keys.IsTouched("n:entity:" + invoice.Forms[0]));
        Assert.True(keys.IsTouched("n:entity:" + invoice.Longest));
        Assert.True(keys.IsTouched("n:entity:" + invoice.Longest.Replace("Invoice", "Bill", StringComparison.Ordinal)));
        Assert.False(keys.IsTouched("n:entity:" + customer.Longest));
        // A kind the change's documents hold no entries of, and no qualifier changed.
        Assert.False(keys.IsTouched("n:enum:" + s.Model.Names("enum").Entries.Single(e => e.Id == InvoiceStatus).Longest));
    }

    [Fact]
    public async Task A_name_key_is_touched_when_a_package_rename_changes_the_ids_its_form_finds()
    {
        await using var s = await BillingStore.OpenAsync();
        var invoice = s.Model.Names("entity").Entries.Single(e => e.Id == Invoice);
        Assert.True(invoice.Forms.Length > 1, string.Join(", ", invoice.Forms));
        var (keys, _) = await KeysAsync(s, Update(s, BillingPackage, e => e["name"] = "Invoicing"));
        Assert.True(keys.IsTouched("n:entity:" + invoice.Longest));
        Assert.False(keys.IsTouched("n:entity:" + invoice.Forms[0])); // the short form still finds Invoice alone
    }

    [Fact]
    public async Task An_element_key_is_not_touched_when_its_dependency_hash_is_unchanged()
    {
        await using var s = await BillingStore.OpenAsync();
        var (keys, _) = await KeysAsync(s, Update(s, Invoice, e => e["description"] = "A bill."), Update(s, Customer, _ => { }));
        Assert.True(keys.IsTouched("e:" + Invoice));
        Assert.False(keys.IsTouched("e:" + Customer));
    }

    [Fact]
    public async Task A_schema_diff_key_and_a_table_name_key_are_touched_when_the_change_reaches_the_database()
    {
        await using var s = await BillingStore.OpenAsync();
        var (reaching, _) = await KeysAsync(s, Update(s, Invoice, e => e["description"] = "A bill."));
        Assert.True(reaching.IsTouched("d:" + Main));
        Assert.True(reaching.IsTouched("n:table:billing.invoices"));
        Assert.False(reaching.IsTouched("d:01J92P0V1QRN2181XM2ZWE0000"));

        var diagram = s.Model.All<Diagram>()[0].Id;
        var (elsewhere, preview) = await KeysAsync(s, Update(s, diagram, e => e["description"] = "Overview."));
        Assert.Empty(preview.Impact!.Databases);
        Assert.False(elsewhere.IsTouched("d:" + Main));
        Assert.False(elsewhere.IsTouched("n:table:billing.invoices"));
    }

    [Fact]
    public async Task A_settings_key_is_touched_only_when_its_section_changes()
    {
        await using var s = await BillingStore.OpenAsync();
        var before = s.Model;
        var path = Path.Combine(s.Harness.Repo.ModelRoot, "maquettiste.json");
        var settings = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        settings["properties"] = new JsonObject { ["team"] = "billing" };
        var saved = await s.Store.SaveSettingsAsync(Encoding.UTF8.GetBytes(settings.ToJsonString()), before.SettingsHash, ChangeSource.Cli, Ct);
        Assert.Equal(SaveOutcome.Saved, saved.Outcome);
        var file = new BatchPreviewFile(".maquettiste/maquettiste.json", "changed", null, null, null, "h", "before", "after");

        var keys = new ImpactKeys(before, s.Model, [], [file], [], []);
        Assert.True(keys.IsTouched("s:project"));
        Assert.False(keys.IsTouched("s:conventions"));
        Assert.False(keys.IsTouched("s:not-a-section"));
        // The same models without the settings file among the writes: nothing to compare.
        Assert.False(new ImpactKeys(before, s.Model, [], [], [], []).IsTouched("s:project"));
    }

    [Fact]
    public async Task Template_keys_are_never_touched()
    {
        await using var s = await BillingStore.OpenAsync();
        var (keys, _) = await KeysAsync(s, Update(s, Invoice, e => e["name"] = "Bill"));
        Assert.False(keys.IsTouched("t:csharp-dapper/entity.scriban"));
    }

    [Fact]
    public async Task An_extension_file_key_is_touched_only_when_the_batch_writes_its_file()
    {
        await using var s = await BillingStore.OpenAsync();
        var keys = Files(s, ".maquettiste/extensions/rows/countries.js", ".maquettiste/extensions/kinds/setting.json");
        Assert.True(keys.IsTouched("x:rows/countries.js"));
        Assert.False(keys.IsTouched("x:rows/regions.js"));
        Assert.True(keys.IsTouched("c:setting"));
        Assert.False(keys.IsTouched("c:feature"));
        Assert.False(Files(s).IsTouched("x:rows/countries.js"));
    }

    [Fact]
    public async Task Calculated_value_keys_are_never_touched()
    {
        await using var s = await BillingStore.OpenAsync();
        var (keys, _) = await KeysAsync(s, Update(s, Invoice, e => e["description"] = "A bill."));
        Assert.False(keys.IsTouched("cv:entity"));
        Assert.False(Files(s, ".maquettiste/extensions/calculated/entity.js").IsTouched("cv:entity"));
    }

    [Fact]
    public async Task Rule_view_keys_count_as_touched()
    {
        await using var s = await BillingStore.OpenAsync();
        var keys = Files(s);
        Assert.True(keys.IsTouched("ix:country:US"));
        Assert.True(keys.IsTouched("rt:" + InvoiceStatus));
    }

    [Fact]
    public async Task The_facts_a_save_may_record_touch_the_migration_kind_set_and_the_databases_referrers()
    {
        await using var s = await BillingStore.OpenAsync();
        var (reshaping, preview) = await KeysAsync(s, Update(s, Invoice, e => e["attributes"]!.AsArray().Single(a => (string?)a!["id"] == Number)!["name"] = "code"));
        Assert.Null(preview.Facts); // one database: the save may record facts in it
        Assert.True(reshaping.IsTouched("k:migration"));
        Assert.True(reshaping.IsTouched("r:" + Main));

        var (describing, described) = await KeysAsync(s, Update(s, Invoice, e => e["description"] = "A bill."));
        Assert.Empty(described.Facts!);
        Assert.False(describing.IsTouched("k:migration"));
        Assert.False(describing.IsTouched("r:" + Main));

        var fact = new RecordedFact("01J92P0VZZ0000000000000099", Main, "rename", "pending", [], "A rename.", "model/migrations/a.json");
        var listed = new ImpactKeys(s.Model, s.Model, [], [], [Main], [fact]);
        Assert.True(listed.IsTouched("k:migration"));
        Assert.True(listed.IsTouched("r:" + Main));
        Assert.True(listed.IsTouched("e:" + fact.Id));
    }

    [Fact]
    public async Task An_unknown_key_kind_counts_as_touched()
    {
        await using var s = await BillingStore.OpenAsync();
        var keys = Files(s);
        Assert.True(keys.IsTouched("zz:anything"));
        Assert.True(keys.IsTouched("no-colon"));
        Assert.True(keys.IsTouched("px:csharp-dapper/" + Invoice)); // no stored exports to read: touched
    }
}
