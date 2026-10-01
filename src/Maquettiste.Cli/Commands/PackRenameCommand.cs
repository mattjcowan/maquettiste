using System.Globalization;
using System.Text;
using System.Text.Json;
using Maquettiste.Engine;

namespace Maquettiste.Cli.Commands;

/// <summary>
/// <c>maquettiste pack rename &lt;name&gt; &lt;new-name&gt; [--apply] [--keep-hints] [--format text|json]</c>: previews renaming a pack (its folder
/// <c>.maquettiste/templates/&lt;name&gt;/</c>, its <c>packs.&lt;name&gt;</c> settings entry, its manifests and unit states); <c>--apply</c>
/// renames it through <see cref="GenerationService.RenamePackAsync"/>. The files the pack generated stay tracked under the new name;
/// both forms list them, and the elements whose generation hints name the old pack, which <c>--apply</c> moves to the new name in one model
/// batch unless <c>--keep-hints</c> is given.
/// </summary>
internal static class PackRenameCommand
{
    /// <summary>Runs the command.</summary>
    /// <param name="context">The global context.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>0 when renamed (or previewed), 1 when there is no such pack or the new name or the settings save is refused, 3 when
    /// pack.json changed while the command ran, 4 for a refused path.</returns>
    public static async Task<int> RunAsync(GlobalContext context, CancellationToken ct)
    {
        context.Line.Expect("pack rename", 4, "--apply", "--keep-hints", "--format");
        var name = context.Line.Positionals[2];
        var newName = context.Line.Positionals[3];
        var json = context.Line.Choice("--format", "text", "text", "json") == "json";
        var apply = context.Line.Has("--apply");
        var updateHints = !context.Line.Has("--keep-hints");
        if (await ProjectGuard.RepoAsync(context).ConfigureAwait(false) is not { } repo)
            return Program.ExitCodes.Invalid;

        var options = context.EngineOptions(repo);
        var store = new ModelStore(options);
        await using (store.ConfigureAwait(false))
        {
            await store.LoadAsync(ct).ConfigureAwait(false);
            var service = new GenerationService(store, options);
            try
            {
                var pack = await service.GetPackAsync(name, ct).ConfigureAwait(false);
                if (pack is null)
                {
                    await context.Error.WriteLineAsync($"maquettiste: no pack named '{name}' (.maquettiste/templates/{name}/pack.json does not exist).").ConfigureAwait(false);
                    return Program.ExitCodes.Invalid;
                }

                var settings = await store.GetSettingsAsync(ct).ConfigureAwait(false);
                var hasEntry = settings.Json.ValueKind == JsonValueKind.Object && settings.Json.TryGetProperty("packs", out var packs)
                    && packs.ValueKind == JsonValueKind.Object && packs.TryGetProperty(name, out _);
                var result = await service.RenamePackAsync(name, newName, pack.Hash, ct, ChangeSource.Cli, dryRun: !apply, updateHints: updateHints).ConfigureAwait(false);
                switch (result.Outcome)
                {
                    case SaveOutcome.Saved:
                        break;
                    case SaveOutcome.Conflict:
                        await context.Error.WriteLineAsync($"maquettiste: pack rename {name}: pack.json changed while the command ran, so nothing was renamed. Run the command again.").ConfigureAwait(false);
                        return Program.ExitCodes.Conflicts;
                    case SaveOutcome.NotFound:
                        await context.Error.WriteLineAsync($"maquettiste: no pack named '{name}'.").ConfigureAwait(false);
                        return Program.ExitCodes.Invalid;
                    default:
                        foreach (var diagnostic in result.Diagnostics)
                            await context.Error.WriteLineAsync(DiagnosticOutput.Line(diagnostic)).ConfigureAwait(false);
                        await context.Error.WriteLineAsync($"maquettiste: pack rename {name} {newName}: refused, so nothing was renamed.").ConfigureAwait(false);
                        return Program.ExitCodes.Invalid;
                }

                if (apply)
                    hasEntry = result.SettingsHash is not null;
                foreach (var warning in result.Diagnostics)
                    await context.Error.WriteLineAsync(DiagnosticOutput.Line(warning)).ConfigureAwait(false);
                await ReportAsync(context, result, apply, updateHints, json, hasEntry).ConfigureAwait(false);
                return Program.ExitCodes.Success;
            }
            catch (PackPathException ex)
            {
                await context.Error.WriteLineAsync("maquettiste: " + ex.Message).ConfigureAwait(false);
                return Program.ExitCodes.Internal;
            }
        }
    }

