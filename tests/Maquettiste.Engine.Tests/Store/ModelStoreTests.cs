using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Hashing;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Tests.Loading;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Store;

public sealed class ModelStoreTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_public_constructor_does_no_io_and_never_throws()
    {
        var root = Path.Combine(Path.GetTempPath(), "maquettiste-tests", "never-created-" + Environment.ProcessId);
        var options = new EngineOptions { RepoRoot = Path.Combine(root, "repo"), CacheDirectory = Path.Combine(root, "cache") };

        await using var store = new ModelStore(options);

        Assert.Null(store.Current);
        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public async Task Concurrent_loads_load_once()
    {
        using var h = new LoaderHarness();
        h.CopyFixture("models", "billing");
        var loader = new CountingLoader(h.NewLoader());
        await using var store = h.NewStore(loader);

        await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => store.LoadAsync(Ct)));
        await store.LoadAsync(Ct);

        Assert.Equal(1, loader.Calls);
        Assert.Equal(33, store.Current!.Documents.Count);
    }

    [Fact]
    public async Task Reads_load_on_first_use_and_expose_the_file_hash_as_etag()
    {
        await using var s = await BillingStore.OpenAsync(load: false);

        var index = await s.Store.GetIndexAsync(Ct);

        Assert.NotNull(s.Store.Current);
        Assert.Equal(33, index.Count);
        foreach (var summary in index)
        {
            Assert.Equal(ContentHash.Of(File.ReadAllBytes(s.Harness.Repo.PathOf(summary.Path))), summary.Hash);
            Assert.True(ContentHash.IsValid(summary.Hash));
        }

        var invoice = s.Id("entity", "Invoice");
        var document = await s.Store.GetElementAsync(invoice, Ct);
        Assert.Equal(invoice, document!.Element.Id);
        var attributeId = ((Entity)document.Element).Attributes[1].Id;
        Assert.Same(document, await s.Store.GetElementAsync(attributeId, Ct)); // a sub-element id returns its owner
        Assert.Null(await s.Store.GetElementAsync("01J92P0V0000000000000000ZZ", Ct));
        var references = await s.Store.GetReferencesAsync(invoice, Ct);
        Assert.Contains(references, r => r.FromElementId == s.Id("mapping", "Invoice in main") && r.Field == "entity");
    }

    [Fact]
    public async Task A_save_rewrites_the_file_canonically_and_publishes_one_change_that_the_watcher_does_not_echo()
    {
        await using var s = await BillingStore.OpenAsync();
        var customer = s.Doc("entity", "Customer");

        var result = await s.Store.SaveAsync(customer.Element.Id, BillingStore.Edit(customer, n => n["displayName"] = "Client"), customer.Hash, ChangeSource.Editor, Ct);

        Assert.Equal(SaveOutcome.Saved, result.Outcome);
        var bytes = File.ReadAllBytes(s.Harness.Model("model/entities/customer.json"));
        Assert.True(TestServices.Json.IsCanonical(bytes, "entity.json", customer.Path));
        Assert.Contains("\"displayName\": \"Client\"", Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);
        Assert.Equal(ContentHash.Of(bytes), result.Hash);
        Assert.Equal(result.Hash, result.Current!.Hash);
        Assert.Equal("Client", result.Current.Element.DisplayName);
        Assert.Same(result.Current, s.Store.Current!.GetDocument(customer.Element.Id));
        var change = Assert.Single(result.Changes!.Changed);
        Assert.Equal((customer.Element.Id, result.Hash, ChangeSource.Editor), (change.Id, change.Hash, result.Changes.Source));
        Assert.Same(result.Changes, Assert.Single(s.Notifications));

        // The watcher sees the store's own write: same hash, nothing to report (host-contracts 19).
        var echo = await s.Store.RefreshAsync([s.Harness.Model("model/entities/customer.json")], Ct);
        Assert.True(echo.IsEmpty);
        Assert.Single(s.Notifications);
        Assert.Empty(Directory.EnumerateFiles(s.Harness.Repo.ModelRoot, "*.tmp", SearchOption.AllDirectories));
        Assert.Empty(Directory.EnumerateFiles(s.Harness.Repo.ModelRoot, "*.bak", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task A_save_with_a_stale_etag_is_a_conflict_carrying_the_current_version()
    {
        await using var s = await BillingStore.OpenAsync();
        var customer = s.Doc("entity", "Customer");
        var first = await s.Store.SaveAsync(customer.Element.Id, BillingStore.Edit(customer, n => n["displayName"] = "Client"), customer.Hash, ChangeSource.Editor, Ct);
        var before = s.Files();

        var second = await s.Store.SaveAsync(customer.Element.Id, BillingStore.Edit(customer, n => n["displayName"] = "Patron"), customer.Hash, ChangeSource.Editor, Ct);

        Assert.Equal(SaveOutcome.Conflict, second.Outcome);
        Assert.Equal(first.Hash, second.Hash);
        Assert.Equal("Client", second.Current!.Element.DisplayName);
        Assert.Null(second.Changes);
        Assert.Equal(before, s.Files());
    }

    [Fact]
    public async Task A_save_refuses_when_the_disk_changed_behind_the_index()
    {
        await using var s = await BillingStore.OpenAsync();
        var customer = s.Doc("entity", "Customer");
        var onDisk = BillingStore.Edit(customer, n => n["displayName"] = "Edited in another editor");
        s.Harness.Write("model/entities/customer.json", Encoding.UTF8.GetString(TestServices.Json.Write(JsonNode.Parse(onDisk)!, "entity.json", customer.Path)));

        var result = await s.Store.SaveAsync(customer.Element.Id, BillingStore.Edit(customer, n => n["displayName"] = "Mine"), customer.Hash, ChangeSource.Editor, Ct);

        Assert.Equal(SaveOutcome.Conflict, result.Outcome);
        Assert.Equal("Edited in another editor", result.Current!.Element.DisplayName);
        Assert.Equal(ContentHash.Of(File.ReadAllBytes(s.Harness.Model("model/entities/customer.json"))), result.Hash);
        var seen = Assert.Single(s.Notifications); // the store indexed the disk edit on the way
        Assert.Equal(ChangeSource.Disk, seen.Source);

        var retry = await s.Store.SaveAsync(customer.Element.Id, BillingStore.Edit(result.Current, n => n["displayName"] = "Mine"), result.Hash!, ChangeSource.Editor, Ct);
        Assert.Equal(SaveOutcome.Saved, retry.Outcome);
    }

    [Fact]
    public async Task Saving_an_unchanged_element_writes_nothing()
    {
        await using var s = await BillingStore.OpenAsync();
        var customer = s.Doc("entity", "Customer");
        var stamp = File.GetLastWriteTimeUtc(s.Harness.Model("model/entities/customer.json"));

        var result = await s.Store.SaveAsync(customer.Element.Id, BillingStore.Edit(customer, _ => { }), customer.Hash, ChangeSource.Editor, Ct);

        Assert.Equal(SaveOutcome.Saved, result.Outcome);
        Assert.Equal(customer.Hash, result.Hash);
        Assert.True(result.Changes!.IsEmpty);
        Assert.Empty(s.Notifications);
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(s.Harness.Model("model/entities/customer.json")));
    }

    [Fact]
    public async Task A_rename_moves_the_file_and_adds_the_id_suffix_only_on_a_collision()
    {
        await using var s = await BillingStore.OpenAsync();
        var line = s.Doc("entity", "InvoiceLine");

        var renamed = await s.Store.SaveAsync(line.Element.Id, BillingStore.Edit(line, n => n["name"] = "LineItem"), line.Hash, ChangeSource.Editor, Ct);

        Assert.Equal(SaveOutcome.Saved, renamed.Outcome);
        Assert.Equal(".maquettiste/model/entities/line-item.json", renamed.Current!.Path);
        Assert.False(s.Harness.Exists("model/entities/invoice-line.json"));
        Assert.True(s.Harness.Exists("model/entities/line-item.json"));
        Assert.Equal(".maquettiste/model/entities/line-item.json", Assert.Single(renamed.Changes!.Changed).Path);

        var payment = s.Doc("entity", "Payment");
        var collided = await s.Store.SaveAsync(payment.Element.Id, BillingStore.Edit(payment, n => n["name"] = "Product"), payment.Hash, ChangeSource.Editor, Ct);

        var expected = "model/entities/product" + Engine.Loading.ModelPaths.Suffix(payment.Element.Id) + ".json";
        Assert.Equal(".maquettiste/" + expected, collided.Current!.Path);
        Assert.True(s.Harness.Exists(expected));
        Assert.True(s.Harness.Exists("model/entities/product.json"));
        Assert.False(s.Harness.Exists("model/entities/payment.json"));

        var reloaded = await s.Store.RescanAsync(true, Ct);
        Assert.True(reloaded.IsEmpty);
        Assert.DoesNotContain(s.Store.Current!.LoadDiagnostics, d => d.Rule == "MQ1005");
    }

    [Fact]
    public async Task Renaming_a_database_moves_its_folder_with_every_table_view_sequence_and_query()
    {
        await using var s = await BillingStore.OpenAsync();
        var db = s.Doc("database", "main");
        var children = s.Model.Documents.Where(d => d.Path.StartsWith(".maquettiste/model/databases/main/", StringComparison.Ordinal)).Select(d => d.Element.Id).ToList();

        var result = await s.Store.SaveAsync(db.Element.Id, BillingStore.Edit(db, n => n["name"] = "Primary Store"), db.Hash, ChangeSource.Editor, Ct);

        Assert.Equal(SaveOutcome.Saved, result.Outcome);
        Assert.Equal(".maquettiste/model/databases/primary-store/database.json", result.Current!.Path);
        Assert.False(Directory.Exists(s.Harness.Model("model/databases/main")));
        Assert.Equal(8, children.Count);
        foreach (var id in children)
            Assert.StartsWith(".maquettiste/model/databases/primary-store/", s.Store.Current!.GetDocument(id)!.Path, StringComparison.Ordinal);
        Assert.Equal(children.Order(StringComparer.Ordinal), result.Changes!.Changed.Select(c => c.Id).Where(children.Contains).Order(StringComparer.Ordinal));
        Assert.Empty(s.Store.Current!.LoadDiagnostics);
    }

    [Fact]
    public async Task Saves_that_change_an_id_a_kind_or_a_stereotype_key_are_invalid()
    {
        await using var s = await BillingStore.OpenAsync();
        var customer = s.Doc("entity", "Customer");
        var audited = s.Doc("stereotype", "Audited");
        var before = s.Files();

        var id = await s.Store.SaveAsync(customer.Element.Id, BillingStore.Edit(customer, n => n["id"] = "01J92P0V0000000000000000ZZ"), customer.Hash, ChangeSource.Editor, Ct);
        var kind = await s.Store.SaveAsync(customer.Element.Id, BillingStore.Edit(customer, n => n["kind"] = "value-object"), customer.Hash, ChangeSource.Editor, Ct);
        var key = await s.Store.SaveAsync(audited.Element.Id, BillingStore.Edit(audited, n => n["key"] = "tracked"), audited.Hash, ChangeSource.Editor, Ct);

        Assert.Equal(SaveOutcome.Invalid, id.Outcome);
        Assert.Equal("/id", Assert.Single(id.Diagnostics).JsonPointer);
        Assert.Equal(SaveOutcome.Invalid, kind.Outcome);
        Assert.Equal(SaveOutcome.Invalid, key.Outcome);
        Assert.Equal("MQ3020", Assert.Single(key.Diagnostics).Rule);
        Assert.Equal(before, s.Files());
        Assert.Empty(s.Notifications);
    }

    [Fact]
    public async Task Schema_violations_and_bad_json_are_invalid_and_write_nothing()
    {
        await using var s = await BillingStore.OpenAsync();
        var customer = s.Doc("entity", "Customer");
        var before = s.Files();

        var schema = await s.Store.SaveAsync(customer.Element.Id, BillingStore.Edit(customer, n => n["abstract"] = "yes"), customer.Hash, ChangeSource.Editor, Ct);
        var json = await s.Store.SaveAsync(customer.Element.Id, "{\n  \"kind\": \"entity\",\n  oops\n}"u8.ToArray(), customer.Hash, ChangeSource.Editor, Ct);

        Assert.Equal(SaveOutcome.Invalid, schema.Outcome);
        var d = Assert.Single(schema.Diagnostics);
        Assert.Equal(("MQ1002", "/abstract", customer.Path), (d.Rule, d.JsonPointer, d.FilePath));
        Assert.Equal(SaveOutcome.Invalid, json.Outcome);
        Assert.Equal(("MQ1001", 3), (json.Diagnostics[0].Rule, json.Diagnostics[0].Line));
        Assert.Equal(before, s.Files());
    }

    [Fact]
    public async Task Saving_an_unknown_id_is_not_found()
    {
        await using var s = await BillingStore.OpenAsync();
        var customer = s.Doc("entity", "Customer");

        var result = await s.Store.SaveAsync("01J92P0V0000000000000000ZZ", BillingStore.Edit(customer, n => n.Remove("id")), customer.Hash, ChangeSource.Editor, Ct);

        Assert.Equal(SaveOutcome.NotFound, result.Outcome);
    }

    [Fact]
    public async Task Validation_runs_on_the_candidate_and_only_new_errors_refuse_the_save()
    {
        await using var s = await BillingStore.OpenAsync();
        var customer = s.Doc("entity", "Customer");
        var validator = s.Harness.Validator;
        validator.Rule = (model, scope) =>
            model.Get<Entity>(customer.Element.Id)?.DisplayName == "Bad"
                ? [RuleCatalog.Create("MQ3018", "Bad display name.", customer.Element.Id, model.GetDocument(customer.Element.Id)!.Path, "/displayName")]
                : [];
        var before = s.Files();

        var refused = await s.Store.SaveAsync(customer.Element.Id, BillingStore.Edit(customer, n => n["displayName"] = "Bad"), customer.Hash, ChangeSource.Editor, Ct);

        Assert.Equal(SaveOutcome.Invalid, refused.Outcome);
        Assert.Equal("MQ3018", Assert.Single(refused.Diagnostics).Rule);
        Assert.Equal(before, s.Files());
        var (candidate, scope) = validator.Calls.First();
        Assert.Equal([customer.Element.Id], scope.ElementIds);
        Assert.True(scope.IncludeReferrers);
        Assert.Equal("Bad", candidate.Get<Entity>(customer.Element.Id)!.DisplayName);

        // An error the element already had does not block an unrelated edit; it comes back with the result.
        validator.Rule = (model, _) => [RuleCatalog.Create("MQ3018", "Always wrong.", customer.Element.Id, model.GetDocument(customer.Element.Id)!.Path, "/name")];
        var saved = await s.Store.SaveAsync(customer.Element.Id, BillingStore.Edit(customer, n => n["displayName"] = "Fine"), customer.Hash, ChangeSource.Editor, Ct);
        Assert.Equal(SaveOutcome.Saved, saved.Outcome);
        Assert.Equal("MQ3018", Assert.Single(saved.Diagnostics).Rule);
    }

    [Fact]
    public async Task Validation_errors_on_a_referrer_are_attributed_to_the_change_that_caused_them()
    {
        await using var s = await BillingStore.OpenAsync();
        var money = s.Doc("value-object", "Money");
        var invoice = s.Id("entity", "Invoice");
        s.Harness.Validator.Rule = (model, _) =>
            model.Get<ValueObject>(money.Element.Id)!.Attributes.Count < 2
                ? [RuleCatalog.Create("MQ2001", "Invoice.total lost a member.", invoice, model.GetDocument(invoice)!.Path, "/attributes/3")]
                : [];

        var result = await s.Store.SaveAsync(money.Element.Id, BillingStore.Edit(money, n => n["attributes"]!.AsArray().RemoveAt(1)), money.Hash, ChangeSource.Editor, Ct);

        Assert.Equal(SaveOutcome.Invalid, result.Outcome);
        Assert.Equal(invoice, Assert.Single(result.Diagnostics).ElementId);
    }

    [Fact]
    public async Task Create_assigns_an_id_writes_the_conventional_path_and_suffixes_only_on_a_collision()
    {
        await using var s = await BillingStore.OpenAsync();
        var billing = s.Id("package", "Billing");

        var created = await s.Store.CreateAsync(Encoding.UTF8.GetBytes($"{{\"name\":\"Refund\",\"kind\":\"entity\",\"package\":\"{billing}\"}}"), ChangeSource.Editor, Ct);

        Assert.Equal(SaveOutcome.Saved, created.Outcome);
        Assert.True(IdFormat.IsValid(created.Id));
        Assert.Equal(".maquettiste/model/entities/refund.json", created.Current!.Path);
        Assert.Equal(ContentHash.Of(File.ReadAllBytes(s.Harness.Model("model/entities/refund.json"))), created.Hash);
        Assert.Equal(created.Id, Assert.Single(created.Changes!.Changed).Id);

        var twin = await s.Store.CreateAsync("{\"kind\":\"entity\",\"name\":\"Refund\"}"u8.ToArray(), ChangeSource.Cli, Ct);
        Assert.Equal(".maquettiste/model/entities/refund" + Engine.Loading.ModelPaths.Suffix(twin.Id!) + ".json", twin.Current!.Path);
        Assert.Equal(ChangeSource.Cli, twin.Changes!.Source);

        var db = s.Id("database", "main");
        var table = await s.Store.CreateAsync(Encoding.UTF8.GetBytes($"{{\"kind\":\"table\",\"name\":\"audit_log\",\"database\":\"{db}\",\"columns\":[{{\"id\":\"{new SequentialIdGenerator(99).NewId()}\",\"name\":\"at\",\"type\":\"datetimeoffset\"}}]}}"), ChangeSource.Editor, Ct);
        Assert.Equal(".maquettiste/model/databases/main/tables/audit-log.json", table.Current!.Path);

        var database = await s.Store.CreateAsync("{\"kind\":\"database\",\"name\":\"Reporting\",\"dialect\":\"sqlite\"}"u8.ToArray(), ChangeSource.Editor, Ct);
        Assert.Equal(".maquettiste/model/databases/reporting/database.json", database.Current!.Path);
        Assert.Empty(s.Store.Current!.LoadDiagnostics);
    }

    [Fact]
    public async Task Create_refuses_used_ids_and_a_second_tag_vocabulary()
    {
        await using var s = await BillingStore.OpenAsync();
        var customer = s.Doc("entity", "Customer");
        var attributeId = ((Entity)customer.Element).Attributes[0].Id;
        var before = s.Files();

        var sameId = await s.Store.CreateAsync(Encoding.UTF8.GetBytes($"{{\"kind\":\"entity\",\"id\":\"{customer.Element.Id}\",\"name\":\"Other\"}}"), ChangeSource.Editor, Ct);
        var subId = await s.Store.CreateAsync(Encoding.UTF8.GetBytes($"{{\"kind\":\"entity\",\"name\":\"Other\",\"attributes\":[{{\"id\":\"{attributeId}\",\"name\":\"x\",\"type\":\"string\"}}]}}"), ChangeSource.Editor, Ct);
        var badId = await s.Store.CreateAsync("{\"kind\":\"entity\",\"id\":\"not-a-ulid\",\"name\":\"Other\"}"u8.ToArray(), ChangeSource.Editor, Ct);
        var tags = await s.Store.CreateAsync("{\"kind\":\"tag-vocabulary\",\"name\":\"more tags\"}"u8.ToArray(), ChangeSource.Editor, Ct);

        Assert.Equal((SaveOutcome.Invalid, "MQ1004"), (sameId.Outcome, sameId.Diagnostics[0].Rule));
        Assert.Equal((SaveOutcome.Invalid, "MQ1004"), (subId.Outcome, subId.Diagnostics[0].Rule));
        Assert.Equal("/attributes/0/id", subId.Diagnostics[0].JsonPointer);
        Assert.Equal((SaveOutcome.Invalid, "MQ1006"), (badId.Outcome, badId.Diagnostics[0].Rule));
        Assert.Equal((SaveOutcome.Invalid, "MQ1009"), (tags.Outcome, tags.Diagnostics[0].Rule));
        Assert.Equal(before, s.Files());
    }

    [Fact]
    public async Task A_write_the_path_policy_refuses_is_invalid_and_changes_nothing()
    {
        await using var s = await BillingStore.OpenAsync();
        var customer = s.Doc("entity", "Customer");
        s.Harness.Policy.Refuse = p => p.EndsWith("customer.json", StringComparison.Ordinal) || p.Contains(".customer.json.", StringComparison.Ordinal);
        var before = s.Files();

        var result = await s.Store.SaveAsync(customer.Element.Id, BillingStore.Edit(customer, n => n["displayName"] = "Client"), customer.Hash, ChangeSource.Editor, Ct);

        Assert.Equal(SaveOutcome.Invalid, result.Outcome);
        Assert.Equal("MQ6004", Assert.Single(result.Diagnostics).Rule);
        Assert.Equal(before, s.Files());
        Assert.Contains(s.Harness.Policy.Checked, c => c.Target == Pipeline.WriteTarget.Model && c.Path == s.Harness.Model("model/entities/customer.json"));
    }

    [Fact]
    public async Task Refresh_reports_external_edits_and_deletes_in_every_path_form()
    {
        await using var s = await BillingStore.OpenAsync();
        var customer = s.Doc("entity", "Customer");
        var product = s.Id("entity", "Product");
        s.Harness.Write("model/entities/customer.json", s.Harness.Read("model/entities/customer.json").Replace("Someone we bill.", "Someone we invoice.", StringComparison.Ordinal));
        File.Delete(s.Harness.Model("model/entities/product.json"));

        var edited = await s.Store.RefreshAsync([".maquettiste/model/entities/customer.json"], Ct);
        var deleted = await s.Store.RefreshAsync([s.Harness.Model("model/entities/product.json")], Ct);
        var nothing = await s.Store.RefreshAsync(["model/entities/customer.json", "../outside.json", "/etc/passwd"], Ct);

        Assert.Equal((customer.Element.Id, ChangeSource.Disk), (Assert.Single(edited.Changed).Id, edited.Source));
        Assert.Equal([product], deleted.Deleted);
        Assert.True(nothing.IsEmpty);
        Assert.Equal(2, s.Notifications.Count);
        Assert.Equal("Someone we invoice.", s.Store.Current!.GetDocument(customer.Element.Id)!.Element.Description!.Text);
    }

    [Fact]
    public async Task Refresh_before_the_first_load_loads()
    {
        await using var s = await BillingStore.OpenAsync(load: false);

        var changes = await s.Store.RefreshAsync(["model/entities/customer.json"], Ct);

        Assert.True(changes.IsEmpty);
        Assert.Equal(33, s.Store.Current!.Documents.Count);
    }

    [Fact]
    public async Task Rescan_finds_added_files_and_verify_finds_same_stat_edits()
    {
        await using var s = await BillingStore.OpenAsync();
        var catalog = s.Id("package", "Catalog");
        var entity = new Entity { Id = new SequentialIdGenerator(77).NewId(), Name = "Coupon" };
        s.Harness.Write("model/entities/coupon.json", ModelLoaderTests.Canonical(entity, "model/entities/coupon.json"));

        var added = await s.Store.RescanAsync(false, Ct);
        Assert.Equal([entity.Id], added.Changed.Select(c => c.Id));

        var path = s.Harness.Model("model/packages/catalog.json");
        var stamp = File.GetLastWriteTimeUtc(path);
        s.Harness.Write("model/packages/catalog.json", s.Harness.Read("model/packages/catalog.json").Replace("What we sell.", "What we SELL.", StringComparison.Ordinal));
        File.SetLastWriteTimeUtc(path, stamp);
        Assert.True((await s.Store.RescanAsync(false, Ct)).IsEmpty);
        var verified = await s.Store.RescanAsync(true, Ct);
        Assert.Equal([catalog], verified.Changed.Select(c => c.Id));
        Assert.Equal(2, s.Notifications.Count);
    }

    [Fact]
    public async Task GetSnapshot_rescans_by_stat()
    {
        await using var s = await BillingStore.OpenAsync();
        var first = await s.Store.GetSnapshotAsync(Ct);
        Assert.Same(first, await s.Store.GetSnapshotAsync(Ct));

        s.Harness.Write("model/packages/catalog.json", s.Harness.Read("model/packages/catalog.json").Replace("What we sell.", "What we sell and ship.", StringComparison.Ordinal));
        var second = await s.Store.GetSnapshotAsync(Ct);

        Assert.True(second.Version > first.Version);
        Assert.Equal("What we sell and ship.", second.Get<Package>(s.Id("package", "Catalog"))!.Description!.Text);
    }

    [Fact]
    public async Task Validate_returns_the_validator_report_plus_load_diagnostics_once()
    {
        await using var s = await BillingStore.OpenAsync(load: false);
        s.Harness.Write("model/entities/broken.json", "{");
        var fromValidator = RuleCatalog.Create("MQ3001", "Duplicate.", null, ".maquettiste/model/entities/customer.json", "/name");
        s.Harness.Validator.Rule = (model, _) => [fromValidator, .. model.LoadDiagnostics];

        var report = await s.Store.ValidateAsync(ValidationScope.All, Ct);

        Assert.Equal(["MQ1001", "MQ3001"], report.Diagnostics.Select(d => d.Rule).Order(StringComparer.Ordinal));
        s.Harness.Validator.Rule = (_, _) => [];
        // The whole model's report is kept with its snapshot (ModelStore.ResolvedAsync): a new snapshot validates again.
        s.Harness.Write("model/entities/broken.json", "{ ");
        await s.Store.RescanAsync(false, Ct);
        var withoutLoad = await s.Store.ValidateAsync(ValidationScope.All, Ct);
        Assert.Equal("MQ1001", Assert.Single(withoutLoad.Diagnostics).Rule);
        Assert.Equal(1, withoutLoad.Errors);
        var scoped = await s.Store.ValidateAsync(new ValidationScope([s.Id("entity", "Customer")]), Ct);
        Assert.Empty(scoped.Diagnostics);
    }

    [Fact]
    public async Task A_throwing_handler_does_not_break_the_save_or_other_handlers_and_unsubscribe_works()
    {
        await using var s = await BillingStore.OpenAsync();
        var calls = 0;
        using var failing = s.Store.OnChanged((_, _) => throw new InvalidOperationException("boom"));
        var counting = s.Store.OnChanged((_, _) =>
        {
            Interlocked.Increment(ref calls);
            return ValueTask.CompletedTask;
        });
        var customer = s.Doc("entity", "Customer");

        var result = await s.Store.SaveAsync(customer.Element.Id, BillingStore.Edit(customer, n => n["displayName"] = "Client"), customer.Hash, ChangeSource.Editor, Ct);
        counting.Dispose();
        counting.Dispose();
        await s.Store.SaveAsync(customer.Element.Id, BillingStore.Edit(result.Current!, n => n["displayName"] = "Patron"), result.Hash!, ChangeSource.Editor, Ct);

        Assert.Equal(SaveOutcome.Saved, result.Outcome);
        Assert.Equal(1, calls);
        Assert.Equal(2, s.Notifications.Count);
    }

    [Fact]
    public async Task Concurrent_saves_to_different_elements_all_land()
    {
        await using var s = await BillingStore.OpenAsync();
        var targets = s.Model.All<Entity>();

        var results = await Task.WhenAll(targets.Select(e =>
        {
            var doc = s.Model.GetDocument(e.Id)!;
            return s.Store.SaveAsync(e.Id, BillingStore.Edit(doc, n => n["displayName"] = e.Name + "!"), doc.Hash, ChangeSource.Editor, Ct);
        }));

        Assert.All(results, r => Assert.Equal(SaveOutcome.Saved, r.Outcome));
        Assert.All(targets, e => Assert.Equal(e.Name + "!", s.Store.Current!.Get<Entity>(e.Id)!.DisplayName));
        Assert.Equal(targets.Count, s.Notifications.Count);
    }

    [Fact]
    public async Task A_disposed_store_refuses_calls()
    {
        var s = await BillingStore.OpenAsync();
        await s.Store.DisposeAsync();
        await s.Store.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => s.Store.GetIndexAsync(Ct));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => s.Store.RescanAsync(false, Ct));
        await s.DisposeAsync();
    }

    [Fact]
    public async Task Results_serialize_with_web_defaults()
    {
        await using var s = await BillingStore.OpenAsync();
        var customer = s.Doc("entity", "Customer");
        var result = await s.Store.SaveAsync(customer.Element.Id, BillingStore.Edit(customer, n => n["displayName"] = "Client"), customer.Hash, ChangeSource.Editor, Ct);

        var json = JsonSerializer.Serialize(result, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.Contains("\"outcome\":\"saved\"", json, StringComparison.Ordinal);
        Assert.Contains("\"source\":\"editor\"", json, StringComparison.Ordinal);
        Assert.Contains("\"displayName\":\"Client\"", json, StringComparison.Ordinal);
    }
}
