using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Hashing;
using Maquettiste.Engine.Loading;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Pipeline;
using Maquettiste.Engine.Tests.Loading;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Store;

/// <summary>Untrusted input, collisions inside one plan, and failures midway: the store answers with a result and changes nothing.</summary>
public sealed class ModelStoreHardeningTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static JsonElement Element(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static List<string> StagedLeftovers(BillingStore s) =>
        [.. s.Files().Keys.Where(p => AtomicFileSet.IsStagedName(p[(p.LastIndexOf('/') + 1)..]))];

    [Fact]
    public async Task Request_bodies_with_duplicated_properties_or_bad_utf8_are_invalid()
    {
        await using var s = await BillingStore.OpenAsync();
        var customer = s.Doc("entity", "Customer");
        var before = s.Files();

        var save = await s.Store.SaveAsync(customer.Element.Id, "{\"kind\":\"entity\",\"kind\":\"entity\",\"name\":\"Customer\"}"u8.ToArray(), customer.Hash, ChangeSource.Editor, Ct);
        var create = await s.Store.CreateAsync("{\"kind\":\"enum\",\"name\":\"A\",\"name\":\"B\"}"u8.ToArray(), ChangeSource.Editor, Ct);
        byte[] bad = [.. "{\"kind\":\"enum\",\"name\":\"A"u8, 0xFF, .. "\"}"u8];
        var utf8 = await s.Store.CreateAsync(bad, ChangeSource.Editor, Ct);

        foreach (var result in new[] { save, create, utf8 })
        {
            Assert.Equal(SaveOutcome.Invalid, result.Outcome);
            var d = Assert.Single(result.Diagnostics);
            Assert.Equal("MQ1001", d.Rule);
            Assert.Equal(1, d.Line);
            Assert.NotNull(d.Column);
        }

        Assert.Equal(25, utf8.Diagnostics[0].Column);
        Assert.Equal(before, s.Files());
        Assert.Empty(s.Notifications);
    }

    [Fact]
    public async Task A_batch_with_a_duplicated_property_is_refused_by_ParseBatch_and_by_ApplyBatch()
    {
        await using var s = await BillingStore.OpenAsync();
        var before = s.Files();

        var parsed = s.Store.ParseBatch("{\"operations\":[{\"op\":\"create\",\"element\":{\"kind\":\"enum\",\"name\":\"A\",\"name\":\"B\"}}]}"u8);
        Assert.Null(parsed.Batch);
        var d = Assert.Single(parsed.Diagnostics);
        Assert.Equal("MQ1001", d.Rule);
        Assert.Equal(1, d.Line);

        // A hand-built batch skips ParseBatch: the element is still parsed strictly.
        var batch = new ModelBatch(
        [
            new BatchOperation(BatchOp.Create, null, null, Element("{\"kind\":\"enum\",\"name\":\"Fine\"}")),
            new BatchOperation(BatchOp.Create, null, null, Element("{\"kind\":\"enum\",\"name\":\"A\",\"name\":\"B\"}")),
        ]);
        var result = await s.Store.ApplyBatchAsync(batch, ChangeSource.Editor, Ct);

        Assert.Equal(SaveOutcome.Invalid, result.Outcome);
        Assert.Null(result.Changes);
        Assert.Equal([SaveOutcome.Saved, SaveOutcome.Invalid], result.Items.Select(i => i.Outcome));
        Assert.Null(result.Items[0].Hash);
        var failure = Assert.Single(result.Items[1].Diagnostics);
        Assert.Equal(("MQ1001", "/operations/1/element"), (failure.Rule, failure.JsonPointer));
        Assert.Equal(before, s.Files());
    }

    [Fact]
    public async Task Two_elements_in_one_batch_may_not_claim_the_same_id()
    {
        await using var s = await BillingStore.OpenAsync();
        var before = s.Files();
        const string Member = "01J92P0V0000000000000000M1";

        var subIds = await s.Store.ApplyBatchAsync(new ModelBatch(
        [
            new BatchOperation(BatchOp.Create, null, null, Element($"{{\"kind\":\"enum\",\"name\":\"First\",\"members\":[{{\"id\":\"{Member}\",\"name\":\"One\"}}]}}")),
            new BatchOperation(BatchOp.Create, null, null, Element($"{{\"kind\":\"enum\",\"name\":\"Second\",\"members\":[{{\"id\":\"{Member}\",\"name\":\"One\"}}]}}")),
        ]), ChangeSource.Editor, Ct);

        Assert.Equal(SaveOutcome.Invalid, subIds.Outcome);
        Assert.Equal([SaveOutcome.Saved, SaveOutcome.Invalid], subIds.Items.Select(i => i.Outcome));
        var d = Assert.Single(subIds.Items[1].Diagnostics);
        Assert.Equal(("MQ1004", "/members/0/id"), (d.Rule, d.JsonPointer));

        // A new element's own id may not be another new element's sub-element id either.
        var topLevel = await s.Store.ApplyBatchAsync(new ModelBatch(
        [
            new BatchOperation(BatchOp.Create, null, null, Element($"{{\"kind\":\"enum\",\"name\":\"First\",\"members\":[{{\"id\":\"{Member}\",\"name\":\"One\"}}]}}")),
            new BatchOperation(BatchOp.Create, null, null, Element($"{{\"kind\":\"entity\",\"id\":\"{Member}\",\"name\":\"Clash\"}}")),
        ]), ChangeSource.Editor, Ct);

        Assert.Equal(SaveOutcome.Invalid, topLevel.Outcome);
        Assert.Equal("MQ1004", Assert.Single(topLevel.Items[1].Diagnostics).Rule);
        Assert.Equal(before, s.Files());
        Assert.DoesNotContain(s.Store.Current!.LoadDiagnostics, x => x.Rule == "MQ1004");
    }

    [Fact]
    public async Task A_sidecar_that_follows_its_element_never_overwrites_another_file()
    {
        await using var s = await BillingStore.OpenAsync();
        var reporting = await s.Store.CreateAsync("{\"kind\":\"database\",\"name\":\"Reporting\",\"dialect\":\"sqlite\"}"u8.ToArray(), ChangeSource.Editor, Ct);
        var other = await s.Store.CreateAsync(
            Encoding.UTF8.GetBytes($"{{\"kind\":\"sequence\",\"name\":\"other_seq\",\"database\":\"{reporting.Id}\",\"description\":{{\"file\":\"notes.md\"}}}}"),
            ChangeSource.Editor,
            Ct);
        Assert.Equal(SaveOutcome.Saved, other.Outcome);
        s.Harness.Write("model/databases/reporting/sequences/notes.md", "REPORTING NOTES");
        s.Harness.Write("model/databases/main/sequences/notes.md", "MAIN NOTES");
        var sequence = s.Doc("sequence", "invoice_number_seq");
        var described = await s.Store.SaveAsync(
            sequence.Element.Id,
            BillingStore.Edit(sequence, n => n["description"] = new JsonObject { ["file"] = "notes.md" }),
            sequence.Hash,
            ChangeSource.Editor,
            Ct);
        Assert.Equal(SaveOutcome.Saved, described.Outcome);

        var moved = await s.Store.SaveAsync(
            sequence.Element.Id,
            BillingStore.Edit(described.Current!, n => { n["database"] = reporting.Id; n.Remove("schema"); }),
            described.Hash!,
            ChangeSource.Editor,
            Ct);

        Assert.Equal(SaveOutcome.Saved, moved.Outcome);
        var suffix = ModelPaths.Suffix(sequence.Element.Id);
        Assert.Equal("notes" + suffix + ".md", moved.Current!.Element.Description!.File);
        Assert.Equal("REPORTING NOTES", s.Harness.Read("model/databases/reporting/sequences/notes.md"));
        Assert.Equal("MAIN NOTES", s.Harness.Read("model/databases/reporting/sequences/notes" + suffix + ".md"));
        Assert.False(s.Harness.Exists("model/databases/main/sequences/notes.md"));
        Assert.Equal("MAIN NOTES", moved.Current.SidecarText);
        Assert.Equal("REPORTING NOTES", s.Store.Current!.GetDocument(other.Id!)!.SidecarText);
        Assert.Empty(s.Store.Current.LoadDiagnostics);
    }

    [Fact]
    public async Task A_conflict_is_judged_against_the_disk_whatever_hash_the_caller_sends()
    {
        await using var s = await BillingStore.OpenAsync();
        var customer = s.Doc("entity", "Customer");
        var onDisk = TestServices.Json.Write(JsonNode.Parse(BillingStore.Edit(customer, n => n["displayName"] = "Disk"))!, "entity.json", customer.Path);
        s.Harness.Write("model/entities/customer.json", Encoding.UTF8.GetString(onDisk));
        var diskHash = ContentHash.Of(onDisk);

        // Neither the index hash nor the disk hash: a conflict that carries the disk version.
        var stale = await s.Store.SaveAsync(customer.Element.Id, BillingStore.Edit(customer, n => n["displayName"] = "Mine"), new string('a', 64), ChangeSource.Editor, Ct);
        Assert.Equal(SaveOutcome.Conflict, stale.Outcome);
        Assert.Equal(diskHash, stale.Hash);
        Assert.Equal("Disk", stale.Current!.Element.DisplayName);
        Assert.Equal(ChangeSource.Disk, Assert.Single(s.Notifications).Source);

        // The disk hash, which the index had not seen when the caller read the file: the save lands on that version.
        s.Harness.Write("model/entities/customer.json", Encoding.UTF8.GetString(TestServices.Json.Write(JsonNode.Parse(BillingStore.Edit(customer, n => n["displayName"] = "Disk 2"))!, "entity.json", customer.Path)));
        var second = ContentHash.Of(File.ReadAllBytes(s.Harness.Model("model/entities/customer.json")));
        var saved = await s.Store.SaveAsync(customer.Element.Id, BillingStore.Edit(customer, n => n["displayName"] = "Mine"), second, ChangeSource.Editor, Ct);
        Assert.Equal(SaveOutcome.Saved, saved.Outcome);
        Assert.Equal("Mine", saved.Current!.Element.DisplayName);
        Assert.Equal([ChangeSource.Disk, ChangeSource.Disk, ChangeSource.Editor], s.Notifications.Select(n => n.Source));
    }

    [Fact]
    public async Task Remove_references_refuses_to_empty_a_list_whose_empty_value_widens_it()
    {
        await using var s = await BillingStore.OpenAsync();
        var main = s.Doc("database", "main");
        var catalog = s.Doc("package", "Catalog");
        var billing = s.Id("package", "Billing");
        var scoped = await s.Store.SaveAsync(main.Element.Id, BillingStore.Edit(main, n => n["packages"] = new JsonArray(catalog.Element.Id)), main.Hash, ChangeSource.Editor, Ct);
        Assert.Equal(SaveOutcome.Saved, scoped.Outcome);
        var before = s.Files();

        var refused = await s.Store.DeleteAsync(catalog.Element.Id, catalog.Hash, DeleteResolution.RemoveReferences, ChangeSource.Editor, Ct);

        Assert.Equal(SaveOutcome.Invalid, refused.Outcome);
        var d = Assert.Single(refused.Diagnostics, x => x.Rule == "MQ2001");
        Assert.Equal((main.Element.Id, "/packages/0"), (d.ElementId, d.JsonPointer));
        Assert.Contains("every package", d.Message, StringComparison.Ordinal);
        Assert.Equal(before, s.Files());

        // With another package left in the list, the entry is simply removed.
        var current = s.Store.Current!.GetDocument(main.Element.Id)!;
        var both = await s.Store.SaveAsync(main.Element.Id, BillingStore.Edit(current, n => n["packages"] = new JsonArray(billing, catalog.Element.Id)), current.Hash, ChangeSource.Editor, Ct);
        Assert.Equal(SaveOutcome.Saved, both.Outcome);
        var removed = await s.Store.DeleteAsync(catalog.Element.Id, catalog.Hash, DeleteResolution.RemoveReferences, ChangeSource.Editor, Ct);
        Assert.Equal(SaveOutcome.Saved, removed.Outcome);
        Assert.Equal([billing], s.Store.Current!.Get<Database>(main.Element.Id)!.Packages);
    }

    [Fact]
    public async Task Staged_files_left_by_a_crashed_process_do_not_block_later_saves()
    {
        await using var s = await BillingStore.OpenAsync();
        var customer = s.Doc("entity", "Customer");
        var batchId = Environment.ProcessId.ToString(CultureInfo.InvariantCulture) + "-1"; // the first batch id of a fresh store
        s.Harness.Write("model/entities/.customer.json.mq-" + batchId + ".tmp", "half-written");
        s.Harness.Write("model/entities/.customer.json.mq-" + batchId + ".bak", "old copy");

        var result = await s.Store.SaveAsync(customer.Element.Id, BillingStore.Edit(customer, n => n["displayName"] = "Client"), customer.Hash, ChangeSource.Editor, Ct);

        Assert.Equal(SaveOutcome.Saved, result.Outcome);
        Assert.Equal("Client", result.Current!.Element.DisplayName);
        Assert.Empty(StagedLeftovers(s));
    }

    [Fact]
    public async Task A_database_move_does_not_carry_staged_leftovers()
    {
        await using var s = await BillingStore.OpenAsync();
        var main = s.Doc("database", "main");
        s.Harness.Write("model/databases/main/views/.outstanding-invoices.json.mq-1-1.bak", "leftover");
        s.Harness.Write("model/databases/main/views/.keep", "a hidden file of the user's");

        var renamed = await s.Store.SaveAsync(main.Element.Id, BillingStore.Edit(main, n => n["name"] = "primary"), main.Hash, ChangeSource.Editor, Ct);

        Assert.Equal(SaveOutcome.Saved, renamed.Outcome);
        Assert.Empty(StagedLeftovers(s));
        Assert.Equal("a hidden file of the user's", s.Harness.Read("model/databases/primary/views/.keep"));
        Assert.False(Directory.Exists(s.Harness.Model("model/databases/main")));
    }

    [Fact]
    public async Task A_disk_edit_indexed_by_a_save_that_then_fails_is_still_published()
    {
        await using var s = await BillingStore.OpenAsync();
        var customer = s.Doc("entity", "Customer");
        var onDisk = TestServices.Json.Write(JsonNode.Parse(BillingStore.Edit(customer, n => n["displayName"] = "Disk"))!, "entity.json", customer.Path);
        s.Harness.Write("model/entities/customer.json", Encoding.UTF8.GetString(onDisk));
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        s.Harness.Validator.Rule = (_, _) =>
        {
            cts.Cancel();
            cts.Token.ThrowIfCancellationRequested();
            return [];
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            s.Store.SaveAsync(customer.Element.Id, BillingStore.Edit(customer, n => n["displayName"] = "Mine"), ContentHash.Of(onDisk), ChangeSource.Editor, cts.Token));

        var published = Assert.Single(s.Notifications);
        Assert.Equal(ChangeSource.Disk, published.Source);
        Assert.Equal([customer.Element.Id], published.Changed.Select(c => c.Id));
        Assert.Equal("Disk", s.Store.Current!.Get<Entity>(customer.Element.Id)!.DisplayName);
        Assert.True((await s.Store.RefreshAsync(["model/entities/customer.json"], Ct)).IsEmpty); // already indexed: nothing more to publish
    }

    [Fact]
    public async Task A_save_cancelled_during_validation_writes_nothing()
    {
        await using var s = await BillingStore.OpenAsync();
        var customer = s.Doc("entity", "Customer");
        var before = s.Files();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        s.Harness.Validator.Rule = (_, _) =>
        {
            cts.Cancel();
            return [];
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            s.Store.SaveAsync(customer.Element.Id, BillingStore.Edit(customer, n => n["displayName"] = "Client"), customer.Hash, ChangeSource.Editor, cts.Token));

        Assert.NotEmpty(s.Harness.Validator.Calls);
        Assert.Equal(before, s.Files());
        Assert.Same(customer, s.Store.Current!.GetDocument(customer.Element.Id));
        Assert.Empty(s.Notifications);
    }

    [Fact]
    public async Task A_save_cancelled_after_the_path_policy_pass_writes_nothing()
    {
        await using var s = await BillingStore.OpenAsync();
        var customer = s.Doc("entity", "Customer");
        var before = s.Files();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        s.Harness.Policy.Refuse = _ =>
        {
            cts.Cancel();
            return false;
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            s.Store.SaveAsync(customer.Element.Id, BillingStore.Edit(customer, n => n["displayName"] = "Client"), customer.Hash, ChangeSource.Editor, cts.Token));

        Assert.Equal(before, s.Files());
        Assert.Same(customer, s.Store.Current!.GetDocument(customer.Element.Id));
    }

    [Fact]
    public async Task Cancelling_midway_through_staging_removes_every_staged_file()
    {
        using var h = new LoaderHarness();
        h.CopyFixture("models", "billing");
        var before = Directory.EnumerateFiles(h.Repo.ModelRoot, "*", SearchOption.AllDirectories)
            .ToDictionary(f => f, File.ReadAllText, StringComparer.Ordinal).OrderBy(kv => kv.Key, StringComparer.Ordinal).ToList();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var staged = 0;
        var set = new AtomicFileSet(h.Policy, h.Repo.ModelRoot, _ =>
        {
            if (++staged == 2)
                cts.Cancel();
        });
        var writes = new List<(string, byte[])>
        {
            (h.Model("model/entities/customer.json"), "{}"u8.ToArray()),
            (h.Model("model/entities/new-one.json"), "{}"u8.ToArray()),
            (h.Model("model/entities/product.json"), "{}"u8.ToArray()),
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => set.ApplyAsync(writes, [h.Model("model/entities/payment.json")], "t-1", cts.Token));

        Assert.Equal(2, staged);
        var after = Directory.EnumerateFiles(h.Repo.ModelRoot, "*", SearchOption.AllDirectories)
            .ToDictionary(f => f, File.ReadAllText, StringComparer.Ordinal).OrderBy(kv => kv.Key, StringComparer.Ordinal).ToList();
        Assert.Equal(before, after);
    }
}
