using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Generation;
using Maquettiste.Engine.Loading;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Tests.Loading;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Processes;

/// <summary>
/// Rights in the model's bookkeeping (rights-design.md sections 6 and 8): own verbs a right names are references; deletes drop an actor
/// from a right but take the right with its last actor, keep the right when an exception goes, and take a use with its named right;
/// rename-verb and remove-verb follow rights; index rows count rights and carry the rights naming members by patch and by full build; inline
/// and named rights localize their display name and description.
/// </summary>
public sealed class RightReferenceTests
{
    private const string P = "01JPRC00000000000000000001"; // OrderLifecycle in the process-basics fixture
    private const string Draft = "01JSTA00000000000000000001";
    private const string Writer = "01JRRA00000000000000000001";
    private const string Editor = "01JRRA00000000000000000002";
    private const string Guest = "01JRRA00000000000000000003";
    private const string Vocabulary = "01JRRV00000000000000000001";
    private const string Escalate = "01JRRV00000000000000000002";
    private const string Named = "01JRRN00000000000000000001"; // GuestsDoNotEscalate
    private const string WritersEdit = "01JRRN00000000000000000011";
    private const string NoGuestDeletes = "01JRRN00000000000000000012";
    private const string Use = "01JRRN00000000000000000013";
    private const string ProcessFile = "model/processes/order-lifecycle.json";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly string Rights = $$"""
        [{"id":"{{WritersEdit}}","name":"WritersEdit","displayName":"Writers edit drafts","access":"allow","actors":["{{Writer}}","{{Editor}}"],
          "verbs":["modify","escalate"],"states":["{{Draft}}"],"description":"Writers shape their drafts."},
         {"id":"{{NoGuestDeletes}}","name":"NoGuestDeletes","access":"deny","actors":["{{Guest}}"],"except":["{{Editor}}"],"verbs":["delete"]},
         {"id":"{{Use}}","right":"{{Named}}"}]
        """;

    private static async Task<(LoaderHarness Harness, ModelStore Store)> OpenAsync(string? defaultAccess = "deny", Action<LoaderHarness>? prepare = null)
    {
        var h = new LoaderHarness();
        h.CopyFixture("models", "process-basics");
        var process = JsonNode.Parse(h.Read(ProcessFile))!.AsObject();
        process["rights"] = JsonNode.Parse(Rights);
        if (defaultAccess is not null)
            process["rightsDefault"] = defaultAccess;
        h.Write(ProcessFile, Canonical(process.ToJsonString(), "process.json", ProcessFile));
        foreach (var (id, name, type) in new[] { (Writer, "Writer", "role"), (Editor, "Editor", "role"), (Guest, "Guest", "person") })
            h.Write($"model/actors/{name.ToLowerInvariant()}.json", Canonical($$"""{"kind":"actor","id":"{{id}}","name":"{{name}}","type":"{{type}}"}""", "actor.json", $"model/actors/{name}.json"));
        h.Write("model/vocabularies/verbs.json", Canonical($$"""
            {"kind":"verb-vocabulary","id":"{{Vocabulary}}","name":"verbs","verbs":[{"id":"{{Escalate}}","key":"escalate"}]}
            """, "verb-vocabulary.json", "model/vocabularies/verbs.json"));
        h.Write("model/rights/guests-do-not-escalate.json", Canonical($$"""
            {"kind":"right","id":"{{Named}}","name":"GuestsDoNotEscalate","displayName":"Guests do not escalate","access":"deny","actors":["{{Guest}}"],
             "verbs":["escalate"],"description":"Only staff escalate."}
            """, "right.json", "model/rights/guests-do-not-escalate.json"));
        prepare?.Invoke(h);
        var store = new ModelStore(h.Options, h.Services() with { Validator = EngineServices.Create(h.Options).Validator });
        await store.LoadAsync(Ct);
        return (h, store);
    }

    private static string Canonical(string json, string schema, string modelPath) =>
        Encoding.UTF8.GetString(TestServices.Json.Write(JsonNode.Parse(json)!, schema, ".maquettiste/" + modelPath));

    private static async Task<SaveResult> DeleteAsync(ModelStore store, string id, DeleteResolution resolution) =>
        await store.DeleteAsync(id, store.Current!.GetDocument(id)!.Hash, resolution, ChangeSource.Editor, Ct);

