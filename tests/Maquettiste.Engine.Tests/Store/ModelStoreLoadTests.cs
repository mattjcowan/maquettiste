using Maquettiste.Engine.Loading;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Pipeline;
using Maquettiste.Engine.Tests.Loading;

namespace Maquettiste.Engine.Tests.Store;

public sealed class ModelStoreLoadTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Forwards to a loader and cancels a token after a number of files.</summary>
    private sealed class CancellingLoader(IModelLoader inner, CancellationTokenSource cts, int afterFiles) : IModelLoader
    {
        public Task<LoadResult> LoadAsync(LoadRequest request, IProgress<ProgressUpdate>? progress, CancellationToken ct)
        {
            var seen = 0;
            var cancelling = new SyncProgress(_ =>
            {
                if (Interlocked.Increment(ref seen) == afterFiles)
                    cts.Cancel();
            });
            return inner.LoadAsync(request, cancelling, ct);
        }
    }

    [Fact]
    public async Task A_5000_entity_model_reopens_through_the_index_cache()
    {
        using var h = new LoaderHarness(parallelism: 4);
        await IndexCacheTests.WriteLargeModelAsync(h.Repo.ModelRoot, 5000);

        var first = h.NewLoader();
        await using (var store = h.NewStore(first))
        {
            Assert.Equal(5000, (await store.GetIndexAsync(Ct)).Count);
            Assert.Equal(5001, first.LastStatistics.ReadFromDisk);
        }

        var second = h.NewLoader();
        await using var reopened = h.NewStore(second);
        var index = await reopened.GetIndexAsync(Ct);

        Assert.Equal(5000, index.Count);
        Assert.Equal(0, second.LastStatistics.ReadFromDisk);
        Assert.Equal(0, second.LastStatistics.SchemaEvaluations);
        Assert.Equal(5001, second.LastStatistics.FromCache);

        // A save on the reopened store touches one file; the next open re-reads just that one.
        var entity = reopened.Current!.All<Entity>()[1234];
        var document = reopened.Current.GetDocument(entity.Id)!;
        var saved = await reopened.SaveAsync(entity.Id, BillingStore.Edit(document, n => n["displayName"] = "Renamed"), document.Hash, ChangeSource.Editor, Ct);
        Assert.Equal(SaveOutcome.Saved, saved.Outcome);
        await reopened.DisposeAsync();

        var third = h.NewLoader();
        await using var again = h.NewStore(third);
        await again.LoadAsync(Ct);
        Assert.Equal(1, third.LastStatistics.ReadFromDisk);
        Assert.Equal("Renamed", again.Current!.Get<Entity>(entity.Id)!.DisplayName);
    }

    [Fact]
    public async Task Cancelling_the_first_load_leaves_the_store_unloaded_and_retryable()
    {
        using var h = new LoaderHarness(parallelism: 1);
        h.CopyFixture("models", "billing");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        await using var store = h.NewStore(new CancellingLoader(h.NewLoader(), cts, 3));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.LoadAsync(cts.Token));

        Assert.Null(store.Current);
        await store.LoadAsync(Ct);
        Assert.Equal(26, store.Current!.Documents.Count);
    }

    [Fact]
    public async Task A_cancelled_save_writes_nothing()
    {
        await using var s = await BillingStore.OpenAsync();
        var customer = s.Doc("entity", "Customer");
        var before = s.Files();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            s.Store.SaveAsync(customer.Element.Id, BillingStore.Edit(customer, n => n["displayName"] = "Client"), customer.Hash, ChangeSource.Editor, cts.Token));

        Assert.Equal(before, s.Files());
        Assert.Same(customer, s.Store.Current!.GetDocument(customer.Element.Id));
    }

    [Fact]
    public async Task A_table_moved_to_another_database_follows_it()
    {
        await using var s = await BillingStore.OpenAsync();
        var reporting = await s.Store.CreateAsync("{\"kind\":\"database\",\"name\":\"Reporting\",\"dialect\":\"sqlite\"}"u8.ToArray(), ChangeSource.Editor, Ct);
        var sequence = s.Doc("sequence", "invoice_number_seq");

        var moved = await s.Store.SaveAsync(
            sequence.Element.Id,
            BillingStore.Edit(sequence, n => { n["database"] = reporting.Id; n.Remove("schema"); }),
            sequence.Hash,
            ChangeSource.Editor,
            Ct);

        Assert.Equal(SaveOutcome.Saved, moved.Outcome);
        Assert.Equal(".maquettiste/model/databases/reporting/sequences/invoice-number-seq.json", moved.Current!.Path);
        Assert.False(s.Harness.Exists("model/databases/main/sequences/invoice-number-seq.json"));
        Assert.Empty(s.Store.Current!.LoadDiagnostics);
    }

    [Fact]
    public async Task Paths_convert_between_absolute_repo_and_model_forms()
    {
        using var h = new LoaderHarness();
        var paths = new ModelPaths(h.Options);

        Assert.Equal(".maquettiste", paths.Prefix);
        Assert.Equal("model/entities/a.json", paths.ToModelPath(".maquettiste/model/entities/a.json"));
        Assert.Equal("model/entities/a.json", paths.ToModelPath("model/entities/a.json"));
        Assert.Equal("model/entities/a.json", paths.ToModelPath(Path.Combine(h.Repo.ModelRoot, "model", "entities", "a.json")));
        Assert.Equal("", paths.ToModelPath(h.Repo.ModelRoot));
        Assert.Null(paths.ToModelPath("../elsewhere.json"));
        Assert.Null(paths.ToModelPath(Path.GetTempPath()));
        Assert.Equal("model/entities/invoice.md", ModelPaths.ResolveSidecar("model/entities/invoice.json", "invoice.md"));
        Assert.Equal("model/docs/invoice.md", ModelPaths.ResolveSidecar("model/entities/invoice.json", "../docs/invoice.md"));
        Assert.Null(ModelPaths.ResolveSidecar("model/entities/invoice.json", "../../../x.md"));
        Assert.Null(ModelPaths.ResolveSidecar("model/entities/invoice.json", "/etc/x.md"));
        Assert.Equal(["invoice-line", "http-server", "is-member-of", "v2-api"], new[] { "InvoiceLine", "HTTPServer", "is member of", "v2 API" }.Select(ModelPaths.Kebab));
    }
}
