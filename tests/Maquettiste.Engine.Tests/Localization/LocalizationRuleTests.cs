using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Validation;
using Kit = Maquettiste.Engine.Tests.Localization.PropertyTextsKit;

namespace Maquettiste.Engine.Tests.Localization;

/// <summary>
/// Where <c>x-translatable</c> may go (next-phase-1-design.md section 4.4): on a top-level string property of an extension file or of
/// a stereotype's <c>parameters</c> (MQ7212 anywhere else), and to some effect only where the schema applies to kinds with
/// translations (MQ7213 otherwise).
/// </summary>
public sealed class LocalizationRuleTests
{
    private const string Stereotype = "01JRDV00000000000000000002";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Nested_or_number_translatable_is_MQ7212_and_physical_only_MQ7213()
    {
        var (h, store) = await Kit.OpenAsync(h =>
        {
            Kit.Write(h, "extensions/misplaced.json", """
                {
                  "name": "misplaced",
                  "appliesTo": { "kinds": ["entity"] },
                  "properties": {
                    "count": { "type": "integer", "x-translatable": true },
                    "meta": { "type": "object", "properties": { "inner": { "type": "string", "x-translatable": true } } },
                    "tags": { "type": "array", "items": { "type": "string", "x-translatable": true } },
                    "motto": { "type": "string", "x-translatable": "yes" },
                    "x-translatable": { "type": "string" }
                  }
                }
                """);
            Kit.Write(h, "extensions/physical.json", """
                {
                  "name": "physical",
                  "appliesTo": { "kinds": ["table", "column"] },
                  "properties": { "caption": { "type": "string", "x-translatable": true } }
                }
                """);
            Kit.Write(h, "model/vocabularies/stereotypes/captioned.json", $$"""
                {
                  "kind": "stereotype",
                  "id": "{{Stereotype}}",
                  "key": "captioned",
                  "name": "Captioned",
                  "appliesTo": ["table"],
                  "parameters": {
                    "properties": {
                      "caption": { "type": "string", "x-translatable": true },
                      "parts": { "type": "object", "additionalProperties": { "type": "string", "x-translatable": true } }
                    }
                  }
                }
                """);
        });
        using var _ = h;
        await using var __ = store;

        var report = await store.ValidateAsync(ValidationScope.All, Ct);
        var found = report.Diagnostics.Where(d => d.Rule is "MQ7212" or "MQ7213")
            .Select(d => (d.Rule, d.FilePath![(d.FilePath!.LastIndexOf('/') + 1)..], d.JsonPointer)).ToList();

        (string, string, string?)[] expected =
        [
            ("MQ7212", "captioned.json", "/parameters/properties/parts/additionalProperties/x-translatable"),
            ("MQ7212", "misplaced.json", "/properties/count/x-translatable"),
            ("MQ7212", "misplaced.json", "/properties/meta/properties/inner/x-translatable"),
            ("MQ7212", "misplaced.json", "/properties/motto/x-translatable"),
            ("MQ7212", "misplaced.json", "/properties/tags/items/x-translatable"),
            ("MQ7213", "captioned.json", "/parameters/properties/caption/x-translatable"),
            ("MQ7213", "physical.json", "/properties/caption/x-translatable"),
        ];
        Assert.Equal(expected.Order(), found.Order());
        Assert.All(report.Diagnostics.Where(d => d.Rule == "MQ7212"), d => Assert.Equal(DiagnosticSeverity.Error, d.Severity));
        Assert.All(report.Diagnostics.Where(d => d.Rule == "MQ7213"), d => Assert.Equal(DiagnosticSeverity.Warning, d.Severity));

        // A misplaced mark marks nothing; the well-placed footer of the texts extension still does.
        var recipe = store.Current!.Localization.Nodes[Kit.Recipe];
        Assert.Equal(["footer"], recipe.TranslatableProperties);

        // The stereotype's findings come with the stereotype's own validation, so a save of it reports them.
        var saved = await store.ValidateAsync(new ValidationScope([Stereotype]), Ct);
        Assert.Contains(saved.Diagnostics, d => d.Rule == "MQ7212" && d.ElementId == Stereotype);
    }
}
