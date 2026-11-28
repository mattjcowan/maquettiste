using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Maquettiste.Engine.Model;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Validation;

/// <summary>
/// Differential fuzzing of the store's incremental whole-model validation (adopted from the 2026-11-28 review): over fixture models and
/// variants that add rows scripts, former codes, ends of several types, foreign-key chains of table seeds and borrowed lifecycles, each
/// mutation of one file (a removed, changed, renamed or retargeted value, a removed list item, a name another item has, a new id, a deleted
/// file, a file moved, an element rewritten as another kind) and some edits of several files refreshed together are applied, then undone,
/// and after each the store's report must equal a fresh whole-model validation of the same snapshot (<see cref="IncrementalValidationCheck"/>).
/// The default run is seeded and sampled, so it is the same on every machine and takes well under a minute. Set
/// <c>MAQUETTISTE_FUZZ_INCREMENTAL=long</c> for every mutation of every file of every model (the reference app too), with
/// <c>MAQUETTISTE_FUZZ_SEED</c> to vary the sample of the multi-file rounds.
/// </summary>
public sealed class IncrementalValidationFuzzTests
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static bool Long => string.Equals(Environment.GetEnvironmentVariable("MAQUETTISTE_FUZZ_INCREMENTAL"), "long", StringComparison.Ordinal);

    public static TheoryData<string> Models()
    {
        var models = new TheoryData<string>
        {
            "billing", "billing+rows-script", "billing+seed-chain", "billing+bound-type", "billing+several-types", "process-basics+lifecycle", "former-codes",
            "showcase", "reference-data", "processes", "bindings", "sql-queries", "process-basics",
        };
        if (Long)
            models.Add("reference-app");
        return models;
    }

    private const string Billing = "01J92P0V01KDRN8GX5PGYCNKSX";

    /// <summary>A model by name: a fixture, or a fixture with one of the repro additions (<see cref="IncrementalValidationReproTests"/>).</summary>
    private static TempRepo Build(string model)
    {
        var repo = new TempRepo();
        var (fixture, variant) = model.IndexOf('+') is var plus and >= 0 ? (model[..plus], model[(plus + 1)..]) : (model, "");
        if (fixture == "former-codes")
        {
            File.WriteAllText(Path.Combine(repo.ModelRoot, "maquettiste.json"), """{"formatVersion":1}""");
            IncrementalValidationReproTests.FormerCode(repo);
            return repo;
        }

        var folder = fixture == "reference-app" ? Path.Combine(Fixtures.RepoRoot, "samples", "reference-app", ".maquettiste") : Fixtures.Path("models", fixture, ".maquettiste");
        IncrementalValidationCheck.CopyModel(repo.ModelRoot, folder);
        switch (variant)
        {
            case "rows-script":
                IncrementalValidationReproTests.ScriptedSeed(repo);
                break;
            case "seed-chain":
                IncrementalValidationReproTests.SeedChain(repo);
                break;
            case "bound-type":
                IncrementalValidationReproTests.BoundTypeChain(repo);
                break;
            case "lifecycle":
                IncrementalValidationReproTests.BorrowedLifecycle(repo);
                break;
            case "several-types":
                var path = Path.Combine(repo.ModelRoot, "model/relations/annotates.json");
                File.WriteAllText(path, $$"""
                    {"kind":"relation","id":"01KFZ0000000000000000000R1","name":"annotates","package":"{{Billing}}","ends":[
                      {"id":"01KFZ0000000000000000000R2","entity":"01K6BND0000000000000000010","role":"note","max":"*","navigation":"annotatedBy"},
                      {"id":"01KFZ0000000000000000000R3","types":[{"entity":"{{IntegrationIds.Invoice}}"},{"entity":"01J92P0V0ETQKXXP951CMMNHH3"},{"stereotype":"audited"}],
                       "role":"target","max":1,"navigation":"annotates"}]}
                    """);
                break;
        }

        return repo;
    }

    private static class IntegrationIds
    {
        public const string Invoice = Integration.E2ERepo.InvoiceId;
    }

    /// <summary>A seed that is the same for a model on every machine (string hashes are randomized per process).</summary>
    private static int SeedOf(string model)
    {
        var hash = 2166136261u;
        foreach (var c in model + (Environment.GetEnvironmentVariable("MAQUETTISTE_FUZZ_SEED") ?? ""))
            hash = (hash ^ c) * 16777619u;
        return (int)(hash & 0x7fffffff);
    }

    private sealed record Mutation(string Label, Action<JsonObject> Apply);

    [Theory]
    [MemberData(nameof(Models))]
    public async Task Random_edits_give_the_whole_model_report(string model)
    {
        using var repo = Build(model);
        var check = new IncrementalValidationCheck(repo.Options);
        var failures = new List<string>();
        try
        {
            await check.CheckAsync("load", null, Ct);
            var snapshot = check.Snapshot;
            var category = new Dictionary<string, string>(StringComparer.Ordinal);
            var byCategory = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var doc in snapshot.Documents.OrderBy(d => d.Path, StringComparer.Ordinal))
                Collect(JsonNode.Parse(File.ReadAllBytes(check.FullPath(doc.Path))), doc.Element.KindName, category, byCategory);

            var rng = new Random(SeedOf(model));
            var database = snapshot.All<Database>().OrderBy(d => d.Id, StringComparer.Ordinal).FirstOrDefault()?.Id;
            var operations = new List<(ElementDocument Document, Mutation Mutation)>();
            foreach (var doc in snapshot.Documents.OrderBy(d => d.Path, StringComparer.Ordinal))
            {
                var root = JsonNode.Parse(File.ReadAllBytes(check.FullPath(doc.Path)))!.AsObject();
                var mutations = new List<Mutation>();
                Walk(root, [], mutations, category, byCategory, true);
                mutations.Add(new Mutation("DELETE", _ => { }));
                mutations.Add(new Mutation("MOVE-first", _ => { }));
                mutations.Add(new Mutation("MOVE-last", _ => { }));
                foreach (var kind in new[] { "entity", "enum", "value-object", "reference-type", "table" })
                {
                    if (kind != doc.Element.KindName && (kind != "table" || database is not null))
                        mutations.Add(KindChange(kind, doc.Element.Id, database));
                }

                operations.AddRange(mutations.Select(m => (doc, m)));
            }

            // The default run takes a seeded, weighted sample: files of the kinds an incremental validation takes weigh four times those that
            // always validate the whole model (weighted sampling without replacement, key = u^(1/w)).
            var single = Long ? operations : [.. operations
                .Select(o => (Operation: o, Key: Math.Pow(rng.NextDouble(), 1.0 / (Incremental(o.Document.Element) ? 4 : 1))))
                .OrderByDescending(o => o.Key).Take(SingleBudget(model)).Select(o => o.Operation)];
            foreach (var (doc, mutation) in single)
            {
                var label = $"{doc.Element.KindName} {doc.Element.Name} [{doc.Path}] {mutation.Label}";
                var original = await File.ReadAllBytesAsync(check.FullPath(doc.Path), Ct);
                try
                {
                    await ApplyAndUndoAsync(check, doc, mutation, label, original);
                }
                catch (InvalidOperationException e) when (IsMismatch(e))
                {
                    failures.Add(e.Message);
                    check = await ReloadAsync(check, repo, [(doc.Path, original)]);
                }
            }

            // Several files changed and refreshed together.
            var rounds = Long ? 400 : 12;
            var documents = snapshot.Documents.OrderBy(d => d.Path, StringComparer.Ordinal).ToList();
            for (var round = 0; round < rounds && documents.Count >= 2; round++)
            {
                var picked = documents.OrderBy(_ => rng.Next()).Take(2 + rng.Next(2)).ToList();
                var originals = picked.Select(d => File.ReadAllBytes(check.FullPath(d.Path))).ToList();
                var label = "";
                try
                {
                    for (var k = 0; k < picked.Count; k++)
                    {
                        var root = JsonNode.Parse(originals[k])!.AsObject();
                        var mutations = new List<Mutation>();
                        Walk(root, [], mutations, category, byCategory, true);
                        mutations.Add(new Mutation("DELETE", _ => { }));
                        var m = mutations[rng.Next(mutations.Count)];
                        label += $"{picked[k].Element.KindName} {picked[k].Element.Name} {m.Label}; ";
                        if (m.Label == "DELETE")
                        {
                            File.Delete(check.FullPath(picked[k].Path));
                        }
                        else
                        {
                            m.Apply(root);
                            File.WriteAllBytes(check.FullPath(picked[k].Path), Encoding.UTF8.GetBytes(root.ToJsonString(Indented) + "\n"));
                        }
                    }

                    await check.Store.RefreshAsync([.. picked.Select(d => d.Path)], Ct);
                    await check.CheckAsync(label, null, Ct);
                    for (var k = 0; k < picked.Count; k++)
                        File.WriteAllBytes(check.FullPath(picked[k].Path), originals[k]);
                    await check.Store.RefreshAsync([.. picked.Select(d => d.Path)], Ct);
                    await check.CheckAsync(label + " undone", null, Ct);
                }
                catch (InvalidOperationException e) when (IsMismatch(e))
                {
                    failures.Add(e.Message);
                    check = await ReloadAsync(check, repo, [.. picked.Select((d, k) => (d.Path, originals[k]))]);
                }
            }

            // A model with a rows script validates the whole model on every edit (any edit may change the rows); the others mostly do not.
            if (!model.EndsWith("+rows-script", StringComparison.Ordinal))
                Assert.True(check.Incremental > 0, string.Join("\n", check.Log));
        }
        finally
        {
            await check.DisposeAsync();
        }

        Assert.True(failures.Count == 0, $"{failures.Count} mismatches in {model}:\n" + string.Join("\n---\n", failures.Take(10)));
    }

    /// <summary>How many single-file mutations the default run takes of a model: its share of a run of well under a minute.</summary>
    private static int SingleBudget(string model) => model switch
    {
        "processes" or "sql-queries" or "showcase" => 80,
        _ => 120,
    };

    /// <summary>Whether a file's kind is one an incremental validation takes (Validation/README.md, "Incremental validation").</summary>
    private static bool Incremental(Element element) =>
        element is Entity or Relation or Mapping or Table or ReferenceType or EnumType or ValueObject || element is Seed { RowsFrom: null };

    private static bool IsMismatch(InvalidOperationException e) => e.Message.Contains(" differs.", StringComparison.Ordinal) || e.Message.Contains("whole has", StringComparison.Ordinal);

    /// <summary>After a mismatch: the files put back as they were (a moved file's copies removed), and a new store over them.</summary>
    private static async Task<IncrementalValidationCheck> ReloadAsync(IncrementalValidationCheck check, TempRepo repo, IReadOnlyList<(string Path, byte[] Bytes)> files)
    {
        await check.DisposeAsync();
        foreach (var (path, bytes) in files)
        {
            var folder = path[..(path.LastIndexOf('/') + 1)];
            foreach (var stray in new[] { "0000-moved.json", "zzzz-moved.json" })
                File.Delete(check.FullPath(folder + stray));
            File.WriteAllBytes(check.FullPath(path), bytes);
        }

        var fresh = new IncrementalValidationCheck(repo.Options);
        await fresh.CheckAsync("reload", null, Ct);
        return fresh;
    }

    private static async Task ApplyAndUndoAsync(IncrementalValidationCheck check, ElementDocument doc, Mutation mutation, string label, byte[] original)
    {
        var path = doc.Path;
        if (mutation.Label == "DELETE")
        {
            await check.DeleteAsync(path, Ct);
            await check.CheckAsync(label, null, Ct);
            await check.WriteAsync(path, original, Ct);
            await check.CheckAsync(label + " undone", null, Ct);
            return;
        }

        if (mutation.Label.StartsWith("MOVE", StringComparison.Ordinal))
        {
            var moved = path[..(path.LastIndexOf('/') + 1)] + (mutation.Label == "MOVE-first" ? "0000-moved.json" : "zzzz-moved.json");
            File.Move(check.FullPath(path), check.FullPath(moved));
            await check.Store.RefreshAsync([path, moved], Ct);
            await check.CheckAsync(label, null, Ct);
            File.Move(check.FullPath(moved), check.FullPath(path));
            await check.Store.RefreshAsync([path, moved], Ct);
            await check.CheckAsync(label + " undone", null, Ct);
            return;
        }

        var node = JsonNode.Parse(original)!.AsObject();
        mutation.Apply(node);
        await check.WriteAsync(path, Encoding.UTF8.GetBytes(node.ToJsonString(Indented) + "\n"), Ct);
        await check.CheckAsync(label, null, Ct);
        await check.WriteAsync(path, original, Ct);
        await check.CheckAsync(label + " undone", null, Ct);
    }

    /// <summary>The element's file rewritten as an element of another kind with the same id and name.</summary>
    private static Mutation KindChange(string kind, string id, string? database) => new("KIND->" + kind, r =>
    {
        var package = r["package"]?.DeepClone();
        var name = r["name"]?.DeepClone() ?? JsonValue.Create("Renamed");
        r.Clear();
        r["kind"] = kind;
        r["id"] = id;
        r["name"] = name;
        if (package is not null && kind != "table")
            r["package"] = package;
        switch (kind)
        {
            case "enum":
                r["members"] = new JsonArray(new JsonObject { ["id"] = "01KZZZZZZZZZZZZZZZZZZZZZ71", ["name"] = "One" });
                break;
            case "reference-type":
                r["code"] = new JsonObject { ["id"] = "01KZZZZZZZZZZZZZZZZZZZZZ72", ["length"] = 32 };
                r["label"] = new JsonObject { ["id"] = "01KZZZZZZZZZZZZZZZZZZZZZ73", ["length"] = 64 };
                break;
            case "table":
                r["database"] = database;
                r["columns"] = new JsonArray(new JsonObject { ["id"] = "01KZZZZZZZZZZZZZZZZZZZZZ74", ["name"] = "id", ["type"] = "int32" });
                break;
        }
    });

    private static void Collect(JsonNode? node, string where, Dictionary<string, string> category, Dictionary<string, List<string>> byCategory)
    {
        switch (node)
        {
            case JsonObject o:
                if (o["id"] is JsonValue v && v.TryGetValue<string>(out var id) && !category.ContainsKey(id))
                {
                    category[id] = where;
                    if (!byCategory.TryGetValue(where, out var list))
                        byCategory[where] = list = [];
                    list.Add(id);
                }

                foreach (var (name, child) in o)
                    Collect(child, where + "/" + name, category, byCategory);
                break;
            case JsonArray a:
                foreach (var item in a)
                    Collect(item, where, category, byCategory);
                break;
        }
    }

    private static JsonNode? At(JsonObject root, List<object> path)
    {
        JsonNode? node = root;
        foreach (var step in path)
            node = step is string s ? node![s] : node![(int)step];
        return node;
    }

    private static string Show(List<object> path) => "/" + string.Join("/", path);

    /// <summary>Every mutation of one value or list item of a file: removed, changed, renamed, retargeted to an id of the same place, flipped, a new id.</summary>
    private static void Walk(JsonNode? node, List<object> path, List<Mutation> mutations, Dictionary<string, string> category,
        Dictionary<string, List<string>> byCategory, bool isRoot)
    {
        switch (node)
        {
            case JsonObject o:
                foreach (var (name, child) in o.ToList())
                {
                    if (isRoot && name is "kind" or "id" or "$schema")
                        continue;
                    var p = path.Append(name).ToList();
                    var parent = path.ToList();
                    if (name != "id")
                        mutations.Add(new Mutation("remove " + Show(p), r => At(r, parent)!.AsObject().Remove(name)));
                    if (child is JsonValue v)
                    {
                        if (v.TryGetValue<string>(out var s))
                        {
                            if (name == "id")
                                mutations.Add(new Mutation("newid " + Show(p), r => At(r, parent)![name] = "01KZZZZZZZZZZZZZZZZZZZZZ77"));
                            else if (category.TryGetValue(s, out var cat))
                            {
                                foreach (var other in byCategory[cat].Where(x => x != s).Take(2))
                                    mutations.Add(new Mutation($"retarget {Show(p)} -> {other}", r => At(r, parent)![name] = other));
                            }
                            else if (name == "name")
                                mutations.Add(new Mutation("rename " + Show(p), r => At(r, parent)![name] = s + "X"));
                            else
                                mutations.Add(new Mutation("string " + Show(p), r => At(r, parent)![name] = s + "z"));
                        }
                        else if (v.TryGetValue<bool>(out var b))
                        {
                            mutations.Add(new Mutation("flip " + Show(p), r => At(r, parent)![name] = !b));
                        }
                        else if (v.TryGetValue<long>(out var n))
                        {
                            mutations.Add(new Mutation("inc " + Show(p), r => At(r, parent)![name] = n + 1));
                        }
                    }
                    else
                    {
                        Walk(child, p, mutations, category, byCategory, false);
                    }
                }

                break;
            case JsonArray a:
                for (var i = 0; i < a.Count; i++)
                {
                    var index = i;
                    var p = path.Append(i).ToList();
                    var parent = path.ToList();
                    mutations.Add(new Mutation("removeitem " + Show(p), r => At(r, parent)!.AsArray().RemoveAt(index)));
                    if (a[i] is JsonObject item)
                    {
                        if (i > 0 && item["name"] is JsonValue && a[0] is JsonObject first && first["name"] is JsonValue fn && fn.TryGetValue<string>(out var firstName))
                            mutations.Add(new Mutation("samename " + Show(p), r => At(r, parent)![index]!["name"] = firstName));
                        Walk(item, p, mutations, category, byCategory, false);
                    }
                    else if (a[i] is JsonValue v && v.TryGetValue<string>(out var s) && category.TryGetValue(s, out var cat))
                    {
                        foreach (var other in byCategory[cat].Where(x => x != s).Take(1))
                            mutations.Add(new Mutation($"retarget {Show(p)} -> {other}", r => At(r, parent)![index] = other));
                    }
                    else if (a[i] is JsonValue cell)
                    {
                        // A value in a list: a seed row's cell, a code, a stated value.
                        if (cell.TryGetValue<string>(out var text))
                            mutations.Add(new Mutation("cell " + Show(p), r => At(r, parent)![index] = text + "z"));
                        else if (cell.TryGetValue<long>(out var number))
                            mutations.Add(new Mutation("cell " + Show(p), r => At(r, parent)![index] = number + 1));
                        else if (cell.TryGetValue<bool>(out var flag))
                            mutations.Add(new Mutation("cell " + Show(p), r => At(r, parent)![index] = !flag));
                    }
                }

                break;
        }
    }
}
