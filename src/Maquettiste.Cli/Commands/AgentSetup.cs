using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Maquettiste.Cli.Mcp;
using Maquettiste.Engine;
using Maquettiste.Engine.Pipeline;
using Maquettiste.Engine.Writing;

namespace Maquettiste.Cli.Commands;

/// <summary>
/// The agent setup of <c>maquettiste init --mcp / --skill / --agent-setup / --agents</c> (docs/mcp.md): registers the
/// <c>maquettiste mcp</c> server in the repository's <c>.mcp.json</c> (the project-scoped MCP configuration that MCP clients read),
/// and with <c>--agents</c> in the project file of each AI tool named (<see cref="Tools"/>), and installs the modeling skill as
/// <c>.claude/skills/maquettiste-modeling/SKILL.md</c> (and, for the tools that read them, in <c>.agents/skills/</c> or
/// <c>.kiro/skills/</c>), a file the engine owns: it carries a header with the release and a hash, is rewritten only while nobody
/// edited it, and leaves the repository's own conventions to <c>CONVENTIONS.md</c> beside it, which init never writes. Every write,
/// and the removal of an earlier version's <c>mcp.sh</c>, goes through the path policy as a setup write.
/// </summary>
internal static class AgentSetup
{
    /// <summary>The server name the registration uses in <c>mcpServers</c>.</summary>
    public const string ServerName = "maquettiste";

    /// <summary>The repo-relative path of the installed skill.</summary>
    public const string SkillPath = ClaudeSkills + "/" + SkillFolderName + "/SKILL.md";

    /// <summary>The name of the skill's folder in every skills folder.</summary>
    public const string SkillFolderName = "maquettiste-modeling";

    /// <summary>The skills folder Claude Code reads (and Grok Build, which reads Claude Code's skills).</summary>
    public const string ClaudeSkills = ".claude/skills";

    /// <summary>The shared skills folder that Codex, Cursor, GitHub Copilot, Devin and Amp read.</summary>
    public const string SharedSkills = ".agents/skills";

    /// <summary>The skills folder Kiro reads.</summary>
    public const string KiroSkills = ".kiro/skills";

    /// <summary>Every skills folder init may write a copy of the skill to, in the order it reports them.</summary>
    public static readonly IReadOnlyList<string> SkillFolders = [ClaudeSkills, SharedSkills, KiroSkills];

    /// <summary>The repo-relative path of the skill in a skills folder.</summary>
    /// <param name="folder">The skills folder, such as <c>.agents/skills</c>.</param>
    /// <returns>The path.</returns>
    public static string SkillPathIn(string folder) => folder + "/" + SkillFolderName + "/SKILL.md";

    /// <summary>The word <c>--agents</c> takes for every tool.</summary>
    public const string AllAgents = "all";

    /// <summary>
    /// The AI tools <c>--agents</c> names, in the order init writes their files. Each lists the project files it reads to find MCP
    /// servers and the skills folder it reads (none for Junie). The formats come from each tool's documentation (docs/guides/ai-tools.md).
    /// </summary>
    public static readonly IReadOnlyList<AgentTool> Tools =
    [
        new("claude", [new(".mcp.json", McpFileFormat.Json, "mcpServers")], ClaudeSkills),
        new("codex", [new(".codex/config.toml", McpFileFormat.Toml, "mcp_servers", Note: "Codex reads it only in projects you trust")], SharedSkills),
        new("copilot",
        [
            new(".vscode/mcp.json", McpFileFormat.Json, "servers"),
            new(".github/mcp.json", McpFileFormat.Json, "mcpServers", AllTools: true),
        ], SharedSkills),
        new("cursor", [new(".cursor/mcp.json", McpFileFormat.Json, "mcpServers")], SharedSkills),
        new("grok", [new(".mcp.json", McpFileFormat.Json, "mcpServers")], ClaudeSkills),
        new("kiro", [new(".kiro/settings/mcp.json", McpFileFormat.Json, "mcpServers")], KiroSkills),
        new("junie", [new(".junie/mcp/mcp.json", McpFileFormat.Json, "mcpServers")], null),
        new("devin", [new(".devin/mcp_config.json", McpFileFormat.Json, "mcpServers")], SharedSkills),
        new("amp", [new(".amp/settings.json", McpFileFormat.Json, "amp.mcpServers", Note: "approve it once with amp mcp approve " + ServerName)], SharedSkills),
    ];

    /// <summary>The <c>.mcp.json</c> file that <c>--mcp</c> writes.</summary>
    public static McpFile ProjectMcpFile => Tools[0].McpFiles[0];

