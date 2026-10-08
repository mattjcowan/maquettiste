using System.Collections.Immutable;
using Maquettiste.Engine.Hashing;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Pipeline;
using Maquettiste.Engine.Planning;
using Maquettiste.Engine.SchemaDiff;
using Maquettiste.Engine.Writing;
using Maquettiste.Testing;
using static Maquettiste.Engine.Tests.Planning.PlanningKit;

namespace Maquettiste.Engine.Tests.Planning;

public sealed class HashingAndStateTests
{
    [Fact]
    public void Dependency_keys_hash_their_current_state()
    {
        var b = Shop();
        var snapshot = b.Build();
        var resolved = Resolve(snapshot);
        var customer = resolved.Entities.Single(e => e.Name == "Customer");
        var main = resolved.Databases.Single();
        var diff = new SchemaDiffResult("main", 0, 1, false, "diff-hash", [], [], []);
        using var repo = new TempRepo();
        repo.WriteFile("pack/t.tpl", "template");
        var pack = Pack("p", []) with { RootPath = repo.PathOf("pack") };
        var hasher = new DependencyHasher(snapshot, resolved, new PackSet([pack], []), new Dictionary<string, SchemaDiffResult> { ["main"] = diff });

        Assert.Equal(snapshot.GetDocument(customer.Id)!.DependencyHash, hasher.CurrentHash("e:" + customer.Id));
        Assert.Equal(snapshot.GetDocument(customer.Key!.Attributes[0].Id)!.DependencyHash, hasher.CurrentHash("e:" + customer.Key.Attributes[0].Id));
        Assert.Equal(snapshot.KindSetHash(ElementKind.Entity), hasher.CurrentHash("k:entity"));
        Assert.Equal(snapshot.KindSetHash(ElementKind.ValueObject), hasher.CurrentHash("k:value-object"));
        Assert.Equal(snapshot.ReferrersHash(customer.Id), hasher.CurrentHash("r:" + customer.Id));
        Assert.Equal(ContentHash.Of("template"u8), hasher.CurrentHash("t:p/t.tpl"));
        Assert.Equal("diff-hash", hasher.CurrentHash("d:" + main.Id));
        Assert.Matches("^[0-9a-f]{64}$", hasher.CurrentHash("s:conventions"));
        Assert.Matches("^[0-9a-f]{64}$", hasher.CurrentHash("s:typeMaps"));
        Assert.Matches("^[0-9a-f]{64}$", hasher.CurrentHash("s:inflection"));
        Assert.Matches("^[0-9a-f]{64}$", hasher.CurrentHash("s:project"));
        foreach (var gone in new[] { "e:01ARZ3NDEKTSV4RRFFQ69G5FAV", "k:nothing", "t:p/missing.tpl", "t:p/../escape", "t:q/t.tpl", "d:nope", "s:other", "x:y", "plain" })
            Assert.Equal(DependencyHasher.Absent, hasher.CurrentHash(gone));
    }

    [Fact]
    public void Settings_keys_follow_their_section_only()
    {
        var b = Shop();
        var before = b.Build();
        b.Settings(s => s with { Conventions = s.Conventions with { PluralTables = false } });
        var after = b.Build();
        DependencyHasher Hasher(ModelSnapshot m) => new(m, Resolve(m), new PackSet([], []), new Dictionary<string, SchemaDiffResult>());
        Assert.NotEqual(Hasher(before).CurrentHash("s:conventions"), Hasher(after).CurrentHash("s:conventions"));
        Assert.Equal(Hasher(before).CurrentHash("s:inflection"), Hasher(after).CurrentHash("s:inflection"));
        Assert.Equal(Hasher(before).CurrentHash("s:project"), Hasher(after).CurrentHash("s:project"));
        b.Settings(s => s with { Properties = new Dictionary<string, string> { ["baseNamespace"] = "Acme" } });
        var named = b.Build();
        Assert.NotEqual(Hasher(after).CurrentHash("s:project"), Hasher(named).CurrentHash("s:project"));
        Assert.Equal(Hasher(after).CurrentHash("s:conventions"), Hasher(named).CurrentHash("s:conventions"));
    }

