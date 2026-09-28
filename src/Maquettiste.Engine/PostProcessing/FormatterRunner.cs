using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Pipeline;

namespace Maquettiste.Engine.PostProcessing;

/// <summary>
/// Runs formatters as external processes (W8; SPEC section 12 "Formatters", engine-design.md section 13). A formatter reads the
/// file on stdin and answers on stdout, so nothing touches disk and <c>--check</c> formats in memory. The working directory is the
/// repo root, arguments go through <see cref="ProcessStartInfo.ArgumentList"/> (no shell), <c>{path}</c> in an argument is replaced
/// by the repo-relative path, and a non-zero exit, a timeout or a process that cannot start is MQ6008.
/// </summary>
/// <param name="options">The engine options.</param>
internal sealed class FormatterRunner(EngineOptions options) : IFormatterRunner
{
    /// <summary>The longest stderr or stdout excerpt quoted in a diagnostic.</summary>
    private const int MaxExcerpt = 400;

    /// <inheritdoc/>
    public async Task<IReadOnlyList<Diagnostic>> VerifyVersionsAsync(IReadOnlyList<FormatterSettings> formatters, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(formatters);
        var diagnostics = new List<Diagnostic>();
        for (var i = 0; i < formatters.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var diagnostic = await VerifyAsync(formatters[i], i, ct).ConfigureAwait(false);
            if (diagnostic is not null)
                diagnostics.Add(diagnostic);
        }

        return diagnostics;
    }

    /// <summary>Checks one formatter's version.</summary>
    /// <param name="formatter">The formatter.</param>
    /// <param name="index">Its index in <c>formatters</c>, for the JSON pointer; -1 when unknown.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>MQ6008, or <see langword="null"/> when the output contains the pinned version.</returns>
    internal async Task<Diagnostic?> VerifyAsync(FormatterSettings formatter, int index, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(formatter);
        var run = await RunAsync(formatter.Command, formatter.VersionArgs, ReadOnlyMemory<byte>.Empty, formatter.TimeoutSeconds, ct).ConfigureAwait(false);
        var pointer = index >= 0 ? "/formatters/" + index.ToString(CultureInfo.InvariantCulture) : null;
        if (run.StartError is not null)
            return SettingsError($"Formatter '{formatter.Name}' could not be started ('{formatter.Command}'): {run.StartError}", pointer);
        if (run.TimedOut)
            return SettingsError($"Formatter '{formatter.Name}' did not report its version within {Seconds(formatter.TimeoutSeconds)} s.", pointer);
        var output = Decode(run.Stdout) + "\n" + Decode(run.Stderr);
        if (output.Contains(formatter.Version, StringComparison.Ordinal))
            return null;
        var exit = run.ExitCode == 0 ? "" : $" (exit code {run.ExitCode.ToString(CultureInfo.InvariantCulture)})";
        return SettingsError(
            $"Formatter '{formatter.Name}' version does not match: maquettiste.json pins '{formatter.Version}', " +
            $"'{formatter.Command} {string.Join(' ', formatter.VersionArgs)}' printed '{Excerpt(output.Trim())}'{exit}.",
            pointer);
    }

    /// <inheritdoc/>
    public async Task<FormatResult> FormatAsync(FormatterSettings formatter, string path, ReadOnlyMemory<byte> input, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(formatter);
        ArgumentNullException.ThrowIfNull(path);
        var args = formatter.Args.Select(a => a.Replace("{path}", path, StringComparison.Ordinal)).ToList();
        var run = await RunAsync(formatter.Command, args, input, formatter.TimeoutSeconds, ct).ConfigureAwait(false);
        if (run.StartError is not null)
            return Failed($"Formatter '{formatter.Name}' could not be started ('{formatter.Command}'): {run.StartError}", path);
        if (run.TimedOut)
            return Failed($"Formatter '{formatter.Name}' timed out after {Seconds(formatter.TimeoutSeconds)} s.", path);
        if (run.ExitCode != 0)
        {
            var stderr = Excerpt(Decode(run.Stderr).Trim());
            return Failed(
                $"Formatter '{formatter.Name}' exited with code {run.ExitCode.ToString(CultureInfo.InvariantCulture)}" +
                (stderr.Length > 0 ? ": " + stderr : "."),
                path);
        }

        return new FormatResult(true, run.Stdout, null);
    }

