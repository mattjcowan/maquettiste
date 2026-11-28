using System.Runtime.CompilerServices;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Generation;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Pipeline;
using Maquettiste.Engine.Planning;
using Maquettiste.Engine.Resolution;

namespace Maquettiste.Engine.Tests.Integration;

/// <summary>
/// An edit in a long-lived host (the editor, <c>watch</c>) costs one model, not two, and its incremental runs reuse what the stores
/// already hold without changing a byte (change-driven-edits-design.md, step 1): the previous snapshot and resolved model are
/// collectable once the store publishes the next snapshot, and after an edit sequence the outputs, manifests and unit states equal a
/// cold run's.
/// </summary>
[Collection(IntegrationCollection.Name)]
public sealed class EditMemoryTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string NewEntityJson = """
        {"kind": "entity", "name": "Note", "package": "01J92P0V01KDRN8GX5PGYCNKSX", "key": {"attributes": ["01J92P0VZZ0000000000000101"]},
         "attributes": [{"id": "01J92P0VZZ0000000000000101", "name": "id", "type": "uuid"}, {"name": "text", "type": "string"}]}
        """;

    [Fact]
    public async Task After_an_edit_and_an_incremental_run_the_previous_model_is_collectable()
    {
        await using var repo = E2ERepo.Create();
        await repo.ApplyAsync();
        // The editor's reads of the same snapshot: a preview session and a database view hold the shared resolved model too.
        Assert.NotEmpty((await repo.Service.PreviewAsync("e2e", "entity", E2ERepo.CustomerId, Ct)).Files);
        Assert.NotNull((await repo.Service.GetDatabaseViewAsync(E2ERepo.MainDatabaseId, Ct)).View);
        var previous = await HoldWeaklyAsync(repo.Store);

        await repo.WidenCustomerNameAsync();
        var result = await repo.ApplyAsync();
        Assert.True(result.UnitsRendered > 0);

        Collect();
        Assert.False(previous.Model.IsAlive, "the previous resolved model is still reachable");
        Assert.False(previous.Snapshot.IsAlive, "the previous snapshot is still reachable");
        GC.KeepAlive(repo);
    }

    [Fact]
    public async Task An_edit_sequence_ends_with_the_outputs_manifests_and_unit_states_of_a_cold_run()
    {
        await using var repo = E2ERepo.Create();
        await repo.ApplyAsync();

        // Change an entity, add one, delete it again, then change another: an incremental run after each.
        await repo.WidenCustomerNameAsync();
        await repo.ApplyAsync();
        var created = await repo.CreateElementAsync(NewEntityJson);
        var added = await repo.ApplyAsync();
        Assert.True(added.UnitsRendered > 0);
        Assert.Contains(added.Changes, c => c.Kind == FileChangeKind.Added);
        var deleted = await repo.Store.DeleteAsync(created.Id!, created.Hash!, DeleteResolution.Refuse, ChangeSource.Editor, Ct);
        Assert.Equal(SaveOutcome.Saved, deleted.Outcome);
        var removed = await repo.ApplyAsync();
        Assert.Contains(removed.Changes, c => c.Kind == FileChangeKind.Deleted);
        await repo.EditAsync(E2ERepo.ProductId, n => n["description"] = "Changed.");
        await repo.ApplyAsync();

        var incremental = repo.Tree();
        var incrementalStates = StateFiles(repo);
        Assert.NotEmpty(incrementalStates);

        // The files a store writes by patching equal a fresh encoding of what they hold.
        foreach (var (name, bytes) in incrementalStates)
            Assert.True(bytes.AsSpan().SequenceEqual(UnitStateStore.Encode([.. UnitStateStore.Decode(bytes)!.Values])), name + " is not a fresh encoding");

        // A cold run of the same model: no outputs, no manifests, no cache, a new store and service.
        foreach (var path in incremental.Keys.Where(p => !p.StartsWith(".maquettiste/snapshots/", StringComparison.Ordinal)))
            File.Delete(repo.Repo.PathOf(path));
        Directory.Delete(repo.Repo.CacheDirectory, recursive: true);
        Directory.CreateDirectory(repo.Repo.CacheDirectory);
        await repo.RestartAsync();
        var cold = await repo.ApplyAsync();
        Assert.Equal(cold.UnitsRendered, cold.UnitsRendered + cold.UnitsSkipped);

        // The deleted entity's files a user owns (once and pair companions) stay where they were, as orphans the run keeps and its
        // manifest still lists; a cold run never wrote them. Without them, everything, the manifests included, is byte for byte the same.
        var coldTree = repo.Tree();
        var leftovers = incremental.Keys.Except(coldTree.Keys).ToList();
        Assert.NotEmpty(leftovers);
        Assert.All(leftovers, path => Assert.Contains("note", path, StringComparison.OrdinalIgnoreCase));
        foreach (var path in leftovers)
            incremental.Remove(path);
        foreach (var manifest in incremental.Keys.Where(p => p.StartsWith(".maquettiste/manifest/", StringComparison.Ordinal)).ToList())
        {
            var lines = System.Text.Encoding.UTF8.GetString(incremental[manifest]).Split('\n')
                .Where(line => !leftovers.Any(path => line.StartsWith("    [\"" + path + "\"", StringComparison.Ordinal)));
            incremental[manifest] = System.Text.Encoding.UTF8.GetBytes(string.Join('\n', lines).Replace(",\n  ]", "\n  ]", StringComparison.Ordinal));
        }

        FullRunTests.AssertSameBytes(incremental, coldTree);
        var coldStates = StateFiles(repo);
        Assert.Equal(incrementalStates.Keys, coldStates.Keys);
        foreach (var (name, bytes) in coldStates)
            Assert.True(WithoutTimes(incrementalStates[name]).AsSpan().SequenceEqual(WithoutTimes(bytes)), name + " differs from the cold run's");
    }

    [Fact]
    public async Task Read_only_views_of_one_snapshot_share_one_validation_and_resolution()
    {
        await using var repo = E2ERepo.Create();
        var services = EngineServices.Create(repo.Options);
        var resolver = new CountingResolver(services.Resolver);
        var counted = services with { Resolver = resolver };
        await using var store = new ModelStore(repo.Options, counted);
        var service = new GenerationService(store, repo.Options, counted);

        var first = await service.GetDatabaseViewAsync(E2ERepo.MainDatabaseId, Ct);
        var second = await service.GetDatabaseViewAsync(E2ERepo.MainDatabaseId, Ct);
        var status = await store.GetMaterializeStatusAsync(E2ERepo.MainDatabaseId, Ct);
        var missing = await service.GetDatabaseViewAsync("01J92P0VZZ00000000000000ZZ", Ct);
        var query = await service.GetQuerySqlAsync(InvoicesByCustomerQueryId, null, null, Ct);
        var binding = await service.GetBindingSqlAsync(E2ERepo.CustomerId, CustomerBindingId, null, null, Ct);
        Assert.Equal(1, resolver.Calls);
        Assert.NotNull(query.Preview);
        Assert.NotNull(binding.Preview);
        Assert.NotNull(first.View);
        Assert.NotNull(second.View);
        Assert.NotNull(status);
        Assert.Null(missing.View);
        Assert.Equal("MQ6017", missing.Diagnostics[^1].Rule);

        // The diagnostics are the ones a validation and a resolution of their own give, sorted.
        var snapshot = store.Current!;
        var report = await services.Validator.ValidateAsync(snapshot, ValidationScope.Previews, null, Ct);
        var resolved = await services.Resolver.ResolveAsync(snapshot, null, Ct);
        Assert.Equal(Outcomes.Sort(report.Diagnostics.Concat(resolved.Diagnostics)), first.Diagnostics);
        Assert.Equal(first.Diagnostics, second.Diagnostics);
    }

    private const string InvoicesByCustomerQueryId = "01K6QRY0000000000000000001";
    private const string CustomerBindingId = "01M8EJPZNRW3DF4MD51RVRWD77";

    [Fact]
    public async Task A_result_of_an_older_snapshot_is_handed_out_but_never_stored()
    {
        await using var repo = E2ERepo.Create();
        var services = EngineServices.Create(repo.Options);
        var resolver = new CountingResolver(services.Resolver);
        var validator = new GatedValidator(services.Validator);
        var gated = services with { Resolver = resolver, Validator = validator };
        await using var store = new ModelStore(repo.Options, gated);
        var old = await store.GetSnapshotAsync(Ct);

        // The shared validation of the old snapshot is still running when an edit publishes the next one.
        validator.Close();
        var inFlight = store.ResolvedAsync(old, Ct);
        await validator.Entered.WaitAsync(Ct);
        await EditCustomerAsync(store);
        var current = store.Current!;
        Assert.NotSame(old, current);
        validator.Open();

        // Its caller still gets the old model, but the store keeps nothing of the old snapshot.
        Assert.NotNull((await inFlight).Model);
        Assert.Null(store.SharedModel(old));

        // A reader still holding the old snapshot (a preview) gets its result without evicting the current snapshot's.
        var shared = await store.ResolvedAsync(current, Ct);
        var calls = resolver.Calls;
        Assert.NotNull((await store.ResolvedAsync(old, Ct)).Model);
        Assert.Equal(calls + 1, resolver.Calls);
        Assert.Same(shared, await store.ResolvedAsync(current, Ct));
        Assert.Equal(calls + 1, resolver.Calls);
        Assert.Null(store.SharedModel(old));

        // A generation run of the old snapshot finishing late shares nothing either.
        store.ShareModel(old, (await inFlight).Model!);
        Assert.Same(shared.Model, await store.SharedModel(current)!);
    }

    [Fact]
    public async Task A_preview_started_while_a_run_resolves_waits_for_the_runs_resolution()
    {
        await using var repo = E2ERepo.Create();
        var services = EngineServices.Create(repo.Options);
        var resolver = new GatedResolver(services.Resolver);
        var gated = services with { Resolver = resolver };
        await using var store = new ModelStore(repo.Options, gated);
        var service = new GenerationService(store, repo.Options, gated);

        // The run's resolution waits at the gate; a preview of the same snapshot arrives meanwhile.
        resolver.Close();
        var run = service.RunAsync(new GenerationRequest { StageBarriers = false, Jobs = 2 }, null, Ct);
        await resolver.Entered.WaitAsync(Ct);
        await RunResolutionSharedAsync(store);
        var preview = service.PreviewAsync("e2e", "entity", E2ERepo.CustomerId, Ct);
        resolver.Open();

        Assert.Equal(RunOutcome.Succeeded, (await run).Outcome);
        Assert.NotEmpty((await preview).Files);
        Assert.Equal(1, resolver.Calls);
        Assert.Same(await store.SharedModel(store.Current!)!, (await store.ResolvedAsync(store.Current!, Ct)).Model);
        Assert.Equal(1, resolver.Calls);
    }

    [Fact]
    public async Task A_preview_waiting_for_a_cancelled_runs_resolution_resolves_on_its_own()
    {
        await using var repo = E2ERepo.Create();
        var services = EngineServices.Create(repo.Options);
        var resolver = new GatedResolver(services.Resolver);
        var gated = services with { Resolver = resolver };
        await using var store = new ModelStore(repo.Options, gated);
        var service = new GenerationService(store, repo.Options, gated);

        resolver.Close();
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var run = service.RunAsync(new GenerationRequest { StageBarriers = false, Jobs = 2 }, null, cancel.Token);
        await resolver.Entered.WaitAsync(Ct);
        await RunResolutionSharedAsync(store);
        var preview = service.PreviewAsync("e2e", "entity", E2ERepo.CustomerId, Ct);

        // The run is cancelled while it resolves: the preview does not fail with it, it resolves the snapshot itself.
        await cancel.CancelAsync();
        Assert.Equal(RunOutcome.Cancelled, (await run).Outcome);
        resolver.Open();
        var files = (await preview).Files;

        Assert.NotEmpty(files);
        Assert.Equal(2, resolver.Calls);
        Assert.NotNull((await store.ResolvedAsync(store.Current!, Ct)).Model);
        Assert.Equal(2, resolver.Calls);
    }

    /// <summary>Waits until the store holds the run's resolution of its current snapshot (the run registers it as it starts it).</summary>
    private static async Task RunResolutionSharedAsync(ModelStore store)
    {
        for (var i = 0; i < 1_000 && (store.Current is not { } current || store.SharedModel(current) is null); i++)
            await Task.Delay(10, Ct);
        Assert.NotNull(store.SharedModel(store.Current!));
    }

    /// <summary>A resolver whose resolutions wait while it is closed (cancellable), counting every call.</summary>
    private sealed class GatedResolver(IModelResolver inner) : IModelResolver
    {
        private volatile TaskCompletionSource _gate = Opened();
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public SemaphoreSlim Entered { get; } = new(0);

        public void Close() => _gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Open() => _gate.TrySetResult();

        public async Task<ResolvedModel> ResolveAsync(ModelSnapshot model, IProgress<ProgressUpdate>? progress, CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            if (!_gate.Task.IsCompleted)
            {
                Entered.Release();
                await _gate.Task.WaitAsync(ct);
            }

            return await inner.ResolveAsync(model, progress, ct);
        }

        private static TaskCompletionSource Opened()
        {
            var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            source.SetResult();
            return source;
        }
    }

    private static async Task EditCustomerAsync(ModelStore store)
    {
        var customer = (await store.GetElementAsync(E2ERepo.CustomerId, Ct))!;
        var node = System.Text.Json.Nodes.JsonNode.Parse(customer.Json.GetRawText())!.AsObject();
        node["description"] = "Changed.";
        var saved = await store.SaveAsync(E2ERepo.CustomerId, System.Text.Encoding.UTF8.GetBytes(node.ToJsonString()), customer.Hash, ChangeSource.Editor, Ct);
        Assert.Equal(SaveOutcome.Saved, saved.Outcome);
    }

    /// <summary>A validator whose shared validations (previews scope) wait while it is closed; a save's validation never waits.</summary>
    private sealed class GatedValidator(IModelValidator inner) : IModelValidator
    {
        private volatile TaskCompletionSource _gate = Opened();

        public SemaphoreSlim Entered { get; } = new(0);

        public void Close() => _gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Open() => _gate.TrySetResult();

        public async Task<ValidationReport> ValidateAsync(ModelSnapshot model, ValidationScope scope, IProgress<ProgressUpdate>? progress, CancellationToken ct)
        {
            if (scope.DeferScriptedRows && !_gate.Task.IsCompleted)
            {
                Entered.Release();
                await _gate.Task.WaitAsync(ct);
            }

            return await inner.ValidateAsync(model, scope, progress, ct);
        }

        private static TaskCompletionSource Opened()
        {
            var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            source.SetResult();
            return source;
        }
    }

    private sealed class CountingResolver(IModelResolver inner) : IModelResolver
    {
        public int Calls;

        public Task<ResolvedModel> ResolveAsync(ModelSnapshot model, IProgress<ProgressUpdate>? progress, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            return inner.ResolveAsync(model, progress, ct);
        }
    }

    /// <summary>The state files of the repo's cache folder, by name.</summary>
    private static SortedDictionary<string, byte[]> StateFiles(E2ERepo repo)
    {
        var files = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
        var folder = Path.Combine(repo.Repo.CacheDirectory, "units");
        foreach (var file in Directory.EnumerateFiles(folder, "*.bin"))
            files[Path.GetFileName(file)] = File.ReadAllBytes(file);
        return files;
    }

    /// <summary>A state file's states encoded again with every output's write time cleared (two runs write the same files at other times).</summary>
    private static byte[] WithoutTimes(byte[] bytes) =>
        UnitStateStore.Encode([.. UnitStateStore.Decode(bytes)!.Values.Select(s => s with
        {
            ReadKeys = [.. s.ReadKeys],
            Outputs = [.. s.Outputs.Select(o => o with { LastWriteUtcTicks = 0 })],
        })]);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<(WeakReference Snapshot, WeakReference Model)> HoldWeaklyAsync(ModelStore store)
    {
        var snapshot = store.Current!;
        ResolvedModel model = await store.SharedModel(snapshot)!;
        return (new WeakReference(snapshot), new WeakReference(model));
    }

    private static void Collect()
    {
        for (var i = 0; i < 3; i++)
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
        }
    }
}
