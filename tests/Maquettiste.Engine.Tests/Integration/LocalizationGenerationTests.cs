using System.Text;
using System.Text.Json.Nodes;
using Maquettiste.Engine.Pipeline;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Integration;

/// <summary>
/// The localization helpers and the <c>each locale</c> scope in generation (reference-types-seeds-localization.md sections 3.7 and
/// 3.8), with the fixture pack <c>tests/fixtures/integration/packs/localization</c> beside the reference-data pack.
/// </summary>
public sealed class LocalizationGenerationTests
{
    private const string Pack = "localization";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ReferenceDataRepo Create()
    {
        var repo = ReferenceDataRepo.Create(settings => settings["packs"]![Pack] = new JsonObject { ["output"] = "gen/l10n" });
        E2ERepo.CopyTree(Fixtures.Path("integration", "packs", Pack), Path.Combine(repo.Repo.ModelRoot, "templates", Pack));
        return repo;
    }

    private static List<string> Rendered(GenerationPlan plan) =>
        [.. plan.Units.Where(u => !u.Skipped).Select(u => u.Key).Order(StringComparer.Ordinal)];

    private static Dictionary<string, string> Texts(ReferenceDataRepo repo) =>
        repo.Outputs().ToDictionary(p => p.Key, p => Encoding.UTF8.GetString(p.Value), StringComparer.Ordinal);

    private static string Output(ReferenceDataRepo repo, string suffix) => Assert.Single(Texts(repo), p => p.Key.EndsWith(suffix, StringComparison.Ordinal)).Value;

    private static async Task TranslateAsync(ReferenceDataRepo repo, string locale, string id, string field, string value)
    {
        var page = await repo.Store.GetTranslationsAsync(locale, null, null, Ct);
        var result = await repo.Store.SaveTranslationsAsync(locale, [new TranslationEdit(id, field, value)], page.ShardHashes, ChangeSource.Editor, Ct);
        Assert.Equal(SaveOutcome.Saved, result.Outcome);
    }

    [Fact]
    public async Task Each_locale_units_walk_the_fallback_chain()
    {
        await using var repo = Create();
        await repo.ApplyAsync();

        var fr = Output(repo, "i18n/fr.txt");
        var frCa = Output(repo, "i18n/fr-CA.txt");
        var en = Output(repo, "i18n/en.txt");
        Assert.Contains("# fr (default: false; chain: fr,en)", fr, StringComparison.Ordinal);
        Assert.Contains("# fr-CA (default: false; chain: fr-CA,fr,en)", frCa, StringComparison.Ordinal);
        Assert.Contains("# en (default: true; chain: en)", en, StringComparison.Ordinal);
        Assert.Contains("UnitOfMeasure=Unité de mesure / Unités de mesure", fr, StringComparison.Ordinal);
        Assert.Contains("=Kilogramme", frCa, StringComparison.Ordinal); // fr-CA falls back to fr
        Assert.Contains("UnitOfMeasure=Unit of measure / Units of measure", en, StringComparison.Ordinal);
        Assert.Contains("Allergen: Allergen", Output(repo, "fr/allergen.txt"), StringComparison.Ordinal); // no fr text: the default
        Assert.Contains("(translated: true)", Output(repo, "fr/unit-of-measure.txt"), StringComparison.Ordinal);
        var plan = await repo.PlanAsync();
        Assert.Contains(Pack + "/bundle:locale:fr-CA", plan.Units.Select(u => u.Key));
    }

    [Fact]
    public async Task A_French_label_change_re_renders_only_the_units_that_read_French_for_that_seed()
    {
        await using var repo = Create();
        await repo.ApplyAsync();

        await TranslateAsync(repo, "fr", ReferenceDataRepo.KgRow, "label", "Kilo");

        Assert.Equal([Pack + "/bundle:locale:fr", Pack + "/bundle:locale:fr-CA"], Rendered(await repo.PlanAsync()));
        await repo.ApplyAsync();
        Assert.Contains("=Kilo\n", Output(repo, "i18n/fr-CA.txt"), StringComparison.Ordinal);
        var incremental = Texts(repo);
        await repo.ApplyAsync(force: true);
        Assert.Equal(incremental, Texts(repo));
    }

    [Fact]
    public async Task A_French_display_name_change_re_renders_the_French_units_of_its_owner()
    {
        await using var repo = Create();
        await repo.ApplyAsync();

        await TranslateAsync(repo, "fr", ReferenceDataRepo.Allergen, "displayName", "Allergène");

        Assert.Equal([Pack + "/bundle:locale:fr", Pack + "/bundle:locale:fr-CA", Pack + "/type-fr:" + ReferenceDataRepo.Allergen],
            Rendered(await repo.PlanAsync()));
    }

    [Fact]
    public async Task A_fallbacks_change_re_renders_every_unit_that_read_a_translation_and_incremental_equals_full()
    {
        await using var repo = Create();
        await repo.ApplyAsync();

        await repo.EditSettingsAsync(s => s["localization"]!["fallbacks"] = new JsonObject { ["fr-CA"] = new JsonArray("en") });

        var rendered = Rendered(await repo.PlanAsync());
        Assert.Contains(Pack + "/bundle:locale:fr-CA", rendered);
        Assert.All(rendered, key => Assert.StartsWith(Pack + "/", key, StringComparison.Ordinal));
        await repo.ApplyAsync();
        Assert.Contains("UnitOfMeasure=Unit of measure", Output(repo, "i18n/fr-CA.txt"), StringComparison.Ordinal);
        var incremental = Texts(repo);
        await repo.ApplyAsync(force: true);
        Assert.Equal(incremental, Texts(repo));
    }

    [Fact]
    public async Task Two_cold_runs_are_byte_identical()
    {
        await using var first = Create();
        await using var second = Create();
        await first.ApplyAsync();
        await second.ApplyAsync();

        Assert.Equal(first.Outputs(), second.Outputs());
    }
}
