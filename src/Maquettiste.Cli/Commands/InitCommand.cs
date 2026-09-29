using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Maquettiste.Engine;
using Maquettiste.Engine.Json;
using Maquettiste.Engine.Pipeline;
using Maquettiste.Engine.Writing;

namespace Maquettiste.Cli.Commands;

/// <summary>
/// <c>maquettiste init [--pack sql-ddl|csharp-dapper|none] [--name &lt;name&gt;] [--hooks] [--mcp] [--skill] [--agent-setup]
/// [--docker &lt;image&gt;]</c> (engine-design.md section 16; SPEC sections 4, 11, 12 and 17).
/// <para>
/// Creates <c>.maquettiste/</c> with the phase 1 folders, <c>maquettiste.json</c> (format 1, output roots <c>db</c> committed and
/// <c>src/Generated</c> built), the JSON schemas in <c>.schema/v1/</c>, the starter pack, and a <c>.gitignore</c> block for the built
/// roots and <c>.maquettiste/.cache/</c>. The project is named by <c>--name</c>, else as <see cref="ProjectName"/> derives it.
/// </para>
/// <para>
/// Idempotent: existing files are kept, except <c>.schema/v1</c>, which is refreshed, and the <c>.gitignore</c> block, which is
/// rewritten in place. <c>--hooks</c> installs the post-checkout and post-merge hooks; <c>--mcp</c> registers <c>maquettiste mcp</c> in
/// <c>.mcp.json</c>; <c>--skill</c> installs the modeling skill under <c>.claude/skills/</c>; <c>--agent-setup</c> does both; and
/// <c>--docker &lt;image&gt;</c> registers a <c>./mcp.sh</c> wrapper that runs the server in that image (<see cref="AgentSetup"/>).
/// </para>
/// </summary>
internal static class InitCommand
{
    /// <summary>The first line of the <c>.gitignore</c> block.</summary>
    public const string BlockBegin = "# maquettiste:begin";

    /// <summary>The last line of the <c>.gitignore</c> block.</summary>
    public const string BlockEnd = "# maquettiste:end";

    /// <summary>A line every hook this command installs carries, so a re-run recognizes its own hooks.</summary>
    public const string HookMarker = "# maquettiste: installed by maquettiste init --hooks";

    /// <summary>The phase 1 model folders (S4 minus the phase 3 and 4 kinds; D25 adds packages).</summary>
    public static readonly IReadOnlyList<string> Folders =
    [
        "model/packages", "model/entities", "model/relations", "model/enums", "model/types", "model/databases", "model/mappings",
        "model/diagrams", "model/vocabularies", "templates", "extensions",
    ];

