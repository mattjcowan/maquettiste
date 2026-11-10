using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Processes;
using Maquettiste.Engine.Tests.Loading;
using static Maquettiste.Engine.Tests.Revisions.RevisionFamily;

namespace Maquettiste.Engine.Tests.Revisions;

/// <summary>
/// The revisions block in the model's bookkeeping (revisions-design.md section 10, "Deletes" and "Index"), over <see cref="RevisionFamily"/>:
/// every id in it (<c>boundAttribute</c>, each entry's lifecycle, state, attribute and end) is a reference that <c>get_references</c>,
/// validation and batch names see, and <c>restart</c> and <c>keep</c> are none; a delete is refused while the block names what it removes,
/// unless a resolution clears <c>boundAttribute</c> and removes each entry naming it, under both choices, with a list going with its last
/// entry and a block with its last key; dry runs show it; <c>nextRevision</c> survives an export and a re-import; the index format stays.
/// </summary>
public sealed class RevisionReferenceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static JsonObject File(LoaderHarness h, string path) => JsonNode.Parse(h.Read(path))!.AsObject();

    private static JsonObject? Block(LoaderHarness h, string path) => File(h, path)["revisions"]?.AsObject();

    private static string Why(DeletePlan plan) => string.Join(" | ", plan.Refused.Select(r => r.Id + " " + r.Pointer + " " + r.Why));

    private static async Task Delete(ModelStore store, string id, DeleteResolution resolution)
    {
        var result = await store.DeleteAsync(id, store.Current!.GetDocument(id)!.Hash, resolution, ChangeSource.Editor, Ct);
        Assert.True(result.Outcome == SaveOutcome.Saved, string.Join(" | ", result.Diagnostics.Select(d => d.Rule + " " + d.Message)));
    }

    // ---- references ----

    [Fact]
    public async Task Every_id_in_the_block_is_a_reference_and_restart_and_keep_are_none()
    {
        var (h, store) = await OpenAsync();
        using var _ = h;
        await using var __ = store;
        var model = store.Current!;

        static (string, string, string)[] Pointers(IEnumerable<ReferenceInfo> references) =>
            [.. references.Where(r => r.JsonPointer.StartsWith("/revisions/", StringComparison.Ordinal)).Select(r => (r.FromElementId, r.FromId, r.JsonPointer))
                .Order()];

        Assert.Equal([(Part, Part, "/revisions/boundAttribute"), (Memo, Memo, "/revisions/boundAttribute")], Pointers(await store.GetReferencesAsync(Revision, Ct)));
        Assert.Equal([(Part, Part, "/revisions/lifecycles/0/lifecycle"), (PurchasedPart, PurchasedPart, "/revisions/lifecycles/0/lifecycle")],
            Pointers(await store.GetReferencesAsync(Delivery, Ct)));
        Assert.Equal([(MadePart, MadePart, "/revisions/lifecycles/0/onRevise")], Pointers(await store.GetReferencesAsync(Approved, Ct)));
        Assert.Equal([(Part, Part, "/revisions/attributes/0/attribute"), (MadePart, MadePart, "/revisions/attributes/1/attribute")],
            Pointers(await store.GetReferencesAsync(ApprovedBy, Ct)));
        Assert.Equal([(MadePart, MadePart, "/revisions/attributes/0/attribute")], Pointers(await store.GetReferencesAsync(TrackingCode, Ct)));
        Assert.Equal([(Part, Part, "/revisions/links/0/end"), (Sheet, Sheet, "/revisions/links/0/end")], Pointers(await store.GetReferencesAsync(DocumentsEnd, Ct)));
        Assert.Equal([(Part, Part, "/revisions/links/1/end")], Pointers(await store.GetReferencesAsync(ToolsEnd, Ct)));

        // The words of a lifecycle entry are no references: Part keeps Delivery, PurchasedPart restarts it.
        Assert.DoesNotContain(model.ReferencesFrom(Part), r => r.JsonPointer.EndsWith("/onRevise", StringComparison.Ordinal));
        Assert.DoesNotContain(model.ReferencesFrom(PurchasedPart), r => r.JsonPointer.EndsWith("/onRevise", StringComparison.Ordinal));
        Assert.Empty(model.ReferencesTo(RevisionLifecycleEntry.Keep));
        Assert.Empty(model.ReferencesTo(RevisionLifecycleEntry.Restart));

        // Validation reads them as any reference: the family has none dangling or of the wrong kind.
        var findings = (await store.ValidateAsync(ValidationScope.All, Ct)).Diagnostics;
        Assert.DoesNotContain(findings, d => d.Rule is "MQ2001" or "MQ2002" && (d.JsonPointer ?? "").StartsWith("/revisions", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Validation_finds_a_dangling_id_and_one_of_the_wrong_kind_in_the_block()
    {
        var (h, store) = await OpenAsync(h =>
        {
            h.Write(MemoFile, Entity(Memo, "Memo", RecordType, "", $$"""
                {"boundAttribute":"{{Delivery}}","lifecycles":[{"lifecycle":"{{Approval}}","onRevise":"{{Revision}}"},{"lifecycle":"{{Delivery}}","onRevise":"keep"}],
                 "attributes":[{"attribute":"01JATT00000000000000000999","onRevise":"clear"}],"links":[{"end":"{{PartDocuments}}","onRevise":"stay"}]}
                """));
        });
        using var _ = h;
        await using var __ = store;

        var findings = (await store.ValidateAsync(ValidationScope.All, Ct)).Diagnostics
            .Where(d => d.ElementId == Memo && d.Rule is "MQ2001" or "MQ2002").Select(d => (d.Rule, d.JsonPointer)).Order().ToArray();

        Assert.Equal(
            [("MQ2001", "/revisions/attributes/0/attribute"), ("MQ2002", "/revisions/boundAttribute"), ("MQ2002", "/revisions/lifecycles/0/onRevise"),
             ("MQ2002", "/revisions/links/0/end")],
            findings);
    }

    [Fact]
    public async Task Batches_take_qualified_names_and_placeholders_in_the_block()
    {
        var (h, store) = await OpenAsync();
        using var _ = h;
        await using var __ = store;

        var parsed = store.ParseBatch(Encoding.UTF8.GetBytes("""
            { "operations": [
              { "op": "create", "element": { "kind": "entity", "name": "Drawing", "package": "@package:Sales", "base": "@entity:Record",
                  "attributes": [ { "as": "$rev", "name": "drawingRevision", "type": "string", "length": 20 } ],
                  "revisions": { "levels": [ { "format": "letters" } ], "boundAttribute": "$rev",
                    "lifecycles": [ { "lifecycle": "@process:Approval", "onRevise": "@state:Approval.Approved" }, { "lifecycle": "@process:Delivery", "onRevise": "keep" } ],
                    "attributes": [ { "attribute": "@attribute:Record.approvedBy", "onRevise": "reset" } ],
                    "links": [ { "end": "@end:PartDocuments.documents", "onRevise": "stay" } ] } } }
            ] }
            """));
        Assert.Empty(parsed.Diagnostics);

        var result = await store.ApplyBatchAsync(parsed.Batch!, ChangeSource.Editor, Ct);

        Assert.True(result.Outcome == SaveOutcome.Saved, string.Join(" | ", result.Items.SelectMany(i => i.Diagnostics).Select(d => d.Rule + " " + d.Message)));
        var drawing = store.Current!.Get<Entity>(result.Items[0].Id!)!;
        var block = drawing.Revisions!;
        Assert.Equal(drawing.Attributes[0].Id, block.BoundAttribute);
        Assert.Equal([(Approval, Approved), (Delivery, RevisionLifecycleEntry.Keep)], block.Lifecycles!.Select(l => (l.Lifecycle, l.OnRevise)));
        Assert.Equal(ApprovedBy, Assert.Single(block.Attributes!).Attribute);
        Assert.Equal(DocumentsEnd, Assert.Single(block.Links!).End);
        Assert.Equal(drawing.Attributes[0].Id, result.Ids.Names["$rev"]);
        Assert.Contains(await store.GetReferencesAsync(Approved, Ct), r => (r.FromElementId, r.JsonPointer) == (drawing.Id, "/revisions/lifecycles/0/onRevise"));
    }

    // ---- deletes ----

    [Fact]
    public async Task A_delete_is_refused_while_the_block_names_what_it_removes()
    {
        var (h, store) = await OpenAsync();
        using var _ = h;
        await using var __ = store;
        var before = h.Read(PartFile);

        var refused = await store.DeleteAsync(RecordType, store.Current!.GetDocument(RecordType)!.Hash, DeleteResolution.Refuse, ChangeSource.Editor, Ct);

        Assert.Equal(SaveOutcome.Referenced, refused.Outcome);
        Assert.Contains(refused.Referrers, r => (r.FromElementId, r.JsonPointer) == (Part, "/revisions/boundAttribute"));
        Assert.Contains(refused.Referrers, r => (r.FromElementId, r.JsonPointer) == (Part, "/revisions/attributes/0/attribute"));
        Assert.Contains(refused.Referrers, r => (r.FromElementId, r.JsonPointer) == (MadePart, "/revisions/attributes/1/attribute"));
        Assert.Contains(refused.Referrers, r => (r.FromElementId, r.JsonPointer) == (Memo, "/revisions/boundAttribute"));
        Assert.Equal(before, h.Read(PartFile));
        foreach (var target in new[] { Delivery, Approval, PartDocuments })
        {
            var other = await store.DeleteAsync(target, store.Current!.GetDocument(target)!.Hash, DeleteResolution.Refuse, ChangeSource.Editor, Ct);
            Assert.Equal(SaveOutcome.Referenced, other.Outcome);
        }
    }

    [Theory]
    [InlineData(DeleteResolution.RemoveReferences)]
    [InlineData(DeleteResolution.DeleteDependents)]
    public async Task Deleting_the_bound_attribute_clears_it_and_an_attribute_takes_its_entries(DeleteResolution resolution)
    {
        var (h, store) = await OpenAsync();
        using var _ = h;
        await using var __ = store;
        var part = h.Read(PartFile);

        // The dry run shows it, and writes nothing.
        var plan = await store.GetDeletePlanAsync([RecordType], resolution, Ct);
        Assert.True(plan.Outcome == SaveOutcome.Saved, Why(plan));
        Assert.Contains(plan.Clears, c => (c.Id, c.Pointer, c.Field, c.Target) == (Part, "/revisions/boundAttribute", "boundAttribute", Revision));
        var entry = Assert.Single(plan.Removes, r => r.Id == Part);
        Assert.Equal(("/revisions/attributes/0", "revisions entry for attribute approvedBy"), (entry.Pointer, entry.What));
        Assert.Contains(plan.Removes, r => (r.Id, r.Pointer) == (MadePart, "/revisions/attributes/1"));
        // Memo's block held only the bound attribute, so it goes whole.
        var memo = Assert.Single(plan.Removes, r => r.Id == Memo);
        Assert.Equal(("/revisions", "revisions block"), (memo.Pointer, memo.What));
        Assert.Contains("is left empty without attribute revision of entity Record", memo.Because, StringComparison.Ordinal);
        Assert.DoesNotContain(plan.Deletes, d => d.Id is Part or MadePart or Memo);
        Assert.Equal(part, h.Read(PartFile));

        var preview = await store.PreviewBatchAsync(new ModelBatch(
            [new BatchOperation(BatchOp.Delete, RecordType, store.Current!.GetDocument(RecordType)!.Hash, null, Resolution: resolution)]), Ct);
        Assert.Equal(SaveOutcome.Saved, preview.Outcome);
        var changed = Assert.Single(preview.Files, f => f.Id == Part);
        Assert.DoesNotContain("boundAttribute", changed.After!, StringComparison.Ordinal);
        Assert.Equal(part, h.Read(PartFile));

        await Delete(store, RecordType, resolution);

        var block = Block(h, PartFile)!;
        Assert.Equal(["levels", "lifecycles", "attributes", "links"], block.Select(p => p.Key));
        Assert.Equal([Note], block["attributes"]!.AsArray().Select(a => a!["attribute"]!.GetValue<string>()));
        Assert.Equal([TrackingCode], Block(h, MadePartFile)!["attributes"]!.AsArray().Select(a => a!["attribute"]!.GetValue<string>()));
        Assert.Null(Block(h, MemoFile));
        Assert.Null(store.Current!.Get<Entity>(Memo)!.Revisions);
        Assert.Empty(store.Current!.ReferencesTo(Revision));
    }

    [Theory]
    [InlineData(DeleteResolution.RemoveReferences)]
    [InlineData(DeleteResolution.DeleteDependents)]
    public async Task Deleting_a_lifecycle_takes_its_entries_and_a_list_goes_with_its_last_entry(DeleteResolution resolution)
    {
        var (h, store) = await OpenAsync();
        using var _ = h;
        await using var __ = store;

        var plan = await store.GetDeletePlanAsync([Delivery], resolution, Ct);
        Assert.True(plan.Outcome == SaveOutcome.Saved, Why(plan));
        Assert.Contains(plan.Removes, r => (r.Id, r.Pointer, r.What) == (Part, "/revisions/lifecycles/0", "revisions entry for lifecycle Delivery"));
        var list = Assert.Single(plan.Removes, r => r.Id == PurchasedPart);
        Assert.Equal(("/revisions/lifecycles", "revisions lifecycles"), (list.Pointer, list.What));

        await Delete(store, Delivery, resolution);

        Assert.Equal([Machining], Block(h, PartFile)!["lifecycles"]!.AsArray().Select(l => l!["lifecycle"]!.GetValue<string>()));
        Assert.Equal(["attributes"], Block(h, PurchasedPartFile)!.Select(p => p.Key));
        Assert.Empty(store.Current!.ReferencesTo(Delivery));
    }

    [Theory]
    [InlineData(DeleteResolution.RemoveReferences)]
    [InlineData(DeleteResolution.DeleteDependents)]
    public async Task Deleting_a_state_an_entry_names_takes_the_entry(DeleteResolution resolution)
    {
        // Part sends Delivery to a state of Approval (MQ8312's to report); the entry goes with the state alone.
        var (h, store) = await OpenAsync(h => h.Write(PartFile, Entity(Part, "Part", RecordType, Attr(Note, "note"),
            PartBlock.Replace("\"onRevise\":\"keep\"},{\"lifecycle\"", $"\"onRevise\":\"{Approved}\"}},{{\"lifecycle\"", StringComparison.Ordinal), Abstract)));
        using var _ = h;
        await using var __ = store;
        Assert.Contains(await store.GetReferencesAsync(Approved, Ct), r => (r.FromElementId, r.JsonPointer) == (Part, "/revisions/lifecycles/0/onRevise"));

        var plan = await store.GetDeletePlanAsync([Approval], resolution, Ct);
        Assert.True(plan.Outcome == SaveOutcome.Saved, Why(plan));
        Assert.Contains(plan.Removes, r => (r.Id, r.Pointer) == (Part, "/revisions/lifecycles/0"));
        // MadePart's one entry names Approval and its state: one removal, of the list.
        Assert.Equal(["/revisions/lifecycles"], plan.Removes.Where(r => r.Id == MadePart).Select(r => r.Pointer));

        await Delete(store, Approval, resolution);

        Assert.Equal([(Machining, "keep")], Block(h, PartFile)!["lifecycles"]!.AsArray().Select(l => (l!["lifecycle"]!.GetValue<string>(), l["onRevise"]!.GetValue<string>())));
        Assert.Equal(["levels", "separator", "attributes"], Block(h, MadePartFile)!.Select(p => p.Key));
    }

    [Theory]
    [InlineData(DeleteResolution.RemoveReferences)]
    [InlineData(DeleteResolution.DeleteDependents)]
    public async Task Deleting_a_relation_takes_the_entries_for_its_ends_and_a_block_goes_with_its_last_key(DeleteResolution resolution)
    {
        var (h, store) = await OpenAsync();
        using var _ = h;
        await using var __ = store;

        var plan = await store.GetDeletePlanAsync([PartDocuments], resolution, Ct);
        Assert.True(plan.Outcome == SaveOutcome.Saved, Why(plan));
        Assert.Contains(plan.Removes, r => (r.Id, r.Pointer, r.What) == (Part, "/revisions/links/0", "revisions entry for end documents"));
        Assert.Contains(plan.Removes, r => (r.Id, r.Pointer, r.What) == (Sheet, "/revisions", "revisions block"));

        await Delete(store, PartDocuments, resolution);

        Assert.Equal([ToolsEnd], Block(h, PartFile)!["links"]!.AsArray().Select(l => l!["end"]!.GetValue<string>()));
        Assert.False(File(h, SheetFile).ContainsKey("revisions"));
        Assert.NotNull(store.Current!.Get<Entity>(Sheet));
    }

    [Theory]
    [InlineData(DeleteResolution.RemoveReferences)]
    [InlineData(DeleteResolution.DeleteDependents)]
    public async Task A_block_emptied_by_several_deletes_in_one_batch_goes_once(DeleteResolution resolution)
    {
        var (h, store) = await OpenAsync(h => h.Write(SheetFile, Entity(Sheet, "Sheet", Part, "", $$"""
            {"lifecycles":[{"lifecycle":"{{Delivery}}","onRevise":"keep"}],"links":[{"end":"{{DocumentsEnd}}","onRevise":"stay"}]}
            """)));
        using var _ = h;
        await using var __ = store;
        var operations = new List<BatchOperation>();
        foreach (var id in new[] { Delivery, PartDocuments })
            operations.Add(new BatchOperation(BatchOp.Delete, id, store.Current!.GetDocument(id)!.Hash, null, Resolution: resolution));

        var plan = await store.GetDeletePlanAsync([Delivery, PartDocuments], resolution, Ct);
        Assert.True(plan.Outcome == SaveOutcome.Saved, Why(plan));
        Assert.Equal(["/revisions"], plan.Removes.Where(r => r.Id == Sheet).Select(r => r.Pointer));
        var result = await store.ApplyBatchAsync(new ModelBatch(operations), ChangeSource.Editor, Ct);

        Assert.True(result.Outcome == SaveOutcome.Saved, string.Join(" | ", result.Items.SelectMany(i => i.Diagnostics).Select(d => d.Rule + " " + d.Message)));
        Assert.False(File(h, SheetFile).ContainsKey("revisions"));
    }

    // ---- export and import, the index ----

    [Fact]
    public async Task Next_revision_survives_an_export_and_a_re_import()
    {
        const string P = "01JPRC00000000000000000001"; // OrderLifecycle
        const string ProcessFile = "model/processes/order-lifecycle.json";
        using var h = new LoaderHarness();
        h.CopyFixture("models", "process-basics");
        var process = File(h, ProcessFile);
        var transitions = process["transitions"]!.AsArray();
        transitions.Single(t => t!["id"]!.GetValue<string>() == "01JTRN00000000000000000001")!["nextRevision"] = 2;
        transitions.Single(t => t!["id"]!.GetValue<string>() == "01JTRN00000000000000000004")!["nextRevision"] = 1;
        h.Write(ProcessFile, process.ToJsonString());
        await using var store = h.NewStore();
        await store.LoadAsync(Ct);
        await store.FormatAsync([ProcessFile], ChangeSource.Editor, Ct); // canonical bytes, as every write leaves them
        var before = h.Read(ProcessFile);

        var export = (await store.ExportProcessAsync(P, Ct)).Value!;
        Assert.Contains("\"nextRevision\":2", export.Json.Replace(" ", "", StringComparison.Ordinal).Replace("\n", "", StringComparison.Ordinal), StringComparison.Ordinal);
        var again = await store.ImportProcessAsync(
            new ProcessImportRequest { Config = JsonDocument.Parse(export.Json).RootElement.Clone(), Into = P }, dryRun: false, ChangeSource.Editor, Ct);

        Assert.True(again.Status == ProcessCallStatus.Ok, string.Join(" | ", again.Diagnostics.Select(d => d.Rule + " " + d.Message)));
        Assert.Equal(before, h.Read(ProcessFile)); // the bytes: a dropped nextRevision fails
        var lifecycle = store.Current!.Get<Process>(P)!;
        Assert.Equal([("01JTRN00000000000000000001", 2), ("01JTRN00000000000000000004", 1)],
            lifecycle.Transitions.Where(t => t.NextRevision is not null).Select(t => (t.Id, t.NextRevision!.Value)));
    }

    [Fact]
    public void The_index_format_stays_e14()
    {
        // The block adds no row and no member to the index (section 10, "Index").
        Assert.Equal("maquettiste-index/e14", ModelReads.IndexFormat);
    }
}
