using System.Globalization;
using System.Text.Json.Nodes;
using Maquettiste.Engine;
using Maquettiste.Engine.Loading;
using Maquettiste.Engine.Localization;

namespace Maquettiste.Bench.Synthetic;

/// <summary>
/// Complete locale shards for the synthetic model (<see cref="SyntheticModelOptions.Locales"/>; reference-types-seeds-localization.md
/// sections 3.3 and 5): the written model is loaded once, and for each translated locale every localizable node of the default locale
/// gets every expected field, in the shard its scope names, with the <c>src</c> fingerprint of its source text. Nodes are visited in id
/// order and the texts draw from their own random stream, so the same options give the same bytes.
/// </summary>
internal static class LocaleShards
{
    /// <summary>The default locale of a model written with locales.</summary>
    public const string DefaultLocale = "en";

    private static readonly string[] Candidates = ["fr", "de", "es", "it", "pt", "nl", "sv", "pl"];

    /// <summary>The most translated locales the option takes.</summary>
    public const int MaxLocales = 8;

    /// <summary>The first <paramref name="count"/> translated locales.</summary>
    /// <param name="count">How many.</param>
    /// <returns>The locale tags.</returns>
    public static IReadOnlyList<string> Translated(int count) => Candidates[..count];

    /// <summary>Loads the written model and writes the shards of every translated locale.</summary>
    /// <param name="repoRoot">The repo root the model was written to.</param>
    /// <param name="writer">The writer (canonical form and the path guard).</param>
    /// <param name="options">The options (<see cref="SyntheticModelOptions.Locales"/>, the seed).</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The number of shard files written.</returns>
    public static async Task<int> WriteAsync(string repoRoot, RepoWriter writer, SyntheticModelOptions options, CancellationToken ct)
    {
        var root = Path.GetFullPath(repoRoot);
        var engine = new EngineOptions { RepoRoot = root, CacheDirectory = Path.Combine(root, ".maquettiste", ".cache", "locales") };
        var store = new ModelStore(engine);
        await using (store.ConfigureAwait(false))
        {
            var snapshot = await store.GetSnapshotAsync(ct).ConfigureAwait(false);
            var index = snapshot.Localization;
            var nodes = index.Nodes.Values.OrderBy(n => n.Id, StringComparer.Ordinal).ToList();
            var files = 0;
            var locales = Translated(options.Locales);
            for (var l = 0; l < locales.Count; l++)
            {
                var locale = locales[l];
                var random = new SeededRandom(0x10CA_1E00_0000_0000UL ^ ((ulong)(uint)options.Seed << 8) ^ (ulong)l);
                var shards = new SortedDictionary<string, JsonObject>(StringComparer.Ordinal);
                foreach (var node in nodes)
                {
                    var entry = new JsonObject();
                    var src = new JsonObject();
                    foreach (var field in node.ExpectedFields())
                    {
                        var source = node.Source(field)!;
                        var word = Vocabulary.Nouns[random.Next(Vocabulary.Nouns.Length)];
                        entry[field] = string.Create(CultureInfo.InvariantCulture, $"{source} ({locale} {word})");
                        src[field] = LocalizationIndex.SourceHash(source);
                    }

                    if (entry.Count == 0)
                        continue;
                    entry["src"] = src;
                    var path = index.ShardPath(locale, node.Scope);
                    if (!shards.TryGetValue(path, out var shard))
                        shards[path] = shard = ChangePlanner.NewShard(path, locale, node.Scope);
                    shard["entries"]!.AsObject()[node.Id] = entry;
                }

                foreach (var (path, shard) in shards)
                {
                    await writer.WriteNodeAsync(shard, "locale.json", path, ct).ConfigureAwait(false);
                    files++;
                }
            }

            return files;
        }
    }
}
