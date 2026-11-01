using System.Text;
using System.Text.Json.Nodes;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Pipeline;
using Maquettiste.Engine.Tests.Integration;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Generation;

/// <summary>
/// Schema snapshots around a run (engine-design.md section 14): saved after a successful apply, drift in check mode; runs against
/// a baseline model instead (next-phase-1-design.md section 3.1), which leave them alone; and the migration facts each diff renders,
/// which the saved snapshot lists (section 3.2).
/// </summary>
public sealed class SchemaSnapshotTests
{
    private const string SnapshotPath = ".maquettiste/snapshots/main.json";
    private const string DiffPath = "db/diffs/diff.txt";
    private const string NicknameId = "01K9AGN0000000000000000001";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Apply_saves_the_snapshot_and_check_reports_a_stale_one()
    {
        await using var f = await GenerationFixture.CreateAsync(b => Models.Shop(b), "basic");
        var packFile = f.Repo.PathOf(".maquettiste/templates/basic/pack.json");
        f.WritePackFile("basic", "pack.json", File.ReadAllText(packFile).Replace("\"units\"", "\"usesSchemaDiff\": true,\n  \"units\"", StringComparison.Ordinal));

        var dry = await f.RunAsync(GenerationMode.DryRun);
        Assert.Equal(RunOutcome.Succeeded, dry.Outcome);
        Assert.False(f.Repo.Exists(".maquettiste/snapshots/main.json"));

        var applied = await f.RunAsync();
        Assert.Equal(RunOutcome.Succeeded, applied.Outcome);
        Assert.True(f.Repo.Exists(".maquettiste/snapshots/main.json"));
        Assert.Equal(RunOutcome.Succeeded, (await f.RunAsync(GenerationMode.Check)).Outcome);

        await f.WriteModelAsync(b => Models.Shop(b, extra: (m, _, _) => m.Entity("Order").Key("id", "uuid")));
        var snapshot = f.Repo.ReadFile(".maquettiste/snapshots/main.json");
        var check = await f.RunAsync(GenerationMode.Check);
        Assert.Equal(RunOutcome.Drift, check.Outcome);
        Assert.Contains(check.Diagnostics, d => d.Rule == "MQ6018" && d.FilePath == ".maquettiste/snapshots/main.json");
        Assert.Equal(snapshot, f.Repo.ReadFile(".maquettiste/snapshots/main.json"));
    }

    [Fact]
    public async Task The_snapshot_serialized_during_the_run_is_what_a_capture_after_it_saves()
    {
        // A run serializes the snapshot from the diff's own capture while it renders (WA); a store the run does not know takes the
        // plain path, a second capture saved after the apply. Both must write the same bytes, run after run.
        await using var early = await GenerationFixture.CreateAsync(b => Models.Shop(b), "basic");
        await using var plain = await GenerationFixture.CreateAsync(b => Models.Shop(b), s => s with { Snapshots = new PassThrough(s.Snapshots) }, "basic");
        foreach (var f in new[] { early, plain })
        {
            var packFile = f.Repo.PathOf(".maquettiste/templates/basic/pack.json");
            f.WritePackFile("basic", "pack.json", File.ReadAllText(packFile).Replace("\"units\"", "\"usesSchemaDiff\": true,\n  \"units\"", StringComparison.Ordinal));
        }

        Action<ModelBuilder>[] models =
        [
            b => Models.Shop(b),
            b => Models.Shop(b, extra: (m, _, _) => m.Entity("Order").Key("id", "uuid")),
            b => Models.Shop(b, extra: (m, _, _) => m.Entity("Order").Key("id", "uuid").Attr("note", "string")),
        ];
        var revision = 0;
        foreach (var model in models)
        {
            foreach (var f in new[] { early, plain })
            {
                await f.WriteModelAsync(model);
                Assert.Equal(RunOutcome.Succeeded, (await f.RunAsync()).Outcome);
                Assert.Equal(RunOutcome.Succeeded, (await f.RunAsync(GenerationMode.Check)).Outcome);
            }

            var bytes = early.Repo.ReadFile(".maquettiste/snapshots/main.json");
            Assert.Equal(plain.Repo.ReadFile(".maquettiste/snapshots/main.json"), bytes);
            Assert.Contains("\"revision\": " + (++revision).ToString(System.Globalization.CultureInfo.InvariantCulture), bytes, StringComparison.Ordinal);
        }

        // An unchanged model leaves the snapshot alone (the store answers the load from what it parsed when it wrote the file).
        var before = early.Repo.ReadFile(".maquettiste/snapshots/main.json");
        Assert.Equal(RunOutcome.Succeeded, (await early.RunAsync()).Outcome);
        Assert.Equal(before, early.Repo.ReadFile(".maquettiste/snapshots/main.json"));
    }

