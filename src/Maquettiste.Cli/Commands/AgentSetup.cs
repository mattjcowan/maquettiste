using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Maquettiste.Cli.Mcp;
using Maquettiste.Engine;
using Maquettiste.Engine.Pipeline;

namespace Maquettiste.Cli.Commands;

/// <summary>
/// The agent setup of <c>maquettiste init --mcp / --skill / --agent-setup</c> (docs/mcp.md): registers the <c>maquettiste mcp</c>
/// server in the repository's <c>.mcp.json</c> (the project-scoped MCP configuration that MCP clients read) and installs
/// the modeling skill as <c>.claude/skills/maquettiste-modeling/SKILL.md</c>, a file the engine owns: it carries a header with the
/// release and a hash, is rewritten only while nobody edited it, and leaves the repository's own conventions to
/// <c>CONVENTIONS.md</c> beside it, which init never writes. Every write, and the removal of an earlier version's
/// <c>mcp.sh</c>, goes through the path policy as a setup write.
/// </summary>
internal static class AgentSetup
{
    /// <summary>The server name the registration uses in <c>mcpServers</c>.</summary>
    public const string ServerName = "maquettiste";

    /// <summary>The repo-relative path of the installed skill.</summary>
    public const string SkillPath = ".claude/skills/maquettiste-modeling/SKILL.md";

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
    /// Adds the <c>maquettiste</c> server to an existing <c>.mcp.json</c> text, keeping every other entry and member in order. An
    /// existing <c>maquettiste</c> entry is kept as is, unless <paramref name="replace"/> asks for it to be replaced in place.
    /// </summary>
    /// <param name="existing">The current file, or <see langword="null"/> when there is none.</param>
    /// <param name="entry">The server entry.</param>
    /// <param name="replace">Whether an existing <c>maquettiste</c> entry that differs from <paramref name="entry"/> is replaced.</param>
    /// <param name="updated">The new text (LF, two-space indent, trailing newline); <see langword="null"/> when nothing changes.</param>
    /// <returns><see langword="null"/> on success, else why the file was left alone.</returns>
    public static string? WithServer(string? existing, JsonObject entry, bool replace, out string? updated)
    {
        ArgumentNullException.ThrowIfNull(entry);
        updated = null;
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

            if (root["mcpServers"] is null)
            {
                if (root.ContainsKey("mcpServers"))
                    root.Remove("mcpServers");
                servers = [];
                root["mcpServers"] = servers;
            }
            else if (root["mcpServers"] is JsonObject found)
            {
                servers = found;
            }
            else
            {
                return "its mcpServers member is not an object";
            }

            if (servers.ContainsKey(ServerName) && (!replace || JsonNode.DeepEquals(servers[ServerName], entry)))
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

        servers[ServerName] = entry.DeepClone();
        updated = root.ToJsonString(WriteOptions) + "\n";
        return null;
    }

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
            + "repository's own modeling conventions in `CONVENTIONS.md` next to this file; `init --skill` never touches that file,\n"
            + "and agents read it after this one. A copy edited by hand is left alone: `init --skill` then writes the new version to\n"
            + "`SKILL.md.new` (`--force` overwrites), and `init --skill --check` exits 2 when this file is missing, stale or edited.\n";
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
    /// Registers the server in <c>.mcp.json</c>. The installed-tool form keeps an entry named <c>maquettiste</c> that is already there as
    /// is; the container form replaces it, so a re-run with another tag or runtime takes effect, and then removes the <c>mcp.sh</c>
    /// wrapper an earlier version wrote (only when it carries the marker; any other <c>mcp.sh</c> is left alone).
    /// </summary>
    /// <param name="files">The guarded writer.</param>
    /// <param name="repo">The repository root.</param>
    /// <param name="dockerImage">The image of the container form (<c>--docker</c>), or <see langword="null"/> for the installed tool.</param>
    /// <param name="runtime">The container command of the container form (<c>--runtime</c>, default <c>docker</c>).</param>
    /// <param name="report">The report lines.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>A task.</returns>
    public static async Task WriteMcpConfigAsync(GuardedFiles files, string repo, string? dockerImage, string runtime, List<string> report, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(report);
        var path = Path.Combine(repo, ".mcp.json");
        var existing = File.Exists(path) ? await File.ReadAllTextAsync(path, ct).ConfigureAwait(false) : null;
        var container = dockerImage is not null;
        var entry = container ? ContainerServerEntry(runtime, dockerImage!) : ServerEntry(await UsesLocalToolAsync(repo, ct).ConfigureAwait(false));
        var problem = WithServer(existing, entry, replace: container, out var updated);
        if (problem is not null)
        {
            report.Add($"kept .mcp.json: {problem}; add \"{ServerName}\": {entry.ToJsonString()} under mcpServers by hand");
            return;
        }

        if (updated is null)
        {
            report.Add($"kept .mcp.json (it already registers the {ServerName} server)");
        }
        else
        {
            var outcome = await files.WriteAsync(WriteTarget.Setup, path, new UTF8Encoding(false).GetBytes(updated), overwrite: true, ct).ConfigureAwait(false);
            report.Add((outcome == WriteOutcome.Created ? "created" : "updated") + $" .mcp.json (server {ServerName}: " + string.Join(' ', entry["args"]!.AsArray().Select(a => a!.GetValue<string>()).Prepend((string)entry["command"]!)) + ")");
        }

        if (container)
            await RemoveLegacyWrapperAsync(files, repo, report, ct).ConfigureAwait(false);
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
    /// Installs or refreshes the modeling skill. A missing file is written; a copy that this release or an earlier one wrote and nobody
    /// changed is rewritten; a copy edited by hand is left alone, the new version goes to <c>SKILL.md.new</c> beside it, and the report
    /// says why (<paramref name="force"/> overwrites it instead). <c>CONVENTIONS.md</c> in the same folder is never written. A leftover
    /// <c>SKILL.md.new</c> is removed once the skill is current, unless it was edited too.
    /// </summary>
    /// <param name="files">The guarded writer.</param>
    /// <param name="repo">The repository root.</param>
    /// <param name="force">Whether a copy edited by hand is overwritten (<c>--force</c>).</param>
    /// <param name="report">The report lines.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>A task.</returns>
    public static async Task WriteSkillAsync(GuardedFiles files, string repo, bool force, List<string> report, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(report);
        var path = Path.Combine(repo, SkillPath.Replace('/', Path.DirectorySeparatorChar));
        var newPath = path + ".new";
        var current = SkillBytes();
        var state = Classify(File.Exists(path) ? await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false) : null, current, out var version);
        if (state == SkillState.Edited && !force)
        {
            await files.WriteAsync(WriteTarget.Setup, newPath, current, overwrite: true, ct).ConfigureAwait(false);
            report.Add(EditedMessage);
            return;
        }

        if (state != SkillState.Current)
            await files.WriteAsync(WriteTarget.Setup, path, current, overwrite: true, ct).ConfigureAwait(false);
        report.Add(state switch
        {
            SkillState.Missing => "created " + SkillPath,
            SkillState.Shipped => $"updated {SkillPath} (from the {version} version to {EngineVersion.Product})",
            SkillState.Edited => $"replaced {SkillPath}, which was edited by hand (--force)",
            _ => "kept " + SkillPath + " (current)",
        });

        if (File.Exists(newPath))
        {
            if (Classify(await File.ReadAllBytesAsync(newPath, ct).ConfigureAwait(false), current, out _) == SkillState.Edited)
            {
                report.Add($"kept {SkillPath}.new: it was edited; remove it when you no longer need it");
            }
            else
            {
                files.Delete(WriteTarget.Setup, newPath);
                report.Add($"removed {SkillPath}.new");
            }
        }
    }