    private static async Task ReportAsync(GlobalContext context, PackRenameResult result, bool apply, bool updateHints, bool json, bool settingsEntry)
    {
        if (json)
        {
            await context.Out.WriteLineAsync(DiagnosticOutput.ToJson(writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("pack", result.From);
                writer.WriteString("name", result.To);
                writer.WriteBoolean("applied", apply);
                writer.WritePropertyName("files");
                JsonSerializer.Serialize(writer, result.Files, CliJson.Options);
                writer.WriteBoolean("settingsEntry", settingsEntry);
                writer.WritePropertyName("tracked");
                JsonSerializer.Serialize(writer, result.Tracked, CliJson.Options);
                writer.WritePropertyName("hints");
                JsonSerializer.Serialize(writer, result.Hints, CliJson.Options);
                writer.WriteBoolean("updateHints", updateHints);
                writer.WritePropertyName("hintsUpdated");
                JsonSerializer.Serialize(writer, result.HintsUpdated, CliJson.Options);
                writer.WriteEndObject();
            })).ConfigureAwait(false);
        }
        else
        {
            var text = new StringBuilder();
            text.Append(apply ? "moved " : "move ").Append(".maquettiste/templates/").Append(result.From).Append("/ to .maquettiste/templates/")
                .Append(result.To).Append("/ (").Append(result.Files.Count.ToString(CultureInfo.InvariantCulture))
                .Append(result.Files.Count == 1 ? " file)\n" : " files)\n");
            if (settingsEntry)
                text.Append(apply ? "moved " : "move ").Append("packs.").Append(result.From).Append(" to packs.").Append(result.To).Append(" in .maquettiste/maquettiste.json\n");
            foreach (var path in result.Tracked)
                text.Append("tracked ").Append(path).Append('\n');
            var updated = result.HintsUpdated.ToHashSet(StringComparer.Ordinal);
            foreach (var id in result.Hints)
            {
                text.Append("hint ").Append(id);
                if (!updateHints || (apply && !updated.Contains(id)))
                    text.Append(" keeps generation.").Append(result.From).Append('\n');
                else
                    text.Append(apply ? " moved generation." : " move generation.").Append(result.From).Append(" to generation.").Append(result.To).Append('\n');
            }
            await context.Out.WriteAsync(text.ToString()).ConfigureAwait(false);
        }

        var count = result.Tracked.Count.ToString(CultureInfo.InvariantCulture);
        var summary = $"pack rename {result.From} {result.To}: {count} generated {(result.Tracked.Count == 1 ? "file stays" : "files stay")} tracked under the new name";
        var kept = !updateHints ? result.Hints.Count : apply ? result.Hints.Count - result.HintsUpdated.Count : 0;
        var moving = result.Hints.Count - kept;
        if (moving > 0)
            summary += $"; {moving.ToString(CultureInfo.InvariantCulture)} {(moving == 1 ? "element's" : "elements'")} generation hints {(apply ? "moved" : "move")} to '{result.To}'";
        if (kept > 0)
            summary += $"; {kept.ToString(CultureInfo.InvariantCulture)} {(kept == 1 ? "element's" : "elements'")} generation hints still name '{result.From}'";
        context.Info(apply ? summary + "." : summary + "; run with --apply to rename the pack.");
    }
}
