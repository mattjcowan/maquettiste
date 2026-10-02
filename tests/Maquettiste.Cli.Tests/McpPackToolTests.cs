using System.Text.Json.Nodes;

namespace Maquettiste.Cli.Tests;

/// <summary>The pack authoring tools end to end (generation-ui.md section 5.4): create, add a unit, write the template, preview, plan, explain.</summary>
public sealed class McpPackToolTests
{
    [Fact]
    public async Task A_client_writes_a_pack_end_to_end_and_reads_why_each_unit_renders()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var session = await McpSession.StartAsync(ct: ct);

        var created = await session.OkAsync("new_pack", new { name = "docs", from = "empty" });
        Assert.Contains("entity.scriban", created["files"]!.AsArray().Select(f => (string)f!));
        Assert.Equal("conflict", (await session.ErrorAsync("new_pack", new { name = "docs" })).Code);
        var starter = await session.OkAsync("new_pack", new { name = "ddl-starter", from = "sql-ddl" });
        Assert.Contains("table.scriban", starter["files"]!.AsArray().Select(f => (string)f!));

        var pack = await session.OkAsync("get_pack", new { pack = "docs" });
        var document = pack["document"]!.DeepClone().AsObject();
        document["units"]!.AsArray().Add(new JsonObject { ["id"] = "index", ["template"] = "index.scriban", ["for"] = "model", ["output"] = "index.md" });
        var saved = await session.OkAsync("save_pack", new { pack = "docs", document, expectedHash = (string)pack["hash"]! });
        Assert.Contains(saved["diagnostics"]!.AsArray(), d => (string)d!["rule"]! == "MQ6022");
        Assert.Equal("conflict", (await session.ErrorAsync("save_pack", new { pack = "docs", document, expectedHash = (string)pack["hash"]! })).Code);

        var written = await session.OkAsync("write_pack_file", new { pack = "docs", path = "index.scriban", text = "# {{ model.name }}\n", expectedHash = "new" });
        Assert.Empty(written["diagnostics"]!.AsArray());
        var file = await session.OkAsync("read_pack_file", new { pack = "docs", path = "index.scriban" });
        Assert.Equal((string)written["hash"]!, (string)file["hash"]!);
        Assert.Equal("conflict", (await session.ErrorAsync("write_pack_file", new { pack = "docs", path = "index.scriban", text = "x", expectedHash = new string('0', 64) })).Code);
        Assert.Equal("bad-request", (await session.ErrorAsync("write_pack_file", new { pack = "docs", path = "pack.json", text = "{}", expectedHash = "new" })).Code);
        var files = await session.OkAsync("list_pack_files", new { pack = "docs" });
        Assert.Contains(files["files"]!.AsArray(), f => (string)f!["path"]! == "index.scriban" && (string)f["role"]! == "template");

        var preview = await session.OkAsync("preview_unit", new { pack = "docs", unit = "index", overlay = new Dictionary<string, string> { ["index.scriban"] = "UNSAVED {{ 6 * 7 }}" } });
        Assert.Equal("UNSAVED 42", (string)preview["files"]![0]!["text"]!);

        var plan = await session.OkAsync("plan", new { packs = new[] { "ddl" } });
        var planId = (string)plan["plan"]!["id"]!;
        var units = await session.OkAsync("get_plan", new { planId, units = true });
        var unit = units["units"]![0]!;
        Assert.Equal("new", (string)unit["reason"]!);
        var explained = await session.OkAsync("explain_unit", new { planId, key = (string)unit["key"]! });
        Assert.Equal((string)unit["key"]!, (string)explained["unit"]!["key"]!);

