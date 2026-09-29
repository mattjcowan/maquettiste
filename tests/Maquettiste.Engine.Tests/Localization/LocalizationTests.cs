using System.Text.Json.Nodes;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Generation;
using Maquettiste.Engine.Localization;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Tests.Loading;
using Maquettiste.Engine.Validation;

namespace Maquettiste.Engine.Tests.Localization;

/// <summary>
/// The localization engine (reference-types-seeds-localization.md section 3) over the reference-data fixture, which declares
/// <c>en</c> (default), <c>fr</c> and <c>fr-CA</c> (falling back to <c>fr</c>) and has a French reference-data shard whose
/// fingerprints are placeholders (so its three texts start stale).
/// </summary>
public sealed class LocalizationTests
{
    private const string UnitOfMeasure = "01JBM9S346Q3D25VT4F5V37E3S";
    private const string KgRow = "01JBS3C4DWA7N36096Q14DR9GP";
    private const string Recipe = "01JRDE00000000000000000001";
    private const string RecipeName = "01JRDE00000000000000000012";
    private const string Package = "01JRDP00000000000000000001";
    private const string FrReference = ".maquettiste/model/locales/fr/_reference-data.json";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static LocalizationSettings Settings(string[] locales, Dictionary<string, IReadOnlyList<string>>? fallbacks = null, string[]? require = null) => new()
    {
        DefaultLocale = "en",
        Locales = locales,
        Fallbacks = fallbacks ?? [],
        Require = require ?? [],
    };

    [Theory]
    [InlineData("en", "en")]
    [InlineData("fr", "fr,en")]
    [InlineData("fr-CA", "fr-CA,fr,en")]
    [InlineData("de-CH", "de-CH,en")]
    [InlineData("pt-BR", "pt-BR,es,en")]
    [InlineData("zh-Hant-TW", "zh-Hant-TW,zh-Hant,zh,en")]
    public void Fallback_chain_is_the_locale_then_fallbacks_or_truncations_then_the_default(string locale, string chain)
    {
        var settings = Settings(["en", "fr", "fr-CA", "de-CH", "es", "pt", "pt-BR", "zh", "zh-Hant", "zh-Hant-TW"],
            new() { ["pt-BR"] = ["es"] });

        Assert.Equal(chain, string.Join(',', LocaleChains.Chain(settings, locale)));
        Assert.Equal(["en", "de-CH", "es", "fr", "fr-CA", "pt", "pt-BR", "zh", "zh-Hant", "zh-Hant-TW"], LocaleChains.Ordered(settings));
    }

    [Theory]
    [InlineData("zh_cn", "zh-CN")]
    [InlineData(" zh_hant_tw ", "zh-Hant-TW")]
    [InlineData("EN", "en")]
    [InlineData("fr-ca", "fr-CA")]
    [InlineData("es-419", "es-419")]
    [InlineData("de-CH-1996", "de-CH-1996")]
    public void Locale_tags_normalize_like_the_editor(string typed, string tag)
    {
        Assert.Equal(tag, LocaleChains.Normalize(typed));
        Assert.True(LocaleChains.IsLanguageTag(LocaleChains.Normalize(typed)));
    }

    [Fact]
    public void MQ7201_says_how_to_write_a_tag()
    {
        var findings = LocaleChains.Check(Settings(["en", "zh_CN", "not a tag"], null, ["entity"]));

        Assert.Contains(findings, f => f.Message == "The locale 'zh_CN' is not a BCP 47 language tag. Use 'zh-CN': language-REGION with a hyphen, such as zh-CN, or a language alone, such as fr.");
        Assert.Contains(findings, f => f.Message == "The locale 'not a tag' is not a BCP 47 language tag. Use language-REGION with a hyphen, such as zh-CN, or a language alone, such as fr.");
    }

