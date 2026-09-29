using System.Globalization;
using System.Text;
using System.Text.Json;
using Maquettiste.Engine;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Localization;
using Maquettiste.Engine.Model;

namespace Maquettiste.Cli.Commands;

/// <summary>
/// <c>maquettiste l10n status|export|import|prune|set-default</c> (reference-types-seeds-localization.md section 3.9): the localization
/// operations of the API and the MCP tools, over the model on disk.
/// </summary>
internal static class L10nCommand
{
    /// <summary>Runs <c>l10n &lt;verb&gt;</c>.</summary>
    /// <param name="context">The global context.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The exit code.</returns>
    public static async Task<int> RunAsync(GlobalContext context, CancellationToken ct)
    {
        var line = context.Line;
        var verb = line.Positionals.Count > 1 ? line.Positionals[1] : throw new UsageException("'l10n' needs a verb: status, export, import, prune or set-default.");
        switch (verb)
        {
            case "status":
                line.Expect("l10n status", 2, "--format");
                break;
            case "export":
                line.Expect("l10n export", 3, "--format", "--out");
                break;
            case "import":
                line.Expect("l10n import", 4, "--format", "--apply", "--dry-run", "--check");
                break;
            case "prune":
                line.Expect("l10n prune", 2, "--format", "--apply", "--dry-run", "--check");
                break;
            case "set-default":
                line.Expect("l10n set-default", 3, "--format", "--apply", "--dry-run", "--check");
                break;
            default:
                throw new UsageException($"Unknown l10n verb '{verb}': use status, export, import, prune or set-default.");
        }

        var json = verb == "export" ? false : line.Choice("--format", "text", "text", "json") == "json";
        var format = verb == "export" ? line.Choice("--format", "xliff", "xliff", "csv") : null;
        if (line.Has("--apply") && (line.Has("--dry-run") || line.Has("--check")))
            throw new UsageException("--apply contradicts --dry-run and --check.");
        if (await ProjectGuard.RepoAsync(context).ConfigureAwait(false) is not { } repo)
            return Program.ExitCodes.Invalid;

        var store = new ModelStore(context.EngineOptions(repo));
        await using (store.ConfigureAwait(false))
        {
            await store.LoadAsync(ct).ConfigureAwait(false);
            var status = await store.GetLocalizationStatusAsync(ct).ConfigureAwait(false);
            if (verb == "status")
                return await StatusAsync(context, status, json).ConfigureAwait(false);
            if (status.DefaultLocale is null)
            {
                await context.Error.WriteLineAsync("maquettiste: the model declares no localization (add a localization block to maquettiste.json).").ConfigureAwait(false);
                return Program.ExitCodes.Invalid;
            }

            return verb switch
            {
                "export" => await ExportAsync(context, store, LocaleArgument(line.Positionals[2]), format!, ct).ConfigureAwait(false),
                "import" => await ImportAsync(context, store, LocaleArgument(line.Positionals[2]), line.Positionals[3], json, ct).ConfigureAwait(false),
                "prune" => await PruneAsync(context, store, json, ct).ConfigureAwait(false),
                _ => await SetDefaultCommand.RunAsync(context, store, status, LocaleArgument(line.Positionals[2]), json, ct).ConfigureAwait(false),
            };
        }
    }

