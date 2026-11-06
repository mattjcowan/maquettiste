using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Resolution;
using Maquettiste.Engine.Tests.Editor;

namespace Maquettiste.Engine.Tests.Store;

/// <summary>
/// Verbs in the resolved model (verbs-design.md sections 6 and 13): <c>model.verbs</c>, its order and hidden verbs, the verb of each event,
/// transition and operation, their dependency keys and records; own verbs' translations; dry runs of the verb operations; and the acceptance
/// model of section 13.
/// </summary>
public sealed class ResolvedVerbTests
{
    private const string Vocabulary = "01K9VRC0000000000000000001";
    private const string Escalate = "01K9VRC0000000000000000002";
    private const string Document = "01K9VRC0000000000000000010";
    private const string Submit = "01K9VRC0000000000000000011";
    private const string SendBack = "01K9VRC0000000000000000012";
    private const string EscalateEvent = "01K9VRC0000000000000000013";
    private const string Draft = "01K9VRC0000000000000000021";
    private const string Approved = "01K9VRC0000000000000000022";
    private const string Published = "01K9VRC0000000000000000023";
    private const string BySubmit = "01K9VRC0000000000000000031";
    private const string BySendBack = "01K9VRC0000000000000000032";
    private const string Always = "01K9VRC0000000000000000033";
    private const string ByEscalate = "01K9VRC0000000000000000034";
    private const string AssignReviewer = "01K9VRC0000000000000000040";
    private const string Outcome = "01K9VRC0000000000000000041";
    private const string CheckOut = "01K9VRC0000000000000000003";

    private static CancellationToken Ct => EditorRepo.Ct;

    private static string VocabularyJson(string own = "") => $$"""
        {"kind":"verb-vocabulary","id":"{{Vocabulary}}","name":"verbs","hidden":["lock","unlock"],
         "verbs":[{"id":"{{Escalate}}","key":"escalate","description":"Raise the request to the next level of support."}{{own}}]}
        """;

    private static string DocumentJson(bool verbs = true, string escalate = "escalate") => $$"""
        {"kind":"process","id":"{{Document}}","name":"DocumentLifecycle","package":"{{EditorRepo.BillingPackageId}}",
         "events":[{"id":"{{Submit}}","name":"Submit"{{(verbs ? ",\"verb\":\"promote\"" : "")}}},{"id":"{{SendBack}}","name":"SendBack"{{(verbs ? ",\"verb\":\"demote\"" : "")}}},
           {"id":"{{EscalateEvent}}","name":"Escalate"{{(verbs ? ",\"verb\":\"" + escalate + "\"" : "")}}}],
         "initial":"{{Draft}}",
         "states":[{"id":"{{Draft}}","name":"Draft"},{"id":"{{Approved}}","name":"Approved"},{"id":"{{Published}}","name":"Published","type":"final"}],
         "transitions":[{"id":"{{BySubmit}}","source":"{{Draft}}","event":"{{Submit}}","targets":["{{Approved}}"]},
           {"id":"{{BySendBack}}","source":"{{Approved}}","event":"{{SendBack}}","targets":["{{Draft}}"]},
           {"id":"{{Always}}","source":"{{Approved}}","trigger":"always","targets":["{{Published}}"]{{(verbs ? ",\"verb\":\"promote\"" : "")}}},
           {"id":"{{ByEscalate}}","source":"{{Draft}}","event":"{{EscalateEvent}}","targets":["{{Draft}}"]}]}
        """;

    private static string AssignReviewerJson(bool verbs = true) => $$"""
        {"kind":"operation","id":"{{AssignReviewer}}","name":"AssignReviewer","subject":"{{EditorRepo.InvoiceId}}","effect":"change"{{(verbs ? ",\"verb\":\"connect\"" : "")}},
         "outcomes":[{"id":"{{Outcome}}","name":"ok"}]}
        """;

    private static async Task<EditorRepo> RepoAsync(bool vocabulary = true, bool verbs = true, string escalate = "escalate")
    {
        var r = EditorRepo.Create(packs: false);
        if (vocabulary)
            await CreateAsync(r, VocabularyJson());
        await CreateAsync(r, DocumentJson(verbs, escalate));
        await CreateAsync(r, AssignReviewerJson(verbs));
        return r;
    }

