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
}
