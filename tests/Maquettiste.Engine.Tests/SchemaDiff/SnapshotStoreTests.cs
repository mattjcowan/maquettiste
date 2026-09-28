using System.Text;
using System.Text.Json;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Resolution;
using Maquettiste.Engine.SchemaDiff;
using Maquettiste.Engine.Tests.PostProcessing;
using Maquettiste.Testing;
using static Maquettiste.Engine.Tests.SchemaDiff.PhysicalBuilder;

namespace Maquettiste.Engine.Tests.SchemaDiff;

public sealed class SnapshotStoreTests : IDisposable
{
    private readonly TempRepo _repo = new();

    public void Dispose() => _repo.Dispose();

    private SnapshotStore Store(FakePathPolicy? paths = null) => new(_repo.Options, TestServices.Json, paths ?? new FakePathPolicy(_repo.ModelRoot));

    private static PhysicalSnapshot Snapshot(string name = "MainDB", int revision = 2) => new SchemaDiffer().Capture(Model(name), revision);

    private static RDatabase Model(string name = "MainDB")
    {
        var customer = Table("01JB2Q0M8X4T5V6W7Y8Z9A0B1B", "customer", Column("id", "id", "uuid"), Column("a-name", "name", "string", nullable: true));
        customer.Col("a-name").Default = "anonymous";
        customer.Col("a-name").Comment = "Display name";
        customer.Indexes = [new RIndex { Name = "ix_customer_name", Columns = [new RIndexColumn { Column = customer.Col("a-name"), Descending = true }] }];
        return Database(name, [customer], [View("01JB2Q0M8X4T5V6W7Y8Z9A0B1D", "v", "SELECT 1")], [Sequence("01JB2Q0M8X4T5V6W7Y8Z9A0B1E", "s", 5)]);
    }

    private static string Json(PhysicalSnapshot snapshot) => JsonSerializer.Serialize(snapshot, EngineJson.Options);

    [Fact]
    public async Task Save_then_load_round_trips_in_canonical_json()
    {
        var store = Store();
        var snapshot = Snapshot();

        await store.SaveAsync(snapshot, TestContext.Current.CancellationToken);
        var loaded = await store.LoadAsync("MainDB", TestContext.Current.CancellationToken);

        Assert.True(_repo.Exists(".maquettiste/snapshots/main-db.json"));
        Assert.NotNull(loaded);
        Assert.Equal(Json(snapshot with { SchemaPath = "../.schema/v1/snapshot.json" }), Json(loaded));

        var bytes = File.ReadAllBytes(_repo.PathOf(".maquettiste/snapshots/main-db.json"));
        Assert.True(TestServices.Json.IsCanonical(bytes, "snapshot.json", "snapshots/main-db.json"));
        var text = Encoding.UTF8.GetString(bytes);
        Assert.StartsWith("{\n  \"$schema\": \"../.schema/v1/snapshot.json\",\n  \"database\": ", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\r", text, StringComparison.Ordinal);
        Assert.Contains("\"key\": \"ix:a-name desc\"", text, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(bytes);
        Assert.Empty(TestServices.Schemas.Evaluate("snapshot.json", document.RootElement, ".maquettiste/snapshots/main-db.json"));

        // A loaded snapshot diffs empty against the model it was captured from.
        var diff = new SchemaDiffer().Diff(loaded, Model());
        Assert.True(diff.IsEmpty);
        Assert.Equal((2, 2), (diff.FromRevision, diff.ToRevision));
    }

    [Fact]
    public async Task Missing_snapshot_loads_as_null()
    {
        Assert.Null(await Store().LoadAsync("main", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Save_goes_through_the_engine_write_guard()
    {
        var refusing = new FakePathPolicy(engineRoot: null);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Store(refusing).SaveAsync(Snapshot(), TestContext.Current.CancellationToken));

        Assert.NotEmpty(refusing.EngineChecks);
        Assert.False(Directory.Exists(Path.Combine(_repo.ModelRoot, "snapshots")));

        var allowing = new FakePathPolicy(_repo.ModelRoot);
        await Store(allowing).SaveAsync(Snapshot(), TestContext.Current.CancellationToken);
        Assert.Contains(Path.Combine(_repo.ModelRoot, "snapshots", "main-db.json"), allowing.EngineChecks);
        Assert.Contains(allowing.EngineChecks, p => Path.GetFileName(p).EndsWith(".tmp", StringComparison.Ordinal));
        Assert.Equal([".maquettiste/snapshots/main-db.json"], _repo.ListFiles());
    }

    [Fact]
    public async Task Identical_save_does_not_rewrite_the_file()
    {
        var store = Store();
        await store.SaveAsync(Snapshot(), TestContext.Current.CancellationToken);
        var path = _repo.PathOf(".maquettiste/snapshots/main-db.json");
        var old = new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(path, old);

        await store.SaveAsync(Snapshot(), TestContext.Current.CancellationToken);
        Assert.Equal(old, File.GetLastWriteTimeUtc(path));

        await store.SaveAsync(Snapshot(revision: 3), TestContext.Current.CancellationToken);
        Assert.NotEqual(old, File.GetLastWriteTimeUtc(path));
        Assert.Equal(3, (await store.LoadAsync("MainDB", TestContext.Current.CancellationToken))!.Revision);
    }

    [Fact]
    public async Task Save_sorts_keyed_lists()
    {
        var snapshot = Snapshot() with
        {
            Views =
            [
                new SnapshotView { Key = "B", Name = "b", Body = "SELECT 2" },
                new SnapshotView { Key = "A", Name = "a", Body = "SELECT 1" },
            ],
        };

        await Store().SaveAsync(snapshot, TestContext.Current.CancellationToken);
        var loaded = await Store().LoadAsync("MainDB", TestContext.Current.CancellationToken);

        Assert.Equal(["A", "B"], loaded!.Views.Select(v => v.Key));
    }

    [Fact]
    public async Task Invalid_snapshot_is_reported_with_its_path()
    {
        _repo.WriteFile(".maquettiste/snapshots/main.json", "{ not json");

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => Store().LoadAsync("main", TestContext.Current.CancellationToken));

        Assert.Contains(".maquettiste/snapshots/main.json", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("main", "main.json")]
    [InlineData("MainDB", "main-db.json")]
    [InlineData("HTTPServer2Id", "http-server2-id.json")]
    [InlineData("Billing Reports", "billing-reports.json")]
    [InlineData("reporting_v2", "reporting-v2.json")]
    [InlineData("--", "database.json")]
    public void Snapshot_file_name_is_the_kebab_case_database_name(string name, string expected)
    {
        Assert.Equal(expected, SnapshotStore.FileNameOf(name));
    }
}
