using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using Maquettiste.Engine.Hashing;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Snapshots;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Snapshots;

/// <summary>Model snapshots (docs/engineering/snapshots.md): archives, open as of, compare, restore, import.</summary>
public sealed class SnapshotTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_snapshot_holds_the_model_documents_and_the_packs_only_on_request()
    {
        await using var f = await SnapshotFixture.CreateAsync(packs: true);
        f.Repo.WriteFile(".maquettiste/manifest/sql-ddl.json", "{}");
        f.Repo.WriteFile(".maquettiste/snapshots/main.json", "{}");
        f.Repo.WriteFile(".maquettiste/.cache/run.lock", "");
        f.Repo.WriteFile(".maquettiste/model/entities/.customer.json.mq-1-1.tmp", "{}");

        var plain = await f.Library.CreateAsync(new SnapshotCreateRequest("Plain", "First cut", Author: "ada"), Ct);
        f.Clock.Advance(TimeSpan.FromSeconds(1));
        var withPacks = await f.Library.CreateAsync(new SnapshotCreateRequest("With packs", IncludePacks: true), Ct);

        Assert.Equal("plain-20261005-120000", plain.Id);
        Assert.Equal(("Plain", "First cut", "ada", "2026-10-05T12:00:00Z", "user", false), (plain.Name, plain.Description, plain.Author, plain.CreatedUtc, plain.Origin, plain.IncludesPacks));
        Assert.Equal(36, plain.Files);
        Assert.Equal(33, plain.Elements);
        Assert.Equal(8, plain.Kinds["entity"]);
        var names = EntryNames(f.Library, plain.Id);
        Assert.Equal(["snapshot-index.json", "snapshot.json"], names[^2..]);
        Assert.Contains("maquettiste.json", names);
        Assert.Contains("model/entities/invoice.md", names);
        Assert.Contains("extensions/retention.json", names);
        Assert.DoesNotContain(names, n => n.StartsWith("templates/", StringComparison.Ordinal) || n.StartsWith("manifest/", StringComparison.Ordinal)
            || n.StartsWith("snapshots/", StringComparison.Ordinal) || n.Contains("/.", StringComparison.Ordinal) || n.StartsWith('.'));
        Assert.Equal(names[..^2].Order(StringComparer.Ordinal), names[..^2]);

        Assert.True(withPacks.IncludesPacks);
        Assert.Contains(EntryNames(f.Library, withPacks.Id), n => n == "templates/sql-ddl/pack.json");
        Assert.Equal(plain.ModelHash, withPacks.ModelHash);
        Assert.Equal([withPacks.Id, plain.Id], (await f.Library.ListAsync(Ct)).Select(s => s.Id));
        Assert.True(File.Exists(Path.Combine(f.Repo.ModelRoot, "model-snapshots", plain.Id + ".zip")));
    }

    [Fact]
    public async Task The_same_model_at_the_same_time_gives_the_same_bytes()
    {
        await using var a = await SnapshotFixture.CreateAsync();
        await using var b = await SnapshotFixture.CreateAsync();

        var first = await a.Library.CreateAsync(new SnapshotCreateRequest("Billing"), Ct);
        var second = await b.Library.CreateAsync(new SnapshotCreateRequest("Billing"), Ct);
        var bytes = File.ReadAllBytes(Path.Combine(a.Library.Folder, first.Id + ".zip"));
        Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(b.Library.Folder, second.Id + ".zip")));

        // Twice in one repo: a new id, the same documents and the same model hash.
        File.Delete(Path.Combine(a.Library.Folder, first.Id + ".zip"));
        var again = await a.Library.CreateAsync(new SnapshotCreateRequest("Billing"), Ct);
        Assert.Equal(first.Id, again.Id);
        Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(a.Library.Folder, again.Id + ".zip")));
        var third = await a.Library.CreateAsync(new SnapshotCreateRequest("Billing"), Ct);
        Assert.Equal(first.Id + "-2", third.Id);
        Assert.Equal(first.ModelHash, third.ModelHash);

        using var zip = ZipFile.OpenRead(Path.Combine(a.Library.Folder, first.Id + ".zip"));
        Assert.All(zip.Entries, e => Assert.Equal(new DateTime(1980, 1, 1), e.LastWriteTime.DateTime));
    }

    [Fact]
    public async Task A_snapshot_opened_as_of_serves_every_document_as_it_was_and_refuses_writes()
    {
        await using var f = await SnapshotFixture.CreateAsync();
        var snapshot = await f.Library.CreateAsync(new SnapshotCreateRequest("Before"), Ct);
        var live = await f.Store.GetSnapshotAsync(Ct);
        var before = live.Documents.ToDictionary(d => d.Element.Id, d => (d.Path, d.Hash, d.Json.GetRawText()));
        var settingsBefore = File.ReadAllBytes(Path.Combine(f.Repo.ModelRoot, "maquettiste.json"));
        await f.RenameAsync("Customer", "Client");

        var store = await f.Library.OpenAsync(snapshot.Id, Ct);

        Assert.NotNull(store);
        Assert.True(store.IsReadOnly);
        var model = await store.GetSnapshotAsync(Ct);
        Assert.Equal(before.Count, model.Documents.Count);
        foreach (var document in model.Documents)
            Assert.Equal(before[document.Element.Id], (document.Path, document.Hash, document.Json.GetRawText()));
        Assert.Contains(model.Summaries(), s => s.Name == "Customer");
        Assert.DoesNotContain(model.Summaries(), s => s.Name == "Client");
        using (var documents = new ZipDocumentStore(Path.Combine(f.Library.Folder, snapshot.Id + ".zip")))
        {
            foreach (var path in Directory.EnumerateFiles(f.Repo.ModelRoot, "*", SearchOption.AllDirectories)
                .Select(p => Path.GetRelativePath(f.Repo.ModelRoot, p).Replace('\\', '/')).Where(SnapshotLayout.IsContentPath))
            {
                if (path is "model/entities/client.json")
                    continue;
                var expected = path == "maquettiste.json" ? settingsBefore : File.ReadAllBytes(Path.Combine(f.Repo.ModelRoot, path));
                Assert.Equal(expected, await documents.ReadAsync(path, Ct));
            }

            Assert.NotNull(await documents.ReadAsync("model/entities/customer.json", Ct));
        }

        var settings = await store.GetSettingsAsync(Ct);
        Assert.Equal(ContentHash.Of(settingsBefore), settings.Hash);
        var invoice = model.Documents.First(d => d.Element.Name == "Invoice");
        var save = await store.SaveAsync(invoice.Element.Id, Encoding.UTF8.GetBytes(invoice.Json.GetRawText()), invoice.Hash, ChangeSource.Editor, Ct);
        Assert.Equal(SaveOutcome.Invalid, save.Outcome);
        Assert.Equal("MQ6029", Assert.Single(save.Diagnostics).Rule);
        var settingsSave = await store.SaveSettingsAsync(settingsBefore, settings.Hash, ChangeSource.Editor, Ct);
        Assert.Equal(SaveOutcome.Invalid, settingsSave.Outcome);
        Assert.Equal("MQ6029", settingsSave.Diagnostics[0].Rule);
        Assert.Same(store, await f.Library.OpenAsync(snapshot.Id, Ct));
        Assert.Null(await f.Library.OpenAsync("no-such-snapshot-20260101-000000", Ct));
        Assert.Null(await f.Library.OpenAsync("../escape", Ct));
    }

    [Fact]
    public async Task Compare_reports_elements_added_removed_changed_and_renamed_by_kind_and_files_by_path()
    {
        await using var f = await SnapshotFixture.CreateAsync();
        var before = await f.Library.CreateAsync(new SnapshotCreateRequest("Before"), Ct);
        var model = await f.Store.GetSnapshotAsync(Ct);
        var productId = model.Summaries().Single(s => s.Name == "Product").Id;
        await f.RenameAsync("Customer", "Client");
        var payment = model.Documents.First(d => d.Element.Name == "Payment");
        var edited = await f.Store.SaveAsync(payment.Element.Id, Edit(payment, n => n["displayName"] = "Settlement"), payment.Hash, ChangeSource.Editor, Ct);
        Assert.Equal(SaveOutcome.Saved, edited.Outcome);
        var product = (await f.Store.GetSnapshotAsync(Ct)).GetDocument(productId)!;
        var deleted = await f.Store.DeleteAsync(productId, product.Hash, DeleteResolution.DeleteDependents, ChangeSource.Editor, Ct);
        Assert.Equal(SaveOutcome.Saved, deleted.Outcome);
        var created = await f.Store.CreateAsync(Encoding.UTF8.GetBytes("""{"kind":"package","name":"Pricing"}"""), ChangeSource.Editor, Ct);
        Assert.Equal(SaveOutcome.Saved, created.Outcome);
        f.Repo.WriteFile(".maquettiste/model/entities/invoice.md", "Changed notes.\n");

        var working = await f.Library.CompareAsync(before.Id, SnapshotLibrary.Working, 0, 500, Ct);

        Assert.NotNull(working);
        Assert.Equal((before.Id, "working"), (working.From, working.To));
        Assert.Contains(working.Elements, e => e is { Change: "added", Kind: "package", Name: "Pricing" });
        Assert.Contains(working.Elements, e => e.Id == productId && e is { Change: "removed", Kind: "entity", Name: "Product" });
        var client = Assert.Single(working.Elements, e => e.Name == "Client");
        Assert.Equal(("changed", "Customer", "model/entities/customer.json", "model/entities/client.json"), (client.Change, client.PreviousName, client.PreviousPath, client.Path));
        Assert.Contains(working.Elements, e => e is { Name: "Payment", Change: "changed", PreviousName: null, PreviousPath: null });
        Assert.Equal(working.Elements.Count(e => e.Change == "added"), working.Added);
        Assert.Equal(working.Kinds.Sum(k => k.Added + k.Removed + k.Changed), working.Elements.Count);
        Assert.Equal(new SnapshotFileChange("model/entities/invoice.md", "changed"), Assert.Single(working.Files));
        Assert.False(working.PacksCompared);
        Assert.Null(working.Next);

        // Between two snapshots: the same answer; paged.
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        var after = await f.Library.CreateAsync(new SnapshotCreateRequest("After"), Ct);
        var between = await f.Library.CompareAsync(before.Id, after.Id, 0, 500, Ct);
        Assert.Equal(working.Elements, between!.Elements);
        Assert.Equal(working.Files, between.Files);
        var page = await f.Library.CompareAsync(before.Id, after.Id, 1, 2, Ct);
        Assert.Equal(between.Elements.Skip(1).Take(2), page!.Elements);
        Assert.Equal(3, page.Next);
        var same = await f.Library.CompareAsync(after.Id, SnapshotLibrary.Working, 0, 10, Ct);
        Assert.Equal((0, 0, 0, 0), (same!.Added, same.Removed, same.Changed, same.Files.Count));
        Assert.Null(await f.Library.CompareAsync("missing-20260101-000000", after.Id, 0, 10, Ct));

        // One element's detail: the two documents and the fields that differ.
        var detail = await f.Library.CompareElementAsync(before.Id, SnapshotLibrary.Working, client.Id, Ct);
        Assert.NotNull(detail);
        Assert.Equal(("changed", "model/entities/customer.json", "model/entities/client.json"), (detail.Change, detail.FromPath, detail.ToPath));
        Assert.Equal("Customer", detail.Before!.Value.GetProperty("name").GetString());
        Assert.Equal("Client", detail.After!.Value.GetProperty("name").GetString());
        var name = Assert.Single(detail.Fields, x => x.Pointer == "/name");
        Assert.Equal(("changed", "Customer", "Client"), (name.Change, name.Before!.Value.GetString(), name.After!.Value.GetString()));
        var removed = await f.Library.CompareElementAsync(before.Id, after.Id, productId, Ct);
        Assert.Equal(("removed", (string?)null), (removed!.Change, removed.ToPath));
        Assert.Null(removed.After);
        var payDetail = await f.Library.CompareElementAsync(before.Id, after.Id, payment.Element.Id, Ct);
        Assert.Equal(("/displayName", "added"), (Assert.Single(payDetail!.Fields).Pointer, payDetail.Fields[0].Change));
    }

    [Fact]
    public async Task Restore_takes_a_safety_snapshot_replaces_the_working_model_and_publishes_one_change()
    {
        await using var f = await SnapshotFixture.CreateAsync();
        var before = await f.Library.CreateAsync(new SnapshotCreateRequest("Before"), Ct);
        await f.RenameAsync("Customer", "Client");
        f.Repo.WriteFile(".maquettiste/model/entities/stray.md", "not referenced\n");
        var version = (await f.Store.GetSnapshotAsync(Ct)).Version;
        var published = new List<ChangeSet>();
        using var subscription = f.Store.OnChanged((c, _) => { published.Add(c); return ValueTask.CompletedTask; });
        f.Clock.Advance(TimeSpan.FromMinutes(5));

        var result = await f.Library.RestoreAsync(before.Id, new SnapshotRestoreRequest(Author: "ada"), Ct);

        Assert.Equal(SnapshotRestoreOutcome.Restored, result.Outcome);
        Assert.Equal("before-restore-20261005-120500", result.Safety!.Id);
        Assert.Equal(("before-restore", "ada"), (result.Safety.Origin, result.Safety.Author));
        Assert.Contains(result.Safety.Id, result.Undo, StringComparison.Ordinal);
        Assert.Equal((1, 2), (result.Written, result.Deleted)); // customer.json back; client.json and stray.md gone
        Assert.False(File.Exists(Path.Combine(f.Repo.ModelRoot, "model", "entities", "client.json")));
        var same = await f.Library.CompareAsync(before.Id, SnapshotLibrary.Working, 0, 10, Ct);
        Assert.Equal((0, 0, 0, 0), (same!.Added, same.Removed, same.Changed, same.Files.Count));
        var model = f.Store.Current!;
        Assert.True(model.Version > version);
        Assert.Contains(model.Summaries(), s => s.Name == "Customer");
        var change = Assert.Single(published);
        Assert.Contains(change.Changed, c => c.Path == ".maquettiste/model/entities/customer.json");
        Assert.Equal(1, result.ElementsChanged);

        // Undo: restore the safety snapshot.
        var undo = await f.Library.RestoreAsync(result.Safety.Id, new SnapshotRestoreRequest(), Ct);
        Assert.Equal(SnapshotRestoreOutcome.Restored, undo.Outcome);
        Assert.Contains(f.Store.Current!.Summaries(), s => s.Name == "Client");
        Assert.True(File.Exists(Path.Combine(f.Repo.ModelRoot, "model", "entities", "stray.md")));
    }

    [Fact]
    public async Task Restore_is_refused_while_a_generation_run_holds_the_run_lock()
    {
        await using var f = await SnapshotFixture.CreateAsync();
        var before = await f.Library.CreateAsync(new SnapshotCreateRequest("Before"), Ct);
        await f.RenameAsync("Customer", "Client");

        await using (var held = await f.Store.Services.RunLock.AcquireAsync(false, Ct))
        {
            Assert.NotNull(held);
            var refused = await f.Library.RestoreAsync(before.Id, new SnapshotRestoreRequest(), Ct);
            Assert.Equal(SnapshotRestoreOutcome.Locked, refused.Outcome);
            Assert.Null(refused.Safety);
        }

        Assert.Single(await f.Library.ListAsync(Ct));
        Assert.True(File.Exists(Path.Combine(f.Repo.ModelRoot, "model", "entities", "client.json")));
        Assert.Equal(SnapshotRestoreOutcome.NotFound, (await f.Library.RestoreAsync("missing-20260101-000000", new SnapshotRestoreRequest(), Ct)).Outcome);
        Assert.Equal(SnapshotRestoreOutcome.Restored, (await f.Library.RestoreAsync(before.Id, new SnapshotRestoreRequest(), Ct)).Outcome);
    }

    [Fact]
    public async Task Packs_are_restored_only_when_the_snapshot_holds_them_and_the_caller_asks()
    {
        await using var f = await SnapshotFixture.CreateAsync(packs: true);
        var withPacks = await f.Library.CreateAsync(new SnapshotCreateRequest("Packs", IncludePacks: true), Ct);
        var pack = Path.Combine(f.Repo.ModelRoot, "templates", "sql-ddl", "pack.json");
        var original = File.ReadAllBytes(pack);
        File.WriteAllText(pack, "{}\n");
        f.Clock.Advance(TimeSpan.FromSeconds(1));

        var modelOnly = await f.Library.RestoreAsync(withPacks.Id, new SnapshotRestoreRequest(), Ct);
        Assert.False(modelOnly.PacksRestored);
        Assert.Equal("{}\n", File.ReadAllText(pack));
        Assert.False(modelOnly.Safety!.IncludesPacks);

        f.Clock.Advance(TimeSpan.FromSeconds(1));
        var full = await f.Library.RestoreAsync(withPacks.Id, new SnapshotRestoreRequest(IncludePacks: true), Ct);
        Assert.True(full.PacksRestored);
        Assert.True(full.Safety!.IncludesPacks);
        Assert.Equal(original, File.ReadAllBytes(pack));
    }

    [Fact]
    public async Task Rename_publish_and_delete_keep_the_id_and_rewrite_only_the_metadata()
    {
        await using var f = await SnapshotFixture.CreateAsync();
        var snapshot = await f.Library.CreateAsync(new SnapshotCreateRequest("First"), Ct);
        var file = Path.Combine(f.Library.Folder, snapshot.Id + ".zip");
        var index = ReadEntry(file, SnapshotLayout.IndexEntry);

        var updated = await f.Library.UpdateAsync(snapshot.Id, new SnapshotUpdate("Reviewed", "Ready", true), Ct);

        Assert.Equal((snapshot.Id, "Reviewed", "Ready", true), (updated!.Id, updated.Name, updated.Description, updated.Published));
        Assert.Equal(snapshot.ModelHash, updated.ModelHash);
        Assert.Equal(index, ReadEntry(file, SnapshotLayout.IndexEntry));
        Assert.Equal("snapshot.json", EntryNames(f.Library, snapshot.Id)[^1]);
        Assert.Equal(updated, await f.Library.GetAsync(snapshot.Id, Ct));
        Assert.Null(await f.Library.UpdateAsync("missing-20260101-000000", new SnapshotUpdate(Published: true), Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => f.Library.UpdateAsync(snapshot.Id, new SnapshotUpdate(" "), Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => f.Library.CreateAsync(new SnapshotCreateRequest(""), Ct));

        Assert.True(await f.Library.DeleteAsync(snapshot.Id, Ct));
        Assert.False(await f.Library.DeleteAsync(snapshot.Id, Ct));
        Assert.Empty(await f.Library.ListAsync(Ct));
    }

    [Fact]
    public async Task An_exported_snapshot_imports_under_a_new_id_with_the_same_documents()
    {
        await using var f = await SnapshotFixture.CreateAsync();
        var snapshot = await f.Library.CreateAsync(new SnapshotCreateRequest("Shared", "To mail"), Ct);
        byte[] exported;
        await using (var stream = f.Library.OpenRead(snapshot.Id)!)
        {
            using var copy = new MemoryStream();
            await stream.CopyToAsync(copy, Ct);
            exported = copy.ToArray();
        }

        var imported = await f.Library.ImportAsync(new MemoryStream(exported), Ct);

        Assert.Empty(imported.Diagnostics);
        Assert.Equal(snapshot.Id + "-2", imported.Snapshot!.Id);
        Assert.Equal((snapshot.Name, snapshot.Description, snapshot.ModelHash, snapshot.Files), (imported.Snapshot.Name, imported.Snapshot.Description, imported.Snapshot.ModelHash, imported.Snapshot.Files));
        Assert.Equal(exported, File.ReadAllBytes(Path.Combine(f.Library.Folder, imported.Snapshot.Id + ".zip")));
        Assert.Empty(Directory.EnumerateFiles(f.Repo.CacheDirectory, "*.zip", SearchOption.AllDirectories));
        Assert.Empty(Directory.EnumerateFiles(f.Library.Folder, ".*"));

        // Another tool's zip of the same documents (any order, folder entries, other times) is rewritten the engine's way.
        var foreign = Rezip(exported, entries => [.. entries.AsEnumerable().Reverse().Prepend(("model/", []))], skipIndex: true);
        var normalized = await f.Library.ImportAsync(new MemoryStream(foreign), Ct);
        Assert.Empty(normalized.Diagnostics);
        Assert.Equal(exported, File.ReadAllBytes(Path.Combine(f.Library.Folder, normalized.Snapshot!.Id + ".zip")));
    }

    [Theory]
    [InlineData("../evil.json", "MQ1011")]
    [InlineData("/model/entities/abs.json", "MQ1011")]
    [InlineData("model/../../evil.json", "MQ1011")]
    [InlineData("model\\entities\\win.json", "MQ1011")]
    [InlineData("manifest/sql-ddl.json", "MQ1011")]
    [InlineData("model/.hidden/x.json", "MQ1011")]
    [InlineData("MODEL/entities/customer.json", "MQ1011")]
    public async Task Import_refuses_paths_outside_the_snapshot_layout(string path, string rule)
    {
        await using var f = await SnapshotFixture.CreateAsync();
        var exported = await f.ExportAsync(await f.Library.CreateAsync(new SnapshotCreateRequest("S"), Ct));

        var result = await f.Library.ImportAsync(new MemoryStream(Rezip(exported, entries => [.. entries, (path, "{}"u8.ToArray())])), Ct);

        Assert.Null(result.Snapshot);
        Assert.Equal(rule, Assert.Single(result.Diagnostics).Rule);
        Assert.Single(await f.Library.ListAsync(Ct));
        Assert.False(File.Exists(Path.Combine(f.Repo.RepoRoot, "evil.json")));
    }

    [Fact]
    public async Task Import_refuses_duplicates_sizes_bad_metadata_and_documents_that_do_not_parse_or_are_not_canonical()
    {
        await using var f = await SnapshotFixture.CreateAsync();
        var exported = await f.ExportAsync(await f.Library.CreateAsync(new SnapshotCreateRequest("S"), Ct));

        async Task<string> Refused(byte[] archive, SnapshotLibrary? library = null)
        {
            var result = await (library ?? f.Library).ImportAsync(new MemoryStream(archive), Ct);
            Assert.Null(result.Snapshot);
            return string.Join(",", result.Diagnostics.Select(d => d.Rule + (result.TooLarge ? "+large" : "")));
        }

        Assert.Equal("MQ1011", await Refused("not a zip"u8.ToArray()));
        Assert.Equal("MQ1011", await Refused(Rezip(exported, e => [.. e, ("model/entities/customer.json", "{}"u8.ToArray())])));
        Assert.Equal("MQ1011", await Refused(Rezip(exported, e => [.. e.Where(x => x.Name != "snapshot.json")])));
        Assert.Equal("MQ1011", await Refused(Rezip(exported, e => [.. e.Select(x => x.Name == "snapshot.json" ? (x.Name, Patch(x.Bytes, "\"format\": 1", "\"format\": 2")) : x)])));
        Assert.Equal("MQ1007", await Refused(Rezip(exported, e => [.. e.Select(x => x.Name == "snapshot.json" ? (x.Name, Patch(x.Bytes, "\"modelFormat\": 1", "\"modelFormat\": 99")) : x)])));
        Assert.Equal("MQ1001", await Refused(Rezip(exported, e => [.. e.Select(x => x.Name == "model/entities/payment.json" ? (x.Name, "{ not json"u8.ToArray()) : x)])));
        Assert.Equal("MQ1003", await Refused(Rezip(exported, e => [.. e.Select(x => x.Name == "model/entities/payment.json"
            ? (x.Name, Encoding.UTF8.GetBytes(JsonNode.Parse(x.Bytes)!.ToJsonString())) : x)])));
        var small = new SnapshotLibrary(f.Store) { Limits = new SnapshotLimits(10_000, 1_000_000, 10_000_000, 1000, 200) };
        Assert.Equal("MQ1011+large", await Refused(exported, small));
        var fewEntries = new SnapshotLibrary(f.Store) { Limits = new SnapshotLimits(10_000_000, 1_000_000, 10_000_000, 10, 200) };
        Assert.Equal("MQ1011+large", await Refused(exported, fewEntries));
        var tinyEntries = new SnapshotLibrary(f.Store) { Limits = new SnapshotLimits(10_000_000, 100, 10_000_000, 1000, 200) };
        Assert.Equal("MQ1011+large", await Refused(exported, tinyEntries));
        var bomb = Rezip(exported, e => [.. e, ("model/entities/bomb.md", new byte[3 * 1024 * 1024])]);
        Assert.Equal("MQ1011+large", await Refused(bomb));

        Assert.Single(await f.Library.ListAsync(Ct));
        Assert.Empty(Directory.EnumerateFiles(f.Library.Folder, ".*"));
    }

    private static byte[] Edit(ElementDocument document, Action<JsonObject> change)
    {
        var node = JsonNode.Parse(document.Json.GetRawText())!.AsObject();
        change(node);
        return Encoding.UTF8.GetBytes(node.ToJsonString());
    }

    private static byte[] Patch(byte[] bytes, string from, string to)
    {
        var text = Encoding.UTF8.GetString(bytes);
        Assert.Contains(from, text, StringComparison.Ordinal);
        return Encoding.UTF8.GetBytes(text.Replace(from, to, StringComparison.Ordinal));
    }

    private static List<string> EntryNames(SnapshotLibrary library, string id)
    {
        using var zip = ZipFile.OpenRead(Path.Combine(library.Folder, id + ".zip"));
        return [.. zip.Entries.Select(e => e.FullName)];
    }

    private static byte[] ReadEntry(string file, string name)
    {
        using var zip = ZipFile.OpenRead(file);
        using var stream = zip.GetEntry(name)!.Open();
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    /// <summary>Rewrites an archive as another tool would: the given entries, the current time, optimal compression.</summary>
    private static byte[] Rezip(byte[] archive, Func<List<(string Name, byte[] Bytes)>, List<(string Name, byte[] Bytes)>> change, bool skipIndex = false)
    {
        var entries = new List<(string Name, byte[] Bytes)>();
        using (var zip = new ZipArchive(new MemoryStream(archive), ZipArchiveMode.Read))
        {
            foreach (var entry in zip.Entries)
            {
                if (skipIndex && entry.FullName == SnapshotLayout.IndexEntry)
                    continue;
                using var stream = entry.Open();
                using var copy = new MemoryStream();
                stream.CopyTo(copy);
                entries.Add((entry.FullName, copy.ToArray()));
            }
        }

        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, bytes) in change(entries))
            {
                var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
                using var stream = entry.Open();
                stream.Write(bytes);
            }
        }

        return output.ToArray();
    }
}

