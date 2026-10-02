using System.Diagnostics;
using System.Text;
using Maquettiste.Engine.Loading;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Pipeline;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Loading;

public sealed class IndexCacheTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<LoadResult> LoadAsync(ModelLoader loader, bool verify = false) =>
        await loader.LoadAsync(new LoadRequest(null, null, verify), null, Ct);

    [Fact]
    public async Task A_full_load_writes_the_cache_through_the_engine_write_guard()
    {
        using var h = new LoaderHarness();
        h.CopyFixture("models", "billing");
        var loader = h.NewLoader();

        await LoadAsync(loader);

        Assert.True(loader.LastStatistics.CacheWritten);
        Assert.Equal(Path.Combine(h.Repo.CacheDirectory, "index.v1.bin"), loader.CachePath);
        Assert.True(File.Exists(loader.CachePath));
        Assert.Contains(h.Policy.Checked, c => c.Target == WriteTarget.Cache && c.Path == loader.CachePath);
        Assert.DoesNotContain(h.Policy.Checked, c => c.Target != WriteTarget.Cache);
        Assert.Empty(Directory.EnumerateFiles(h.Repo.CacheDirectory, "*.tmp", SearchOption.AllDirectories));
        Assert.StartsWith("MQIX", Encoding.ASCII.GetString(File.ReadAllBytes(loader.CachePath).AsSpan(0, 4)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_new_loader_takes_unchanged_files_from_the_cache_without_reading_or_validating_them()
    {
        using var h = new LoaderHarness();
        h.CopyFixture("models", "billing");
        var cold = (await LoadAsync(h.NewLoader())).Snapshot;

        var loader = h.NewLoader();
        var warm = (await LoadAsync(loader)).Snapshot;

        Assert.Equal(32, loader.LastStatistics.FromCache);
        Assert.Equal(0, loader.LastStatistics.ReadFromDisk);
        Assert.Equal(0, loader.LastStatistics.SchemaEvaluations);
        Assert.False(loader.LastStatistics.CacheWritten); // nothing changed
        Assert.Equal(cold.Documents.Select(d => (d.Path, d.Hash, d.DependencyHash, d.SidecarText)), warm.Documents.Select(d => (d.Path, d.Hash, d.DependencyHash, d.SidecarText)));
        Assert.Equal(cold.LoadDiagnostics, warm.LoadDiagnostics);
        Assert.Equal((cold.SettingsHash, cold.Settings.Name), (warm.SettingsHash, warm.Settings.Name));
    }

    [Fact]
    public async Task A_cached_verdict_keeps_warnings_and_a_changed_file_is_read_and_validated_again()
    {
        using var h = new LoaderHarness();
        h.CopyFixture("models", "billing");
        var noncanonical = h.Read("model/entities/customer.json").Replace("\n", "\r\n", StringComparison.Ordinal);
        h.Write("model/entities/customer.json", noncanonical);
        await LoadAsync(h.NewLoader());
        h.Write("model/packages/catalog.json", h.Read("model/packages/catalog.json").Replace("What we sell.", "What we sell now.", StringComparison.Ordinal));

        var loader = h.NewLoader();
        var model = (await LoadAsync(loader)).Snapshot;

        Assert.Equal(1, loader.LastStatistics.ReadFromDisk);
        Assert.Equal(1, loader.LastStatistics.SchemaEvaluations);
        Assert.True(loader.LastStatistics.CacheWritten);
        var d = Assert.Single(model.LoadDiagnostics);
        Assert.Equal(("MQ1003", ".maquettiste/model/entities/customer.json"), (d.Rule, d.FilePath));
    }

    [Fact]
    public async Task Cached_bytes_are_used_when_the_stat_matches_and_verify_rehashes_everything()
    {
        using var h = new LoaderHarness();
        h.CopyFixture("models", "billing");
        await LoadAsync(h.NewLoader());

        // Change the content but keep the length and the last-write time: only a hash pass can see it.
        var path = h.Model("model/packages/catalog.json");
        var stamp = File.GetLastWriteTimeUtc(path);
        var text = h.Read("model/packages/catalog.json");
        h.Write("model/packages/catalog.json", text.Replace("What we sell.", "What we SELL.", StringComparison.Ordinal));
        File.SetLastWriteTimeUtc(path, stamp);

        var trusting = (await LoadAsync(h.NewLoader())).Snapshot;
        Assert.Equal("What we sell.", trusting.Get<Package>(Billing.IdOf(trusting, "package", "Catalog"))!.Description!.Text);

        var loader = h.NewLoader();
        var verified = (await LoadAsync(loader, verify: true)).Snapshot;
        Assert.Equal("What we SELL.", verified.Get<Package>(Billing.IdOf(verified, "package", "Catalog"))!.Description!.Text);
        Assert.Equal(32, loader.LastStatistics.ReadFromDisk);
        Assert.Equal(0, loader.LastStatistics.FromCache);
        Assert.Equal(1, loader.LastStatistics.SchemaEvaluations); // content-addressed: the other records are still trusted
    }

    [Fact]
    public async Task A_damaged_or_foreign_cache_is_ignored()
    {
        using var h = new LoaderHarness();
        h.CopyFixture("models", "billing");
        var loader = h.NewLoader();
        await LoadAsync(loader);
        var bytes = await File.ReadAllBytesAsync(loader.CachePath, Ct);

        // Corrupt one record's bytes: that record's hash no longer matches and it is dropped; the rest stays usable.
        var corrupted = (byte[])bytes.Clone();
        corrupted[^10] ^= 0xFF;
        await File.WriteAllBytesAsync(loader.CachePath, corrupted, Ct);
        var second = h.NewLoader();
        var model = (await LoadAsync(second)).Snapshot;
        Assert.Empty(model.LoadDiagnostics);
        Assert.Equal(1, second.LastStatistics.ReadFromDisk);

        // A truncated file, or one from another format, is ignored entirely.
        await File.WriteAllBytesAsync(second.CachePath, bytes.AsSpan(0, bytes.Length / 2).ToArray(), Ct);
        var third = h.NewLoader();
        Assert.Empty((await LoadAsync(third)).Snapshot.LoadDiagnostics);
        Assert.Equal(32, third.LastStatistics.ReadFromDisk);

        await File.WriteAllTextAsync(third.CachePath, "MQIX not really", Ct);
        var fourth = h.NewLoader();
        Assert.Empty((await LoadAsync(fourth)).Snapshot.LoadDiagnostics);
        Assert.Equal(32, fourth.LastStatistics.ReadFromDisk);
    }

    [Fact]
    public async Task A_refused_cache_write_leaves_no_file_and_the_load_succeeds()
    {
        using var h = new LoaderHarness();
        h.CopyFixture("models", "billing");
        h.Policy.Refuse = p => p.StartsWith(h.Repo.CacheDirectory, StringComparison.Ordinal);
        var loader = h.NewLoader();

        var model = (await LoadAsync(loader)).Snapshot;

        Assert.Equal(29, model.Documents.Count);
        Assert.False(loader.LastStatistics.CacheWritten);
        Assert.False(File.Exists(loader.CachePath));
    }

    [Fact]
    public async Task A_partial_load_does_not_rewrite_the_cache_and_the_next_open_rereads_only_what_changed()
    {
        using var h = new LoaderHarness();
        h.CopyFixture("models", "billing");
        var loader = h.NewLoader();
        var first = (await LoadAsync(loader)).Snapshot;
        var cacheStamp = File.GetLastWriteTimeUtc(loader.CachePath);
        h.Write("model/packages/catalog.json", h.Read("model/packages/catalog.json").Replace("What we sell.", "What we sell today.", StringComparison.Ordinal));

        await loader.LoadAsync(new LoadRequest(first, ["model/packages/catalog.json"], false), null, Ct);
        Assert.False(loader.LastStatistics.CacheWritten);
        Assert.Equal(cacheStamp, File.GetLastWriteTimeUtc(loader.CachePath));

        var reopened = h.NewLoader();
        await LoadAsync(reopened);
        Assert.Equal(1, reopened.LastStatistics.ReadFromDisk);
        Assert.Equal(31, reopened.LastStatistics.FromCache);
    }

    [Fact]
    public async Task A_5000_entity_model_reopens_from_the_cache()
    {
        using var h = new LoaderHarness(parallelism: 4);
        var entityCount = await WriteLargeModelAsync(h.Repo.ModelRoot, 5000);

        var coldWatch = Stopwatch.StartNew();
        var coldLoader = h.NewLoader();
        var cold = (await LoadAsync(coldLoader)).Snapshot;
        coldWatch.Stop();
        Assert.Empty(cold.LoadDiagnostics);
        Assert.Equal(entityCount, cold.All<Entity>().Count);
        Assert.Equal(entityCount + 1, coldLoader.LastStatistics.ReadFromDisk);
        Assert.Equal(entityCount + 1, coldLoader.LastStatistics.SchemaEvaluations);
        Assert.True(coldLoader.LastStatistics.CacheWritten);

        var warmWatch = Stopwatch.StartNew();
        var warmLoader = h.NewLoader();
        var warm = (await LoadAsync(warmLoader)).Snapshot;
        warmWatch.Stop();

        // The cache path: no file read through the model folder, no schema evaluated, same model.
        Assert.Equal(0, warmLoader.LastStatistics.ReadFromDisk);
        Assert.Equal(0, warmLoader.LastStatistics.SchemaEvaluations);
        Assert.Equal(entityCount + 1, warmLoader.LastStatistics.FromCache);
        Assert.Equal(cold.Documents.Select(d => (d.Path, d.Hash)), warm.Documents.Select(d => (d.Path, d.Hash)));
        Assert.Equal(cold.Summaries().Count, warm.Summaries().Count);
        TestContext.Current.SendDiagnosticMessage($"5,000 entities: cold load {coldWatch.ElapsedMilliseconds} ms, cached reopen {warmWatch.ElapsedMilliseconds} ms.");
    }

    /// <summary>Writes a synthetic model with the given number of entities (8 attributes each) in canonical form.</summary>
    internal static async Task<int> WriteLargeModelAsync(string modelRoot, int entities)
    {
        var ids = new SequentialIdGenerator(42);
        var json = TestServices.Json;
        var settings = new ProjectSettings { FormatVersion = 1, Name = "large" };
        Directory.CreateDirectory(Path.Combine(modelRoot, "model", "entities"));
        await File.WriteAllBytesAsync(Path.Combine(modelRoot, "maquettiste.json"), json.Serialize(settings, "maquettiste.json", ".maquettiste/maquettiste.json"), Ct);
        var types = new[] { "string", "int32", "int64", "decimal", "date", "datetimeoffset", "bool", "uuid" };
        await Parallel.ForEachAsync(Enumerable.Range(0, entities), Ct, async (i, token) =>
        {
            Entity entity;
            lock (ids)
            {
                var attributes = Enumerable.Range(0, 8)
                    .Select(a => new ModelAttribute { Id = ids.NewId(), Name = "field" + a, Type = new TypeRef { Builtin = types[a] }, Required = a == 0 })
                    .ToList();
                entity = new Entity
                {
                    Id = ids.NewId(),
                    Name = "Entity" + i.ToString("D5", System.Globalization.CultureInfo.InvariantCulture),
                    Key = new EntityKey { Attributes = [attributes[7].Id], Strategy = IdentityStrategy.UuidV7 },
                    Attributes = attributes,
                };
            }

            var modelPath = "model/entities/" + ModelPaths.Kebab(entity.Name) + ".json";
            await File.WriteAllBytesAsync(Path.Combine(modelRoot, modelPath), json.Serialize(entity, "entity.json", ".maquettiste/" + modelPath), token);
        });
        return entities;
    }
}
