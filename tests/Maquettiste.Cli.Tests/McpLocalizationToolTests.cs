using System.Text.Json.Nodes;

namespace Maquettiste.Cli.Tests;

/// <summary>The MCP tools of reference-types-seeds-localization.md section 3.9, mirroring the API's shapes, on the reference-data fixture.</summary>
public sealed class McpLocalizationToolTests
{
    private const string UnitOfMeasure = "01JBM9S346Q3D25VT4F5V37E3S";
    private const string UnitSeed = "01JB9EE0ZBGJ09TQM83XSSSS6Y";
    private const string Gram = "01JBQY77ZXYYK596NGYA1DQ91K";
    private const string Shard = ".maquettiste/model/locales/fr/_reference-data.json";

    [Fact]
    public async Task Status_translations_read_write_conflict_and_invalid()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var session = await McpSession.StartAsync(CliRepo.ReferenceData(), ct);

        var status = await session.CallAsync("localization_status");
        var read = await session.CallAsync("get_translations", new { locale = "fr", owner = UnitSeed });
        var unknown = await session.CallAsync("get_translations", new { locale = "de" });
        var hash = (string)read.Json["entries"]![0]!["shardHash"]!;
        var written = await session.CallAsync("set_translations", new
        {
            locale = "fr",
            entries = new[] { new { id = Gram, field = "label", value = "Gramme" } },
            expected = new Dictionary<string, string> { [Shard] = hash },
        });
        var stale = await session.CallAsync("set_translations", new
        {
            locale = "fr",
            entries = new[] { new { id = Gram, field = "label", value = "G" } },
            expected = new Dictionary<string, string> { [Shard] = hash },
        });
        var invalid = await session.CallAsync("set_translations", new { locale = "fr", entries = new[] { new { id = UnitOfMeasure, field = "label", value = "x" } } });
        var after = await session.CallAsync("get_translations", new { locale = "fr", owner = UnitSeed });

        Assert.False(status.IsError, status.ToString());
        Assert.Equal("en", (string)status.Json["defaultLocale"]!);
        Assert.Equal(["fr", "fr-CA"], status.Json["locales"]!.AsArray().Select(l => (string)l!["locale"]!));
        Assert.False(read.IsError, read.ToString());
        Assert.Equal("not-found", unknown.Code);
        Assert.False(written.IsError, written.ToString());
        Assert.Equal("saved", (string)written.Json["outcome"]!);
        Assert.Equal("conflict", stale.Code);
        Assert.Equal("invalid", invalid.Code);
        Assert.Contains(after.Json["entries"]!.AsArray(), e => (string)e!["id"]! == Gram && (string?)e["translation"] == "Gramme");
    }

    [Fact]
    public async Task Seed_csv_export_import_preview_apply_and_reference_type_usage()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var session = await McpSession.StartAsync(CliRepo.ReferenceData(), ct);

        var csv = await session.CallAsync("export_seed_csv", new { id = UnitSeed, locales = new[] { "fr" } });
        var same = await session.CallAsync("import_seed_csv", new { id = UnitSeed, csv = csv.Text });
        var preview = await session.CallAsync("import_seed_csv", new { id = UnitSeed, csv = "@code,@label,factor\nmg,Milligram,0.001\n" });
        var element = await session.CallAsync("get_element", new { id = UnitSeed });
        var applied = await session.CallAsync("import_seed_csv", new { id = UnitSeed, csv = "@code,@label,factor\nmg,Milligram,0.001\n", apply = true, expectedHash = (string)element.Json["hash"]! });
        var stale = await session.CallAsync("import_seed_csv", new { id = UnitSeed, csv = "@code,@label,factor\nmcg,Microgram,0.000001\n", apply = true, expectedHash = (string)element.Json["hash"]! });
        var usage = await session.CallAsync("reference_type_usage", new { id = UnitOfMeasure });
        var notSeed = await session.CallAsync("export_seed_csv", new { id = UnitOfMeasure });

        Assert.False(csv.IsError, csv.ToString());
        Assert.StartsWith("@id,@code,@label,factor,symbol,@label:fr,@description:fr\n", csv.Text, StringComparison.Ordinal);
        Assert.Equal(0, (int)same.Json["added"]!);
        Assert.Empty(same.Json["changed"]!.AsArray());
        Assert.Equal(1, (int)preview.Json["added"]!);
        Assert.False((bool)preview.Json["applied"]!);
        Assert.False(applied.IsError, applied.ToString());
        Assert.True((bool)applied.Json["applied"]!);
        Assert.Equal("conflict", stale.Code);
        Assert.False(usage.IsError, usage.ToString());
        Assert.Contains(usage.Json["usages"]!.AsArray(), u => (string)u!["attribute"]! == "01JRDR00000000000000000022");
        Assert.Equal("not-found", notSeed.Code);
    }
}
