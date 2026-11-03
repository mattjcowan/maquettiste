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

    /// <summary>A member's content text (next-phase-2-design.md section 4.2) is translated under the member's own id.</summary>
    [Fact]
    public async Task Translate_reads_a_member_content_field()
    {
        var (h, store) = await LocalizationTests.OpenCustomAsync();
        using var _ = h;
        await using var __ = store;
        await Kit.Translate(store, "fr", new TranslationEdit(LocalizationTests.Subject, "content/text", "Votre facture"));
        var model = ResolutionKit.Resolve(store.Current!);

        var unit = await Adhoc.RenderAsync(
            "{{ translate '" + LocalizationTests.Subject + "' 'content/text' 'fr-CA' }}|{{ translate '" + LocalizationTests.Body + "' 'content/text' 'fr' }}|"
            + "{{ has_translation '" + LocalizationTests.Subject + "' 'content/text' 'fr' }}",
            model.Entity("Recipe"), model: model);

        // fr-CA falls back to fr; an untranslated part is its default text; the l: key is the owner's.
        Assert.Equal("Votre facture|Thank you.|true", unit.Text());
        Assert.Contains("l:fr:" + LocalizationTests.Message, unit.ReadKeys);
        Assert.Contains("e:" + LocalizationTests.Message, unit.ReadKeys);

        // A resolved member takes the same field: the parts of the message, in file order.
        var parts = await Adhoc.RenderAsync(
            "{{ for part in (lookup '" + LocalizationTests.Message + "').content.parts }}{{ part.name }}={{ translate part 'content/text' 'fr' }};{{ end }}", model: model);
        Assert.Equal("subject=Votre facture;body=Thank you.;", parts.Text());

        var wrong = await Adhoc.RenderAsync("{{ translate '" + LocalizationTests.Subject + "' 'content/9lives' 'fr' }}", model.Entity("Recipe"), model: model);
        Assert.Contains("content/<name>", wrong.Error().Message, StringComparison.Ordinal);
    }

    /// <summary>Own parts and own errors (next-phase-2-design.md section 4.1); inherited errors are the next test.</summary>
    [Fact]
    public async Task An_operations_members_outcomes_and_errors_take_their_translations()
    {
        const string prepare = "01JRDQ00000000000000000011", servings = "01JRDQ00000000000000000012", done = "01JRDQ00000000000000000013";
        const string tooMany = "01JRDQ00000000000000000014";
        var (h, store) = await Kit.OpenAsync();
        using var _ = h;
        await using var __ = store;
        var json = $$"""
            { "kind": "operation", "id": "{{prepare}}", "name": "prepare", "subject": "{{Kit.Recipe}}", "effect": "change",
              "input": [ { "id": "{{servings}}", "name": "servings", "type": "int32", "description": "How many plates." } ],
              "outcomes": [ { "id": "{{done}}", "name": "done" } ],
              "errors": [ { "id": "{{tooMany}}", "name": "too_many", "displayName": "Too many" } ] }
            """;
        var created = await store.CreateAsync(System.Text.Encoding.UTF8.GetBytes(json), ChangeSource.Editor, Kit.Ct);
        Assert.Equal(SaveOutcome.Saved, created.Outcome);
        await Kit.Translate(store, "fr", new TranslationEdit(prepare, "displayName", "Préparer"), new TranslationEdit(servings, "displayName", "Portions"),
            new TranslationEdit(servings, "description", "Combien d'assiettes."), new TranslationEdit(done, "displayName", "Prêt"),
            new TranslationEdit(tooMany, "displayName", "Trop"));
        var model = ResolutionKit.Resolve(store.Current!);
        var operation = model.Operations.Single();

        var unit = await Adhoc.RenderAsync(
            "{{ display_name operation 'fr' }}|{{ display_name operation.input[0] 'fr-CA' }}|{{ description_of operation.input[0] 'fr' }}|"
            + "{{ display_name operation.outcomes[0] 'fr' }}|{{ display_name operation.errors[0] 'fr' }}|{{ display_name operation.errors[0] }}|"
            + "{{ has_translation operation.errors[0] 'displayName' 'fr-CA' }}",
            operation, u => u with { For = "each operation" }, model: model);

        Assert.Equal("Préparer|Portions|Combien d'assiettes.|Prêt|Trop|Too many|false", unit.Text());
        Assert.Contains("l:fr:" + prepare, unit.ReadKeys);
        Assert.Contains("e:" + prepare, unit.ReadKeys);
    }

    /// <summary>
    /// An error an operation inherits from a stereotype takes the stereotype error's translations (its <c>l:</c> key is the stereotype's);
    /// a facet the operation's redefinition states is its own text in every locale.
    /// </summary>
    [Fact]
    public async Task An_inherited_error_takes_its_stereotype_errors_translation_unless_redefined()
    {
        const string common = "01JRDQ00000000000000000021", notFound = "01JRDQ00000000000000000022", prepare = "01JRDQ00000000000000000023";
        const string done = "01JRDQ00000000000000000024";
        var (h, store) = await Kit.OpenAsync();
        using var _ = h;
        await using var __ = store;
        foreach (var json in new[]
        {
            $$"""
            { "kind": "stereotype", "id": "{{common}}", "key": "common-errors", "name": "CommonErrors", "appliesTo": ["operation"],
              "errors": [ { "id": "{{notFound}}", "name": "not_found", "displayName": "Not found", "description": "No such record." } ] }
            """,
            $$"""
            { "kind": "operation", "id": "{{prepare}}", "name": "prepare", "subject": "{{Kit.Recipe}}", "effect": "change",
              "outcomes": [ { "id": "{{done}}", "name": "done" } ], "stereotypes": [ "common-errors" ],
              "redefines": [ { "member": "{{notFound}}", "description": "No such recipe." } ] }
            """,
        })
        {
            var created = await store.CreateAsync(System.Text.Encoding.UTF8.GetBytes(json), ChangeSource.Editor, Kit.Ct);
            Assert.True(created.Outcome == SaveOutcome.Saved, string.Join("; ", created.Diagnostics.Select(d => d.Rule + " " + d.Message)));
        }

        await Kit.Translate(store, "fr", new TranslationEdit(notFound, "displayName", "Introuvable"), new TranslationEdit(notFound, "description", "Aucun enregistrement."));
        var model = ResolutionKit.Resolve(store.Current!);
        var operation = model.Operations.Single();

        var unit = await Adhoc.RenderAsync(
            "{{ display_name operation.errors[0] 'fr' }}|{{ display_name operation.errors[0] }}|{{ description_of operation.errors[0] 'fr' }}|"
            + "{{ has_translation operation.errors[0] 'displayName' 'fr' }}/{{ has_translation operation.errors[0] 'description' 'fr' }}",
            operation, u => u with { For = "each operation" }, model: model);

        Assert.Equal("Introuvable|Not found|No such recipe.|true/false", unit.Text());
        Assert.Contains("l:fr:" + common, unit.ReadKeys);
    }
}
