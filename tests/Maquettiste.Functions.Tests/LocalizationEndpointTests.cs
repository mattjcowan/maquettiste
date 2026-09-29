using System.Text;
using System.Text.Json.Nodes;
using Maquettiste.Functions.Tests.Support;

namespace Maquettiste.Functions.Tests;

/// <summary>
/// The operations of reference-types-seeds-localization.md section 3.9 over HTTP, on the reference-data fixture (en, fr, fr-CA falling
/// back to fr), each response checked against the contract.
/// </summary>
public sealed class LocalizationEndpointTests
{
    private const string UnitOfMeasure = "01JBM9S346Q3D25VT4F5V37E3S";
    private const string UnitSeed = "01JB9EE0ZBGJ09TQM83XSSSS6Y";
    private const string Kilogram = "01JBS3C4DWA7N36096Q14DR9GP";
    private const string Gram = "01JBQY77ZXYYK596NGYA1DQ91K";
    private const string Shard = ".maquettiste/model/locales/fr/_reference-data.json";
    private const string UnitAttribute = "01JRDR00000000000000000022";

    [Fact]
    public async Task Status_lists_the_translated_locales_with_their_chain_and_shard_counts()
    {
        await using var host = EditorHost.CreateReferenceData();

        var response = await host.GetAsync("/api/localization");

        Assert.Equal(200, response.Status);
        Contract.AssertResponse(response, "/api/localization");
        Assert.Equal("en", response.Json["defaultLocale"]!.GetValue<string>());
        var locales = response.Json["locales"]!.AsArray();
        Assert.Equal(["fr", "fr-CA"], locales.Select(l => l!["locale"]!.GetValue<string>()));
        Assert.Equal(["fr-CA", "fr", "en"], locales[1]!["chain"]!.AsArray().Select(l => l!.GetValue<string>()));
        var shard = locales[0]!["shards"]!.AsArray().Single(s => s!["shard"]!.GetValue<string>() == Shard)!;
        Assert.True(shard["expected"]!.GetValue<int>() >= shard["translated"]!.GetValue<int>() + shard["missing"]!.GetValue<int>());
    }

    [Fact]
    public async Task Entries_by_owner_by_shard_and_the_paged_queue_and_404_for_an_undeclared_locale()
    {
        await using var host = EditorHost.CreateReferenceData();

        var byOwner = await host.GetAsync("/api/localization/fr/entries?owner=" + UnitSeed);
        var queue = await host.GetAsync("/api/localization/fr-CA/entries?missing=true");
        var unknown = await host.GetAsync("/api/localization/de/entries");
        var byDefault = await host.GetAsync("/api/localization/en/entries");

        Assert.Equal(200, byOwner.Status);
        Contract.AssertResponse(byOwner, "/api/localization/{locale}/entries");
        var kilo = byOwner.Json["entries"]!.AsArray().Single(e => e!["id"]!.GetValue<string>() == Kilogram && e["field"]!.GetValue<string>() == "label")!;
        Assert.Equal("Kilogramme", kilo["translation"]!.GetValue<string>());
        Assert.Equal(Shard, kilo["shard"]!.GetValue<string>());
        Assert.Matches("^[0-9a-f]{64}$", kilo["shardHash"]!.GetValue<string>());
        Assert.All(byOwner.Json["entries"]!.AsArray(), e => Assert.Equal(UnitSeed, e!["owner"]!.GetValue<string>()));
        Assert.Equal(200, queue.Status);
        Contract.AssertResponse(queue, "/api/localization/{locale}/entries");
        Assert.All(queue.Json["entries"]!.AsArray(), e => Assert.NotEqual("translated", e!["state"]!.GetValue<string>()));
        Assert.Contains(queue.Json["entries"]!.AsArray(), e => e!["id"]!.GetValue<string>() == Kilogram && e["state"]!.GetValue<string>() == "fallback" && e["effective"]!.GetValue<string>() == "Kilogramme");
        Assert.Equal(404, unknown.Status);
        Contract.AssertResponse(unknown, "/api/localization/{locale}/entries");
        Assert.Equal(404, byDefault.Status);
    }