/// <summary>A temp copy of the billing fixture (with the sql-ddl pack on request), a real store and its snapshot library at a fixed time.</summary>
internal sealed class SnapshotFixture : IAsyncDisposable
{
    private SnapshotFixture(TempRepo repo, FixedClock clock, ModelStore store)
    {
        Repo = repo;
        Clock = clock;
        Store = store;
        Library = new SnapshotLibrary(store);
    }

    public TempRepo Repo { get; }

    public FixedClock Clock { get; }

    public ModelStore Store { get; }

    public SnapshotLibrary Library { get; }

    public static async Task<SnapshotFixture> CreateAsync(bool packs = false)
    {
        var repo = new TempRepo();
        Copy(Fixtures.Path("models", "billing", ".maquettiste"), repo.ModelRoot);
        if (packs)
            Copy(Path.Combine(Fixtures.RepoRoot, "packs", "sql-ddl"), Path.Combine(repo.ModelRoot, "templates", "sql-ddl"));
        var clock = new FixedClock(new DateTimeOffset(2026, 10, 5, 12, 0, 0, TimeSpan.Zero));
        var store = new ModelStore(repo.Options with { TimeProvider = clock, IdGenerator = null });
        await store.LoadAsync(TestContext.Current.CancellationToken);
        return new SnapshotFixture(repo, clock, store);
    }

