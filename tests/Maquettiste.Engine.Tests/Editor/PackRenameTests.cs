using System.Text;
using System.Text.Json.Nodes;
using Maquettiste.Engine.Pipeline;

namespace Maquettiste.Engine.Tests.Editor;

/// <summary>Renaming a pack (engine-design.md 12.3a): everything that carries its name moves together, so its files stay tracked.</summary>
public sealed class PackRenameTests
{
    private static CancellationToken Ct => EditorRepo.Ct;

    /// <summary>The repo's files minus the engine's cache folder (the run lock file appears there on first use).</summary>
    private static List<KeyValuePair<string, string>> Model(EditorRepo repo) =>
        [.. repo.Files().Where(f => !f.Key.StartsWith(".maquettiste/.cache/", StringComparison.Ordinal))];

    [Fact]
    public async Task Renaming_a_pack_moves_its_folder_settings_entry_manifests_and_state_and_keeps_its_files_tracked()
    {
        await using var repo = EditorRepo.Create();
        var plan = await repo.Service.PlanAsync(new GenerationRequest { Packs = ["sql-ddl"] }, null, Ct);
        await repo.Service.ApplyAsync(plan.Plan!.Id, null, Ct);
        var outputs = (await repo.Service.GetPackOutputsAsync("sql-ddl", Ct))!.Outputs.Select(o => o.Path).ToList();
        Assert.NotEmpty(outputs);
        var states = Path.Combine(repo.Repo.CacheDirectory, "units");
        var customer = await repo.Store.GetElementAsync(EditorRepo.CustomerId, Ct);
        var hinted = JsonNode.Parse(customer!.Json.GetRawText())!.AsObject();
        hinted["generation"] = new JsonObject { ["sql-ddl"] = new JsonObject { ["variables"] = new JsonObject { ["note"] = "kept" } } };
        Assert.Equal(SaveOutcome.Saved, (await repo.Store.SaveAsync(EditorRepo.CustomerId, Encoding.UTF8.GetBytes(hinted.ToJsonString()), customer.Hash,
            ChangeSource.Editor, Ct)).Outcome);
        var pack = await repo.Service.GetPackAsync("sql-ddl", Ct);
        var before = Model(repo);

        var preview = await repo.Service.RenamePackAsync("sql-ddl", "ddl", pack!.Hash, Ct, dryRun: true);
        Assert.Equal(SaveOutcome.Saved, preview.Outcome);
        Assert.Equal(before, Model(repo));
        Assert.Null(preview.Hash);

        var renamed = await repo.Service.RenamePackAsync("sql-ddl", "ddl", pack.Hash, Ct);

        Assert.Equal(SaveOutcome.Saved, renamed.Outcome);
        Assert.Equal(("sql-ddl", "ddl"), (renamed.From, renamed.To));
        Assert.Equal(pack.Files.Select(f => f.Path).Order(StringComparer.Ordinal), renamed.Files);
        Assert.Equal(preview.Files, renamed.Files);
        Assert.Equal(outputs.Order(StringComparer.Ordinal), renamed.Tracked);
        Assert.Equal([EditorRepo.CustomerId], renamed.Hints);
        Assert.Empty(renamed.HintsUpdated);
        Assert.Contains("\"sql-ddl\"", (await repo.Store.GetElementAsync(EditorRepo.CustomerId, Ct))!.Json.GetRawText(), StringComparison.Ordinal);
        Assert.False(Directory.Exists(repo.Repo.PathOf(".maquettiste/templates/sql-ddl")));
        var moved = await repo.Service.GetPackAsync("ddl", Ct);
        Assert.Equal(renamed.Hash, moved!.Hash);
        Assert.Equal("ddl", moved.Document!.Value.GetProperty("name").GetString());
        Assert.DoesNotContain(moved.Diagnostics, d => d.Severity == Diagnostics.DiagnosticSeverity.Error);
        Assert.False(File.Exists(repo.Repo.PathOf(".maquettiste/manifest/sql-ddl.json")));
        Assert.True(File.Exists(repo.Repo.PathOf(".maquettiste/manifest/ddl.json")));
        Assert.DoesNotContain(Directory.EnumerateFiles(states), f => Path.GetFileName(f).StartsWith("sql-ddl.", StringComparison.Ordinal));
        Assert.Contains(Directory.EnumerateFiles(states), f => Path.GetFileName(f).StartsWith("ddl.", StringComparison.Ordinal));
        var settings = await repo.Store.GetSettingsAsync(Ct);
        Assert.Equal(renamed.SettingsHash, settings.Hash);
        Assert.False(settings.Settings.Packs.ContainsKey("sql-ddl"));
        Assert.Equal("db", settings.Settings.Packs["ddl"].Output);
        Assert.Equal(outputs.Order(StringComparer.Ordinal), (await repo.Service.GetPackOutputsAsync("ddl", Ct))!.Outputs.Select(o => o.Path));
        Assert.Equal(["csharp-dapper", "ddl"], (await repo.Service.ListPacksAsync(Ct)).Select(p => p.Name));

        // A run over every pack finds the files tracked under the new name: none is deleted as an orphan or reported as a hand edit.
        var next = await repo.Service.PlanAsync(new GenerationRequest(), null, Ct);
        Assert.DoesNotContain(next.Plan!.Changes, c => outputs.Contains(c.Path, StringComparer.Ordinal)
            && c.Kind is FileChangeKind.Deleted or FileChangeKind.OrphanedOwned or FileChangeKind.HandEdited);
        await repo.Service.ApplyAsync(next.Plan.Id, null, Ct);
        Assert.All(outputs, o => Assert.True(File.Exists(repo.Repo.PathOf(o)), o));
        Assert.Equal(outputs.Order(StringComparer.Ordinal), (await repo.Service.GetPackOutputsAsync("ddl", Ct))!.Outputs.Select(o => o.Path));
    }

