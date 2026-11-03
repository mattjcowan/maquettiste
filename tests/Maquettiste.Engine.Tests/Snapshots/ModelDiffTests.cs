using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Maquettiste.Engine.Model;

namespace Maquettiste.Engine.Tests.Snapshots;

/// <summary>
/// The model diff (next-phase-2-design.md section 6.2): sides that are snapshots, folders or the working model, rows grouped by package
/// and facts by database, and field changes that name their sub-element.
/// </summary>
public sealed class ModelDiffTests
{
    internal const string Billing = "01J92P0V01KDRN8GX5PGYCNKSX";
    internal const string Catalog = "01J92P0V025DRFTXKMS240G2NG";
    internal const string Invoice = "01J92P0V0FJ23CGSNKM7P1W5V7";
    internal const string InvoiceNumber = "01J92P0V0R3VSP7D5238DTNZX1";
    internal const string Customer = "01J92P0V0ETQKXXP951CMMNHH3";
    internal const string Main = "01J92P0V1QRN2181XM2ZWE02W4";

    private static readonly SnapshotCompareOptions Review = new() { ByPackage = true, FieldChanges = true };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_folder_side_compares_like_the_snapshot_it_copies()
    {
        await using var f = await SnapshotFixture.CreateAsync();
        var snapshot = await f.Library.CreateAsync(new SnapshotCreateRequest("Before review"), Ct);
        var folder = ModelBaselineTests.CopyBilling(Path.Combine(f.Repo.Root, "base"));
        await ReviewChangeAsync(f.Store);

        var fromSnapshot = await f.Library.CompareAsync(snapshot.Id, SnapshotLibrary.Working, 0, 100, Review, Ct);
        var fromFolder = await f.Library.CompareAsync(folder, SnapshotLibrary.Working, 0, 100, Folders(f), Ct);

        Assert.NotNull(fromSnapshot);
        Assert.NotNull(fromFolder);
        Assert.Equal(folder, fromFolder.From);
        Assert.False(fromFolder.PacksCompared);
        Assert.Equal(Json(fromSnapshot with { From = "" }), Json(fromFolder with { From = "" }));
        Assert.Equal(3, fromFolder.Elements.Count);
    }

    [Fact]
    public async Task Two_folders_compare_without_the_working_model()
    {
        await using var f = await SnapshotFixture.CreateAsync();
        var before = ModelBaselineTests.CopyBilling(Path.Combine(f.Repo.Root, "before"));
        var after = ModelBaselineTests.CopyBilling(Path.Combine(f.Repo.Root, "after"));
        Edit(after, "model/entities/customer.json", "\"name\": \"Customer\"", "\"name\": \"Client\"");

        var comparison = await f.Library.CompareAsync("before", "./after", 0, 100, Folders(f), Ct);

        Assert.NotNull(comparison);
        var row = Assert.Single(comparison.Elements);
        Assert.Equal((Customer, "changed", "Client", "Customer", Billing), (row.Id, row.Change, row.Name, row.PreviousName, row.Package));
        var field = Assert.Single(row.Fields!);
        Assert.Equal(("/name", "changed", (string?)null), (field.Pointer, field.Change, field.SubId));
        Assert.Equal(("before", "./after"), (comparison.From, comparison.To));
        var unchanged = await f.Library.CompareAsync(SnapshotLibrary.Working, before, 0, 100, Folders(f), Ct);
        Assert.Empty(unchanged!.Elements); // the working model is the copy before
    }

