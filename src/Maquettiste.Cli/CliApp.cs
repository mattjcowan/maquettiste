using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Maquettiste.Cli.Commands;
using Maquettiste.Engine;

namespace Maquettiste.Cli;

/// <summary>How much the CLI writes to stderr.</summary>
internal enum Verbosity
{
    /// <summary>Errors and diagnostics only; no progress, no summary.</summary>
    Quiet,

    /// <summary>Progress and a summary.</summary>
    Normal,

    /// <summary>Also stage timings.</summary>
    Detailed,
}

/// <summary>The progress style (<c>--progress</c>).</summary>
internal enum ProgressStyle
{
    /// <summary>No progress.</summary>
    None,

    /// <summary>A line when each stage starts and ends.</summary>
    Plain,

    /// <summary>One rewritten line on a terminal.</summary>
    Terminal,

    /// <summary>One JSON line per update.</summary>
    Json,
}

/// <summary>The global options, resolved.</summary>
/// <param name="Environment">The process view.</param>
/// <param name="Line">The parsed command line.</param>
/// <param name="Verbosity">The verbosity.</param>
/// <param name="Progress">The progress style.</param>
/// <param name="Jobs">The <c>--jobs</c> value.</param>
internal sealed record GlobalContext(CliEnvironment Environment, CommandLine Line, Verbosity Verbosity, ProgressStyle Progress, int? Jobs)
{
    /// <summary>Results.</summary>
    public TextWriter Out => Environment.Out;

    /// <summary>Messages.</summary>
    public TextWriter Error => Environment.Error;

    /// <summary>The model folder name under the repo root.</summary>
    public const string ModelFolder = ".maquettiste";

    /// <summary>
    /// The repo root: <c>--repo</c> when given (relative to the current directory), else the nearest ancestor of the current directory
    /// holding <c>.maquettiste/maquettiste.json</c>, else the current directory.
    /// </summary>
    /// <param name="search">Whether to search ancestors (<c>init</c> does not).</param>
    /// <returns>The absolute repo root, without a trailing separator.</returns>
    public string RepoRoot(bool search = true)
    {
        if (Line.Value("--repo") is { } repo)
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(repo, Environment.CurrentDirectory));
        var current = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Environment.CurrentDirectory));
        if (search)
        {
            for (var dir = new DirectoryInfo(current); dir is not null; dir = dir.Parent)
            {
                if (File.Exists(Path.Combine(dir.FullName, ModelFolder, "maquettiste.json")))
                    return Path.TrimEndingDirectorySeparator(dir.FullName);
            }
        }

        return current;
    }

    /// <summary>
    /// The cache folder (D13): <c>--cache-dir</c>, else <c>$MAQUETTISTE_CACHE_DIR</c>, else the OS user cache folder
    /// <c>…/maquettiste/&lt;first 16 hex of SHA-256 of the repo path&gt;</c>.
    /// </summary>
    /// <param name="repoRoot">The repo root.</param>
    /// <returns>The absolute cache folder.</returns>
    public string CacheDirectory(string repoRoot)
    {
        if (Line.Value("--cache-dir") is { } dir)
            return Path.GetFullPath(dir, Environment.CurrentDirectory);
        if (Environment.GetEnvironmentVariable("MAQUETTISTE_CACHE_DIR") is { Length: > 0 } fromEnv)
            return Path.GetFullPath(fromEnv, Environment.CurrentDirectory);
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(repoRoot)))[..16];
        return Path.Combine(UserCacheRoot(), "maquettiste", hash);
    }

    /// <summary>Engine options for a repo.</summary>
    /// <param name="repoRoot">The repo root.</param>
    /// <returns>The options.</returns>
    public EngineOptions EngineOptions(string repoRoot) => new()
    {
        RepoRoot = repoRoot,
        CacheDirectory = CacheDirectory(repoRoot),
        MaxDegreeOfParallelism = Jobs ?? 0,
    };

    /// <summary>Writes a line to stderr unless quiet.</summary>
    /// <param name="message">The message.</param>
    public void Info(string message)
    {
        if (Verbosity != Verbosity.Quiet)
            Error.WriteLine(message);
    }

    private string UserCacheRoot()
    {
        if (OperatingSystem.IsWindows())
            return System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData, System.Environment.SpecialFolderOption.DoNotVerify);
        var home = Environment.GetEnvironmentVariable("HOME") is { Length: > 0 } h
            ? h
            : System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile, System.Environment.SpecialFolderOption.DoNotVerify);
        if (OperatingSystem.IsMacOS())
            return Path.Combine(home, "Library", "Caches");
        return Environment.GetEnvironmentVariable("XDG_CACHE_HOME") is { Length: > 0 } xdg && Path.IsPathFullyQualified(xdg) ? xdg : Path.Combine(home, ".cache");
    }
}

