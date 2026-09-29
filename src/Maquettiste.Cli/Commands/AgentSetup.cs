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

    /// <summary>Registers the server in <c>.mcp.json</c>; an entry named <c>maquettiste</c> that is already there is kept as is.</summary>
    /// <param name="files">The guarded writer.</param>
    /// <param name="repo">The repository root.</param>
    /// <param name="report">The report lines.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>A task.</returns>
    public static async Task WriteMcpConfigAsync(GuardedFiles files, string repo, List<string> report, CancellationToken ct)
    {
        var path = Path.Combine(repo, ".mcp.json");
        var existing = File.Exists(path) ? await File.ReadAllTextAsync(path, ct).ConfigureAwait(false) : null;
        var entry = ServerEntry(await UsesLocalToolAsync(repo, ct).ConfigureAwait(false));
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
        report.Add((outcome == WriteOutcome.Created ? "created" : "updated") + $" .mcp.json (server {ServerName}: {entry["command"]} {string.Join(' ', entry["args"]!.AsArray().Select(a => a!.GetValue<string>()))})");
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