    [Fact]
    public async Task A_write_saves_answers_409_on_a_changed_shard_422_on_an_unknown_node_and_publishes_display_names()
    {
        await using var host = EditorHost.CreateReferenceData();
        host.StartBackground();
        var read = await host.GetAsync("/api/localization/fr/entries?owner=" + UnitOfMeasure);
        var hash = read.Json["entries"]![0]!["shardHash"]!.GetValue<string>();

        var saved = await host.SendJsonAsync("PUT", "/api/localization/fr/entries", new JsonObject
        {
            ["entries"] = new JsonArray(new JsonObject { ["id"] = UnitOfMeasure, ["field"] = "displayName", ["value"] = "Unité" }),
            ["expected"] = new JsonObject { [Shard] = hash },
        });
        var stale = await host.SendJsonAsync("PUT", "/api/localization/fr/entries", new JsonObject
        {
            ["entries"] = new JsonArray(new JsonObject { ["id"] = Gram, ["field"] = "label", ["value"] = "Gramme" }),
            ["expected"] = new JsonObject { [Shard] = hash },
        });
        var invalid = await host.SendJsonAsync("PUT", "/api/localization/fr/entries", new JsonObject
        {
            ["entries"] = new JsonArray(new JsonObject { ["id"] = "01J92P0V0FJ23CGSNKM7P1W5V9", ["field"] = "label", ["value"] = "x" }),
            ["expected"] = new JsonObject(),
        });

        Assert.Equal(200, saved.Status);
        Contract.AssertResponse(saved, "/api/localization/{locale}/entries");
        var newHash = saved.Json["hashes"]![Shard]!.GetValue<string>();
        Assert.NotEqual(hash, newHash);
        Assert.Equal(409, stale.Status);
        Contract.AssertResponse(stale, "/api/localization/{locale}/entries");
        Assert.Equal(newHash, stale.Json["hashes"]![Shard]!.GetValue<string>());
        Assert.Equal(422, invalid.Status);
        Contract.AssertResponse(invalid, "/api/localization/{locale}/entries");
        Assert.Equal("MQ7203", invalid.Json["diagnostics"]![0]!["rule"]!.GetValue<string>());

        await EditorHost.WaitForAsync(() => host.Published("model.changed").Any(e => e.Payload["translations"] is not null), "model.changed with translations");
        var changed = host.Published("model.changed").First(e => e.Payload["translations"] is not null).Payload;
        Contract.AssertEvent("model.changed", changed);
        var translations = changed["translations"]!.AsArray();
        Assert.Equal(["fr", "fr-CA"], translations.Select(t => t!["locale"]!.GetValue<string>()));
        Assert.All(translations, t => Assert.Equal("Unité", t!["displayNames"]![UnitOfMeasure]!.GetValue<string>()));
    }

    [Fact]
    public async Task The_index_in_a_locale_fills_display_names_from_the_chain_with_its_own_etag()
    {
        await using var host = EditorHost.CreateReferenceData();

        var plain = await host.GetAsync("/api/model/index");
        var french = await host.GetAsync("/api/model/index?locale=fr-CA");
        var again = await host.GetAsync("/api/model/index?locale=fr-CA", r => r.Header("If-None-Match", french.Headers.ETag.ToString()));
        var unknown = await host.GetAsync("/api/model/index?locale=de");

        Assert.Equal(200, french.Status);
        Contract.AssertResponse(french, "/api/model/index");
        string Name(TestResponse r) => r.Json.AsArray().Single(x => x!["id"]!.GetValue<string>() == UnitOfMeasure)!["displayName"]!.GetValue<string>();
        Assert.Equal("Unit of measure", Name(plain));
        Assert.Equal("Unité de mesure", Name(french));
        Assert.NotEqual(plain.Headers.ETag.ToString(), french.Headers.ETag.ToString());
        Assert.Equal(304, again.Status);
        Assert.Equal(400, unknown.Status);
        Contract.AssertResponse(unknown, "/api/model/index");
    }

