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
/// the modeling skill as <c>.claude/skills/maquettiste-modeling/SKILL.md</c>. Both writes go through the path policy as setup writes.
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

    /// <summary>The repo-relative path of the Docker wrapper that <c>init --mcp --docker</c> writes.</summary>
    public const string DockerWrapperPath = "mcp.sh";

    /// <summary>The line that marks a wrapper maquettiste wrote, so a re-run refreshes it and never replaces someone else's script.</summary>
    public const string DockerWrapperMarker = "# maquettiste-mcp-wrapper";

    /// <summary>Returns the server entry of the Docker form: the repository's <c>./mcp.sh</c>, which runs the server in the image.</summary>
    /// <returns>The entry.</returns>
    public static JsonObject DockerServerEntry() => new()
    {
        ["type"] = "stdio",
        ["command"] = "./" + DockerWrapperPath,
        ["args"] = new JsonArray(),
    };

    /// <summary>Whether an image reference is safe to write into the wrapper unquoted: a name, an optional tag and an optional digest.</summary>
    /// <param name="image">The reference, such as <c>mattjcowan/maquettiste:0.2.0</c>.</param>
    /// <returns><see langword="true"/> when it is usable.</returns>
    public static bool IsImageReference(string image) =>
        image.Length is > 0 and <= 255 && char.IsAsciiLetterOrDigit(image[0])
        && image.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' or '/' or ':' or '@');

    /// <summary>
    /// Returns the wrapper script: it finds docker (an MCP client may start it with a short PATH), runs <c>maquettiste mcp</c> in the image
    /// over this folder, keeps stdout for the protocol and appends the server's messages to <c>.maquettiste/.cache/mcp.log</c>. The
    /// container starts as root (<c>--user 0:0</c>) so the image's entrypoint repairs files an earlier run left owned by another user
    /// and runs the server as the owner of this folder (under rootless Podman it stays root, which is the user outside). The engine
    /// cache lives in <c>.maquettiste/.cache/cli</c> so it survives the container.
    /// </summary>
    /// <param name="image">The image reference (checked by <see cref="IsImageReference"/>).</param>
    /// <returns>The script (LF line endings).</returns>
    public static string DockerWrapper(string image) => $$"""
        #!/bin/sh
        {{DockerWrapperMarker}} (written by maquettiste init --mcp --docker; docs/mcp.md)
        # Runs the maquettiste MCP server in Docker for an MCP client started in this folder. Stdout carries the protocol only; the
        # server's messages go to .maquettiste/.cache/mcp.log. MAQUETTISTE_IMAGE overrides the image, MAQUETTISTE_DOCKER the docker path.
        # It starts as root: the image runs the server as the owner of this folder (docker/README.md "File ownership").
        set -eu
        cd "$(dirname "$0")"
        image="${MAQUETTISTE_IMAGE:-{{image}}}"
        docker="${MAQUETTISTE_DOCKER:-}"
        if [ -z "$docker" ]; then
          docker=$(command -v docker 2>/dev/null || true)
        fi
        if [ -z "$docker" ]; then
          for candidate in /usr/local/bin/docker /opt/homebrew/bin/docker "$HOME/.docker/bin/docker"             /Applications/Docker.app/Contents/Resources/bin/docker /usr/bin/docker; do
            if [ -x "$candidate" ]; then docker="$candidate"; break; fi
          done
        fi
        mkdir -p .maquettiste/.cache 2>/dev/null || true
        log=.maquettiste/.cache/mcp.log
        # A log an earlier run as root left behind cannot be appended to until the container repairs it: log to /tmp this time.
        if ! { true >> "$log"; } 2>/dev/null; then log="${TMPDIR:-/tmp}/maquettiste-mcp-$(id -u).log"; fi
        if [ -z "$docker" ]; then
          echo "mcp.sh: docker not found; set MAQUETTISTE_DOCKER to its path" >> "$log"
          echo "mcp.sh: docker not found; set MAQUETTISTE_DOCKER to its path" >&2
          exit 127
        fi
        exec "$docker" run -i --rm --user 0:0 -v "$PWD:/repo" -w /repo           -e MAQUETTISTE_CACHE_DIR=/repo/.maquettiste/.cache/cli "$image" maquettiste mcp 2>> "$log"

        """.Replace("\r\n", "\n", StringComparison.Ordinal);

    /// <summary>
    /// Adds the <c>maquettiste</c> server to an existing <c>.mcp.json</c> text, keeping every other entry and member in order.
    /// </summary>
    /// <param name="existing">The current file, or <see langword="null"/> when there is none.</param>
    /// <param name="entry">The server entry.</param>
    /// <param name="updated">The new text (LF, two-space indent, trailing newline); <see langword="null"/> when nothing changes.</param>
    /// <returns><see langword="null"/> on success, else why the file was left alone.</returns>
    public static string? WithServer(string? existing, JsonObject entry, out string? updated)
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

            if (servers.ContainsKey(ServerName))
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
    /// Registers the server in <c>.mcp.json</c>; an entry named <c>maquettiste</c> that is already there is kept as is. With an image, the
    /// entry runs <c>./mcp.sh</c>, which this writes (executable) unless a script maquettiste did not write is already there.
    /// </summary>
    /// <param name="files">The guarded writer.</param>
    /// <param name="repo">The repository root.</param>
    /// <param name="dockerImage">The image of the Docker form (<c>--docker</c>), or <see langword="null"/> for the installed tool.</param>
    /// <param name="report">The report lines.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>A task.</returns>
    public static async Task WriteMcpConfigAsync(GuardedFiles files, string repo, string? dockerImage, List<string> report, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(report);
        if (dockerImage is not null)
            await WriteDockerWrapperAsync(files, repo, dockerImage, report, ct).ConfigureAwait(false);
        var path = Path.Combine(repo, ".mcp.json");
        var existing = File.Exists(path) ? await File.ReadAllTextAsync(path, ct).ConfigureAwait(false) : null;
        var entry = dockerImage is null ? ServerEntry(await UsesLocalToolAsync(repo, ct).ConfigureAwait(false)) : DockerServerEntry();
        var problem = WithServer(existing, entry, out var updated);
        if (problem is not null)
        {
            report.Add($"kept .mcp.json: {problem}; add \"{ServerName}\": {entry.ToJsonString()} under mcpServers by hand");
            return;
        }

        if (updated is null)
        {
            report.Add($"kept .mcp.json (it already registers the {ServerName} server)");
            return;
        }

        var outcome = await files.WriteAsync(WriteTarget.Setup, path, new UTF8Encoding(false).GetBytes(updated), overwrite: true, ct).ConfigureAwait(false);
        report.Add((outcome == WriteOutcome.Created ? "created" : "updated") + $" .mcp.json (server {ServerName}: " + string.Join(' ', entry["args"]!.AsArray().Select(a => a!.GetValue<string>()).Prepend((string)entry["command"]!)) + ")");
    }

    private static async Task WriteDockerWrapperAsync(GuardedFiles files, string repo, string image, List<string> report, CancellationToken ct)
    {
        var path = Path.Combine(repo, DockerWrapperPath);
        var mine = !File.Exists(path) || (await File.ReadAllTextAsync(path, ct).ConfigureAwait(false)).Contains(DockerWrapperMarker, StringComparison.Ordinal);
        var outcome = await files.WriteAsync(WriteTarget.Setup, path, new UTF8Encoding(false).GetBytes(DockerWrapper(image)), overwrite: mine, ct).ConfigureAwait(false);
        if (outcome == WriteOutcome.Kept && !mine)
        {
            report.Add($"kept {DockerWrapperPath}: a script maquettiste did not write; move it away and run init --mcp --docker again");
            return;
        }

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead
                | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }

        report.Add(outcome switch
        {
            WriteOutcome.Created => $"created {DockerWrapperPath} (runs maquettiste mcp in {image}; log in .maquettiste/.cache/mcp.log)",
            WriteOutcome.Updated => $"updated {DockerWrapperPath} (runs maquettiste mcp in {image})",
            _ => $"kept {DockerWrapperPath} (current)",
        });
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
