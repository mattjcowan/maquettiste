using System.Globalization;
using System.Text;
using System.Text.Json;
using Maquettiste.Engine;
using Maquettiste.Engine.Model;

namespace Maquettiste.Cli.Commands;

/// <summary>
/// <c>maquettiste seed new|export|import</c> (reference-types-seeds-localization.md section 2.3): a reference type's empty seed with
/// the code, label and description columns, a seed's rows as CSV, and a CSV import previewed, then applied with <c>--apply</c> as one
/// save checked against the seed's hash as read.
/// </summary>
internal static class SeedCommand
{
    /// <summary>Runs <c>seed &lt;verb&gt;</c>.</summary>
    /// <param name="context">The global context.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The exit code.</returns>
    public static async Task<int> RunAsync(GlobalContext context, CancellationToken ct)
    {
        var line = context.Line;
        var verb = line.Positionals.Count > 1 ? line.Positionals[1] : throw new UsageException("'seed' needs a verb: new, export or import.");
        switch (verb)
        {
            case "new":
                line.Expect("seed new", 3);
                break;
            case "export":
                line.Expect("seed export", 3, "--locale", "--out");
                break;
            case "import":
                line.Expect("seed import", 4, "--mode", "--format", "--apply", "--dry-run", "--check");
                break;
            default:
                throw new UsageException($"Unknown seed verb '{verb}': use new, export or import.");
        }

        var json = verb == "import" && line.Choice("--format", "text", "text", "json") == "json";
        var mode = verb == "import" ? line.Choice("--mode", "merge", "merge", "replace") : "merge";
        if (line.Has("--apply") && (line.Has("--dry-run") || line.Has("--check")))
            throw new UsageException("--apply contradicts --dry-run and --check.");
        if (await ProjectGuard.RepoAsync(context).ConfigureAwait(false) is not { } repo)
            return Program.ExitCodes.Invalid;

        var store = new ModelStore(context.EngineOptions(repo));
        await using (store.ConfigureAwait(false))
        {
            await store.LoadAsync(ct).ConfigureAwait(false);
            if (verb == "new")
                return await NewAsync(context, store, line.Positionals[2], ct).ConfigureAwait(false);
            if (await ResolveAsync(context, store, line.Positionals[2], ct).ConfigureAwait(false) is not { } seed)
                return Program.ExitCodes.Invalid;
            return verb == "export"
                ? await ExportAsync(context, store, seed, ct).ConfigureAwait(false)
                : await ImportAsync(context, store, seed, line.Positionals[3], mode == "replace", json, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Creates the seed of a reference type (by id, else by name) with the code, label and description columns.</summary>
    private static async Task<int> NewAsync(GlobalContext context, ModelStore store, string key, CancellationToken ct)
    {
        var index = await store.GetIndexAsync(ct).ConfigureAwait(false);
        var types = index.Where(e => e.Kind == "reference-type").ToList();
        var matches = types.Where(t => t.Id == key).ToList();
        if (matches.Count == 0)
            matches = [.. types.Where(t => string.Equals(t.Name, key, StringComparison.Ordinal))];
        if (matches.Count == 0)
            matches = [.. types.Where(t => string.Equals(t.Name, key, StringComparison.OrdinalIgnoreCase))];
        if (matches.Count != 1)
            throw new UsageException(matches.Count == 0
                ? $"no reference type has the id or name '{key}'."
                : $"'{key}' names {matches.Count} reference types; pass one id: {string.Join(", ", matches.Select(m => m.Id + " (" + m.Name + ")"))}.");
        var type = matches[0];
        var result = (await store.CreateSeedAsync(type.Id, ChangeSource.Cli, ct).ConfigureAwait(false))!;
        if (result.Outcome != SaveOutcome.Saved)
        {
            foreach (var d in result.Diagnostics)
                await context.Error.WriteLineAsync(DiagnosticOutput.Line(d)).ConfigureAwait(false);
            return Program.ExitCodes.Invalid;
        }

        await context.Out.WriteLineAsync(result.Changes is null
            ? $"{type.Name} already has a seed ({result.Id})."
            : $"created seed {type.Name} ({result.Id}) with the columns code, label and description.").ConfigureAwait(false);
        return Program.ExitCodes.Success;
    }

    /// <summary>A seed by id, else by name (exact, then ignoring case), else by the id or name of the element it seeds.</summary>
    private static async Task<ElementSummary?> ResolveAsync(GlobalContext context, ModelStore store, string key, CancellationToken ct)
    {
        var index = await store.GetIndexAsync(ct).ConfigureAwait(false);
        var seeds = index.Where(e => e.Kind == "seed").ToList();
        var matches = seeds.Where(s => s.Id == key).ToList();
        if (matches.Count == 0)
            matches = [.. seeds.Where(s => string.Equals(s.Name, key, StringComparison.Ordinal))];
        if (matches.Count == 0)
            matches = [.. seeds.Where(s => string.Equals(s.Name, key, StringComparison.OrdinalIgnoreCase))];
        if (matches.Count == 0)
        {
            var targets = index.Where(e => e.Id == key || string.Equals(e.Name, key, StringComparison.OrdinalIgnoreCase)).Select(e => e.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var summary in seeds)
            {
                if ((await store.GetElementAsync(summary.Id, ct).ConfigureAwait(false))?.Element is Seed s && targets.Contains(s.Target))
                    matches.Add(summary);
            }
        }

        if (matches.Count == 1)
            return matches[0];
        // An argument that names no seed, or several, is a usage error (exit 4).
        throw new UsageException(matches.Count == 0
            ? $"no seed has the id or name '{key}', and no element with that id or name has a seed."
            : $"'{key}' names {matches.Count} seeds; pass one id: {string.Join(", ", matches.Select(m => m.Id + " (" + m.Name + ")"))}.");
    }

    private static async Task<int> ExportAsync(GlobalContext context, ModelStore store, ElementSummary seed, CancellationToken ct)
    {
        var locales = context.Line.Values("--locale").Select(L10nCommand.LocaleArgument).ToList();
        foreach (var locale in locales)
        {
            if (!await store.IsTranslatedLocaleAsync(locale, ct).ConfigureAwait(false))
                return await L10nCommand.NotTranslatedAsync(context, locale).ConfigureAwait(false);
        }

        var text = await store.ExportSeedCsvAsync(seed.Id, false, locales, ct).ConfigureAwait(false);
        await CliFiles.EmitAsync(context, text!, ct).ConfigureAwait(false);
        return Program.ExitCodes.Success;
    }

    private static async Task<int> ImportAsync(GlobalContext context, ModelStore store, ElementSummary seed, string file, bool replace, bool json, CancellationToken ct)
    {
        if (await CliFiles.ReadAsync(context, file, ct).ConfigureAwait(false) is not { } csv)
            return Program.ExitCodes.Invalid;
        var apply = context.Line.Has("--apply");
        ImportResult result;
        try
        {
            // The preview, then (with --apply) the same import checked against the hash the preview read.
            result = (await store.ImportSeedCsvAsync(seed.Id, csv, replace, true, null, ChangeSource.Cli, ct).ConfigureAwait(false))!;
            if (apply && Pending(result.Preview) > 0)
                result = (await store.ImportSeedCsvAsync(seed.Id, csv, replace, false, seed.Hash, ChangeSource.Cli, ct).ConfigureAwait(false))!;
        }
        catch (FormatException e)
        {
            await context.Error.WriteLineAsync($"maquettiste: {file} cannot be read: {e.Message}").ConfigureAwait(false);
            return Program.ExitCodes.Invalid;
        }

        var preview = result.Preview;
        if (json)
        {
            await context.Out.WriteLineAsync(DiagnosticOutput.ToJson(writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("seed", seed.Id);
                writer.WriteString("outcome", L10nCommand.OutcomeName(result.Outcome));
                writer.WritePropertyName("preview");
                JsonSerializer.Serialize(writer, preview, CliJson.Options);
                writer.WriteEndObject();
            })).ConfigureAwait(false);
        }
        else
        {
            var text = new StringBuilder();
            foreach (var change in preview.Changed)
                text.Append("changed   ").Append(change.ToJsonString(CliJson.Options)).Append('\n');
            foreach (var blocked in preview.Blocked ?? [])
                text.Append("blocked   ").Append((string?)blocked["id"]).Append(": still referenced ")
                    .Append(blocked["referrers"] is System.Text.Json.Nodes.JsonArray { Count: var n } ? n.ToString(CultureInfo.InvariantCulture) + (n == 1 ? " time" : " times") : "").Append('\n');
            foreach (var header in preview.IgnoredHeaders ?? [])
                text.Append("ignored   column ").Append(header).Append('\n');
            foreach (var d in preview.Diagnostics)
                text.Append(DiagnosticOutput.Line(d)).Append('\n');
            await context.Out.WriteAsync(text.ToString()).ConfigureAwait(false);
        }

        var summary = string.Create(CultureInfo.InvariantCulture,
            $"{seed.Name} import ({(replace ? "replace" : "merge")}): {preview.Added} added, {preview.Changed.Count} changed, {preview.Removed} removed, {preview.Blocked?.Count ?? 0} blocked");
        if (!apply && preview.Diagnostics.Any(d => d.Severity == Engine.Diagnostics.DiagnosticSeverity.Error))
        {
            await context.Error.WriteLineAsync("maquettiste: " + summary + "; the import is not valid.").ConfigureAwait(false);
            return Program.ExitCodes.Invalid;
        }

        return await L10nCommand.FinishAsync(context, summary, Pending(preview), apply, result.Outcome, "run with --apply to write it", preview.Diagnostics).ConfigureAwait(false);
    }

    private static int Pending(ImportPreview preview) => preview.Added + preview.Changed.Count + preview.Removed;
}