    [Fact]
    public async Task Elements_group_by_package_and_a_package_by_its_parent()
    {
        await using var f = await SnapshotFixture.CreateAsync();
        var snapshot = await f.Library.CreateAsync(new SnapshotCreateRequest("Base"), Ct);
        Edit(f.Repo.RepoRoot, "model/packages/catalog.json", "What we sell.", "What we sell and rent.");
        Edit(f.Repo.RepoRoot, "model/entities/product.json", "\"name\": \"Product\"", "\"name\": \"Article\"");
        Edit(f.Repo.RepoRoot, "model/entities/customer.json", "\"name\": \"Customer\"", "\"name\": \"Client\"");

        var comparison = (await f.Library.CompareAsync(snapshot.Id, SnapshotLibrary.Working, 0, 100, Review, Ct))!;

        Assert.Equal(["Billing", "Billing.Catalog"], comparison.Packages!.Select(p => p.Name));
        Assert.Equal([Billing, Catalog], comparison.Packages!.Select(p => p.Id));
        Assert.Equal([(2, Billing), (1, Catalog)], comparison.Packages!.Select(p => (p.Changed, p.Id!)));
        Assert.Equal(["entity Client", "package Catalog", "entity Article"], comparison.Elements.Select(e => e.Kind + " " + e.Name));
        Assert.Equal([Billing, Billing, Catalog], comparison.Elements.Select(e => e.Package));
        Assert.Equal(["entity", "package"], comparison.Kinds.Select(k => k.Kind)); // the kind counts stay as before
    }

    [Fact]
    public async Task Field_changes_name_their_sub_element_and_the_pointer_inside_it()
    {
        await using var f = await SnapshotFixture.CreateAsync();
        var snapshot = await f.Library.CreateAsync(new SnapshotCreateRequest("Base"), Ct);
        await ReviewChangeAsync(f.Store);

        var comparison = (await f.Library.CompareAsync(snapshot.Id, SnapshotLibrary.Working, 0, 100, Review, Ct))!;

        var invoice = Assert.Single(comparison.Elements, e => e.Id == Invoice);
        var field = Assert.Single(invoice.Fields!);
        Assert.Equal(("/attributes/1/name", "changed", "\"number\"", "\"code\""), (field.Pointer, field.Change, field.Before!.Value.GetRawText(), field.After!.Value.GetRawText()));
        Assert.Equal((InvoiceNumber, "attribute", "code", "/name"), (field.SubId, field.SubKind, field.SubName, field.SubPointer));
        Assert.False(invoice.FieldsTruncated);
        Assert.All(comparison.Elements.Where(e => e.Change != "changed"), e => Assert.Null(e.Fields));

        // The one-element detail names the same sub-element, with or without options.
        var detail = (await f.Library.CompareElementAsync(snapshot.Id, SnapshotLibrary.Working, Invoice, Ct))!;
        var same = Assert.Single(detail.Fields);
        Assert.Equal((InvoiceNumber, "attribute", "code", "/name"), (same.SubId, same.SubKind, same.SubName, same.SubPointer));

        // A whole sub-element added is its own owner, with an empty pointer inside it.
        var model = await f.Store.GetSnapshotAsync(Ct);
        var document = model.GetDocument(Invoice)!;
        var node = JsonNode.Parse(document.Json.GetRawText())!.AsObject();
        node["attributes"]!.AsArray().Add(new JsonObject { ["id"] = "01K9DXFF000000000000000001", ["name"] = "dueOn", ["type"] = "date" });
        Assert.Equal(SaveOutcome.Saved, (await f.Store.SaveAsync(Invoice, Encoding.UTF8.GetBytes(node.ToJsonString()), document.Hash, ChangeSource.Editor, Ct)).Outcome);
        var added = (await f.Library.CompareElementAsync(snapshot.Id, SnapshotLibrary.Working, Invoice, Review, Ct))!.Fields.Single(x => x.Change == "added");
        Assert.Equal(("01K9DXFF000000000000000001", "attribute", "dueOn", ""), (added.SubId, added.SubKind, added.SubName, added.SubPointer));
    }

    [Fact]
    public async Task Migration_facts_group_by_their_database()
    {
        await using var f = await SnapshotFixture.CreateAsync();
        var snapshot = await f.Library.CreateAsync(new SnapshotCreateRequest("Base"), Ct);
        await ReviewChangeAsync(f.Store);

        var comparison = (await f.Library.CompareAsync(snapshot.Id, SnapshotLibrary.Working, 0, 100, Review, Ct))!;

        Assert.Equal(["Billing", "migrations of main"], comparison.Packages!.Select(p => p.Name));
        Assert.Equal([Billing, null], comparison.Packages!.Select(p => p.Id));
        var fact = comparison.Elements[^1];
        Assert.Equal(("migration", "added", Main, (string?)null), (fact.Kind, fact.Change, fact.Database, fact.Package));
        Assert.Equal((2, 0, 1), (comparison.Added, comparison.Removed, comparison.Changed));
    }

