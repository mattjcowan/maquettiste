using System.ComponentModel;
using System.Diagnostics;

namespace Maquettiste.Functions;

/// <summary>Read-only git status of the model folder for the top bar.</summary>
/// <param name="Branch">The branch, or <see langword="null"/> on a detached head.</param>
/// <param name="Head">The short commit id, or <see langword="null"/> before the first commit.</param>
/// <param name="ChangedModelFiles">Files under <c>.maquettiste/</c> that <c>git status --porcelain</c> lists.</param>
public sealed record GitSummary(string? Branch, string? Head, int ChangedModelFiles);

/// <summary>Reads <see cref="GitSummary"/> with the <c>git</c> command line (2 s timeout per call); never writes.</summary>
/// <param name="settings">The editor settings (the repository root).</param>
public sealed class GitStatusReader(EditorSettings settings)
{
    /// <summary>How long one git call may take.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Runs <c>git -C &lt;repo&gt; status --porcelain=v1 --branch -- .maquettiste</c> and <c>git rev-parse --short HEAD</c>.
    /// </summary>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The summary, or <see langword="null"/> when the repository is not a git checkout or git is unavailable.</returns>
    public async Task<GitSummary?> ReadAsync(CancellationToken ct)
    {
        var repo = settings.Engine.RepoRoot;
        var status = await RunAsync(repo, ["status", "--porcelain=v1", "--branch", "--", ".maquettiste"], ct).ConfigureAwait(false);
        if (status is null)
            return null;
        var head = await RunAsync(repo, ["rev-parse", "--short", "HEAD"], ct).ConfigureAwait(false);
        return Parse(status, head);
    }

    /// <summary>Parses <c>git status --porcelain=v1 --branch</c> output.</summary>
    /// <param name="status">The status output.</param>
    /// <param name="head">The <c>rev-parse</c> output, or <see langword="null"/>.</param>
    /// <returns>The summary.</returns>
    public static GitSummary Parse(string status, string? head)
    {
        ArgumentNullException.ThrowIfNull(status);
        string? branch = null;
        var changed = 0;
        foreach (var line in status.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!line.StartsWith("## ", StringComparison.Ordinal))
            {
                changed++;
                continue;
            }

            var text = line[3..].Trim();
            if (text.StartsWith("No commits yet on ", StringComparison.Ordinal))
                text = text["No commits yet on ".Length..];
            else if (text.StartsWith("Initial commit on ", StringComparison.Ordinal))
                text = text["Initial commit on ".Length..];
            if (text.StartsWith("HEAD (no branch)", StringComparison.Ordinal))
                continue;
            var end = text.IndexOf("...", StringComparison.Ordinal);
            if (end < 0)
                end = text.IndexOf(' ', StringComparison.Ordinal);
            branch = end < 0 ? text : text[..end];
        }

        var shortHead = head?.Trim();
        return new GitSummary(branch, string.IsNullOrEmpty(shortHead) ? null : shortHead, changed);
    }

    private static async Task<string?> RunAsync(string repo, string[] arguments, CancellationToken ct)
    {
        var start = new ProcessStartInfo("git")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add("-C");
        start.ArgumentList.Add(repo);
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        start.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        start.Environment["LC_ALL"] = "C";

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        Process process;
        try
        {
            process = Process.Start(start) ?? throw new InvalidOperationException("git did not start.");
        }
        catch (Win32Exception)
        {
            return null;
        }

        using (process)
        {
            try
            {
                var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
                var error = process.StandardError.ReadToEndAsync(timeout.Token);
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                var text = await output.ConfigureAwait(false);
                await error.ConfigureAwait(false);
                return process.ExitCode == 0 ? text : null;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                }

                return null;
            }
        }
    }
}