    [Fact]
    public void Input_hash_is_the_same_for_any_order_of_the_same_keys_and_changes_with_the_static_hash()
    {
        var snapshot = Shop().Build();
        var hasher = new DependencyHasher(snapshot, Resolve(snapshot), new PackSet([], []), new Dictionary<string, SchemaDiffResult>());
        var sorted = hasher.InputHash("static", ["k:entity", "k:relation"]);
        Assert.Equal(sorted, hasher.InputHash("static", ["k:relation", "k:entity", "k:entity"]));
        Assert.NotEqual(sorted, hasher.InputHash("static2", ["k:entity", "k:relation"]));
        Assert.NotEqual(sorted, hasher.InputHash("static", ["k:entity"]));
        Assert.Equal(HashBuilder.Of("static", "k:entity", hasher.CurrentHash("k:entity"), "k:relation", hasher.CurrentHash("k:relation")), sorted);
    }

    [Fact]
    public void Input_hash_equality_agrees_with_the_input_hash()
    {
        var snapshot = Shop().Build();
        var hasher = new DependencyHasher(snapshot, Resolve(snapshot), new PackSet([], []), new Dictionary<string, SchemaDiffResult>());
        string[] keys = ["k:entity", "k:relation", "s:conventions"];
        var hash = hasher.InputHash("static", keys);

        Assert.True(hasher.InputHashEquals("static", keys, hash));
        Assert.True(hasher.InputHashEquals("static", ["s:conventions", "k:entity", "k:relation", "k:entity"], hash));
        Assert.False(hasher.InputHashEquals("static2", keys, hash));
        Assert.False(hasher.InputHashEquals("static", ["k:entity"], hash));
        Assert.False(hasher.InputHashEquals("static", keys, hash.ToUpperInvariant()));
        Assert.False(hasher.InputHashEquals("static", keys, "short"));
        Assert.True(hasher.InputHashEquals("static", [], hasher.InputHash("static", [])));
    }

    [Fact]
    public void Unit_state_encoding_keeps_every_field_and_shares_repeated_read_keys()
    {
        var states = new[]
        {
            new UnitState("p/b:2", "h2", ["e:1", "k:entity", "r:é"], [new UnitOutput("out/b.txt", "abc", 3, 42), new UnitOutput("out/c.txt", "o:def", 0, -1)]),
            new UnitState("p/a", "h1", [], []),
            new UnitState("p/c:3", "h3", ["e:1", "k:entity"], [new UnitOutput("out/ü.txt", "r:x", long.MaxValue, long.MinValue)]),
        };

        var decoded = UnitStateStore.Decode(UnitStateStore.Encode(states))!;

        Assert.Equal(["p/a", "p/b:2", "p/c:3"], decoded.Keys.Order(StringComparer.Ordinal));
        foreach (var state in states)
        {
            Assert.Equal(state.InputHash, decoded[state.Key].InputHash);
            Assert.Equal(state.ReadKeys, decoded[state.Key].ReadKeys);
            Assert.Equal(state.Outputs, decoded[state.Key].Outputs);
        }

        Assert.Same(decoded["p/b:2"].ReadKeys[0], decoded["p/c:3"].ReadKeys[0]);
        // Deterministic: the same states in any order encode to the same bytes.
        Assert.Equal(UnitStateStore.Encode(states), UnitStateStore.Encode([.. states.Reverse()]));
    }