    /// <summary>The line <c>init --skill</c> reports when it leaves a copy edited by hand alone.</summary>
    public const string EditedMessage = "skill not updated: " + SkillPath + " was edited by hand; the new version is in SKILL.md.new "
        + "(diff them, move your additions to CONVENTIONS.md, then run init --skill --force)";

    /// <summary>
    /// <c>init --skill --check</c>: compares the installed skill with the one this release writes and writes nothing. Prints one line on
    /// stdout and returns 0 when it is current, 2 (drift, as <c>generate --check</c>) when it is missing, stale or edited by hand.
    /// </summary>
    /// <param name="output">Standard output.</param>
    /// <param name="repo">The repository root.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The exit code.</returns>
    public static async Task<int> CheckSkillAsync(TextWriter output, string repo, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(output);
        var path = Path.Combine(repo, SkillPath.Replace('/', Path.DirectorySeparatorChar));
        var state = Classify(File.Exists(path) ? await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false) : null, SkillBytes(), out var version);
        await output.WriteLineAsync(state switch
        {
            SkillState.Current => $"skill current: {SkillPath} (maquettiste {EngineVersion.Product})",
            SkillState.Missing => $"skill missing: {SkillPath} (run maquettiste init --skill)",
            SkillState.Shipped => $"skill stale: {SkillPath} is the {version} version, not {EngineVersion.Product} (run maquettiste init --skill)",
            _ => $"skill edited: {SkillPath} was edited by hand (move your additions to CONVENTIONS.md, then run maquettiste init --skill --force)",
        }).ConfigureAwait(false);
        return state == SkillState.Current ? Program.ExitCodes.Success : Program.ExitCodes.Drift;
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
