using System.Text.Json;
using System.Text.Json.Nodes;
using Maquettiste.Cli.Commands;
using Maquettiste.Engine.Localization;

namespace Maquettiste.Cli.Tests;

/// <summary>
/// The <c>l10n</c> and <c>seed</c> verbs (reference-types-seeds-localization.md section 3.9) on the reference-data fixture (en default,
/// fr and fr-CA; the fr shard holds three stale entries), and the project name rules of <c>init</c>.
/// </summary>
public sealed class L10nAndSeedCommandTests
{
    private const string UnitOfMeasure = "01JBM9S346Q3D25VT4F5V37E3S";
    private const string UnitSeed = "01JB9EE0ZBGJ09TQM83XSSSS6Y";
    private const string Kilogram = "01JBS3C4DWA7N36096Q14DR9GP";
    private const string Gram = "01JBQY77ZXYYK596NGYA1DQ91K";
    private const string FrShard = ".maquettiste/model/locales/fr/_reference-data.json";

    [Fact]
    public async Task Seed_new_creates_a_reference_types_seed_with_code_label_and_description_once()
    {
        using var repo = CliRepo.ReferenceData();
        repo.Write(".maquettiste/model/reference-types/shipping-speed.json", """
            {
              "$schema": "../../.schema/v1/reference-type.json",
              "kind": "reference-type",
              "id": "01JC0000000000000000000SPD",
              "name": "ShippingSpeed",
              "code": { "id": "01JC0000000000000000000SPC" },
              "label": { "id": "01JC0000000000000000000SPB" }
            }
            """);

        var created = await repo.RunAsync("seed", "new", "shippingspeed");
        Assert.True(created.ExitCode == 0, created.Error);
        Assert.StartsWith("created seed ShippingSpeed (", created.Out, StringComparison.Ordinal);
        var file = Directory.GetFiles(repo.PathOf(".maquettiste/model"), "*.json", SearchOption.AllDirectories)
            .Select(f => JsonNode.Parse(File.ReadAllText(f))).OfType<JsonObject>()
            .Single(n => (string?)n["kind"] == "seed" && (string?)n["target"] == "01JC0000000000000000000SPD");
        Assert.Equal(["code", "label", "description"], file["columns"]!.AsArray().Select(c => (string)c!));
        Assert.Equal("ShippingSpeed", (string)file["name"]!);

        var again = await repo.RunAsync("seed", "new", "01JC0000000000000000000SPD");
        Assert.Equal(0, again.ExitCode);
        Assert.Contains("already has a seed", again.Out, StringComparison.Ordinal);
        Assert.Equal(4, (await repo.RunAsync("seed", "new", "NoSuchType")).ExitCode);
        Assert.Equal(0, (await repo.RunAsync("validate")).ExitCode);
    }

    [Fact]
    public async Task Status_reports_the_locales_and_completeness_per_shard_as_text_and_json()
    {
        using var repo = CliRepo.ReferenceData();

        var text = await repo.RunAsync("l10n", "status");
        var json = await repo.RunAsync("l10n", "status", "--format", "json");

        Assert.Equal(0, text.ExitCode);
        Assert.Contains("Default locale: en\nDeclared locales: en, fr, fr-CA\n", text.Out, StringComparison.Ordinal);
        Assert.Contains("fr: 0 of 54 translated (0%), 51 missing, 3 stale; chain fr > en\n", text.Out, StringComparison.Ordinal);
        Assert.Contains("  " + FrShard + ": 0 of 23 translated, 20 missing, 3 stale\n", text.Out, StringComparison.Ordinal);
        Assert.Contains("chain fr-CA > fr > en", text.Out, StringComparison.Ordinal);
        Assert.Equal(0, json.ExitCode);
        var status = JsonNode.Parse(json.Out)!;
        Assert.Equal("en", (string)status["defaultLocale"]!);
        Assert.Equal(["fr", "fr-CA"], status["locales"]!.AsArray().Select(l => (string)l!["locale"]!));
        Assert.Equal(3, status["locales"]![0]!["shards"]!.AsArray().Sum(s => (int)s!["stale"]!));
    }

