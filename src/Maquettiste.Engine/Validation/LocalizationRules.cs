using System.Globalization;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Json;
using Maquettiste.Engine.Localization;
using Maquettiste.Engine.Model;

namespace Maquettiste.Engine.Validation;

/// <summary>
/// The localization rules MQ7201 to MQ7211 (reference-types-seeds-localization.md section 3.6), run on whole-model validation.
/// Completeness and staleness are reported per (locale, shard); MQ7205 (one per missing field) runs only when
/// <c>validation.rules</c> gives it a severity.
/// </summary>
internal static class LocalizationRules
{
    /// <summary>Checks the settings, the shards and their entries.</summary>
    /// <param name="model">The snapshot.</param>
    /// <returns>The diagnostics.</returns>
    public static IReadOnlyList<Diagnostic> Check(ModelSnapshot model)
    {
        var diagnostics = new List<Diagnostic>();
        var l10n = model.Localization;

        // MQ7211 on the default texts: a to-one end that states a plural name (independent of any locale being declared).
        foreach (var document in model.Documents.Where(d => d.Element is Relation))
        {
            var relation = (Relation)document.Element;
            for (var i = 0; i < relation.Ends.Count; i++)
            {
                if (relation.Ends[i] is { Max: MaxCardinality.One, PluralName: not null } end)
                    diagnostics.Add(RuleCatalog.Create("MQ7211", $"The to-one end '{end.Role}' of relation '{relation.Name}' has a plural name, which is never read.",
                        end.Id, document.Path, "/ends/" + Str(i) + "/pluralName"));
            }
        }

        if (l10n.Settings is not { } settings)
            return diagnostics;
        var settingsPath = ReferenceDataRules.SettingsPath(model);
        foreach (var (message, pointer) in LocaleChains.Check(settings))
            diagnostics.Add(RuleCatalog.Create("MQ7201", message, null, settingsPath, pointer));

        foreach (var (shard, reason) in l10n.Ignored)
            diagnostics.Add(RuleCatalog.Create("MQ7202", reason, null, shard.Path, "/locale"));

        foreach (var shard in l10n.Shards.Where(s => !l10n.Ignored.Any(i => ReferenceEquals(i.Shard, s))))
        {
            var folder = shard.FolderLocale;
            if (folder.Length > 0 && !string.Equals(folder, shard.Shard.Locale, StringComparison.Ordinal))
                diagnostics.Add(RuleCatalog.Create("MQ7210", $"The shard declares the locale '{shard.Shard.Locale}' but sits in the folder of '{folder}'; it applies to '{shard.Shard.Locale}'.",
                    null, shard.Path, "/locale"));
            else if (folder.Length > 0)
            {
                var derived = l10n.ShardFileOf(shard.Shard.Scope) is { } file ? LocalizationIndex.PathOf(folder, file) : null;
                if (derived is null)
                    diagnostics.Add(RuleCatalog.Create("MQ7210", $"The shard's scope '{shard.Shard.Scope}' names no package of the model.", null, shard.Path, "/scope"));
                else if (!string.Equals(derived, shard.ModelPath, StringComparison.Ordinal))
                    diagnostics.Add(RuleCatalog.Create("MQ7210", $"The shard's scope '{shard.Shard.Scope}' belongs in {derived}; it applies to its declared scope.", null, shard.Path, "/scope"));
            }

            foreach (var (file, sidecar) in shard.Sidecars)
            {
                if (sidecar.Text is not null)
                    continue;
                foreach (var (id, _) in shard.Shard.Entries.Where(e => e.Value.Description?.File == file).OrderBy(e => e.Key, StringComparer.Ordinal))
                    diagnostics.Add(RuleCatalog.Create("MQ7208", $"The description sidecar '{file}' does not exist.", id, shard.Path, Pointer(id, "description")));
            }
        }

        foreach (var (locale, id, kept, other) in l10n.Duplicates)
            diagnostics.Add(RuleCatalog.Create("MQ7209", $"The node {id} has entries in two '{locale}' shards; the one in {kept.Path} applies.", id, other.Path, "/entries/" + id));

        var nodes = l10n.Nodes;
        foreach (var locale in l10n.Locales.Skip(1))
        {
            foreach (var (id, entry, shard) in l10n.EntriesOf(locale))
            {
                if (!nodes.TryGetValue(id, out var node))
                {
                    diagnostics.Add(RuleCatalog.Create("MQ7203", $"The '{locale}' entry {id} names no localizable node of the model.", id, shard.Path, "/entries/" + id));
                    continue;
                }

                foreach (var field in LocalizationIndex.Fields.Where(f => LocalizationIndex.Has(entry, f) && !node.Allows(f)))
                {
                    if (field == LocalizationIndex.PluralNameField && node.Kind == "end")
                        diagnostics.Add(RuleCatalog.Create("MQ7211", $"The '{locale}' plural name of the to-one end {id} is never read.", id, shard.Path, Pointer(id, field)));
                    else
                        diagnostics.Add(RuleCatalog.Create("MQ7203", $"The '{locale}' entry {id} has a '{field}', which a {node.Kind} does not have.", id, shard.Path, Pointer(id, field)));
                }

                if (!string.Equals(shard.Shard.Scope, node.Scope, StringComparison.Ordinal))
                    diagnostics.Add(RuleCatalog.Create("MQ7207", $"The '{locale}' entry {id} belongs in {l10n.ShardPath(locale, node.Scope)}; it still applies.", id, shard.Path, "/entries/" + id));
            }
        }

        // Completeness and staleness: one diagnostic per (locale, shard).
        foreach (var row in l10n.Completeness())
        {
            var path = RepoPath(model, row.ShardPath);
            if (row.Missing + row.Stale > 0)
                diagnostics.Add(RuleCatalog.Create("MQ7204", string.Create(CultureInfo.InvariantCulture,
                    $"'{row.Locale}' is incomplete in this shard: {row.Missing} of {row.Expected} texts missing, {row.Stale} stale."), null, path, ""));
            if (row.Stale > 0)
                diagnostics.Add(RuleCatalog.Create("MQ7206", string.Create(CultureInfo.InvariantCulture,
                    $"{row.Stale} '{row.Locale}' translations in this shard were made from a default text that has changed since."), null, path, ""));
        }

        if (model.Settings.Validation.Rules.TryGetValue("MQ7205", out var setting) && setting is "error" or "warning" or "info")
        {
            var require = settings.Require.Count == 0 ? null : settings.Require.ToHashSet(StringComparer.Ordinal);
            foreach (var locale in l10n.Locales.Skip(1))
            {
                foreach (var node in nodes.Values.Where(n => require is null || require.Contains(n.Kind)).OrderBy(n => n.Id, StringComparer.Ordinal))
                {
                    foreach (var field in node.ExpectedFields().Where(f => l10n.StateOf(locale, node, f) == TranslationState.Missing))
                        diagnostics.Add(RuleCatalog.Create("MQ7205", $"The {node.Kind} {node.Id} has no '{locale}' {field}.", node.Id,
                            RepoPath(model, l10n.ShardPath(locale, node.Scope)), Pointer(node.Id, field)));
                }
            }
        }

        return diagnostics;
    }

    /// <summary>A shard's repo path: the prefix of an existing shard, else of the settings file.</summary>
    private static string RepoPath(ModelSnapshot model, string modelPath)
    {
        var settings = ReferenceDataRules.SettingsPath(model);
        var cut = settings.LastIndexOf('/');
        return cut < 0 ? modelPath : settings[..cut] + "/" + modelPath;
    }

    private static string Pointer(string id, string field) => "/entries/" + id + "/" + JsonPointer.Escape(field);

    private static string Str(int i) => i.ToString(CultureInfo.InvariantCulture);
}
