using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Maquettiste.Cli.Mcp;
using Maquettiste.Engine.Pipeline;

namespace Maquettiste.Cli.Commands;

/// <summary>
/// The agent setup of <c>maquettiste init --mcp / --skill / --agent-setup</c> (docs/mcp.md): registers the <c>maquettiste mcp</c>
/// server in the repository's <c>.mcp.json</c> (the project-scoped MCP configuration that MCP clients read) and installs
/// the modeling skill as <c>.claude/skills/maquettiste-modeling/SKILL.md</c>. Both writes, and the removal of an earlier version's
/// <c>mcp.sh</c>, go through the path policy as setup writes.
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
    /// server's stderr is appended to <c>.maquettiste/.cache/mcp.log</c>, under the cache folder git ignores. On Windows, which has no
    /// <c>/bin/sh</c>, the entry runs the container command itself with <c>${PWD}</c>, which the client expands. Either way the file has
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
                "-e", "MAQUETTISTE_CACHE_DIR=/repo/.maquettiste/.cache/cli", image, "maquettiste", "mcp"),
        }
        : new JsonObject
        {
            ["type"] = "stdio",
            ["command"] = "/bin/sh",
            ["args"] = new JsonArray("-c", ContainerCommandLine(runtime, image)),
        };

    /// <summary>
    /// Returns the <c>/bin/sh -c</c> line of the container form outside Windows. It has no <c>${...}</c>, which an MCP client would
    /// expand itself: the shell expands <c>$PATH</c>, <c>$HOME</c> and <c>$(pwd -P)</c>. The image reference needs no quoting
    /// (<see cref="IsImageReference"/>).
    /// </summary>
    /// <param name="runtime">The container command, <c>docker</c> or <c>podman</c>.</param>
    /// <param name="image">The image reference.</param>
    /// <returns>The line.</returns>
    public static string ContainerCommandLine(string runtime, string image) =>
        "export PATH=\"$PATH:/opt/homebrew/bin:/usr/local/bin:$HOME/.docker/bin\"; mkdir -p .maquettiste/.cache; "
        + $"exec {runtime} run -i --rm --user 0:0 -v \"$(pwd -P):/repo\" -w /repo -e MAQUETTISTE_CACHE_DIR=/repo/.maquettiste/.cache/cli "
        + $"{image} maquettiste mcp 2>>.maquettiste/.cache/mcp.log";

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
    /// <returns>The UTF-8 bytes.</returns>
    public static byte[] SkillBytes()
    {
        using var stream = typeof(McpServerSetup).Assembly.GetManifestResourceStream(McpServerSetup.ConventionsResource)
            ?? throw new InvalidOperationException("The modeling skill is not embedded in the CLI.");
        using var reader = new StreamReader(stream, new UTF8Encoding(false));
        return new UTF8Encoding(false).GetBytes(reader.ReadToEnd().Replace("\r\n", "\n", StringComparison.Ordinal));
    }

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

    /// <summary>Installs (or refreshes) the modeling skill; the file tracks the installed tool's version, like the schemas.</summary>
    /// <param name="files">The guarded writer.</param>
    /// <param name="repo">The repository root.</param>
    /// <param name="report">The report lines.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>A task.</returns>
    public static async Task WriteSkillAsync(GuardedFiles files, string repo, List<string> report, CancellationToken ct)
    {
        var path = Path.Combine(repo, SkillPath.Replace('/', Path.DirectorySeparatorChar));
        var outcome = await files.WriteAsync(WriteTarget.Setup, path, SkillBytes(), overwrite: true, ct).ConfigureAwait(false);
        report.Add(outcome switch
        {
            WriteOutcome.Created => "created " + SkillPath,
            WriteOutcome.Updated => "updated " + SkillPath + " (refreshed from this version of maquettiste)",
            _ => "kept " + SkillPath + " (current)",
        });
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
