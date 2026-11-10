using System.Text;
using System.Text.Json.Nodes;
using Maquettiste.Cli.Commands;
using Maquettiste.Engine;
using Maquettiste.Testing;

namespace Maquettiste.Cli.Tests;

/// <summary>
/// <c>init --agents &lt;names&gt;|all</c>: each named AI tool gets its project MCP file and its copy of the modeling skill, merged the way
/// <c>.mcp.json</c> is; nothing is written for a tool that is not named; the skill's refresh and <c>--check</c> cover every copy.
/// </summary>
public sealed class AgentsOptionTests
{
    private const string TomlTable = "[mcp_servers.maquettiste]\ncommand = \"maquettiste\"\nargs = [\"mcp\"]\n";

    private static readonly string[] AllFiles =
    [
        ".mcp.json", ".codex/config.toml", ".vscode/mcp.json", ".github/mcp.json", ".cursor/mcp.json", ".kiro/settings/mcp.json",
        ".junie/mcp/mcp.json", ".devin/mcp_config.json", ".amp/settings.json",
        ".claude/skills/maquettiste-modeling/SKILL.md", ".agents/skills/maquettiste-modeling/SKILL.md", ".kiro/skills/maquettiste-modeling/SKILL.md",
    ];

    /// <summary>Every JSON file a tool reads, with the member that holds the servers.</summary>
    public static TheoryData<string, string, string> JsonFiles()
    {
        var data = new TheoryData<string, string, string>();
        foreach (var tool in AgentSetup.Tools)
        {
            foreach (var file in tool.McpFiles.Where(f => f.Format == McpFileFormat.Json))
                data.Add(tool.Name, file.Path, file.Key);
        }

        return data;
    }

    private static string Skill054() =>
        File.ReadAllText(Fixtures.Path("skills", "maquettiste-modeling-0.5.4.md")).Replace("\r\n", "\n", StringComparison.Ordinal);

    private static JsonObject Servers(CliRepo repo, string path, string key) => JsonNode.Parse(repo.Read(path))![key]!.AsObject();

    private static CliRepo NewRepo()
    {
        var repo = CliRepo.Empty();
        Directory.CreateDirectory(repo.RepoRoot);
        return repo;
    }

    [Fact]
    public async Task Agents_all_writes_every_tool_file_once_and_a_second_run_keeps_them_all()
    {
        using var repo = NewRepo();
        var result = await repo.RunAsync("init", "--agents", "all");
        Assert.Equal(0, result.ExitCode);
        foreach (var file in AllFiles)
            Assert.True(repo.Temp.Exists(file), file);

        // One line per file, .mcp.json once although claude and grok both read it; the notes ride on their lines.
        Assert.Single(Text.Lines(result.Error), l => l.StartsWith("created .mcp.json ", StringComparison.Ordinal));
        Assert.Contains("created .codex/config.toml (server maquettiste: maquettiste mcp; Codex reads it only in projects you trust)", result.Error, StringComparison.Ordinal);
        Assert.Contains("created .amp/settings.json (server maquettiste: maquettiste mcp; approve it once with amp mcp approve maquettiste)", result.Error, StringComparison.Ordinal);
        Assert.Contains("created .junie/mcp/mcp.json (server maquettiste: maquettiste mcp)", result.Error, StringComparison.Ordinal);
        foreach (var folder in AgentSetup.SkillFolders)
            Assert.Single(Text.Lines(result.Error), l => l == "created " + AgentSetup.SkillPathIn(folder));

        // The shapes: the same entry everywhere; VS Code's file names its members servers, Amp's amp.mcpServers, and Copilot's own
        // file lists every tool.
        var entry = AgentSetup.ServerEntry(local: false);
        Assert.True(JsonNode.DeepEquals(entry, Servers(repo, ".mcp.json", "mcpServers")["maquettiste"]));
        Assert.True(JsonNode.DeepEquals(entry, Servers(repo, ".vscode/mcp.json", "servers")["maquettiste"]));
        Assert.True(JsonNode.DeepEquals(entry, Servers(repo, ".amp/settings.json", "amp.mcpServers")["maquettiste"]));
        var copilot = Servers(repo, ".github/mcp.json", "mcpServers")["maquettiste"]!.AsObject();
        Assert.Equal(["type", "command", "args", "tools"], copilot.Select(p => p.Key));
        Assert.Equal(["*"], copilot["tools"]!.AsArray().Select(t => (string)t!));
        Assert.Equal(TomlTable, repo.Read(".codex/config.toml"));
        var skill = SkillRefreshTests.CurrentSkill();
        foreach (var folder in AgentSetup.SkillFolders)
            Assert.Equal(skill, repo.Read(AgentSetup.SkillPathIn(folder)));

        var before = AllFiles.ToDictionary(f => f, repo.Read);
        var again = await repo.RunAsync("init", "--agents", "all");
        Assert.Equal(0, again.ExitCode);
        Assert.DoesNotContain(Text.Lines(again.Error), l => l.StartsWith("created ", StringComparison.Ordinal) || l.StartsWith("updated ", StringComparison.Ordinal));
        Assert.Contains("kept .codex/config.toml (it already registers the maquettiste server; Codex reads it only in projects you trust)", again.Error, StringComparison.Ordinal);
        Assert.Contains("kept .vscode/mcp.json (it already registers the maquettiste server)", again.Error, StringComparison.Ordinal);
        Assert.Contains("kept " + AgentSetup.SkillPathIn(AgentSetup.KiroSkills) + " (current)", again.Error, StringComparison.Ordinal);
        Assert.All(AllFiles, f => Assert.Equal(before[f], repo.Read(f)));
    }

