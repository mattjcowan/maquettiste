namespace Maquettiste.Cli;

/// <summary>
/// What a command sees of its process: the two output streams, the current directory and the environment. The console entry point
/// fills it from the process; tests fill it with string writers and a temporary folder, so commands run in-process without touching
/// the real console or environment.
/// </summary>
public sealed record CliEnvironment
{
    /// <summary>Results only: lists, diffs, JSON and SARIF, so output pipes cleanly.</summary>
    public required TextWriter Out { get; init; }

    /// <summary>Progress, summaries, diagnostics and errors. Must be safe to call from several threads.</summary>
    public required TextWriter Error { get; init; }

    /// <summary>The absolute current directory.</summary>
    public required string CurrentDirectory { get; init; }

    /// <summary>Reads an environment variable (<c>MAQUETTISTE_CACHE_DIR</c>, <c>XDG_CACHE_HOME</c>, <c>HOME</c>).</summary>
    public Func<string, string?> GetEnvironmentVariable { get; init; } = _ => null;

    /// <summary>
    /// Opens the raw standard input stream (<c>maquettiste mcp</c> reads JSON-RPC messages from it); <see langword="null"/> when the
    /// process has none.
    /// </summary>
    public Func<Stream>? OpenStandardInput { get; init; }

    /// <summary>
    /// Opens the raw standard output stream (<c>maquettiste mcp</c> writes JSON-RPC messages to it, and nothing else writes to stdout
    /// while it runs); <see langword="null"/> when the process has none.
    /// </summary>
    public Func<Stream>? OpenStandardOutput { get; init; }

    /// <summary>Whether <see cref="Error"/> is an interactive terminal (<c>--progress auto</c> then rewrites one line).</summary>
    public bool ErrorIsTerminal { get; init; }
}
