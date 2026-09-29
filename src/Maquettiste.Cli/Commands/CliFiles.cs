using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Maquettiste.Cli.Commands;

/// <summary>The JSON options of the <c>--format json</c> outputs that serialize engine records.</summary>
internal static class CliJson
{
    /// <summary>camelCase, nulls omitted, no escaping of non-ASCII text.</summary>
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}

/// <summary>The input and output files of the exchange verbs (<c>l10n</c>, <c>seed</c>).</summary>
internal static class CliFiles
{
    /// <summary>
    /// Writes a document to <c>--out</c> (the file the user names, relative to the current directory; an exchange file for translators or
    /// a spreadsheet, so it is not held to <c>outputs.allow</c>, but never inside the model folder), else to stdout.
    /// </summary>
    /// <param name="context">The context.</param>
    /// <param name="text">The document.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>A task.</returns>
    public static async Task EmitAsync(GlobalContext context, string text, CancellationToken ct)
    {
        if (context.Line.Value("--out") is not { } output)
        {
            await context.Out.WriteAsync(text).ConfigureAwait(false);
            await context.Out.FlushAsync(ct).ConfigureAwait(false);
            return;
        }

        var full = Path.GetFullPath(output, context.Environment.CurrentDirectory);
        var model = Path.Combine(context.RepoRoot(), GlobalContext.ModelFolder) + Path.DirectorySeparatorChar;
        if (full.StartsWith(model, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new UsageException($"--out {output} lies inside the model folder; write the export elsewhere.");
        if (Path.GetDirectoryName(full) is { } folder)
            Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(full, text, new UTF8Encoding(false), ct).ConfigureAwait(false);
        context.Info("Wrote " + full + ".");
    }

    /// <summary>Reads an input file (relative to the current directory) as UTF-8, or reports why it cannot.</summary>
    /// <param name="context">The context.</param>
    /// <param name="file">The path.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The text, or <see langword="null"/> after an error message.</returns>
    public static async Task<string?> ReadAsync(GlobalContext context, string file, CancellationToken ct)
    {
        var full = Path.GetFullPath(file, context.Environment.CurrentDirectory);
        if (!File.Exists(full))
        {
            await context.Error.WriteLineAsync($"maquettiste: {file} does not exist.").ConfigureAwait(false);
            return null;
        }

        var text = await File.ReadAllTextAsync(full, Encoding.UTF8, ct).ConfigureAwait(false);
        return text.TrimStart('﻿');
    }
}
