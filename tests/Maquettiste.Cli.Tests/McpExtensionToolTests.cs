namespace Maquettiste.Cli.Tests;

/// <summary>
/// The extension file tools end to end: an agent writes a script rule, sees a syntax error at once, fixes it, and the rule's findings
/// come back from validate, which runs script rules by default.
/// </summary>
public sealed class McpExtensionToolTests
{
    private const string Rule = """
        maquettiste.rule({
          id: "short-names",
          severity: "warning",
          kinds: ["entity"],
          check(element, model, report) {
            if (element.name.length > 7) report("Entity names should be at most 7 characters.", { pointer: "/name" });
          },
        });
        """;

    [Fact]
    public async Task Declaration_files_list_their_rules()
    {
        // next-phase-2-design.md section 5.6: rules/<name>.json is kind declared-rules, written in canonical form, checked alone and
        // listed with the rules it declares, each running on the resolved model; validate reports their findings.
        var ct = TestContext.Current.CancellationToken;
        await using var session = await McpSession.StartAsync(ct: ct);
        const string Declarations = """
            { "rules": [ { "id": "notes-described", "severity": "warning", "select": { "kind": "table" },
              "require": { "has": "properties.nothing" }, "message": "Table {name} has no note." } ] }
            """;

        var written = await session.OkAsync("write_extension_file", new { path = "rules/tables.json", text = Declarations, expectedHash = "new" });
        Assert.Empty(written["diagnostics"]!.AsArray());
        Assert.StartsWith("{\n  \"$schema\"", (string)written["text"]!, StringComparison.Ordinal);

        var files = (await session.OkAsync("list_extension_files"))["files"]!.AsArray();
        var file = Assert.Single(files, f => (string)f!["path"]! == "rules/tables.json")!;
        Assert.Equal("declared-rules", (string)file["kind"]!);
        var rule = Assert.Single(file["rules"]!.AsArray())!;
        Assert.Equal(("x/notes-described", "warning", true, false),
            ((string)rule["id"]!, (string)rule["severity"]!, (bool)rule["resolved"]!, (bool)rule["wholeModel"]!));
        Assert.Equal("declared-rules", (string)(await session.OkAsync("read_extension_file", new { path = "rules/tables.json" }))["kind"]!);

        var report = await session.OkAsync("validate");
        Assert.Contains(report["diagnostics"]!.AsArray(), d => (string)d!["rule"]! == "x/notes-described");
        Assert.Empty(report["suppressed"]!.AsArray());
    }

    [Fact]
    public async Task Calculation_files_list_their_calculated_values()
    {
        // next-phase-2-design.md section 5.5: calculated/<name>.js is kind calculated, checked alone on write and listed with what it
        // declares; a rule with scope "resolved" is marked; get_calculated then reads the values.
        var ct = TestContext.Current.CancellationToken;
        await using var session = await McpSession.StartAsync(ct: ct);
        const string Names = """
            maquettiste.calculated({
              name: "apiName",
              kinds: ["entity"],
              schema: { type: "string", pattern: "^[a-z_]+$" },
              description: "The entity's name in the API.",
              value(entity) { return entity.name.replace(/([a-z])([A-Z])/g, "$1_$2").toLowerCase(); },
            });

            """;

        var broken = await session.OkAsync("write_extension_file", new { path = "calculated/names.js", text = "maquettiste.calculated({ name: \"apiName\" });\n", expectedHash = "new" });
        Assert.Equal(("MQ5002", 1), ((string)broken["diagnostics"]![0]!["rule"]!, (int)broken["diagnostics"]![0]!["line"]!));
        var written = await session.OkAsync("write_extension_file", new { path = "calculated/names.js", text = Names, expectedHash = (string)broken["hash"]! });
        Assert.Empty(written["diagnostics"]!.AsArray());
        await session.OkAsync("write_extension_file", new
        {
            path = "rules/api-names.js",
            text = "maquettiste.rule({ id: \"api-names\", scope: \"resolved\", kinds: [\"entity\"], check(entity, model, report) {} });\n",
            expectedHash = "new",
        });

        var files = (await session.OkAsync("list_extension_files"))["files"]!.AsArray();
        Assert.Equal([("retention.json", "schema"), ("rules/api-names.js", "rule"), ("calculated/names.js", "calculated")],
            files.Select(f => ((string)f!["path"]!, (string)f["kind"]!)));
        var declared = Assert.Single(files[2]!["calculations"]!.AsArray())!;
        Assert.Equal(("apiName", "entity", "string", "The entity's name in the API."),
            ((string)declared["name"]!, (string)declared["kinds"]![0]!, (string)declared["schema"]!["type"]!, (string)declared["description"]!));
        Assert.True((bool)files[1]!["rules"]![0]!["resolved"]!);
        Assert.Equal("calculated", (string)(await session.OkAsync("read_extension_file", new { path = "calculated/names.js" }))["kind"]!);

        var answer = await session.OkAsync("get_calculated", new { ids = new[] { "01J92P0V0FJ23CGSNKM7P1W5V7" } });
        Assert.Equal("invoice", (string)answer["items"]![0]!["calculated"]!["apiName"]!);
    }