    /// <summary>Runs the command.</summary>
    /// <param name="context">The global context.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The exit code.</returns>
    public static async Task<int> RunAsync(GlobalContext context, CancellationToken ct)
    {
        context.Line.Expect("init", 1, "--pack", "--hooks", "--mcp", "--skill", "--agent-setup", "--name", "--docker");
        if (context.Line.Value("--name") is { } given && string.IsNullOrWhiteSpace(given))
            throw new UsageException("--name needs a project name.");
        var dockerImage = context.Line.Value("--docker");
        if (dockerImage is not null && !AgentSetup.IsImageReference(dockerImage))
            throw new UsageException("--docker needs an image reference such as mattjcowan/maquettiste:0.2.0 (letters, digits and . _ - / : @).");
        var pack = context.Line.Choice("--pack", "sql-ddl", "sql-ddl", "csharp-dapper", "none");
        var repo = context.RepoRoot(search: false);
        if (!Directory.Exists(repo))
            throw new UsageException($"The repo folder {repo} does not exist.");

        // The .gitignore markers are checked before anything is written, so a malformed block stops init with nothing changed.
        var gitignore = Path.Combine(repo, ".gitignore");
        var existing = File.Exists(gitignore) ? await File.ReadAllTextAsync(gitignore, ct).ConfigureAwait(false) : null;
        _ = WithBlock(existing, []);

        var options = context.EngineOptions(repo);
        var files = new GuardedFiles(new OutputPathPolicy(options, null));
        var modelRoot = Path.Combine(repo, GlobalContext.ModelFolder);
        var report = new List<string>();

        // Folders; each still empty at the end gets a .gitkeep, so git records the layout.
        foreach (var folder in Folders)
            files.CreateDirectory(WriteTarget.Model, Path.Combine(modelRoot, folder.Replace('/', Path.DirectorySeparatorChar)));

        // Settings.
        var schemas = new SchemaRegistry();
        var json = new CanonicalJson(schemas);
        var settingsPath = Path.Combine(modelRoot, "maquettiste.json");
        var settingsOutcome = await files.WriteAsync(WriteTarget.Model, settingsPath, Settings(json, ProjectName(repo, context.Line.Value("--name")), pack), overwrite: false, ct).ConfigureAwait(false);
        if (settingsOutcome == WriteOutcome.Kept && pack != "none")
            settingsOutcome = await AddPackOutputAsync(files, json, settingsPath, pack, report, ct).ConfigureAwait(false);
        report.Add(Describe(settingsOutcome, ".maquettiste/maquettiste.json"));

        // Schemas: refreshed on every run; files the engine no longer ships are removed.
        var schemaFolder = Path.Combine(modelRoot, ".schema", "v1");
        files.CreateDirectory(WriteTarget.Model, schemaFolder);
        int refreshed = 0;
        foreach (var name in schemas.FileNames)
        {
            var outcome = await files.WriteAsync(WriteTarget.Model, Path.Combine(schemaFolder, name), schemas.GetFileBytes(name), overwrite: true, ct).ConfigureAwait(false);
            if (outcome is WriteOutcome.Created or WriteOutcome.Updated)
                refreshed++;
        }

        var known = schemas.FileNames.ToHashSet(StringComparer.Ordinal);
        foreach (var stale in Directory.EnumerateFiles(schemaFolder, "*.json").Where(f => !known.Contains(Path.GetFileName(f))).Order(StringComparer.Ordinal).ToList())
        {
            files.Delete(WriteTarget.Model, stale);
        }

        report.Add($"{(refreshed == 0 ? "kept" : "wrote")} .maquettiste/.schema/v1/ ({schemas.FileNames.Count} schemas{(refreshed == 0 ? ", current" : "")})");

        // Starter pack.
        if (pack != "none")
        {
            var packFiles = StarterPacks.Files(pack, out var embedded);
            var packRoot = Path.Combine(modelRoot, "templates", pack);
            int written = 0, kept = 0;
            foreach (var (relative, bytes) in packFiles)
            {
                var outcome = await files.WriteAsync(WriteTarget.Model, Path.Combine(packRoot, relative.Replace('/', Path.DirectorySeparatorChar)), bytes, overwrite: false, ct).ConfigureAwait(false);
                if (outcome == WriteOutcome.Kept)
                    kept++;
                else
                    written++;
            }

            report.Add($"{(written == 0 ? "kept" : "wrote")} .maquettiste/templates/{pack}/ ({packFiles.Count} files{(kept > 0 ? $", {kept} kept" : "")}{(embedded ? "" : ", built-in starter")})");
        }

        foreach (var folder in Folders)
        {
            var full = Path.Combine(modelRoot, folder.Replace('/', Path.DirectorySeparatorChar));
            if (!Directory.EnumerateFileSystemEntries(full).Any())
                await files.WriteAsync(WriteTarget.Model, Path.Combine(full, ".gitkeep"), Array.Empty<byte>(), overwrite: false, ct).ConfigureAwait(false);
        }

        // .gitignore block.
        var builtRoots = BuiltRoots(settingsPath);
        var updated = WithBlock(existing, builtRoots);
        var gitOutcome = await files.WriteAsync(WriteTarget.Setup, gitignore, new UTF8Encoding(false).GetBytes(updated), overwrite: true, ct).ConfigureAwait(false);
        report.Add(Describe(gitOutcome, ".gitignore") + " (maquettiste block)");

        if (context.Line.Has("--hooks"))
            await InstallHooksAsync(files, repo, report, ct).ConfigureAwait(false);
        if (context.Line.Has("--mcp") || context.Line.Has("--agent-setup") || dockerImage is not null)
            await AgentSetup.WriteMcpConfigAsync(files, repo, dockerImage, report, ct).ConfigureAwait(false);
        if (context.Line.Has("--skill") || context.Line.Has("--agent-setup"))
            await AgentSetup.WriteSkillAsync(files, repo, report, ct).ConfigureAwait(false);

        foreach (var line in report)
            context.Info(line);
        context.Info($"Initialized maquettiste in {repo}.");
        return Program.ExitCodes.Success;
    }