    [Fact]
    public async Task With_update_hints_the_hints_that_name_the_pack_move_to_the_new_name_in_one_batch()
    {
        await using var repo = EditorRepo.Create();
        var customer = await repo.Store.GetElementAsync(EditorRepo.CustomerId, Ct);
        var hinted = JsonNode.Parse(customer!.Json.GetRawText())!.AsObject();
        hinted["generation"] = new JsonObject
        {
            ["*"] = new JsonObject { ["skip"] = false },
            ["sql-ddl"] = new JsonObject { ["skip"] = true },
            ["csharp-dapper"] = new JsonObject { ["rename"] = "Client" },
        };
        hinted["attributes"]![0]!["generation"] = new JsonObject { ["sql-ddl"] = new JsonObject { ["rename"] = "customer_id" } };
        Assert.Equal(SaveOutcome.Saved, (await repo.Store.SaveAsync(EditorRepo.CustomerId, Encoding.UTF8.GetBytes(hinted.ToJsonString()), customer.Hash,
            ChangeSource.Editor, Ct)).Outcome);
        var invoice = await repo.Store.GetElementAsync(EditorRepo.InvoiceId, Ct);
        var pack = await repo.Service.GetPackAsync("sql-ddl", Ct);

        var preview = await repo.Service.RenamePackAsync("sql-ddl", "ddl", pack!.Hash, Ct, dryRun: true, updateHints: true);
        Assert.Equal([EditorRepo.CustomerId], preview.Hints);
        Assert.Empty(preview.HintsUpdated);

        var renamed = await repo.Service.RenamePackAsync("sql-ddl", "ddl", pack.Hash, Ct, updateHints: true);

        Assert.Equal(SaveOutcome.Saved, renamed.Outcome);
        Assert.Equal([EditorRepo.CustomerId], renamed.HintsUpdated);
        Assert.Empty(renamed.Diagnostics);
        var after = JsonNode.Parse((await repo.Store.GetElementAsync(EditorRepo.CustomerId, Ct))!.Json.GetRawText())!;
        Assert.Equal(["*", "csharp-dapper", "ddl"], after["generation"]!.AsObject().Select(p => p.Key).Order(StringComparer.Ordinal));
        Assert.True(after["generation"]!["ddl"]!["skip"]!.GetValue<bool>());
        Assert.Equal("customer_id", after["attributes"]![0]!["generation"]!["ddl"]!["rename"]!.GetValue<string>());
        Assert.Null(after["attributes"]![0]!["generation"]!["sql-ddl"]);
        Assert.Equal(invoice!.Hash, (await repo.Store.GetElementAsync(EditorRepo.InvoiceId, Ct))!.Hash);
        Assert.Null(await repo.Service.RenamePackHintsAsync("sql-ddl", "ddl", Ct));
    }

