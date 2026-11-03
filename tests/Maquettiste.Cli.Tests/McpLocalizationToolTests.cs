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
    public async Task Translations_handle_property_fields()
    {
        const string recipe = "01JRDE00000000000000000001";
        const string root = ".maquettiste/model/locales/fr/_root.json";
        var ct = TestContext.Current.CancellationToken;
        using var repo = CliRepo.ReferenceData();
        repo.Write(".maquettiste/extensions/texts.json",
            """{ "name": "texts", "appliesTo": { "kinds": ["entity"] }, "properties": { "footer": { "type": "string", "x-translatable": true }, "note": { "type": "string" } } }""");
        repo.Replace(".maquettiste/model/entities/recipe.json", "\"name\": \"Recipe\",",
            "\"name\": \"Recipe\",\n  \"properties\": { \"footer\": \"Thank you for cooking.\", \"note\": \"Plain.\" },");
        await using var session = await McpSession.StartAsync(repo, ct);

        var read = await session.CallAsync("get_translations", new { locale = "fr", owner = recipe });
        var written = await session.CallAsync("set_translations", new
        {
            locale = "fr",
            entries = new[] { new { id = recipe, field = "properties/footer", value = "Merci d'avoir cuisiné." } },
            expected = new Dictionary<string, string>(),
        });
        var plain = await session.CallAsync("set_translations", new { locale = "fr", entries = new[] { new { id = recipe, field = "properties/note", value = "x" } } });
        var after = await session.CallAsync("get_translations", new { locale = "fr", owner = recipe });
        var status = await session.CallAsync("localization_status");

        Assert.False(read.IsError, read.ToString());
        var row = Assert.Single(read.Json["entries"]!.AsArray(), e => (string)e!["field"]! == "properties/footer")!;
        Assert.Equal(("Thank you for cooking.", "missing", root), ((string?)row["source"], (string?)row["state"], (string?)row["shard"]));
        Assert.DoesNotContain(read.Json["entries"]!.AsArray(), e => (string)e!["field"]! == "properties/note");
        Assert.False(written.IsError, written.ToString());
        Assert.Equal("saved", (string)written.Json["outcome"]!);
        Assert.Equal("invalid", plain.Code);
        var translated = Assert.Single(after.Json["entries"]!.AsArray(), e => (string)e!["field"]! == "properties/footer")!;
        Assert.Equal(("Merci d'avoir cuisiné.", "translated"), ((string?)translated["translation"], (string?)translated["state"]));
        Assert.Contains("\"footer\": \"Merci d'avoir cuisiné.\"", repo.Read(root), StringComparison.Ordinal);
        Assert.Contains("\"properties/footer\": ", repo.Read(root), StringComparison.Ordinal);
        var shard = status.Json["locales"]!.AsArray().Single(l => (string)l!["locale"]! == "fr")!["shards"]!.AsArray().Single(s => (string)s!["shard"]! == root)!;
        Assert.True((int)shard["translated"]! >= 1, shard.ToJsonString());
    }

    /// <summary>Content texts of a custom element's members (next-phase-2-design.md section 4.2): <c>content/&lt;name&gt;</c> under
    /// the member's id, in its owner's shard.</summary>
    [Fact]
    public async Task Translations_handle_content_fields()
    {
        const string message = "01JRDC00000000000000000002";
        const string subject = "01JRDM00000000000000000001";
        const string root = ".maquettiste/model/locales/fr/_root.json";
        var ct = TestContext.Current.CancellationToken;
        using var repo = CliRepo.ReferenceData();
        repo.Write(".maquettiste/extensions/kinds/message.json",
            """{ "type": "message", "properties": { "parts": { "type": "array", "x-member": true, "items": { "type": "object", "properties": { "name": { "type": "string" }, "text": { "type": "string", "x-translatable": true } } } } } }""");
        repo.Write(".maquettiste/model/custom/message/invoice-sent.json", $$"""
            { "kind": "custom", "id": "{{message}}", "name": "invoice_sent", "type": "message",
              "content": { "parts": [ { "id": "{{subject}}", "name": "subject", "text": "Your invoice" } ] } }
            """);
        await using var session = await McpSession.StartAsync(repo, ct);

        var before = await session.CallAsync("localization_status");
        var read = await session.CallAsync("get_translations", new { locale = "fr", owner = message });
        var written = await session.CallAsync("set_translations", new
        {
            locale = "fr",
            entries = new[] { new { id = subject, field = "content/text", value = "Votre facture" } },
            expected = new Dictionary<string, string>(),
        });
        var plain = await session.CallAsync("set_translations", new { locale = "fr", entries = new[] { new { id = subject, field = "content/name", value = "x" } } });
        var after = await session.CallAsync("get_translations", new { locale = "fr", owner = message });
        var status = await session.CallAsync("localization_status");

        Assert.False(read.IsError, read.ToString());
        var row = Assert.Single(read.Json["entries"]!.AsArray(), e => (string)e!["field"]! == "content/text")!;
        Assert.Equal((subject, "Your invoice", "missing", root), ((string?)row["id"], (string?)row["source"], (string?)row["state"], (string?)row["shard"]));
        Assert.False(written.IsError, written.ToString());
        Assert.Equal("invalid", plain.Code);
        var translated = Assert.Single(after.Json["entries"]!.AsArray(), e => (string)e!["field"]! == "content/text")!;
        Assert.Equal(("Votre facture", "translated"), ((string?)translated["translation"], (string?)translated["state"]));
        Assert.Contains("\"content/text\": ", repo.Read(root), StringComparison.Ordinal);
        static int Translated(ToolReply result, string shard) => (int)result.Json["locales"]!.AsArray().Single(l => (string)l!["locale"]! == "fr")!["shards"]!
            .AsArray().Single(s => (string)s!["shard"]! == shard)!["translated"]!;
        Assert.Equal(Translated(before, root) + 1, Translated(status, root));
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