    private static async Task<BatchResult> ApplyAsync(ModelStore store, string operation)
    {
        var parsed = store.ParseBatch(Encoding.UTF8.GetBytes("{\"operations\":[" + operation + "]}"));
        Assert.Empty(parsed.Diagnostics);
        return await store.ApplyBatchAsync(parsed.Batch!, ChangeSource.Editor, Ct);
    }

    private static Process Lifecycle(ModelStore store) => store.Current!.Get<Process>(P)!;

    [Fact]
    public async Task An_own_verb_a_right_names_is_a_reference_from_each_right()
    {
        var (h, store) = await OpenAsync();
        using var _ = h;
        await using var __ = store;

        var references = await store.GetReferencesAsync(Escalate, Ct);
        Assert.Contains(references, r => (r.FromElementId, r.FromId, r.JsonPointer) == (P, WritersEdit, "/rights/0/verbs/1"));
        Assert.Contains(references, r => (r.FromElementId, r.JsonPointer) == (Named, "/verbs/0"));
        Assert.Contains(await store.GetReferencesAsync(Editor, Ct), r => (r.FromId, r.JsonPointer) == (NoGuestDeletes, "/rights/1/except/0"));
    }

    [Fact]
    public async Task Removing_an_actor_drops_it_from_a_right_and_an_exception_and_keeps_both_rights()
    {
        var (h, store) = await OpenAsync();
        using var _ = h;
        await using var __ = store;
        Assert.NotEqual(SaveOutcome.Saved, (await DeleteAsync(store, Editor, DeleteResolution.Refuse)).Outcome);

        var removed = await DeleteAsync(store, Editor, DeleteResolution.RemoveReferences);

        Assert.True(removed.Outcome == SaveOutcome.Saved, string.Join("; ", removed.Diagnostics.Select(d => d.Rule + " " + d.Message)));
        var rights = Lifecycle(store).Rights;
        Assert.Equal([WritersEdit, NoGuestDeletes, Use], rights.Select(r => r.Id));
        Assert.Equal([Writer], rights[0].Actors);
        Assert.Empty(rights[1].Except);
        Assert.Equal([Guest], rights[1].Actors);
    }

    [Theory]
    [InlineData(DeleteResolution.RemoveReferences)]
    [InlineData(DeleteResolution.DeleteDependents)]
    public async Task Attributes_a_right_names_are_references_and_the_last_one_takes_its_right_with_it(DeleteResolution resolution)
    {
        const string Record = "01JRRE00000000000000000001"; // a base of Order
        const string Title = "01JRRT00000000000000000001";
        const string OrderId = "01JATT00000000000000000001";
        const string HideTitle = "01JRRN00000000000000000021";
        const string NoEdits = "01JRRN00000000000000000022";
        var (h, store) = await OpenAsync(prepare: h =>
        {
            h.Write("model/entities/record.json", Canonical($$"""
                {"kind":"entity","id":"{{Record}}","name":"Record","attributes":[{"id":"{{Title}}","name":"title","type":"string","length":80}]}
                """, "entity.json", "model/entities/record.json"));
            var order = JsonNode.Parse(h.Read("model/entities/order.json"))!.AsObject();
            order["base"] = Record;
            h.Write("model/entities/order.json", Canonical(order.ToJsonString(), "entity.json", "model/entities/order.json"));
            var process = JsonNode.Parse(h.Read(ProcessFile))!.AsObject();
            process["rights"] = JsonNode.Parse($$"""
                [{"id":"{{HideTitle}}","name":"HideTitle","access":"deny","actors":["{{Guest}}"],"verbs":["read"],"attributes":["{{Title}}"]},
                 {"id":"{{NoEdits}}","name":"NoEdits","access":"deny","actors":["{{Guest}}"],"verbs":["modify"],"attributes":["{{Title}}","{{OrderId}}"],"whileFrozen":true}]
                """);
            h.Write(ProcessFile, Canonical(process.ToJsonString(), "process.json", ProcessFile));
        });
        using var _ = h;
        await using var __ = store;
        Assert.DoesNotContain((await store.ValidateAsync(ValidationScope.All, Ct)).Diagnostics, d => d.Rule is "MQ9901" or "MQ9903");

        var references = await store.GetReferencesAsync(Title, Ct);
        Assert.Contains(references, r => (r.FromElementId, r.FromId, r.JsonPointer) == (P, HideTitle, "/rights/0/attributes/0"));
        Assert.Contains(references, r => (r.FromElementId, r.FromId, r.JsonPointer) == (P, NoEdits, "/rights/1/attributes/0"));

        var removed = await DeleteAsync(store, Record, resolution);

        Assert.True(removed.Outcome == SaveOutcome.Saved, string.Join("; ", removed.Diagnostics.Select(d => d.Rule + " " + d.Message)));
        // An empty attributes list would mean the whole object: HideTitle goes, and NoEdits keeps the attribute that remains.
        var rights = Lifecycle(store).Rights;
        Assert.Equal([NoEdits], rights.Select(r => r.Id));
        Assert.Equal([OrderId], rights[0].Attributes);
        Assert.True(rights[0].WhileFrozen);
    }