    [Fact]
    public void Settings_findings_are_MQ7201()
    {
        var settings = Settings(["fr", "not a tag", "de"], new() { ["fr"] = ["de"], ["de"] = ["fr"], ["it"] = ["xx"] }, ["entity", "reference-row", "widget"]);

        var messages = LocaleChains.Check(settings).Select(f => f.Pointer).ToList();

        Assert.Contains("/localization/locales/1", messages);          // not BCP 47
        Assert.Contains("/localization/locales", messages);            // default not declared
        Assert.Contains("/localization/fallbacks/it", messages);       // undeclared locale with fallbacks
        Assert.Contains("/localization/fallbacks/it/0", messages);     // undeclared fallback
        Assert.Equal(2, messages.Count(m => m is "/localization/fallbacks/fr" or "/localization/fallbacks/de")); // the cycle, from both ends
        Assert.Contains("/localization/require/2", messages);          // unknown kind
        Assert.Empty(LocaleChains.Check(Settings(["en", "fr", "fr-CA"], new() { ["fr-CA"] = ["fr"] }, ["entity", "attribute", "enum-member", "reference-row"])));
    }

    private static async Task<(LoaderHarness Harness, ModelStore Store)> OpenAsync(Action<LoaderHarness>? prepare = null)
    {
        var harness = new LoaderHarness();
        harness.CopyFixture("models", "reference-data");
        prepare?.Invoke(harness);
        var store = new ModelStore(harness.Options, harness.Services() with { Validator = EngineServices.Create(harness.Options).Validator });
        await store.LoadAsync(Ct);
        return (harness, store);
    }

    private static void Write(LoaderHarness h, string modelPath, string text)
    {
        var full = h.Model(modelPath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text.ReplaceLineEndings("\n"));
    }

    private static async Task<IReadOnlyDictionary<string, string>> Hashes(ModelStore store, string locale) =>
        (await store.GetTranslationsAsync(locale, null, null, Ct)).ShardHashes;

    private static async Task Translate(ModelStore store, string locale, params TranslationEdit[] edits)
    {
        var result = await store.SaveTranslationsAsync(locale, edits, await Hashes(store, locale), ChangeSource.Editor, Ct);
        Assert.True(result.Outcome == SaveOutcome.Saved, result.Outcome + ": " + string.Join("; ", result.Diagnostics.Select(d => d.Rule + " " + d.Message)));
    }

    [Fact]
    public async Task Shards_load_and_completeness_is_reported_per_locale_and_shard()
    {
        var (h, store) = await OpenAsync();
        using var _ = h;
        await using var __ = store;
        var l10n = store.Current!.Localization;

        Assert.Equal(["en", "fr", "fr-CA"], l10n.Locales);
        Assert.Equal("Kilogramme", l10n.Text("fr", KgRow, "label"));
        Assert.Null(l10n.Text("fr-CA", KgRow, "label"));
        Assert.Equal("reference-data", l10n.Nodes[KgRow].Scope);
        Assert.Equal("reference-row", l10n.Nodes[KgRow].Kind);
        Assert.Equal("root", l10n.Nodes[Recipe].Scope);

        var report = await store.ValidateAsync(ValidationScope.All, Ct);
        var completeness = report.Diagnostics.Where(d => d.Rule is "MQ7204" or "MQ7206").Select(d => (d.Rule, d.FilePath)).ToList();
        Assert.Contains(("MQ7204", FrReference), completeness);
        Assert.Contains(("MQ7206", FrReference), completeness);
        Assert.Contains(("MQ7204", ".maquettiste/model/locales/fr-CA/_root.json"), completeness);
        Assert.Equal(5, completeness.Count); // per (locale, shard), never per node
        Assert.DoesNotContain(report.Diagnostics, d => d.Rule == "MQ7205"); // off unless turned on
        var row = Assert.Single(l10n.Completeness("fr"), c => c.Scope == "reference-data");
        Assert.Equal(3, row.Stale);
    }