    [Fact]
    public async Task The_store_holds_the_written_snapshot_only_while_runs_diff_it()
    {
        await using var f = await GenerationFixture.CreateAsync(b => Models.Shop(b), "basic");
        var store = Assert.IsType<Maquettiste.Engine.SchemaDiff.SnapshotStore>(f.Services.Snapshots);
        var packFile = f.Repo.PathOf(".maquettiste/templates/basic/pack.json");
        var plainPack = File.ReadAllText(packFile);
        f.WritePackFile("basic", "pack.json", plainPack.Replace("\"units\"", "\"usesSchemaDiff\": true,\n  \"units\"", StringComparison.Ordinal));

        // The apply writes the snapshot and parses it back for the next run, which reuses that object.
        Assert.Equal(RunOutcome.Succeeded, (await f.RunAsync()).Outcome);
        var held = store.Held("main");
        Assert.NotNull(held);
        var parsed = await held;
        Assert.Equal(RunOutcome.Succeeded, (await f.RunAsync()).Outcome);
        Assert.Same(parsed, await store.Held("main")!);

        // A run whose packs no longer diff drops it.
        f.WritePackFile("basic", "pack.json", plainPack);
        Assert.Equal(RunOutcome.Succeeded, (await f.RunAsync()).Outcome);
        Assert.Null(store.Held("main"));
    }