    private static async Task<int> StatusAsync(GlobalContext context, LocalizationStatus status, bool json)
    {
        if (json)
        {
            await context.Out.WriteLineAsync(DiagnosticOutput.ToJson(writer => JsonSerializer.Serialize(writer, status, CliJson.Options))).ConfigureAwait(false);
            return Program.ExitCodes.Success;
        }

        var text = new StringBuilder();
        if (status.DefaultLocale is null)
        {
            text.Append("Localization: not declared (no localization block in maquettiste.json).\n");
        }
        else
        {
            text.Append("Default locale: ").Append(status.DefaultLocale).Append('\n');
            text.Append("Declared locales: ").Append(string.Join(", ", status.Declared)).Append('\n');
            foreach (var locale in status.Locales)
            {
                int expected = locale.Shards.Sum(s => s.Expected), translated = locale.Shards.Sum(s => s.Translated);
                text.Append(Invariant($"{locale.Locale}: {translated} of {expected} translated ({Percent(translated, expected)}), "))
                    .Append(Invariant($"{locale.Shards.Sum(s => s.Missing)} missing, {locale.Shards.Sum(s => s.Stale)} stale; chain {string.Join(" > ", locale.Chain)}\n"));
                foreach (var shard in locale.Shards)
                    text.Append(Invariant($"  {shard.Shard}: {shard.Translated} of {shard.Expected} translated, {shard.Missing} missing, {shard.Stale} stale\n"));
            }
        }

        await context.Out.WriteAsync(text.ToString()).ConfigureAwait(false);
        return Program.ExitCodes.Success;
    }

    private static async Task<int> ExportAsync(GlobalContext context, ModelStore store, string locale, string format, CancellationToken ct)
    {
        if (!await store.IsTranslatedLocaleAsync(locale, ct).ConfigureAwait(false))
            return await NotTranslatedAsync(context, locale).ConfigureAwait(false);
        var text = await store.ExportTranslationsAsync(locale, format, null, ct).ConfigureAwait(false);
        await CliFiles.EmitAsync(context, text, ct).ConfigureAwait(false);
        return Program.ExitCodes.Success;
    }

    private static async Task<int> ImportAsync(GlobalContext context, ModelStore store, string locale, string file, bool json, CancellationToken ct)
    {
        if (!await store.IsTranslatedLocaleAsync(locale, ct).ConfigureAwait(false))
            return await NotTranslatedAsync(context, locale).ConfigureAwait(false);
        if (await CliFiles.ReadAsync(context, file, ct).ConfigureAwait(false) is not { } content)
            return Program.ExitCodes.Invalid;

        IReadOnlyList<TranslationUnit> units;
        try
        {
            units = IsCsv(file, content) ? TranslationFiles.ReadCsv(content) : TranslationFiles.ReadXliff(content);
        }
        catch (FormatException e)
        {
            await context.Error.WriteLineAsync($"maquettiste: {file} cannot be read: {e.Message}").ConfigureAwait(false);
            return Program.ExitCodes.Invalid;
        }

        // The plan: last unit per (id, field) wins; empty targets are left alone.
        var page = await store.GetTranslationsAsync(locale, null, null, ct).ConfigureAwait(false);
        var current = page.Items.ToDictionary(i => (i.Id, i.Field));
        var latest = new Dictionary<(string, string), TranslationUnit>();
        var order = new List<(string, string)>();
        foreach (var unit in units.Where(u => u.Value.Length > 0))
        {
            if (latest.TryAdd((unit.Id, unit.Field), unit))
                order.Add((unit.Id, unit.Field));
            else
                latest[(unit.Id, unit.Field)] = unit;
        }

        var edits = new List<TranslationEdit>();
        var lines = new List<(string Change, string Key, string? Before, string? After)>();
        var ignored = new List<Diagnostic>();
        foreach (var key in order)
        {
            var unit = latest[key];
            var label = unit.Id + "/" + unit.Field;
            if (!current.TryGetValue(key, out var item))
            {
                ignored.Add(RuleCatalog.Create("MQ7203", $"{label} is not a translatable field of the model; the unit is ignored.", unit.Id, null, null));
                continue;
            }

            if (item.Value == unit.Value)
            {
                if (item.State == "stale" && unit.State is "translated" or "reviewed" or "final")
                {
                    edits.Add(new TranslationEdit(unit.Id, unit.Field, null, Confirm: true));
                    lines.Add(("confirmed", label, null, null));
                }

                continue;
            }

            edits.Add(new TranslationEdit(unit.Id, unit.Field, unit.Value));
            lines.Add((item.Value is null ? "added" : "changed", label, item.Value, unit.Value));
        }

        var apply = context.Line.Has("--apply");
        var outcome = SaveOutcome.Saved;
        IReadOnlyList<Diagnostic> refused = [];
        if (apply && edits.Count > 0)
        {
            var saved = await store.SaveTranslationsAsync(locale, edits, page.ShardHashes, ChangeSource.Cli, ct).ConfigureAwait(false);
            outcome = saved.Outcome;
            refused = saved.Diagnostics;
        }

        int Count(string change) => lines.Count(l => l.Change == change);
        var applied = apply && edits.Count > 0 && outcome == SaveOutcome.Saved;
        if (json)
        {
            await context.Out.WriteLineAsync(DiagnosticOutput.ToJson(writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("locale", locale);
                writer.WriteNumber("added", Count("added"));
                writer.WriteNumber("changed", Count("changed"));
                writer.WriteNumber("confirmed", Count("confirmed"));
                writer.WriteNumber("ignored", ignored.Count);
                writer.WriteBoolean("applied", applied);
                writer.WriteString("outcome", OutcomeName(outcome));
                writer.WriteStartArray("entries");
                foreach (var (change, key, before, after) in lines)
                {
                    writer.WriteStartObject();
                    writer.WriteString("change", change);
                    writer.WriteString("id", key[..key.LastIndexOf('/')]);
                    writer.WriteString("field", key[(key.LastIndexOf('/') + 1)..]);
                    if (before is not null)
                        writer.WriteString("before", before);
                    if (after is not null)
                        writer.WriteString("after", after);
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
                DiagnosticOutput.WriteArray(writer, "diagnostics", [.. ignored, .. refused]);
                writer.WriteEndObject();
            })).ConfigureAwait(false);
        }
        else
        {
            var text = new StringBuilder();
            foreach (var (change, key, before, after) in lines)
            {
                text.Append(change.PadRight(10)).Append(key);
                if (change == "added")
                    text.Append(": ").Append(Quote(after));
                else if (change == "changed")
                    text.Append(": ").Append(Quote(before)).Append(" -> ").Append(Quote(after));
                text.Append('\n');
            }

            foreach (var d in ignored.Concat(refused))
                text.Append(DiagnosticOutput.Line(d)).Append('\n');
            await context.Out.WriteAsync(text.ToString()).ConfigureAwait(false);
        }

        var summary = Invariant($"{locale} import: {Count("added")} added, {Count("changed")} changed, {Count("confirmed")} stale confirmed, {ignored.Count} ignored");
        return await FinishAsync(context, summary, edits.Count, apply, outcome, "run with --apply to write it", refused).ConfigureAwait(false);
    }

