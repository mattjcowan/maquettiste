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