    /// <summary>
    /// Reads the value of <c>--agents</c>: names separated by commas (spaces and case ignored), or <c>all</c>. Returns the tools in the
    /// order of <see cref="Tools"/>, each once.
    /// </summary>
    /// <param name="value">The option's value.</param>
    /// <returns>The tools.</returns>
    /// <exception cref="UsageException">An empty list or a name that is not a tool.</exception>
    public static IReadOnlyList<AgentTool> ParseAgents(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var valid = string.Join(", ", Tools.Select(t => t.Name)) + ", or " + AllAgents;
        var names = value.Split(',').Select(n => n.Trim().ToLowerInvariant()).ToList();
        if (names.Count == 0 || names.Any(n => n.Length == 0))
            throw new UsageException("--agents needs AI tool names separated by commas, such as claude,codex. Valid names: " + valid + ".");
        foreach (var name in names)
        {
            if (name != AllAgents && !Tools.Any(t => t.Name == name))
                throw new UsageException($"--agents: '{name}' is not an AI tool init knows. Valid names: {valid}.");
        }

        return names.Contains(AllAgents) ? Tools : [.. Tools.Where(t => names.Contains(t.Name))];
    }

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        IndentSize = 2,
        NewLine = "\n",
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// Returns the server entry for a repository: <c>dotnet tool run maquettiste mcp</c> when the repository's local tool manifest
    /// lists the <c>maquettiste</c> command, else <c>maquettiste mcp</c> (a global install on the PATH). No <c>--repo</c>: the client
    /// starts the server in the project folder and the server finds the repository from there, so the file can be committed.
    /// </summary>
    /// <param name="local">Whether the repository's local tool manifest lists the <c>maquettiste</c> command.</param>
    /// <returns>The entry.</returns>
    public static JsonObject ServerEntry(bool local)
    {
        var args = local ? new JsonArray("tool", "run", "maquettiste", "mcp") : new JsonArray("mcp");
        return new JsonObject
        {
            ["type"] = "stdio",
            ["command"] = local ? "dotnet" : "maquettiste",
            ["args"] = args,
        };
    }

    /// <summary>The container runtimes <c>--runtime</c> accepts; the first is the default.</summary>
    public static readonly IReadOnlyList<string> Runtimes = ["docker", "podman"];

    /// <summary>The repo-relative path of the wrapper script that earlier versions wrote for <c>init --mcp --docker</c>.</summary>
    public const string LegacyWrapperPath = "mcp.sh";

    /// <summary>The comment line, right after <c>#!/bin/sh</c>, that marks a wrapper maquettiste wrote; nothing else is ever removed.</summary>
    public const string LegacyWrapperMarker = "# maquettiste-mcp-wrapper";

    /// <summary>
    /// Returns the server entry of the container form: the MCP client runs <c>maquettiste mcp</c> in the image over the folder it
    /// starts the server in. Outside Windows the client starts <c>/bin/sh</c> with one command line (<see cref="ContainerCommandLine"/>):
    /// a client launched from the desktop does not inherit the shell's <c>PATH</c>, so the line adds the usual places of the container
    /// command first; <c>$(pwd -P)</c> mounts the real path of the folder (it works when the repository sits behind a link); and the
    /// server's stderr is appended to <c>.maquettiste/.cache/mcp.log</c>, under the cache folder git ignores. <c>MAQUETTISTE_WORKSPACE</c>
    /// names the workspace <c>get_project</c> reports: the branch git reads on the host, else the folder's name (a linked worktree's
    /// <c>.git</c> points at a folder the container does not mount). On Windows, which has no <c>/bin/sh</c>, the entry runs the
    /// container command itself with <c>${PWD}</c>, which the client expands, and passes <c>MAQUETTISTE_WORKSPACE</c> through from the
    /// client's environment when it is set (the server otherwise reads the branch from <c>.git</c>). Either way the file has
    /// no absolute path and can be committed. The container starts as root (<c>--user 0:0</c>) so the image's entrypoint repairs files
    /// an earlier run left owned by another user and runs the server as the owner of the folder (under rootless Podman root is the
    /// user outside, which the entrypoint recognizes). The engine cache lives in <c>.maquettiste/.cache/cli</c> so it survives the container.
    /// </summary>
    /// <param name="runtime">The container command, <c>docker</c> or <c>podman</c>.</param>
    /// <param name="image">The image reference (checked by <see cref="IsImageReference"/>).</param>
    /// <param name="windows">Whether to write the Windows form; <see langword="null"/> for the platform <c>init</c> runs on.</param>
    /// <returns>The entry.</returns>
    public static JsonObject ContainerServerEntry(string runtime, string image, bool? windows = null) => (windows ?? OperatingSystem.IsWindows())
        ? new JsonObject
        {
            ["type"] = "stdio",
            ["command"] = runtime,
            ["args"] = new JsonArray(
                "run", "-i", "--rm", "--user", "0:0", "-v", "${PWD}:/repo", "-w", "/repo",
                "-e", "MAQUETTISTE_CACHE_DIR=/repo/.maquettiste/.cache/cli", "-e", "MAQUETTISTE_WORKSPACE", image, "maquettiste", "mcp"),
        }
        : new JsonObject
        {
            ["type"] = "stdio",
            ["command"] = "/bin/sh",
            ["args"] = new JsonArray("-c", ContainerCommandLine(runtime, image)),
        };

    /// <summary>
    /// Returns the <c>/bin/sh -c</c> line of the container form outside Windows. It has no <c>${...}</c>, which an MCP client would
    /// expand itself: the shell expands <c>$PATH</c>, <c>$HOME</c>, <c>$(pwd -P)</c> and the workspace name (<c>git symbolic-ref</c>, else
    /// the folder's name). The image reference needs no quoting
    /// (<see cref="IsImageReference"/>).
    /// </summary>
    /// <param name="runtime">The container command, <c>docker</c> or <c>podman</c>.</param>
    /// <param name="image">The image reference.</param>
    /// <returns>The line.</returns>
    public static string ContainerCommandLine(string runtime, string image) =>
        "export PATH=\"$PATH:/opt/homebrew/bin:/usr/local/bin:$HOME/.docker/bin\"; mkdir -p .maquettiste/.cache; "
        + $"exec {runtime} run -i --rm --user 0:0 -v \"$(pwd -P):/repo\" -w /repo -e MAQUETTISTE_CACHE_DIR=/repo/.maquettiste/.cache/cli "
        + WorkspaceOption + $" {image} maquettiste mcp 2>>.maquettiste/.cache/mcp.log";

