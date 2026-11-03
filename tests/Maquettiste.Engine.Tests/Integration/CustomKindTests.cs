using System.Text;
using System.Text.Json.Nodes;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Pipeline;
using Maquettiste.Testing;
using static Maquettiste.Engine.Tests.CustomKinds.CustomFixture;

namespace Maquettiste.Engine.Tests.Integration;

/// <summary>
/// The engine acceptance tests of project-defined kinds (next-phase-2-design.md section 4.2, tasks E28 and E29), each on a copy of the
/// billing fixture (<see cref="E2ERepo"/>) driven through the store and the generation service. The copy declares the invented kind
/// <c>setting</c> in <c>extensions/kinds/setting.json</c> and holds the setting <c>due_days</c> (value 30) in package Billing; the
/// second test declares <c>message</c>, whose parts are members, and the locales <c>en</c> and <c>fr</c>. Each test writes its own
/// small pack under <c>templates/</c>.
/// </summary>
[Collection(IntegrationCollection.Name)]
public sealed class CustomKindTests
{
    private const string PaymentTerm = "01JR6CKP000000000000000001";
    private const string PaymentTermSeed = "01JR6CKP000000000000000010";
    private const string Net30Row = "01JR6CKP000000000000000011";
    private const string DefaultPaymentTerm = "01JR6CKP000000000000000020";
    private const string Send = "01JR6CKS000000000000000001";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---- setup ----

    /// <summary>A billing copy with the given custom kind files (<c>setting</c> by default) and, when asked, <c>due_days</c>.</summary>
    private static async Task<E2ERepo> CopyAsync(Action<JsonObject>? settings = null, bool withDueDays = true, params (string Type, string Json)[] kinds)
    {
        var repo = E2ERepo.Create(demo: false, settings: settings);
        foreach (var (type, json) in kinds.Length == 0 ? [("setting", SettingKind)] : kinds)
            repo.Repo.WriteFile(".maquettiste/extensions/kinds/" + type + ".json", json);
        await repo.RestartAsync();
        if (withDueDays)
            await repo.CreateElementAsync(DueDaysJson());
        return repo;
    }

    /// <summary>Writes a pack under <c>templates/&lt;name&gt;</c> with the given units (JSON objects) and template files.</summary>
    private static void WritePack(E2ERepo repo, string name, string units, params (string File, string Text)[] templates)
    {
        var pack = Path.Combine(repo.Repo.ModelRoot, "templates", name);
        Directory.CreateDirectory(pack);
        File.WriteAllText(Path.Combine(pack, "pack.json"), $$"""{"name": "{{name}}", "version": "1.0.0", "engine": ">=1.0", "units": [{{units}}]}""");
        foreach (var (file, text) in templates)
            File.WriteAllText(Path.Combine(pack, file), text);
    }

    private static async Task<IReadOnlyList<Diagnostic>> DiagnosticsAsync(E2ERepo repo) =>
        (await repo.Store.ValidateAsync(ValidationScope.All, Ct)).Diagnostics;

    private static string Describe(IEnumerable<Diagnostic> diagnostics) =>
        string.Join("\n", diagnostics.Select(d => $"{d.Rule} {d.FilePath} {d.JsonPointer} {d.Message}"));

    /// <summary>The keys of the units a plan would render, ordinal.</summary>
    private static async Task<List<string>> RenderedAsync(E2ERepo repo)
    {
        var result = await repo.Service.PlanAsync(new GenerationRequest { Jobs = 2 }, null, Ct);
        Assert.Equal(RunOutcome.Succeeded, result.Outcome);
        return [.. result.Plan!.Units.Where(u => !u.Skipped).Select(u => u.Key).Order(StringComparer.Ordinal)];
    }

    private static async Task<SaveResult> DeleteAsync(E2ERepo repo, string id) =>
        await repo.Store.DeleteAsync(id, (await repo.Store.GetElementAsync(id, Ct))!.Hash, DeleteResolution.Refuse, ChangeSource.Editor, Ct);

    /// <summary>The translated count of the <c>fr</c> shard of package Billing (0 while it has no shard).</summary>
    private static async Task<int> FrenchBillingTranslatedAsync(E2ERepo repo) =>
        (await repo.Store.GetLocalizationStatusAsync(Ct)).Locales.Single(l => l.Locale == "fr").Shards
            .SingleOrDefault(s => s.Shard.EndsWith("locales/fr/billing.json", StringComparison.Ordinal))?.Translated ?? 0;

