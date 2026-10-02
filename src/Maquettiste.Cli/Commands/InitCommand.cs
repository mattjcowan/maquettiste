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
/// [--docker &lt;image&gt; [--runtime docker|podman]]</c> (engine-design.md section 16; SPEC sections 4, 11, 12 and 17).
/// <para>
/// Creates <c>.maquettiste/</c> with the phase 1 folders, <c>maquettiste.json</c> (format 1, output roots <c>db</c> and
/// <c>src/Generated</c>), the JSON schemas in <c>.schema/v1/</c> and the starter pack. The project is named by <c>--name</c>, else
/// as <see cref="ProjectName"/> derives it. The repository's <c>.gitignore</c> belongs to the customer: <c>init</c> never reads or writes
/// it (the write guard refuses it); a pack unit in <c>block</c> mode can manage lines in it through generation (spec-errata E42). The
/// engine's <c>.maquettiste/.cache/</c> ignores itself (Engine <c>Writing/CacheFolder</c>).
/// </para>
/// <para>
/// Idempotent: existing files are kept, except <c>.schema/v1</c>, which is refreshed; on a project that already has its settings, a starter
/// pack is scaffolded only when <c>--pack</c> names it, so a re-run of plain <c>init</c> never puts back a pack the team removed.
/// <c>--gitignore</c> was removed in 0.5.5 and is refused with that hint. <c>--hooks</c> installs the post-checkout and post-merge hooks; <c>--mcp</c> registers <c>maquettiste mcp</c> in
/// <c>.mcp.json</c>; <c>--skill</c> installs the modeling skill under <c>.claude/skills/</c>; <c>--agent-setup</c> does both; and
/// <c>--docker &lt;image&gt;</c> registers the server as a <c>docker run</c> of that image (<c>podman run</c> with <c>--runtime podman</c>)
/// and removes the <c>mcp.sh</c> wrapper earlier versions wrote (<see cref="AgentSetup"/>).
/// </para>
/// </summary>
internal static class InitCommand
{
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
        context.Line.Expect("init", 1, "--pack", "--hooks", "--gitignore", "--mcp", "--skill", "--agent-setup", "--name", "--docker", "--runtime");
        if (context.Line.Has("--gitignore"))
        {
            throw new UsageException("--gitignore was removed in 0.5.5: init no longer touches .gitignore. To keep generated folders out of git, "
                + "add a pack unit in block mode that writes the lines (see 'Managed blocks' in the user guide), or edit .gitignore yourself.");
        }

        if (context.Line.Value("--name") is { } given && string.IsNullOrWhiteSpace(given))
            throw new UsageException("--name needs a project name.");
        var dockerImage = context.Line.Value("--docker");
        if (dockerImage is not null && !AgentSetup.IsImageReference(dockerImage))
            throw new UsageException("--docker needs an image reference such as mattjcowan/maquettiste:0.2.0 (letters, digits and . _ - / : @).");
        if (dockerImage is null && context.Line.Value("--runtime") is not null)
            throw new UsageException("--runtime picks the container command for --docker <image>; give the image too.");
        var runtime = context.Line.Choice("--runtime", AgentSetup.Runtimes[0], [.. AgentSetup.Runtimes]);
        // The starter pack is scaffolded for a new project only. On a project that already has its settings, a pack is
        // added only when --pack names it: a re-run of plain init must not put back a pack the team removed.
        var packAsked = context.Line.Has("--pack");
        var pack = context.Line.Choice("--pack", "sql-ddl", "sql-ddl", "csharp-dapper", "none");
        var repo = context.RepoRoot(search: false);
        if (!Directory.Exists(repo))
            throw new UsageException($"The repo folder {repo} does not exist.");

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
        if (settingsOutcome == WriteOutcome.Kept && !packAsked)
            pack = "none";
        if (settingsOutcome == WriteOutcome.Kept && pack != "none")
            settingsOutcome = await AddPackOutputAsync(files, json, settingsPath, pack, report, ct).ConfigureAwait(false);
        report.Add(Describe(settingsOutcome, ".maquettiste/maquettiste.json"));

        // Schemas: refreshed on every run; files the engine no longer ships are removed (SchemaFolder, shared with the editor's start).
        var schemaStatus = await SchemaFolder.RefreshAsync(options, ct).ConfigureAwait(false);
        report.Add($"{(schemaStatus.IsCurrent ? "kept" : "wrote")} .maquettiste/.schema/v1/ ({schemas.FileNames.Count} schemas{(schemaStatus.IsCurrent ? ", current" : "")})");

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

        if (context.Line.Has("--hooks"))
            await InstallHooksAsync(files, repo, report, ct).ConfigureAwait(false);
        if (context.Line.Has("--mcp") || context.Line.Has("--agent-setup") || dockerImage is not null)
            await AgentSetup.WriteMcpConfigAsync(files, repo, dockerImage, runtime, report, ct).ConfigureAwait(false);
        if (context.Line.Has("--skill") || context.Line.Has("--agent-setup"))
            await AgentSetup.WriteSkillAsync(files, repo, report, ct).ConfigureAwait(false);

        foreach (var line in report)
            context.Info(line);
        context.Info($"Initialized maquettiste in {repo}.");
        context.Info(FormattersNote);
        return Program.ExitCodes.Success;
    }

    /// <summary>
    /// The line <c>init</c> ends with: the model folder holds canonical JSON and sandboxed JavaScript, so a repository's formatter or
    /// linter should leave it alone (docs/user-guide.md, "Formatters and linters"). <c>init</c> writes no ignore file.
    /// </summary>
    internal const string FormattersNote = "Keep formatters and linters off .maquettiste/ (see the guide).";

    /// <summary>The body of the post-checkout and post-merge hooks: regenerate, never fail the git command.</summary>
    /// <param name="hook">The hook name.</param>
    /// <returns>The script.</returns>
    public static string HookScript(string hook) =>
        "#!/bin/sh\n" + HookMarker + "\n"
        + "# Regenerates the output after a " + (hook == "post-merge" ? "merge" : "branch checkout") + ", so IDEs see generated code before the first build.\n"
        + (hook == "post-checkout" ? "[ \"$3\" = \"0\" ] && exit 0\n" : "")
        + "if command -v maquettiste >/dev/null 2>&1; then\n"
        + "  maquettiste generate --quiet || true\n"
        + "elif command -v dotnet >/dev/null 2>&1; then\n"
        + "  dotnet tool run maquettiste generate --quiet || true\n"
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
                report.Add($"kept .git/hooks/{hook}: an existing hook that maquettiste did not install; add 'maquettiste generate' to it");
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
                    new JsonObject { ["path"] = "db" },
                    new JsonObject { ["path"] = "src/Generated" }),
            },
        };
        if (pack != "none")
            settings["packs"] = new JsonObject { [pack] = new JsonObject { ["output"] = StarterPacks.OutputFolder(pack) } };
        // The sql-ddl starter realizes lookup-table, check and native, so the project declares them (reference-types-seeds-localization.md
        // section 1.4); without the declaration the New reference type dialog offers only Template-defined.
        if (pack == "sql-ddl")
            settings["referenceData"] = new JsonObject { ["strategies"] = StarterPacks.StandardReferenceStrategies() };
        return json.Write(settings, "maquettiste.json", "maquettiste.json");
    }

    private static string Describe(WriteOutcome outcome, string path) => outcome switch
    {
        WriteOutcome.Created => "created " + path,
        WriteOutcome.Updated => "updated " + path,
        _ => "kept " + path,
    };
}