    /// <summary>
    /// The <c>-e MAQUETTISTE_WORKSPACE=...</c> option of the shell line: the branch checked out in the folder, else (a detached head, no
    /// git) the folder's name, which for a linked worktree is the worktree's.
    /// </summary>
    public const string WorkspaceOption =
        "-e MAQUETTISTE_WORKSPACE=\"$(git symbolic-ref --short -q HEAD 2>/dev/null || basename \"$(pwd -P)\")\"";

    /// <summary>Whether an image reference is usable as one argument: a name, an optional tag and an optional digest, never an option.</summary>
    /// <param name="image">The reference, such as <c>mattjcowan/maquettiste:0.2.0</c>.</param>
    /// <returns><see langword="true"/> when it is usable.</returns>
    public static bool IsImageReference(string image) =>
        image.Length is > 0 and <= 255 && char.IsAsciiLetterOrDigit(image[0])
        && image.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' or '/' or ':' or '@');

    /// <summary>Whether a script is the wrapper an earlier <c>init --mcp --docker</c> wrote: <c>#!/bin/sh</c>, then the marker line.</summary>
    /// <param name="text">The script.</param>
    /// <returns><see langword="true"/> when maquettiste wrote it.</returns>
    public static bool IsLegacyWrapper(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var lines = text.Split('\n', 3);
        return lines.Length >= 2 && lines[0].TrimEnd('\r') == "#!/bin/sh"
            && lines[1].StartsWith(LegacyWrapperMarker + " ", StringComparison.Ordinal);
    }

    /// <summary>
    /// Adds the <c>maquettiste</c> server to an existing <c>.mcp.json</c> text (or another tool's JSON file), keeping every other entry
    /// and member in order, and the file's line endings, indent unit and final newline. An existing <c>maquettiste</c> entry is kept
    /// as is, unless <paramref name="replace"/> asks for it to be replaced in place (an entry that is <c>null</c> counts as none). A file
    /// with comments or trailing commas is never rewritten, since writing it again would drop them. An empty file counts as none.
    /// </summary>
    /// <param name="existing">The current file, or <see langword="null"/> when there is none.</param>
    /// <param name="entry">The server entry.</param>
    /// <param name="replace">Whether an existing <c>maquettiste</c> entry that differs from <paramref name="entry"/> is replaced.</param>
    /// <param name="updated">The new text; <see langword="null"/> when nothing changes. A new file is LF, two-space indent, trailing newline.</param>
    /// <param name="key">The root member that holds the servers: <c>mcpServers</c>, <c>servers</c> or <c>amp.mcpServers</c>.</param>
    /// <returns><see langword="null"/> on success, else why the file was left alone.</returns>
    public static string? WithServer(string? existing, JsonObject entry, bool replace, out string? updated, string key = "mcpServers")
    {
        ArgumentNullException.ThrowIfNull(entry);
        updated = null;
        if (string.IsNullOrWhiteSpace(existing))
            existing = null;
        JsonObject root;
        JsonObject servers;
        try
        {
            if (existing is null)
            {
                root = [];
            }
            else if (JsonNode.Parse(existing, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true, AllowDuplicateProperties = false }) is JsonObject parsed)
            {
                root = parsed;
            }
            else
            {
                return "it is not a JSON object";
            }

            if (root[key] is null)
            {
                if (root.ContainsKey(key))
                    root.Remove(key);
                servers = [];
                root[key] = servers;
            }
            else if (root[key] is JsonObject found)
            {
                servers = found;
            }
            else
            {
                return $"its {key} member is not an object";
            }

            if (servers[ServerName] is not null && (!replace || JsonNode.DeepEquals(servers[ServerName], entry)))
                return null;
        }
        catch (JsonException e)
        {
            return "it is not valid JSON (" + e.Message + ")";
        }
        catch (ArgumentException e)
        {
            // JsonObject builds its dictionary lazily, so a duplicate key surfaces on first lookup.
            return "it has duplicate keys (" + e.Message + ")";
        }

        if (existing is not null && !IsPlainJson(existing))
            return "it has comments or trailing commas, which init does not rewrite";