    private static async Task<int> PruneAsync(GlobalContext context, ModelStore store, bool json, CancellationToken ct)
    {
        var preview = !context.Line.Has("--apply");
        var result = await store.PruneTranslationsAsync(preview, ChangeSource.Cli, ct).ConfigureAwait(false);
        if (json)
        {
            await context.Out.WriteLineAsync(DiagnosticOutput.ToJson(writer =>
            {
                writer.WriteStartObject();
                writer.WriteNumber("removed", result.Applied ? result.Orphans.Count : 0);
                writer.WriteBoolean("applied", result.Applied);
                writer.WriteString("outcome", OutcomeName(result.Outcome));
                writer.WritePropertyName("orphans");
                JsonSerializer.Serialize(writer, result.Orphans, CliJson.Options);
                DiagnosticOutput.WriteArray(writer, "diagnostics", result.Diagnostics);
                writer.WriteEndObject();
            })).ConfigureAwait(false);
        }
        else
        {
            var text = new StringBuilder();
            foreach (var orphan in result.Orphans)
                text.Append(preview ? "orphan " : "removed ").Append(orphan.Locale).Append(' ').Append(orphan.Id).Append(orphan.Field is null ? "" : "/" + orphan.Field)
                    .Append(" (").Append(orphan.Shard).Append(")\n");
            await context.Out.WriteAsync(text.ToString()).ConfigureAwait(false);
        }

        var summary = Invariant($"l10n prune: {result.Orphans.Count} orphan {(result.Orphans.Count == 1 ? "entry" : "entries")}");
        return await FinishAsync(context, summary, result.Orphans.Count, !preview, result.Outcome, "run with --apply to remove them", result.Diagnostics).ConfigureAwait(false);
    }