    [Fact]
    public async Task Export_writes_xliff_or_csv_to_stdout_or_a_file_and_refuses_an_undeclared_locale()
    {
        using var repo = CliRepo.ReferenceData();

        var xliff = await repo.RunAsync("l10n", "export", "fr");
        var csv = await repo.RunAsync("l10n", "export", "fr-CA", "--format", "csv", "--out", "exchange/fr-CA.csv");
        var unknown = await repo.RunAsync("l10n", "export", "de");
        var defaultLocale = await repo.RunAsync("l10n", "export", "en");
        var inModel = await repo.RunAsync("l10n", "export", "fr", "--out", ".maquettiste/fr.xlf");

        Assert.Equal(0, xliff.ExitCode);
        Assert.Contains("<xliff", xliff.Out, StringComparison.Ordinal);
        Assert.Contains($"<unit id=\"{Kilogram}/label\">", xliff.Out, StringComparison.Ordinal);
        Assert.Equal(0, csv.ExitCode);
        Assert.Equal("", csv.Out);
        Assert.StartsWith("id,field,source,translation,state,shard\n", repo.Read("exchange/fr-CA.csv"), StringComparison.Ordinal);
        Assert.Equal(4, unknown.ExitCode); // A locale argument that names nothing is a usage error.
        Assert.Contains("'de' is not a declared locale", unknown.Error, StringComparison.Ordinal);
        Assert.Equal(4, defaultLocale.ExitCode);
        Assert.Equal(4, inModel.ExitCode);
    }