/// <summary>Parses the command line, resolves the global options and runs one command.</summary>
/// <param name="environment">The process view.</param>
public sealed class CliApp(CliEnvironment environment)
{
    private const string Usage = """
        Usage: maquettiste [global options] <command> [options]

        Commands:
          init                  Create .maquettiste/, the schema files, a starter pack and .gitignore entries
                                  --pack sql-ddl|csharp-dapper|none (default sql-ddl), --hooks,
                                  --mcp (.mcp.json), --skill (.claude/skills), --agent-setup (both),
                                  --docker <image> (with --mcp: ./mcp.sh runs the server in the image),
                                  --name <project name> (default: package.json name, git remote, folder)
          validate              Validate the model and packs
                                  --format text|json|sarif, --output <file>
          generate              Incremental generation
                                  --pack <name> (repeatable), --force, --roots all|committed|built,
                                  --hand-edits fail|overwrite|skip, --watch, --dry-run, --diff, --check,
                                  --format text|json, --no-wait
          migrate               Upgrade the model format (format 1 is current)
          format                Rewrite every model file in canonical form and report the count
                                  --check (write nothing; exit 2 when a file would change)
          pack new <name>       Scaffold a template pack under .maquettiste/templates/<name>/
                                  --from empty|sql-ddl|csharp-dapper
          bench                 Run the synthetic benchmark
                                  --out <dir>, --seed, --entities, --relations, --enums, --fanout, --keep,
                                  --baseline <file>, --max-regression <percent>, --format text|json, --no-example-packs
          l10n status           Default locale, declared locales, completeness per locale and shard (--format text|json)
          l10n export <locale>  Translations as XLIFF 2.1 or CSV: --format xliff|csv, --out <file>
          l10n import <locale> <file>
                                Preview an XLIFF or CSV import (added, changed, stale confirmed); --apply writes it,
                                  --check exits 2 when it would change something, --format text|json
          l10n prune            List orphan translations (MQ7203); --apply removes them
          l10n set-default <locale>
                                Preview making a locale the default (texts swap between files and shards); --apply
          seed export <seed>    A seed's rows as CSV (seed id or name, or the id or name of the element it seeds)
                                  --locale <tag> (repeatable: its label and description columns), --out <file>
          seed import <seed> <file>
                                Preview a CSV import: --mode merge|replace; --apply writes it, --check, --format text|json
          mcp                   Serve the model to agents over the Model Context Protocol (stdio; see docs/mcp.md)

        Global options:
          --repo <dir>          The repo root (default: nearest ancestor holding .maquettiste/maquettiste.json, else the current directory)
          --cache-dir <dir>     The index and plan cache (default: $MAQUETTISTE_CACHE_DIR, else the user cache folder)
          --jobs <n>            Parallelism (default: processor count)
          --progress auto|plain|json|none
          --verbosity quiet|normal|detailed, --quiet (-q)
          --no-color, --version, --help (-h)

        Exit codes: 0 success, 1 validation or read errors, 2 drift (or a --check preview that would change something), 3 hand-edit conflicts (or a file changed while a write ran), 4 internal or usage error (a refused write too).
        """;

    private readonly CliEnvironment _environment = environment ?? throw new ArgumentNullException(nameof(environment));

