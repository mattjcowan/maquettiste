using System.Globalization;
using System.Text;
using System.Text.Json;
using Maquettiste.Engine;

namespace Maquettiste.Cli.Commands;

/// <summary>
/// <c>maquettiste pack remove &lt;name&gt; [--apply] [--format text|json]</c>: previews removing a pack (its folder
/// <c>.maquettiste/templates/&lt;name&gt;/</c>, its <c>packs.&lt;name&gt;</c> settings entry, its manifests and unit states); <c>--apply</c>
/// removes it through <see cref="GenerationService.DeletePackAsync"/>. The files the pack generated stay on disk, untracked; both forms
/// list them.
/// </summary>
internal static class PackRemoveCommand
{
    /// <summary>Runs the command.</summary>
    /// <param name="context">The global context.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>0 when removed (or previewed), 1 when there is no such pack or the settings save is refused, 3 when pack.json changed
    /// while the command ran, 4 for a refused path.</returns>
    public static async Task<int> RunAsync(GlobalContext context, CancellationToken ct)
    {
        context.Line.Expect("pack remove", 3, "--apply", "--format");
        var name = context.Line.Positionals[2];
        var json = context.Line.Choice("--format", "text", "text", "json") == "json";
        var apply = context.Line.Has("--apply");
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
                IReadOnlyList<string> files;
                IReadOnlyList<string> untracked;
                if (apply)
                {
                    var result = await service.DeletePackAsync(name, pack.Hash, ct, ChangeSource.Cli).ConfigureAwait(false);
                    switch (result.Outcome)
                    {
                        case SaveOutcome.Saved:
                            break;
                        case SaveOutcome.Conflict:
                            await context.Error.WriteLineAsync($"maquettiste: pack remove {name}: pack.json changed while the command ran, so nothing was removed. Run the command again.").ConfigureAwait(false);
                            return Program.ExitCodes.Conflicts;
                        case SaveOutcome.NotFound:
                            await context.Error.WriteLineAsync($"maquettiste: no pack named '{name}'.").ConfigureAwait(false);
                            return Program.ExitCodes.Invalid;
                        default:
                            foreach (var diagnostic in result.Diagnostics)
                                await context.Error.WriteLineAsync(DiagnosticOutput.Line(diagnostic)).ConfigureAwait(false);
                            await context.Error.WriteLineAsync($"maquettiste: pack remove {name}: the settings change is not valid, so nothing was removed.").ConfigureAwait(false);
                            return Program.ExitCodes.Invalid;
                    }

                    files = result.Files;
                    untracked = result.Untracked;
                    hasEntry = result.SettingsHash is not null;
                }
                else
                {
                    files = [.. pack.Files.Select(f => f.Path)];
                    untracked = [.. ((await service.GetPackOutputsAsync(name, ct).ConfigureAwait(false))?.Outputs ?? []).Select(o => o.Path)];
                }

                await ReportAsync(context, name, apply, json, files, untracked, hasEntry).ConfigureAwait(false);
                return Program.ExitCodes.Success;
            }
            catch (PackPathException ex)
            {
                await context.Error.WriteLineAsync("maquettiste: " + ex.Message).ConfigureAwait(false);
                return Program.ExitCodes.Internal;
            }
        }
    }

    private static async Task ReportAsync(GlobalContext context, string name, bool apply, bool json, IReadOnlyList<string> files,
        IReadOnlyList<string> untracked, bool settingsEntry)
    {
        if (json)
        {
            await context.Out.WriteLineAsync(DiagnosticOutput.ToJson(writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("pack", name);
                writer.WriteBoolean("applied", apply);
                writer.WritePropertyName("files");
                JsonSerializer.Serialize(writer, files, CliJson.Options);
                writer.WriteBoolean("settingsEntry", settingsEntry);
                writer.WritePropertyName("untracked");
                JsonSerializer.Serialize(writer, untracked, CliJson.Options);
                writer.WriteEndObject();
            })).ConfigureAwait(false);
        }
        else
        {
            var text = new StringBuilder();
            foreach (var file in files)
                text.Append(apply ? "deleted " : "delete ").Append(".maquettiste/templates/").Append(name).Append('/').Append(file).Append('\n');
            if (settingsEntry)
                text.Append(apply ? "removed " : "remove ").Append("packs.").Append(name).Append(" from .maquettiste/maquettiste.json\n");
            foreach (var path in untracked)
                text.Append("untracked ").Append(path).Append('\n');
            await context.Out.WriteAsync(text.ToString()).ConfigureAwait(false);
        }

        var count = untracked.Count.ToString(CultureInfo.InvariantCulture);
        var summary = $"pack remove {name}: {files.Count.ToString(CultureInfo.InvariantCulture)} pack {(files.Count == 1 ? "file" : "files")}; "
            + $"{count} generated {(untracked.Count == 1 ? "file stays" : "files stay")} on disk, no longer tracked";
        context.Info(apply ? summary + "." : summary + "; run with --apply to remove the pack.");
    }
}
