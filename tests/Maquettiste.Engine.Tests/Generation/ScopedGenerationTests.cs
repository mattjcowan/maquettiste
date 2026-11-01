using System.Text;
using System.Text.Json.Nodes;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Generation;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Pipeline;
using Maquettiste.Engine.Tests.Editor;
using Maquettiste.Engine.Tests.Integration;

namespace Maquettiste.Engine.Tests.Generation;

/// <summary>
/// Scoped generation (next-phase-1-design.md section 5.6) over a temporary billing copy with the two example packs: the unit and
/// element filters limit the plan, the writer keeps every output and unit state of the units a run does not select and deletes only
/// the orphans of those it does, a scoped run never touches the last-run record or the schema snapshots, and a unit that reads the
/// schema diff fails in a scoped run (MQ6033). Unknown or ambiguous unit values are MQ6032; a filter selecting nothing is MQ6034.
/// </summary>
[Collection(IntegrationCollection.Name)]
public sealed class ScopedGenerationTests
{
    private const string CustomerId = EditorRepo.CustomerId;
    private const string MainDatabaseId = EditorRepo.MainDatabaseId;
    private const string SnapshotPath = ".maquettiste/snapshots/main.json";
    private const string RefundAttributeId = "01J92P0VZZ0000000000000010";
    private static readonly string[] Packs = ["csharp-dapper", "sql-ddl"];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Generating_one_element_rewrites_its_files_and_keeps_every_other_file_and_state()
    {
        await using var repo = EditorRepo.Create();
        await SettleAsync(repo);
        var files = repo.Files();
        var states = await StatesAsync(repo);
        await WidenCustomerNameAsync(repo);

        var scoped = await ApplyAsync(repo, new GenerationRequest { Elements = [CustomerId] });
        Assert.True(scoped.UnitsRendered > 0);
        Assert.NotEmpty(scoped.Changes);
        Assert.All(scoped.Changes, c => Assert.EndsWith(":" + CustomerId, c.UnitKey, StringComparison.Ordinal));

        // Every file and manifest entry the customer's units do not own is as the full run left it; the snapshot did not move.
        AssertOthersKept(repo, files, scoped.Changes, OfCustomer);

        // The states of every other unit are kept (they still describe the full run), the customer's units are current.
        var after2 = await StatesAsync(repo);
        foreach (var (key, state) in states)
        {
            Assert.True(after2.TryGetValue(key, out var kept), key);
            if (!key.EndsWith(":" + CustomerId, StringComparison.Ordinal))
                Assert.Equal(state.InputHash, kept.InputHash);
        }

        // So a full run still renders what the edit changed beyond the customer's own units (its table, the schema, the migration).
        var plan = await repo.Service.PlanAsync(new GenerationRequest(), null, Ct);
        Assert.Equal(RunOutcome.Succeeded, plan.Outcome);
        Assert.Contains(plan.Plan!.Units, u => !u.Skipped && u.Key.StartsWith("sql-ddl/", StringComparison.Ordinal));
        Assert.DoesNotContain(plan.Plan.Units, u => !u.Skipped && u.Key.EndsWith(":" + CustomerId, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Generating_one_unit_deletes_only_its_own_orphans()
    {
        await using var repo = CreateWithoutFacts();
        var refund = await CreateRefundAsync(repo);
        await SettleAsync(repo);
        var refundKeys = await RefundOutputsAsync(repo, refund);
        Assert.Contains(refundKeys, o => o.Unit.StartsWith("csharp-dapper/entity:", StringComparison.Ordinal));
        Assert.Contains(refundKeys, o => !o.Unit.StartsWith("csharp-dapper/entity:", StringComparison.Ordinal));
        await DeleteAsync(repo, refund);
        var files = repo.Files();

        var scoped = await ApplyAsync(repo, new GenerationRequest { Units = ["csharp-dapper/entity"] });
        var deleted = scoped.Changes.Where(c => c.Kind is FileChangeKind.Deleted or FileChangeKind.OrphanedOwned).ToList();
        Assert.NotEmpty(deleted);
        Assert.All(scoped.Changes, c => Assert.StartsWith("csharp-dapper/entity:", c.UnitKey, StringComparison.Ordinal));
        foreach (var (path, unit) in refundKeys.Where(o => o.Unit.StartsWith("csharp-dapper/entity:", StringComparison.Ordinal)))
            Assert.Contains(scoped.Changes, c => c.Path == path && c.UnitKey == unit);
        AssertOthersKept(repo, files, scoped.Changes, unit => unit.StartsWith("csharp-dapper/entity:", StringComparison.Ordinal));
        Assert.All(refundKeys.Where(o => !o.Unit.StartsWith("csharp-dapper/entity:", StringComparison.Ordinal)), o => Assert.True(repo.Repo.Exists(o.Path), o.Path));

        // The other units' entries stay in the manifests, so the full run removes them later.
        var full = await ApplyAsync(repo, new GenerationRequest());
        foreach (var (path, unit) in refundKeys.Where(o => !o.Unit.StartsWith("csharp-dapper/entity:", StringComparison.Ordinal)))
            Assert.Contains(full.Changes, c => c.Path == path && c.UnitKey == unit);
    }

    [Fact]
    public async Task Generating_a_deleted_element_removes_its_outputs()
    {
        await using var repo = CreateWithoutFacts();
        var refund = await CreateRefundAsync(repo);
        await SettleAsync(repo);
        var outputs = await RefundOutputsAsync(repo, refund);
        var table = refund + "@" + MainDatabaseId;
        Assert.Contains(outputs, o => o.Unit.EndsWith(":" + table, StringComparison.Ordinal)); // the synthesized table, keyed by its id
        await DeleteAsync(repo, refund);
        var files = repo.Files();

        // The entity's id and its synthesized table's key: their outputs go, and nothing else changes (the schema keeps the table
        // until a run selects it).
        var scoped = await ApplyAsync(repo, new GenerationRequest { Elements = [refund, table] });
        Assert.All(scoped.Changes, c => Assert.True(c.UnitKey.EndsWith(":" + refund, StringComparison.Ordinal) || c.UnitKey.EndsWith(":" + table, StringComparison.Ordinal), c.UnitKey));
        Assert.Equal(0, scoped.UnitsRendered);
        foreach (var (path, _) in outputs)
            Assert.Contains(scoped.Changes, c => c.Path == path && c.Kind is FileChangeKind.Deleted or FileChangeKind.OrphanedOwned or FileChangeKind.Kept);
        var gone = scoped.Changes.Where(c => c.Kind == FileChangeKind.Deleted).Select(c => c.Path).ToList();
        Assert.NotEmpty(gone);
        Assert.All(gone, p => Assert.False(repo.Repo.Exists(p), p));
        AssertOthersKept(repo, files, scoped.Changes, unit => unit.EndsWith(":" + refund, StringComparison.Ordinal) || unit.EndsWith(":" + table, StringComparison.Ordinal));
        Assert.Contains("refund", repo.Repo.ReadFile("db/main/schema.sql"), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_scoped_plan_applies_without_marking_unselected_units_stale()
    {
        await using var repo = EditorRepo.Create();
        await SettleAsync(repo);
        var files = repo.Files();
        await WidenCustomerNameAsync(repo);

        // The plan holds the customer's units only, and stores the filter; its apply plans again with it, so the units the edit also
        // changed (the table, the schema, the migration) are neither appeared nor stale.
        var planned = await repo.Service.PlanAsync(new GenerationRequest { Elements = [CustomerId] }, null, Ct);
        Assert.Equal(RunOutcome.Succeeded, planned.Outcome);
        Assert.NotEmpty(planned.Plan!.Units);
        Assert.All(planned.Plan.Units, u => Assert.EndsWith(":" + CustomerId, u.Key, StringComparison.Ordinal));
        Assert.Equal(new[] { CustomerId }, (await repo.Service.GetPlanAsync(planned.Plan.Id, Ct))!.Request.Elements!);
        var full = await repo.Service.PlanAsync(new GenerationRequest(), null, Ct);
        Assert.Contains(full.Plan!.Units, u => !u.Skipped && !u.Key.EndsWith(":" + CustomerId, StringComparison.Ordinal));

        var applied = await repo.Service.ApplyAsync(planned.Plan.Id, null, Ct);
        Assert.True(applied.Outcome == RunOutcome.Succeeded, applied.Outcome + " " + string.Join(", ", applied.StaleUnits.Concat(applied.StalePaths)));
        Assert.NotEmpty(applied.Result!.Changes);
        Assert.All(applied.Result.Changes, c => Assert.EndsWith(":" + CustomerId, c.UnitKey, StringComparison.Ordinal));
        AssertOthersKept(repo, files, applied.Result.Changes, OfCustomer);
    }

    [Fact]
    public async Task A_scoped_run_neither_answers_from_nor_writes_the_last_run_record()
    {
        await using var repo = EditorRepo.Create();
        await SettleAsync(repo);
        var (full, _) = await OneShotAsync(repo, new GenerationRequest());
        E2ERepo.AssertOutcome(RunOutcome.Succeeded, full);
        Assert.True(File.Exists(RecordPath(repo)), "the full run records");
        Assert.True((await OneShotAsync(repo, new GenerationRequest())).Replayed, "nothing changed: answered from the record");

        // Nothing changed, yet the scoped run is not answered from the record, and it leaves none behind.
        foreach (var request in new[] { new GenerationRequest { Elements = [CustomerId] }, new GenerationRequest { Units = ["sql-ddl/table"] } })
        {
            var (scoped, replayed) = await OneShotAsync(repo, request);
            E2ERepo.AssertOutcome(RunOutcome.Succeeded, scoped);
            Assert.False(replayed, "a scoped run loads the model");
            Assert.False(File.Exists(RecordPath(repo)), "a scoped run writes no record");
            await OneShotAsync(repo, new GenerationRequest()); // records again
            Assert.True(File.Exists(RecordPath(repo)));
        }
    }

    [Fact]
    public async Task A_scoped_run_forgets_the_last_run_record_so_a_full_run_does_not_replay_it()
    {
        await using var repo = EditorRepo.Create();
        await SettleAsync(repo);
        await OneShotAsync(repo, new GenerationRequest());
        Assert.True(File.Exists(RecordPath(repo)));
        await WidenCustomerNameAsync(repo);

        var (scoped, _) = await OneShotAsync(repo, new GenerationRequest { Elements = [CustomerId] });
        E2ERepo.AssertOutcome(RunOutcome.Succeeded, scoped);
        Assert.False(File.Exists(RecordPath(repo)), "the scoped run forgot the record before it applied");

        // The full run goes through the pipeline and renders what the scoped run left (the table, the schema, the migration).
        var (full, replayed) = await OneShotAsync(repo, new GenerationRequest());
        E2ERepo.AssertOutcome(RunOutcome.Succeeded, full);
        Assert.False(replayed);
        Assert.Contains(full.Changes, c => c.UnitKey.StartsWith("sql-ddl/", StringComparison.Ordinal));
        Assert.DoesNotContain(full.Changes, c => c.UnitKey.EndsWith(":" + CustomerId, StringComparison.Ordinal));

        // The snapshot advanced, so the migration's diff empties and it renders once more; that run records, and the next is answered.
        await OneShotAsync(repo, new GenerationRequest());
        Assert.True(File.Exists(RecordPath(repo)));
        Assert.True((await OneShotAsync(repo, new GenerationRequest())).Replayed);
    }

    [Fact]
    public async Task A_scoped_run_rendering_the_migration_unit_is_MQ6033_and_keeps_the_schema_snapshot()
    {
        await using var repo = EditorRepo.Create();
        await SettleAsync(repo);
        var snapshot = repo.Repo.ReadFile(SnapshotPath);
        await WidenCustomerNameAsync(repo);

        // The database element selects every unit of `each database`, the migration among them: it fails and writes nothing.
        var scoped = await repo.Service.RunAsync(new GenerationRequest { Elements = [MainDatabaseId] }, null, Ct);
        E2ERepo.AssertOutcome(RunOutcome.Invalid, scoped);
        var error = Assert.Single(scoped.Diagnostics, d => d.Rule == "MQ6033");
        Assert.Equal(MainDatabaseId, error.ElementId);
        Assert.Contains("sql-ddl/migration:" + MainDatabaseId, error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(scoped.Changes, c => c.UnitKey.StartsWith("sql-ddl/migration", StringComparison.Ordinal));
        Assert.Equal(snapshot, repo.Repo.ReadFile(SnapshotPath));

        // A plan of the same scope is invalid too, and a full run renders the migration and advances the snapshot.
        var plan = await repo.Service.PlanAsync(new GenerationRequest { Elements = [MainDatabaseId] }, null, Ct);
        Assert.Equal(RunOutcome.Invalid, plan.Outcome);
        Assert.Contains(plan.Plan!.Diagnostics, d => d.Rule == "MQ6033");
        var full = await ApplyAsync(repo, new GenerationRequest());
        Assert.Contains(full.Changes, c => c.UnitKey.StartsWith("sql-ddl/migration", StringComparison.Ordinal));
        Assert.NotEqual(snapshot, repo.Repo.ReadFile(SnapshotPath));
    }

    [Fact]
    public async Task The_schema_unit_of_one_database_is_scopable()
    {
        await using var repo = EditorRepo.Create();
        await SettleAsync(repo);
        var snapshot = repo.Repo.ReadFile(SnapshotPath);
        var files = repo.Files();
        await WidenCustomerNameAsync(repo);

        var scoped = await ApplyAsync(repo, new GenerationRequest { Units = ["sql-ddl/schema"], Elements = [MainDatabaseId] });
        var change = Assert.Single(scoped.Changes);
        Assert.Equal(("db/main/schema.sql", FileChangeKind.Modified, "sql-ddl/schema:" + MainDatabaseId), (change.Path, change.Kind, change.UnitKey));
        Assert.Contains("(150)", repo.Repo.ReadFile("db/main/schema.sql"), StringComparison.Ordinal);
        Assert.Equal(snapshot, repo.Repo.ReadFile(SnapshotPath));
        AssertOthersKept(repo, files, scoped.Changes, unit => unit == "sql-ddl/schema:" + MainDatabaseId);

        // The full run renders the rest (the table, the migration) from the snapshot the scoped run left alone; the schema is current.
        var full = await ApplyAsync(repo, new GenerationRequest());
        Assert.Contains(full.Changes, c => c.UnitKey.StartsWith("sql-ddl/migration", StringComparison.Ordinal));
        Assert.DoesNotContain(full.Changes, c => c.Path == "db/main/schema.sql");
        Assert.NotEqual(snapshot, repo.Repo.ReadFile(SnapshotPath));
    }

    [Fact]
    public async Task An_ambiguous_bare_unit_id_is_MQ6032()
    {
        await using var repo = EditorRepo.Create();
        repo.EditSettingsOnDisk(s => s["packs"]!["notes"] = new JsonObject { ["output"] = "db/notes" });
        repo.Repo.WriteFile(".maquettiste/templates/notes/pack.json", """
            {
              "name": "notes",
              "version": "1.0.0",
              "engine": ">=1.0 <2.0",
              "description": "One note per database.",
              "units": [
                { "id": "schema", "template": "schema.scriban", "for": "each database", "output": "{{ database.name }}.txt" }
              ]
            }

            """);
        repo.Repo.WriteFile(".maquettiste/templates/notes/schema.scriban", "{{ database.name }}\n");

        var ambiguous = await repo.Service.RunAsync(new GenerationRequest { Units = ["schema"] }, null, Ct);
        E2ERepo.AssertOutcome(RunOutcome.Invalid, ambiguous);
        var error = Assert.Single(ambiguous.Diagnostics, d => d.Rule == "MQ6032");
        Assert.Contains("'notes/schema' or 'sql-ddl/schema'", error.Message, StringComparison.Ordinal);
        Assert.Empty(ambiguous.Changes);

        // A pack the run lacks (missing, or not among the requested packs) and a unit its pack lacks are MQ6032 too.
        foreach (var request in new[]
        {
            new GenerationRequest { Units = ["nosuch/schema"] },
            new GenerationRequest { Units = ["notes/schema"], Packs = ["sql-ddl"] },
            new GenerationRequest { Units = ["sql-ddl/nosuch"] },
            new GenerationRequest { Units = ["nosuch"] },
        })
        {
            var unknown = await repo.Service.RunAsync(request, null, Ct);
            E2ERepo.AssertOutcome(RunOutcome.Invalid, unknown);
            Assert.Single(unknown.Diagnostics, d => d.Rule == "MQ6032");
            Assert.DoesNotContain(unknown.Diagnostics, d => d.Rule == "MQ6001");
        }

        // Named with its pack, the unit runs, and only its pack loads.
        var named = await ApplyAsync(repo, new GenerationRequest { Units = ["notes/schema"] });
        var change = Assert.Single(named.Changes);
        Assert.Equal(("db/notes/main.txt", "notes/schema:" + MainDatabaseId), (change.Path, change.UnitKey));
        Assert.False(repo.Repo.Exists("db/main/schema.sql"));
    }

    [Fact]
    public async Task A_filter_selecting_nothing_is_MQ6034()
    {
        await using var repo = EditorRepo.Create();
        await SettleAsync(repo);
        var files = repo.Files();

        // An id nothing has or had, and a model-scope unit with an element (which never selects one).
        foreach (var request in new[]
        {
            new GenerationRequest { Elements = ["01J92P0VZZ0000000000000099"] },
            new GenerationRequest { Units = ["csharp-dapper/registrations"], Elements = [CustomerId] },
        })
        {
            var result = await repo.Service.RunAsync(request, null, Ct);
            E2ERepo.AssertOutcome(RunOutcome.Succeeded, result);
            var warning = Assert.Single(result.Diagnostics, d => d.Rule == "MQ6034");
            Assert.Equal(DiagnosticSeverity.Warning, warning.Severity);
            Assert.Empty(result.Changes);
            Assert.Equal(0, result.UnitsRendered + result.UnitsSkipped);
            Assert.Equal(files, repo.Files());
        }

        // A filter that selects only skipped units is not MQ6034: it selects units, which have nothing to do.
        var quiet = await repo.Service.RunAsync(new GenerationRequest { Elements = [CustomerId] }, null, Ct);
        E2ERepo.AssertOutcome(RunOutcome.Succeeded, quiet);
        Assert.DoesNotContain(quiet.Diagnostics, d => d.Rule == "MQ6034");
        Assert.True(quiet.UnitsSkipped > 0);
    }

    [Fact]
    public async Task A_full_run_after_a_scoped_run_skips_every_unit()
    {
        await using var repo = EditorRepo.Create();
        await SettleAsync(repo);

        // Forced scoped runs render their units again; every state they do not select is carried over, so nothing is left to do.
        var forced = await ApplyAsync(repo, new GenerationRequest { Elements = [CustomerId], Force = true });
        Assert.True(forced.UnitsRendered > 0);
        Assert.True((await ApplyAsync(repo, new GenerationRequest { Units = ["sql-ddl/table", "csharp-dapper/registrations"], Force = true })).UnitsRendered > 0);
        var full = await ApplyAsync(repo, new GenerationRequest());
        Assert.Equal(0, full.UnitsRendered);
        Assert.Empty(full.Changes);
    }

    private static bool OfCustomer(string unitKey) => unitKey.EndsWith(":" + CustomerId, StringComparison.Ordinal);

    /// <summary>
    /// Every file the run did not change is as it was (the model aside: the test's edits), and every manifest entry of a unit the
    /// run did not select is kept as it was.
    /// </summary>
    private static void AssertOthersKept(EditorRepo repo, SortedDictionary<string, string> before, IReadOnlyList<FileChange> changes, Func<string, bool> selected)
    {
        var changed = changes.Select(c => c.Path).ToHashSet(StringComparer.Ordinal);
        var now = repo.Files();
        foreach (var (path, bytes) in before)
        {
            if (changed.Contains(path) || path.StartsWith(".maquettiste/model/", StringComparison.Ordinal) || path.StartsWith(".maquettiste/manifest/", StringComparison.Ordinal))
                continue;
            Assert.True(now.TryGetValue(path, out var after) && after == bytes, path + " changed");
        }

        var entries = Entries(now);
        foreach (var (path, (unit, hash)) in Entries(before).Where(e => !selected(e.Value.Unit)))
            Assert.True(entries.TryGetValue(path, out var kept) && kept == (unit, hash), path + " lost its manifest entry");
    }

    /// <summary>Every manifest entry by path: the unit key (companion mark removed) and the hash.</summary>
    private static Dictionary<string, (string Unit, string Hash)> Entries(SortedDictionary<string, string> files)
    {
        var entries = new Dictionary<string, (string, string)>(StringComparer.Ordinal);
        foreach (var (path, hex) in files.Where(f => f.Key.StartsWith(".maquettiste/manifest/", StringComparison.Ordinal)))
        {
            var node = JsonNode.Parse(Convert.FromHexString(hex))!;
            var pack = (string)node["pack"]!;
            foreach (var row in node["files"]!.AsArray())
                entries[(string)row![0]!] = (pack + "/" + UnitSelection.StripCompanion((string)row[2]!), (string)row[1]!);
        }

        return entries;
    }

    /// <summary>
    /// Full runs until nothing is left to do: the first saves the schema snapshot, so the migration unit, whose diff then empties,
    /// renders once more in the second.
    /// </summary>
    private static async Task SettleAsync(EditorRepo repo)
    {
        await ApplyAsync(repo, new GenerationRequest());
        await ApplyAsync(repo, new GenerationRequest());
        var settled = await ApplyAsync(repo, new GenerationRequest());
        Assert.Equal(0, settled.UnitsRendered);
        Assert.Empty(settled.Changes);
    }

    private static EditorRepo CreateWithoutFacts()
    {
        // Without migration facts: deleting an entity drops its table, whose pending fact would stop generation.
        var repo = EditorRepo.Create();
        repo.EditSettingsOnDisk(E2ERepo.RecordOff);
        return repo;
    }

    private static async Task<GenerationResult> ApplyAsync(EditorRepo repo, GenerationRequest request)
    {
        var result = await repo.Service.RunAsync(request with { Jobs = 2 }, null, Ct);
        E2ERepo.AssertOutcome(RunOutcome.Succeeded, result);
        return result;
    }

    /// <summary>A one-shot host's run (the command-line tool's): a fresh store, and the last-run record in use.</summary>
    private static async Task<(GenerationResult Result, bool Replayed)> OneShotAsync(EditorRepo repo, GenerationRequest request)
    {
        var store = new ModelStore(repo.Repo.Options);
        await using (store)
        {
            var service = new GenerationService(store, repo.Repo.Options) { ReuseLastRun = true };
            var result = await service.RunAsync(request with { Jobs = 2 }, null, Ct);
            return (result, store.Current is null);
        }
    }

    private static string RecordPath(EditorRepo repo) => Path.Combine(repo.Repo.CacheDirectory, RunRecord.FileName);

    private static async Task<Dictionary<string, UnitState>> StatesAsync(EditorRepo repo)
    {
        var states = new Dictionary<string, UnitState>(StringComparer.Ordinal);
        foreach (var pack in Packs)
        {
            foreach (var (key, state) in await repo.Service.Services.UnitState.LoadAsync(pack, Ct))
                states[key] = state;
        }

        return states;
    }

    /// <summary>Widens Customer.name from 120 to 150: the customer's units, its table, the schema and the migration render again.</summary>
    private static async Task WidenCustomerNameAsync(EditorRepo repo)
    {
        var document = await repo.Store.GetElementAsync(CustomerId, Ct);
        Assert.NotNull(document);
        var node = JsonNode.Parse(document.Json.GetRawText())!.AsObject();
        node["attributes"]![1]!["length"] = 150;
        var saved = await repo.Store.SaveAsync(CustomerId, Encoding.UTF8.GetBytes(node.ToJsonString()), document.Hash, ChangeSource.Editor, Ct);
        Assert.True(saved.Outcome == SaveOutcome.Saved, saved.Outcome + ": " + string.Join("; ", saved.Diagnostics.Select(d => d.Rule + " " + d.Message)));
    }

    private static async Task<string> CreateRefundAsync(EditorRepo repo)
    {
        var created = await repo.Store.CreateAsync(Encoding.UTF8.GetBytes($$"""
            {"kind": "entity", "name": "Refund", "package": "{{EditorRepo.BillingPackageId}}",
             "key": {"attributes": ["{{RefundAttributeId}}"]},
             "attributes": [{"id": "{{RefundAttributeId}}", "name": "id", "type": "uuid", "required": true}]}
            """), ChangeSource.Editor, Ct);
        Assert.True(created.Outcome == SaveOutcome.Saved, created.Outcome + ": " + string.Join("; ", created.Diagnostics.Select(d => d.Rule + " " + d.Message)));
        return created.Id!;
    }

    private static async Task DeleteAsync(EditorRepo repo, string id)
    {
        var document = await repo.Store.GetElementAsync(id, Ct);
        Assert.NotNull(document);
        var deleted = await repo.Store.DeleteAsync(id, document.Hash, DeleteResolution.Refuse, ChangeSource.Editor, Ct);
        Assert.Equal(SaveOutcome.Saved, deleted.Outcome);
    }

    /// <summary>Every manifest entry of a unit of the element or of its synthesized table, as (path, unit key).</summary>
    private static async Task<List<(string Path, string Unit)>> RefundOutputsAsync(EditorRepo repo, string refund)
    {
        var outputs = new List<(string, string)>();
        foreach (var (key, state) in await StatesAsync(repo))
        {
            if (key.EndsWith(":" + refund, StringComparison.Ordinal) || key.EndsWith(":" + refund + "@" + MainDatabaseId, StringComparison.Ordinal))
                outputs.AddRange(state.Outputs.Select(o => (o.Path, key)));
        }

        Assert.NotEmpty(outputs);
        return outputs;
    }
}
