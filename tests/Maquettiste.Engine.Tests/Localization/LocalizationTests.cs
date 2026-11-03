using System.Text.Json.Nodes;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Generation;
using Maquettiste.Engine.Localization;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Tests.Loading;
using Maquettiste.Engine.Validation;
using Kit = Maquettiste.Engine.Tests.Localization.PropertyTextsKit;

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
    internal const string Package = "01JRDP00000000000000000001";
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

    [Fact]
    public async Task Translatable_properties_join_rows_status_and_pruning()
    {
        var (h, store) = await Kit.OpenAsync();
        using var _ = h;
        await using var __ = store;
        var l10n = store.Current!.Localization;
        var recipe = l10n.Nodes[Recipe];

        // Only the marked property is a field, after the standard ones; the plain one is no field at all.
        Assert.Equal(["footer"], recipe.TranslatableProperties);
        Assert.Equal(Kit.RecipeFooter, recipe.Source("properties/footer"));
        Assert.Null(recipe.Source("properties/note"));
        Assert.Equal(["displayName", "pluralName", "description", "properties/footer"], LocalizationIndex.FieldsOf(recipe));
        Assert.Equal(Kit.NameFooter, l10n.Nodes[RecipeName].Source("properties/footer"));
        Assert.True(LocalizationIndex.IsField("properties/footer"));
        Assert.False(LocalizationIndex.IsField("properties/9lives"));
        Assert.False(LocalizationIndex.IsField("properties/"));

        var row = Assert.Single((await store.GetTranslationsAsync("fr", Recipe, null, Ct)).Items, i => i.Id == Recipe && i.Field == "properties/footer");
        Assert.Equal((Kit.RecipeFooter, (string?)null, "missing", Kit.FrRoot), (row.Source, row.Value, row.State, row.ShardPath));
        var before = await RootStatus(store, "fr");

        await Kit.Translate(store, "fr", new TranslationEdit(Recipe, "properties/footer", "Merci d'avoir cuisiné."),
            new TranslationEdit(RecipeName, "properties/footer", "Affiché sous le nom."));

        // The shard holds the texts in a nested properties object, their fingerprints under src as properties/<name>.
        var shardText = File.ReadAllText(h.Model("model/locales/fr/_root.json"));
        using (var parsed = System.Text.Json.JsonDocument.Parse(shardText))
            Assert.Equal(Engine.Json.PrecheckVerdict.Valid, new Engine.Json.SchemaRegistry().Precheck("locale.json", parsed.RootElement)); // no slow path
        var entry = JsonNode.Parse(shardText)!["entries"]![Recipe]!.AsObject();
        Assert.Equal(["properties", "src"], entry.Select(p => p.Key));
        Assert.Equal("Merci d'avoir cuisiné.", (string?)entry["properties"]!["footer"]);
        Assert.Equal(LocalizationIndex.SourceHash(Kit.RecipeFooter), (string?)entry["src"]!["properties/footer"]);
        l10n = store.Current!.Localization;
        Assert.Equal("Merci d'avoir cuisiné.", l10n.Text("fr", Recipe, "properties/footer"));
        Assert.Equal(TranslationState.Translated, l10n.StateOf("fr", l10n.Nodes[Recipe], "properties/footer"));
        var fallback = Assert.Single((await store.GetTranslationsAsync("fr-CA", Recipe, null, Ct)).Items, i => i.Id == Recipe && i.Field == "properties/footer");
        Assert.Equal(("fallback", "Merci d'avoir cuisiné."), (fallback.State, fallback.Effective));
        var after = await RootStatus(store, "fr");
        Assert.Equal((before.Expected, before.Translated + 2, before.Missing - 2), (after.Expected, after.Translated, after.Missing));

        // A plain property, or a name that is not an identifier, is no translatable field.
        var hashes = (await store.GetTranslationsAsync("fr", null, null, Ct)).ShardHashes;
        foreach (var field in new[] { "properties/note", "properties/9lives" })
        {
            var refused = await store.SaveTranslationsAsync("fr", [new TranslationEdit(Recipe, field, "x")], hashes, ChangeSource.Editor, Ct);
            Assert.Equal((SaveOutcome.Invalid, "MQ7203"), (refused.Outcome, Assert.Single(refused.Diagnostics).Rule));
        }

        // The default text changes: the translation goes stale.
        await Kit.Edit(store, Recipe, n => n["properties"]!["footer"] = "Thanks for cooking.");
        Assert.Equal("stale", Assert.Single((await store.GetTranslationsAsync("fr", Recipe, null, Ct)).Items, i => i.Id == Recipe && i.Field == "properties/footer").State);
        Assert.Contains((await store.ValidateAsync(ValidationScope.All, Ct)).Diagnostics, d => d.Rule == "MQ7206" && d.FilePath == Kit.FrRoot);

        // The mark goes: the texts are orphans (MQ7203 at the property), and pruning drops them with the empty entries.
        var extension = store.Current!.Extensions.Single(e => e.Path.EndsWith("texts.json", StringComparison.Ordinal));
        Assert.Equal(SaveOutcome.Saved, (await store.WriteExtensionFileAsync("texts.json", Kit.Extension(translatable: false), extension.Hash, Ct)).Outcome);
        var orphans = (await store.ValidateAsync(ValidationScope.All, Ct)).Diagnostics.Where(d => d.Rule == "MQ7203").Select(d => d.JsonPointer).ToList();
        Assert.Contains("/entries/" + Recipe + "/properties/footer", orphans);
        var pruned = await store.PruneTranslationsAsync(false, ChangeSource.Editor, Ct);
        Assert.True(pruned.Applied);
        Assert.Equal([("fr", Recipe, "properties/footer"), ("fr", RecipeName, "properties/footer")],
            pruned.Orphans.Select(o => (o.Locale, o.Id, o.Field ?? "")).Order());
        Assert.False(File.Exists(h.Model("model/locales/fr/_root.json")));
    }

    internal const string Notice = "01JRDC00000000000000000001";
    internal const string Message = "01JRDC00000000000000000002";
    internal const string Subject = "01JRDM00000000000000000001";
    internal const string Body = "01JRDM00000000000000000002";
    private const string FrKitchen = ".maquettiste/model/locales/fr/kitchen.json";

    private static string NoticeKind(bool translatable = true) =>
        "{ \"type\": \"notice\", \"properties\": { \"title\": { \"type\": \"string\"" + (translatable ? ", \"x-translatable\": true" : "")
        + " }, \"code\": { \"type\": \"string\" } } }";

    /// <summary>The kit with package Kitchen, the custom kinds notice (a translatable title) and message (parts as members with a
    /// translatable text), the notice closed and the message invoice_sent with parts subject and body, all in Kitchen.</summary>
    internal static Task<(LoaderHarness Harness, ModelStore Store)> OpenCustomAsync() => Kit.OpenAsync(h =>
    {
        Kit.Write(h, "model/packages/kitchen.json", $$"""{ "kind": "package", "id": "{{Package}}", "name": "Kitchen" }""");
        Kit.Write(h, "extensions/kinds/notice.json", NoticeKind());
        Kit.Write(h, "extensions/kinds/message.json", CustomKinds.CustomFixture.MessageKind);
        Kit.Write(h, "model/custom/notice/closed.json", $$"""
            { "kind": "custom", "id": "{{Notice}}", "name": "closed", "package": "{{Package}}", "type": "notice",
              "content": { "code": "c1", "title": "Closed today." } }
            """);
        Kit.Write(h, "model/custom/message/invoice-sent.json", $$"""
            { "kind": "custom", "id": "{{Message}}", "name": "invoice_sent", "package": "{{Package}}", "type": "message",
              "content": { "parts": [ { "id": "{{Subject}}", "name": "subject", "text": "Your invoice" }, { "id": "{{Body}}", "name": "body", "text": "Thank you." } ] } }
            """);
    });

    private static async Task<ShardStatus> KitchenStatus(ModelStore store, string locale) =>
        Assert.Single(Assert.Single((await store.GetLocalizationStatusAsync(Ct)).Locales, l => l.Locale == locale).Shards,
            s => s.Shard.EndsWith("/kitchen.json", StringComparison.Ordinal));

    [Fact]
    public async Task Content_texts_join_rows_status_and_pruning()
    {
        var (h, store) = await OpenCustomAsync();
        using var _ = h;
        await using var __ = store;
        Assert.DoesNotContain((await store.ValidateAsync(ValidationScope.All, Ct)).Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        var l10n = store.Current!.Localization;
        var notice = l10n.Nodes[Notice];

        // Only the marked content key is a field, after the standard ones; the plain one is no field at all.
        Assert.Equal(["title"], notice.TranslatableContent);
        Assert.Equal("Closed today.", notice.Source("content/title"));
        Assert.Null(notice.Source("content/code"));
        Assert.Equal(["displayName", "pluralName", "description", "content/title"], LocalizationIndex.FieldsOf(notice));
        Assert.Equal(Package, notice.Scope);
        Assert.True(LocalizationIndex.IsField("content/title"));
        Assert.False(LocalizationIndex.IsField("content/9lives"));
        Assert.False(LocalizationIndex.IsField("content/"));

        var row = Assert.Single((await store.GetTranslationsAsync("fr", Notice, null, Ct)).Items, i => i.Id == Notice && i.Field == "content/title");
        Assert.Equal(("Closed today.", (string?)null, "missing", FrKitchen), (row.Source, row.Value, row.State, row.ShardPath));
        var before = await KitchenStatus(store, "fr");
        var ownerHash = l10n.OwnerHash("fr", Notice);

        await Kit.Translate(store, "fr", new TranslationEdit(Notice, "content/title", "Fermé aujourd'hui."));

        // The shard holds the text in a nested content object, its fingerprint under src as content/<name>.
        var shardText = File.ReadAllText(h.Model("model/locales/fr/kitchen.json"));
        using (var parsed = System.Text.Json.JsonDocument.Parse(shardText))
            Assert.Equal(Engine.Json.PrecheckVerdict.Valid, new Engine.Json.SchemaRegistry().Precheck("locale.json", parsed.RootElement));
        var entry = JsonNode.Parse(shardText)!["entries"]![Notice]!.AsObject();
        Assert.Equal(["content", "src"], entry.Select(p => p.Key));
        Assert.Equal("Fermé aujourd'hui.", (string?)entry["content"]!["title"]);
        Assert.Equal(LocalizationIndex.SourceHash("Closed today."), (string?)entry["src"]!["content/title"]);
        l10n = store.Current!.Localization;
        Assert.Equal("Fermé aujourd'hui.", l10n.Text("fr", Notice, "content/title"));
        Assert.Equal(TranslationState.Translated, l10n.StateOf("fr", l10n.Nodes[Notice], "content/title"));
        Assert.NotEqual(ownerHash, l10n.OwnerHash("fr", Notice));
        var after = await KitchenStatus(store, "fr");
        Assert.Equal((before.Expected, before.Translated + 1, before.Missing - 1), (after.Expected, after.Translated, after.Missing));

        // A plain content key, or a name that is not an identifier, is no translatable field.
        var hashes = (await store.GetTranslationsAsync("fr", null, null, Ct)).ShardHashes;
        foreach (var field in new[] { "content/code", "content/9lives" })
        {
            var refused = await store.SaveTranslationsAsync("fr", [new TranslationEdit(Notice, field, "x")], hashes, ChangeSource.Editor, Ct);
            Assert.Equal((SaveOutcome.Invalid, "MQ7203"), (refused.Outcome, Assert.Single(refused.Diagnostics).Rule));
        }

        // The default text changes: the translation goes stale.
        await Kit.Edit(store, Notice, n => n["content"]!["title"] = "Closed until noon.");
        Assert.Equal("stale", Assert.Single((await store.GetTranslationsAsync("fr", Notice, null, Ct)).Items, i => i.Field == "content/title").State);

        // The mark goes: the text is an orphan (MQ7203 at the content key), and pruning drops it with the empty entry.
        Kit.Write(h, "extensions/kinds/notice.json", NoticeKind(translatable: false));
        await store.RefreshAsync([h.Model("extensions/kinds/notice.json")], Ct);
        Assert.Empty(store.Current!.Localization.Nodes[Notice].TranslatableContent);
        var orphans = (await store.ValidateAsync(ValidationScope.All, Ct)).Diagnostics.Where(d => d.Rule == "MQ7203").Select(d => d.JsonPointer).ToList();
        Assert.Contains("/entries/" + Notice + "/content/title", orphans);
        var pruned = await store.PruneTranslationsAsync(false, ChangeSource.Editor, Ct);
        Assert.True(pruned.Applied);
        Assert.Equal([("fr", Notice, "content/title")], pruned.Orphans.Select(o => (o.Locale, o.Id, o.Field ?? "")));
        Assert.False(File.Exists(h.Model("model/locales/fr/kitchen.json")));
    }

    [Fact]
    public async Task Member_texts_are_member_nodes()
    {
        var (h, store) = await OpenCustomAsync();
        using var _ = h;
        await using var __ = store;
        var l10n = store.Current!.Localization;

        // Each part is a member node in its owner's shard that translates its text only; the message itself has no content field.
        var subject = l10n.Nodes[Subject];
        Assert.Equal(("member", Message, Package), (subject.Kind, subject.OwnerId, subject.Scope));
        Assert.Equal(["content/text"], LocalizationIndex.FieldsOf(subject));
        Assert.Equal(["content/text"], subject.ExpectedFields());
        Assert.Equal("Your invoice", subject.Source("content/text"));
        Assert.False(subject.Allows("description"));
        Assert.DoesNotContain(LocalizationIndex.FieldsOf(l10n.Nodes[Message]), f => f.StartsWith("content/", StringComparison.Ordinal));

        var rows = (await store.GetTranslationsAsync("fr", Message, null, Ct)).Items.Where(i => i.Field == "content/text").ToList();
        Assert.Equal([(Subject, "Your invoice"), (Body, "Thank you.")], rows.Select(r => (r.Id, r.Source ?? "")).OrderBy(r => r.Item1, StringComparer.Ordinal));
        var before = await KitchenStatus(store, "fr");
        var ownerHash = l10n.OwnerHash("fr", Message);

        await Kit.Translate(store, "fr", new TranslationEdit(Subject, "content/text", "Votre facture"), new TranslationEdit(Body, "content/text", "Merci."));

        var entries = JsonNode.Parse(File.ReadAllText(h.Model("model/locales/fr/kitchen.json")))!["entries"]!;
        Assert.Equal("Votre facture", (string?)entries[Subject]!["content"]!["text"]);
        l10n = store.Current!.Localization;
        Assert.Equal("Merci.", l10n.Text("fr", Body, "content/text"));
        Assert.NotEqual(ownerHash, l10n.OwnerHash("fr", Message)); // the l: key of the owner covers its members
        var after = await KitchenStatus(store, "fr");
        Assert.Equal((before.Expected, before.Translated + 2), (after.Expected, after.Translated));

        // A member has no description to translate.
        var hashes = (await store.GetTranslationsAsync("fr", null, null, Ct)).ShardHashes;
        var refused = await store.SaveTranslationsAsync("fr", [new TranslationEdit(Subject, "description", "x")], hashes, ChangeSource.Editor, Ct);
        Assert.Equal(SaveOutcome.Invalid, refused.Outcome);
        Assert.DoesNotContain((await store.ValidateAsync(ValidationScope.All, Ct)).Diagnostics, d => d.Rule is "MQ7203" or "MQ7207");
    }

    [Fact]
    public async Task An_inherited_default_text_is_not_translated()
    {
        const string Stereotype = "01JRDV00000000000000000001";
        var (h, store) = await Kit.OpenAsync(h =>
        {
            Kit.Write(h, "model/vocabularies/stereotypes/branded.json", $$"""
                {
                  "kind": "stereotype",
                  "id": "{{Stereotype}}",
                  "key": "branded",
                  "name": "Branded",
                  "appliesTo": ["entity"],
                  "parameters": { "properties": { "slogan": { "type": "string", "x-translatable": true } } },
                  "defaultProperties": { "slogan": "Fresh every day." }
                }
                """);
            Kit.EditFile(h, "model/entities/ingredient.json", n => n["stereotypes"] = new JsonArray("branded"));
            Kit.EditFile(h, "model/entities/recipe.json", n =>
            {
                n["stereotypes"] = new JsonArray("branded");
                n["properties"]!["slogan"] = "Cooked with care.";
            });
        });
        using var _ = h;
        await using var __ = store;
        var l10n = store.Current!.Localization;

        // Ingredient only inherits the stereotype's default: the property is translatable there, but it has no text of its own.
        var ingredient = l10n.Nodes[Kit.Ingredient];
        Assert.Contains("slogan", ingredient.TranslatableProperties);
        Assert.Empty(ingredient.PropertyTexts);
        Assert.DoesNotContain("properties/slogan", ingredient.ExpectedFields());
        Assert.DoesNotContain((await store.GetTranslationsAsync("fr", Kit.Ingredient, null, Ct)).Items, i => i.Field == "properties/slogan");
        Assert.Null(l10n.Nodes[Stereotype].Source("properties/slogan")); // nor is the default the stereotype's own property

        // Recipe states its own text: that one is translated.
        Assert.Equal("Cooked with care.", l10n.Nodes[Recipe].Source("properties/slogan"));
        Assert.Single((await store.GetTranslationsAsync("fr", Recipe, null, Ct)).Items, i => i.Field == "properties/slogan");
        Assert.DoesNotContain((await store.ValidateAsync(ValidationScope.All, Ct)).Diagnostics, d => d.Rule is "MQ7212" or "MQ7213");
    }

    [Fact]
    public async Task Completeness_and_owner_hashes_cover_property_fields()
    {
        var (h, store) = await Kit.OpenAsync(h => Kit.EditFile(h, "model/entities/recipe.json", n => n["properties"]!.AsObject().Remove("footer")));
        using var _ = h;
        await using var __ = store;
        var expected = (await RootStatus(store, "fr")).Expected;

        // A property text the default states is one more expected field of its shard.
        await Kit.Edit(store, Recipe, n => n["properties"]!["footer"] = Kit.RecipeFooter);
        Assert.Equal(expected + 1, (await RootStatus(store, "fr")).Expected);
        var row = Assert.Single(store.Current!.Localization.Completeness("fr"), c => c.Scope == "root");
        Assert.Equal(expected + 1, row.Expected);

        // The l: hash of the owner follows the property text, and an entry without one keeps the hash it had.
        await Kit.Translate(store, "fr", new TranslationEdit(Recipe, "displayName", "Recette"));
        var standard = store.Current!.Localization.OwnerHash("fr", Recipe);
        await Kit.Translate(store, "fr", new TranslationEdit(Recipe, "properties/footer", "Merci."));
        var withProperty = store.Current!.Localization.OwnerHash("fr", Recipe);
        Assert.NotEqual(standard, withProperty);
        Assert.Equal(row.Translated + 2, Assert.Single(store.Current.Localization.Completeness("fr"), c => c.Scope == "root").Translated);
        await Kit.Translate(store, "fr", new TranslationEdit(Recipe, "properties/footer", "Merci beaucoup."));
        Assert.NotEqual(withProperty, store.Current!.Localization.OwnerHash("fr", Recipe));
        await Kit.Translate(store, "fr", new TranslationEdit(Recipe, "properties/footer", null));
        Assert.Equal(standard, store.Current!.Localization.OwnerHash("fr", Recipe));
    }

    [Fact]
    public async Task A_default_locale_change_moves_property_texts_both_ways()
    {
        var (h, store) = await Kit.OpenAsync();
        using var _ = h;
        await using var __ = store;
        await Kit.Translate(store, "fr", new TranslationEdit(Recipe, "properties/footer", "Merci d'avoir cuisiné."));

        var result = await store.ChangeDefaultLocaleAsync("fr", false, ChangeSource.Editor, Ct);

        Assert.Equal(SaveOutcome.Saved, result!.Outcome);
        Assert.Contains(new DefaultLocaleMove(Recipe, "properties/footer", Kit.RecipeFooter, "Merci d'avoir cuisiné."), result.Moves);
        Assert.Equal("Merci d'avoir cuisiné.", (string?)JsonNode.Parse(store.Current!.GetDocument(Recipe)!.Json.GetRawText())!["properties"]!["footer"]);
        Assert.Equal(Kit.RecipeFooter, store.Current.Localization.Text("en", Recipe, "properties/footer"));
        Assert.Equal(TranslationState.Translated, store.Current.Localization.StateOf("en", store.Current.Localization.Nodes[Recipe], "properties/footer"));
    }

    [Fact]
    public async Task Operation_members_outcomes_and_errors_are_localizable_in_the_operations_package_shard()
    {
        const string prepare = "01JRDQ00000000000000000001";
        const string servings = "01JRDQ00000000000000000002";
        const string done = "01JRDQ00000000000000000003";
        const string portion = "01JRDQ00000000000000000004";
        const string tooMany = "01JRDQ00000000000000000005";
        const string taste = "01JRDQ00000000000000000006";
        var (h, store) = await OpenAsync();
        using var _ = h;
        await using var __ = store;
        await Create(store, $$"""{ "kind": "package", "id": "{{Package}}", "name": "Kitchen" }""");
        // prepare sits in Kitchen; taste has no package, and its subject's package is never consulted.
        await Create(store, $$"""
            { "kind": "operation", "id": "{{prepare}}", "name": "prepare", "package": "{{Package}}", "subject": "{{Recipe}}", "effect": "change",
              "input": [ { "id": "{{servings}}", "name": "servings", "type": "int32", "description": "How many plates." } ],
              "outcomes": [ { "id": "{{done}}", "name": "done", "returns": { "members": [ { "id": "{{portion}}", "name": "portion", "type": "string" } ] } } ],
              "errors": [ { "id": "{{tooMany}}", "name": "too_many", "displayName": "Too many", "input": "{{servings}}" } ] }
            """);
        await Edit(store, Recipe, n => n["package"] = Package);
        await Create(store, $$"""
            { "kind": "operation", "id": "{{taste}}", "name": "taste", "subject": "{{Recipe}}", "effect": "read",
              "outcomes": [ { "id": "01JRDQ00000000000000000007", "name": "tasted" } ] }
            """);

        var l10n = store.Current!.Localization;
        Assert.Equal([("operation", Package), ("operation-member", Package), ("outcome", Package), ("operation-member", Package), ("error", Package)],
            new[] { prepare, servings, done, portion, tooMany }.Select(id => (l10n.Nodes[id].Kind, l10n.Nodes[id].Scope)));
        Assert.Equal("root", l10n.Nodes[taste].Scope);
        Assert.Equal(("Too many", (string?)null), (l10n.Nodes[tooMany].Source("displayName"), l10n.Nodes[tooMany].Source("pluralName")));
        Assert.Equal("How many plates.", l10n.Nodes[servings].Source("description"));

        await Translate(store, "fr", new TranslationEdit(prepare, "displayName", "Préparer"), new TranslationEdit(servings, "description", "Combien d'assiettes."),
            new TranslationEdit(done, "displayName", "Prêt"), new TranslationEdit(portion, "displayName", "Portion servie"),
            new TranslationEdit(tooMany, "displayName", "Trop"));
        var kitchen = File.ReadAllText(h.Model("model/locales/fr/kitchen.json"));
        foreach (var id in new[] { prepare, servings, done, portion, tooMany })
            Assert.Contains("\"" + id + "\"", kitchen, StringComparison.Ordinal);
        Assert.Equal("Trop", store.Current!.Localization.Text("fr", tooMany, "displayName"));
        Assert.Equal("Combien d'assiettes.", store.Current.Localization.Text("fr", servings, "description"));
        var report = await store.ValidateAsync(ValidationScope.All, Ct);
        Assert.DoesNotContain(report.Diagnostics, d => d.Rule is "MQ7203" or "MQ7207" or "MQ7209" or "MQ7210");

        // Leaving the package moves the operation's entries, its parts' included, to the root shard.
        await Edit(store, prepare, n => n.Remove("package"));
        Assert.False(File.Exists(h.Model("model/locales/fr/kitchen.json")));
        Assert.Equal("Prêt", store.Current!.Localization.Text("fr", done, "displayName"));
        Assert.Contains("\"" + portion + "\"", File.ReadAllText(h.Model("model/locales/fr/_root.json")), StringComparison.Ordinal);
    }

    private static async Task Create(ModelStore store, string json)
    {
        var result = await store.CreateAsync(System.Text.Encoding.UTF8.GetBytes(json), ChangeSource.Editor, Ct);
        Assert.True(result.Outcome == SaveOutcome.Saved, result.Outcome + ": " + string.Join("; ", result.Diagnostics.Select(d => d.Rule + " " + d.JsonPointer + " " + d.Message)));
    }

    private static async Task<ShardStatus> RootStatus(ModelStore store, string locale) =>
        Assert.Single(Assert.Single((await store.GetLocalizationStatusAsync(Ct)).Locales, l => l.Locale == locale).Shards,
            s => s.Shard.EndsWith("/_root.json", StringComparison.Ordinal));

    private static string Shard(string locale, string scope, string entries) =>
        "{\n  \"kind\": \"locale-shard\",\n  \"locale\": \"" + locale + "\",\n  \"scope\": \"" + scope + "\",\n  \"entries\": {\n    " + entries + "\n  }\n}\n";
}
