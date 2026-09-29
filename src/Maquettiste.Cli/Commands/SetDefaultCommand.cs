using System.Globalization;
using System.Text;
using System.Text.Json;
using Maquettiste.Engine;
using Maquettiste.Engine.Model;

namespace Maquettiste.Cli.Commands;

/// <summary>
/// <c>maquettiste l10n set-default &lt;locale&gt;</c>: previews, then with <c>--apply</c> performs, the default-locale refactoring of
/// reference-types-seeds-localization.md section 3.2 (texts swap between the element files and the locale shards in one change).
/// </summary>
internal static class SetDefaultCommand
{
    /// <summary>Runs the verb over a loaded store.</summary>
    /// <param name="context">The context.</param>
    /// <param name="store">The loaded store.</param>
    /// <param name="status">The localization status.</param>
    /// <param name="locale">The new default locale.</param>
    /// <param name="json">Whether to write JSON.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The exit code.</returns>
    public static async Task<int> RunAsync(GlobalContext context, ModelStore store, LocalizationStatus status, string locale, bool json, CancellationToken ct)
    {
        if (locale == status.DefaultLocale)
        {
            context.Info($"l10n set-default: {locale} is already the default locale; nothing to write.");
            return Program.ExitCodes.Success;
        }

        var apply = context.Line.Has("--apply");
        if (await store.ChangeDefaultLocaleAsync(locale, !apply, ChangeSource.Cli, ct).ConfigureAwait(false) is not { } result)
            return await L10nCommand.NotTranslatedAsync(context, locale).ConfigureAwait(false);

        if (json)
        {
            await context.Out.WriteLineAsync(DiagnosticOutput.ToJson(writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("from", result.From);
                writer.WriteString("to", result.To);
                writer.WriteBoolean("applied", result.Applied);
                writer.WriteString("outcome", L10nCommand.OutcomeName(result.Outcome));
                writer.WritePropertyName("moves");
                JsonSerializer.Serialize(writer, result.Moves, CliJson.Options);
                writer.WritePropertyName("skipped");
                JsonSerializer.Serialize(writer, result.Skipped, CliJson.Options);
                DiagnosticOutput.WriteArray(writer, "diagnostics", result.Diagnostics);
                writer.WriteEndObject();
            })).ConfigureAwait(false);
        }
        else
        {
            var text = new StringBuilder();
            foreach (var move in result.Moves)
            {
                text.Append("moved   ").Append(move.Id).Append('/').Append(move.Field).Append(": \"").Append(move.After).Append('"');
                if (move.Before is not null)
                    text.Append(" (").Append(result.From).Append(": \"").Append(move.Before).Append("\")");
                text.Append('\n');
            }

            foreach (var skipped in result.Skipped)
                text.Append("skipped ").Append(skipped).Append('\n');
            await context.Out.WriteAsync(text.ToString()).ConfigureAwait(false);
        }

        var summary = string.Create(CultureInfo.InvariantCulture,
            $"l10n set-default {result.From} -> {result.To}: {result.Moves.Count} {(result.Moves.Count == 1 ? "text" : "texts")} swapped, {result.Skipped.Count} skipped");
        // The locale itself changes even when no text moves, so a preview always holds one change.
        return await L10nCommand.FinishAsync(context, summary, result.Moves.Count + 1, apply, result.Outcome, "run with --apply to write it", result.Diagnostics).ConfigureAwait(false);
    }
}