        servers[ServerName] = entry.DeepClone();
        updated = existing is null ? root.ToJsonString(WriteOptions) + "\n" : Reserialize(root, existing);
        return null;
    }

    /// <summary>Whether a text that parses leniently also parses with no comments and no trailing commas.</summary>
    private static bool IsPlainJson(string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Disallow, AllowTrailingCommas = false });
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>Writes a merged file in the style of the one it replaces: its line endings, indent unit and final newline.</summary>
    private static string Reserialize(JsonObject root, string existing)
    {
        var newline = existing.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        char indentCharacter = ' ';
        var indentSize = 2;
        foreach (var line in existing.Split('\n').Skip(1))
        {
            var width = line.Length - line.TrimStart(' ', '\t').Length;
            if (width == 0 || width == line.TrimEnd('\r').Length)
                continue;
            indentCharacter = line[0];
            indentSize = Math.Min(127, line.Length - line.TrimStart(indentCharacter).Length);
            break;
        }

        var options = new JsonSerializerOptions(WriteOptions) { IndentCharacter = indentCharacter, IndentSize = indentSize, NewLine = newline };
        var text = root.ToJsonString(options);
        return existing.EndsWith('\n') ? text + newline : text;
    }

    /// <summary>The entry as the by-hand hints print it: one line, quotes and non-ASCII letters as they are.</summary>
    private static string HintJson(JsonNode entry) => entry.ToJsonString(new JsonSerializerOptions(WriteOptions) { WriteIndented = false });

    /// <summary>Returns the embedded <c>skills/maquettiste-modeling/SKILL.md</c>, front matter included, with LF line endings.</summary>
    /// <returns>The text.</returns>
    public static string SkillSource()
    {
        using var stream = typeof(McpServerSetup).Assembly.GetManifestResourceStream(McpServerSetup.ConventionsResource)
            ?? throw new InvalidOperationException("The modeling skill is not embedded in the CLI.");
        using var reader = new StreamReader(stream, new UTF8Encoding(false));
        return reader.ReadToEnd().Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    /// <summary>Returns the skill <c>init --skill</c> writes: the embedded skill with this release's header (<see cref="WithHeader"/>).</summary>
    /// <returns>The UTF-8 bytes.</returns>
    public static byte[] SkillBytes() => new UTF8Encoding(false).GetBytes(WithHeader(SkillSource(), EngineVersion.Product));

    /// <summary>
    /// Inserts the header right after the front matter: the marker comment <c>&lt;!-- maquettiste-skill: version=V; sha256=H --&gt;</c>, then
    /// a paragraph that says the file is generated and where a repository's own conventions go. <c>H</c> is the sha256 of the whole
    /// file without the marker line, so <see cref="Classify"/> recognizes an untouched copy of any release that writes the header, and an
    /// edit anywhere (front matter, paragraph or body) shows.
    /// </summary>
    /// <param name="source">The skill source, LF line endings.</param>
    /// <param name="version">The release that writes it.</param>
    /// <returns>The file's text.</returns>
    internal static string WithHeader(string source, string version)
    {
        ArgumentNullException.ThrowIfNull(source);
        var cut = 0;
        if (source.StartsWith("---\n", StringComparison.Ordinal))
        {
            var end = source.IndexOf("\n---\n", 3, StringComparison.Ordinal);
            if (end >= 0)
                cut = end + 5;
        }

        var head = cut == 0 ? "" : source[..cut] + "\n";
        var body = source[cut..];
        body = body.StartsWith('\n') ? body : "\n" + body;
        var paragraph =
            $"This file is generated by maquettiste {version} and rewritten by `maquettiste init --skill`. Do not edit it: put this\n"
            + "repository's own modeling conventions in `.claude/skills/maquettiste-modeling/CONVENTIONS.md`, whichever copy of\n"
            + "this skill you read; `init` never touches that file, and agents read it after this one. A copy edited by hand is left\n"
            + "alone: `init --skill` then writes the new version to `SKILL.md.new` (`--force` overwrites), and `--check` exits 2.\n";
        var hash = Sha256(head + paragraph + body);
        return head + HeaderPrefix + "version=" + version + "; sha256=" + hash + " -->\n" + paragraph + body;
    }

    /// <summary>The start of the header's marker line.</summary>
    public const string HeaderPrefix = "<!-- maquettiste-skill: ";

    /// <summary>The repo-relative path of the repository's own conventions, which <c>init</c> never writes.</summary>
    public const string ConventionsPath = AgentConventions.ProjectConventionsPath;

    /// <summary>What a copy of the skill on disk is, compared with the one this release writes.</summary>
    internal enum SkillState
    {
        /// <summary>There is no file.</summary>
        Missing,

        /// <summary>The file is the one this release writes (line endings aside).</summary>
        Current,

        /// <summary>An untouched copy that another release wrote.</summary>
        Shipped,

        /// <summary>Neither: the file was edited by hand.</summary>
        Edited,
    }

    /// <summary>Classifies a copy of the skill.</summary>
    /// <param name="onDisk">The file's bytes, or <see langword="null"/> when there is none.</param>
    /// <param name="current">The bytes this release writes (<see cref="SkillBytes"/>).</param>
    /// <param name="version">For <see cref="SkillState.Shipped"/>, the release or releases that wrote it.</param>
    /// <returns>The state.</returns>
    internal static SkillState Classify(byte[]? onDisk, byte[] current, out string? version)
    {
        ArgumentNullException.ThrowIfNull(current);
        version = null;
        if (onDisk is null)
            return SkillState.Missing;

        // A checkout may have turned LF into CRLF; a byte order mark is not content either.
        var text = new UTF8Encoding(false).GetString(onDisk).TrimStart('\uFEFF').Replace("\r\n", "\n", StringComparison.Ordinal);
        var bytes = new UTF8Encoding(false).GetBytes(text);
        if (bytes.AsSpan().SequenceEqual(current))
            return SkillState.Current;

        if (HeaderVersion(text) is { } headed)
        {
            version = headed;
            return SkillState.Shipped;
        }

        var sum = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var releases = ShippedSkills.Releases.Where(r => string.Equals(r.Sha256, sum, StringComparison.Ordinal)).Select(r => r.Version).ToList();
        if (releases.Count == 0)
            return SkillState.Edited;
        version = string.Join(" or ", releases);
        return SkillState.Shipped;
    }

    /// <summary>The release named by a valid header: the marker line's hash matches the rest of the file.</summary>
    private static string? HeaderVersion(string text)
    {
        int start;
        if (text.StartsWith(HeaderPrefix, StringComparison.Ordinal))
        {
            start = 0;
        }
        else
        {
            start = text.IndexOf("\n" + HeaderPrefix, StringComparison.Ordinal) + 1;
            if (start == 0)
                return null;
        }

        var end = text.IndexOf('\n', start);
        if (end < 0)
            return null;
        var line = text[(start + HeaderPrefix.Length)..end];
        if (!line.EndsWith(" -->", StringComparison.Ordinal))
            return null;
        string? version = null, hash = null;
        foreach (var part in line[..^4].Split("; "))
        {
            if (part.StartsWith("version=", StringComparison.Ordinal))
                version = part[8..];
            else if (part.StartsWith("sha256=", StringComparison.Ordinal))
                hash = part[7..];
        }

        return version is { Length: > 0 } && hash is not null
            && string.Equals(hash, Sha256(text[..start] + text[(end + 1)..]), StringComparison.Ordinal) ? version : null;
    }

    private static string Sha256(string text) => Convert.ToHexStringLower(SHA256.HashData(new UTF8Encoding(false).GetBytes(text)));

    /// <summary>
    /// Registers the server in <c>.mcp.json</c> and in the other files given (<c>--agents</c>). The installed-tool form keeps an entry
    /// named <c>maquettiste</c> that is already there as is; the container form replaces it in a JSON file, so a re-run with another
    /// tag or runtime takes effect, and then removes the <c>mcp.sh</c> wrapper an earlier version wrote (only when it carries the
    /// marker; any other <c>mcp.sh</c> is left alone). A TOML file is never rewritten: the table is appended when the file has none.
    /// </summary>
    /// <param name="files">The guarded writer.</param>
    /// <param name="repo">The repository root.</param>
    /// <param name="targets">The files to write, each once, in order.</param>
    /// <param name="dockerImage">The image of the container form (<c>--docker</c>), or <see langword="null"/> for the installed tool.</param>
    /// <param name="runtime">The container command of the container form (<c>--runtime</c>, default <c>docker</c>).</param>
    /// <param name="report">The report lines.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>A task.</returns>
    public static async Task WriteMcpFilesAsync(GuardedFiles files, string repo, IReadOnlyList<McpFile> targets, string? dockerImage, string runtime,
        List<string> report, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentNullException.ThrowIfNull(report);
        if (targets.Count == 0)
            return;
        var container = dockerImage is not null;
        var entry = container ? ContainerServerEntry(runtime, dockerImage!) : ServerEntry(await UsesLocalToolAsync(repo, ct).ConfigureAwait(false));
        foreach (var target in targets)
        {
            if (target.Format == McpFileFormat.Toml)
                await WriteTomlAsync(files, repo, target, entry, container, report, ct).ConfigureAwait(false);
            else
                await WriteJsonAsync(files, repo, target, target.AllTools ? WithAllTools(entry) : entry, container, report, ct).ConfigureAwait(false);
        }

        if (container && targets.Contains(ProjectMcpFile))
            await RemoveLegacyWrapperAsync(files, repo, report, ct).ConfigureAwait(false);
    }

    /// <summary>The entry with <c>"tools": ["*"]</c>, which GitHub Copilot's own files list for a local server.</summary>
    private static JsonObject WithAllTools(JsonObject entry)
    {
        var copy = entry.DeepClone().AsObject();
        copy["tools"] = new JsonArray("*");
        return copy;
    }

    private static string CommandLine(JsonObject entry) =>
        string.Join(' ', entry["args"]!.AsArray().Select(a => a!.GetValue<string>()).Prepend((string)entry["command"]!));

    private static string WithNote(McpFile target, string text) => target.Note is null ? text + ")" : text + "; " + target.Note + ")";

    private static async Task WriteJsonAsync(GuardedFiles files, string repo, McpFile target, JsonObject entry, bool container, List<string> report, CancellationToken ct)
    {
        var path = Path.Combine(repo, target.Path.Replace('/', Path.DirectorySeparatorChar));
        var existing = File.Exists(path) ? await File.ReadAllTextAsync(path, ct).ConfigureAwait(false) : null;
        var problem = WithServer(existing, entry, replace: container, out var updated, target.Key);
        if (problem is not null)
        {
            report.Add($"kept {target.Path}: {problem}; add \"{ServerName}\": {HintJson(entry)} under {target.Key} by hand");
            return;
        }

        if (updated is null)
        {
            report.Add(WithNote(target, $"kept {target.Path} (it already registers the {ServerName} server"));
            return;
        }

        var outcome = await files.WriteAsync(WriteTarget.Setup, path, new UTF8Encoding(false).GetBytes(updated), overwrite: true, ct).ConfigureAwait(false);
        report.Add(WithNote(target, (outcome == WriteOutcome.Created ? "created" : "updated") + $" {target.Path} (server {ServerName}: " + CommandLine(entry)));
    }

    private static async Task WriteTomlAsync(GuardedFiles files, string repo, McpFile target, JsonObject entry, bool container, List<string> report, CancellationToken ct)
    {
        var path = Path.Combine(repo, target.Path.Replace('/', Path.DirectorySeparatorChar));
        var existing = File.Exists(path) ? await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false) : null;
        var problem = WithTomlServer(existing, entry, out var updated);
        if (problem is not null)
        {
            report.Add($"kept {target.Path}: {problem}; add this table by hand:\n" + TomlTable(entry, "\n").TrimEnd('\n'));
            return;
        }

        if (updated is null)
        {
            report.Add(container
                ? $"kept {target.Path} (it already has a [{target.Key}.{ServerName}] table; init never rewrites this file: change the table by hand to run the container)"
                : WithNote(target, $"kept {target.Path} (it already registers the {ServerName} server"));
            return;
        }

        var outcome = await files.WriteAsync(WriteTarget.Setup, path, updated, overwrite: true, ct).ConfigureAwait(false);
        report.Add(WithNote(target, (outcome == WriteOutcome.Created ? "created" : "updated") + $" {target.Path} (server {ServerName}: " + CommandLine(entry)));
    }

    /// <summary>
    /// Adds the <c>[mcp_servers.maquettiste]</c> table to the end of a Codex <c>config.toml</c>, conservatively: the file's own text is
    /// never changed, only appended to, and only when nothing in it could clash with the new table. A file whose lines hold the exact
    /// header <c>[mcp_servers.maquettiste]</c> already registers the server. A file that mentions <c>maquettiste</c> anywhere else, declares
    /// the <c>[mcp_servers]</c> table itself or an inline <c>mcp_servers = ...</c>, or is not UTF-8 text, is left alone.
    /// </summary>
    /// <param name="existing">The file's bytes, or <see langword="null"/> when there is none.</param>
    /// <param name="entry">The server entry (its <c>command</c> and <c>args</c>).</param>
    /// <param name="updated">The new bytes; <see langword="null"/> when the file already registers the server.</param>
    /// <returns><see langword="null"/> on success, else why the file was left alone.</returns>
    internal static string? WithTomlServer(byte[]? existing, JsonObject entry, out byte[]? updated)
    {
        ArgumentNullException.ThrowIfNull(entry);
        updated = null;
        string text;
        try
        {
            text = existing is null ? "" : new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(existing);
        }
        catch (DecoderFallbackException)
        {
            return "it is not UTF-8 text";
        }

        var lines = text.Split('\n').Select(l => TomlBare(WithoutComment(l.TrimStart('\uFEFF')))).ToList();
        var header = "[mcp_servers." + ServerName + "]";
        if (lines.Contains(header))
            return null;
        if (text.Contains(ServerName, StringComparison.OrdinalIgnoreCase))
            return $"it mentions {ServerName} but has no exact {header} header";
        if (lines.Any(l => l == "[mcp_servers]" || l.StartsWith("mcp_servers=", StringComparison.Ordinal)))
            return "it declares the mcp_servers table itself";

        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var prefix = text.Length == 0 ? "" : text.EndsWith('\n') ? newline : newline + newline;
        updated = new UTF8Encoding(false).GetBytes(text + prefix + TomlTable(entry, newline));
        return null;
    }

    /// <summary>A TOML line without a <c>#</c> comment that starts outside quotes.</summary>
    private static string WithoutComment(string line)
    {
        char? quote = null;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (quote is null && c == '#')
                return line[..i];
            if (c is '"' or '\'')
                quote = quote is null ? c : quote == c ? null : quote;
            else if (c == '\\' && quote == '"')
                i++;
        }

        return line;
    }

    /// <summary>A TOML line without spaces, enough to compare a table header or a key's start.</summary>
    private static string TomlBare(string line)
    {
        var builder = new StringBuilder(line.Length);
        foreach (var c in line)
        {
            if (c is not (' ' or '\t' or '\r'))
                builder.Append(c);
        }

        return builder.ToString();
    }

    /// <summary>The table init appends: <c>command</c> and <c>args</c> as TOML basic strings.</summary>
    internal static string TomlTable(JsonObject entry, string newline)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var args = string.Join(", ", entry["args"]!.AsArray().Select(a => TomlString(a!.GetValue<string>())));
        return $"[mcp_servers.{ServerName}]{newline}command = {TomlString((string)entry["command"]!)}{newline}args = [{args}]{newline}";
    }

    private static string TomlString(string value)
    {
        var builder = new StringBuilder(value.Length + 2).Append('"');
        foreach (var c in value)
        {
            switch (c)
            {
                case '"':
                    builder.Append("\\\"");
                    break;
                case '\\':
                    builder.Append("\\\\");
                    break;
                case '\n':
                    builder.Append("\\n");
                    break;
                case '\r':
                    builder.Append("\\r");
                    break;
                case '\t':
                    builder.Append("\\t");
                    break;
                default:
                    if (c < 0x20 || c == 0x7F)
                        builder.Append("\\u").Append(((int)c).ToString("X4", System.Globalization.CultureInfo.InvariantCulture));
                    else
                        builder.Append(c);
                    break;
            }
        }

        return builder.Append('"').ToString();
    }

    private static async Task RemoveLegacyWrapperAsync(GuardedFiles files, string repo, List<string> report, CancellationToken ct)
    {
        var path = Path.Combine(repo, LegacyWrapperPath);
        if (!File.Exists(path) || !IsLegacyWrapper(await File.ReadAllTextAsync(path, ct).ConfigureAwait(false)))
            return;
        files.Delete(WriteTarget.Setup, path);
        report.Add($"removed {LegacyWrapperPath} (the wrapper an earlier init wrote; .mcp.json now starts the container itself)");
    }

    /// <summary>
    /// Which copies of the modeling skill an init run writes, in the order of <see cref="SkillFolders"/>: the copy in each folder of
    /// <paramref name="create"/> (written when missing), and, when <paramref name="refreshExisting"/>, every other copy that exists
    /// (never created there). A copy in <c>.agents/skills</c> or <c>.kiro/skills</c> without the engine's header is the team's own: it
    /// is listed with <c>Own</c> set and never written. Two folders that are one folder through a link give one copy.
    /// </summary>
    /// <param name="repo">The repository root.</param>
    /// <param name="create">The skills folders that get a copy.</param>
    /// <param name="refreshExisting">Whether copies in the other folders are refreshed too (<c>--skill</c>).</param>
    /// <returns>The copies' repo-relative paths, and whether each is the team's own.</returns>
    public static IReadOnlyList<(string Path, bool Own)> SkillCopies(string repo, IReadOnlyCollection<string> create, bool refreshExisting)
    {
        ArgumentNullException.ThrowIfNull(create);
        var seen = new HashSet<string>(StringComparer.FromComparison(FileSystemPaths.Comparison));
        var copies = new List<(string, bool)>();
        foreach (var folder in SkillFolders)
        {
            var relative = SkillPathIn(folder);
            var full = FullPath(repo, relative);
            var exists = File.Exists(full);
            if (!create.Contains(folder) && !(refreshExisting && exists))
                continue;
            if (!seen.Add(RealPathOrSelf(full)))
                continue;
            copies.Add((relative, exists && folder != ClaudeSkills && !IsEngineCopy(File.ReadAllBytes(full))));
        }

        return copies;
    }

    /// <summary>Whether a copy of the skill that init may own exists: the <c>.claude/skills</c> copy, or a copy elsewhere with the header.</summary>
    /// <param name="repo">The repository root.</param>
    /// <returns><see langword="true"/> when one exists.</returns>
    public static bool AnyEngineCopy(string repo) =>
        SkillCopies(repo, [], refreshExisting: true).Any(c => !c.Own);

    /// <summary>Whether a file carries the engine's header line (whatever its hash says), so the engine owns it.</summary>
    /// <param name="bytes">The file's bytes.</param>
    /// <returns><see langword="true"/> when it does.</returns>
    internal static bool IsEngineCopy(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        var text = new UTF8Encoding(false).GetString(bytes).TrimStart('\uFEFF');
        return text.StartsWith(HeaderPrefix, StringComparison.Ordinal) || text.Contains("\n" + HeaderPrefix, StringComparison.Ordinal);
    }

    private static string RealPathOrSelf(string full)
    {
        try
        {
            return FileSystemPaths.RealPath(full);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return full;
        }
    }

    /// <summary>Installs or refreshes the copies <see cref="SkillCopies"/> lists; a copy of the team's own gets a line and is left alone.</summary>
    /// <param name="files">The guarded writer.</param>
    /// <param name="repo">The repository root.</param>
    /// <param name="copies">The copies.</param>
    /// <param name="force">Whether a copy edited by hand is overwritten (<c>--force</c>).</param>
    /// <param name="report">The report lines.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>A task.</returns>
    public static async Task WriteSkillsAsync(GuardedFiles files, string repo, IReadOnlyList<(string Path, bool Own)> copies, bool force,
        List<string> report, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(copies);
        ArgumentNullException.ThrowIfNull(report);
        foreach (var (relative, own) in copies)
        {
            if (own)
                report.Add($"kept {relative} (it has no maquettiste header, so it is your own copy; init leaves it alone)");
            else
                await WriteSkillAsync(files, repo, relative, force, report, ct).ConfigureAwait(false);
        }
    }

    private static string FullPath(string repo, string relative) => Path.Combine(repo, relative.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>
    /// Installs or refreshes one copy of the modeling skill. A missing file is written; a copy that this release or an earlier one wrote
    /// and nobody changed is rewritten; a copy edited by hand is left alone, the new version goes to <c>SKILL.md.new</c> beside it, and
    /// the report says why (<paramref name="force"/> overwrites it instead). <c>CONVENTIONS.md</c> in the same folder is never written.
    /// A leftover <c>SKILL.md.new</c> is removed once the skill is current, unless it was edited too.
    /// </summary>
    /// <param name="files">The guarded writer.</param>
    /// <param name="repo">The repository root.</param>
    /// <param name="relative">The copy's repo-relative path, such as <see cref="SkillPath"/>.</param>
    /// <param name="force">Whether a copy edited by hand is overwritten (<c>--force</c>).</param>
    /// <param name="report">The report lines.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>A task.</returns>
    public static async Task WriteSkillAsync(GuardedFiles files, string repo, string relative, bool force, List<string> report, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(report);
        var path = FullPath(repo, relative);
        var newPath = path + ".new";
        var current = SkillBytes();
        var state = Classify(File.Exists(path) ? await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false) : null, current, out var version);
        if (state == SkillState.Edited && !force)
        {
            await files.WriteAsync(WriteTarget.Setup, newPath, current, overwrite: true, ct).ConfigureAwait(false);
            report.Add(EditedMessage(relative));
            return;
        }

        if (state != SkillState.Current)
            await files.WriteAsync(WriteTarget.Setup, path, current, overwrite: true, ct).ConfigureAwait(false);
        report.Add(state switch
        {
            SkillState.Missing => "created " + relative,
            SkillState.Shipped => $"updated {relative} (from the {version} version to {EngineVersion.Product})",
            SkillState.Edited => $"replaced {relative}, which was edited by hand (--force)",
            _ => "kept " + relative + " (current)",
        });

        if (File.Exists(newPath))
        {
            if (Classify(await File.ReadAllBytesAsync(newPath, ct).ConfigureAwait(false), current, out _) == SkillState.Edited)
            {
                report.Add($"kept {relative}.new: it was edited; remove it when you no longer need it");
            }
            else
            {
                files.Delete(WriteTarget.Setup, newPath);
                report.Add($"removed {relative}.new");
            }
        }
    }

    /// <summary>The line <c>init --skill</c> reports when it leaves a copy edited by hand alone.</summary>
    /// <param name="relative">The copy's repo-relative path.</param>
    /// <returns>The line.</returns>
    public static string EditedMessage(string relative) => "skill not updated: " + relative + " was edited by hand; the new version is in SKILL.md.new "
        + "(diff them, move your additions to CONVENTIONS.md, then run init --skill --force)";

    /// <summary>
    /// <c>init --skill --check</c>: compares every copy of the skill the engine owns (<see cref="SkillCopies"/>; a copy of the team's own
    /// in <c>.agents/skills</c> or <c>.kiro/skills</c> is not checked) with the one this release writes and writes nothing. Prints one line per copy on stdout (<c>skill missing</c> for <see cref="SkillPath"/> when there is no
    /// copy at all) and returns 0 when every copy is current, 2 (drift, as <c>generate --check</c>) when one is missing, stale or edited.
    /// </summary>
    /// <param name="output">Standard output.</param>
    /// <param name="repo">The repository root.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The exit code.</returns>
    public static async Task<int> CheckSkillAsync(TextWriter output, string repo, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(output);
        var copies = SkillCopies(repo, [], refreshExisting: true).Where(c => !c.Own).Select(c => c.Path).ToList();
        if (copies.Count == 0)
            copies.Add(SkillPath);
        var current = SkillBytes();
        var exit = Program.ExitCodes.Success;
        foreach (var relative in copies)
        {
            var path = FullPath(repo, relative);
            var state = Classify(File.Exists(path) ? await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false) : null, current, out var version);
            await output.WriteLineAsync(state switch
            {
                SkillState.Current => $"skill current: {relative} (maquettiste {EngineVersion.Product})",
                SkillState.Missing => $"skill missing: {relative} (run maquettiste init --skill)",
                SkillState.Shipped => $"skill stale: {relative} is the {version} version, not {EngineVersion.Product} (run maquettiste init --skill)",
                _ => $"skill edited: {relative} was edited by hand (move your additions to CONVENTIONS.md, then run maquettiste init --skill --force)",
            }).ConfigureAwait(false);
            if (state != SkillState.Current)
                exit = Program.ExitCodes.Drift;
        }

        return exit;
    }

    private static async Task<bool> UsesLocalToolAsync(string repo, CancellationToken ct)
    {
        foreach (var manifest in new[] { Path.Combine(repo, ".config", "dotnet-tools.json"), Path.Combine(repo, "dotnet-tools.json") })
        {
            if (!File.Exists(manifest))
                continue;
            try
            {
                using var doc = JsonDocument.Parse(await File.ReadAllBytesAsync(manifest, ct).ConfigureAwait(false), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
                if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("tools", out var tools) && tools.ValueKind == JsonValueKind.Object)
                {
                    foreach (var tool in tools.EnumerateObject())
                    {
                        if (tool.Value.ValueKind == JsonValueKind.Object && tool.Value.TryGetProperty("commands", out var commands) && commands.ValueKind == JsonValueKind.Array
                            && commands.EnumerateArray().Any(c => c.ValueKind == JsonValueKind.String && string.Equals(c.GetString(), "maquettiste", StringComparison.Ordinal)))
                        {
                            return true;
                        }
                    }
                }
            }
            catch (Exception e) when (e is JsonException or IOException)
            {
                // An unreadable manifest counts as no local tool.
            }
        }

        return false;
    }
}