    [Fact]
    public async Task Staleness_is_per_field_and_confirm_rewrites_one_fingerprint()
    {
        var (h, store) = await OpenAsync();
        using var _ = h;
        await using var __ = store;
        await Translate(store, "fr",
            new TranslationEdit(UnitOfMeasure, "displayName", "Unité de mesure"),
            new TranslationEdit(UnitOfMeasure, "description", "Unités dans lesquelles les quantités sont exprimées."));
        var node = store.Current!.Localization.Nodes[UnitOfMeasure];
        Assert.Equal(TranslationState.Translated, store.Current.Localization.StateOf("fr", node, "displayName"));
        Assert.Equal(TranslationState.Stale, store.Current.Localization.StateOf("fr", node, "pluralName")); // placeholder fingerprint

        // The default description changes: only the description goes stale.
        var document = store.Current.GetDocument(UnitOfMeasure)!;
        var json = JsonNode.Parse(document.Json.GetRawText())!.AsObject();
        json["description"] = "Units of quantity.";
        var saved = await store.SaveAsync(UnitOfMeasure, System.Text.Encoding.UTF8.GetBytes(json.ToJsonString()), document.Hash, ChangeSource.Editor, Ct);
        Assert.Equal(SaveOutcome.Saved, saved.Outcome);
        var l10n = store.Current.Localization;
        node = l10n.Nodes[UnitOfMeasure];
        Assert.Equal(TranslationState.Translated, l10n.StateOf("fr", node, "displayName"));
        Assert.Equal(TranslationState.Stale, l10n.StateOf("fr", node, "description"));
        var items = (await store.GetTranslationsAsync("fr", UnitOfMeasure, null, Ct)).Items;
        Assert.Equal("stale", Assert.Single(items, i => i.Id == UnitOfMeasure && i.Field == "description").State);
        Assert.Equal("fallback", Assert.Single((await store.GetTranslationsAsync("fr-CA", UnitOfMeasure, null, Ct)).Items,
            i => i.Id == UnitOfMeasure && i.Field == "displayName").State);

        await Translate(store, "fr", new TranslationEdit(UnitOfMeasure, "description", null, Confirm: true));
        l10n = store.Current.Localization;
        Assert.Equal(TranslationState.Translated, l10n.StateOf("fr", l10n.Nodes[UnitOfMeasure], "description"));
        Assert.Equal("Unités dans lesquelles les quantités sont exprimées.", l10n.Text("fr", UnitOfMeasure, "description"));
    }

    [Fact]
    public async Task A_save_with_a_changed_shard_is_a_conflict_and_writes_nothing()
    {
        var (h, store) = await OpenAsync();
        using var _ = h;
        await using var __ = store;
        var before = await Hashes(store, "fr");

        var result = await store.SaveTranslationsAsync("fr", [new TranslationEdit(KgRow, "label", "Kilo")],
            new Dictionary<string, string> { [FrReference] = "0000" }, ChangeSource.Editor, Ct);

        Assert.Equal(SaveOutcome.Conflict, result.Outcome);
        Assert.Equal(before[FrReference], result.ShardHashes[FrReference]);
        Assert.Equal("Kilogramme", store.Current!.Localization.Text("fr", KgRow, "label"));
        var invalid = await store.SaveTranslationsAsync("fr", [new TranslationEdit(KgRow, "displayName", "x")], before, ChangeSource.Editor, Ct);
        Assert.Equal(("MQ7203", SaveOutcome.Invalid), (Assert.Single(invalid.Diagnostics).Rule, invalid.Outcome));
    }