    private static async Task CreateAsync(EditorRepo r, string json)
    {
        var result = await r.Store.CreateAsync(Encoding.UTF8.GetBytes(JsonNode.Parse(json)!.ToJsonString()), ChangeSource.Editor, Ct);
        Assert.True(result.Outcome == SaveOutcome.Saved, string.Join("; ", result.Diagnostics.Select(d => d.Rule + " " + d.Message)));
    }

    private static async Task<ResolvedModel> ResolveAsync(EditorRepo r) =>
        await r.Store.Services.Resolver.ResolveAsync(r.Store.Current!, null, Ct);

    private static ModelBatch Batch(EditorRepo r, string operation)
    {
        var parsed = r.Store.ParseBatch(Encoding.UTF8.GetBytes("{\"operations\":[" + operation + "]}"));
        Assert.Empty(parsed.Diagnostics);
        return parsed.Batch!;
    }

    [Fact]
    public async Task Model_verbs_is_empty_without_verbs_but_keys_on_every_use_site()
    {
        await using var r = await RepoAsync(vocabulary: false, verbs: false);
        var model = await ResolveAsync(r);

        Assert.Empty(model.Verbs);
        Assert.Contains("k:verb-vocabulary", model.Verbs.MembershipKeys);
        Assert.Contains("e:" + Document, model.Verbs.MembershipKeys);
        Assert.Contains("e:" + AssignReviewer, model.Verbs.MembershipKeys);
        var process = model.Processes.Single(p => p.Id == Document);
        Assert.All(process.Events, e => Assert.Null(e.Verb));
        Assert.All(process.Transitions, t => Assert.Null(t.Verb));
        Assert.All(process.Events, e => Assert.DoesNotContain("k:verb-vocabulary", e.Dependencies));
        Assert.Null(model.Operations.Single(o => o.Id == AssignReviewer).Verb);

        var records = ResolvedRecords.Entries(model, "all", null).ToList();
        Assert.DoesNotContain(records, e => e.Kind == "verb");
        Assert.DoesNotContain("\"verb\"", JsonSerializer.Serialize(records.Select(e => e.Project()).ToList(), EngineJson.Options), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_named_verb_alone_makes_the_project_use_verbs()
    {
        await using var r = await RepoAsync(vocabulary: false, escalate: "route");
        var model = await ResolveAsync(r);

        Assert.Equal(BuiltInVerbs.All.Select(v => v.Key), model.Verbs.Select(v => v.Key));
        Assert.Equal("route", model.Processes.Single(p => p.Id == Document).Events[2].Verb!.Key);
        Assert.Equal(["route"], model.Verbs.Where(v => v.Group == "signature" && v.Used).Select(v => v.Key));
    }

    [Fact]
    public async Task Model_verbs_lists_visible_built_ins_then_own_verbs_then_hidden_ones_still_named()
    {
        await using var r = await RepoAsync();
        Assert.Equal(SaveOutcome.Saved, (await SaveAsync(r, AssignReviewer, n => n["verb"] = "lock")).Outcome);
        var model = await ResolveAsync(r);

        var expected = BuiltInVerbs.All.Select(v => v.Key).Where(k => k is not "lock" and not "unlock").Append("escalate").Append("lock");
        Assert.Equal(expected, model.Verbs.Select(v => v.Key));
        var escalate = model.Verbs.Single(v => v.Key == "escalate");
        Assert.Equal((Escalate, "Escalate", "Raise the request to the next level of support.", false, "project", false),
            (escalate.Id, escalate.DisplayName, escalate.Description, escalate.BuiltIn, escalate.Group, escalate.Hidden));
        var locked = model.Verbs[^1];
        Assert.Equal((null, true, true, "object"), (locked.Id, locked.Hidden, locked.BuiltIn, locked.Group));
        Assert.Equal([AssignReviewer], locked.Operations.Select(o => o.Id));
        Assert.False(model.Verbs.Single(v => v.Key == "freeze").Used);
        Assert.Same(escalate, model.Find(Escalate));
    }

    [Fact]
    public async Task A_transition_takes_its_own_verb_else_its_events_and_each_verb_lists_its_uses()
    {
        await using var r = await RepoAsync();
        var model = await ResolveAsync(r);
        var process = model.Processes.Single(p => p.Id == Document);

        Assert.Equal(["promote", "demote", "escalate"], process.Events.Select(e => e.Verb!.Key));
        Assert.Equal(
            [(BySubmit, "promote"), (BySendBack, "demote"), (Always, "promote"), (ByEscalate, "escalate")],
            process.Transitions.Select(t => (t.Id, t.Verb!.Key)).OrderBy(x => x.Id, StringComparer.Ordinal));
        var promote = model.Verbs.Single(v => v.Key == "promote");
        Assert.Equal([Submit], promote.Events.Select(e => e.Id));
        Assert.Equal([BySubmit, Always], promote.Transitions.Select(t => t.Id).Order(StringComparer.Ordinal));
        Assert.True(promote.Used);
        Assert.Equal("connect", model.Operations.Single(o => o.Id == AssignReviewer).Verb!.Key);
        Assert.Same(model.Verbs.Single(v => v.Key == "connect"), model.Operations.Single(o => o.Id == AssignReviewer).Verb);
    }

    [Fact]
    public async Task Uses_that_name_a_verb_key_on_the_vocabulary_and_verbs_key_on_every_use_site()
    {
        await using var r = await RepoAsync();
        var model = await ResolveAsync(r);
        var process = model.Processes.Single(p => p.Id == Document);
        var operation = model.Operations.Single(o => o.Id == AssignReviewer);

        Assert.All(process.Events, e => Assert.Contains("e:" + Vocabulary, e.Dependencies));
        Assert.Contains("e:" + Vocabulary, operation.Dependencies);
        Assert.Contains("e:" + Vocabulary, process.Transitions.Single(t => t.Id == Always).Dependencies);
        Assert.DoesNotContain("e:" + Vocabulary, process.Dependencies);
        foreach (var verb in model.Verbs)
        {
            Assert.Contains("e:" + Vocabulary, verb.Dependencies);
            Assert.Contains("e:" + Document, verb.Dependencies);
            Assert.Contains("e:" + AssignReviewer, verb.Events.MembershipKeys);
        }
    }

    [Fact]
    public async Task Records_carry_each_verb_and_verb_records_follow_model_verbs()
    {
        await using var r = await RepoAsync();
        var model = await ResolveAsync(r);
        var entries = ResolvedRecords.Entries(model, "all", null).ToList();

        var verbs = entries.Where(e => e.Kind == "verb").Select(e => (VerbRecord)e.Project()).ToList();
        Assert.Equal(model.Verbs.Select(v => v.Key), verbs.Select(v => v.Name));
        var escalate = verbs.Single(v => v.Key == "escalate");
        Assert.Equal((Escalate, "verb", true), (escalate.Id, escalate.Kind, escalate.Used));
        Assert.Equal([EscalateEvent], escalate.Events);
        Assert.Equal([ByEscalate], escalate.Transitions);
        Assert.Null(verbs.Single(v => v.Key == "promote").Id);

        var process = (ProcessRecord)entries.Single(e => e.Id == Document).Project();
        Assert.Equal(["promote", "demote", "escalate"], process.Events.Select(e => e.Verb));
        Assert.Equal("promote", process.Transitions.Single(t => t.Id == Always).Verb);
        Assert.Equal("connect", ((OperationRecord)entries.Single(e => e.Id == AssignReviewer).Project()).Verb);
        var json = JsonSerializer.Serialize(process, EngineJson.Options);
        Assert.Contains("\"verb\":\"demote\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_own_verbs_display_name_and_description_translate_and_read_back()
    {
        await using var r = await RepoAsync();
        r.EditSettingsOnDisk(n => n["localization"] = new JsonObject { ["defaultLocale"] = "en", ["locales"] = new JsonArray("en", "fr") });
        await r.Store.RefreshAsync([r.Repo.PathOf(EditorRepo.SettingsPath)], Ct);
        var page = await r.Store.GetTranslationsAsync("fr", null, null, Ct);

        var saved = await r.Store.SaveTranslationsAsync("fr",
            [new TranslationEdit(Escalate, "displayName", "Escalader"), new TranslationEdit(Escalate, "description", "Transmettre au niveau suivant.")],
            page.ShardHashes, ChangeSource.Editor, Ct);

        Assert.True(saved.Outcome == SaveOutcome.Saved, string.Join("; ", saved.Diagnostics.Select(d => d.Rule + " " + d.Message)));
        var model = r.Store.Current!;
        Assert.Equal("Escalader", model.Localization.Text("fr", Escalate, "displayName"));
        Assert.Equal("Transmettre au niveau suivant.", model.Localization.Text("fr", Escalate, "description"));
        var reread = await r.Store.GetTranslationsAsync("fr", null, null, Ct);
        Assert.Contains("Escalader", JsonSerializer.Serialize(reread, EngineJson.Options), StringComparison.Ordinal);

        // Removing the verb drops its translations with it (verbs-design.md decision 9).
        Assert.Equal(SaveOutcome.Saved, (await r.Store.ApplyBatchAsync(Batch(r, """{"op":"remove-verb","verb":"escalate"}"""), ChangeSource.Editor, Ct)).Outcome);
        Assert.Null(r.Store.Current!.Localization.Text("fr", Escalate, "displayName"));
    }

    [Theory]
    [InlineData("""{"op":"rename-verb","verb":"escalate","to":"raise"}""")]
    [InlineData("""{"op":"remove-verb","verb":"escalate"}""")]
    public async Task A_dry_run_of_a_verb_operation_shows_every_file_it_changes_and_writes_nothing(string operation)
    {
        await using var r = await RepoAsync();
        var before = r.Files();

        var preview = await r.Store.PreviewBatchAsync(Batch(r, operation), Ct);

        Assert.Equal(SaveOutcome.Saved, preview.Outcome);
        Assert.Equal([Vocabulary, Document], preview.Files.Select(f => f.Id!).Order(StringComparer.Ordinal));
        Assert.All(preview.Files, f => Assert.Equal("changed", f.Action));
        Assert.Equal(before, r.Files());
        Assert.Equal("escalate", r.Store.Current!.Get<Process>(Document)!.Events[2].Verb);
    }

    [Fact]
    public async Task Acceptance_a_document_lifecycle_names_its_verbs_and_a_rename_rewrites_every_use_in_one_step()
    {
        await using var r = await RepoAsync();
        // An own check-out, as the editor's suggestion pre-fills it.
        Assert.Contains("check-out", BuiltInVerbs.Suggestions);
        Assert.Equal(SaveOutcome.Saved, (await SaveAsync(r, Vocabulary, n => n["verbs"]!.AsArray().Add(new JsonObject
        {
            ["id"] = CheckOut, ["key"] = "check-out", ["description"] = "Reserve the document to edit it.",
        }))).Outcome);
        Assert.DoesNotContain((await r.Store.ValidateAsync(ValidationScope.All, Ct)).Diagnostics, d => d.Rule.StartsWith("MQ97", StringComparison.Ordinal));

        var result = await r.Store.ApplyBatchAsync(Batch(r, """{"op":"rename-verb","verb":"escalate","to":"raise"}"""), ChangeSource.Editor, Ct);

        Assert.Equal(SaveOutcome.Saved, result.Outcome);
        // The vocabulary and the process that names escalate change in one batch, so one undo step reverts it.
        Assert.Equal([Vocabulary, Document], result.Changes!.Changed.Select(c => c.Id).Order(StringComparer.Ordinal));
        var model = await ResolveAsync(r);
        var process = model.Processes.Single(p => p.Id == Document);
        Assert.Equal(["promote", "demote", "raise"], process.Events.Select(e => e.Verb!.Key));
        Assert.Equal("promote", process.Transitions.Single(t => t.Id == Always).Verb!.Key);
        Assert.Equal("connect", model.Operations.Single(o => o.Id == AssignReviewer).Verb!.Key);
        Assert.Equal(["raise", "check-out"], model.Verbs.Where(v => !v.BuiltIn).Select(v => v.Key));
        Assert.DoesNotContain(model.Verbs, v => v.Key is "lock" or "unlock");
        Assert.Equal("Raise", model.Verbs.Single(v => v.Key == "raise").DisplayName);
        Assert.Equal(Escalate, model.Verbs.Single(v => v.Key == "raise").Id);
    }

    private static async Task<SaveResult> SaveAsync(EditorRepo r, string id, Action<JsonObject> change)
    {
        var current = (await r.Store.GetElementAsync(id, Ct))!;
        var node = JsonNode.Parse(current.Json.GetRawText())!.AsObject();
        change(node);
        return await r.Store.SaveAsync(id, Encoding.UTF8.GetBytes(node.ToJsonString()), current.Hash, ChangeSource.Editor, Ct);
    }
}