    [Theory]
    [InlineData(DeleteResolution.RemoveReferences)]
    [InlineData(DeleteResolution.DeleteDependents)]
    public async Task The_last_actor_takes_its_right_with_it_and_a_use_goes_with_its_named_right(DeleteResolution resolution)
    {
        var (h, store) = await OpenAsync();
        using var _ = h;
        await using var __ = store;
        var hash = store.Current!.GetDocument(Guest)!.Hash;

        var plan = await store.GetDeletePlanAsync([Guest], resolution, Ct);
        var removed = await store.DeleteAsync(Guest, hash, resolution, ChangeSource.Editor, Ct);

        Assert.True(removed.Outcome == SaveOutcome.Saved, string.Join("; ", removed.Diagnostics.Select(d => d.Rule + " " + d.Message)));
        // An empty actors list would mean everyone: NoGuestDeletes leaves the lifecycle, and the named right is deleted with its use.
        Assert.Equal([WritersEdit], Lifecycle(store).Rights.Select(r => r.Id));
        Assert.Null(store.Current!.GetDocument(Named));
        var json = JsonSerializer.Serialize(plan, EngineJson.Options);
        Assert.True(json.Contains("names no other actor than actor Guest", StringComparison.Ordinal), json);
        Assert.Contains("names no other actor than actor Guest", json, StringComparison.Ordinal);
        Assert.Contains("\"what\":\"right NoGuestDeletes\"", json, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(DeleteResolution.RemoveReferences)]
    [InlineData(DeleteResolution.DeleteDependents)]
    public async Task A_named_right_in_use_is_refused_unless_a_choice_removes_its_uses(DeleteResolution resolution)
    {
        var (h, store) = await OpenAsync();
        using var _ = h;
        await using var __ = store;
        Assert.NotEqual(SaveOutcome.Saved, (await DeleteAsync(store, Named, DeleteResolution.Refuse)).Outcome);

        var removed = await DeleteAsync(store, Named, resolution);

        Assert.True(removed.Outcome == SaveOutcome.Saved, string.Join("; ", removed.Diagnostics.Select(d => d.Rule + " " + d.Message)));
        Assert.Equal([WritersEdit, NoGuestDeletes], Lifecycle(store).Rights.Select(r => r.Id));
    }

    [Fact]
    public async Task Rename_verb_renames_and_merges_in_rights()
    {
        var (h, store) = await OpenAsync();
        using var _ = h;
        await using var __ = store;

        Assert.Equal(SaveOutcome.Saved, (await ApplyAsync(store, """{"op":"rename-verb","verb":"escalate","to":"raise"}""")).Outcome);
        Assert.Equal(["modify", "raise"], Lifecycle(store).Rights[0].Verbs);
        Assert.Equal(["raise"], store.Current!.Get<Right>(Named)!.Verbs);

        // Into a built-in verb the right already names: the two merge into one entry.
        Assert.Equal(SaveOutcome.Saved, (await ApplyAsync(store, """{"op":"rename-verb","verb":"raise","to":"modify"}""")).Outcome);
        Assert.Equal(["modify"], Lifecycle(store).Rights[0].Verbs);
        Assert.Equal(["modify"], store.Current!.Get<Right>(Named)!.Verbs);
    }

    [Fact]
    public async Task Remove_verb_clears_it_from_rights_and_a_right_whose_last_verb_goes_goes_too()
    {
        var (h, store) = await OpenAsync();
        using var _ = h;
        await using var __ = store;

        var result = await ApplyAsync(store, """{"op":"remove-verb","verb":"escalate"}""");

        Assert.True(result.Outcome == SaveOutcome.Saved, string.Join("; ", result.Items.SelectMany(i => i.Diagnostics).Select(d => d.Rule + " " + d.Message)));
        Assert.Equal(["modify"], Lifecycle(store).Rights[0].Verbs);
        Assert.Null(store.Current!.GetDocument(Named));
        Assert.Equal([WritersEdit, NoGuestDeletes], Lifecycle(store).Rights.Select(r => r.Id));
    }

    [Fact]
    public async Task Index_rows_count_rights_and_carry_the_rights_naming_members_by_patch_and_by_full_build()
    {
        var (h, store) = await OpenAsync();
        using var _ = h;
        await using var __ = store;
        var row = store.Current!.Index.Summaries.Single(s => s.Id == P);
        Assert.Equal((3, "deny"), (row.Rights, row.RightsDefault));
        Assert.Contains("escalate", row.Verbs!);
        Assert.Equal(["delete"], row.DenyVerbs!);
        Assert.Equal([Named], row.RightUses!);
        var named = store.Current!.Index.Summaries.Single(s => s.Id == Named);
        Assert.Equal("deny", named.Access);
        Assert.Equal(["escalate"], named.Verbs!);
        var text = JsonSerializer.Serialize(row, EngineJson.Options);
        Assert.Contains("\"rights\":3", text, StringComparison.Ordinal);
        Assert.DoesNotContain("denyVerbs", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("rightUses", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("rightsDefault", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"access\":\"deny\"", JsonSerializer.Serialize(named, EngineJson.Options), StringComparison.Ordinal);

        // A save that allows by default and denies another verb: the patched row equals a full build's.
        var current = (await store.GetElementAsync(P, Ct))!;
        var node = JsonNode.Parse(current.Json.GetRawText())!.AsObject();
        node["rightsDefault"] = "allow";
        node["rights"]![1]!["verbs"] = new JsonArray("delete", "copy");
        var saved = await store.SaveAsync(P, Encoding.UTF8.GetBytes(node.ToJsonString()), current.Hash, ChangeSource.Editor, Ct);
        Assert.Equal(SaveOutcome.Saved, saved.Outcome);
        Assert.True(store.Current!.Index.Patched);
        var patched = store.Current!.Index.Summaries.Single(s => s.Id == P);
        Assert.Equal("allow", patched.RightsDefault);
        Assert.Equal(["copy", "delete"], patched.DenyVerbs!);

        var fresh = new ModelStore(h.Options, h.Services() with { Validator = EngineServices.Create(h.Options).Validator });
        await using var ___ = fresh;
        await fresh.LoadAsync(Ct);
        Assert.Equal(patched, fresh.Current!.Index.Summaries.Single(s => s.Id == P));
        Assert.Equal(named, fresh.Current!.Index.Summaries.Single(s => s.Id == Named));
    }

    [Fact]
    public async Task A_project_without_rights_keeps_its_index_rows()
    {
        using var h = new LoaderHarness();
        h.CopyFixture("models", "process-basics");
        await using var store = new ModelStore(h.Options, h.Services() with { Validator = EngineServices.Create(h.Options).Validator });
        await store.LoadAsync(Ct);
        var row = store.Current!.Index.Summaries.Single(s => s.Id == P);
        Assert.Equal((null, null, null, null, false), (row.Rights, row.RightsDefault, row.DenyVerbs, row.RightUses, row.UsesRights));
        var text = JsonSerializer.Serialize(row, EngineJson.Options);
        Assert.DoesNotContain("\"rights\"", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\"access\"", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Inline_and_named_rights_localize_their_display_name_and_description_and_a_use_has_none()
    {
        var (h, store) = await OpenAsync();
        using var _ = h;
        await using var __ = store;
        var l10n = store.Current!.Localization;

        Assert.Equal(("process-right", "Writers edit drafts", "Writers shape their drafts."),
            (l10n.Nodes[WritersEdit].Kind, l10n.Nodes[WritersEdit].DisplayName, l10n.Nodes[WritersEdit].Description));
        Assert.Equal("NoGuestDeletes", l10n.Nodes[NoGuestDeletes].DisplayName);
        Assert.Equal(("right", "Guests do not escalate", "Only staff escalate."),
            (l10n.Nodes[Named].Kind, l10n.Nodes[Named].DisplayName, l10n.Nodes[Named].Description));
        Assert.False(l10n.Nodes.ContainsKey(Use));
        Assert.Contains("process-right", (IReadOnlySet<string>)Maquettiste.Engine.Localization.LocaleChains.NodeKinds);
    }
}