    public async Task RenameAsync(string from, string to)
    {
        var model = await Store.GetSnapshotAsync(TestContext.Current.CancellationToken);
        var document = model.Documents.Single(d => d.Element.Name == from);
        var node = JsonNode.Parse(document.Json.GetRawText())!.AsObject();
        node["name"] = to;
        var result = await Store.SaveAsync(document.Element.Id, Encoding.UTF8.GetBytes(node.ToJsonString()), document.Hash, ChangeSource.Editor, TestContext.Current.CancellationToken);
        Assert.Equal(SaveOutcome.Saved, result.Outcome);
    }

    public async Task<byte[]> ExportAsync(SnapshotInfo snapshot)
    {
        await using var stream = Library.OpenRead(snapshot.Id)!;
        using var copy = new MemoryStream();
        await stream.CopyToAsync(copy, TestContext.Current.CancellationToken);
        return copy.ToArray();
    }

    public async ValueTask DisposeAsync()
    {
        await Store.DisposeAsync();
        Repo.Dispose();
    }

    private static void Copy(string source, string target)
    {
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var to = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(file, to, overwrite: true);
        }
    }
}

/// <summary>A clock that moves only when told to.</summary>
internal sealed class FixedClock(DateTimeOffset now) : TimeProvider
{
    private DateTimeOffset _now = now;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}