    [Fact]
    public async Task Entries_follow_a_move_a_package_rename_and_a_delete()
    {
        var (h, store) = await OpenAsync();
        using var _ = h;
        await using var __ = store;
        await Translate(store, "fr", new TranslationEdit(Recipe, "displayName", "Recette"), new TranslationEdit(RecipeName, "displayName", "Nom"));
        Assert.True(File.Exists(h.Model("model/locales/fr/_root.json")));

        var package = new JsonObject { ["kind"] = "package", ["id"] = Package, ["name"] = "Kitchen" };
        Assert.Equal(SaveOutcome.Saved, (await store.CreateAsync(System.Text.Encoding.UTF8.GetBytes(package.ToJsonString()), ChangeSource.Editor, Ct)).Outcome);
        await Edit(store, Recipe, n => n["package"] = Package);
        Assert.False(File.Exists(h.Model("model/locales/fr/_root.json"))); // emptied, so deleted
        Assert.Contains("\"scope\": \"" + Package + "\"", File.ReadAllText(h.Model("model/locales/fr/kitchen.json")), StringComparison.Ordinal);
        Assert.Equal("Recette", store.Current!.Localization.Text("fr", Recipe, "displayName"));
        Assert.Equal("Nom", store.Current.Localization.Text("fr", RecipeName, "displayName"));

        await Edit(store, Package, n => n["name"] = "Pantry");
        Assert.False(File.Exists(h.Model("model/locales/fr/kitchen.json")));
        Assert.True(File.Exists(h.Model("model/locales/fr/pantry.json")));
        var report = await store.ValidateAsync(ValidationScope.All, Ct);
        Assert.DoesNotContain(report.Diagnostics, d => d.Rule is "MQ7207" or "MQ7210");

        // Removing a sub-element drops its entries; deleting an element drops its entries and an emptied shard.
        await Edit(store, Recipe, n => n["attributes"]!.AsArray().RemoveAt(1));
        Assert.Null(store.Current!.Localization.Text("fr", RecipeName, "displayName"));
        Assert.Equal("Recette", store.Current.Localization.Text("fr", Recipe, "displayName"));
        const string spare = "01JRDP00000000000000000002";
        var created = new JsonObject { ["kind"] = "package", ["id"] = spare, ["name"] = "Spare" };
        Assert.Equal(SaveOutcome.Saved, (await store.CreateAsync(System.Text.Encoding.UTF8.GetBytes(created.ToJsonString()), ChangeSource.Editor, Ct)).Outcome);
        await Translate(store, "fr", new TranslationEdit(spare, "displayName", "Réserve"));
        Assert.True(File.Exists(h.Model("model/locales/fr/spare.json")));
        var deleted = await store.DeleteAsync(spare, store.Current!.GetDocument(spare)!.Hash, DeleteResolution.Refuse, ChangeSource.Editor, Ct);
        Assert.Equal(SaveOutcome.Saved, deleted.Outcome);
        Assert.False(File.Exists(h.Model("model/locales/fr/spare.json")));
        Assert.True(File.Exists(h.Model("model/locales/fr/pantry.json")));
    }

    private static async Task Edit(ModelStore store, string id, Action<JsonObject> change)
    {
        var document = store.Current!.GetDocument(id)!;
        var node = JsonNode.Parse(document.Json.GetRawText())!.AsObject();
        change(node);
        var result = await store.SaveAsync(id, System.Text.Encoding.UTF8.GetBytes(node.ToJsonString()), document.Hash, ChangeSource.Editor, Ct);
        Assert.True(result.Outcome == SaveOutcome.Saved, result.Outcome + ": " + string.Join("; ", result.Diagnostics.Select(d => d.Rule + " " + d.Message)));
    }

    [Fact]
    public async Task Shard_rules_report_misplaced_orphan_duplicate_and_missing_sidecar_entries()
    {
        var (h, store) = await OpenAsync(h =>
        {
            // de is undeclared (MQ7202); en is the default (MQ7202).
            Write(h, "model/locales/de/_root.json", Shard("de", "root", $"\"{Recipe}\": {{ \"displayName\": \"Rezept\" }}"));
            Write(h, "model/locales/en/_root.json", Shard("en", "root", $"\"{Recipe}\": {{ \"displayName\": \"Recipe\" }}"));
            // An orphan id, a label on an entity (MQ7203), a missing sidecar (MQ7208), a wrong shard (MQ7207), a duplicate (MQ7209).
            Write(h, "model/locales/fr/_root.json", Shard("fr", "root",
                $"\"01JRDE0000000000000000009Z\": {{ \"displayName\": \"Rien\" }},\n    \"{Recipe}\": {{ \"label\": \"Recette\", \"description\": {{ \"file\": \"entities/recipe.md\" }} }},\n    \"{UnitOfMeasure}\": {{ \"displayName\": \"Unité\" }},\n    \"01JRDA00000000000000000001\": {{ \"displayName\": \"Allergène\" }}"));
            // A shard whose scope is not its path (MQ7210) and a shard outside the locales folder (MQ1005).
            Write(h, "model/locales/fr/misnamed.json", Shard("fr", "root", $"\"{RecipeName}\": {{ \"displayName\": \"Nom\" }}"));
            Write(h, "model/entities/stray.json", Shard("fr", "reference-data", $"\"{KgRow}\": {{ \"label\": \"Kilo\" }}"));
        });
        using var _ = h;
        await using var __ = store;

        var report = await store.ValidateAsync(ValidationScope.All, Ct);
        var rules = report.Diagnostics.Select(d => d.Rule).ToHashSet(StringComparer.Ordinal);

        foreach (var rule in new[] { "MQ1005", "MQ7202", "MQ7203", "MQ7207", "MQ7208", "MQ7209", "MQ7210" })
            Assert.Contains(rule, rules);
        Assert.Equal(2, report.Diagnostics.Count(d => d.Rule == "MQ7202"));
        Assert.Equal(2, report.Diagnostics.Count(d => d.Rule == "MQ7203"));
        Assert.Equal("Kilo", store.Current!.Localization.Text("fr", KgRow, "label")); // model/entities/stray.json is ordinally first, so it wins
        Assert.Equal("Nom", store.Current.Localization.Text("fr", RecipeName, "displayName")); // a misplaced shard still applies
    }

