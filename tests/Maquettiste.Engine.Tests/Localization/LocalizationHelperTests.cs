using Maquettiste.Engine.Tests.Rendering;
using Maquettiste.Engine.Tests.Resolution;
using Kit = Maquettiste.Engine.Tests.Localization.PropertyTextsKit;

namespace Maquettiste.Engine.Tests.Localization;

/// <summary>
/// The localization helpers on property fields (next-phase-1-design.md section 4.4): <c>translate</c> and <c>has_translation</c> take
/// <c>properties/&lt;name&gt;</c> beside the standard fields and walk the locale's fallback chain to the holder's own text.
/// </summary>
public sealed class LocalizationHelperTests
{
    [Fact]
    public async Task Translate_reads_a_property_field_through_the_chain()
    {
        var (h, store) = await Kit.OpenAsync();
        using var _ = h;
        await using var __ = store;
        await Kit.Translate(store, "fr", new TranslationEdit(Kit.Recipe, "properties/footer", "Merci d'avoir cuisiné."),
            new TranslationEdit(Kit.RecipeName, "properties/footer", "Affiché sous le nom."));
        var model = ResolutionKit.Resolve(store.Current!);
        var recipe = model.Entity("Recipe");

        var unit = await Adhoc.RenderAsync(
            "{{ translate entity 'properties/footer' 'fr-CA' }}|{{ translate entity 'properties/footer' 'fr' }}|{{ translate entity 'properties/footer' }}|"
            + "{{ has_translation entity 'properties/footer' 'fr-CA' }}/{{ has_translation entity 'properties/footer' 'fr' }}|"
            + "{{ translate entity.attributes[1] 'properties/footer' 'fr-CA' }}|{{ translate entity 'properties/note' 'fr' }}",
            recipe, model: model);

        // fr-CA has no text of its own and falls back to fr; no locale is the default text; a plain property is its resolved value.
        Assert.Equal("Merci d'avoir cuisiné.|Merci d'avoir cuisiné.|" + Kit.RecipeFooter + "|false/true|Affiché sous le nom.|Kept in the default locale.",
            unit.Text());
        Assert.Contains("l:fr-CA:" + Kit.Recipe, unit.ReadKeys);
        Assert.Contains("l:fr:" + Kit.Recipe, unit.ReadKeys);

        var wrong = await Adhoc.RenderAsync("{{ translate entity 'properties/9lives' 'fr' }}", recipe, model: model);
        var error = wrong.Error();
        Assert.Equal("MQ6006", error.Rule);
        Assert.Contains("properties/<name>", error.Message, StringComparison.Ordinal);
    }
}
