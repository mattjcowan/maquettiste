using System.Text.Json;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Resolution;
using Maquettiste.Engine.SchemaDiff;
using Maquettiste.Engine.Tests.PostProcessing;
using Maquettiste.Testing;
using static Maquettiste.Engine.Tests.SchemaDiff.PhysicalBuilder;

namespace Maquettiste.Engine.Tests.SchemaDiff;

/// <summary>
/// The incremental-run paths of the snapshot store and the differ (WA): a snapshot serialized ahead of the write, the parsed
/// snapshot reused while its file is unchanged, the parallel capture and the unchanged-table shortcut, each against the plain path.
/// </summary>
public sealed class SnapshotReuseTests : IDisposable
{
    private readonly TempRepo _repo = new();

    public void Dispose() => _repo.Dispose();

    private SnapshotStore Store(FakePathPolicy? paths = null) => new(_repo.Options, TestServices.Json, paths ?? new FakePathPolicy(_repo.ModelRoot));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string SnapshotPath => _repo.PathOf(".maquettiste/snapshots/main.json");

    private static string Json(object? value) => JsonSerializer.Serialize(value, EngineJson.Options);

    /// <summary>A chain of tables, each referencing the one before it, with a unique constraint and a check on every table.</summary>
    private static RDatabase Chain(int count, Action<List<RTable>>? change = null)
    {
        var tables = new List<RTable>();
        for (var i = 0; i < count; i++)
        {
            var key = "01JB2Q0M8X4T5V6W7Y" + i.ToString("D8", System.Globalization.CultureInfo.InvariantCulture);
            var table = Table(key, "t" + i.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Column("id", "id"), Column("a-code", "code", "string"), Column("a-note", "note", "string", nullable: true), Column("prev.id", "prev_id"));
            table.Uniques = [new RUnique { Name = "uq_" + table.Name + "_code", Columns = [table.Col("a-code")] },
                new RUnique { Name = "uq_" + table.Name + "_note", Columns = [table.Col("a-note")] }];
            table.Checks = [new RCheck { Name = "ck_" + table.Name, Expression = "code <> ''" }];
            if (i > 0)
                table.ForeignKeys = [ForeignKey(table, "prev.id", tables[i - 1])];
            tables.Add(table);
        }

        change?.Invoke(tables);
        return Database("main", tables);
    }

    [Fact]
    public async Task Prepared_bytes_are_what_save_writes_and_write_goes_through_the_guard()
    {
        var snapshot = new SchemaDiffer().Capture(Chain(3), 4);
        await Store().SaveAsync(snapshot, Ct);
        var saved = File.ReadAllBytes(SnapshotPath);

        var prepared = Store().Prepare(snapshot, parse: false, Ct);
        Assert.Equal(saved, prepared.Bytes);
        Assert.Null(prepared.Parsed);

        File.Delete(SnapshotPath);
        var refusing = new FakePathPolicy(engineRoot: null);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Store(refusing).WriteAsync(prepared, Ct));
        Assert.False(File.Exists(SnapshotPath));

