using System.Globalization;
using System.Text.Json;
using Maquettiste.Engine;

namespace Maquettiste.Cli.Commands;

/// <summary>
/// <c>maquettiste migrate</c>: a phase 1 stub (engine-design.md section 16). Format 1 prints "Model format 1 is current." and exits 0;
/// a newer format exits 4 (this tool is too old); a missing or unreadable <c>maquettiste.json</c> or format version exits 1.
/// </summary>
internal static class MigrateCommand
{
    /// <summary>Runs the command.</summary>
    /// <param name="context">The global context.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The exit code.</returns>
    public static async Task<int> RunAsync(GlobalContext context, CancellationToken ct)
    {
        context.Line.Expect("migrate", 1);
        var repo = context.RepoRoot();
        var path = Path.Combine(repo, GlobalContext.ModelFolder, "maquettiste.json");
        if (!File.Exists(path))
        {
            await context.Error.WriteLineAsync($"maquettiste: no model found: {path} does not exist (run maquettiste init).").ConfigureAwait(false);
            return Program.ExitCodes.Invalid;
        }

        int version;
        try
        {
            using var document = JsonDocument.Parse(await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false));
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("formatVersion", out var element)
                || element.ValueKind != JsonValueKind.Number || !element.TryGetInt32(out version) || version < 1)
            {
                await context.Error.WriteLineAsync($"maquettiste: {path} has no valid formatVersion (a whole number, at least 1).").ConfigureAwait(false);
                return Program.ExitCodes.Invalid;
            }
        }
        catch (JsonException e)
        {
            await context.Error.WriteLineAsync($"maquettiste: {path} is not valid JSON: {e.Message}").ConfigureAwait(false);
            return Program.ExitCodes.Invalid;
        }

        var current = EngineVersion.FormatVersion.ToString(CultureInfo.InvariantCulture);
        if (version > EngineVersion.FormatVersion)
        {
            await context.Error.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
                $"maquettiste: model format {version} is newer than this tool supports (format {current}); update Maquettiste.Cli.")).ConfigureAwait(false);
            return Program.ExitCodes.Internal;
        }

        await context.Out.WriteLineAsync($"Model format {current} is current.").ConfigureAwait(false);
        return Program.ExitCodes.Success;
    }
}