    [Fact]
    public async Task Locale_arguments_are_normalized_like_the_editor_and_a_malformed_one_says_how_to_write_it()
    {
        using var repo = CliRepo.ReferenceData();

        var underscored = await repo.RunAsync("l10n", "export", "fr_ca", "--format", "csv");
        var malformed = await repo.RunAsync("l10n", "export", "fr.ca");

        Assert.Equal(0, underscored.ExitCode);
        Assert.StartsWith("id,field,source,translation,state,shard\n", underscored.Out, StringComparison.Ordinal);
        Assert.Equal(4, malformed.ExitCode);
        Assert.Contains("'fr.ca' is not a BCP 47 language tag. Use language-REGION with a hyphen, such as zh-CN", malformed.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Import_previews_added_changed_and_stale_confirmed_then_applies_with_apply()
    {
        using var repo = CliRepo.ReferenceData();
        var export = await repo.RunAsync("l10n", "export", "fr", "--format", "csv");
        var rows = Csv.Read(export.Out).Select(r => r.ToArray()).ToList();
        var edited = new List<IReadOnlyList<string?>> { rows[0] };
        foreach (var r in rows.Skip(1))
        {
            if (r[0] == Gram && r[1] == "label")
                r[3] = "Gramme";
            if (r[0] == Kilogram && r[1] == "label")
                r[4] = "translated"; // The translator confirms a stale text without changing it.
            if (r[0] == UnitOfMeasure && r[1] == "displayName")
                r[3] = "Unité";
            edited.Add(r);
        }

        edited.Add(["01NOSUCHNODE000000000000000", "label", "", "Rien", "translated", ""]);
        repo.Write("fr.csv", Csv.Write(edited));
        var shardBefore = repo.Read(FrShard);

        var untouched = await repo.RunAsync("l10n", "import", "fr", "fr.csv", "--dry-run");
        var check = await repo.RunAsync("l10n", "import", "fr", "fr.csv", "--check", "--format", "json");
        var unchanged = repo.Read(FrShard);
        var applied = await repo.RunAsync("l10n", "import", "fr", "fr.csv", "--apply");
        var again = await repo.RunAsync("l10n", "import", "fr", "fr.csv", "--check");
        var status = JsonNode.Parse((await repo.RunAsync("l10n", "status", "--format", "json")).Out)!;
        var contradiction = await repo.RunAsync("l10n", "import", "fr", "fr.csv", "--apply", "--dry-run");
        var missing = await repo.RunAsync("l10n", "import", "fr", "nothing.xlf");

        Assert.Equal(0, untouched.ExitCode);
        Assert.Contains("fr import: 1 added, 1 changed, 1 stale confirmed, 1 ignored; preview only", untouched.Error, StringComparison.Ordinal);
        Assert.Contains($"added     {Gram}/label: \"Gramme\"", untouched.Out, StringComparison.Ordinal);
        Assert.Contains($"changed   {UnitOfMeasure}/displayName: \"Unité de mesure\" -> \"Unité\"", untouched.Out, StringComparison.Ordinal);
        Assert.Contains($"confirmed {Kilogram}/label", untouched.Out, StringComparison.Ordinal);
        Assert.Contains("MQ7203", untouched.Out, StringComparison.Ordinal);
        Assert.Equal(2, check.ExitCode);
        var preview = JsonNode.Parse(check.Out)!;
        Assert.Equal(1, (int)preview["added"]!);
        Assert.Equal(1, (int)preview["changed"]!);
        Assert.Equal(1, (int)preview["confirmed"]!);
        Assert.False((bool)preview["applied"]!);
        Assert.Equal(shardBefore, unchanged);
        Assert.Equal(0, applied.ExitCode);
        Assert.Contains("written", applied.Error, StringComparison.Ordinal);
        Assert.Equal(0, again.ExitCode);
        Assert.Contains("0 added, 0 changed, 0 stale confirmed", again.Error, StringComparison.Ordinal);
        var fr = status["locales"]![0]!["shards"]!.AsArray().Single(s => (string)s!["shard"]! == FrShard)!;
        Assert.Equal(1, (int)fr["stale"]!); // Two of the three stale entries were confirmed or rewritten.
        Assert.Equal(3, (int)fr["translated"]!);
        Assert.Equal(4, contradiction.ExitCode);
        Assert.Equal(1, missing.ExitCode);
    }

    [Fact]
    public async Task Import_reads_xliff_and_confirms_a_reviewed_segment()
    {
        using var repo = CliRepo.ReferenceData();
        var xliff = (await repo.RunAsync("l10n", "export", "fr")).Out;
        var unit = xliff.IndexOf($"<unit id=\"{Kilogram}/label\">", StringComparison.Ordinal);
        var segment = xliff.IndexOf("state=\"initial\"", unit, StringComparison.Ordinal);
        repo.Write("fr.xlf", xliff[..segment] + "state=\"reviewed\"" + xliff[(segment + "state=\"initial\"".Length)..]);

        var untouched = await repo.RunAsync("l10n", "import", "fr", "fr.xlf");
        repo.Write("fr-original.xlf", xliff);
        var original = await repo.RunAsync("l10n", "import", "fr", "fr-original.xlf", "--check");

        Assert.Equal(0, untouched.ExitCode);
        Assert.Contains("0 added, 0 changed, 1 stale confirmed, 0 ignored", untouched.Error, StringComparison.Ordinal);
        Assert.Equal(0, original.ExitCode); // Re-importing an untouched export changes nothing: stale stays stale.
    }

    [Fact]
    public async Task Prune_reports_and_removes_orphans_in_one_save()
    {
        using var repo = CliRepo.ReferenceData();
        var shard = JsonNode.Parse(repo.Read(FrShard))!.AsObject();
        shard["entries"]!["01JRDA00000000000000000099"] = new JsonObject { ["displayName"] = "Fantôme" };
        shard["entries"]![UnitOfMeasure]!["label"] = "Étiquette";
        repo.Write(FrShard, shard.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");

        var check = await repo.RunAsync("l10n", "prune", "--check");
        var json = await repo.RunAsync("l10n", "prune", "--dry-run", "--format", "json");
        var plain = await repo.RunAsync("l10n", "prune");
        var kept = repo.Read(FrShard);
        var pruned = await repo.RunAsync("l10n", "prune", "--apply");
        var again = await repo.RunAsync("l10n", "prune", "--check");
        var validate = await repo.RunAsync("validate");

        Assert.Equal(2, check.ExitCode);
        Assert.Contains($"orphan fr {UnitOfMeasure}/label ({FrShard})", check.Out, StringComparison.Ordinal);
        Assert.Contains($"orphan fr 01JRDA00000000000000000099 ({FrShard})", check.Out, StringComparison.Ordinal);
        Assert.Equal(0, json.ExitCode);
        Assert.Equal(2, JsonNode.Parse(json.Out)!["orphans"]!.AsArray().Count);
        Assert.Equal(0, plain.ExitCode); // Like the other verbs, prune only previews without --apply.
        Assert.Contains("01JRDA00000000000000000099", kept, StringComparison.Ordinal);
        Assert.Equal(0, pruned.ExitCode);
        Assert.Contains("removed fr 01JRDA00000000000000000099", pruned.Out, StringComparison.Ordinal);
        Assert.Equal(0, again.ExitCode);
        Assert.DoesNotContain("MQ7203", validate.Out, StringComparison.Ordinal);
        var after = JsonNode.Parse(repo.Read(FrShard))!["entries"]!.AsObject();
        Assert.False(after.ContainsKey("01JRDA00000000000000000099"));
        Assert.Equal("Unité de mesure", (string)after[UnitOfMeasure]!["displayName"]!);
        Assert.Null(after[UnitOfMeasure]!["label"]);
    }

    [Fact]
    public async Task Set_default_previews_then_swaps_texts_between_files_and_shards()
    {
        using var repo = CliRepo.ReferenceData();
        var before = repo.Tree(".maquettiste");

        var preview = await repo.RunAsync("l10n", "set-default", "fr");
        var unchanged = repo.Tree(".maquettiste");
        var same = await repo.RunAsync("l10n", "set-default", "en");
        var applied = await repo.RunAsync("l10n", "set-default", "fr", "--apply");
        var validate = await repo.RunAsync("validate");
        var format = await repo.RunAsync("format", "--check");
        var status = JsonNode.Parse((await repo.RunAsync("l10n", "status", "--format", "json")).Out)!;

        Assert.Equal(0, preview.ExitCode);
        Assert.Contains($"moved   {UnitOfMeasure}/displayName: \"Unité de mesure\" (en: \"Unit of measure\")", preview.Out, StringComparison.Ordinal);
        Assert.Contains($"moved   {Kilogram}/label: \"Kilogramme\" (en: \"Kilogram\")", preview.Out, StringComparison.Ordinal);
        Assert.Equal(before.Keys, unchanged.Keys);
        Assert.Equal(0, same.ExitCode);
        Assert.Contains("already the default", same.Error, StringComparison.Ordinal);
        Assert.Equal(0, applied.ExitCode);
        Assert.Equal(0, validate.ExitCode);
        Assert.Equal(0, format.ExitCode);
        Assert.Equal("fr", (string)status["defaultLocale"]!);
        Assert.Contains("\"Kilogramme\"", repo.Read(".maquettiste/model/seeds/unit-of-measure/unit-of-measure.json"), StringComparison.Ordinal);
        Assert.Contains("\"displayName\": \"Unité de mesure\"", repo.Read(".maquettiste/model/reference-types/unit-of-measure.json"), StringComparison.Ordinal);
        var en = JsonNode.Parse(repo.Read(".maquettiste/model/locales/en/_reference-data.json"))!["entries"]!;
        Assert.Equal("Kilogram", (string)en[Kilogram]!["label"]!);
        Assert.Equal("Unit of measure", (string)en[UnitOfMeasure]!["displayName"]!);
        Assert.False(File.Exists(repo.PathOf(FrShard)));
        var enStatus = status["locales"]!.AsArray().Single(l => (string)l!["locale"]! == "en")!;
        Assert.Equal(0, enStatus["shards"]!.AsArray().Sum(s => (int)s!["stale"]!));
    }

    [Fact]
    public async Task Seed_export_by_id_name_or_target_with_locale_columns()
    {
        using var repo = CliRepo.ReferenceData();

        var byName = await repo.RunAsync("seed", "export", "UnitOfMeasure", "--locale", "fr");
        var byId = await repo.RunAsync("seed", "export", UnitSeed, "--locale", "fr");
        var byTarget = await repo.RunAsync("seed", "export", UnitOfMeasure, "--out", "uom.csv");
        var caseless = await repo.RunAsync("seed", "export", "allergen");
        var unknown = await repo.RunAsync("seed", "export", "Nope");
        var badLocale = await repo.RunAsync("seed", "export", UnitSeed, "--locale", "de");

        Assert.Equal(0, byName.ExitCode);
        Assert.StartsWith("@id,@code,@label,factor,symbol,@label:fr,@description:fr\n", byName.Out, StringComparison.Ordinal);
        Assert.Contains($"{Kilogram},kg,Kilogram,1000,kg,Kilogramme,", byName.Out, StringComparison.Ordinal);
        Assert.Equal(byName.Out, byId.Out);
        Assert.Equal(0, byTarget.ExitCode);
        Assert.StartsWith("@id,@code,@label,factor,symbol\n", repo.Read("uom.csv"), StringComparison.Ordinal);
        Assert.Equal(0, caseless.ExitCode);
        Assert.StartsWith("@id,@code,@label,parent\n", caseless.Out, StringComparison.Ordinal);
        Assert.Equal(4, unknown.ExitCode); // An argument that names no seed or locale is a usage error.
        Assert.Equal(4, badLocale.ExitCode);
    }

    [Fact]
    public async Task Seed_import_previews_merge_and_replace_then_applies_with_apply()
    {
        using var repo = CliRepo.ReferenceData();
        repo.Write("uom.csv", "@code,@label,factor\nmg,Milligram,0.001\n");
        var seedPath = ".maquettiste/model/seeds/unit-of-measure/unit-of-measure.json";
        var before = repo.Read(seedPath);

        var merge = await repo.RunAsync("seed", "import", "UnitOfMeasure", "uom.csv");
        var replace = await repo.RunAsync("seed", "import", "UnitOfMeasure", "uom.csv", "--mode", "replace", "--check", "--format", "json");
        var unchanged = repo.Read(seedPath);
        var applied = await repo.RunAsync("seed", "import", UnitSeed, "uom.csv", "--apply");
        var again = await repo.RunAsync("seed", "import", UnitSeed, "uom.csv", "--check");
        repo.Write("bad.csv", "\"unterminated\n");
        var bad = await repo.RunAsync("seed", "import", UnitSeed, "bad.csv");
        var badMode = await repo.RunAsync("seed", "import", UnitSeed, "uom.csv", "--mode", "upsert");

        Assert.Equal(0, merge.ExitCode);
        Assert.Contains("UnitOfMeasure import (merge): 1 added, 0 changed, 0 removed, 0 blocked; preview only", merge.Error, StringComparison.Ordinal);
        Assert.Equal(2, replace.ExitCode);
        var preview = JsonNode.Parse(replace.Out)!["preview"]!;
        Assert.Equal(1, (int)preview["added"]!);
        Assert.Equal(2, preview["blocked"]!.AsArray().Count);
        Assert.Equal(before, unchanged);
        Assert.Equal(0, applied.ExitCode);
        Assert.Contains("\"mg\"", repo.Read(seedPath), StringComparison.Ordinal);
        Assert.Equal(0, again.ExitCode);
        Assert.Equal(1, bad.ExitCode);
        Assert.Equal(4, badMode.ExitCode);
    }

    [Fact]
    public async Task Init_names_the_project_from_name_then_package_json_then_git_remote_then_folder()
    {
        using var given = CliRepo.Empty();
        given.Write("package.json", "{ \"name\": \"from-package\" }");
        using var package = CliRepo.Empty();
        package.Write("package.json", "{ \"name\": \"@acme/storefront\", \"private\": true }");
        package.Write(".git/config", "[remote \"origin\"]\n\turl = git@example.com:acme/from-git.git\n");
        using var git = CliRepo.Empty();
        git.Write(".git/config", "[core]\n\tbare = false\n[remote \"upstream\"]\n\turl = https://example.com/acme/other.git\n[remote \"origin\"]\n\turl = https://example.com/acme/recipes.git/\n");
        using var folder = CliRepo.Empty();

        Assert.Equal(0, (await given.RunAsync("init", "--name", "Kitchen", "--pack", "none")).ExitCode);
        Assert.Equal(0, (await package.RunAsync("init", "--pack", "none")).ExitCode);
        Assert.Equal(0, (await git.RunAsync("init", "--pack", "none")).ExitCode);
        Assert.Equal(0, (await folder.RunAsync("init", "--pack", "none")).ExitCode);
        var blank = await folder.RunAsync("init", "--name", " ");

        Assert.Equal("Kitchen", NameOf(given));
        Assert.Equal("storefront", NameOf(package));
        Assert.Equal("recipes", NameOf(git));
        Assert.Equal(Path.GetFileName(folder.RepoRoot), NameOf(folder));
        Assert.Equal(4, blank.ExitCode);
        Assert.Equal("repo", InitCommand.RepositoryName("/srv/git/repo.git"));
        Assert.Equal("maquettiste", InitCommand.RepositoryName("ssh://git@example.com:22/mattjcowan/maquettiste"));
        Assert.Null(InitCommand.RepositoryName("  "));
    }

    private static string NameOf(CliRepo repo) => (string)JsonNode.Parse(repo.Read(".maquettiste/maquettiste.json"))!["name"]!;
}