    [Fact]
    public async Task An_agent_writes_a_script_rule_and_validate_runs_it()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var session = await McpSession.StartAsync(ct: ct);

        var list = await session.OkAsync("list_extension_files");
        Assert.Equal(["retention.json"], list["files"]!.AsArray().Select(f => (string)f!["path"]!));
        var schema = await session.OkAsync("read_extension_file", new { path = "retention.json" });
        Assert.Contains("retentionDays", (string)schema["text"]!, StringComparison.Ordinal);

        var broken = await session.OkAsync("write_extension_file", new { path = "rules/naming.js", text = "maquettiste.rule({\n  id: \"x\",,\n});\n", expectedHash = "new" });
        var syntax = Assert.Single(broken["diagnostics"]!.AsArray())!;
        Assert.Equal(("MQ5002", 2), ((string)syntax["rule"]!, (int)syntax["line"]!));
        Assert.Contains((await session.OkAsync("validate"))["diagnostics"]!.AsArray(),
            d => (string)d!["rule"]! == "MQ5002" && (string)d["filePath"]! == ".maquettiste/extensions/rules/naming.js");

        var fixedRule = await session.OkAsync("write_extension_file", new { path = "rules/naming.js", text = Rule, expectedHash = (string)broken["hash"]! });
        Assert.Empty(fixedRule["diagnostics"]!.AsArray());
        Assert.Contains((await session.OkAsync("validate"))["diagnostics"]!.AsArray(), d => (string)d!["rule"]! == "x/short-names");
        Assert.DoesNotContain((await session.OkAsync("validate", new { includeScriptRules = false }))["diagnostics"]!.AsArray(), d => (string)d!["rule"]! == "x/short-names");

        Assert.Equal("conflict", (await session.ErrorAsync("write_extension_file", new { path = "rules/naming.js", text = "//", expectedHash = (string)broken["hash"]! })).Code);
        Assert.Equal("invalid", (await session.ErrorAsync("write_extension_file", new { path = "audit.json", text = "{}", expectedHash = "new" })).Code);
        Assert.Equal("bad-request", (await session.ErrorAsync("write_extension_file", new { path = "../maquettiste.json", text = "{}", expectedHash = "new" })).Code);