    [Fact]
    public void A_hint_map_that_already_has_the_new_name_is_left_as_it_is()
    {
        var node = JsonNode.Parse("""{"generation":{"a":{"skip":true},"b":{"skip":false}},"members":[{"generation":{"a":{"rename":"x"}}}]}""");
        Assert.Equal(1, PackHints.Rename(node, "a", "b"));
        Assert.Equal("""{"generation":{"a":{"skip":true},"b":{"skip":false}},"members":[{"generation":{"b":{"rename":"x"}}}]}""", node!.ToJsonString());
    }

    [Fact]
    public async Task A_stale_hash_a_taken_or_bad_name_and_an_unknown_pack_change_nothing()
    {
        await using var repo = EditorRepo.Create();
        var hash = (await repo.Service.GetPackAsync("sql-ddl", Ct))!.Hash;
        var before = Model(repo);

        var stale = await repo.Service.RenamePackAsync("sql-ddl", "ddl", new string('0', 64), Ct);
        Assert.Equal(SaveOutcome.Conflict, stale.Outcome);
        Assert.Equal(hash, stale.Hash);
        Assert.NotNull(stale.Current);

        Assert.Equal(SaveOutcome.Invalid, (await repo.Service.RenamePackAsync("sql-ddl", "csharp-dapper", hash, Ct)).Outcome);
        Assert.Equal(SaveOutcome.Invalid, (await repo.Service.RenamePackAsync("sql-ddl", "Bad_Name", hash, Ct)).Outcome);
        Assert.Equal(SaveOutcome.Invalid, (await repo.Service.RenamePackAsync("sql-ddl", "sql-ddl", hash, Ct)).Outcome);
        Assert.Equal(SaveOutcome.NotFound, (await repo.Service.RenamePackAsync("ghost", "ddl", hash, Ct)).Outcome);
        await Assert.ThrowsAsync<PackPathException>(() => repo.Service.RenamePackAsync("../model", "ddl", hash, Ct));
        Assert.Equal(before, Model(repo));

        // A settings entry for the new name with no folder would be overwritten: refused.
        repo.EditSettingsOnDisk(s => s["packs"]!["ddl"] = new JsonObject { ["output"] = "other" });
        var taken = await repo.Service.RenamePackAsync("sql-ddl", "ddl", hash, Ct);
        Assert.Equal(SaveOutcome.Invalid, taken.Outcome);
        Assert.Contains("packs.ddl", taken.Diagnostics.Single().Message, StringComparison.Ordinal);
        Assert.True(File.Exists(repo.Repo.PathOf(".maquettiste/templates/sql-ddl/pack.json")));
    }

    [Fact]
    public async Task A_pack_without_a_settings_entry_or_outputs_is_renamed_without_touching_the_settings()
    {
        await using var repo = EditorRepo.Create();
        var created = await repo.Service.CreatePackAsync("docs", "empty", null, Ct);
        var before = (await repo.Store.GetSettingsAsync(Ct)).Hash;

        var renamed = await repo.Service.RenamePackAsync("docs", "notes", created.Hash!, Ct);

        Assert.Equal(SaveOutcome.Saved, renamed.Outcome);
        Assert.Null(renamed.SettingsHash);
        Assert.Empty(renamed.Tracked);
        Assert.Equal(["entity.scriban", "pack.json"], renamed.Files);
        Assert.Equal(before, (await repo.Store.GetSettingsAsync(Ct)).Hash);
        Assert.False(Directory.Exists(repo.Repo.PathOf(".maquettiste/templates/docs")));
        Assert.Contains("\"name\": \"notes\"", File.ReadAllText(repo.Repo.PathOf(".maquettiste/templates/notes/pack.json")), StringComparison.Ordinal);
    }
}