    /// <summary>The summary line and the exit code shared by the preview-then-apply verbs.</summary>
    /// <param name="context">The context.</param>
    /// <param name="summary">The counts.</param>
    /// <param name="pending">How many changes the operation holds.</param>
    /// <param name="apply">Whether it was meant to write.</param>
    /// <param name="outcome">The write outcome.</param>
    /// <param name="hint">How to apply a preview.</param>
    /// <param name="diagnostics">The diagnostics of the write, if any (a refused model write is MQ6004).</param>
    /// <returns>0 when done, 1 when the change is not valid, 2 for a <c>--check</c> preview that would change something, 3 when a
    /// file changed while the command ran, 4 when the model write was refused (as for every other refused write).</returns>
    internal static async Task<int> FinishAsync(GlobalContext context, string summary, int pending, bool apply, SaveOutcome outcome, string hint,
        IReadOnlyList<Diagnostic>? diagnostics = null)
    {
        if (apply && outcome == SaveOutcome.Conflict)
        {
            await context.Error.WriteLineAsync($"maquettiste: {summary}; a file changed while the command ran, so nothing was written. Run the command again.").ConfigureAwait(false);
            return Program.ExitCodes.Conflicts;
        }

        if (apply && outcome != SaveOutcome.Saved && diagnostics?.FirstOrDefault(d => d.Rule == "MQ6004") is { } refusedWrite)
        {
            await context.Error.WriteLineAsync($"maquettiste: {summary}; {refusedWrite.Message}").ConfigureAwait(false);
            return Program.ExitCodes.Internal;
        }

        if (apply && outcome != SaveOutcome.Saved)
        {
            await context.Error.WriteLineAsync($"maquettiste: {summary}; the change is not valid, so nothing was written.").ConfigureAwait(false);
            return Program.ExitCodes.Invalid;
        }

        if (apply)
        {
            context.Info(pending == 0 ? summary + "; nothing to write." : summary + "; written.");
            return Program.ExitCodes.Success;
        }

        context.Info(pending == 0 ? summary + "; nothing to write." : summary + "; preview only, " + hint + ".");
        return context.Line.Has("--check") && pending > 0 ? Program.ExitCodes.Drift : Program.ExitCodes.Success;
    }

    internal static string OutcomeName(SaveOutcome outcome) => outcome switch
    {
        SaveOutcome.Saved => "saved",
        SaveOutcome.Conflict => "conflict",
        _ => "invalid",
    };

    /// <summary>A locale argument as the editor takes it (zh_cn is zh-CN); one that stays malformed is a usage error that says how to write it.</summary>
    /// <param name="typed">The argument.</param>
    /// <returns>The normalized tag.</returns>
    internal static string LocaleArgument(string typed)
    {
        var tag = LocaleChains.Normalize(typed);
        return LocaleChains.IsLanguageTag(tag) ? tag
            : throw new UsageException($"'{typed}' is not a BCP 47 language tag. {LocaleChains.TagAdvice(typed)}");
    }

    /// <summary>A locale argument that names no translated locale is a usage error (exit 4).</summary>
    internal static Task<int> NotTranslatedAsync(GlobalContext context, string locale) =>
        throw new UsageException($"'{locale}' is not a declared locale other than the default (see maquettiste l10n status).");

    private static bool IsCsv(string file, string content) =>
        file.EndsWith(".csv", StringComparison.OrdinalIgnoreCase) || !content.TrimStart('﻿', ' ', '\t', '\r', '\n').StartsWith('<');

    private static string Quote(string? text) => text is null ? "(none)" : "\"" + text + "\"";

    private static string Percent(int part, int whole) =>
        whole == 0 ? "100%" : ((int)Math.Floor(part * 100.0 / whole)).ToString(CultureInfo.InvariantCulture) + "%";

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