    [Fact]
    public async Task Generate_against_a_baseline_diffs_from_it_and_leaves_the_committed_snapshot_alone()
    {
        await using var repo = DiffRepo();
        await repo.ApplyAsync(); // the committed snapshot, revision 1
        var baseline = (await new SnapshotLibrary(repo.Store).CreateAsync(new SnapshotCreateRequest("Base"), Ct)).Id;
        Assert.Equal(SaveOutcome.Saved, (await repo.EditAsync(E2ERepo.CustomerId, AddNickname)).Outcome);
        await repo.ApplyAsync(); // revision 2, which holds the new column
        Assert.StartsWith("revisions 1 2\n", repo.Repo.ReadFile(DiffPath), StringComparison.Ordinal);
        var committed = repo.Repo.ReadFile(SnapshotPath);

        // Against the baseline the diff runs from the model as the model snapshot holds it, whatever the committed snapshot says.
        var against = await repo.Service.RunAsync(new GenerationRequest { Against = baseline, Jobs = 2 }, null, Ct);
        E2ERepo.AssertOutcome(RunOutcome.Succeeded, against);
        var diff = repo.Repo.ReadFile(DiffPath);
        Assert.StartsWith("revisions 0 1\n", diff, StringComparison.Ordinal);
        Assert.Contains("table altered " + CustomerKey, diff, StringComparison.Ordinal);
        Assert.Contains("column added " + NicknameId, diff, StringComparison.Ordinal);
        Assert.Equal(committed, repo.Repo.ReadFile(SnapshotPath));

        // Check mode against it: the diff is not empty, and no committed snapshot is called stale.
        var check = await repo.Service.RunAsync(new GenerationRequest { Mode = GenerationMode.Check, Against = baseline, Jobs = 2 }, null, Ct);
        E2ERepo.AssertOutcome(RunOutcome.Succeeded, check);
        Assert.DoesNotContain(check.Diagnostics, d => d.Rule == "MQ6018");

        // Without it the committed snapshot is the baseline again, and the model has not changed since it.
        await repo.ApplyAsync();
        Assert.StartsWith("revisions 2 2\n", repo.Repo.ReadFile(DiffPath), StringComparison.Ordinal);
        Assert.Equal(committed, repo.Repo.ReadFile(SnapshotPath));

        // A value that names nothing is the run's argument error, marked as the baseline's (hosts tell it from any other).
        var unknown = await Assert.ThrowsAsync<BadBaselineException>(() =>
            repo.Service.RunAsync(new GenerationRequest { Against = "no-such-snapshot", Jobs = 2 }, null, Ct));
        Assert.Contains("no-such-snapshot", unknown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Generate_against_a_baseline_after_materialize_plans_no_drop()
    {
        await using var repo = DiffRepo();
        using var copy = new TempRepo();
        E2ERepo.CopyTree(repo.Repo.ModelRoot, copy.ModelRoot); // the baseline: a folder that holds another copy of the project

        // invoice stored as a table file: its table and columns take the file's ids, and the write records one rekey fact.
        var parsed = repo.Store.ParseBatch(Encoding.UTF8.GetBytes(new JsonObject
        {
            ["operations"] = new JsonArray(new JsonObject
            {
                ["op"] = "materialize-tables", ["database"] = E2ERepo.MainDatabaseId, ["entities"] = new JsonArray(E2ERepo.InvoiceId),
            }),
        }.ToJsonString()));
        var stored = await repo.Store.ApplyBatchAsync(parsed.Batch!, ChangeSource.Editor, Ct);
        Assert.Equal(SaveOutcome.Saved, stored.Outcome);
        var rekey = Assert.Single(stored.Facts!, f => f.Fact == "rekey").Id;
        var aliases = repo.Repo.ReadFile(SnapshotPath); // materialize records its aliases in the committed snapshot

        var service = new GenerationService(repo.Store, repo.Options) { AllowFolderBaselines = true };
        var run = await service.RunAsync(new GenerationRequest { Against = copy.RepoRoot, Jobs = 2 }, null, Ct);
        E2ERepo.AssertOutcome(RunOutcome.Succeeded, run);
        var diff = repo.Repo.ReadFile(DiffPath);
        Assert.StartsWith("revisions 0 ", diff, StringComparison.Ordinal);
        foreach (var kind in new[] { "table added", "table dropped", "column added", "column dropped" })
            Assert.DoesNotContain(kind, diff, StringComparison.Ordinal);
        // The baseline lacks the rekey fact, so it renders; the diff starts from the baseline's own state.
        Assert.Contains("fact " + rekey + " rekey complete", diff, StringComparison.Ordinal);
        Assert.Equal(12, Line(diff, "states ").Split(' ')[1].Length);
        Assert.Equal(aliases, repo.Repo.ReadFile(SnapshotPath)); // a run against a baseline leaves the committed snapshot alone
    }

    [Fact]
    public async Task Plan_against_then_apply_plan_uses_the_same_baseline()
    {
        await using var repo = DiffRepo();
        using var copy = new TempRepo();
        E2ERepo.CopyTree(repo.Repo.ModelRoot, copy.ModelRoot);
        Assert.Equal(SaveOutcome.Saved, (await repo.EditAsync(E2ERepo.CustomerId, AddNickname)).Outcome);
        var service = new GenerationService(repo.Store, repo.Options) { AllowFolderBaselines = true };

        // The stored request holds the text, and the apply opens the baseline again from it.
        var planned = await service.PlanAsync(new GenerationRequest { Against = copy.RepoRoot, Jobs = 2 }, null, Ct);
        Assert.Equal(RunOutcome.Succeeded, planned.Outcome);
        Assert.Equal(copy.RepoRoot, (await service.GetPlanAsync(planned.Plan!.Id, Ct))!.Request.Against);
        var applied = await service.ApplyAsync(planned.Plan.Id, null, Ct);
        Assert.Equal(RunOutcome.Succeeded, applied.Outcome);
        Assert.Contains("column added " + NicknameId, repo.Repo.ReadFile(DiffPath), StringComparison.Ordinal);
        Assert.False(repo.Repo.Exists(SnapshotPath));

        // A baseline that changed between the plan and its apply makes the plan stale (the units that read its diffs).
        var again = await service.PlanAsync(new GenerationRequest { Against = copy.RepoRoot, Jobs = 2 }, null, Ct);
        Assert.Equal(RunOutcome.Succeeded, again.Outcome);
        var customer = Path.Combine(copy.ModelRoot, "model", "entities", "customer.json");
        File.WriteAllText(customer, File.ReadAllText(customer).Replace("Someone we bill.", "Someone we bill, and keep.", StringComparison.Ordinal));
        var refused = await service.ApplyAsync(again.Plan!.Id, null, Ct);
        Assert.Equal(RunOutcome.Stale, refused.Outcome);
        Assert.Equal(["diffs/diff"], refused.StaleUnits);

        // Planned again against the changed baseline, the apply goes through.
        var third = await service.PlanAsync(new GenerationRequest { Against = copy.RepoRoot, Jobs = 2 }, null, Ct);
        Assert.Equal(RunOutcome.Succeeded, (await service.ApplyAsync(third.Plan!.Id, null, Ct)).Outcome);
    }

    [Fact]
    public async Task Rendered_facts_join_the_committed_snapshot_and_held_ones_never_do()
    {
        await using var repo = DiffRepo();
        await repo.ApplyAsync(); // revision 1, no facts
        Assert.Empty(SnapshotFacts(repo));

        // One write renames Customer's name column (a complete rename) and drops customerSince (a pending drop), which is then held.
        var edited = await repo.EditAsync(E2ERepo.CustomerId, n =>
        {
            var attributes = n["attributes"]!.AsArray();
            attributes[1]!["name"] = "fullName";
            attributes.RemoveAt(3);
        });
        var rename = Assert.Single(edited.Facts!, f => f.Fact == "rename").Id;
        var drop = Assert.Single(edited.Facts!, f => f.Fact == "drop").Id;
        await EditFactAsync(repo, drop, n => n["held"] = true);

        // The rename renders and joins the snapshot's facts; the held drop renders nothing and never joins.
        await repo.ApplyAsync();
        var diff = repo.Repo.ReadFile(DiffPath);
        Assert.StartsWith("revisions 1 2\n", diff, StringComparison.Ordinal);
        Assert.Contains("fact " + rename + " rename complete", diff, StringComparison.Ordinal);
        Assert.DoesNotContain(drop, diff, StringComparison.Ordinal);
        Assert.Equal([rename], SnapshotFacts(repo));
        await AssertSnapshotCurrentAsync(repo);

        // Released (and acknowledged), the drop renders in the next diff, though the column left the snapshot while it was held.
        await EditFactAsync(repo, drop, n =>
        {
            n["released"] = true;
            n["acknowledge"] = "Nobody reads when a customer joined.";
        });
        Assert.Contains((await repo.RunAsync(GenerationMode.Check)).Diagnostics, d => d.Rule == "MQ6018");
        await repo.ApplyAsync();
        diff = repo.Repo.ReadFile(DiffPath);
        Assert.StartsWith("revisions 2 3\n", diff, StringComparison.Ordinal);
        Assert.Contains("fact " + drop + " drop released", diff, StringComparison.Ordinal);
        Assert.DoesNotContain("table ", diff, StringComparison.Ordinal);
        Assert.Equal(new[] { rename, drop }.Order(StringComparer.Ordinal), SnapshotFacts(repo));
        await AssertSnapshotCurrentAsync(repo);

        // A fact gone from the model leaves the list when the snapshot is next written.
        var document = await repo.Store.GetElementAsync(rename, Ct);
        Assert.Equal(SaveOutcome.Saved, (await repo.Store.DeleteAsync(rename, document!.Hash, DeleteResolution.Refuse, ChangeSource.Editor, Ct)).Outcome);
        Assert.Equal(SaveOutcome.Saved, (await repo.EditAsync(E2ERepo.CustomerId, AddNickname)).Outcome);
        await repo.ApplyAsync();
        Assert.Equal([drop], SnapshotFacts(repo));
    }

    [Fact]
    public async Task A_new_fact_alone_makes_the_diff_not_empty()
    {
        await using var repo = DiffRepo();
        await repo.ApplyAsync();
        var first = repo.Repo.ReadFile(DiffPath);
        Assert.StartsWith("revisions 0 1\n", first, StringComparison.Ordinal);
        var state = Line(first, "states ").Split(' ');
        Assert.Equal(("", 12), (state[1], state[2].Length)); // a diff from nothing starts from no state
        await AssertSnapshotCurrentAsync(repo);

        // A complete raw fact changes no table, yet the diff is no longer empty: check reports the stale snapshot.
        var created = await repo.CreateElementAsync($$$"""
            {"kind": "migration", "database": "{{{E2ERepo.MainDatabaseId}}}", "fact": "raw",
             "sql": {"*": "update billing.customers set name = trim(name)"}, "skipWhen": {"*": "select 1 where false"}}
            """);
        Assert.Contains((await repo.RunAsync(GenerationMode.Check)).Diagnostics, d => d.Rule == "MQ6018");

        var applied = await repo.ApplyAsync();
        Assert.Contains(applied.Changes, c => c.Path == DiffPath && c.Kind == FileChangeKind.Modified);
        var diff = repo.Repo.ReadFile(DiffPath);
        Assert.StartsWith("revisions 1 2\nstates " + state[2] + " " + state[2] + "\nfact " + created.Id + " raw complete\n", diff, StringComparison.Ordinal);
        Assert.DoesNotContain("table ", diff, StringComparison.Ordinal);
        Assert.Equal([created.Id!], SnapshotFacts(repo));
        Assert.Contains("\"revision\": 2", repo.Repo.ReadFile(SnapshotPath), StringComparison.Ordinal);

        // Rendered, the fact is in the snapshot's list: the next diff is empty again and starts where this one ended.
        await AssertSnapshotCurrentAsync(repo);
        Assert.Equal(SaveOutcome.Saved, (await repo.EditAsync(E2ERepo.CustomerId, AddNickname)).Outcome);
        await repo.ApplyAsync();
        var next = Line(repo.Repo.ReadFile(DiffPath), "states ").Split(' ');
        Assert.Equal(state[2], next[1]);
        Assert.NotEqual(state[2], next[2]);
    }

    [Fact]
    public async Task Generate_after_materialize_with_only_a_rekey_fact_plans_no_drop()
    {
        await using var repo = DiffRepo();
        await repo.ApplyAsync(); // revision 1: invoice is a projected table under its synthesized key
        var parsed = repo.Store.ParseBatch(Encoding.UTF8.GetBytes(new JsonObject
        {
            ["operations"] = new JsonArray(new JsonObject
            {
                ["op"] = "materialize-tables", ["database"] = E2ERepo.MainDatabaseId, ["entities"] = new JsonArray(E2ERepo.InvoiceId),
            }),
        }.ToJsonString()));
        var stored = await repo.Store.ApplyBatchAsync(parsed.Batch!, ChangeSource.Editor, Ct);
        Assert.Equal(SaveOutcome.Saved, stored.Outcome);
        var rekey = Assert.Single(stored.Facts!, f => f.Fact == "rekey").Id;

        // Without the aliases materialize recorded, only the rekey fact reads the snapshot under today's keys.
        var snapshot = JsonNode.Parse(repo.Repo.ReadFile(SnapshotPath))!.AsObject();
        Assert.True(snapshot.Remove("aliases"));
        repo.Repo.WriteFile(SnapshotPath, snapshot.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }) + "\n");

        var planned = await repo.Service.PlanAsync(new GenerationRequest { Jobs = 2 }, null, Ct);
        Assert.Equal(RunOutcome.Succeeded, planned.Outcome);
        Assert.Equal(RunOutcome.Succeeded, (await repo.Service.ApplyAsync(planned.Plan!.Id, null, Ct)).Outcome);
        var diff = repo.Repo.ReadFile(DiffPath);
        Assert.StartsWith("revisions 1 2\n", diff, StringComparison.Ordinal);
        Assert.Contains("fact " + rekey + " rekey complete", diff, StringComparison.Ordinal);
        foreach (var kind in new[] { "table added", "table dropped", "column added", "column dropped" })
            Assert.DoesNotContain(kind, diff, StringComparison.Ordinal);
        Assert.Equal([rekey], SnapshotFacts(repo));
        await AssertSnapshotCurrentAsync(repo);
    }

