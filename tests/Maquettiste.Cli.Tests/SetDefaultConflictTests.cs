using System.Text.Json;
using System.Text.Json.Nodes;
using Maquettiste.Engine;

namespace Maquettiste.Cli.Tests;

/// <summary>
/// <c>ModelStore.ChangeDefaultLocaleAsync</c> plans from the loaded model, so a locale shard that changes on disk after the load
/// makes the write a conflict: nothing is written (reference-types-seeds-localization.md section 3.9).
/// </summary>
public sealed class SetDefaultConflictTests
{
    private const string FrShard = ".maquettiste/model/locales/fr/_reference-data.json";

    [Fact]
    public async Task A_shard_changed_after_the_load_is_a_conflict_and_nothing_is_written()
    {
        using var repo = CliRepo.ReferenceData();
        var ct = TestContext.Current.CancellationToken;
        var store = new ModelStore(new EngineOptions { RepoRoot = repo.RepoRoot, CacheDirectory = repo.CacheDirectory });
        await using (store.ConfigureAwait(false))
        {
            await store.LoadAsync(ct);

            // Someone edits the fr shard after the load: the plan holds the loaded entries, the file holds newer ones.
            var shard = JsonNode.Parse(repo.Read(FrShard))!.AsObject();
            shard["entries"]!["01JRDA00000000000000000099"] = new JsonObject { ["displayName"] = "Nouveau" };
            repo.Write(FrShard, shard.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
            var before = repo.Tree(".maquettiste");

            var result = await store.ChangeDefaultLocaleAsync("fr", false, ChangeSource.Cli, ct);

            Assert.NotNull(result);
            Assert.Equal(SaveOutcome.Conflict, result.Outcome);
            Assert.False(result.Applied);
            var after = repo.Tree(".maquettiste");
            Assert.Equal(before.Keys, after.Keys);
            foreach (var (path, bytes) in before)
                Assert.Equal(bytes, after[path]);
        }
    }
}
