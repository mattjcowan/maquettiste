using System.Text;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Hashing;
using Maquettiste.Engine.Loading;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Pipeline;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Loading;

public sealed class ModelLoaderTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly SequentialIdGenerator _ids = new(7);

    internal static string Canonical(Element element, string modelPath) =>
        Encoding.UTF8.GetString(TestServices.Json.Serialize(element, KindInfo.Get(element.Kind).SchemaFile, ".maquettiste/" + modelPath));

    private Entity NewEntity(string name, params string[] attributes) => new()
    {
        Id = _ids.NewId(),
        Name = name,
        Attributes = [.. attributes.Select(a => new ModelAttribute { Id = _ids.NewId(), Name = a, Type = new TypeRef { Builtin = "string" } })],
    };

    private static async Task<LoadResult> LoadAsync(ModelLoader loader, ModelSnapshot? previous = null, IReadOnlyCollection<string>? paths = null, bool verify = false) =>
        await loader.LoadAsync(new LoadRequest(previous, paths, verify), null, Ct);

    [Fact]
    public async Task Missing_model_root_loads_an_empty_model_with_default_settings()
    {
        using var h = new LoaderHarness();
        Directory.Delete(h.Repo.ModelRoot, recursive: true);

        var result = await LoadAsync(h.NewLoader());

        Assert.Empty(result.Snapshot.Documents);
        Assert.Empty(result.Snapshot.LoadDiagnostics);
        Assert.Equal(1, result.Snapshot.Settings.FormatVersion);
        Assert.True(result.Changes.IsEmpty);
    }

    [Fact]
    public async Task Invalid_json_is_MQ1001_with_line_and_column_and_the_file_is_left_out()
    {
        using var h = new LoaderHarness();
        var good = NewEntity("Good");
        h.Write("model/entities/good.json", Canonical(good, "model/entities/good.json"));
        h.Write("model/entities/broken.json", "{\n  \"kind\": \"entity\",\n  \"id\": oops\n}\n");

        var model = (await LoadAsync(h.NewLoader())).Snapshot;

        var d = Assert.Single(model.LoadDiagnostics);
        Assert.Equal("MQ1001", d.Rule);
        Assert.Equal(DiagnosticSeverity.Error, d.Severity);
        Assert.Equal(".maquettiste/model/entities/broken.json", d.FilePath);
        Assert.Equal(3, d.Line);
        Assert.Equal(9, d.Column);
        Assert.Equal([good.Id], model.Documents.Select(x => x.Element.Id));
    }

    [Fact]
    public async Task Schema_violations_are_MQ1002_with_pointer_line_and_column()
    {
        using var h = new LoaderHarness();
        var id = _ids.NewId();
        h.Write("model/entities/bad.json", $"{{\n  \"kind\": \"entity\",\n  \"id\": \"{id}\",\n  \"name\": \"Bad\",\n  \"colour\": \"red\",\n  \"abstract\": \"yes\"\n}}\n");

        var model = (await LoadAsync(h.NewLoader())).Snapshot;

        Assert.Empty(model.Documents);
        Assert.All(model.LoadDiagnostics, d => Assert.Equal("MQ1002", d.Rule));
        Assert.All(model.LoadDiagnostics, d => Assert.Equal(id, d.ElementId));
        var type = Assert.Single(model.LoadDiagnostics, d => d.JsonPointer == "/abstract");
        Assert.Equal((6, 15), (type.Line, type.Column));
        var extra = Assert.Single(model.LoadDiagnostics, d => d.JsonPointer == "/colour");
        Assert.Equal((5, 13), (extra.Line, extra.Column));
    }

    [Fact]
    public async Task A_malformed_id_is_MQ1006()
    {
        using var h = new LoaderHarness();
        h.Write("model/entities/bad-id.json", "{\n  \"kind\": \"entity\",\n  \"id\": \"01JAX4R0MEMBERSHIPRELATION1\",\n  \"name\": \"BadId\"\n}\n");

        var model = (await LoadAsync(h.NewLoader())).Snapshot;

        var d = Assert.Single(model.LoadDiagnostics);
        Assert.Equal("MQ1006", d.Rule);
        Assert.Equal("/id", d.JsonPointer);
        Assert.Equal(3, d.Line);
        Assert.Empty(model.Documents);
    }

    [Fact]
    public async Task An_unknown_kind_is_MQ1002()
    {
        using var h = new LoaderHarness();
        h.Write("model/entities/process.json", $"{{\n  \"kind\": \"process\",\n  \"id\": \"{_ids.NewId()}\"\n}}\n");

        var d = Assert.Single((await LoadAsync(h.NewLoader())).Snapshot.LoadDiagnostics);

        Assert.Equal("MQ1002", d.Rule);
        Assert.Equal("/kind", d.JsonPointer);
        Assert.Equal(2, d.Line);
    }

    [Fact]
    public async Task A_duplicate_id_is_MQ1004_and_the_ordinally_first_path_wins()
    {
        using var h = new LoaderHarness();
        var entity = NewEntity("Customer", "name");
        h.Write("model/entities/customer.json", Canonical(entity, "model/entities/customer.json"));
        h.Write("model/entities/zz-copy.json", Canonical(entity with { Name = "Zz" }, "model/entities/zz-copy.json"));

        var model = (await LoadAsync(h.NewLoader())).Snapshot;

        Assert.Equal("Customer", Assert.Single(model.Documents).Element.Name);
        var d = Assert.Single(model.LoadDiagnostics, x => x.Rule == "MQ1004");
        Assert.Equal(".maquettiste/model/entities/zz-copy.json", d.FilePath);
        Assert.Contains(".maquettiste/model/entities/customer.json", d.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_duplicate_sub_element_id_is_MQ1004_and_the_file_still_loads()
    {
        using var h = new LoaderHarness();
        var a = NewEntity("A", "name");
        var b = NewEntity("B") with { Attributes = [a.Attributes[0]] };
        h.Write("model/entities/a.json", Canonical(a, "model/entities/a.json"));
        h.Write("model/entities/b.json", Canonical(b, "model/entities/b.json"));

        var model = (await LoadAsync(h.NewLoader())).Snapshot;

        Assert.Equal(2, model.Documents.Count);
        var d = Assert.Single(model.LoadDiagnostics);
        Assert.Equal("MQ1004", d.Rule);
        Assert.Equal(".maquettiste/model/entities/b.json", d.FilePath);
        Assert.Equal("/attributes/0/id", d.JsonPointer);
        Assert.NotNull(d.Line);
    }

    [Fact]
    public async Task A_non_canonical_file_loads_with_warning_MQ1003()
    {
        using var h = new LoaderHarness();
        var entity = NewEntity("Customer");
        h.Write("model/entities/customer.json", $"{{ \"name\": \"Customer\", \"kind\": \"entity\", \"id\": \"{entity.Id}\" }}");

        var model = (await LoadAsync(h.NewLoader())).Snapshot;

        Assert.Single(model.Documents);
        var d = Assert.Single(model.LoadDiagnostics);
        Assert.Equal(("MQ1003", DiagnosticSeverity.Warning), (d.Rule, d.Severity));
    }

    [Fact]
    public async Task A_file_outside_its_folder_or_with_another_name_loads_with_warning_MQ1005()
    {
        using var h = new LoaderHarness();
        var misplaced = NewEntity("Customer");
        var misnamed = NewEntity("Invoice");
        var collided = NewEntity("Invoice");
        h.Write("model/relations/customer.json", Canonical(misplaced, "model/relations/customer.json"));
        h.Write("model/entities/bill.json", Canonical(misnamed, "model/entities/bill.json"));
        var suffixed = "model/entities/invoice" + ModelPaths.Suffix(collided.Id) + ".json";
        h.Write(suffixed, Canonical(collided, suffixed));

        var model = (await LoadAsync(h.NewLoader())).Snapshot;

        Assert.Equal(3, model.Documents.Count);
        Assert.Equal(
            [".maquettiste/model/entities/bill.json", ".maquettiste/model/relations/customer.json"],
            model.LoadDiagnostics.Select(d => (d.Rule, d.FilePath)).Where(x => x.Rule == "MQ1005").Select(x => x.FilePath));
        Assert.All(model.LoadDiagnostics, d => Assert.Equal(DiagnosticSeverity.Warning, d.Severity));
    }

    [Fact]
    public async Task Tables_are_expected_in_their_database_folder()
    {
        using var h = new LoaderHarness();
        var b = new ModelBuilder(3);
        var db = b.Database("Main Store", Dialect.PostgreSql);
        b.Table("orders", db);
        await b.WriteToAsync(h.Repo.ModelRoot, Ct);

        var model = (await LoadAsync(h.NewLoader())).Snapshot;

        Assert.Empty(model.LoadDiagnostics);
        Assert.Contains(model.Documents, d => d.Path == ".maquettiste/model/databases/main-store/tables/orders.json");
    }

    [Fact]
    public async Task An_unsupported_format_version_is_MQ1007()
    {
        using var h = new LoaderHarness();
        h.Write("maquettiste.json", "{\n  \"formatVersion\": 2,\n  \"future\": true\n}\n");

        var model = (await LoadAsync(h.NewLoader())).Snapshot;

        var d = Assert.Single(model.LoadDiagnostics);
        Assert.Equal(("MQ1007", "/formatVersion", 2), (d.Rule, d.JsonPointer, d.Line));
        Assert.Equal(1, model.Settings.FormatVersion);
    }

    [Fact]
    public async Task Explorer_folders_load_from_the_project_settings()
    {
        using var h = new LoaderHarness();
        h.Write("maquettiste.json", """
            {
              "formatVersion": 1,
              "explorer": {
                "folders": [
                  { "label": "Agents", "icon": "bot", "kind": "entity", "match": { "stereotype": "acme-agent" } },
                  { "label": "Core", "kind": "entity", "match": { "tag": "core" } }
                ]
              }
            }

            """);

        var model = (await LoadAsync(h.NewLoader())).Snapshot;

        // The hand-written text is not in canonical form (MQ1003); nothing else is reported.
        Assert.Empty(model.LoadDiagnostics.Where(d => d.Rule != "MQ1003").Select(d => $"{d.Rule} {d.JsonPointer} {d.Message}"));
        var folders = model.Settings.Explorer.Folders;
        Assert.Equal(2, folders.Count);
        Assert.Equal(("Agents", "bot", "entity", "acme-agent"), (folders[0].Label, folders[0].Icon, folders[0].Kind, folders[0].Match.Stereotype));
        Assert.Equal((null, "core"), (folders[1].Icon, folders[1].Match.Tag));
    }

    [Fact]
    public async Task Explorer_scopes_load_from_the_project_settings()
    {
        using var h = new LoaderHarness();
        h.Write("maquettiste.json", """
            {
              "formatVersion": 1,
              "explorer": {
                "scopes": [
                  { "name": "Billing core", "kinds": ["entity"], "domain": "01J92P0V0000000000000000AA", "tags": ["core"], "errors": true },
                  { "name": "Everything" }
                ]
              }
            }

            """);

        var model = (await LoadAsync(h.NewLoader())).Snapshot;

        Assert.Empty(model.LoadDiagnostics.Where(d => d.Rule != "MQ1003").Select(d => $"{d.Rule} {d.JsonPointer} {d.Message}"));
        var scopes = model.Settings.Explorer.Scopes;
        Assert.Equal(2, scopes.Count);
        Assert.Equal(("Billing core", "entity", "01J92P0V0000000000000000AA", "core", true), (scopes[0].Name, scopes[0].Kinds[0], scopes[0].Domain, scopes[0].Tags[0], scopes[0].Errors));
        Assert.Equal(("Everything", 0, null, false), (scopes[1].Name, scopes[1].Kinds.Count, scopes[1].Diagram, scopes[1].Errors));
        Assert.Empty(model.Settings.Explorer.Folders);
    }

    [Fact]
    public async Task An_explorer_folder_with_two_conditions_is_a_schema_error()
    {
        using var h = new LoaderHarness();
        h.Write("maquettiste.json", """
            {
              "formatVersion": 1,
              "explorer": { "folders": [ { "label": "Both", "kind": "entity", "match": { "stereotype": "a", "tag": "b" } } ] }
            }

            """);

        var model = (await LoadAsync(h.NewLoader())).Snapshot;

        Assert.Contains(model.LoadDiagnostics, d => d.FilePath == ".maquettiste/maquettiste.json");
        Assert.Empty(model.Settings.Explorer.Folders);
    }

    [Fact]
    public async Task Extensions_and_rule_scripts_load_in_path_order_and_a_bad_extension_is_MQ5004()
    {
        using var h = new LoaderHarness();
        h.Write("extensions/b.json", "{\n  \"name\": \"b\",\n  \"appliesTo\": {},\n  \"properties\": {}\n}\n");
        h.Write("extensions/a.json", "{\n  \"name\": \"a\",\n  \"appliesTo\": { \"kinds\": [\"entity\"] },\n  \"properties\": { \"owner\": { \"type\": \"string\" } }\n}\n");
        h.Write("extensions/bad.json", "{\n  \"name\": \"bad\"\n}\n");
        h.Write("extensions/rules/z.js", "maquettiste.rule({ id: 'z' });\n");
        h.Write("extensions/rules/a.js", "﻿maquettiste.rule({ id: 'a' });\n");
        h.Write("extensions/rules/notes.txt", "ignored");

        var model = (await LoadAsync(h.NewLoader())).Snapshot;

        Assert.Equal(["a", "b"], model.Extensions.Select(e => e.Schema.Name));
        Assert.Equal([".maquettiste/extensions/rules/a.js", ".maquettiste/extensions/rules/z.js"], model.RuleScripts.Select(s => s.Path));
        Assert.StartsWith("maquettiste.rule", model.RuleScripts[0].Code, StringComparison.Ordinal);
        Assert.All(model.LoadDiagnostics, d => Assert.Equal(("MQ5004", ".maquettiste/extensions/bad.json"), (d.Rule, d.FilePath)));
        Assert.NotEmpty(model.LoadDiagnostics);
    }

    [Fact]
    public async Task Hidden_and_foreign_files_are_ignored()
    {
        using var h = new LoaderHarness();
        h.Write("model/entities/.invoice.json.mq-1-1.tmp", "{");
        h.Write("model/entities/.hidden.json", "{");
        h.Write("model/entities/readme.txt", "not a model file");
        h.Write("templates/pack/pack.json", "{");
        h.Write(".cache/journal.jsonl", "{");

        var model = (await LoadAsync(h.NewLoader())).Snapshot;

        Assert.Empty(model.LoadDiagnostics);
        Assert.Empty(model.Documents);
    }

    [Fact]
    public async Task Parallel_and_sequential_loads_are_identical()
    {
        using var h1 = new LoaderHarness(parallelism: 1);
        h1.CopyFixture("models", "billing");
        using var h8 = new LoaderHarness(parallelism: 8);
        h8.CopyFixture("models", "billing");
        h8.Write("model/entities/broken.json", "{");
        h1.Write("model/entities/broken.json", "{");

        var one = (await LoadAsync(h1.NewLoader())).Snapshot;
        var eight = (await LoadAsync(h8.NewLoader())).Snapshot;

        Assert.Equal(one.Documents.Select(d => (d.Path, d.Hash, d.DependencyHash)), eight.Documents.Select(d => (d.Path, d.Hash, d.DependencyHash)));
        Assert.Equal(one.LoadDiagnostics, eight.LoadDiagnostics);
        Assert.Equal(one.Summaries().Select(s => s.Id), eight.Summaries().Select(s => s.Id));
    }

    [Fact]
    public async Task Progress_is_reported_per_file_for_the_load_stage()
    {
        using var h = new LoaderHarness();
        h.CopyFixture("models", "billing");
        var updates = new List<ProgressUpdate>();

        await h.NewLoader().LoadAsync(new LoadRequest(null, null, false), new SyncProgress(u => { lock (updates) updates.Add(u); }), Ct);

        Assert.All(updates, u => Assert.Equal(PipelineStage.Load, u.Stage));
        Assert.Equal(28, updates.Count); // 26 elements, maquettiste.json, one extension (the sidecar is not a primary file)
        Assert.Equal(28, updates.Max(u => u.Done));
        Assert.All(updates, u => Assert.Equal(28, u.Total));
        Assert.All(updates, u => Assert.StartsWith(".maquettiste/", u.CurrentPath, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Cancellation_mid_load_throws_and_leaves_the_loader_usable()
    {
        using var h = new LoaderHarness(parallelism: 1);
        h.CopyFixture("models", "billing");
        var loader = h.NewLoader();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var seen = 0;
        var progress = new SyncProgress(_ =>
        {
            if (Interlocked.Increment(ref seen) == 5)
                cts.Cancel();
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => loader.LoadAsync(new LoadRequest(null, null, false), progress, cts.Token));

        Assert.InRange(seen, 5, 6);
        Assert.False(File.Exists(loader.CachePath)); // nothing half-written
        var result = await LoadAsync(loader);
        Assert.Equal(26, result.Snapshot.Documents.Count);
        Assert.Equal(1, result.Snapshot.Version);
    }

    [Fact]
    public async Task A_partial_load_rereads_only_the_given_paths_and_reports_what_changed()
    {
        using var h = new LoaderHarness();
        h.CopyFixture("models", "billing");
        var loader = h.NewLoader();
        var first = (await LoadAsync(loader)).Snapshot;
        var customerId = Billing.IdOf(first, "entity", "Customer");
        var customer = first.Get<Entity>(customerId)!;

        // Unchanged path: nothing.
        var same = await LoadAsync(loader, first, [".maquettiste/model/entities/customer.json"]);
        Assert.True(same.Changes.IsEmpty);
        Assert.Same(first, same.Snapshot);

        // Edited: one change, with the new hash; the other files are not read.
        h.Write("model/entities/customer.json", Canonical(customer with { DisplayName = "Client" }, "model/entities/customer.json"));
        var edited = await LoadAsync(loader, first, [h.Model("model/entities/customer.json")]);
        var change = Assert.Single(edited.Changes.Changed);
        Assert.Equal((customerId, "entity", ".maquettiste/model/entities/customer.json"), (change.Id, change.Kind, change.Path));
        Assert.Equal(ContentHash.Of(File.ReadAllBytes(h.Model("model/entities/customer.json"))), change.Hash);
        Assert.Empty(edited.Changes.Deleted);
        Assert.Equal(ChangeSource.Disk, edited.Changes.Source);
        Assert.Equal(1, loader.LastStatistics.ReadFromDisk);
        Assert.Equal(first.Version + 1, edited.Snapshot.Version);
        Assert.Equal("Client", edited.Snapshot.Get<Entity>(customerId)!.DisplayName);

        // Deleted: reported by id.
        var productId = Billing.IdOf(first, "entity", "Product");
        File.Delete(h.Model("model/entities/product.json"));
        var deleted = await LoadAsync(loader, edited.Snapshot, ["model/entities/product.json"]);
        Assert.Equal([productId], deleted.Changes.Deleted);
        Assert.Null(deleted.Snapshot.GetDocument(productId));
    }

    [Fact]
    public async Task A_sidecar_edit_changes_the_element_that_references_it()
    {
        using var h = new LoaderHarness();
        h.CopyFixture("models", "billing");
        var loader = h.NewLoader();
        var first = (await LoadAsync(loader)).Snapshot;
        var invoice = first.GetDocument(Billing.IdOf(first, "entity", "Invoice"))!;

        h.Write("model/entities/invoice.md", "# Invoice\n\nRewritten.\n");
        var result = await LoadAsync(loader, first, [".maquettiste/model/entities/invoice.md"]);

        var change = Assert.Single(result.Changes.Changed);
        Assert.Equal(invoice.Element.Id, change.Id);
        Assert.Equal(invoice.Hash, change.Hash); // the file is unchanged; its dependency hash is not
        var after = result.Snapshot.GetDocument(invoice.Element.Id)!;
        Assert.NotEqual(invoice.DependencyHash, after.DependencyHash);
        Assert.Equal("# Invoice\n\nRewritten.\n", after.SidecarText);
    }

    [Fact]
    public async Task A_rename_reported_only_by_its_new_path_does_not_leave_a_duplicate()
    {
        using var h = new LoaderHarness();
        h.CopyFixture("models", "billing");
        var loader = h.NewLoader();
        var first = (await LoadAsync(loader)).Snapshot;

        File.Move(h.Model("model/entities/payment.json"), h.Model("model/entities/settlement.json"));
        var result = await LoadAsync(loader, first, ["model/entities/settlement.json"]);

        Assert.DoesNotContain(result.Snapshot.LoadDiagnostics, d => d.Rule == "MQ1004");
        Assert.Contains(result.Snapshot.Documents, d => d.Path == ".maquettiste/model/entities/settlement.json");
        Assert.DoesNotContain(result.Snapshot.Documents, d => d.Path == ".maquettiste/model/entities/payment.json");
    }

    [Fact]
    public async Task A_folder_reported_by_a_watcher_is_rescanned()
    {
        using var h = new LoaderHarness();
        h.CopyFixture("models", "billing");
        var loader = h.NewLoader();
        var first = (await LoadAsync(loader)).Snapshot;
        var views = first.Documents.Where(d => d.Path.Contains("/views/", StringComparison.Ordinal)).Select(d => d.Element.Id).ToList();

        Directory.Delete(h.Model("model/databases/main/views"), recursive: true);
        var extra = NewEntity("Refund");
        h.Write("model/entities/refund.json", Canonical(extra, "model/entities/refund.json"));
        var result = await LoadAsync(loader, first, [".maquettiste/model/databases/main/views", ".maquettiste/model/entities"]);

        Assert.Equal(views, result.Changes.Deleted);
        Assert.Equal([extra.Id], result.Changes.Changed.Select(c => c.Id));
    }

    [Fact]
    public async Task A_rescan_with_nothing_changed_returns_the_same_snapshot()
    {
        using var h = new LoaderHarness();
        h.CopyFixture("models", "billing");
        var loader = h.NewLoader();
        var first = (await LoadAsync(loader)).Snapshot;

        var again = await LoadAsync(loader, first);

        Assert.Same(first, again.Snapshot);
        Assert.True(again.Changes.IsEmpty);
        Assert.Equal(0, loader.LastStatistics.ReadFromDisk);
        Assert.Equal(28 + 1, loader.LastStatistics.Reused);
        Assert.False(loader.LastStatistics.CacheWritten);
    }
}