    // ---- acceptance ----

    [Fact]
    public async Task A_setting_in_billing_reaches_templates_by_name_and_id()
    {
        await using var repo = await CopyAsync();
        WritePack(repo, "cfg", """
            {"id": "value", "template": "value.scriban", "for": "each custom:setting", "output": "src/Generated/cfg/{{ custom.name }}.txt"},
            {"id": "all", "template": "all.scriban", "for": "model", "output": "src/Generated/cfg/all.txt"}
            """,
            ("value.scriban", "{{ custom.name }}={{ custom.content.value }}\n"),
            ("all.scriban", "{{ (custom_named 'setting' 'due_days').content.value }} {{ (lookup '" + DueDays + "').content.value }}\n"));

        var diagnostics = await DiagnosticsAsync(repo);
        await repo.ApplyAsync();

        Assert.True(diagnostics.Count == 0, Describe(diagnostics));
        Assert.Equal("due_days=30\n", repo.Repo.ReadFile("src/Generated/cfg/due_days.txt"));
        Assert.Equal("30 30\n", repo.Repo.ReadFile("src/Generated/cfg/all.txt"));

        // A changed value re-renders both units; a second run with nothing changed renders none.
        await repo.EditAsync(DueDays, n => n["content"]!["value"] = 45);
        Assert.Equal(["cfg/all", "cfg/value:" + DueDays], (await RenderedAsync(repo)).Where(k => k.StartsWith("cfg/", StringComparison.Ordinal)));
        await repo.ApplyAsync();
        Assert.Equal("due_days=45\n", repo.Repo.ReadFile("src/Generated/cfg/due_days.txt"));
        Assert.Equal("45 45\n", repo.Repo.ReadFile("src/Generated/cfg/all.txt"));
        Assert.Empty(await RenderedAsync(repo));
    }

    [Fact]
    public async Task A_message_has_parts_with_ids_and_a_translated_text_per_part()
    {
        await using var repo = await CopyAsync(s => s["localization"] = JsonNode.Parse("""{ "defaultLocale": "en", "locales": ["en", "fr"] }"""),
            withDueDays: false, ("message", MessageKind));
        var parsed = repo.Store.ParseBatch(Encoding.UTF8.GetBytes($$"""
            { "operations": [
              { "op": "create", "element": { "kind": "custom", "name": "invoice_sent", "type": "message", "package": "{{Billing}}",
                  "content": { "parts": [ { "name": "subject", "text": "Invoice sent" }, { "name": "body", "text": "Your invoice is attached." } ] } } }
            ] }
            """));
        Assert.True(parsed.Batch is not null, Describe(parsed.Diagnostics));

        var applied = await repo.Store.ApplyBatchAsync(parsed.Batch, ChangeSource.Editor, Ct);

        Assert.True(applied.Outcome == SaveOutcome.Saved, Describe(applied.Items.SelectMany(i => i.Diagnostics)));
        // The minted names are the longest forms; the forms without the package name the same members.
        var members = applied.Ids.Minted.Where(m => m.Kind == "member").ToList();
        Assert.Equal(["@member:message.Billing.invoice_sent.subject", "@member:message.Billing.invoice_sent.body"], members.Select(m => m.Name));
        Assert.Equal(["/content/parts/0", "/content/parts/1"], members.Select(m => m.Pointer));
        var snapshot = await repo.Store.GetSnapshotAsync(Ct);
        Assert.Equal(members[0].Id, Assert.Single(snapshot.Names("member").Find("message.invoice_sent.subject")).Id);
        Assert.Equal(members[1].Id, Assert.Single(snapshot.Names("member").Find("message.invoice_sent.body")).Id);
        var (subject, body) = (members[0].Id, members[1].Id);

        var before = await FrenchBillingTranslatedAsync(repo);
        var page = await repo.Store.GetTranslationsAsync("fr", null, null, Ct);
        var saved = await repo.Store.SaveTranslationsAsync("fr",
            [new TranslationEdit(subject, "content/text", "Facture envoyée"), new TranslationEdit(body, "content/text", "Votre facture est jointe.")],
            page.ShardHashes, ChangeSource.Editor, Ct);

        Assert.True(saved.Outcome == SaveOutcome.Saved, Describe(saved.Diagnostics));
        Assert.Equal(before + 2, await FrenchBillingTranslatedAsync(repo));
        var shard = repo.Repo.ReadFile(".maquettiste/model/locales/fr/billing.json");
        Assert.Contains(subject, shard, StringComparison.Ordinal);
        Assert.Contains(body, shard, StringComparison.Ordinal);

        WritePack(repo, "msg", """
            {"id": "fr", "template": "fr.scriban", "for": "each custom:message", "output": "src/Generated/msg/{{ custom.name }}.fr.txt"}
            """,
            ("fr.scriban", "{{ for m in custom.members }}{{ m.name }}={{ translate m 'content/text' 'fr' }}\n{{ end }}"));
        await repo.ApplyAsync();
        Assert.Equal("subject=Facture envoyée\nbody=Votre facture est jointe.\n", repo.Repo.ReadFile("src/Generated/msg/invoice_sent.fr.txt"));
    }

