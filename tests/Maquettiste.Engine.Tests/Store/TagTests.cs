using System.Text;
using System.Text.Json.Nodes;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Tests.Editor;

namespace Maquettiste.Engine.Tests.Store;

/// <summary>
/// Tags across the model (2026-10-07): the usage of each tag a vocabulary governs, the whole model's MQ2006 as one finding per tag,
/// and <c>retag</c>, which removes tags or renames one in the vocabulary and in every use it governs, all or nothing.
/// </summary>
public sealed class TagTests
{
    private const string TagsVocabularyId = "01J92P0V22Q6775D5GE10SN3RB";
    private const string Ledger = "01K6TAG0000000000000000001";
    private const string LedgerId = "01K6TAG0000000000000000002";
    private const string LedgerNote = "01K6TAG0000000000000000003";

    private static CancellationToken Ct => EditorRepo.Ct;

    /// <summary>The billing fixture with its vocabulary not strict and an entity whose tags and attributes' tags use audit (undeclared).</summary>
    private static async Task<EditorRepo> RepoAsync()
    {
        var r = EditorRepo.Create(packs: false);
        var vocabulary = (await r.Store.GetElementAsync(TagsVocabularyId, Ct))!;
        var node = JsonNode.Parse(vocabulary.Json.GetRawText())!.AsObject();
        node["strict"] = false;
        var saved = await r.Store.SaveAsync(TagsVocabularyId, Encoding.UTF8.GetBytes(node.ToJsonString()), vocabulary.Hash, ChangeSource.Editor, Ct);
        Assert.Equal(SaveOutcome.Saved, saved.Outcome);
        await CreateAsync(r, $$"""
            {"kind":"entity","id":"{{Ledger}}","name":"Ledger","package":"{{EditorRepo.BillingPackageId}}","tags":["audit","billing"],
             "key":{"attributes":["{{LedgerId}}"],"strategy":"uuid-v7"},
             "attributes":[{"id":"{{LedgerId}}","name":"id","type":"uuid","required":true,"tags":["audit"]},
               {"id":"{{LedgerNote}}","name":"note","type":"string","tags":["audit","pii"]}]}
            """);
        return r;
    }

    private static async Task CreateAsync(EditorRepo r, string json)
    {
        var result = await r.Store.CreateAsync(Encoding.UTF8.GetBytes(JsonNode.Parse(json)!.ToJsonString()), ChangeSource.Editor, Ct);
        Assert.True(result.Outcome == SaveOutcome.Saved, string.Join("; ", result.Diagnostics.Select(d => d.Rule + " " + d.Message)));
    }

    private static async Task<BatchResult> ApplyAsync(EditorRepo r, string operation)
    {
        var parsed = r.Store.ParseBatch(Encoding.UTF8.GetBytes("{\"operations\":[" + operation + "]}"));
        Assert.Empty(parsed.Diagnostics);
        return await r.Store.ApplyBatchAsync(parsed.Batch!, ChangeSource.Editor, Ct);
    }

    private static IReadOnlyList<string> TagsOf(EditorRepo r, string id, string? attribute = null)
    {
        var entity = r.Store.Current!.Get<Entity>(id)!;
        return attribute is null ? entity.Tags : entity.Attributes.Single(a => a.Id == attribute).Tags;
    }

    [Fact]
    public async Task The_usage_counts_each_tag_the_vocabulary_governs_and_the_whole_model_reports_one_note_per_undeclared_tag()
    {
        await using var r = await RepoAsync();

        var usage = (await r.Store.GetTagUsageAsync(null, Ct))!;

        Assert.Equal(TagsVocabularyId, usage.Vocabulary);
        Assert.False(usage.Strict);
        var audit = usage.Tags.Single(t => t.Tag == "audit");
        Assert.Equal((false, 3), (audit.Declared, audit.Uses));
        Assert.Equal([Ledger], audit.Elements);
        Assert.Equal(["Ledger"], audit.Examples);
        Assert.True(usage.Tags.Single(t => t.Tag == "pii").Declared);
        Assert.Contains(Ledger, usage.Tags.Single(t => t.Tag == "billing").Elements);
        Assert.Equal(usage.Tags.Select(t => t.Tag).Order(StringComparer.Ordinal), usage.Tags.Select(t => t.Tag));
        Assert.Null(await r.Store.GetTagUsageAsync(EditorRepo.InvoiceId, Ct));

        // The whole model: one note for audit with its count; a validation of the entity alone still names each use.
        var whole = (await r.Store.ValidateAsync(ValidationScope.All, Ct)).Diagnostics.Where(d => d.Rule == "MQ2006").ToList();
        var note = Assert.Single(whole);
        Assert.Equal(DiagnosticSeverity.Info, note.Severity);
        Assert.Contains("'audit'", note.Message, StringComparison.Ordinal);
        Assert.Contains("3 uses in 1 element (Ledger)", note.Message, StringComparison.Ordinal);
        var scoped = (await r.Store.ValidateAsync(new ValidationScope([Ledger]), Ct)).Diagnostics.Where(d => d.Rule == "MQ2006").ToList();
        Assert.Equal(3, scoped.Count);
    }