    /// <summary>Replaces (or appends) the maquettiste block of a <c>.gitignore</c>, keeping every other line.</summary>
    /// <param name="existing">The current file, or <see langword="null"/>.</param>
    /// <param name="builtRoots">The built output roots, repo-relative.</param>
    /// <returns>The new text, LF, with a trailing newline.</returns>
    public static string WithBlock(string? existing, IReadOnlyList<string> builtRoots)
    {
        var block = new List<string> { BlockBegin, "# Built output roots and the run journal; regenerated by maquettiste generate." };
        block.AddRange(builtRoots.Select(r => "/" + r.Trim('/') + "/").Distinct(StringComparer.Ordinal));
        block.Add("/.maquettiste/.cache/");
        block.Add(BlockEnd);

        var lines = (existing ?? "").Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').ToList();
        if (lines.Count > 0 && lines[^1].Length == 0)
            lines.RemoveAt(lines.Count - 1);
        var begins = Indexes(lines, BlockBegin);
        var ends = Indexes(lines, BlockEnd);
        if (begins.Count > 0 || ends.Count > 0)
        {
            // Only one well-formed pair is replaced; anything else would drop user lines between stray markers.
            if (begins.Count != 1 || ends.Count != 1 || ends[0] < begins[0])
            {
                var (marker, line) = begins.Count == 0 ? (BlockEnd, ends[0])
                    : ends.Count == 0 ? (BlockBegin, begins[0])
                    : begins.Count > 1 ? (BlockBegin, begins[1])
                    : ends.Count > 1 ? (BlockEnd, ends[1])
                    : (BlockEnd, ends[0]);
                throw new UsageException(string.Create(CultureInfo.InvariantCulture,
                    $".gitignore line {line + 1}: '{marker}' has no matching '{(marker == BlockBegin ? BlockEnd : BlockBegin)}' (the maquettiste block needs exactly one '{BlockBegin}' line followed by one '{BlockEnd}' line); fix the markers and run init again. The file was not changed."));
            }

            var (begin, end) = (begins[0], ends[0]);
            lines.RemoveRange(begin, end - begin + 1);
            lines.InsertRange(begin, block);
        }
        else
        {
            if (lines.Count > 0 && lines[^1].Length > 0)
                lines.Add("");
            lines.AddRange(block);
        }

        return string.Join('\n', lines) + "\n";
    }

    private static List<int> Indexes(List<string> lines, string marker) =>
        [.. lines.Select((l, i) => (l, i)).Where(x => x.l.Trim() == marker).Select(x => x.i)];

    /// <summary>The body of the post-checkout and post-merge hooks: regenerate built roots, never fail the git command.</summary>
    /// <param name="hook">The hook name.</param>
    /// <returns>The script.</returns>
    public static string HookScript(string hook) =>
        "#!/bin/sh\n" + HookMarker + "\n"
        + "# Regenerates the built (gitignored) output roots after a " + (hook == "post-merge" ? "merge" : "branch checkout") + ", so IDEs see generated code before the first build.\n"
        + (hook == "post-checkout" ? "[ \"$3\" = \"0\" ] && exit 0\n" : "")
        + "if command -v maquettiste >/dev/null 2>&1; then\n"
        + "  maquettiste generate --roots built --quiet || true\n"
        + "elif command -v dotnet >/dev/null 2>&1; then\n"
        + "  dotnet tool run maquettiste generate --roots built --quiet || true\n"
        + "fi\n"
        + "exit 0\n";