    /// <summary>Check mode finds the committed snapshot current (no MQ6018); the diff file itself changes after every apply.</summary>
    private static async Task AssertSnapshotCurrentAsync(E2ERepo repo)
    {
        var check = await repo.RunAsync(GenerationMode.Check);
        Assert.NotEqual(RunOutcome.Invalid, check.Outcome);
        Assert.DoesNotContain(check.Diagnostics, d => d.Rule == "MQ6018");
    }

    private static IReadOnlyList<string> SnapshotFacts(E2ERepo repo) =>
        JsonNode.Parse(repo.Repo.ReadFile(SnapshotPath))!["facts"]?.AsArray().Select(f => (string)f!).ToList() ?? [];

    private static string Line(string text, string prefix) =>
        text.Split('\n').Single(l => l.StartsWith(prefix, StringComparison.Ordinal));

    private static async Task EditFactAsync(E2ERepo repo, string id, Action<JsonObject> edit)
    {
        var result = await repo.EditAsync(id, edit);
        Assert.True(result.Facts is null or { Count: 0 }, "editing a fact records none");
    }

    private static string CustomerKey => E2ERepo.CustomerId + "@" + E2ERepo.MainDatabaseId;

    private static void AddNickname(JsonObject customer) => customer["attributes"]!.AsArray().Add(new JsonObject
    {
        ["id"] = NicknameId, ["name"] = "nickname", ["type"] = "string", ["length"] = 40,
    });

