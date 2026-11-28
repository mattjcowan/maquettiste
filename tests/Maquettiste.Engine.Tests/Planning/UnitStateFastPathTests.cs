using Maquettiste.Engine.Hashing;
using Maquettiste.Engine.Pipeline;
using Maquettiste.Engine.Planning;
using Maquettiste.Engine.SchemaDiff;
using Maquettiste.Testing;
using static Maquettiste.Engine.Tests.Planning.PlanningKit;

namespace Maquettiste.Engine.Tests.Planning;

/// <summary>
/// The unit state store's fast paths give the same bytes and hashes as the plain ones: states decoded from a file (read keys as
/// table indexes, records copied when every key keeps its index) encode exactly like the same states built from plain lists, and
/// the change detector's input hash over table-indexed keys equals the general one.
/// </summary>
public sealed class UnitStateFastPathTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Encoding_decoded_and_remembered_states_gives_the_bytes_of_a_fresh_encoding(int seed)
    {
        var random = new Random(seed);
        var keys = Enumerable.Range(0, 60).Select(i => (i % 3 == 0 ? "e:" : i % 3 == 1 ? "r:" : "s:") + "K" + i.ToString("D3", System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        var states = Enumerable.Range(0, 200).Select(i => State(random, keys, "u" + i.ToString("D4", System.Globalization.CultureInfo.InvariantCulture))).ToList();
        var bytes = UnitStateStore.Encode(states);

        for (var round = 0; round < 8; round++)
        {
            // What the next save gets: the states as loaded (decoded, or remembered from the last save), a few of them re-rendered
            // with new keys (some never seen before), some dropped, some added, and some copies of loaded states with new outputs.
            var loaded = round % 2 == 0
                ? UnitStateStore.Decode(bytes)!.Values.ToList()
                : Remembered(states);
            var next = new List<UnitState>();
            foreach (var state in loaded)
            {
                switch (random.Next(10))
                {
                    case 0:
                        next.Add(State(random, [.. keys, "e:NEW" + random.Next(5).ToString(System.Globalization.CultureInfo.InvariantCulture)], state.Key));
                        break;
                    case 1:
                        break; // dropped
                    case 2:
                        next.Add(state with { Outputs = [new UnitOutput("out/" + state.Key + ".changed", "h", 1, 2)] });
                        break;
                    default:
                        next.Add(state);
                        break;
                }
            }

            if (random.Next(2) == 0)
                next.Add(State(random, keys, "u9" + round.ToString(System.Globalization.CultureInfo.InvariantCulture)));

            var plain = next.Select(s => s with { ReadKeys = [.. s.ReadKeys] }).ToList();
            var expected = UnitStateStore.Encode(plain);
            var indexed = new List<UnitState>();
            var actual = UnitStateStore.Encode(next, indexed);
            Assert.Equal(expected, actual);
            Assert.Equal(plain.OrderBy(s => s.Key, StringComparer.Ordinal).Select(s => (s.Key, s.InputHash, string.Join(",", s.ReadKeys))),
                indexed.Select(s => (s.Key, s.InputHash, string.Join(",", s.ReadKeys))));
            states = indexed;
            bytes = actual;
        }

        static List<UnitState> Remembered(List<UnitState> states) => states;
    }

    [Fact]
    public async Task A_save_remembers_states_that_encode_to_the_same_bytes_again()
    {
        using var repo = new TempRepo();
        var store = new UnitStateStore(repo.Options, new Maquettiste.Engine.Writing.OutputPathPolicy(repo.Options, null));
        var random = new Random(5);
        var keys = Enumerable.Range(0, 20).Select(i => "e:K" + i.ToString("D2", System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        var states = Enumerable.Range(0, 50).Select(i => State(random, keys, "u" + i.ToString("D2", System.Globalization.CultureInfo.InvariantCulture))).ToList();
        await store.SaveAsync("p", states, Ct);
        var first = await File.ReadAllBytesAsync(store.FileOf("p"), Ct);

        var loaded = await store.LoadAsync("p", Ct);
        Assert.All(loaded.Values, s => Assert.IsType<UnitStateStore.TableKeys>(s.ReadKeys));
        await store.SaveAsync("p", [.. loaded.Values], Ct);
        Assert.Equal(first, await File.ReadAllBytesAsync(store.FileOf("p"), Ct));
        Assert.Equal(states.OrderBy(s => s.Key, StringComparer.Ordinal).Select(s => string.Join(",", s.ReadKeys)),
            (await store.LoadAsync("p", Ct)).Values.OrderBy(s => s.Key, StringComparer.Ordinal).Select(s => string.Join(",", s.ReadKeys)));
    }

    [Fact]
    public void Input_hash_over_table_indexed_keys_equals_the_general_one()
    {
        var b = Shop();
        var snapshot = b.Build();
        var resolved = Resolve(snapshot);
        var hasher = new DependencyHasher(snapshot, resolved, new PackSet([], []), new Dictionary<string, SchemaDiffResult>());
        var ids = resolved.Entities.SelectMany(e => new[] { "e:" + e.Id, "r:" + e.Id }).Append("s:conventions").Append("k:entity").Append("e:missing").ToArray();
        var random = new Random(9);
        var states = Enumerable.Range(0, 40).Select(i => State(random, ids, "u" + i.ToString("D2", System.Globalization.CultureInfo.InvariantCulture))).ToList();
        var decoded = UnitStateStore.Decode(UnitStateStore.Encode(states))!;
        foreach (var state in decoded.Values)
        {
            var plain = state.ReadKeys.ToArray();
            var expected = hasher.InputHash("static", plain);
            Assert.True(hasher.InputHashEquals("static", state.ReadKeys, expected));
            Assert.False(hasher.InputHashEquals("other", state.ReadKeys, expected));
        }

        // Keys out of order (a file this engine did not write) still hash as the sorted, distinct list.
        var table = new[] { "e:b", "e:a", "e:a" };
        var unsorted = new UnitStateStore.TableKeys(table, [0, 1]);
        Assert.True(hasher.InputHashEquals("static", unsorted, hasher.InputHash("static", ["e:a", "e:b"])));
        var repeated = new UnitStateStore.TableKeys(table, [1, 2]);
        Assert.True(hasher.InputHashEquals("static", repeated, hasher.InputHash("static", ["e:a"])));
    }

    [Fact]
    public async Task Loads_reuse_the_kept_states_until_the_file_changes_behind_the_store()
    {
        using var repo = new TempRepo();
        var store = new UnitStateStore(repo.Options, new Maquettiste.Engine.Writing.OutputPathPolicy(repo.Options, null));
        var random = new Random(7);
        var keys = Enumerable.Range(0, 20).Select(i => "e:K" + i.ToString("D2", System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        var states = Enumerable.Range(0, 30).Select(i => State(random, keys, "u" + i.ToString("D2", System.Globalization.CultureInfo.InvariantCulture))).ToList();
        await store.SaveAsync("p", states, Ct);
        var file = store.FileOf("p");
        Assert.Equal(UnitStateStore.Encode(states), await File.ReadAllBytesAsync(file, Ct));

        // A run loads the states twice (beside resolve and in the skip check): neither reads the file the store wrote.
        var loaded = await store.LoadAsync("p", Ct);
        Assert.Same(loaded, await store.LoadAsync("p", Ct));
        Assert.Equal(0, store.FileReads);

        // A save with one changed unit writes a fresh encoding's bytes, and the next load still reads nothing.
        var next = loaded.Values.OrderBy(s => s.Key, StringComparer.Ordinal).ToList();
        next[3] = State(random, [.. keys, "e:NEW"], next[3].Key);
        await store.SaveAsync("p", next, Ct);
        var expected = UnitStateStore.Encode([.. next.Select(s => s with { ReadKeys = [.. s.ReadKeys] })]);
        Assert.Equal(expected, await File.ReadAllBytesAsync(file, Ct));
        Assert.Equal(next[3].InputHash, (await store.LoadAsync("p", Ct))[next[3].Key].InputHash);
        Assert.Equal(0, store.FileReads);

        // Another writer replaces the file (a new stat): the store reads and decodes it.
        UnitState[] other = [new UnitState("p/a", "h2", ["e:1"], [new UnitOutput("out/a.txt", "abc", 3, 42)])];
        await File.WriteAllBytesAsync(file, UnitStateStore.Encode(other), Ct);
        File.SetLastWriteTimeUtc(file, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        Assert.Equal("h2", Assert.Single(await store.LoadAsync("p", Ct)).Value.InputHash);
        Assert.Equal(1, store.FileReads);
        await store.LoadAsync("p", Ct);
        Assert.Equal(1, store.FileReads);

        // Touched without a change of bytes: read once, not decoded again (the same states come back).
        var before = await store.LoadAsync("p", Ct);
        File.SetLastWriteTimeUtc(file, new DateTime(2021, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        Assert.Same(before, await store.LoadAsync("p", Ct));
        Assert.Equal(2, store.FileReads);
    }

    /// <summary>
    /// States with every field (key hashes, static parts, element names, exports, non-ASCII and long strings) encode to the bytes the
    /// encoder gave before the save started sizing its buffer per state and keeping names as table indexes: the hash below was taken
    /// from that encoder, over a fresh encoding and four saves of loaded, re-rendered, dropped and added states.
    /// </summary>
    [Fact]
    public void Encoding_states_with_every_field_gives_the_same_bytes_as_before()
    {
        var random = new Random(11);
        var keys = Enumerable.Range(0, 80).Select(i => (i % 4) switch
        {
            0 => "e:K" + i.ToString("D3", System.Globalization.CultureInfo.InvariantCulture),
            1 => "r:K" + i.ToString("D3", System.Globalization.CultureInfo.InvariantCulture),
            2 => "s:é" + i.ToString("D3", System.Globalization.CultureInfo.InvariantCulture),
            _ => "e:" + new string('L', 130) + i.ToString("D3", System.Globalization.CultureInfo.InvariantCulture),
        }).ToArray();
        var states = Enumerable.Range(0, 240).Select(i => RichState(random, keys, "u" + i.ToString("D4", System.Globalization.CultureInfo.InvariantCulture))).ToList();
        using var all = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
        var bytes = UnitStateStore.Encode(states);
        all.AppendData(bytes);
        for (var round = 0; round < 4; round++)
        {
            var loaded = round % 2 == 0 ? UnitStateStore.Decode(bytes)!.Values.ToList() : states;
            var next = new List<UnitState>();
            foreach (var state in loaded)
            {
                switch (random.Next(8))
                {
                    case 0:
                        next.Add(RichState(random, [.. keys, "e:NEW" + random.Next(4).ToString(System.Globalization.CultureInfo.InvariantCulture)], state.Key));
                        break;
                    case 1:
                        break;
                    default:
                        next.Add(state);
                        break;
                }
            }

            next.Add(RichState(random, keys, "u9" + round.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            var indexed = new List<UnitState>();
            bytes = UnitStateStore.Encode(next, indexed);
            all.AppendData(bytes);

            // The states kept after a save name the elements a load of the bytes names (a name of a key not read is not kept).
            var decoded = UnitStateStore.Decode(bytes)!;
            foreach (var state in indexed)
                Assert.Equal(Named(decoded[state.Key]), Named(state));

            states = indexed;
        }

        Assert.Equal("ddd17c1c35081866a30edfc5d70e9b8d199e449dcdea07226aae87dd4a80c98c", Convert.ToHexStringLower(all.GetHashAndReset()));

        static string Named(UnitState state) =>
            state.Names is null ? "-" : string.Join(";", state.Names.OrderBy(n => n.Key, StringComparer.Ordinal).Select(n => n.Key + "=" + n.Value));
    }

    [Fact]
    public void States_kept_after_a_save_share_their_name_labels_with_the_key_table()
    {
        Dictionary<string, string> Names() => new(StringComparer.Ordinal)
        {
            ["e:1"] = new string("Customer (entity)".AsSpan()),
            ["e:2"] = new string("Invoice (entity)".AsSpan()),
            ["e:9"] = "Not read (entity)",
        };

        UnitState[] states =
        [
            new UnitState("p/a", "h1", ["e:1", "e:2", "k:entity"], []) { Names = Names() },
            new UnitState("p/b", "h2", ["e:1", "e:2"], []) { Names = Names() },
            new UnitState("p/c", "h3", ["e:1"], []) { Names = new Dictionary<string, string>(StringComparer.Ordinal) { ["e:9"] = "x" } },
        ];
        Assert.NotSame(states[0].Names!["e:1"], states[1].Names!["e:1"]);
        var indexed = new List<UnitState>();
        var bytes = UnitStateStore.Encode(states, indexed);

        Assert.IsType<UnitStateStore.TableNames>(indexed[0].Names);
        Assert.Same(indexed[0].Names!["e:1"], indexed[1].Names!["e:1"]);
        Assert.Same(indexed[0].Names!["e:2"], indexed[1].Names!["e:2"]);
        Assert.Equal(["e:1", "e:2"], indexed[0].Names!.Keys.Order(StringComparer.Ordinal)); // a name of a key not read is not kept
        Assert.Null(indexed[2].Names);

        // Saving the kept states again copies their records and keeps their names.
        var again = new List<UnitState>();
        Assert.Equal(bytes, UnitStateStore.Encode(indexed, again));
        Assert.Equal("Invoice (entity)", again[1].Names!["e:2"]);
        Assert.Same(again[0].Names!["e:1"], again[1].Names!["e:1"]);
    }

    private static UnitState RichState(Random random, IReadOnlyList<string> keys, string key)
    {
        var plain = State(random, keys, key);
        var chosen = plain.ReadKeys;
        var names = random.Next(3) == 0 ? null : chosen.Where(k => k.StartsWith("e:", StringComparison.Ordinal) && random.Next(4) != 0)
            .Append("e:unread" + random.Next(3).ToString(System.Globalization.CultureInfo.InvariantCulture))
            .ToDictionary(k => k, k => "Näme " + k[^3..] + " (entity)", StringComparer.Ordinal);
        UnitExports? exports = null;
        if (random.Next(3) == 0)
        {
            var exportKeys = random.Next(4) == 0 ? ["e:not-read"] : chosen.Where(_ => random.Next(2) == 0).ToList();
            using var document = System.Text.Json.JsonDocument.Parse("{\"b\":" + random.Next(100).ToString(System.Globalization.CultureInfo.InvariantCulture) + ",\"a\":\"vé" + new string('x', random.Next(200)) + "\"}");
            exports = new UnitExports(document.RootElement.Clone(), exportKeys, ContentHash.Of(key + "exports"));
        }

        var outputs = plain.Outputs.Select((o, i) => i == 0 && random.Next(3) == 0 ? o with { Path = "out/ü/" + new string('p', 140) + key } : o).ToList();
        return plain with
        {
            Outputs = outputs,
            KeyHashes = random.Next(4) == 0 ? default : KeyHashes.Of(chosen, k => ContentHash.Of(k + key[^1])),
            StaticParts = random.Next(3) == 0 ? null : "pack-version=1.0." + random.Next(3).ToString(System.Globalization.CultureInfo.InvariantCulture) + "\nunit=" + key[..2],
            Names = names,
            Exports = exports,
        };
    }

    private static UnitState State(Random random, IReadOnlyList<string> keys, string key)
    {
        var count = 1 + random.Next(12);
        var chosen = Enumerable.Range(0, count).Select(_ => keys[random.Next(keys.Count)]).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        var outputs = Enumerable.Range(0, 1 + random.Next(2))
            .Select(o => new UnitOutput("out/" + key + "/" + o.ToString(System.Globalization.CultureInfo.InvariantCulture), ContentHash.Of(key + o), 10 + o, 1000 + o))
            .ToList();
        return new UnitState(key, ContentHash.Of(key + string.Join(",", chosen)), chosen, outputs);
    }
}