    [Fact]
    public async Task Xliff_and_csv_exports_round_trip_and_an_import_previews_then_applies()
    {
        await using var host = EditorHost.CreateReferenceData();

        var xliff = await host.GetAsync("/api/localization/fr/export?format=xliff");
        var csv = await host.GetAsync("/api/localization/fr/export?format=csv");
        var same = await host.SendJsonAsync("POST", "/api/localization/fr/import", new JsonObject { ["format"] = "xliff", ["content"] = xliff.Text });
        var edited = xliff.Text.Replace("<target>Kilogramme</target>", "<target>Kilo</target>", StringComparison.Ordinal);
        var preview = await host.SendJsonAsync("POST", "/api/localization/fr/import", new JsonObject { ["format"] = "xliff", ["content"] = edited });
        var applied = await host.SendJsonAsync("POST", "/api/localization/fr/import?dryRun=false", new JsonObject { ["format"] = "xliff", ["content"] = edited });
        var csvSame = await host.SendJsonAsync("POST", "/api/localization/fr/import", new JsonObject { ["format"] = "csv", ["content"] = (await host.GetAsync("/api/localization/fr/export?format=csv")).Text });
        var bad = await host.SendJsonAsync("POST", "/api/localization/fr/import", new JsonObject { ["format"] = "xliff", ["content"] = "<nope" });

        Assert.Equal(200, xliff.Status);
        Contract.AssertResponse(xliff, "/api/localization/{locale}/export");
        Assert.Contains("id=\"" + Kilogram + "/label\"", xliff.Text, StringComparison.Ordinal);
        Assert.Contains("subState=\"maquettiste:", xliff.Text, StringComparison.Ordinal);
        Assert.Equal(200, csv.Status);
        Contract.AssertResponse(csv, "/api/localization/{locale}/export");
        Assert.StartsWith("id,field,source,translation,state,shard\n", csv.Text, StringComparison.Ordinal);
        Assert.Equal(0, same.Json["added"]!.GetValue<int>());
        Assert.Empty(same.Json["changed"]!.AsArray());
        Contract.AssertResponse(preview, "/api/localization/{locale}/import");
        Assert.Equal("Kilo", preview.Json["changed"]![0]!["after"]!.GetValue<string>());
        Assert.False(preview.Json["applied"]!.GetValue<bool>());
        Assert.Equal(200, applied.Status);
        Assert.True(applied.Json["applied"]!.GetValue<bool>());
        Assert.Contains("Kilo\"", File.ReadAllText(host.PathOf(Shard)), StringComparison.Ordinal);
        Assert.Empty(csvSame.Json["changed"]!.AsArray());
        Assert.Equal(400, bad.Status);
    }

    [Fact]
    public async Task Seed_csv_exports_with_locale_columns_and_bom_form_and_round_trips_without_change()
    {
        await using var host = EditorHost.CreateReferenceData();

        var csv = await host.GetAsync("/api/seeds/" + UnitSeed + "/csv?locale=fr");
        var bom = await host.GetAsync("/api/seeds/" + UnitSeed + "/csv?bom=true");
        var same = await host.SendJsonAsync("POST", "/api/seeds/" + UnitSeed + "/csv?dryRun=false", new JsonObject { ["content"] = csv.Text });
        var missing = await host.GetAsync("/api/seeds/" + UnitOfMeasure + "/csv");

        Assert.Equal(200, csv.Status);
        Contract.AssertResponse(csv, "/api/seeds/{id}/csv");
        var lines = csv.Text.Split('\n');
        Assert.Equal("@id,@code,@label,factor,symbol,@label:fr,@description:fr", lines[0]);
        Assert.Equal(Kilogram + ",kg,Kilogram,1000,kg,Kilogramme,", lines[1]);
        Assert.Equal("01JB5GQAPENECFSECZP11HYGCP,pinch,Pinch,0.36,,,", lines[3]);
        Assert.StartsWith("﻿@id,", bom.Text, StringComparison.Ordinal);
        Assert.Contains("\r\n", bom.Text, StringComparison.Ordinal);
        Assert.Equal(200, same.Status);
        Contract.AssertResponse(same, "/api/seeds/{id}/csv");
        Assert.Equal(0, same.Json["added"]!.GetValue<int>());
        Assert.Empty(same.Json["changed"]!.AsArray());
        Assert.Equal(0, same.Json["removed"]!.GetValue<int>());
        Assert.Equal(404, missing.Status);
    }