    private static FormatResult Failed(string message, string path) =>
        new(false, ReadOnlyMemory<byte>.Empty, new Diagnostic("MQ6008", DiagnosticSeverity.Error, message, null, path, null, null, null));

    private Diagnostic SettingsError(string message, string? pointer) =>
        new("MQ6008", DiagnosticSeverity.Error, message, null, SettingsPath(), pointer, null, null);

    /// <summary>The repo-relative path of <c>maquettiste.json</c>.</summary>
    private string SettingsPath()
    {
        var relative = Path.GetRelativePath(options.RepoRoot, Path.Combine(options.EffectiveModelRoot, "maquettiste.json"));
        return relative.Replace('\\', '/');
    }

    /// <summary>Runs a process with the given stdin, capturing stdout and stderr as bytes.</summary>
    private async Task<ProcessRun> RunAsync(string command, IReadOnlyList<string> args, ReadOnlyMemory<byte> input, int timeoutSeconds, CancellationToken ct)
    {
        var info = new ProcessStartInfo(command)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = options.RepoRoot,
        };
        foreach (var arg in args)
            info.ArgumentList.Add(arg);

        using var process = new Process { StartInfo = info };
        try
        {
            if (!process.Start())
                return ProcessRun.NotStarted("the process did not start");
        }
        catch (Win32Exception ex)
        {
            return ProcessRun.NotStarted(ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return ProcessRun.NotStarted(ex.Message);
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(1, timeoutSeconds)));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        var stdout = new MemoryStream();
        var stderr = new MemoryStream();
        var readOut = process.StandardOutput.BaseStream.CopyToAsync(stdout, linked.Token);
        var readErr = process.StandardError.BaseStream.CopyToAsync(stderr, linked.Token);
        var write = WriteInputAsync(process.StandardInput.BaseStream, input, linked.Token);
        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
            await Task.WhenAll(readOut, readErr, write).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            await Settle(readOut, readErr, write).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            return new ProcessRun(null, true, -1, ReadOnlyMemory<byte>.Empty, ReadOnlyMemory<byte>.Empty);
        }

        return new ProcessRun(null, false, process.ExitCode, stdout.ToArray(), stderr.ToArray());
    }

    /// <summary>Writes stdin and closes it; a formatter that exits without reading all of it is not an error here.</summary>
    private static async Task WriteInputAsync(Stream stdin, ReadOnlyMemory<byte> input, CancellationToken ct)
    {
        try
        {
            if (!input.IsEmpty)
                await stdin.WriteAsync(input, ct).ConfigureAwait(false);
            await stdin.FlushAsync(ct).ConfigureAwait(false);
        }
        catch (IOException)
        {
            // The process closed its stdin early (broken pipe); its exit code decides the outcome.
        }
        finally
        {
            try
            {
                await stdin.DisposeAsync().ConfigureAwait(false);
            }
            catch (IOException)
            {
            }
        }
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // Already exited.
        }
        catch (Win32Exception)
        {
            // Could not be killed; the pipes close when it ends.
        }
    }

    private static async Task Settle(params Task[] tasks)
    {
        foreach (var task in tasks)
        {
            try
            {
                // A grandchild that keeps a pipe open must not hold the run: give each stream a moment, then move on.
                await task.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
            }
            catch (OperationCanceledException)
            {
            }
            catch (IOException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    private static string Decode(ReadOnlyMemory<byte> bytes) => Encoding.UTF8.GetString(bytes.Span);

    private static string Excerpt(string text)
    {
        text = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        return text.Length <= MaxExcerpt ? text : text[..MaxExcerpt] + "…";
    }

    private static string Seconds(int seconds) => Math.Max(1, seconds).ToString(CultureInfo.InvariantCulture);

    /// <summary>The outcome of one process run.</summary>
    private sealed record ProcessRun(string? StartError, bool TimedOut, int ExitCode, ReadOnlyMemory<byte> Stdout, ReadOnlyMemory<byte> Stderr)
    {
        public static ProcessRun NotStarted(string error) => new(error, false, -1, ReadOnlyMemory<byte>.Empty, ReadOnlyMemory<byte>.Empty);
    }
}