    [Fact]
    public async Task A_fact_row_carries_its_label()
    {
        await using var f = await SnapshotFixture.CreateAsync();
        var snapshot = await f.Library.CreateAsync(new SnapshotCreateRequest("Base"), Ct);
        await ReviewChangeAsync(f.Store);

        var comparison = (await f.Library.CompareAsync(snapshot.Id, SnapshotLibrary.Working, 0, 100, Review, Ct))!;

        Assert.Equal("rename column billing.invoices.number", Assert.Single(comparison.Elements, e => e.Kind == "migration").Label);
        Assert.All(comparison.Elements.Where(e => e.Kind != "migration"), e => Assert.Null(e.Label));
    }

    [Fact]
    public async Task A_package_the_side_lacks_labels_its_group_with_its_id()
    {
        await using var f = await SnapshotFixture.CreateAsync();
        var snapshot = await f.Library.CreateAsync(new SnapshotCreateRequest("Base"), Ct);
        const string Gone = "01K9DXFF0000000000000000GG";
        Edit(f.Repo.RepoRoot, "model/entities/customer.json", $"\"package\": \"{Billing}\"", $"\"package\": \"{Gone}\"");

        var comparison = (await f.Library.CompareAsync(snapshot.Id, SnapshotLibrary.Working, 0, 100, Review, Ct))!;

        var group = Assert.Single(comparison.Packages!);
        Assert.Equal((Gone, Gone, 1), (group.Id, group.Name, group.Changed));
        var field = Assert.Single(Assert.Single(comparison.Elements).Fields!);
        Assert.Equal("/package", field.Pointer);
    }

    [Fact]
    public async Task Paging_follows_the_group_order()
    {
        await using var f = await SnapshotFixture.CreateAsync();
        var snapshot = await f.Library.CreateAsync(new SnapshotCreateRequest("Base"), Ct);
        await ReviewChangeAsync(f.Store);
        Edit(f.Repo.RepoRoot, "model/databases/main/database.json", "\"version\": \"16\"", "\"version\": \"17\"");

        var whole = (await f.Library.CompareAsync(snapshot.Id, SnapshotLibrary.Working, 0, 100, Review, Ct))!;
        var pages = new List<SnapshotElementChange>();
        for (int? offset = 0; offset is { } at;)
        {
            var page = (await f.Library.CompareAsync(snapshot.Id, SnapshotLibrary.Working, at, 1, Review, Ct))!;
            Assert.Equal(Json(whole.Packages), Json(page.Packages));
            pages.AddRange(page.Elements);
            offset = page.Next;
        }

        Assert.Equal(Json(whole.Elements), Json(pages));
        Assert.Equal(["Billing", "(no package)", "migrations of main"], whole.Packages!.Select(p => p.Name));
        Assert.Equal(["entity", "table", "database", "migration"], whole.Elements.Select(e => e.Kind));
    }

    [Fact]
    public async Task A_folder_side_is_refused_unless_folders_are_allowed()
    {
        await using var f = await SnapshotFixture.CreateAsync();
        ModelBaselineTests.CopyBilling(Path.Combine(f.Repo.Root, "base"));

        var refused = await Assert.ThrowsAsync<ArgumentException>(() => f.Library.CompareAsync("./base", SnapshotLibrary.Working, 0, 100, Review, Ct));
        Assert.Equal("'./base' is a folder; this host compares model snapshots and the working model only.", refused.Message);
        var detail = await Assert.ThrowsAsync<ArgumentException>(() => f.Library.CompareElementAsync(SnapshotLibrary.Working, "./base", Invoice, Review, Ct));
        Assert.Contains("'./base' is a folder", detail.Message, StringComparison.Ordinal);
        Assert.Null(await f.Library.CompareAsync("./base", SnapshotLibrary.Working, 0, 100, Ct)); // without options, as before
    }