    [Fact]
    public async Task Missing_translations_per_node_and_plural_names_on_to_one_ends_are_reported()
    {
        var (h, store) = await OpenAsync(h =>
        {
            var path = h.Model("maquettiste.json");
            var node = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            node["validation"] = new JsonObject { ["rules"] = new JsonObject { ["MQ7205"] = "warning" } };
            node["localization"]!["require"] = new JsonArray("reference-row");
            File.WriteAllText(path, node.ToJsonString());
        });
        using var _ = h;
        await using var __ = store;
        var report = await store.ValidateAsync(ValidationScope.All, Ct);

        var missing = report.Diagnostics.Where(d => d.Rule == "MQ7205").ToList();
        Assert.NotEmpty(missing);
        Assert.All(missing, d => Assert.Equal(DiagnosticSeverity.Warning, d.Severity));
        Assert.All(missing, d => Assert.Equal("reference-row", store.Current!.Localization.Nodes[d.ElementId!].Kind));
        Assert.DoesNotContain(missing, d => d.ElementId == KgRow && d.Message.Contains("'fr' label", StringComparison.Ordinal));

        var relation = store.Current!.All<Relation>().First(r => r.Ends.Any(e => e.Max == MaxCardinality.One));
        var index = relation.Ends.ToList().FindIndex(e => e.Max == MaxCardinality.One);
        await Edit(store, relation.Id, n => n["ends"]![index]!["pluralName"] = "Things");
        report = await store.ValidateAsync(ValidationScope.All, Ct);
        Assert.Contains(report.Diagnostics, d => d.Rule == "MQ7211" && d.JsonPointer == "/ends/" + index + "/pluralName");
    }

    [Fact]
    public async Task A_translated_description_sidecar_loads_and_moves_with_its_element_file()
    {
        var (h, store) = await OpenAsync(h =>
        {
            Write(h, "model/locales/fr/_reference-data.json", Shard("fr", "reference-data",
                $"\"{UnitOfMeasure}\": {{ \"description\": {{ \"file\": \"reference-types/unit-of-measure.md\" }} }}"));
            Write(h, "model/locales/fr/reference-types/unit-of-measure.md", "Unités de **mesure**.\n");
        });
        using var _ = h;
        await using var __ = store;
        Assert.Equal("Unités de **mesure**.\n", store.Current!.Localization.Text("fr", UnitOfMeasure, "description"));
        var before = store.Current.Localization.OwnerHash("fr", UnitOfMeasure);

        await Edit(store, UnitOfMeasure, n => n["name"] = "Measure");

        Assert.False(File.Exists(h.Model("model/locales/fr/reference-types/unit-of-measure.md")));
        Assert.True(File.Exists(h.Model("model/locales/fr/reference-types/measure.md")));
        Assert.Contains("\"file\": \"reference-types/measure.md\"", File.ReadAllText(h.Model("model/locales/fr/_reference-data.json")), StringComparison.Ordinal);
        Assert.Equal("Unités de **mesure**.\n", store.Current!.Localization.Text("fr", UnitOfMeasure, "description"));
        Assert.NotEqual(before, store.Current.Localization.OwnerHash("fr", UnitOfMeasure)); // the l: key follows the entry and its sidecar
        Assert.DoesNotContain((await store.ValidateAsync(ValidationScope.All, Ct)).Diagnostics, d => d.Rule == "MQ7208");
    }

    private static string Shard(string locale, string scope, string entries) =>
        "{\n  \"kind\": \"locale-shard\",\n  \"locale\": \"" + locale + "\",\n  \"scope\": \"" + scope + "\",\n  \"entries\": {\n    " + entries + "\n  }\n}\n";
}