/// <summary>The format of an AI tool's MCP file.</summary>
internal enum McpFileFormat
{
    /// <summary>A JSON object whose member <see cref="McpFile.Key"/> holds the servers by name.</summary>
    Json,

    /// <summary>A TOML file with one <c>[mcp_servers.&lt;name&gt;]</c> table per server (Codex).</summary>
    Toml,
}

/// <summary>A project file an AI tool reads to find MCP servers.</summary>
/// <param name="Path">The repo-relative path.</param>
/// <param name="Format">The file's format.</param>
/// <param name="Key">The member (JSON) or table (TOML) that holds the servers.</param>
/// <param name="AllTools">Whether the entry lists <c>"tools": ["*"]</c> (GitHub Copilot's own files).</param>
/// <param name="Note">A short note the report line ends with, such as a trust or approval step.</param>
internal sealed record McpFile(string Path, McpFileFormat Format, string Key, bool AllTools = false, string? Note = null);

/// <summary>An AI tool that <c>init --agents</c> sets up.</summary>
/// <param name="Name">The name <c>--agents</c> takes.</param>
/// <param name="McpFiles">The project files it reads to find MCP servers.</param>
/// <param name="SkillFolder">The skills folder it reads, or <see langword="null"/> when it has none.</param>
internal sealed record AgentTool(string Name, IReadOnlyList<McpFile> McpFiles, string? SkillFolder);