    [Fact]
    public void Unit_state_format_3_keeps_per_key_hashes_and_static_parts_and_copies_an_unchanged_record()
    {
        var hashes = KeyHashes.Of(["e:1", "k:entity"], k => k == "e:1" ? new string('a', 64) : DependencyHasher.Absent);
        const string parts = "pack-version=1.0.0\nunit=0123";
        var states = new[]
        {
            new UnitState("p/b:2", "h2", ["e:1", "k:entity"], []) { KeyHashes = hashes, StaticParts = parts },
            new UnitState("p/c:3", "h3", ["e:1", "k:entity"], []) { KeyHashes = hashes, StaticParts = parts },
            new UnitState("p/a", "h1", ["e:1"], []),
        };

        var bytes = UnitStateStore.Encode(states);
        var decoded = UnitStateStore.Decode(bytes)!;
        Assert.Equal(hashes, decoded["p/b:2"].KeyHashes.ToArray());
        Assert.Equal(parts, decoded["p/b:2"].StaticParts);
        Assert.Same(decoded["p/b:2"].StaticParts, decoded["p/c:3"].StaticParts); // one table string for every element of a pack unit
        Assert.True(decoded["p/a"].KeyHashes.IsEmpty);
        Assert.Null(decoded["p/a"].StaticParts);
        Assert.False(KeyHashes.Differs(decoded["p/b:2"].KeyHashes.Span, 0, new string('a', 64)));
        Assert.True(KeyHashes.Differs(decoded["p/b:2"].KeyHashes.Span, 0, new string('b', 64)));

        // Saving the decoded states again copies their records: the same bytes, and the copies still carry hashes and parts.
        var indexed = new List<UnitState>();
        Assert.Equal(bytes, UnitStateStore.Encode([.. decoded.Values], indexed));
        Assert.Equal(hashes, indexed.Single(s => s.Key == "p/c:3").KeyHashes.ToArray());
        Assert.Equal(parts, indexed.Single(s => s.Key == "p/c:3").StaticParts);
    }

    [Fact]
    public async Task A_state_file_of_the_previous_format_loads_empty_as_a_reset_and_is_deleted_on_save()
    {
        using var repo = new TempRepo();
        var store = new UnitStateStore(repo.Options, new OutputPathPolicy(repo.Options, null));
        Assert.EndsWith("p.v4.bin", store.FileOf("p"), StringComparison.Ordinal);
        Assert.Empty(await store.LoadAsync("p", Ct));
        Assert.False(store.WasReset("p")); // no state at all: new, not reset

        Directory.CreateDirectory(store.Folder);
        await File.WriteAllBytesAsync(store.LegacyFileOf("p"), [(byte)'M', (byte)'Q', (byte)'U', (byte)'S', 2, 0, 0, 0], Ct);
        Assert.Empty(await store.LoadAsync("p", Ct));
        Assert.True(store.WasReset("p"));

        await store.SaveAsync("p", [new UnitState("p/a", "h1", [], [])], Ct);
        Assert.False(File.Exists(store.LegacyFileOf("p")));
        Assert.False(store.WasReset("p"));

        await File.WriteAllBytesAsync(store.FileOf("p"), [1, 2, 3], Ct); // another engine's or format's bytes
        Assert.Empty(await store.LoadAsync("p", Ct));
        Assert.True(store.WasReset("p"));
    }

    [Fact]
    public async Task Unit_state_store_reuses_what_it_decoded_only_for_the_same_bytes()
    {
        using var repo = new TempRepo();
        var store = new UnitStateStore(repo.Options, new OutputPathPolicy(repo.Options, null));
        UnitState[] first = [new UnitState("p/a", "h1", ["e:1"], [new UnitOutput("out/a.txt", "abc", 3, 42)])];
        await store.SaveAsync("p", first, Ct);
        var loaded = await store.LoadAsync("p", Ct);
        Assert.Same(loaded, await store.LoadAsync("p", Ct));
        Assert.Equal("h1", loaded["p/a"].InputHash);

        // Another writer replaces the file: the store decodes the new bytes.
        UnitState[] second = [new UnitState("p/a", "h2", ["e:1"], [new UnitOutput("out/a.txt", "abc", 3, 42)])];
        await File.WriteAllBytesAsync(store.FileOf("p"), UnitStateStore.Encode(second), Ct);
        Assert.Equal("h2", (await store.LoadAsync("p", Ct))["p/a"].InputHash);

        // A damaged file loads empty and is not remembered.
        await File.WriteAllBytesAsync(store.FileOf("p"), [1, 2, 3], Ct);
        Assert.Empty(await store.LoadAsync("p", Ct));
        await File.WriteAllBytesAsync(store.FileOf("p"), UnitStateStore.Encode(first), Ct);
        Assert.Equal("h1", (await store.LoadAsync("p", Ct))["p/a"].InputHash);
    }