    [Fact]
    public async Task Removing_several_tags_everywhere_drops_them_from_every_use_and_from_the_vocabulary_in_one_batch()
    {
        await using var r = await RepoAsync();
        var customer = EditorRepo.CustomerId;
        Assert.Contains("pii", TagsOf(r, customer));

        var result = await ApplyAsync(r, """{"op":"retag","tags":["audit","pii"]}""");

        Assert.Equal(SaveOutcome.Saved, result.Outcome);
        Assert.Equal(["billing"], TagsOf(r, Ledger));
        Assert.Empty(TagsOf(r, Ledger, LedgerId));
        Assert.Empty(TagsOf(r, Ledger, LedgerNote));
        Assert.DoesNotContain("pii", TagsOf(r, customer));
        Assert.Equal(["billing"], r.Store.Current!.Get<TagVocabulary>(TagsVocabularyId)!.Definitions.Select(d => d.Key));
        var validation = await r.Store.ValidateAsync(ValidationScope.All, Ct);
        Assert.DoesNotContain(validation.Diagnostics, d => d.Rule == "MQ2006");

        // Nothing uses or declares them now: refused, and so is a request without tags.
        var again = await ApplyAsync(r, """{"op":"retag","tags":["audit"]}""");
        Assert.Equal("MQ1002", again.Items.SelectMany(i => i.Diagnostics).Single().Rule);
        Assert.Contains("Nothing uses or declares", again.Items.SelectMany(i => i.Diagnostics).Single().Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Renaming_a_tag_renames_its_definition_and_every_use_and_merges_with_the_new_key_on_one_list()
    {
        await using var r = await RepoAsync();
        // The note attribute carries both audit and pii; renaming audit to pii leaves one pii there.
        var result = await ApplyAsync(r, """{"op":"retag","tags":["audit"],"name":"pii"}""");

        Assert.Equal(SaveOutcome.Saved, result.Outcome);
        Assert.Equal(["pii", "billing"], TagsOf(r, Ledger));
        Assert.Equal(["pii"], TagsOf(r, Ledger, LedgerId));
        Assert.Equal(["pii"], TagsOf(r, Ledger, LedgerNote));

        // A declared tag renamed to a new key: the definition keeps its description under the new key.
        Assert.Equal(SaveOutcome.Saved, (await ApplyAsync(r, """{"op":"retag","tags":["billing"],"name":"finance"}""")).Outcome);
        var definitions = r.Store.Current!.Get<TagVocabulary>(TagsVocabularyId)!.Definitions;
        Assert.Equal(["finance", "pii"], definitions.Select(d => d.Key));
        Assert.Equal("Owned by the billing team.", definitions[0].Description);
        Assert.Contains("finance", TagsOf(r, EditorRepo.InvoiceId));

        // A rename names one tag and a valid key.
        Assert.Equal("MQ1002", (await ApplyAsync(r, """{"op":"retag","tags":["pii","finance"],"name":"x"}""")).Items.SelectMany(i => i.Diagnostics).Single().Rule);
        Assert.Equal("MQ1002", (await ApplyAsync(r, """{"op":"retag","tags":["pii"],"name":"has space"}""")).Items.SelectMany(i => i.Diagnostics).Single().Rule);
    }

    [Fact]
    public async Task A_domain_vocabulary_governs_its_own_uses_and_the_global_retag_leaves_them()
    {
        await using var r = await RepoAsync();
        await CreateAsync(r, $$"""
            {"kind":"tag-vocabulary","id":"01K6TAG0000000000000000010","name":"tags","package":"{{EditorRepo.BillingPackageId}}","definitions":[{"key":"audit"}]}
            """);

        var global = (await r.Store.GetTagUsageAsync(null, Ct))!;
        var domain = (await r.Store.GetTagUsageAsync(EditorRepo.BillingPackageId, Ct))!;

        Assert.DoesNotContain(global.Tags, t => t.Tag == "audit");
        Assert.Equal((true, 3), domain.Tags.Single(t => t.Tag == "audit") is var a ? (a.Declared, a.Uses) : default);
        Assert.Contains("Nothing uses or declares", (await ApplyAsync(r, """{"op":"retag","tags":["audit"]}""")).Items.SelectMany(i => i.Diagnostics).Single().Message,
            StringComparison.Ordinal);
        var result = await ApplyAsync(r, $$"""{"op":"retag","tags":["audit"],"package":"{{EditorRepo.BillingPackageId}}"}""");
        Assert.Equal(SaveOutcome.Saved, result.Outcome);
        Assert.Equal(["billing"], TagsOf(r, Ledger));
        Assert.Empty(r.Store.Current!.Get<TagVocabulary>("01K6TAG0000000000000000010")!.Definitions);
    }
}