    private static async Task InstallHooksAsync(GuardedFiles files, string repo, List<string> report, CancellationToken ct)
    {
        var git = Path.Combine(repo, ".git");
        if (!Directory.Exists(git))
        {
            report.Add("skipped --hooks: " + (File.Exists(git) ? ".git is a file (a worktree or submodule); install the hooks in the main repository" : "no .git folder (not a git repository)"));
            return;
        }

        foreach (var hook in new[] { "post-checkout", "post-merge" })
        {
            var path = Path.Combine(git, "hooks", hook);
            var script = HookScript(hook);
            var mine = File.Exists(path) && (await File.ReadAllTextAsync(path, ct).ConfigureAwait(false)).Contains(HookMarker, StringComparison.Ordinal);
            var outcome = await files.WriteAsync(WriteTarget.Setup, path, new UTF8Encoding(false).GetBytes(script), overwrite: mine, ct).ConfigureAwait(false);
            if (outcome == WriteOutcome.Kept)
            {
                report.Add($"kept .git/hooks/{hook}: an existing hook that maquettiste did not install; add 'maquettiste generate --roots built' to it");
                continue;
            }

            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead
                    | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            }

            report.Add(Describe(outcome, ".git/hooks/" + hook));
        }
    }

    /// <summary>
    /// On a re-run with another starter pack, adds <c>packs.&lt;pack&gt;.output</c> to the kept settings file when it has no entry for the
    /// pack, so the next <c>generate</c> writes under an allowed root. A file that cannot be read is left alone with a hint.
    /// </summary>
    private static async Task<WriteOutcome> AddPackOutputAsync(GuardedFiles files, CanonicalJson json, string settingsPath, string pack,
        List<string> report, CancellationToken ct)
    {
        var hint = $"set packs.{pack}.output in .maquettiste/maquettiste.json (for example \"{StarterPacks.OutputFolder(pack)}\") before running generate";
        JsonObject? settings;
        try
        {
            settings = JsonNode.Parse(await File.ReadAllBytesAsync(settingsPath, ct).ConfigureAwait(false)) as JsonObject;
        }
        catch (JsonException)
        {
            settings = null;
        }

        if (settings is null || (settings.TryGetPropertyValue("packs", out var packsNode) && packsNode is not null and not JsonObject))
        {
            report.Add(hint);
            return WriteOutcome.Kept;
        }

        var packs = settings["packs"] as JsonObject;
        if (packs is not null && packs.ContainsKey(pack))
            return WriteOutcome.Kept;
        if (packs is null)
            settings["packs"] = packs = new JsonObject();
        packs[pack] = new JsonObject { ["output"] = StarterPacks.OutputFolder(pack) };
        byte[] bytes;
        try
        {
            bytes = json.Write(settings, "maquettiste.json", "maquettiste.json");
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or ArgumentException)
        {
            report.Add(hint);
            return WriteOutcome.Kept;
        }

        var outcome = await files.WriteAsync(WriteTarget.Model, settingsPath, bytes, overwrite: true, ct).ConfigureAwait(false);
        report.Add($"added packs.{pack}.output \"{StarterPacks.OutputFolder(pack)}\" to .maquettiste/maquettiste.json");
        return outcome;
    }

    /// <summary>
    /// The project name of a new model: <c>--name</c>; else the <c>name</c> of <c>package.json</c> (without its <c>@scope/</c>); else the
    /// repository name of the git remote (<c>origin</c>, else the first); else the folder name; else <c>model</c>.
    /// </summary>
    /// <param name="repo">The repo root.</param>
    /// <param name="given">The <c>--name</c> value.</param>
    /// <returns>The name.</returns>
    internal static string ProjectName(string repo, string? given)
    {
        if (given?.Trim() is { Length: > 0 } name)
            return name;
        if (PackageName(Path.Combine(repo, "package.json")) is { } package)
            return package;
        if (GitRemoteName(repo) is { } remote)
            return remote;
        return Path.GetFileName(repo) is { Length: > 0 } folder ? folder : "model";
    }

    private static string? PackageName(string path)
    {
        try
        {
            if (!File.Exists(path) || JsonNode.Parse(File.ReadAllText(path)) is not JsonObject package || package["name"] is not JsonValue value
                || !value.TryGetValue<string>(out var name))
                return null;
            name = name.Trim();
            var slash = name.LastIndexOf('/');
            name = name.StartsWith('@') && slash > 0 ? name[(slash + 1)..] : name;
            return name.Length > 0 ? name : null;
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return null;
        }
    }

    private static string? GitRemoteName(string repo)
    {
        try
        {
            var git = Path.Combine(repo, ".git");
            if (File.Exists(git))
            {
                // A worktree or submodule: ".git" names the git folder; a worktree shares the config of its common folder.
                var pointer = File.ReadAllText(git).Trim();
                if (!pointer.StartsWith("gitdir:", StringComparison.Ordinal))
                    return null;
                git = Path.GetFullPath(pointer[7..].Trim(), repo);
                var common = Path.Combine(git, "commondir");
                if (File.Exists(common))
                    git = Path.GetFullPath(File.ReadAllText(common).Trim(), git);
            }

            var config = Path.Combine(git, "config");
            if (!File.Exists(config))
                return null;
            string? section = null, first = null, origin = null;
            foreach (var raw in File.ReadLines(config))
            {
                var line = raw.Trim();
                if (line.StartsWith('['))
                {
                    section = line;
                    continue;
                }

                if (section is null || !section.StartsWith("[remote ", StringComparison.Ordinal) || !line.StartsWith("url", StringComparison.Ordinal))
                    continue;
                var equals = line.IndexOf('=', StringComparison.Ordinal);
                if (equals < 0 || line[..equals].Trim() != "url")
                    continue;
                var url = line[(equals + 1)..].Trim();
                first ??= url;
                if (section == "[remote \"origin\"]")
                    origin ??= url;
            }

            return RepositoryName(origin ?? first);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>The repository name of a remote URL: its last path segment without <c>.git</c>.</summary>
    /// <param name="url">The URL (<c>https://host/owner/name.git</c>, <c>git@host:owner/name.git</c>, a path).</param>
    /// <returns>The name, or <see langword="null"/>.</returns>
    internal static string? RepositoryName(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;
        var trimmed = url.Trim().TrimEnd('/', '\\');
        var cut = trimmed.LastIndexOfAny(['/', '\\', ':']);
        var name = cut >= 0 ? trimmed[(cut + 1)..] : trimmed;
        if (name.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            name = name[..^4];
        return name.Length > 0 ? name : null;
    }

    private static byte[] Settings(CanonicalJson json, string name, string pack)
    {
        var settings = new JsonObject
        {
            ["formatVersion"] = EngineVersion.FormatVersion,
            ["name"] = name,
            ["outputs"] = new JsonObject
            {
                ["allow"] = new JsonArray(
                    new JsonObject { ["path"] = "db", ["commit"] = true },
                    new JsonObject { ["path"] = "src/Generated" }),
            },
        };
        if (pack != "none")
            settings["packs"] = new JsonObject { [pack] = new JsonObject { ["output"] = StarterPacks.OutputFolder(pack) } };
        return json.Write(settings, "maquettiste.json", "maquettiste.json");
    }

    /// <summary>The built roots (<c>commit</c> false) of the settings file; the default built root when the file cannot be read.</summary>
    /// <param name="settingsPath">The settings file.</param>
    /// <returns>The repo-relative roots.</returns>
    private static IReadOnlyList<string> BuiltRoots(string settingsPath)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(settingsPath));
            if (document.RootElement.TryGetProperty("outputs", out var outputs) && outputs.TryGetProperty("allow", out var allow) && allow.ValueKind == JsonValueKind.Array)
            {
                return [.. allow.EnumerateArray()
                    .Where(r => r.ValueKind == JsonValueKind.Object && r.TryGetProperty("path", out var p) && p.ValueKind == JsonValueKind.String
                        && !(r.TryGetProperty("commit", out var c) && c.ValueKind == JsonValueKind.True))
                    .Select(r => r.GetProperty("path").GetString()!)
                    .Where(p => p.Trim('/').Length > 0 && p.Trim('/') != ".")];
            }

            return [];
        }
        catch (Exception e) when (e is JsonException or IOException)
        {
            return ["src/Generated"];
        }
    }

    private static string Describe(WriteOutcome outcome, string path) => outcome switch
    {
        WriteOutcome.Created => "created " + path,
        WriteOutcome.Updated => "updated " + path,
        _ => "kept " + path,
    };
}