    [Fact]
    public async Task Seed_csv_import_previews_merges_by_code_applies_with_translations_replaces_and_answers_409()
    {
        await using var host = EditorHost.CreateReferenceData();
        var seed = await host.GetAsync("/api/model/elements/" + UnitSeed);
        var hash = seed.Json["hash"]!.GetValue<string>();
        const string Text = "@code,@label,factor,@label:fr,unknown\nkg,Kilogram,1000,Kilogramme,x\ng,Gramme metric,1,Gramme,\nmg,Milligram,0.001,Milligramme,\n";

        var preview = await host.SendJsonAsync("POST", "/api/seeds/" + UnitSeed + "/csv", new JsonObject { ["content"] = Text });
        var stale = await host.SendJsonAsync("POST", "/api/seeds/" + UnitSeed + "/csv?dryRun=false", new JsonObject { ["content"] = Text }, r => r.IfMatch(new string('0', 64)));
        var applied = await host.SendJsonAsync("POST", "/api/seeds/" + UnitSeed + "/csv?dryRun=false", new JsonObject { ["content"] = Text }, r => r.IfMatch(hash));
        var replaced = await host.SendJsonAsync("POST", "/api/seeds/" + UnitSeed + "/csv?mode=replace", new JsonObject { ["content"] = "@code,@label\nkg,Kilogram\n" });
        var badMode = await host.SendJsonAsync("POST", "/api/seeds/" + UnitSeed + "/csv?mode=upsert", new JsonObject { ["content"] = Text });

        Assert.Equal(200, preview.Status);
        Contract.AssertResponse(preview, "/api/seeds/{id}/csv");
        Assert.Equal(1, preview.Json["added"]!.GetValue<int>());
        Assert.Equal(["unknown"], preview.Json["ignoredHeaders"]!.AsArray().Select(h => h!.GetValue<string>()));
        var changes = preview.Json["changed"]!.AsArray();
        Assert.Contains(changes, c => c!["id"]!.GetValue<string>() == Gram && c["after"]!["@label"]!.GetValue<string>() == "Gramme metric");
        Assert.Contains(changes, c => c!["id"]!.GetValue<string>() == Gram && c["locale"]?.GetValue<string>() == "fr");
        Assert.Equal(409, stale.Status);
        Contract.AssertResponse(stale, "/api/seeds/{id}/csv");
        Assert.Equal(200, applied.Status);
        Assert.True(applied.Json["applied"]!.GetValue<bool>());
        var after = await host.GetAsync("/api/model/elements/" + UnitSeed);
        Assert.Equal(4, after.Json["json"]!["rows"]!.AsArray().Count);
        var french = await host.GetAsync("/api/localization/fr/entries?owner=" + UnitSeed);
        Assert.Contains(french.Json["entries"]!.AsArray(), e => e!["translation"]?.GetValue<string>() == "Milligramme");
        // g is used by a cell of the contains seed, so replace keeps it and lists it as blocked.
        Assert.Equal(2, replaced.Json["removed"]!.GetValue<int>());
        Assert.Equal([Gram], replaced.Json["blocked"]!.AsArray().Select(b => b!["id"]!.GetValue<string>()));
        Assert.Equal(400, badMode.Status);
    }

    [Fact]
    public async Task Reference_type_usage_lists_the_typed_attributes_and_404s_for_other_ids()
    {
        await using var host = EditorHost.CreateReferenceData();

        var usage = await host.GetAsync("/api/reference-types/" + UnitOfMeasure + "/usage");
        var other = await host.GetAsync("/api/reference-types/" + UnitSeed + "/usage");

        Assert.Equal(200, usage.Status);
        Contract.AssertResponse(usage, "/api/reference-types/{id}/usage");
        var use = usage.Json["usages"]!.AsArray().Single(u => u!["attribute"]!.GetValue<string>() == UnitAttribute)!;
        Assert.Equal("01JRDR00000000000000000001", use["owner"]!.GetValue<string>());
        Assert.False(use["collection"]!.GetValue<bool>());
        Assert.Equal(404, other.Status);
    }

    [Fact]
    public async Task A_seed_of_an_entity_with_an_attribute_named_id_exports_both_headers()
    {
        // SPEC section 6's Invoice has an attribute named id: its column exports as "id" beside the row's "@id".
        await using var host = EditorHost.Create(packs: false);
        const string IdAttribute = "01J92P0V0Q9EK961M5HAQ3C5MY";
        const string Row = "01JBS3C4DWA7N36096Q14DR9GQ";
        Directory.CreateDirectory(host.PathOf(".maquettiste/model/seeds/invoice"));
        File.WriteAllText(host.PathOf(".maquettiste/model/seeds/invoice/demo.json"), $$"""
            {
              "$schema": "../../../.schema/v1/seed.json",
              "kind": "seed",
              "id": "01JBS3C4DWA7N36096Q14DR9GR",
              "name": "Demo",
              "target": "{{EditorHost.InvoiceId}}",
              "columns": ["{{IdAttribute}}"],
              "rows": [
                { "id": "{{Row}}", "values": ["0f8fad5b-d9cb-469f-a165-70867728950e"] }
              ]
            }
            """);

        var csv = await host.GetAsync("/api/seeds/01JBS3C4DWA7N36096Q14DR9GR/csv");

        Assert.Equal(200, csv.Status);
        Assert.Equal(["@id,id", Row + ",0f8fad5b-d9cb-469f-a165-70867728950e", ""], csv.Text.Split('\n'));
    }
}
