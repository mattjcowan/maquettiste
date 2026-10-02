using System.Text.Json.Nodes;
using Maquettiste.Cli.Commands;

namespace Maquettiste.Cli.Tests;

/// <summary><c>init --mcp / --skill / --agent-setup</c>, and a scripted agent session against the registered server.</summary>
public sealed class AgentSetupTests
{
    private const string GlobalRegistration = """
        {
          "mcpServers": {
            "maquettiste": {
              "type": "stdio",
              "command": "maquettiste",
              "args": [
                "mcp"
              ]
            }
          }
        }

        """;

    private static string RepoSkill()
    {
        var folder = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(folder, "maquettiste.slnx")))
            folder = Path.GetDirectoryName(folder) ?? throw new InvalidOperationException("repository root not found");
        return File.ReadAllText(Path.Combine(folder, "skills", "maquettiste-modeling", "SKILL.md")).Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    [Fact]
    public async Task Init_mcp_registers_the_installed_tool_and_writes_nothing_else_for_agents()
    {
        using var repo = CliRepo.Empty();
        var result = await repo.RunAsync("init", "--mcp");
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(GlobalRegistration.Replace("\r\n", "\n", StringComparison.Ordinal), repo.Read(".mcp.json"));
        Assert.Contains("created .mcp.json (server maquettiste: maquettiste mcp)", result.Error, StringComparison.Ordinal);
        Assert.False(Directory.Exists(repo.PathOf(".claude")));

        // Idempotent: the entry is kept, byte for byte.
        var again = await repo.RunAsync("init", "--mcp");
        Assert.Equal(0, again.ExitCode);
        Assert.Contains("kept .mcp.json (it already registers the maquettiste server)", again.Error, StringComparison.Ordinal);
        Assert.Equal(GlobalRegistration.Replace("\r\n", "\n", StringComparison.Ordinal), repo.Read(".mcp.json"));
    }

    // The one /bin/sh line the container form registers outside Windows, exactly.
    private static string ContainerLine(string runtime, string image) =>
        "export PATH=\"$PATH:/opt/homebrew/bin:/usr/local/bin:$HOME/.docker/bin\"; mkdir -p .maquettiste/.cache; exec " + runtime
        + " run -i --rm --user 0:0 -v \"$(pwd -P):/repo\" -w /repo -e MAQUETTISTE_CACHE_DIR=/repo/.maquettiste/.cache/cli"
        + " -e MAQUETTISTE_WORKSPACE=\"$(git symbolic-ref --short -q HEAD 2>/dev/null || basename \"$(pwd -P)\")\" " + image
        + " maquettiste mcp 2>>.maquettiste/.cache/mcp.log";

    private static string ShellRegistration(string runtime, string image) => $$"""
        {
          "mcpServers": {
            "maquettiste": {
              "type": "stdio",
              "command": "/bin/sh",
              "args": [
                "-c",
                "export PATH=\"$PATH:/opt/homebrew/bin:/usr/local/bin:$HOME/.docker/bin\"; mkdir -p .maquettiste/.cache; exec {{runtime}} run -i --rm --user 0:0 -v \"$(pwd -P):/repo\" -w /repo -e MAQUETTISTE_CACHE_DIR=/repo/.maquettiste/.cache/cli -e MAQUETTISTE_WORKSPACE=\"$(git symbolic-ref --short -q HEAD 2>/dev/null || basename \"$(pwd -P)\")\" {{image}} maquettiste mcp 2>>.maquettiste/.cache/mcp.log"
              ]
            }
          }
        }

        """.Replace("\r\n", "\n", StringComparison.Ordinal);

    private static string WindowsRegistration(string runtime, string image) => $$"""
        {
          "mcpServers": {
            "maquettiste": {
              "type": "stdio",
              "command": "{{runtime}}",
              "args": [
                "run",
                "-i",
                "--rm",
                "--user",
                "0:0",
                "-v",
                "${PWD}:/repo",
                "-w",
                "/repo",
                "-e",
                "MAQUETTISTE_CACHE_DIR=/repo/.maquettiste/.cache/cli",
                "-e",
                "MAQUETTISTE_WORKSPACE",
                "{{image}}",
                "maquettiste",
                "mcp"
              ]
            }
          }
        }

        """.Replace("\r\n", "\n", StringComparison.Ordinal);

    // What init writes on the platform the tests run on.
    private static string ContainerRegistration(string runtime, string image) =>
        OperatingSystem.IsWindows() ? WindowsRegistration(runtime, image) : ShellRegistration(runtime, image);

    private static string ContainerReport(string runtime, string image) => OperatingSystem.IsWindows()
        ? $"{runtime} run -i --rm --user 0:0 -v ${{PWD}}:/repo -w /repo -e MAQUETTISTE_CACHE_DIR=/repo/.maquettiste/.cache/cli -e MAQUETTISTE_WORKSPACE {image} maquettiste mcp"
        : "/bin/sh -c " + ContainerLine(runtime, image);

    private const string LegacyWrapper = "#!/bin/sh\n# maquettiste-mcp-wrapper (written by maquettiste init --mcp --docker; docs/mcp.md)\nexec docker run -i --rm img maquettiste mcp\n";

    [Fact]
    public async Task Init_mcp_docker_registers_the_container_command_and_writes_no_script()
    {
        using var repo = CliRepo.Empty();
        var result = await repo.RunAsync("init", "--mcp", "--docker", "mattjcowan/maquettiste:0.2.0");
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(ContainerRegistration("docker", "mattjcowan/maquettiste:0.2.0"), repo.Read(".mcp.json"));
        Assert.Contains("created .mcp.json (server maquettiste: " + ContainerReport("docker", "mattjcowan/maquettiste:0.2.0") + ")", result.Error, StringComparison.Ordinal);
        Assert.False(File.Exists(repo.PathOf("mcp.sh")));
        Assert.DoesNotContain("mcp.sh", result.Error, StringComparison.Ordinal);

        // The args exactly: outside Windows one /bin/sh line that finds the container command when the client was started from the
        // desktop, mounts the real path of the folder the client starts the server in at /repo, starts as root (the image's entrypoint
        // repairs strays and runs the server as the folder's owner), keeps the cache inside the repository and appends stderr to a log
        // under the ignored cache folder.
        var entry = JsonNode.Parse(repo.Read(".mcp.json"))!["mcpServers"]!["maquettiste"]!;
        Assert.Equal("stdio", (string)entry["type"]!);
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal("docker", (string)entry["command"]!);
        }
        else
        {
            Assert.Equal("/bin/sh", (string)entry["command"]!);
            Assert.Equal(
                ["-c", "export PATH=\"$PATH:/opt/homebrew/bin:/usr/local/bin:$HOME/.docker/bin\"; mkdir -p .maquettiste/.cache; exec docker run -i --rm --user 0:0 -v \"$(pwd -P):/repo\" -w /repo -e MAQUETTISTE_CACHE_DIR=/repo/.maquettiste/.cache/cli -e MAQUETTISTE_WORKSPACE=\"$(git symbolic-ref --short -q HEAD 2>/dev/null || basename \"$(pwd -P)\")\" mattjcowan/maquettiste:0.2.0 maquettiste mcp 2>>.maquettiste/.cache/mcp.log"],
                entry["args"]!.AsArray().Select(a => (string)a!));
        }

        // The same command again keeps the file byte for byte.
        var same = await repo.RunAsync("init", "--mcp", "--docker", "mattjcowan/maquettiste:0.2.0");
        Assert.Equal(0, same.ExitCode);
        Assert.Contains("kept .mcp.json (it already registers the maquettiste server)", same.Error, StringComparison.Ordinal);
        Assert.Equal(ContainerRegistration("docker", "mattjcowan/maquettiste:0.2.0"), repo.Read(".mcp.json"));

        // A re-run with another tag replaces the entry; --docker alone implies --mcp.
        var again = await repo.RunAsync("init", "--docker", "mattjcowan/maquettiste:0.3.0");
        Assert.Equal(0, again.ExitCode);
        Assert.Contains("updated .mcp.json (server maquettiste: " + ContainerReport("docker", "mattjcowan/maquettiste:0.3.0") + ")", again.Error, StringComparison.Ordinal);
        Assert.Equal(ContainerRegistration("docker", "mattjcowan/maquettiste:0.3.0"), repo.Read(".mcp.json"));
        Assert.False(File.Exists(repo.PathOf("mcp.sh")));
    }

    [Fact]
    public async Task Init_mcp_docker_with_runtime_podman_registers_podman_with_the_same_arguments()
    {
        using var repo = CliRepo.Empty();
        var result = await repo.RunAsync("init", "--docker", "mattjcowan/maquettiste:0.2.0", "--runtime", "podman");
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(ContainerRegistration("podman", "mattjcowan/maquettiste:0.2.0"), repo.Read(".mcp.json"));
        Assert.Contains("created .mcp.json (server maquettiste: " + ContainerReport("podman", "mattjcowan/maquettiste:0.2.0") + ")", result.Error, StringComparison.Ordinal);
        if (!OperatingSystem.IsWindows())
            Assert.Contains("; exec podman run -i --rm --user 0:0 -v \"$(pwd -P):/repo\" ", repo.Read(".mcp.json").Replace("\\\"", "\"", StringComparison.Ordinal), StringComparison.Ordinal);

        // Back to Docker: the entry is replaced again.
        Assert.Equal(0, (await repo.RunAsync("init", "--docker", "mattjcowan/maquettiste:0.2.0", "--runtime", "docker")).ExitCode);
        Assert.Equal(ContainerRegistration("docker", "mattjcowan/maquettiste:0.2.0"), repo.Read(".mcp.json"));

        // Another runtime, or --runtime without an image, is a usage error and writes nothing.
        using var other = CliRepo.Empty();
        Assert.Equal(4, (await other.RunAsync("init", "--docker", "img:1", "--runtime", "nerdctl")).ExitCode);
        var alone = await other.RunAsync("init", "--mcp", "--runtime", "podman");
        Assert.Equal(4, alone.ExitCode);
        Assert.Contains("--runtime picks the container command for --docker <image>", alone.Error, StringComparison.Ordinal);
        Assert.False(File.Exists(other.PathOf(".mcp.json")));
    }

    [Fact]
    public async Task Init_mcp_docker_merges_into_an_existing_config_and_replaces_the_maquettiste_entry()
    {
        using var repo = CliRepo.Empty();
        Directory.CreateDirectory(repo.RepoRoot);
        // The registration an earlier version wrote, next to another server and another member.
        repo.Write(".mcp.json", "{\n  \"mcpServers\": {\n    \"other\": { \"command\": \"x\", \"args\": [\"é\"] },\n    \"maquettiste\": { \"type\": \"stdio\", \"command\": \"./mcp.sh\", \"args\": [] },\n    \"last\": { \"command\": \"y\" }\n  },\n  \"extra\": 1\n}\n");
        var result = await repo.RunAsync("init", "--docker", "mattjcowan/maquettiste:0.2.0");
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("updated .mcp.json", result.Error, StringComparison.Ordinal);
        var root = JsonNode.Parse(repo.Read(".mcp.json"))!.AsObject();
        Assert.Equal(["mcpServers", "extra"], root.Select(p => p.Key));
        var servers = root["mcpServers"]!.AsObject();
        Assert.Equal(["other", "maquettiste", "last"], servers.Select(p => p.Key));
        Assert.Equal("é", (string)servers["other"]!["args"]![0]!);
        Assert.Contains("\"é\"", repo.Read(".mcp.json"), StringComparison.Ordinal);
        Assert.True(JsonNode.DeepEquals(AgentSetup.ContainerServerEntry("docker", "mattjcowan/maquettiste:0.2.0"), servers["maquettiste"]));
    }

    [Fact]
    public void Container_entry_on_windows_runs_the_container_command_itself_and_both_forms_replace_each_other()
    {
        // Windows has no /bin/sh: the entry runs the container command with ${PWD}, which the client expands.
        foreach (var runtime in AgentSetup.Runtimes)
        {
            var windows = AgentSetup.ContainerServerEntry(runtime, "mattjcowan/maquettiste:0.2.0", windows: true);
            Assert.Null(AgentSetup.WithServer(null, windows, replace: true, out var written));
            Assert.Equal(WindowsRegistration(runtime, "mattjcowan/maquettiste:0.2.0"), written);

            var shell = AgentSetup.ContainerServerEntry(runtime, "mattjcowan/maquettiste:0.2.0", windows: false);
            Assert.Equal(["-c", ContainerLine(runtime, "mattjcowan/maquettiste:0.2.0")], shell["args"]!.AsArray().Select(a => (string)a!));
            Assert.Null(AgentSetup.WithServer(null, shell, replace: true, out written));
            Assert.Equal(ShellRegistration(runtime, "mattjcowan/maquettiste:0.2.0"), written);
        }

        // Either form, written on another machine, is ours on a re-run: the maquettiste entry is replaced.
        var windowsFile = WindowsRegistration("docker", "mattjcowan/maquettiste:0.2.0");
        var shellFile = ShellRegistration("docker", "mattjcowan/maquettiste:0.2.0");
        Assert.Null(AgentSetup.WithServer(windowsFile, AgentSetup.ContainerServerEntry("docker", "mattjcowan/maquettiste:0.2.0", windows: false), replace: true, out var replaced));
        Assert.Equal(shellFile, replaced);
        Assert.Null(AgentSetup.WithServer(shellFile, AgentSetup.ContainerServerEntry("docker", "mattjcowan/maquettiste:0.2.0", windows: true), replace: true, out replaced));
        Assert.Equal(windowsFile, replaced);
    }

    [Fact]
    public async Task Init_mcp_docker_replaces_an_entry_of_the_other_platform()
    {
        using var repo = CliRepo.Empty();
        Directory.CreateDirectory(repo.RepoRoot);
        var other = OperatingSystem.IsWindows() ? ShellRegistration("podman", "img:1") : WindowsRegistration("podman", "img:1");
        repo.Write(".mcp.json", other);
        var result = await repo.RunAsync("init", "--docker", "mattjcowan/maquettiste:0.2.0");
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("updated .mcp.json", result.Error, StringComparison.Ordinal);
        Assert.Equal(ContainerRegistration("docker", "mattjcowan/maquettiste:0.2.0"), repo.Read(".mcp.json"));
    }

    [Fact]
    public async Task Init_mcp_docker_removes_the_wrapper_an_earlier_init_wrote_and_keeps_any_other_script()
    {
        using var repo = CliRepo.Empty();
        Directory.CreateDirectory(repo.RepoRoot);
        repo.Write("mcp.sh", LegacyWrapper);
        var result = await repo.RunAsync("init", "--mcp", "--docker", "mattjcowan/maquettiste:0.2.0");
        Assert.Equal(0, result.ExitCode);
        Assert.False(File.Exists(repo.PathOf("mcp.sh")));
        Assert.Contains("removed mcp.sh (the wrapper an earlier init wrote; .mcp.json now starts the container itself)", result.Error, StringComparison.Ordinal);

        // A wrapper with CRLF line endings (a checkout that converts them) is still ours.
        repo.Write("mcp.sh", LegacyWrapper.Replace("\n", "\r\n", StringComparison.Ordinal));
        Assert.Contains("removed mcp.sh", (await repo.RunAsync("init", "--docker", "mattjcowan/maquettiste:0.2.0")).Error, StringComparison.Ordinal);
        Assert.False(File.Exists(repo.PathOf("mcp.sh")));

        // A script maquettiste did not write, even one that mentions the marker later on, is never removed.
        foreach (var foreign in new[] { "#!/bin/sh\necho mine\n", "#!/bin/bash\n# maquettiste-mcp-wrapper (copied)\n", "#!/bin/sh\n# mine\n# maquettiste-mcp-wrapper (copied)\n" })
        {
            repo.Write("mcp.sh", foreign);
            var kept = await repo.RunAsync("init", "--docker", "mattjcowan/maquettiste:0.2.0");
            Assert.Equal(0, kept.ExitCode);
            Assert.Equal(foreign, repo.Read("mcp.sh"));
            Assert.DoesNotContain("mcp.sh", kept.Error, StringComparison.Ordinal);
        }

        // The installed-tool form leaves a wrapper alone: an entry that still runs it is kept as well.
        using var tool = CliRepo.Empty();
        Directory.CreateDirectory(tool.RepoRoot);
        tool.Write("mcp.sh", LegacyWrapper);
        Assert.Equal(0, (await tool.RunAsync("init", "--mcp")).ExitCode);
        Assert.Equal(LegacyWrapper, tool.Read("mcp.sh"));
    }

    [Fact]
    public async Task Init_mcp_docker_refuses_an_unsafe_image_reference()
    {
        using var other = CliRepo.Empty();
        foreach (var bad in new[] { "img; rm -rf /", "img:tag $(id)", "-v", "" })
        {
            var refused = await other.RunAsync("init", "--mcp", "--docker", bad);
            Assert.Equal(4, refused.ExitCode);
        }

        Assert.False(File.Exists(other.PathOf(".mcp.json")));
    }

    [Fact]
    public async Task Init_mcp_merges_into_an_existing_config_and_keeps_a_custom_maquettiste_entry()
    {
        using var repo = CliRepo.Empty();
        Directory.CreateDirectory(repo.RepoRoot);
        repo.Write(".mcp.json", "{\n  // a comment\n  \"mcpServers\": { \"other\": { \"command\": \"x\", \"args\": [\"é\"] } },\n  \"extra\": 1,\n}\n");
        var result = await repo.RunAsync("init", "--mcp");
        Assert.Equal(0, result.ExitCode);
        var root = JsonNode.Parse(repo.Read(".mcp.json"))!.AsObject();
        Assert.Equal(["mcpServers", "extra"], root.Select(p => p.Key));
        var servers = root["mcpServers"]!.AsObject();
        Assert.Equal(["other", "maquettiste"], servers.Select(p => p.Key));
        Assert.Equal("é", (string)servers["other"]!["args"]![0]!);
        Assert.Contains("\"é\"", repo.Read(".mcp.json"), StringComparison.Ordinal);
        Assert.Contains("updated .mcp.json", result.Error, StringComparison.Ordinal);

        // A maquettiste entry the user wrote (for example with --repo) is never replaced.
        var custom = "{\"mcpServers\":{\"maquettiste\":{\"command\":\"maquettiste\",\"args\":[\"--repo\",\"model\",\"mcp\"]}}}\n";
        repo.Write(".mcp.json", custom);
        Assert.Equal(0, (await repo.RunAsync("init", "--mcp")).ExitCode);
        Assert.Equal(custom, repo.Read(".mcp.json"));
    }

    [Fact]
    public async Task Init_mcp_uses_dotnet_tool_run_when_the_local_tool_manifest_lists_maquettiste()
    {
        using var repo = CliRepo.Empty();
        Directory.CreateDirectory(repo.RepoRoot);
        repo.Write(".config/dotnet-tools.json", "{\"version\":1,\"isRoot\":true,\"tools\":{\"maquettiste\":{\"version\":\"0.1.0\",\"commands\":[\"maquettiste\"]}}}");
        var result = await repo.RunAsync("init", "--mcp");
        Assert.Equal(0, result.ExitCode);
        var entry = JsonNode.Parse(repo.Read(".mcp.json"))!["mcpServers"]!["maquettiste"]!;
        Assert.Equal("dotnet", (string)entry["command"]!);
        Assert.Equal(["tool", "run", "maquettiste", "mcp"], entry["args"]!.AsArray().Select(a => (string)a!));
    }

    [Theory]
    [InlineData("not json", "it is not valid JSON")]
    [InlineData("[1]", "it is not a JSON object")]
    [InlineData("{\"mcpServers\":[]}", "its mcpServers member is not an object")]
    [InlineData("{\"mcpServers\":{\"a\":{},\"a\":{}}}", "it is not valid JSON (Duplicate property 'a'")]
    [InlineData("{\"mcpServers\":{},\"mcpServers\":{}}", "it is not valid JSON (Duplicate property 'mcpServers'")]
    [InlineData("{\"x\":1,\"x\":2}", "it is not valid JSON (Duplicate property 'x'")]
    public async Task Init_mcp_leaves_a_config_it_cannot_merge_alone_with_a_hint(string existing, string reason)
    {
        using var repo = CliRepo.Empty();
        Directory.CreateDirectory(repo.RepoRoot);
        repo.Write(".mcp.json", existing);
        var result = await repo.RunAsync("init", "--mcp");
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(existing, repo.Read(".mcp.json"));
        Assert.Contains("kept .mcp.json: " + reason, result.Error, StringComparison.Ordinal);
        Assert.Contains("under mcpServers by hand", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void With_server_adds_the_members_object_when_it_is_missing_or_null()
    {
        var entry = AgentSetup.ServerEntry(local: false);
        Assert.Null(AgentSetup.WithServer("{\"mcpServers\":null}", entry, replace: false, out var updated));
        Assert.Equal(GlobalRegistration.Replace("\r\n", "\n", StringComparison.Ordinal), updated);
        Assert.Null(AgentSetup.WithServer(null, entry, replace: false, out updated));
        Assert.Equal(GlobalRegistration.Replace("\r\n", "\n", StringComparison.Ordinal), updated);
    }

    [Fact]
    public async Task Init_skill_installs_the_modeling_skill_and_refreshes_an_older_copy()
    {
        using var repo = CliRepo.Empty();
        var result = await repo.RunAsync("init", "--skill");
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(RepoSkill(), repo.Read(AgentSetup.SkillPath));
        Assert.StartsWith("---\nname: maquettiste-modeling\n", repo.Read(AgentSetup.SkillPath), StringComparison.Ordinal);
        Assert.Contains("created " + AgentSetup.SkillPath, result.Error, StringComparison.Ordinal);
        Assert.False(File.Exists(repo.PathOf(".mcp.json")));

        var again = await repo.RunAsync("init", "--skill");
        Assert.Contains("kept " + AgentSetup.SkillPath + " (current)", again.Error, StringComparison.Ordinal);

        repo.Write(AgentSetup.SkillPath, "old\n");
        var refreshed = await repo.RunAsync("init", "--skill");
        Assert.Equal(0, refreshed.ExitCode);
        Assert.Equal(RepoSkill(), repo.Read(AgentSetup.SkillPath));
        Assert.Contains("updated " + AgentSetup.SkillPath, refreshed.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Init_agent_setup_does_both_and_the_skill_path_cannot_escape_the_repo()
    {
        using var repo = CliRepo.Empty();
        var result = await repo.RunAsync("init", "--agent-setup");
        Assert.Equal(0, result.ExitCode);
        Assert.True(File.Exists(repo.PathOf(".mcp.json")));
        Assert.Equal(RepoSkill(), repo.Read(AgentSetup.SkillPath));

        if (OperatingSystem.IsWindows())
            return;
        using var other = CliRepo.Empty();
        Directory.CreateDirectory(other.RepoRoot);
        var outside = Path.Combine(other.Temp.Root, "outside");
        Directory.CreateDirectory(outside);
        File.CreateSymbolicLink(other.PathOf(".claude"), outside);
        var refused = await other.RunAsync("init", "--skill");
        Assert.Equal(4, refused.ExitCode);
        Assert.Contains("MQ6004", refused.Error, StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFileSystemEntries(outside));
    }

    [Fact]
    public async Task A_scripted_agent_renames_an_attribute_through_the_registered_server_and_plans_the_change()
    {
        var ct = TestContext.Current.CancellationToken;
        var repo = CliRepo.Billing();
        var registered = AgentSetup.ServerEntry(local: false)["args"]!.AsArray().Select(a => (string)a!).ToList();
        await using var session = await McpSession.StartRegisteredAsync(repo, registered, ct);

        // The tool list the skill names.
        var tools = (await session.Client.ListToolsAsync(cancellationToken: ct)).Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var name in new[] { "get_model_index", "get_element", "save_element", "validate", "plan", "get_plan_diff", "apply_plan" })
            Assert.Contains(name, tools);

        // Find Invoice by name, read it, rename total to amountDue, save with the hash just read.
        var index = await session.OkAsync("get_model_index", new { kind = "entity", query = "Invoice" });
        var invoiceId = index.AsArray().Select(e => e!).Single(e => (string)e["name"]! == "Invoice")["id"]!.GetValue<string>();
        var invoice = await session.OkAsync("get_element", new { id = invoiceId });
        var json = invoice["json"]!.DeepClone();
        var total = json["attributes"]!.AsArray().Single(a => (string)a!["name"]! == "total")!;
        total["name"] = "amountDue";
        var saved = await session.OkAsync("save_element", new { id = invoiceId, element = json, expectedHash = (string)invoice["hash"]! });
        Assert.Equal("saved", (string)saved["outcome"]!);
        Assert.NotEqual((string)invoice["hash"]!, (string)saved["hash"]!);
        Assert.DoesNotContain(saved["diagnostics"]!.AsArray(), d => (string)d!["severity"]! == "error");
        Assert.Contains("\"name\": \"amountDue\"", repo.Read(".maquettiste/model/entities/invoice.json"), StringComparison.Ordinal);

        // The old hash is now a conflict, and the model still validates.
        Assert.Equal("conflict", (await session.ErrorAsync("save_element", new { id = invoiceId, element = json, expectedHash = (string)invoice["hash"]! })).Code);
        var validation = await session.OkAsync("validate");
        Assert.Equal(0, (int)validation["errors"]!);

        // The plan renders the renamed property without touching the repository (the column keeps its mapped prefix).
        var planned = await session.OkAsync("plan");
        Assert.Equal("succeeded", (string)planned["outcome"]!);
        var plan = planned["plan"]!;
        var code = plan["changes"]!.AsArray().Select(c => (string)c!["path"]!).Single(p => p.EndsWith("/Invoice.g.cs", StringComparison.Ordinal));
        var diff = await session.CallAsync("get_plan_diff", new { planId = (string)plan["id"]!, path = code });
        Assert.False(diff.IsError, diff.ToString());
        Assert.Contains("+    // amountDue\n", diff.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("// total\n", diff.Text, StringComparison.Ordinal);
        Assert.False(File.Exists(repo.PathOf(code)));
    }
}
