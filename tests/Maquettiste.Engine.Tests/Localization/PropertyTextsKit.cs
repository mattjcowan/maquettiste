using System.Text.Json.Nodes;
using Maquettiste.Engine.Generation;
using Maquettiste.Engine.Tests.Loading;

namespace Maquettiste.Engine.Tests.Localization;

/// <summary>
/// The reference-data fixture (<c>en</c>, <c>fr</c>, <c>fr-CA</c> falling back to <c>fr</c>) with translatable custom properties
/// (next-phase-1-design.md section 4.4): <c>extensions/texts.json</c> gives entities and attributes a translatable <c>footer</c> and a
/// plain <c>note</c>, and Recipe and its <c>name</c> attribute set both.
/// </summary>
internal static class PropertyTextsKit
{
    public const string Recipe = "01JRDE00000000000000000001";
    public const string RecipeName = "01JRDE00000000000000000012";
    public const string Ingredient = "01JRDE00000000000000000002";
    public const string TextsFile = "extensions/texts.json";
    public const string RecipeFooter = "Thank you for cooking.";
    public const string NameFooter = "Shown under the name.";
    public const string FrRoot = ".maquettiste/model/locales/fr/_root.json";

    public static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The texts extension, with <c>footer</c> marked translatable or not.</summary>
    public static string Extension(bool translatable = true) =>
        "{ \"name\": \"texts\", \"appliesTo\": { \"kinds\": [\"entity\", \"attribute\"] }, \"properties\": { \"footer\": { \"type\": \"string\""
        + (translatable ? ", \"x-translatable\": true" : "") + " }, \"note\": { \"type\": \"string\" } } }";

    public static async Task<(LoaderHarness Harness, ModelStore Store)> OpenAsync(Action<LoaderHarness>? prepare = null)
    {
        var harness = new LoaderHarness();
        harness.CopyFixture("models", "reference-data");
        Write(harness, TextsFile, Extension());
        EditFile(harness, "model/entities/recipe.json", recipe =>
        {
            recipe["properties"] = new JsonObject { ["footer"] = RecipeFooter, ["note"] = "Kept in the default locale." };
            recipe["attributes"]![1]!["properties"] = new JsonObject { ["footer"] = NameFooter };
        });
        prepare?.Invoke(harness);
        var store = new ModelStore(harness.Options, harness.Services() with { Validator = EngineServices.Create(harness.Options).Validator });
        await store.LoadAsync(Ct);
        return (harness, store);
    }

    public static void Write(LoaderHarness h, string modelPath, string text)
    {
        var full = h.Model(modelPath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text.ReplaceLineEndings("\n"));
    }

    public static void EditFile(LoaderHarness h, string modelPath, Action<JsonObject> change)
    {
        var full = h.Model(modelPath);
        var node = JsonNode.Parse(File.ReadAllText(full))!.AsObject();
        change(node);
        File.WriteAllText(full, node.ToJsonString());
    }

    public static async Task Translate(ModelStore store, string locale, params TranslationEdit[] edits)
    {
        var hashes = (await store.GetTranslationsAsync(locale, null, null, Ct)).ShardHashes;
        var result = await store.SaveTranslationsAsync(locale, edits, hashes, ChangeSource.Editor, Ct);
        Assert.True(result.Outcome == SaveOutcome.Saved, result.Outcome + ": " + string.Join("; ", result.Diagnostics.Select(d => d.Rule + " " + d.Message)));
    }

    public static async Task Edit(ModelStore store, string id, Action<JsonObject> change)
    {
        var document = store.Current!.GetDocument(id)!;
        var node = JsonNode.Parse(document.Json.GetRawText())!.AsObject();
        change(node);
        var result = await store.SaveAsync(id, System.Text.Encoding.UTF8.GetBytes(node.ToJsonString()), document.Hash, ChangeSource.Editor, Ct);
        Assert.True(result.Outcome == SaveOutcome.Saved, result.Outcome + ": " + string.Join("; ", result.Diagnostics.Select(d => d.Rule + " " + d.Message)));
    }
}