    /// <summary>Runs one command.</summary>
    /// <param name="args">The arguments.</param>
    /// <param name="ct">Cancellation (Ctrl+C); a cancelled command exits 4, except <c>generate --watch</c>, which stops with 0.</param>
    /// <returns>The exit code.</returns>
    public async Task<int> RunAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(args);
        try
        {
            var line = CommandLine.Parse(args);
            if (line.Has("--version"))
            {
                if (line.Positionals.Count > 0)
                    throw new UsageException("--version takes no command.");
                await _environment.Out.WriteLineAsync(VersionLine()).ConfigureAwait(false);
                return Program.ExitCodes.Success;
            }

            if (line.Has("--help") || line.Positionals.Count == 0)
            {
                await (line.Has("--help") ? _environment.Out : _environment.Error).WriteLineAsync(Usage.Replace("\r\n", "\n", StringComparison.Ordinal)).ConfigureAwait(false);
                return line.Has("--help") ? Program.ExitCodes.Success : Program.ExitCodes.Internal;
            }

            var context = Resolve(line);
            var command = line.Positionals[0];
            return command switch
            {
                "init" => await InitCommand.RunAsync(context, ct).ConfigureAwait(false),
                "validate" => await ValidateCommand.RunAsync(context, ct).ConfigureAwait(false),
                "generate" => await GenerateCommand.RunAsync(context, ct).ConfigureAwait(false),
                "migrate" => await MigrateCommand.RunAsync(context, ct).ConfigureAwait(false),
                "format" => await FormatCommand.RunAsync(context, ct).ConfigureAwait(false),
                "pack" => await PackNewCommand.RunAsync(context, ct).ConfigureAwait(false),
                "bench" => await BenchCommand.RunAsync(context, ct).ConfigureAwait(false),
                "l10n" => await L10nCommand.RunAsync(context, ct).ConfigureAwait(false),
                "seed" => await SeedCommand.RunAsync(context, ct).ConfigureAwait(false),
                "mcp" => await McpCommand.RunAsync(context, ct).ConfigureAwait(false),
                _ => throw new UsageException($"Unknown command '{command}'. Run 'maquettiste --help'."),
            };
        }
        catch (UsageException e)
        {
            await _environment.Error.WriteLineAsync("maquettiste: " + e.Message).ConfigureAwait(false);
            return Program.ExitCodes.Internal;
        }
        catch (RefusedWriteException e)
        {
            await _environment.Error.WriteLineAsync("maquettiste: " + e.Message).ConfigureAwait(false);
            return Program.ExitCodes.Internal;
        }
        catch (Exception e) when (PermissionError.Find(e) is { } denied)
        {
            await _environment.Error.WriteLineAsync(PermissionError.Message(denied, OperatingSystem.IsLinux())).ConfigureAwait(false);
            return Program.ExitCodes.Invalid;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await _environment.Error.WriteLineAsync("maquettiste: cancelled.").ConfigureAwait(false);
            return Program.ExitCodes.Internal;
        }
#pragma warning disable CA1031 // The CLI turns any unexpected failure into exit code 4 with a message.
        catch (Exception e)
#pragma warning restore CA1031
        {
            await _environment.Error.WriteLineAsync("maquettiste: internal error: " + e.Message).ConfigureAwait(false);
            await _environment.Error.WriteLineAsync(e.ToString()).ConfigureAwait(false);
            return Program.ExitCodes.Internal;
        }
    }

    private GlobalContext Resolve(CommandLine line)
    {
        var verbosity = line.Has("--quiet")
            ? Verbosity.Quiet
            : line.Choice("--verbosity", "normal", "quiet", "normal", "detailed") switch
            {
                "quiet" => Verbosity.Quiet,
                "detailed" => Verbosity.Detailed,
                _ => Verbosity.Normal,
            };
        if (line.Has("--quiet") && line.Value("--verbosity") is { } v && v != "quiet")
            throw new UsageException("--quiet contradicts --verbosity " + v + ".");
        var progressOption = line.Choice("--progress", "auto", "auto", "plain", "json", "none");
        var progress = progressOption switch
        {
            "plain" => ProgressStyle.Plain,
            "json" => ProgressStyle.Json,
            "none" => ProgressStyle.None,
            _ when verbosity == Verbosity.Quiet => ProgressStyle.None,
            _ => _environment.ErrorIsTerminal ? ProgressStyle.Terminal : ProgressStyle.Plain,
        };
        var jobs = line.Int("--jobs", 1);
        return new GlobalContext(_environment, line, verbosity, progress, jobs);
    }

    /// <summary>
    /// The product version (the package version the build was given, without its build metadata) with the engine's contract version
    /// and the model format beside it: "maquettiste 0.1.0 (engine contract 1.0.0, model format 1)".
    /// </summary>
    /// <returns>The line <c>--version</c> prints.</returns>
    public static string VersionLine()
    {
        var informational = typeof(CliApp).Assembly.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var product = string.IsNullOrEmpty(informational) ? typeof(CliApp).Assembly.GetName().Version?.ToString(3) ?? "0.0.0" : informational.Split('+')[0];
        return $"maquettiste {product} (engine contract {EngineVersion.Value}, model format {EngineVersion.FormatVersion})";
    }
}