    [Theory]
    [MemberData(nameof(JsonFiles))]
    public async Task Each_json_file_is_created_merged_kept_and_left_alone_when_it_is_not_what_init_expects(string tool, string path, string key)
    {
        // Created.
        using var repo = NewRepo();
        var created = await repo.RunAsync("init", "--agents", tool);
        Assert.Equal(0, created.ExitCode);
        Assert.Contains($"created {path} (server maquettiste: maquettiste mcp", created.Error, StringComparison.Ordinal);
        Assert.Equal([key], JsonNode.Parse(repo.Read(path))!.AsObject().Select(p => p.Key));

        // Merged: other servers and members stay, in order, and the file keeps its line endings, indent and final newline.
        using var merged = NewRepo();
        merged.Write(path, "{\r\n\t\"" + key + "\": { \"other\": { \"command\": \"x\", \"args\": [\"é\"] } },\r\n\t\"extra\": 1\r\n}\r\n");
        var merge = await merged.RunAsync("init", "--agents", tool);
        Assert.Equal(0, merge.ExitCode);
        Assert.Contains($"updated {path} (server maquettiste:", merge.Error, StringComparison.Ordinal);
        var root = JsonNode.Parse(merged.Read(path))!.AsObject();
        Assert.Equal([key, "extra"], root.Select(p => p.Key));
        Assert.Equal(["other", "maquettiste"], root[key]!.AsObject().Select(p => p.Key));
        Assert.Contains("\"é\"", merged.Read(path), StringComparison.Ordinal);
        Assert.StartsWith("{\r\n\t\"" + key + "\": {\r\n\t\t\"other\": {", merged.Read(path), StringComparison.Ordinal);
        Assert.EndsWith("\r\n\t\"extra\": 1\r\n}\r\n", merged.Read(path), StringComparison.Ordinal);
        Assert.DoesNotContain("\n", merged.Read(path).Replace("\r\n", "", StringComparison.Ordinal), StringComparison.Ordinal);

        // Comments or trailing commas: never rewritten; the line shows the entry to add.
        foreach (var commented in new[] { "{\n  // ours\n  \"" + key + "\": {}\n}\n", "{ \"" + key + "\": {}, }" })
        {
            using var jsonc = NewRepo();
            jsonc.Write(path, commented);
            var kept2 = await jsonc.RunAsync("init", "--agents", tool);
            Assert.Equal(0, kept2.ExitCode);
            Assert.Equal(commented, jsonc.Read(path));
            Assert.Contains($"kept {path}: it has comments or trailing commas, which init does not rewrite; add \"maquettiste\": {{", kept2.Error, StringComparison.Ordinal);
            Assert.Contains($"under {key} by hand", kept2.Error, StringComparison.Ordinal);
        }

        // An empty file counts as none, and a null entry as no entry.
        using var empty = NewRepo();
        empty.Write(path, "  \n");
        Assert.Contains($"updated {path} (server maquettiste:", (await empty.RunAsync("init", "--agents", tool)).Error, StringComparison.Ordinal);
        empty.Write(path, "{\"" + key + "\":{\"maquettiste\":null}}");
        Assert.Contains($"updated {path} (server maquettiste:", (await empty.RunAsync("init", "--agents", tool)).Error, StringComparison.Ordinal);
        Assert.NotNull(JsonNode.Parse(empty.Read(path))![key]!["maquettiste"]);

        // Kept: a maquettiste entry the team wrote is never replaced.
        using var kept = NewRepo();
        var custom = "{\"" + key + "\":{\"maquettiste\":{\"command\":\"maquettiste\",\"args\":[\"--repo\",\"model\",\"mcp\"]}}}\n";
        kept.Write(path, custom);
        var keep = await kept.RunAsync("init", "--agents", tool);
        Assert.Equal(0, keep.ExitCode);
        Assert.Contains($"kept {path} (it already registers the maquettiste server", keep.Error, StringComparison.Ordinal);
        Assert.Equal(custom, kept.Read(path));

        // Not what init expects: left alone with a hint, exit 0.
        foreach (var (text, reason) in new[] { ("not json", "it is not valid JSON"), ("[1]", "it is not a JSON object"), ("{\"" + key + "\":[]}", $"its {key} member is not an object") })
        {
            using var bad = NewRepo();
            bad.Write(path, text);
            var refused = await bad.RunAsync("init", "--agents", tool);
            Assert.Equal(0, refused.ExitCode);
            Assert.Equal(text, bad.Read(path));
            Assert.Contains($"kept {path}: {reason}", refused.Error, StringComparison.Ordinal);
            Assert.Contains($"under {key} by hand", refused.Error, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("cursor", ".cursor/mcp.json", ".agents/skills")]
    [InlineData("kiro", ".kiro/settings/mcp.json", ".kiro/skills")]
    [InlineData("junie", ".junie/mcp/mcp.json", null)]
    public async Task Only_the_named_tools_get_files(string tool, string mcpFile, string? skillFolder)
    {
        using var repo = NewRepo();
        var result = await repo.RunAsync("init", "--agents", tool);
        Assert.Equal(0, result.ExitCode);
        var expected = new List<string> { mcpFile };
        if (skillFolder is not null)
            expected.Add(AgentSetup.SkillPathIn(skillFolder));
        var written = AllFiles.Where(repo.Temp.Exists).ToList();
        Assert.Equal(expected, written);
        Assert.False(File.Exists(repo.PathOf(".mcp.json")));
        Assert.False(Directory.Exists(repo.PathOf(".claude")));
    }

    [Fact]
    public async Task Agents_claude_equals_agent_setup_and_names_are_read_without_case_or_spaces()
    {
        using var setup = NewRepo();
        Assert.Equal(0, (await setup.RunAsync("init", "--agent-setup")).ExitCode);
        using var claude = NewRepo();
        Assert.Equal(0, (await claude.RunAsync("init", "--agents", "claude")).ExitCode);
        Assert.Equal(AllFiles.Where(setup.Temp.Exists), AllFiles.Where(claude.Temp.Exists));
        Assert.Equal([".mcp.json", AgentSetup.SkillPath], AllFiles.Where(claude.Temp.Exists));
        Assert.Equal(setup.Read(".mcp.json"), claude.Read(".mcp.json"));

        using var mixed = NewRepo();
        Assert.Equal(0, (await mixed.RunAsync("init", "--agents", " Codex, CURSOR ")).ExitCode);
        Assert.Equal([".codex/config.toml", ".cursor/mcp.json", AgentSetup.SkillPathIn(AgentSetup.SharedSkills)], AllFiles.Where(mixed.Temp.Exists));

        using var all = NewRepo();
        Assert.Equal(0, (await all.RunAsync("init", "--agents", "ALL")).ExitCode);
        Assert.All(AllFiles, f => Assert.True(all.Temp.Exists(f), f));
    }

    [Theory]
    [InlineData("vim")]
    [InlineData("*")]
    [InlineData("claude,opencode")]
    [InlineData("")]
    [InlineData("claude,,codex")]
    public async Task An_unknown_or_empty_name_is_a_usage_error_that_lists_the_valid_names_and_writes_nothing(string value)
    {
        using var repo = NewRepo();
        var result = await repo.RunAsync("init", "--agents", value);
        Assert.Equal(4, result.ExitCode);
        Assert.Contains("Valid names: claude, codex, copilot, cursor, grok, kiro, junie, devin, amp, or all.", result.Error, StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFileSystemEntries(repo.RepoRoot));
    }

    [Fact]
    public async Task Check_and_force_follow_their_rules_with_agents()
    {
        using var repo = NewRepo();
        Assert.Equal(4, (await repo.RunAsync("init", "--skill", "--check", "--agents", "codex")).ExitCode);
        var force = await repo.RunAsync("init", "--agents", "junie", "--force");
        Assert.Equal(4, force.ExitCode);
        Assert.Contains("--force goes with --skill", force.Error, StringComparison.Ordinal);
        Assert.Equal(0, (await repo.RunAsync("init", "--agents", "codex", "--force")).ExitCode);
    }

    [Fact]
    public async Task Agents_with_docker_write_the_container_command_in_each_named_file_and_no_mcp_json()
    {
        using var repo = NewRepo();
        var result = await repo.RunAsync("init", "--agents", "cursor,codex", "--docker", "mattjcowan/maquettiste:0.2.0");
        Assert.Equal(0, result.ExitCode);
        var entry = AgentSetup.ContainerServerEntry("docker", "mattjcowan/maquettiste:0.2.0");
        Assert.True(JsonNode.DeepEquals(entry, Servers(repo, ".cursor/mcp.json", "mcpServers")["maquettiste"]));
        Assert.Equal(AgentSetup.TomlTable(entry, "\n"), repo.Read(".codex/config.toml"));
        Assert.False(File.Exists(repo.PathOf(".mcp.json")));
        if (!OperatingSystem.IsWindows())
            Assert.StartsWith("[mcp_servers.maquettiste]\ncommand = \"/bin/sh\"\nargs = [\"-c\", \"export PATH=\\\"$PATH:", repo.Read(".codex/config.toml"), StringComparison.Ordinal);

        // Another tag: the JSON entry is replaced; the TOML file is never rewritten, and the line says how to change it.
        var again = await repo.RunAsync("init", "--agents", "cursor,codex", "--docker", "mattjcowan/maquettiste:0.3.0");
        Assert.Equal(0, again.ExitCode);
        Assert.True(JsonNode.DeepEquals(AgentSetup.ContainerServerEntry("docker", "mattjcowan/maquettiste:0.3.0"), Servers(repo, ".cursor/mcp.json", "mcpServers")["maquettiste"]));
        Assert.Contains("updated .cursor/mcp.json", again.Error, StringComparison.Ordinal);
        Assert.Contains("kept .codex/config.toml (it already has a [mcp_servers.maquettiste] table; init never rewrites this file", again.Error, StringComparison.Ordinal);
        Assert.Equal(AgentSetup.TomlTable(entry, "\n"), repo.Read(".codex/config.toml"));

        // With --mcp as well, .mcp.json gets the same command; Copilot's file adds its tools list to it.
        Assert.Equal(0, (await repo.RunAsync("init", "--mcp", "--agents", "copilot", "--docker", "img:1", "--runtime", "podman")).ExitCode);
        var podman = AgentSetup.ContainerServerEntry("podman", "img:1");
        Assert.True(JsonNode.DeepEquals(podman, Servers(repo, ".mcp.json", "mcpServers")["maquettiste"]));
        Assert.True(JsonNode.DeepEquals(podman, Servers(repo, ".vscode/mcp.json", "servers")["maquettiste"]));
        var copilot = Servers(repo, ".github/mcp.json", "mcpServers")["maquettiste"]!.AsObject();
        Assert.Equal(podman["command"]!.ToJsonString(), copilot["command"]!.ToJsonString());
    }

    [Fact]
    public async Task The_codex_table_is_appended_to_a_config_that_lacks_it_and_the_file_is_kept_otherwise()
    {
        using var repo = NewRepo();
        const string Mine = "model = \"o3\"\n\n[mcp_servers.other]\ncommand = \"x\"\n";
        repo.Write(".codex/config.toml", Mine);
        var result = await repo.RunAsync("init", "--agents", "codex");
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("updated .codex/config.toml (server maquettiste: maquettiste mcp; Codex reads it only in projects you trust)", result.Error, StringComparison.Ordinal);
        Assert.Equal(Mine + "\n" + TomlTable, repo.Read(".codex/config.toml"));

        // No newline at the end, and CRLF line endings, are followed.
        Assert.Equal(Text.Utf8("a = 1\n\n" + TomlTable), Appended("a = 1"));
        Assert.Equal(Text.Utf8("a = 1\r\n\r\n" + TomlTable.Replace("\n", "\r\n", StringComparison.Ordinal)), Appended("a = 1\r\n"));
        Assert.Equal(Text.Utf8(TomlTable), Appended(null));

        // The exact header: registered, kept byte for byte (a trailing comment and spaces around it are fine).
        foreach (var present in new[] { "[mcp_servers.maquettiste]\ncommand = \"mine\"\n", "  [mcp_servers.maquettiste]  # ours\r\ncommand = \"mine\"\r\n" })
        {
            Assert.Null(AgentSetup.WithTomlServer(Text.Utf8(present), AgentSetup.ServerEntry(local: false), out var updated));
            Assert.Null(updated);
        }

        // Anything else that names maquettiste, or declares mcp_servers itself: left alone with the table to add by hand.
        foreach (var (text, reason) in new[]
        {
            ("[mcp_servers] # all servers\nmaquettiste = { command = \"mine\" }\n", "it mentions maquettiste but has no exact [mcp_servers.maquettiste] header"),
            ("matrix = [\n  [\"a\", \"b\"],\n]\nmcp_servers.maquettiste.command = \"mine\"\n", "it mentions maquettiste"),
            ("[[mcp_servers.maquettiste]]\ncommand = \"mine\"\n", "it mentions maquettiste"),
            ("[mcp_servers.\"maquettiste.old\"]\ncommand = \"mine\"\n", "it mentions maquettiste"),
            ("# [mcp_servers.maquettiste]\n", "it mentions maquettiste"),
            ("[mcp_servers]   # ours\nother = { command = \"x\" }\n", "it declares the mcp_servers table itself"),
            ("mcp_servers = { other = { command = \"x\" } }\n", "it declares the mcp_servers table itself"),
        })
        {
            Assert.StartsWith(reason, AgentSetup.WithTomlServer(Text.Utf8(text), AgentSetup.ServerEntry(local: false), out var updated), StringComparison.Ordinal);
            Assert.Null(updated);
        }

        using var inline = NewRepo();
        const string Inline = "mcp_servers = { other = { command = \"x\" } }\n";
        inline.Write(".codex/config.toml", Inline);
        var refused = await inline.RunAsync("init", "--agents", "codex");
        Assert.Equal(0, refused.ExitCode);
        Assert.Equal(Inline, inline.Read(".codex/config.toml"));
        Assert.Contains("kept .codex/config.toml: it declares the mcp_servers table itself; add this table by hand:\n" + TomlTable.TrimEnd('\n'), refused.Error, StringComparison.Ordinal);
        Assert.Equal("it is not UTF-8 text", AgentSetup.WithTomlServer([0x61, 0xFF, 0x0A], AgentSetup.ServerEntry(local: false), out _));
    }

    [Fact]
    public async Task The_codex_table_runs_the_pinned_local_tool_and_quotes_the_windows_container_form()
    {
        using var repo = NewRepo();
        repo.Write(".config/dotnet-tools.json", "{\"version\":1,\"isRoot\":true,\"tools\":{\"maquettiste\":{\"version\":\"0.1.0\",\"commands\":[\"maquettiste\"]}}}");
        Assert.Equal(0, (await repo.RunAsync("init", "--agents", "codex,cursor")).ExitCode);
        Assert.Equal("[mcp_servers.maquettiste]\ncommand = \"dotnet\"\nargs = [\"tool\", \"run\", \"maquettiste\", \"mcp\"]\n", repo.Read(".codex/config.toml"));
        Assert.Equal("dotnet", (string)Servers(repo, ".cursor/mcp.json", "mcpServers")["maquettiste"]!["command"]!);

        var windows = AgentSetup.TomlTable(AgentSetup.ContainerServerEntry("docker", "img:1", windows: true), "\n");
        Assert.StartsWith("[mcp_servers.maquettiste]\ncommand = \"docker\"\nargs = [\"run\", \"-i\", \"--rm\", \"--user\", \"0:0\", \"-v\", \"${PWD}:/repo\",", windows, StringComparison.Ordinal);
        var shell = AgentSetup.TomlTable(AgentSetup.ContainerServerEntry("docker", "img:1", windows: false), "\n");
        Assert.Contains("args = [\"-c\", \"export PATH=\\\"$PATH:", shell, StringComparison.Ordinal);
    }

    private static byte[]? Appended(string? existing)
    {
        Assert.Null(AgentSetup.WithTomlServer(existing is null ? null : Text.Utf8(existing), AgentSetup.ServerEntry(local: false), out var updated));
        return updated;
    }

    /// <summary>An untouched copy that another release wrote with its header.</summary>
    private static string Headed(string version) => AgentSetup.WithHeader(AgentSetup.SkillSource(), version);

    [Fact]
    public async Task Skill_refreshes_every_copy_the_engine_owns_and_creates_the_claude_one_only_when_there_is_none()
    {
        using var repo = NewRepo();
        var shared = AgentSetup.SkillPathIn(AgentSetup.SharedSkills);
        var kiro = AgentSetup.SkillPathIn(AgentSetup.KiroSkills);

        // A team that set up Codex only: --skill refreshes its copy and creates no Claude Code copy.
        repo.Write(shared, Headed("0.0.1"));
        var result = await repo.RunAsync("init", "--skill");
        Assert.Equal(0, result.ExitCode);
        Assert.Contains($"updated {shared} (from the 0.0.1 version to {EngineVersion.Product})", result.Error, StringComparison.Ordinal);
        Assert.Equal(SkillRefreshTests.CurrentSkill(), repo.Read(shared));
        Assert.False(File.Exists(repo.PathOf(AgentSetup.SkillPath)));
        Assert.False(File.Exists(repo.PathOf(kiro)));
        Assert.Equal(0, (await repo.RunAsync("init", "--skill", "--check")).ExitCode);

        // An edited copy with the header is left alone with the new version beside it; --force replaces it.
        var edited = Headed("0.0.1") + "\n## Ours\n";
        repo.Write(kiro, edited);
        var kept = await repo.RunAsync("init", "--skill");
        Assert.Contains(AgentSetup.EditedMessage(kiro), kept.Error, StringComparison.Ordinal);
        Assert.Equal(edited, repo.Read(kiro));
        Assert.Equal(SkillRefreshTests.CurrentSkill(), repo.Read(kiro + ".new"));
        var forced = await repo.RunAsync("init", "--skill", "--force");
        Assert.Contains($"replaced {kiro}, which was edited by hand (--force)", forced.Error, StringComparison.Ordinal);
        Assert.Contains("removed " + kiro + ".new", forced.Error, StringComparison.Ordinal);
        Assert.Equal(SkillRefreshTests.CurrentSkill(), repo.Read(kiro));

        // --agents touches only the named tools' folders: a stale Claude Code copy stays as it is.
        repo.Write(AgentSetup.SkillPath, Skill054());
        repo.Write(shared, Headed("0.0.1"));
        var codex = await repo.RunAsync("init", "--agents", "codex");
        Assert.Contains($"updated {shared} (from", codex.Error, StringComparison.Ordinal);
        Assert.Equal(Skill054(), repo.Read(AgentSetup.SkillPath));

        // --agent-setup writes the Claude Code copy and refreshes the others the engine owns.
        repo.Write(shared, Headed("0.0.1"));
        var setup = await repo.RunAsync("init", "--agent-setup");
        Assert.Contains($"updated {AgentSetup.SkillPath} (from the 0.5.3 or 0.5.4 version", setup.Error, StringComparison.Ordinal);
        Assert.Contains($"updated {shared} (from the 0.0.1 version", setup.Error, StringComparison.Ordinal);

        // With no copy anywhere, --skill writes the Claude Code one, as before.
        using var fresh = NewRepo();
        Assert.Contains("created " + AgentSetup.SkillPath, (await fresh.RunAsync("init", "--skill")).Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_copy_without_the_header_outside_claude_skills_is_the_teams_own()
    {
        using var repo = NewRepo();
        var shared = AgentSetup.SkillPathIn(AgentSetup.SharedSkills);
        var kiro = AgentSetup.SkillPathIn(AgentSetup.KiroSkills);
        const string Own = "---\nname: maquettiste-modeling\n---\n\n# Our own notes\n";
        repo.Write(shared, Own);
        repo.Write(kiro, Skill054());

        // --skill: no own copy is touched, no .new beside it, and a Claude Code copy is created as before (none of these is ours).
        var skill = await repo.RunAsync("init", "--skill");
        Assert.Equal(0, skill.ExitCode);
        Assert.Contains($"kept {shared} (it has no maquettiste header, so it is your own copy; init leaves it alone)", skill.Error, StringComparison.Ordinal);
        Assert.Contains($"kept {kiro} (it has no maquettiste header", skill.Error, StringComparison.Ordinal);
        Assert.Contains("created " + AgentSetup.SkillPath, skill.Error, StringComparison.Ordinal);
        Assert.Equal(Own, repo.Read(shared));
        Assert.Equal(Skill054(), repo.Read(kiro));
        Assert.False(File.Exists(repo.PathOf(shared + ".new")));

        // --agents naming the folder leaves it alone too, and --check ignores both.
        Assert.Contains($"kept {shared} (it has no maquettiste header", (await repo.RunAsync("init", "--agents", "codex", "--force")).Error, StringComparison.Ordinal);
        Assert.Equal(Own, repo.Read(shared));
        var check = await repo.RunAsync("init", "--skill", "--check");
        Assert.Equal(0, check.ExitCode);
        Assert.Equal($"skill current: {AgentSetup.SkillPath} (maquettiste {EngineVersion.Product})\n", check.Out);
    }

    [Fact]
    public async Task Check_compares_every_copy_the_engine_owns()
    {
        using var repo = NewRepo();
        var shared = AgentSetup.SkillPathIn(AgentSetup.SharedSkills);
        var kiro = AgentSetup.SkillPathIn(AgentSetup.KiroSkills);

        // A team that uses only the shared folder: that copy alone is checked.
        repo.Write(shared, SkillRefreshTests.CurrentSkill());
        var only = await repo.RunAsync("init", "--skill", "--check");
        Assert.Equal(0, only.ExitCode);
        Assert.Equal($"skill current: {shared} (maquettiste {EngineVersion.Product})\n", only.Out);

        repo.Write(kiro, Headed("0.0.1"));
        var stale = await repo.RunAsync("init", "--skill", "--check");
        Assert.Equal(2, stale.ExitCode);
        Assert.Equal(
            $"skill current: {shared} (maquettiste {EngineVersion.Product})\n"
            + $"skill stale: {kiro} is the 0.0.1 version, not {EngineVersion.Product} (run maquettiste init --skill)\n", stale.Out);
        Assert.Equal(Headed("0.0.1"), repo.Read(kiro));

        repo.Write(AgentSetup.SkillPath, SkillRefreshTests.CurrentSkill() + "more\n");
        var edited = await repo.RunAsync("init", "--skill", "--check");
        Assert.Equal(2, edited.ExitCode);
        Assert.StartsWith($"skill edited: {AgentSetup.SkillPath} was edited by hand", edited.Out, StringComparison.Ordinal);
        Assert.Equal(3, Text.Lines(edited.Out).Length);
        Assert.False(File.Exists(repo.PathOf(AgentSetup.SkillPath + ".new")));
    }

    [Fact]
    public async Task A_refused_path_stops_init_before_anything_is_written()
    {
        if (OperatingSystem.IsWindows())
            return;
        using var repo = NewRepo();
        var outside = Path.Combine(repo.Temp.Root, "outside");
        Directory.CreateDirectory(outside);
        File.CreateSymbolicLink(repo.PathOf(".cursor"), outside);
        var result = await repo.RunAsync("init", "--agents", "all");
        Assert.Equal(4, result.ExitCode);
        Assert.Contains("Refused to write " + repo.PathOf(".cursor/mcp.json"), result.Error, StringComparison.Ordinal);
        Assert.Contains("MQ6004", result.Error, StringComparison.Ordinal);
        Assert.Equal([".cursor"], Directory.EnumerateFileSystemEntries(repo.RepoRoot).Select(Path.GetFileName));
        Assert.Empty(Directory.EnumerateFileSystemEntries(outside));
    }

    [Fact]
    public async Task Linked_skills_folders_get_one_copy_and_one_line()
    {
        if (OperatingSystem.IsWindows())
            return;
        using var repo = NewRepo();
        Directory.CreateDirectory(repo.PathOf(".claude/skills"));
        Directory.CreateDirectory(repo.PathOf(".agents"));
        File.CreateSymbolicLink(repo.PathOf(".agents/skills"), repo.PathOf(".claude/skills"));
        var result = await repo.RunAsync("init", "--agents", "claude,codex");
        Assert.Equal(0, result.ExitCode);
        Assert.Single(Text.Lines(result.Error), l => l.Contains("SKILL.md", StringComparison.Ordinal));
        Assert.Equal(SkillRefreshTests.CurrentSkill(), repo.Read(AgentSetup.SkillPathIn(AgentSetup.SharedSkills)));
    }

    [Fact]
    public void Every_tool_file_is_a_setup_write_and_conventions_never_are()
    {
        using var temp = new TempRepo();
        var policy = new Maquettiste.Engine.Writing.OutputPathPolicy(temp.Options, null);
        foreach (var file in AgentSetup.Tools.SelectMany(t => t.McpFiles).Select(f => f.Path)
            .Concat(AgentSetup.SkillFolders.SelectMany(f => new[] { AgentSetup.SkillPathIn(f), AgentSetup.SkillPathIn(f) + ".new" })))
        {
            Assert.True(policy.CheckEngineWrite(Maquettiste.Engine.Pipeline.WriteTarget.Setup, temp.PathOf(file)).Allowed, file);
        }

        foreach (var folder in AgentSetup.SkillFolders)
            Assert.False(policy.CheckEngineWrite(Maquettiste.Engine.Pipeline.WriteTarget.Setup, temp.PathOf(folder + "/maquettiste-modeling/CONVENTIONS.md")).Allowed);
        Assert.False(policy.CheckEngineWrite(Maquettiste.Engine.Pipeline.WriteTarget.Setup, temp.PathOf(".github/workflows/ci.yml")).Allowed);
    }
}
