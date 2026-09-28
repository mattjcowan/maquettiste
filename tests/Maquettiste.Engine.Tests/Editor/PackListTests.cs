using System.Text.Json;
using System.Text.Json.Nodes;
using Maquettiste.Engine.Diagnostics;

namespace Maquettiste.Engine.Tests.Editor;

/// <summary>E2: <see cref="GenerationService.GetPacksAsync"/> (phase2-design.md section 3.8).</summary>
public sealed class PackListTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Lists_every_pack_with_its_manifest_in_folder_order()
    {
        await using var repo = EditorRepo.Create();

        var result = await repo.Service.GetPacksAsync(EditorRepo.Ct);

        Assert.Empty(result.Diagnostics);
        Assert.Equal(["csharp-dapper", "sql-ddl"], result.Packs.Select(p => p.Name));
        var sql = result.Packs[1];
        Assert.Equal(">=1.0 <2.0", sql.Engine);
        Assert.Contains(sql.Units, u => u.Id == "table" && u.For == "each table");
        Assert.True(sql.UsesSchemaDiff);
    }

    [Fact]
    public async Task A_disabled_pack_is_listed_too()
    {
        await using var repo = EditorRepo.Create();
        repo.EditSettingsOnDisk(s => s["packs"]!["sql-ddl"]!["enabled"] = false);

        var result = await repo.Service.GetPacksAsync(EditorRepo.Ct);

        Assert.Equal(["csharp-dapper", "sql-ddl"], result.Packs.Select(p => p.Name));
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public async Task A_broken_disabled_pack_is_reported_and_left_out()
    {
        await using var repo = EditorRepo.Create();
        repo.EditSettingsOnDisk(s => s["packs"]!["sql-ddl"]!["enabled"] = false);
        File.WriteAllText(repo.Repo.PathOf(".maquettiste/templates/sql-ddl/pack.json"), """{ "name": "sql-ddl", "units": 3 }""");

        var result = await repo.Service.GetPacksAsync(EditorRepo.Ct);

        Assert.Equal(["csharp-dapper"], result.Packs.Select(p => p.Name));
        Assert.NotEmpty(result.Diagnostics);
        Assert.All(result.Diagnostics, d =>
        {
            Assert.Equal("MQ6001", d.Rule);
            Assert.Equal(".maquettiste/templates/sql-ddl/pack.json", d.FilePath);
        });
    }

    [Fact]
    public async Task An_enabled_pack_with_a_load_error_is_listed_with_the_loader_diagnostic()
    {
        await using var repo = EditorRepo.Create();
        File.Delete(repo.Repo.PathOf(".maquettiste/templates/sql-ddl/table.scriban"));

        var result = await repo.Service.GetPacksAsync(EditorRepo.Ct);

        Assert.Equal(["csharp-dapper", "sql-ddl"], result.Packs.Select(p => p.Name));
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("MQ6001", diagnostic.Rule);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Contains("table.scriban", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Folders_without_a_pack_json_and_hidden_folders_are_ignored()
    {
        await using var repo = EditorRepo.Create();
        Directory.CreateDirectory(repo.Repo.PathOf(".maquettiste/templates/notes"));
        File.WriteAllText(repo.Repo.PathOf(".maquettiste/templates/notes/readme.md"), "not a pack");
        Directory.CreateDirectory(repo.Repo.PathOf(".maquettiste/templates/.old"));
        File.Copy(repo.Repo.PathOf(".maquettiste/templates/sql-ddl/pack.json"), repo.Repo.PathOf(".maquettiste/templates/.old/pack.json"));

        var result = await repo.Service.GetPacksAsync(EditorRepo.Ct);

        Assert.Equal(["csharp-dapper", "sql-ddl"], result.Packs.Select(p => p.Name));
    }

    [Fact]
    public async Task A_model_without_templates_lists_no_packs()
    {
        await using var repo = EditorRepo.Create(packs: false);

        var result = await repo.Service.GetPacksAsync(EditorRepo.Ct);

        Assert.Empty(result.Packs);
    }

    [Fact]
    public async Task The_list_serializes_with_web_defaults()
    {
        await using var repo = EditorRepo.Create();
        var result = await repo.Service.GetPacksAsync(EditorRepo.Ct);

        var node = JsonNode.Parse(JsonSerializer.Serialize(result, Web))!;

        var sql = node["packs"]![1]!.AsObject();
        Assert.True(sql.ContainsKey("$schema"));
        Assert.Equal(">=1.0 <2.0", sql["engine"]!.GetValue<string>());
        Assert.Equal("overwrite", sql["units"]![0]!["mode"]!.GetValue<string>());
        Assert.Empty(node["diagnostics"]!.AsArray());
    }
}