    [Fact]
    public async Task A_setting_whose_code_is_a_row_survives_renaming_the_code()
    {
        await using var repo = await CopyAsync();
        await repo.CreateElementAsync($$"""
            { "kind": "reference-type", "id": "{{PaymentTerm}}", "name": "PaymentTerm",
              "code": { "id": "01JR6CKP000000000000000002", "length": 8 }, "label": { "id": "01JR6CKP000000000000000003", "length": 64 } }
            """);
        await repo.CreateElementAsync($$"""
            { "kind": "seed", "id": "{{PaymentTermSeed}}", "name": "PaymentTerms", "target": "{{PaymentTerm}}", "columns": ["code", "label"],
              "rows": [ { "id": "{{Net30Row}}", "values": ["net_30", "Net 30"] }, { "id": "01JR6CKP000000000000000012", "values": ["net_60", "Net 60"] } ] }
            """);
        await repo.CreateElementAsync($$"""
            { "kind": "custom", "id": "{{DefaultPaymentTerm}}", "name": "default_payment_term", "package": "{{Billing}}", "type": "setting",
              "content": { "code": "{{Net30Row}}" } }
            """);
        WritePack(repo, "cfg", """
            {"id": "term", "template": "term.scriban", "for": "model", "output": "src/Generated/cfg/term.txt"}
            """,
            ("term.scriban", "{{ (lookup (custom_named 'setting' 'default_payment_term').content.code).code }}\n"));
        await repo.ApplyAsync();
        Assert.Equal("net_30\n", repo.Repo.ReadFile("src/Generated/cfg/term.txt"));
        var path = (await repo.Store.GetElementAsync(DefaultPaymentTerm, Ct))!.Path;
        var file = repo.Repo.ReadFile(path);

        await PaymentTerms.ApplyAsync(repo, $$"""{ "op": "rename-code", "id": "{{PaymentTerm}}", "code": "net_30", "to": "net30" }""");

        var diagnostics = await DiagnosticsAsync(repo);
        Assert.True(diagnostics.Count == 0, Describe(diagnostics));
        Assert.Equal(file, repo.Repo.ReadFile(path));
        await repo.ApplyAsync();
        Assert.Equal("net30\n", repo.Repo.ReadFile("src/Generated/cfg/term.txt"));
    }

    [Fact]
    public async Task Deleting_a_setting_an_operation_requires_is_refused()
    {
        await using var repo = await CopyAsync();
        await repo.CreateElementAsync($$"""
            { "kind": "operation", "id": "{{Send}}", "name": "send", "package": "{{Billing}}", "subject": "{{E2ERepo.InvoiceId}}", "effect": "change",
              "outcomes": [ { "name": "applied" } ], "requires": [ "{{DueDays}}" ] }
            """);
        var diagnostics = await DiagnosticsAsync(repo);
        Assert.True(diagnostics.Count == 0, Describe(diagnostics));

        var refused = await DeleteAsync(repo, DueDays);

        Assert.Equal(SaveOutcome.Referenced, refused.Outcome);
        Assert.Contains(refused.Referrers, r => r is { FromElementId: Send, JsonPointer: "/requires/0" });

        await repo.EditAsync(Send, n => n.Remove("requires"));
        var deleted = await DeleteAsync(repo, DueDays);
        Assert.True(deleted.Outcome == SaveOutcome.Saved, Describe(deleted.Diagnostics));
        Assert.Null(await repo.Store.GetElementAsync(DueDays, Ct));
    }
}
