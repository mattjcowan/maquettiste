using System.Diagnostics;
using System.Text;

namespace Maquettiste.Packs.Tests;

/// <summary>Runs an external command with a timeout and captures its output.</summary>
internal static class ProcessRunner
{
    /// <summary>The result of a command.</summary>
    /// <param name="ExitCode">The exit code, or -1 on timeout.</param>
    /// <param name="Output">Standard output and standard error, interleaved by line.</param>
    public sealed record Result(int ExitCode, string Output);

    /// <summary>Runs a command.</summary>
    /// <param name="fileName">The program.</param>
    /// <param name="arguments">Its arguments.</param>
    /// <param name="workingDirectory">The working directory.</param>
    /// <param name="timeout">How long to wait.</param>
    /// <param name="standardInput">Text written to standard input, if any.</param>
    /// <param name="environment">Environment variables to set for the command, if any.</param>
    /// <returns>The result.</returns>
    public static async Task<Result> RunAsync(string fileName, IEnumerable<string> arguments, string workingDirectory, TimeSpan timeout, string? standardInput = null,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        var start = new ProcessStartInfo(fileName)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = standardInput is not null,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        start.Environment["DOTNET_NOLOGO"] = "1";
        start.Environment["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1";
        start.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        foreach (var (name, value) in environment ?? new Dictionary<string, string>())
            start.Environment[name] = value;

        var output = new StringBuilder();
        using var process = new Process { StartInfo = start };
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (output) output.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (output) output.AppendLine(e.Data); };
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        if (standardInput is not null)
        {
            await process.StandardInput.WriteAsync(standardInput);
            process.StandardInput.Close();
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            lock (output)
                return new Result(-1, output + $"\n(timed out after {timeout})");
        }

        process.WaitForExit();
        lock (output)
            return new Result(process.ExitCode, output.ToString());
    }

    /// <summary>Finds a program on the PATH.</summary>
    /// <param name="name">The program name without extension.</param>
    /// <returns>Its full path, or null.</returns>
    public static string? FindOnPath(string name)
    {
        var extensions = OperatingSystem.IsWindows() ? new[] { ".exe", ".cmd" } : new[] { "" };
        foreach (var folder in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var extension in extensions)
            {
                var candidate = Path.Combine(folder, name + extension);
                if (File.Exists(candidate))
                    return candidate;
            }
        }

        return null;
    }

    /// <summary>The dotnet host running the tests (so the same SDK builds the temporary projects).</summary>
    public static string Dotnet =>
        Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host && File.Exists(host) ? host : FindOnPath("dotnet") ?? "dotnet";
}