        var moved = await session.OkAsync("move_extension_file", new { from = "rules/naming.js", to = "rules/names.js", expectedHash = (string)fixedRule["hash"]! });
        Assert.Equal((string)fixedRule["hash"]!, (string)moved["hash"]!);
        await session.OkAsync("delete_extension_file", new { path = "rules/names.js", expectedHash = (string)moved["hash"]! });
        Assert.Equal("not-found", (await session.ErrorAsync("read_extension_file", new { path = "rules/names.js" })).Code);
    }

    [Fact]
    public async Task Write_extension_file_checks_a_rows_script()
    {
        // next-phase-2-design.md section 3.4 (D44): a seed's rows script lives in extensions/seeds/, kind seed-rows. A write loads it alone
        // and never runs it: registering rows twice, or a rule, is MQ7127 with its line; a rows function that would throw is fine until
        // something reads the rows.
        var ct = TestContext.Current.CancellationToken;
        await using var session = await McpSession.StartAsync(ct: ct);

        var twice = await session.OkAsync("write_extension_file", new
        {
            path = "seeds/kinds.js", text = "maquettiste.rows(() => []);\nmaquettiste.rows(() => []);\n", expectedHash = "new",
        });
        Assert.Equal("MQ7127", (string)Assert.Single(twice["diagnostics"]!.AsArray())!["rule"]!);

        var rule = await session.OkAsync("write_extension_file", new
        {
            path = "seeds/kinds.js", text = "maquettiste.rule({ id: \"x\", check() {} });\n", expectedHash = (string)twice["hash"]!,
        });
        var refused = Assert.Single(rule["diagnostics"]!.AsArray())!;
        Assert.Equal("MQ7127", (string)refused["rule"]!);
        Assert.Equal(".maquettiste/extensions/seeds/kinds.js", (string)refused["filePath"]!);

        var good = await session.OkAsync("write_extension_file", new
        {
            path = "seeds/kinds.js", text = "maquettiste.rows(() => { throw new Error('only when read'); });\n", expectedHash = (string)rule["hash"]!,
        });
        Assert.Empty(good["diagnostics"]!.AsArray());
        var listed = (await session.OkAsync("list_extension_files"))["files"]!.AsArray().Single(f => (string)f!["path"]! == "seeds/kinds.js")!;
        Assert.Equal("seed-rows", (string)listed["kind"]!);
        Assert.Empty(listed["diagnostics"]!.AsArray());
        Assert.Equal("seed-rows", (string)(await session.OkAsync("read_extension_file", new { path = "seeds/kinds.js" }))["kind"]!);
        Assert.Equal("invalid", (await session.ErrorAsync("move_extension_file", new { from = "seeds/kinds.js", to = "rules/kinds.js", expectedHash = (string)good["hash"]! })).Code);
        Assert.Equal("bad-request", (await session.ErrorAsync("write_extension_file", new { path = "seeds/deep/kinds.js", text = "//", expectedHash = "new" })).Code);
    }

    /// <summary>A custom kind file (next-phase-2-design.md section 4.2) through the extension tools: written in canonical form with the
    /// MQ3033 findings of its elements, listed and read as custom-kind, and refused for delete while an element uses it.</summary>
    [Fact]
    public async Task Custom_kind_files_round_trip()
    {
        var ct = TestContext.Current.CancellationToken;
        using var repo = CliRepo.Billing();
        repo.Write(".maquettiste/model/custom/setting/due-days.json", """
            { "kind": "custom", "id": "01JR6CK000000000000000000A", "name": "due_days", "package": "01J92P0V01KDRN8GX5PGYCNKSX", "type": "setting",
              "content": { "unit": "days", "value": 30 } }
            """);
        await using var session = await McpSession.StartAsync(repo, ct);

        var written = await session.OkAsync("write_extension_file", new { path = "kinds/setting.json", text = """{ "type": "setting", "displayName": "Setting", "properties": { "unit": { "type": "string" }, "value": { "type": "integer" } } }""", expectedHash = "new" });
        Assert.Empty(written["diagnostics"]!.AsArray());
        Assert.StartsWith("{\n  \"$schema\"", (string)written["text"]!, StringComparison.Ordinal);
        var listed = (await session.OkAsync("list_extension_files"))["files"]!.AsArray().Single(f => (string)f!["path"]! == "kinds/setting.json")!;
        Assert.Equal(("custom-kind", (string)written["hash"]!), ((string)listed["kind"]!, (string)listed["hash"]!));
        var read = await session.OkAsync("read_extension_file", new { path = "kinds/setting.json" });
        Assert.Equal(("custom-kind", (string)written["text"]!), ((string)read["kind"]!, (string)read["text"]!));

        // A narrower kind still writes, and answers with the element's MQ3033.
        var narrower = await session.OkAsync("write_extension_file", new
        {
            path = "kinds/setting.json", text = """{ "type": "setting", "properties": { "value": { "type": "string" } } }""", expectedHash = (string)written["hash"]!,
        });
        Assert.Contains(narrower["diagnostics"]!.AsArray(), d => (string)d!["rule"]! == "MQ3033" && (string)d["elementId"]! == "01JR6CK000000000000000000A");

        var delete = await session.ErrorAsync("delete_extension_file", new { path = "kinds/setting.json", expectedHash = (string)narrower["hash"]! });
        Assert.Equal("invalid", delete.Code);
        Assert.Contains("MQ1019", delete.ToString(), StringComparison.Ordinal);
        Assert.Equal("invalid", (await session.ErrorAsync("write_extension_file", new { path = "kinds/flag.json", text = "{ \"type\": \"flag\" }", expectedHash = "new" })).Code);
    }
}
