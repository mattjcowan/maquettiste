using System.Text.Json.Nodes;

namespace Maquettiste.Cli.Tests;

/// <summary><c>maquettiste snapshot …</c> and the snapshot MCP tools (docs/engineering/snapshots.md).</summary>
public sealed class SnapshotCommandTests
{
    [Fact]
    public async Task Create_list_show_compare_restore_and_delete_from_the_command_line()
    {
        using var repo = CliRepo.Billing();

        var created = await repo.RunAsync("snapshot", "create", "First cut", "--description", "Before the rename", "--format", "json");
        Assert.True(created.ExitCode == 0, created.ToString());
        var id = (string)JsonNode.Parse(created.Out)!["id"]!;
        Assert.StartsWith("first-cut-", id, StringComparison.Ordinal);
        Assert.Contains("Took snapshot " + id, created.Error, StringComparison.Ordinal);
        Assert.False((bool)JsonNode.Parse(created.Out)!["includesPacks"]!);
        Assert.True(File.Exists(repo.PathOf(".maquettiste/model-snapshots/" + id + ".zip")));

        var list = await repo.RunAsync("snapshot", "list");
        Assert.True(list.ExitCode == 0, list.ToString());
        Assert.Contains(id, list.Out, StringComparison.Ordinal);
        Assert.Contains("First cut", list.Out, StringComparison.Ordinal);
        var show = await repo.RunAsync("snapshot", "show", id);
        Assert.Contains("description: Before the rename", show.Out, StringComparison.Ordinal);
        Assert.Contains("packs:       not included", show.Out, StringComparison.Ordinal);

        repo.Replace(".maquettiste/model/entities/payment.json", "\"name\": \"Payment\"", "\"name\": \"Settlement\"");
        var compare = await repo.RunAsync("snapshot", "compare", id);
        Assert.True(compare.ExitCode == 0, compare.ToString());
        Assert.Contains(id + " -> working: 0 added, 0 removed, 1 changed", compare.Out, StringComparison.Ordinal);
        Assert.Contains("~ entity Settlement (01J92P0V0HEGSC6MW92CST5KA6) (was Payment)", compare.Out, StringComparison.Ordinal);
        var fields = await repo.RunAsync("snapshot", "compare", id, "working", "--element", "01J92P0V0HEGSC6MW92CST5KA6");
        Assert.Contains("~ /name: \"Payment\" -> \"Settlement\"", fields.Out, StringComparison.Ordinal);
        var json = await repo.RunAsync("snapshot", "compare", id, "--format", "json");
        Assert.Equal(1, (int)JsonNode.Parse(json.Out)!["changed"]!);

        var preview = await repo.RunAsync("snapshot", "restore", id);
        Assert.True(preview.ExitCode == 0, preview.ToString());
        Assert.Contains("Run again with --apply.", preview.Out, StringComparison.Ordinal);
        Assert.Contains("\"Settlement\"", repo.Read(".maquettiste/model/entities/payment.json"), StringComparison.Ordinal);

        var restored = await repo.RunAsync("snapshot", "restore", id, "--apply");
        Assert.True(restored.ExitCode == 0, restored.ToString());
        Assert.Contains("Restored " + id + ": 1 documents written, 0 deleted.", restored.Out, StringComparison.Ordinal);
        Assert.Contains("Restore snapshot before-restore-", restored.Out, StringComparison.Ordinal);
        Assert.Contains("\"Payment\"", repo.Read(".maquettiste/model/entities/payment.json"), StringComparison.Ordinal);
        Assert.Contains("[before-restore]", (await repo.RunAsync("snapshot", "list")).Out, StringComparison.Ordinal);

        var deleted = await repo.RunAsync("snapshot", "delete", id);
        Assert.True(deleted.ExitCode == 0, deleted.ToString());
        Assert.Equal(1, (await repo.RunAsync("snapshot", "show", id)).ExitCode);
        Assert.Equal(4, (await repo.RunAsync("snapshot", "frobnicate")).ExitCode);
        Assert.Equal(4, (await repo.RunAsync("snapshot", "create")).ExitCode);
    }

    [Fact]
    public async Task Export_and_import_round_trip_an_archive_with_its_packs()
    {
        using var repo = CliRepo.Billing();
        var id = (string)JsonNode.Parse((await repo.RunAsync("snapshot", "create", "Shared", "--packs", "--format", "json")).Out)!["id"]!;
        var file = Path.Combine(repo.Temp.Root, "out", "shared.zip");

        var export = await repo.RunAsync("snapshot", "export", id, "--out", file);
        Assert.True(export.ExitCode == 0, export.ToString());
        Assert.Equal(File.ReadAllBytes(repo.PathOf(".maquettiste/model-snapshots/" + id + ".zip")), File.ReadAllBytes(file));
        Assert.Equal(4, (await repo.RunAsync("snapshot", "export", id, "--out", repo.PathOf(".maquettiste/x.zip"))).ExitCode);
        Assert.Equal(4, (await repo.RunAsync("snapshot", "export", id)).ExitCode);

        var imported = await repo.RunAsync("snapshot", "import", file, "--format", "json");
        Assert.True(imported.ExitCode == 0, imported.ToString());
        var snapshot = JsonNode.Parse(imported.Out)!["snapshot"]!;
        Assert.Equal(id + "-2", (string)snapshot["id"]!);
        Assert.True((bool)snapshot["includesPacks"]!);

        File.WriteAllText(file, "not a zip");
        var refused = await repo.RunAsync("snapshot", "import", file);
        Assert.Equal(1, refused.ExitCode);
        Assert.Contains("MQ1011", refused.Out, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_mcp_tools_take_list_compare_and_restore_snapshots()
    {
        await using var session = await McpSession.StartAsync(ct: TestContext.Current.CancellationToken);

        var created = await session.OkAsync("create_snapshot", new { name = "Agent checkpoint" });
        var id = (string)created["id"]!;
        Assert.Equal("agent", (string)created["author"]!);
        var listed = await session.OkAsync("list_snapshots");
        Assert.Equal(id, (string)listed.AsArray().Single()!["id"]!);

        session.Repo.Replace(McpSession.CustomerPath, "\"name\": \"Customer\"", "\"name\": \"Client\"");
        var compare = await session.OkAsync("compare_snapshots", new { from = id });
        Assert.Equal(1, (int)compare["changed"]!);
        var detail = await session.OkAsync("compare_snapshots", new { from = id, to = "working", id = McpSession.Customer });
        Assert.Equal("Customer", (string)detail["before"]!["name"]!);
        Assert.Equal("not-found", (await session.ErrorAsync("compare_snapshots", new { from = "missing-20260101-000000" })).Code);
        Assert.Equal("bad-request", (await session.ErrorAsync("compare_snapshots", new { })).Code);

        var restored = await session.OkAsync("restore_snapshot", new { id });
        Assert.Equal("restored", (string)restored["outcome"]!);
        Assert.StartsWith("before-restore-", (string)restored["safety"]!["id"]!, StringComparison.Ordinal);
        Assert.Contains("\"Customer\"", session.Repo.Read(McpSession.CustomerPath), StringComparison.Ordinal);
        Assert.Equal("not-found", (await session.ErrorAsync("restore_snapshot", new { id = "missing-20260101-000000" })).Code);
    }
}