    /// <summary>A copy of billing with a pack that prints the main database's schema diff to <see cref="DiffPath"/>.</summary>
    private static E2ERepo DiffRepo()
    {
        var repo = E2ERepo.Create(demo: false, settings: s => s["packs"]!["diffs"] = new JsonObject { ["output"] = "db/diffs" });
        repo.Repo.WriteFile(".maquettiste/templates/diffs/pack.json", """
            {
              "name": "diffs",
              "version": "1.0.0",
              "engine": ">=1.0 <2.0",
              "description": "The main database's schema diff, one line per table and column change.",
              "usesSchemaDiff": true,
              "units": [
                { "id": "diff", "template": "diff.scriban", "for": "model", "output": "diff.txt" }
              ]
            }

            """);
        repo.Repo.WriteFile(".maquettiste/templates/diffs/diff.scriban", """
            {{- d = schema_diff.main -}}
            revisions {{ d.from_revision }} {{ d.to_revision }}
            states {{ d.from_state }} {{ d.to_state }}
            {{- for f in d.facts }}
            fact {{ f.id }} {{ f.fact }} {{ f.state }}
            {{- end }}
            {{- for t in d.tables }}
            table {{ t.kind }} {{ t.key }}
            {{- for c in t.columns }}
            column {{ c.kind }} {{ c.key }}
            {{- end }}
            {{- end }}

            """);
        return repo;
    }

    /// <summary>A snapshot store that is not the engine's own, so a run takes the plain save path.</summary>
    private sealed class PassThrough(ISnapshotStore inner) : ISnapshotStore
    {
        public Task<PhysicalSnapshot?> LoadAsync(string databaseName, CancellationToken ct) => inner.LoadAsync(databaseName, ct);

        public Task SaveAsync(PhysicalSnapshot snapshot, CancellationToken ct) => inner.SaveAsync(snapshot, ct);
    }
}
