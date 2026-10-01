using System.Text.Json;
using Maquettiste.Engine.Pipeline;

namespace Maquettiste.Engine.Tests.Editor;

/// <summary>Removing a pack (engine-design.md 12.3a) and the element names of unit paths.</summary>
public sealed class PackRemoveTests
{
    private static CancellationToken Ct => EditorRepo.Ct;

    [Fact]
    public async Task Removing_a_pack_deletes_its_folder_settings_entry_manifests_and_state_and_leaves_its_files_untracked()
    {
        await using var repo = EditorRepo.Create();
        var plan = await repo.Service.PlanAsync(new GenerationRequest { Packs = ["sql-ddl"] }, null, Ct);
        await repo.Service.ApplyAsync(plan.Plan!.Id, null, Ct);
        var outputs = (await repo.Service.GetPackOutputsAsync("sql-ddl", Ct))!.Outputs.Select(o => o.Path).ToList();
        Assert.NotEmpty(outputs);
        var manifest = repo.Repo.PathOf(".maquettiste/manifest/sql-ddl.json");
        Assert.True(File.Exists(manifest));
        var states = Path.Combine(repo.Repo.CacheDirectory, "units");
        Assert.Contains(Directory.EnumerateFiles(states), f => Path.GetFileName(f).StartsWith("sql-ddl.", StringComparison.Ordinal));
        var pack = await repo.Service.GetPackAsync("sql-ddl", Ct);

        var removed = await repo.Service.DeletePackAsync("sql-ddl", pack!.Hash, Ct);

        Assert.Equal(SaveOutcome.Saved, removed.Outcome);
        Assert.Equal(pack.Files.Select(f => f.Path).Order(StringComparer.Ordinal), removed.Files);
        Assert.Equal(outputs.Order(StringComparer.Ordinal), removed.Untracked);
        Assert.NotNull(removed.SettingsHash);
        Assert.False(Directory.Exists(repo.Repo.PathOf(".maquettiste/templates/sql-ddl")));
        Assert.True(Directory.Exists(repo.Repo.PathOf(".maquettiste/templates/csharp-dapper")));
        Assert.False(File.Exists(manifest));
        Assert.DoesNotContain(Directory.EnumerateFiles(states), f => Path.GetFileName(f).StartsWith("sql-ddl.", StringComparison.Ordinal));
        var settings = await repo.Store.GetSettingsAsync(Ct);
        Assert.Equal(removed.SettingsHash, settings.Hash);
        Assert.False(settings.Settings.Packs.ContainsKey("sql-ddl"));
        Assert.True(settings.Settings.Packs.ContainsKey("csharp-dapper"));
        Assert.All(outputs, o => Assert.True(File.Exists(repo.Repo.PathOf(o)), o));
        Assert.DoesNotContain(await repo.Service.ListPacksAsync(Ct), p => p.Name == "sql-ddl");

        // A run over every pack has no manifest of the removed pack to orphan: its files stay.
        var next = await repo.Service.PlanAsync(new GenerationRequest(), null, Ct);
        Assert.DoesNotContain(next.Plan!.Changes, c => outputs.Contains(c.Path, StringComparer.Ordinal) && c.Kind == FileChangeKind.Deleted);
        await repo.Service.ApplyAsync(next.Plan.Id, null, Ct);
        Assert.All(outputs, o => Assert.True(File.Exists(repo.Repo.PathOf(o)), o));
    }

    [Fact]
    public async Task A_stale_hash_removes_nothing_and_an_unknown_or_bad_name_is_refused()
    {
        await using var repo = EditorRepo.Create();
        var settingsBefore = (await repo.Store.GetSettingsAsync(Ct)).Hash;

        var stale = await repo.Service.DeletePackAsync("sql-ddl", new string('0', 64), Ct);
        Assert.Equal(SaveOutcome.Conflict, stale.Outcome);
        Assert.Equal((await repo.Service.GetPackAsync("sql-ddl", Ct))!.Hash, stale.Hash);
        Assert.NotNull(stale.Current);
        Assert.True(File.Exists(repo.Repo.PathOf(".maquettiste/templates/sql-ddl/pack.json")));
        Assert.Equal(settingsBefore, (await repo.Store.GetSettingsAsync(Ct)).Hash);

        Assert.Equal(SaveOutcome.NotFound, (await repo.Service.DeletePackAsync("ghost", new string('0', 64), Ct)).Outcome);
        await Assert.ThrowsAsync<PackPathException>(() => repo.Service.DeletePackAsync("../model", new string('0', 64), Ct));
    }

    [Fact]
    public async Task A_pack_without_a_settings_entry_is_removed_without_touching_the_settings()
    {
        await using var repo = EditorRepo.Create();
        var created = await repo.Service.CreatePackAsync("docs", "empty", null, Ct);
        var before = (await repo.Store.GetSettingsAsync(Ct)).Hash;

        var removed = await repo.Service.DeletePackAsync("docs", created.Hash!, Ct);

        Assert.Equal(SaveOutcome.Saved, removed.Outcome);
        Assert.Null(removed.SettingsHash);
        Assert.Empty(removed.Untracked);
        Assert.Equal(["entity.scriban", "pack.json"], removed.Files);
        Assert.Equal(before, (await repo.Store.GetSettingsAsync(Ct)).Hash);
        Assert.False(Directory.Exists(repo.Repo.PathOf(".maquettiste/templates/docs")));
    }

    [Fact]
    public async Task A_link_in_the_pack_folder_is_removed_without_following_it()
    {
        await using var repo = EditorRepo.Create();
        var created = await repo.Service.CreatePackAsync("docs", "empty", null, Ct);
        var outside = Path.Combine(repo.Repo.RepoRoot, "keep");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "kept.txt"), "kept");
        try
        {
            Directory.CreateSymbolicLink(repo.Repo.PathOf(".maquettiste/templates/docs/linked"), outside);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return; // no link rights here (Windows without developer mode)
        }

        var removed = await repo.Service.DeletePackAsync("docs", created.Hash!, Ct);

        Assert.Equal(SaveOutcome.Saved, removed.Outcome);
        Assert.False(Directory.Exists(repo.Repo.PathOf(".maquettiste/templates/docs")));
        Assert.True(File.Exists(Path.Combine(outside, "kept.txt")));
    }

    [Fact]
    public async Task Unit_paths_name_their_element_and_kind()
    {
        await using var repo = EditorRepo.Create();

        var tables = await repo.Service.PathsAsync("sql-ddl", "table", null, null, 0, Ct);
        var customer = tables.Paths.Single(p => p.ElementId == EditorRepo.CustomerId + "@" + EditorRepo.MainDatabaseId);
        Assert.Equal("table", customer.ElementKind);
        Assert.EndsWith(" (main)", customer.ElementName, StringComparison.Ordinal);
        Assert.DoesNotContain("@", customer.ElementName, StringComparison.Ordinal);
        Assert.DoesNotContain(".", customer.ElementName, StringComparison.Ordinal); // billing is the default schema

        var entities = await repo.Service.PathsAsync("csharp-dapper", "entity", [EditorRepo.CustomerId], null, 0, Ct);
        Assert.All(entities.Paths, p => Assert.Equal(("Customer", "entity"), (p.ElementName, p.ElementKind)));

        var json = JsonSerializer.SerializeToElement(customer, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal(customer.ElementName, json.GetProperty("elementName").GetString());
        Assert.Equal("table", json.GetProperty("elementKind").GetString());
    }
}
