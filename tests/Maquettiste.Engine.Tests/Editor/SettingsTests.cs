using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Model;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Editor;

/// <summary>E3: <see cref="ModelStore.GetSettingsAsync"/> and <see cref="ModelStore.SaveSettingsAsync"/> (phase2-design.md section 3.8).</summary>
public sealed class SettingsTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static string Sha(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    [Fact]
    public async Task Get_returns_the_file_its_hash_and_the_typed_settings()
    {
        await using var repo = EditorRepo.Create();
        var bytes = File.ReadAllBytes(repo.Repo.PathOf(EditorRepo.SettingsPath));

        var document = await repo.Store.GetSettingsAsync(EditorRepo.Ct);

        Assert.Equal(EditorRepo.SettingsPath, document.Path);
        Assert.Equal(Sha(bytes), document.Hash);
        Assert.Equal(repo.Store.Current!.SettingsHash, document.Hash);
        Assert.Equal("billing", document.Settings.Name);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(bytes), JsonNode.Parse(document.Json.GetRawText())));
    }

    [Fact]
    public async Task Get_sees_a_disk_edit_the_index_has_not_seen_yet()
    {
        await using var repo = EditorRepo.Create();
        await repo.Store.LoadAsync(EditorRepo.Ct);
        repo.EditSettingsOnDisk(s => s["name"] = "billing-renamed");

        var document = await repo.Store.GetSettingsAsync(EditorRepo.Ct);

        Assert.Equal("billing-renamed", document.Settings.Name);
        Assert.Equal(Sha(File.ReadAllBytes(repo.Repo.PathOf(EditorRepo.SettingsPath))), document.Hash);
        Assert.Equal(document.Hash, repo.Store.Current!.SettingsHash);
    }

    [Fact]
    public async Task Get_without_a_settings_file_returns_the_defaults_and_the_hash_of_no_bytes()
    {
        await using var repo = EditorRepo.Create(packs: false);
        File.Delete(repo.Repo.PathOf(EditorRepo.SettingsPath));

        var document = await repo.Store.GetSettingsAsync(EditorRepo.Ct);

        Assert.Equal(Sha([]), document.Hash);
        Assert.Equal(1, document.Json.GetProperty("formatVersion").GetInt32());
        Assert.Null(document.Settings.Name);
    }

    [Fact]
    public async Task Save_writes_canonical_bytes_and_reloads_the_settings()
    {
        await using var repo = EditorRepo.Create();
        var loaded = await repo.Store.GetSettingsAsync(EditorRepo.Ct);
        var body = repo.SettingsWith(s => s["conventions"] = new JsonObject { ["columnCase"] = "camel" });

        var result = await repo.Store.SaveSettingsAsync(body, loaded.Hash, ChangeSource.Editor, EditorRepo.Ct);

        Assert.Equal(SaveOutcome.Saved, result.Outcome);
        var disk = File.ReadAllBytes(repo.Repo.PathOf(EditorRepo.SettingsPath));
        Assert.Equal(Sha(disk), result.Hash);
        Assert.Equal(TestServices.Json.Write(JsonNode.Parse(body)!, "maquettiste.json", "maquettiste.json"), disk);
        Assert.Equal(result.Hash, repo.Store.Current!.SettingsHash);
        Assert.Equal(CaseStyle.Camel, repo.Store.Current.Settings.Conventions.ColumnCase);
        Assert.Equal(result.Hash, result.Current!.Hash);
        Assert.Equal(CaseStyle.Camel, result.Current.Settings.Conventions.ColumnCase);
        Assert.Equal(result.Hash, (await repo.Store.GetSettingsAsync(EditorRepo.Ct)).Hash);

        // The new conventions reach the resolved model at once.
        var view = (await repo.Service.GetDatabaseViewAsync(EditorRepo.MainDatabaseId, EditorRepo.Ct)).View!;
        Assert.Contains(view.Tables.Single(t => t.EntityId == EditorRepo.CustomerId).Columns, c => c.Name == "customerSince");
    }

    [Fact]
    public async Task Saving_the_same_settings_again_writes_nothing_and_keeps_the_hash()
    {
        await using var repo = EditorRepo.Create();
        var loaded = await repo.Store.GetSettingsAsync(EditorRepo.Ct);
        var before = File.GetLastWriteTimeUtc(repo.Repo.PathOf(EditorRepo.SettingsPath));

        var result = await repo.Store.SaveSettingsAsync(Encoding.UTF8.GetBytes(loaded.Json.GetRawText()), loaded.Hash, ChangeSource.Editor, EditorRepo.Ct);

        Assert.Equal(SaveOutcome.Saved, result.Outcome);
        Assert.Equal(loaded.Hash, result.Hash);
        Assert.Equal(before, File.GetLastWriteTimeUtc(repo.Repo.PathOf(EditorRepo.SettingsPath)));
    }

    [Fact]
    public async Task A_stale_hash_is_a_conflict_with_the_disk_version_and_writes_nothing()
    {
        await using var repo = EditorRepo.Create();
        var loaded = await repo.Store.GetSettingsAsync(EditorRepo.Ct);
        repo.EditSettingsOnDisk(s => s["name"] = "edited-on-disk");
        var disk = File.ReadAllBytes(repo.Repo.PathOf(EditorRepo.SettingsPath));

        var result = await repo.Store.SaveSettingsAsync(repo.SettingsWith(s => s["name"] = "mine"), loaded.Hash, ChangeSource.Editor, EditorRepo.Ct);

        Assert.Equal(SaveOutcome.Conflict, result.Outcome);
        Assert.Equal(Sha(disk), result.Hash);
        Assert.Equal("edited-on-disk", result.Current!.Settings.Name);
        Assert.Equal(disk, File.ReadAllBytes(repo.Repo.PathOf(EditorRepo.SettingsPath)));
        Assert.Equal(Sha(disk), repo.Store.Current!.SettingsHash);
    }

    [Theory]
    [InlineData("""{ "formatVersion": 1, "handEdits": "sometimes" }""", "MQ1002")]
    [InlineData("""{ "formatVersion": 1, "unknownMember": true }""", "MQ1002")]
    [InlineData("""{ "formatVersion": 1, """, "MQ1001")]
    public async Task An_invalid_body_is_refused_and_writes_nothing(string body, string rule)
    {
        await using var repo = EditorRepo.Create();
        var loaded = await repo.Store.GetSettingsAsync(EditorRepo.Ct);
        var before = repo.Files();

        var result = await repo.Store.SaveSettingsAsync(Encoding.UTF8.GetBytes(body), loaded.Hash, ChangeSource.Editor, EditorRepo.Ct);

        Assert.Equal(SaveOutcome.Invalid, result.Outcome);
        Assert.Null(result.Hash);
        Assert.Null(result.Current);
        Assert.Contains(result.Diagnostics, d => d.Rule == rule && d.FilePath == EditorRepo.SettingsPath);
        Assert.Equal(before, repo.Files());
    }

    [Fact]
    public async Task A_settings_change_that_introduces_a_model_error_is_invalid_and_writes_nothing()
    {
        await using var repo = EditorRepo.Create();
        // A non-canonical element file is a warning (MQ1003); making that rule an error introduces an error.
        var customer = repo.Repo.PathOf(".maquettiste/model/entities/customer.json");
        File.WriteAllText(customer, JsonNode.Parse(File.ReadAllText(customer))!.ToJsonString(), new UTF8Encoding(false));
        var loaded = await repo.Store.GetSettingsAsync(EditorRepo.Ct);
        var before = repo.Files();

        var result = await repo.Store.SaveSettingsAsync(
            repo.SettingsWith(s => s["validation"] = new JsonObject { ["rules"] = new JsonObject { ["MQ1003"] = "error" } }),
            loaded.Hash, ChangeSource.Editor, EditorRepo.Ct);

        Assert.Equal(SaveOutcome.Invalid, result.Outcome);
        var introduced = Assert.Single(result.Diagnostics);
        Assert.Equal("MQ1003", introduced.Rule);
        Assert.Equal(DiagnosticSeverity.Error, introduced.Severity);
        Assert.Equal(before, repo.Files());
    }

    [Fact]
    public async Task A_pre_existing_model_error_does_not_block_an_unrelated_settings_save()
    {
        await using var repo = EditorRepo.Create();
        var payment = repo.Repo.PathOf(".maquettiste/model/entities/payment.json");
        var node = JsonNode.Parse(File.ReadAllText(payment))!.AsObject();
        node.Remove("key");
        File.WriteAllText(payment, node.ToJsonString(), new UTF8Encoding(false));
        var loaded = await repo.Store.GetSettingsAsync(EditorRepo.Ct);

        var result = await repo.Store.SaveSettingsAsync(repo.SettingsWith(s => s["name"] = "still-saves"), loaded.Hash, ChangeSource.Editor, EditorRepo.Ct);

        Assert.Equal(SaveOutcome.Saved, result.Outcome);
        Assert.Empty(result.Diagnostics);
        Assert.Equal("still-saves", repo.Store.Current!.Settings.Name);
    }

    [Fact]
    public async Task Save_creates_a_missing_settings_file_from_the_hash_of_no_bytes()
    {
        await using var repo = EditorRepo.Create(packs: false);
        File.Delete(repo.Repo.PathOf(EditorRepo.SettingsPath));
        var loaded = await repo.Store.GetSettingsAsync(EditorRepo.Ct);

        var result = await repo.Store.SaveSettingsAsync("""{ "formatVersion": 1, "name": "fresh" }"""u8.ToArray(), loaded.Hash, ChangeSource.Editor, EditorRepo.Ct);

        Assert.Equal(SaveOutcome.Saved, result.Outcome);
        Assert.True(repo.Repo.Exists(EditorRepo.SettingsPath));
        Assert.Equal("fresh", repo.Store.Current!.Settings.Name);
    }

    [Fact]
    public async Task Save_results_serialize_with_web_defaults()
    {
        await using var repo = EditorRepo.Create();
        var loaded = await repo.Store.GetSettingsAsync(EditorRepo.Ct);
        var result = await repo.Store.SaveSettingsAsync(repo.SettingsWith(s => s["name"] = "x"), loaded.Hash, ChangeSource.Editor, EditorRepo.Ct);

        var node = JsonNode.Parse(JsonSerializer.Serialize(result, Web))!;

        Assert.Equal("saved", node["outcome"]!.GetValue<string>());
        Assert.Equal("x", node["current"]!["settings"]!["name"]!.GetValue<string>());
        Assert.Equal("x", node["current"]!["json"]!["name"]!.GetValue<string>());
        Assert.Equal(EditorRepo.SettingsPath, node["current"]!["path"]!.GetValue<string>());
    }

    [Fact]
    public async Task Concurrent_saves_from_the_same_hash_save_once_and_conflict_otherwise()
    {
        await using var repo = EditorRepo.Create();
        var loaded = await repo.Store.GetSettingsAsync(EditorRepo.Ct);
        var bodies = Enumerable.Range(0, 4).Select(i => repo.SettingsWith(s => s["name"] = "billing-" + i)).ToList();

        var results = await Task.WhenAll(bodies.Select(b => Task.Run(() => repo.Store.SaveSettingsAsync(b, loaded.Hash, ChangeSource.Editor, EditorRepo.Ct))));

        var saved = Assert.Single(results, r => r.Outcome == SaveOutcome.Saved);
        Assert.All(results.Where(r => r != saved), r =>
        {
            Assert.Equal(SaveOutcome.Conflict, r.Outcome);
            Assert.Equal(saved.Hash, r.Hash);
            Assert.Equal(saved.Current!.Settings.Name, r.Current!.Settings.Name);
        });
        Assert.Equal(Sha(File.ReadAllBytes(repo.Repo.PathOf(EditorRepo.SettingsPath))), saved.Hash);
        Assert.Equal(saved.Hash, repo.Store.Current!.SettingsHash);
    }
}