    [Fact]
    public async Task Unit_state_round_trips_and_a_damaged_file_loads_empty()
    {
        using var repo = new TempRepo();
        var store = new UnitStateStore(repo.Options, new OutputPathPolicy(repo.Options, null));
        var states = new[]
        {
            new UnitState("p/b:2", "h2", ["e:1", "k:entity"], [new UnitOutput("out/b.txt", "abc", 3, 42)]),
            new UnitState("p/a", "h1", [], []),
        };
        await store.SaveAsync("p", states, Ct);
        var loaded = await store.LoadAsync("p", Ct);
        Assert.Equal(["p/a", "p/b:2"], loaded.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(states[0].ReadKeys, loaded["p/b:2"].ReadKeys);
        Assert.Equal(states[0].Outputs, loaded["p/b:2"].Outputs);
        Assert.Empty(await store.LoadAsync("other", Ct));

        var file = store.FileOf("p");
        await File.WriteAllBytesAsync(file, (await File.ReadAllBytesAsync(file, Ct))[..^3], Ct);
        Assert.Empty(await store.LoadAsync("p", Ct));

        await store.SaveAsync("p", [], Ct);
        Assert.False(File.Exists(file));
        Assert.StartsWith("x-", UnitStateStore.SafeName("../evil"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unit_state_writes_go_through_the_engine_write_guard()
    {
        using var repo = new TempRepo();
        using var other = new TempRepo();
        var store = new UnitStateStore(repo.Options, new OutputPathPolicy(other.Options, null));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => store.SaveAsync("p", [new UnitState("p/a", "h", [], [])], Ct));
        Assert.False(Directory.Exists(Path.Combine(repo.CacheDirectory, "units")));
    }

    [Fact]
    public async Task Change_detector_skips_only_units_whose_inputs_and_outputs_are_intact()
    {
        using var repo = new TempRepo();
        var snapshot = Shop().Build();
        var resolved = Resolve(snapshot);
        var hasher = new DependencyHasher(snapshot, resolved, new PackSet([], []), new Dictionary<string, SchemaDiffResult>());
        var pack = Pack("p", [Unit("u", "model")]);
        PlannedUnit Planned(string key) => new(key, pack, pack.Manifest.Units[0], null, "static-" + key);
        repo.WriteFile("out/a.txt", "a");
        repo.WriteFile("out/b.txt", "b");
        repo.WriteFile("out/owned.txt", "mine");
        UnitOutput Output(string path, string hash)
        {
            var info = new FileInfo(repo.PathOf(path));
            return new UnitOutput(path, hash, info.Length, info.LastWriteTimeUtc.Ticks);
        }

        var hashA = ContentHash.Of("a"u8);
        var hashB = ContentHash.Of("b"u8);
        var keys = new[] { "k:entity" };
        var state = new InMemoryStates();
        state.States["p"] = new Dictionary<string, UnitState>
        {
            ["p/intact"] = new("p/intact", hasher.InputHash("static-p/intact", keys), keys, [Output("out/a.txt", hashA), Output("out/owned.txt", "o:whatever")]),
            ["p/touched"] = new("p/touched", hasher.InputHash("static-p/touched", keys), keys, [Output("out/b.txt", hashB) with { LastWriteUtcTicks = 1 }]),
            ["p/stale"] = new("p/stale", "old-input", keys, []),
            ["p/edited"] = new("p/edited", hasher.InputHash("static-p/edited", keys), keys, [Output("out/b.txt", hashA) with { LastWriteUtcTicks = 1 }]),
            ["p/unlisted"] = new("p/unlisted", hasher.InputHash("static-p/unlisted", keys), keys, [Output("out/a.txt", hashB)]),
        };
        var manifests = new ManifestSet(new ManifestSetData(ImmutableSortedDictionary.Create<string, PackManifestData>(StringComparer.Ordinal)))
            .WithJournalOverlay([new JournalRecord("write", "p", "out/a.txt", hashA, "u"), new JournalRecord("write", "p", "out/b.txt", hashB, "u")]);
        var plan = new UnitPlan([Planned("p/edited"), Planned("p/intact"), Planned("p/new"), Planned("p/stale"), Planned("p/touched"), Planned("p/unlisted")], []);
        var detector = new ChangeDetector(repo.Options);

        var result = await detector.SelectAsync(plan, hasher, state, manifests, GenerationMode.Apply, false, null, Ct);
        Assert.Equal(["p/intact", "p/touched"], result.Skipped.Select(s => s.Unit.Key));
        Assert.Equal(["p/edited", "p/new", "p/stale", "p/unlisted"], result.ToRender.Select(u => u.Key));

        File.Delete(repo.PathOf("out/owned.txt"));
        result = await detector.SelectAsync(plan, hasher, state, manifests, GenerationMode.DryRun, false, null, Ct);
        Assert.Equal(["p/touched"], result.Skipped.Select(s => s.Unit.Key));

        Assert.Empty((await detector.SelectAsync(plan, hasher, state, manifests, GenerationMode.Apply, true, null, Ct)).Skipped);
        Assert.Empty((await detector.SelectAsync(plan, hasher, state, manifests, GenerationMode.Check, false, null, Ct)).Skipped);
    }

    private sealed class InMemoryStates : IUnitStateStore
    {
        public Dictionary<string, IReadOnlyDictionary<string, UnitState>> States { get; } = new(StringComparer.Ordinal);

        public Task<IReadOnlyDictionary<string, UnitState>> LoadAsync(string pack, CancellationToken ct) =>
            Task.FromResult(States.TryGetValue(pack, out var s) ? s : new Dictionary<string, UnitState>());

        public Task SaveAsync(string pack, IReadOnlyCollection<UnitState> states, CancellationToken ct) => Task.CompletedTask;
    }

    [Fact]
    public void Element_names_round_trip_and_a_decoded_state_is_copied_byte_for_byte()
    {
        var named = new UnitState("p/a", "h1", ["e:1", "k:entity", "e:2"], [new UnitOutput("a.sql", "m", 3, 4)])
        {
            StaticParts = "parts",
            Names = new Dictionary<string, string>(StringComparer.Ordinal) { ["e:1"] = "Customer (entity)", ["e:2"] = "Invoice (entity)" },
        };
        var plain = new UnitState("p/b", "h2", ["e:1"], []);
        var bytes = UnitStateStore.Encode([named, plain]);
        var decoded = UnitStateStore.Decode(bytes)!;
        Assert.Equal(named.Names.OrderBy(n => n.Key, StringComparer.Ordinal), decoded["p/a"].Names!.OrderBy(n => n.Key, StringComparer.Ordinal));
        Assert.Equal("parts", decoded["p/a"].StaticParts);
        Assert.Null(decoded["p/b"].Names);

        // Re-encoding the decoded states copies their records: the same bytes; a changed name encodes afresh.
        Assert.Equal(bytes, UnitStateStore.Encode(decoded.Values.ToList()));
        var renamed = decoded["p/a"] with { Names = new Dictionary<string, string>(StringComparer.Ordinal) { ["e:2"] = "Bill (entity)" } };
        var again = UnitStateStore.Decode(UnitStateStore.Encode([renamed, decoded["p/b"]]))!;
        Assert.Equal("Bill (entity)", Assert.Single(again["p/a"].Names!).Value);
        Assert.Equal(["e:1", "k:entity", "e:2"], again["p/a"].ReadKeys);
    }
}