        var store = Store();
        await store.WriteAsync(prepared, Ct);
        Assert.Equal(saved, File.ReadAllBytes(SnapshotPath));
        Assert.Null(store.Held("main"));
    }

    [Fact]
    public async Task A_load_reuses_the_parsed_snapshot_until_the_file_changes()
    {
        var store = Store();
        var prepared = store.Prepare(new SchemaDiffer().Capture(Chain(3), 1), parse: true, Ct);
        Assert.NotNull(prepared.Parsed);
        Assert.Null(store.Held("main")); // a prepared snapshot that is never written is never held
        await store.WriteAsync(prepared, Ct);

        // The bytes parsed back beside the run are held for the written file; the load returns that object without parsing again.
        Assert.Same(prepared.Parsed, store.Held("main"));
        var first = await store.LoadAsync("main", Ct);
        Assert.Same(await prepared.Parsed, first);
        Assert.Equal(Json(await Store().LoadAsync("main", Ct)), Json(first));
        Assert.Same(first, await store.LoadAsync("main", Ct));

        // Another writer (a checkout, a hand edit, another process) changes the file: the next load parses it.
        await Store().SaveAsync(new SchemaDiffer().Capture(Chain(4), 2), Ct);
        var changed = await store.LoadAsync("main", Ct);
        Assert.NotSame(first, changed);
        Assert.Equal((2, 4), (changed!.Revision, changed.Tables.Count));
        Assert.Same(changed, await store.LoadAsync("main", Ct));

        // A missing file drops what the store held for it.
        File.Delete(SnapshotPath);
        Assert.Null(await store.LoadAsync("main", Ct));
        Assert.Null(store.Held("main"));
    }

    [Fact]
    public async Task A_held_parse_that_failed_or_was_cancelled_is_replaced_by_parsing_the_file()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        var snapshot = new SchemaDiffer().Capture(Chain(3), 2);
        Task<PhysicalSnapshot>[] broken = [Task.FromCanceled<PhysicalSnapshot>(cancelled.Token), Task.FromException<PhysicalSnapshot>(new InvalidDataException("x"))];
        foreach (var parse in broken)
        {
            var store = Store();
            await store.WriteAsync(store.Prepare(snapshot, parse: false, Ct) with { Parsed = parse }, Ct);
            var loaded = await store.LoadAsync("main", Ct);
            Assert.NotNull(loaded);
            Assert.Equal(Json(await Store().LoadAsync("main", Ct)), Json(loaded));
            Assert.Same(loaded, await store.LoadAsync("main", Ct));
        }
    }

    [Fact]
    public async Task Retain_keeps_only_the_named_databases_snapshots()
    {
        var store = Store();
        await store.SaveAsync(new SchemaDiffer().Capture(Chain(2), 1), Ct);
        await store.SaveAsync(new SchemaDiffer().Capture(Database("OtherDb", []), 1), Ct);
        Assert.NotNull(await store.LoadAsync("main", Ct));
        Assert.NotNull(await store.LoadAsync("OtherDb", Ct));

        store.Retain(["main"]);
        Assert.NotNull(store.Held("main"));
        Assert.Null(store.Held("OtherDb"));

        store.Retain([]);
        Assert.Null(store.Held("main"));
        Assert.NotNull(await store.LoadAsync("main", Ct));
    }

    [Fact]
    public async Task Prepare_capture_and_compare_observe_cancellation()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        var differ = new SchemaDiffer();
        var previous = differ.Capture(Chain(600), 1);
        var current = Chain(600);

        Assert.ThrowsAny<OperationCanceledException>(() => Store().Prepare(previous, parse: true, cancelled.Token));
        Assert.ThrowsAny<OperationCanceledException>(() => differ.DiffAndCapture(previous, current, parallelism: 8, cancelled.Token));
        Assert.ThrowsAny<OperationCanceledException>(() => SchemaDiffer.Compare(previous, previous, current, parallelism: 8, cancelled.Token));
        Assert.False(File.Exists(SnapshotPath));
    }

    [Fact]
    public void Parallel_capture_and_compare_match_the_sequential_ones()
    {
        var differ = new SchemaDiffer();
        var previous = differ.Capture(Chain(700), 3);
        var current = Chain(700, tables =>
        {
            tables[5].Col("a-note").Nullable = false;
            tables[650].Name = "renamed";
            tables[699].Checks = [];
        });

        var sequential = differ.DiffAndCapture(previous, current, parallelism: 1, Ct);
        var parallel = differ.DiffAndCapture(previous, current, parallelism: 8, Ct);
        var plain = differ.Diff(previous, current);

        Assert.Equal(Json(differ.Capture(current, 3)), Json(sequential.Current));
        Assert.Equal(Json(sequential.Current), Json(parallel.Current));
        Assert.Equal(sequential.Diff.Hash, parallel.Diff.Hash);
        Assert.Equal(plain.Hash, parallel.Diff.Hash);
        Assert.Equal(4, parallel.Diff.ToRevision);
        Assert.Equal(
            [(ChangeKind.Renamed, "01JB2Q0M8X4T5V6W7Y00000650"), (ChangeKind.Altered, "01JB2Q0M8X4T5V6W7Y00000005"), (ChangeKind.Altered, "01JB2Q0M8X4T5V6W7Y00000699")],
            parallel.Diff.Tables.Select(t => (t.Kind, t.Key)));

        var same = differ.DiffAndCapture(differ.Capture(current, 4), current, parallelism: 8, Ct);
        Assert.True(same.Diff.IsEmpty);
    }

    [Fact]
    public void A_table_whose_lists_are_only_reordered_is_unchanged()
    {
        // The pairwise shortcut fails on a reordering; the keyed comparison behind it must still find no change.
        var differ = new SchemaDiffer();
        var current = Chain(2);
        var captured = differ.Capture(current, 1);
        var reordered = captured with
        {
            Tables = [.. captured.Tables.Select(t => t with { Uniques = [.. t.Uniques.Reverse()], Columns = [.. t.Columns.Reverse()] })],
        };

        var diff = differ.Diff(reordered, current);

        Assert.True(diff.IsEmpty);
        Assert.Equal(differ.Diff(captured, current).Hash, diff.Hash);
    }
}