        var why = await session.OkAsync("explain_unit", new { pack = "ddl", unit = (string)unit["unit"]!, elementId = (string?)unit["elementId"], planId });
        Assert.True((bool)why["planned"]!);
        Assert.Equal("unknown-unit", (string)(await session.OkAsync("explain_unit", new { pack = "ddl", unit = "ghost" }))["reason"]!);
        Assert.Equal("not-selected", (string)(await session.OkAsync("explain_unit", new { pack = "ddl", unit = (string)unit["unit"]!, elementId = (string?)unit["elementId"], packs = new[] { "other" } }))["reason"]!);
        Assert.Equal("bad-request", (await session.ErrorAsync("explain_unit", new { key = "x" })).Code);

        var paths = await session.OkAsync("unit_paths", new { pack = "docs", unit = "index" });
        Assert.Equal(1, (int)paths["count"]!);
        Assert.EndsWith("index.md", (string)paths["paths"]![0]!["path"]!, StringComparison.Ordinal);
        var context = await session.OkAsync("get_template_context", new { pack = "docs", unit = "index" });
        Assert.Contains(context["variables"]!.AsArray(), v => (string)v!["name"]! == "model");
        Assert.Equal("ddl", (string)(await session.OkAsync("get_pack_outputs", new { pack = "ddl" }))["pack"]!);

        var index = await session.OkAsync("read_pack_file", new { pack = "docs", path = "index.scriban" });
        var docs = await session.OkAsync("get_pack", new { pack = "docs" });
        var moved = await session.OkAsync("move_pack_file", new { pack = "docs", from = "index.scriban", to = "pages/index.scriban", expectedHash = (string)index["hash"]!, updateUnits = true, expectedPackHash = (string)docs["hash"]! });
        Assert.Equal("saved", (string)moved["outcome"]!);
        Assert.Equal("conflict", (await session.ErrorAsync("move_pack_file", new { pack = "docs", from = "pages/index.scriban", to = "index.scriban", expectedHash = new string('0', 64), updateUnits = true, expectedPackHash = (string)docs["hash"]! })).Code);

        var settings = await session.OkAsync("get_settings", new { });
        var packSettings = await session.OkAsync("save_pack_settings", new { pack = "docs", settings = new { enabled = false }, expectedHash = (string)settings["hash"]! });
        Assert.Equal("saved", (string)packSettings["outcome"]!);
        Assert.Equal("conflict", (await session.ErrorAsync("save_pack_settings", new { pack = "docs", settings = new { enabled = true }, expectedHash = (string)settings["hash"]! })).Code);