    [Fact]
    public async Task An_unknown_side_names_itself_in_the_error()
    {
        await using var f = await SnapshotFixture.CreateAsync();

        var unknown = await Assert.ThrowsAsync<ArgumentException>(() => f.Library.CompareAsync("no-such-base", SnapshotLibrary.Working, 0, 100, Folders(f), Ct));
        Assert.Equal("'no-such-base' names neither a model snapshot nor a folder.", unknown.Message);
        var snapshotsOnly = await Assert.ThrowsAsync<ArgumentException>(() => f.Library.CompareAsync(SnapshotLibrary.Working, "no-such-base", 0, 100, Review, Ct));
        Assert.Contains("'no-such-base' names no model snapshot", snapshotsOnly.Message, StringComparison.Ordinal);
        var path = await Assert.ThrowsAsync<ArgumentException>(() => f.Library.CompareAsync("./missing", SnapshotLibrary.Working, 0, 100, Folders(f), Ct));
        Assert.StartsWith("'./missing' names no folder (", path.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_folder_that_is_not_a_project_is_refused()
    {
        await using var f = await SnapshotFixture.CreateAsync();
        Directory.CreateDirectory(Path.Combine(f.Repo.Root, "notes"));

        var refused = await Assert.ThrowsAsync<ArgumentException>(() => f.Library.CompareAsync("./notes", SnapshotLibrary.Working, 0, 100, Folders(f), Ct));

        Assert.StartsWith("'./notes' is not a project:", refused.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Parameter", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_options_the_comparison_answers_as_before()
    {
        await using var f = await SnapshotFixture.CreateAsync();
        var snapshot = await f.Library.CreateAsync(new SnapshotCreateRequest("Base"), Ct);
        await ReviewChangeAsync(f.Store);

        var plain = (await f.Library.CompareAsync(snapshot.Id, SnapshotLibrary.Working, 0, 100, Ct))!;
        var empty = (await f.Library.CompareAsync(snapshot.Id, SnapshotLibrary.Working, 0, 100, new SnapshotCompareOptions(), Ct))!;

        Assert.Equal(Json(plain), Json(empty));
        Assert.Equal(["entity", "migration", "table"], plain.Elements.Select(e => e.Kind));
        var json = Json(plain);
        foreach (var member in new[] { "\"package\"", "\"database\"", "\"label\"", "\"fields\"", "\"fieldsTruncated\"", "\"packages\"" })
            Assert.DoesNotContain(member, json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Fields_are_capped_per_element()
    {
        await using var f = await SnapshotFixture.CreateAsync();
        ModelBaselineTests.CopyBilling(Path.Combine(f.Repo.Root, "before"));
        var after = ModelBaselineTests.CopyBilling(Path.Combine(f.Repo.Root, "after"));
        var path = Path.Combine(after, ".maquettiste", "model", "entities", "invoice.json");
        var node = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        for (var i = 0; i < 60; i++)
            node["attributes"]!.AsArray().Add(new JsonObject { ["id"] = $"01K9CAP{i:D19}", ["name"] = $"extra{i:D2}", ["type"] = "string" });
        File.WriteAllText(path, node.ToJsonString());

        var comparison = (await f.Library.CompareAsync("./before", "./after", 0, 100, Folders(f), Ct))!;

        var invoice = Assert.Single(comparison.Elements);
        Assert.Equal(SnapshotCompareOptions.MaxElementFields, invoice.Fields!.Count);
        Assert.True(invoice.FieldsTruncated);
        Assert.Contains("\"fieldsTruncated\":true", Json(invoice), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_comparison_is_byte_identical_over_two_runs()
    {
        await using var f = await SnapshotFixture.CreateAsync();
        var snapshot = await f.Library.CreateAsync(new SnapshotCreateRequest("Base"), Ct);
        ModelBaselineTests.CopyBilling(Path.Combine(f.Repo.Root, "base"));
        await ReviewChangeAsync(f.Store);

        var first = (await f.Library.CompareAsync(snapshot.Id, SnapshotLibrary.Working, 0, 100, Folders(f), Ct))!;
        var second = (await new SnapshotLibrary(f.Store).CompareAsync(snapshot.Id, SnapshotLibrary.Working, 0, 100, Folders(f), Ct))!;
        var folder = (await f.Library.CompareAsync("./base", SnapshotLibrary.Working, 0, 100, Folders(f), Ct))!;

        Assert.Equal(Json(first), Json(second));
        Assert.Equal(ModelDiffFormat.Markdown(first), ModelDiffFormat.Markdown(second));
        Assert.Equal(ModelDiffFormat.Text(first), ModelDiffFormat.Text(second));
        Assert.Equal(ModelDiffFormat.Markdown(folder), ModelDiffFormat.Markdown((await f.Library.CompareAsync("./base", SnapshotLibrary.Working, 0, 100, Folders(f), Ct))!));
    }

    [Fact]
    public async Task A_folder_side_writes_nothing_into_its_folder_or_the_cache()
    {
        await using var f = await SnapshotFixture.CreateAsync();
        var folder = ModelBaselineTests.CopyBilling(Path.Combine(f.Repo.Root, "base"));
        await ReviewChangeAsync(f.Store);
        var before = Tree(folder);
        var library = Tree(f.Library.Folder);

        Assert.NotNull(await f.Library.CompareAsync(folder, SnapshotLibrary.Working, 0, 100, Folders(f), Ct));
        Assert.NotNull(await f.Library.CompareElementAsync(folder, SnapshotLibrary.Working, Invoice, Folders(f), Ct));

        Assert.Equal(before, Tree(folder));
        Assert.Equal(library, Tree(f.Library.Folder));
    }

    /// <summary>
    /// The review's change: in one batch, attribute number of Invoice renamed to code (which records a rename fact in main), and table
    /// payment_method created in main with package Billing.
    /// </summary>
    internal static async Task ReviewChangeAsync(ModelStore store)
    {
        var model = await store.GetSnapshotAsync(Ct);
        var invoice = model.GetDocument(Invoice)!;
        var node = JsonNode.Parse(invoice.Json.GetRawText())!.AsObject();
        node["attributes"]!.AsArray().Single(a => (string?)a!["id"] == InvoiceNumber)!["name"] = "code";
        var batch = new JsonObject
        {
            ["operations"] = new JsonArray(
                new JsonObject { ["op"] = "update", ["id"] = Invoice, ["expectedHash"] = invoice.Hash, ["element"] = node },
                JsonNode.Parse($$"""
                    { "op": "create", "element": { "kind": "table", "name": "payment_method", "database": "@database:main",
                      "schema": "@schema:billing", "package": "{{Billing}}",
                      "columns": [ { "name": "code", "type": "string", "length": 16, "nullable": false } ],
                      "primaryKey": { "columns": [ "@column:billing.payment_method.code" ] } } }
                    """)),
        };
        var parsed = store.ParseBatch(Encoding.UTF8.GetBytes(batch.ToJsonString()));
        Assert.True(parsed.Batch is not null, string.Join("; ", parsed.Diagnostics.Select(d => d.Rule + " " + d.Message)));
        var result = await store.ApplyBatchAsync(parsed.Batch, ChangeSource.Editor, Ct);
        Assert.True(result.Outcome == SaveOutcome.Saved,
            result.Outcome + ": " + string.Join("; ", result.Items.SelectMany(i => i.Diagnostics).Select(d => d.Rule + " " + d.Message)));
    }

    private static SnapshotCompareOptions Folders(SnapshotFixture f) => Review with { AllowFolders = true, CurrentDirectory = f.Repo.Root };

    private static string Json<T>(T value) => JsonSerializer.Serialize(value, new JsonSerializerOptions(JsonSerializerDefaults.Web));

    private static void Edit(string root, string path, string from, string to)
    {
        var file = Path.Combine(root, ".maquettiste", path);
        var text = File.ReadAllText(file);
        Assert.Contains(from, text, StringComparison.Ordinal);
        File.WriteAllText(file, text.Replace(from, to, StringComparison.Ordinal));
    }

    private static List<string> Tree(string root) =>
        !Directory.Exists(root) ? [] :
        [.. Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
            .Select(p => Path.GetRelativePath(root, p) + " " + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(p))))];
}