        var deleted = await session.OkAsync("delete_pack_file", new { pack = "ddl-starter", path = "README.md", expectedHash = (string)(await session.OkAsync("read_pack_file", new { pack = "ddl-starter", path = "README.md" }))["hash"]! });
        Assert.Equal("saved", (string)deleted["outcome"]!);
    }

    [Fact]
    public async Task A_client_removes_a_pack_with_its_hash_and_unit_paths_name_their_elements()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var session = await McpSession.StartAsync(ct: ct);
        await session.OkAsync("new_pack", new { name = "docs", from = "empty" });
        var paths = await session.OkAsync("unit_paths", new { pack = "docs", unit = "entity" });
        Assert.All(paths["paths"]!.AsArray(), p =>
        {
            Assert.Equal("entity", (string)p!["elementKind"]!);
            Assert.False(string.IsNullOrEmpty((string?)p["elementName"]));
        });
        var settings = await session.OkAsync("get_settings", new { });
        await session.OkAsync("save_pack_settings", new { pack = "docs", settings = new { enabled = false }, expectedHash = (string)settings["hash"]! });
        var pack = await session.OkAsync("get_pack", new { pack = "docs" });

        Assert.Equal("precondition-required", (await session.ErrorAsync("delete_pack", new { pack = "docs" })).Code);
        Assert.Equal("conflict", (await session.ErrorAsync("delete_pack", new { pack = "docs", expectedHash = new string('0', 64) })).Code);
        Assert.Equal("not-found", (await session.ErrorAsync("delete_pack", new { pack = "ghost", expectedHash = (string)pack["hash"]! })).Code);
        Assert.Equal("bad-request", (await session.ErrorAsync("delete_pack", new { pack = "Bad Name", expectedHash = (string)pack["hash"]! })).Code);

        var removed = await session.OkAsync("delete_pack", new { pack = "docs", expectedHash = (string)pack["hash"]! });
        Assert.Equal("saved", (string)removed["outcome"]!);
        Assert.Contains("pack.json", removed["files"]!.AsArray().Select(f => (string)f!));
        Assert.NotNull((string?)removed["settingsHash"]);
        Assert.Equal("not-found", (await session.ErrorAsync("get_pack", new { pack = "docs" })).Code);
        var after = await session.OkAsync("get_settings", new { });
        Assert.Null(after["settings"]!["packs"]!["docs"]);
    }

    [Fact]
    public async Task A_client_renames_a_pack_with_its_hash_and_its_settings_entry_follows()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var session = await McpSession.StartAsync(ct: ct);
        await session.OkAsync("new_pack", new { name = "docs", from = "empty" });
        var settings = await session.OkAsync("get_settings", new { });
        await session.OkAsync("save_pack_settings", new { pack = "docs", settings = new { enabled = false }, expectedHash = (string)settings["hash"]! });
        var pack = await session.OkAsync("get_pack", new { pack = "docs" });
        var hash = (string)pack["hash"]!;

        Assert.Equal("precondition-required", (await session.ErrorAsync("rename_pack", new { pack = "docs", name = "notes" })).Code);
        Assert.Equal("bad-request", (await session.ErrorAsync("rename_pack", new { pack = "docs", expectedHash = hash })).Code);
        Assert.Equal("conflict", (await session.ErrorAsync("rename_pack", new { pack = "docs", name = "notes", expectedHash = new string('0', 64) })).Code);
        Assert.Equal("not-found", (await session.ErrorAsync("rename_pack", new { pack = "ghost", name = "notes", expectedHash = hash })).Code);
        Assert.Equal("invalid", (await session.ErrorAsync("rename_pack", new { pack = "docs", name = "Bad Name", expectedHash = hash })).Code);

        var customer = await session.OkAsync("get_element", new { id = McpSession.Customer });
        var hinted = customer["json"]!.DeepClone().AsObject();
        hinted["generation"] = new System.Text.Json.Nodes.JsonObject { ["docs"] = new System.Text.Json.Nodes.JsonObject { ["skip"] = true } };
        await session.OkAsync("save_element", new { id = McpSession.Customer, element = hinted, expectedHash = (string)customer["hash"]! });

        var renamed = await session.OkAsync("rename_pack", new { pack = "docs", name = "notes", expectedHash = hash });
        Assert.Equal([McpSession.Customer], renamed["hintsUpdated"]!.AsArray().Select(h => (string)h!));
        var moved = await session.OkAsync("get_element", new { id = McpSession.Customer });
        Assert.True((bool)moved["json"]!["generation"]!["notes"]!["skip"]!);
        Assert.Null(moved["json"]!["generation"]!["docs"]);
        Assert.Equal(("saved", "docs", "notes"), ((string)renamed["outcome"]!, (string)renamed["from"]!, (string)renamed["to"]!));
        Assert.Contains("pack.json", renamed["files"]!.AsArray().Select(f => (string)f!));
        Assert.NotNull((string?)renamed["settingsHash"]);
        Assert.Equal("not-found", (await session.ErrorAsync("get_pack", new { pack = "docs" })).Code);
        Assert.Equal((string)renamed["hash"]!, (string)(await session.OkAsync("get_pack", new { pack = "notes" }))["hash"]!);
        var after = await session.OkAsync("get_settings", new { });
        Assert.Null(after["settings"]!["packs"]!["docs"]);
        Assert.False((bool)after["settings"]!["packs"]!["notes"]!["enabled"]!);
    }
}
