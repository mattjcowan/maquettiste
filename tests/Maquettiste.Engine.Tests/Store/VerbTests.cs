using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Tests.Editor;

namespace Maquettiste.Engine.Tests.Store;

/// <summary>
/// Verbs (verbs-design.md): the verb vocabulary, verbs on events, eventless transitions and operations, MQ9701 to MQ9707 and MQ1009,
/// references and deletes, index rows, the index patch, localization, and the batch operations rename-verb and remove-verb.
/// </summary>
public sealed class VerbTests
{
    private const string Vocabulary = "01K9VRB0000000000000000001";
    private const string Escalate = "01K9VRB0000000000000000002";
    private const string Quarantine = "01K9VRB0000000000000000003";
    private const string Flow = "01K9VRB0000000000000000010";
    private const string Submit = "01K9VRB0000000000000000011";
    private const string Raise = "01K9VRB0000000000000000012";
    private const string Draft = "01K9VRB0000000000000000021";
    private const string Done = "01K9VRB0000000000000000022";
    private const string ByEvent = "01K9VRB0000000000000000031";
    private const string Always = "01K9VRB0000000000000000032";
    private const string Assign = "01K9VRB0000000000000000040";
    private const string Outcome = "01K9VRB0000000000000000041";

    private static CancellationToken Ct => EditorRepo.Ct;

    private static string VocabularyJson(string escalateKey = "escalate", string hidden = "", string extra = "") => $$"""
        {"kind":"verb-vocabulary","id":"{{Vocabulary}}","name":"verbs","hidden":[{{hidden}}],
         "verbs":[{"id":"{{Escalate}}","key":"{{escalateKey}}","description":"Raise it to the next level."},
           {"id":"{{Quarantine}}","key":"quarantine","displayName":"Put in quarantine"}{{extra}}]}
        """;

    private static string ProcessJson(string submitVerb = "promote", string raiseVerb = "escalate", string byEventVerb = "", string alwaysVerb = "promote") => $$"""
        {"kind":"process","id":"{{Flow}}","name":"Flow","package":"{{EditorRepo.BillingPackageId}}",
         "events":[{"id":"{{Submit}}","name":"Submit","verb":"{{submitVerb}}"},{"id":"{{Raise}}","name":"Raise","verb":"{{raiseVerb}}"}],
         "initial":"{{Draft}}",
         "states":[{"id":"{{Draft}}","name":"Draft"},{"id":"{{Done}}","name":"Done","type":"final"}],
         "transitions":[{"id":"{{ByEvent}}","source":"{{Draft}}","event":"{{Submit}}","targets":["{{Done}}"]{{(byEventVerb.Length > 0 ? $",\"verb\":\"{byEventVerb}\"" : "")}}},
           {"id":"{{Always}}","source":"{{Draft}}","trigger":"always","verb":"{{alwaysVerb}}","targets":["{{Done}}"]}]}
        """;

    private static string OperationJson(string verb = "escalate") => $$"""
        {"kind":"operation","id":"{{Assign}}","name":"assign","subject":"{{EditorRepo.InvoiceId}}","effect":"change","verb":"{{verb}}",
         "outcomes":[{"id":"{{Outcome}}","name":"ok"}]}
        """;

    private static async Task<EditorRepo> RepoAsync(string? vocabulary = null, string? process = null, string? operation = null)
    {
        var r = EditorRepo.Create(packs: false);
        await CreateAsync(r, vocabulary ?? VocabularyJson());
        await CreateAsync(r, process ?? ProcessJson());
        await CreateAsync(r, operation ?? OperationJson());
        return r;
    }

    /// <summary>Writes an element's file on disk, as a hand edit would, so a document with errors reaches the model.</summary>
    private static async Task WriteAsync(EditorRepo r, string id, string json)
    {
        var path = Path.Combine(r.Repo.RepoRoot, r.Store.Current!.GetDocument(id)!.Path);
        await File.WriteAllTextAsync(path, JsonNode.Parse(json)!.ToJsonString() + "\n", Ct);
        await r.Store.RefreshAsync([path], Ct);
    }

    private static async Task CreateAsync(EditorRepo r, string json)
    {
        var result = await r.Store.CreateAsync(Encoding.UTF8.GetBytes(JsonNode.Parse(json)!.ToJsonString()), ChangeSource.Editor, Ct);
        Assert.True(result.Outcome == SaveOutcome.Saved, string.Join("; ", result.Diagnostics.Select(d => d.Rule + " " + d.Message)));
    }

    private static async Task<SaveResult> SaveAsync(EditorRepo r, string id, Action<JsonObject> change)
    {
        var current = (await r.Store.GetElementAsync(id, Ct))!;
        var node = JsonNode.Parse(current.Json.GetRawText())!.AsObject();
        change(node);
        return await r.Store.SaveAsync(id, Encoding.UTF8.GetBytes(node.ToJsonString()), current.Hash, ChangeSource.Editor, Ct);
    }

    private static async Task<BatchResult> ApplyAsync(EditorRepo r, string operation)
    {
        var parsed = r.Store.ParseBatch(Encoding.UTF8.GetBytes("{\"operations\":[" + operation + "]}"));
        Assert.Empty(parsed.Diagnostics);
        return await r.Store.ApplyBatchAsync(parsed.Batch!, ChangeSource.Editor, Ct);
    }

    private static async Task<List<(string Rule, string? Pointer)>> VerbFindingsAsync(EditorRepo r) =>
        [.. (await r.Store.ValidateAsync(ValidationScope.All, Ct)).Diagnostics
            .Where(d => d.Rule.StartsWith("MQ97", StringComparison.Ordinal) || d.Rule == "MQ1009")
            .Select(d => (d.Rule, d.JsonPointer)).Order()];

    [Fact]
    public async Task A_model_without_verbs_has_no_vocabulary_no_verb_rows_and_no_verb_findings()
    {
        await using var r = EditorRepo.Create(packs: false);
        Assert.Empty(await VerbFindingsAsync(r));
        var model = r.Store.Current!;

        Assert.Null(model.Verbs);
        Assert.All(model.Summaries(), s => Assert.True(s.Verb is null && s.Verbs is null));
        Assert.DoesNotContain("\"verb", System.Text.Json.JsonSerializer.Serialize(model.Summaries(), EngineJson.Options), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Each_rule_reports_at_its_use_or_entry_and_a_clean_model_reports_none()
    {
        await using var clean = await RepoAsync();
        Assert.Empty(await VerbFindingsAsync(clean));

        await using var r = await RepoAsync();
        await WriteAsync(r, Assign, OperationJson("nothing-here"));
        await WriteAsync(r, Flow, ProcessJson(submitVerb: "lock", raiseVerb: "typo", byEventVerb: "demote"));
        await WriteAsync(r, Vocabulary,
            VocabularyJson(hidden: "\"lock\",\"nope\"", extra: $$""",{"id":"01K9VRB0000000000000000004","key":"promote"},{"id":"01K9VRB0000000000000000005","key":"escalate"},{"id":"01K9VRB0000000000000000006","key":"setstate"}"""));

        Assert.Equal(
            [
                ("MQ9701", "/events/1/verb"), ("MQ9701", "/verb"), ("MQ9702", "/verbs/2/key"), ("MQ9702", "/verbs/3/key"), ("MQ9703", "/hidden/1"),
                ("MQ9704", "/events/0/verb"), ("MQ9705", "/transitions/0/verb"), ("MQ9706", "/verbs/4/key"),
            ],
            await VerbFindingsAsync(r));
    }

    [Fact]
    public async Task A_use_of_an_own_verb_is_a_reference_to_its_id_and_index_rows_carry_the_verbs()
    {
        await using var r = await RepoAsync();
        var model = r.Store.Current!;

        Assert.Equal([(Flow, "/events/1/verb"), (Assign, "/verb")],
            model.ReferencesTo(Escalate).Select(x => (x.FromElementId, x.JsonPointer)).Order().ToList());
        Assert.Empty(model.ReferencesTo(Quarantine));
        Assert.True(model.TryGetEntry(Escalate, out var entry) && entry.Kind == "verb" && entry.OwnerId == Vocabulary);
        Assert.Equal("escalate", model.Summaries().Single(s => s.Id == Assign).Verb);
        Assert.Equal(["escalate", "promote"], model.Summaries().Single(s => s.Id == Flow).Verbs);
        Assert.Equal("model/vocabularies/verbs.json", model.GetDocument(Vocabulary)!.Path[^"model/vocabularies/verbs.json".Length..]);
    }

    [Fact]
    public async Task An_edit_to_the_vocabulary_rebuilds_the_index_and_an_edit_to_a_use_patches_it()
    {
        await using var r = await RepoAsync();

        Assert.Equal(SaveOutcome.Saved, (await SaveAsync(r, Vocabulary, n => n["verbs"]![1]!["key"] = "isolate")).Outcome);
        Assert.False(r.Store.Current!.Index.Patched);

        // escalate's uses stay references after the vocabulary changes; a new use of quarantine on the process becomes one by patch.
        Assert.Equal(SaveOutcome.Saved, (await SaveAsync(r, Flow, n => n["events"]![0]!["verb"] = "isolate")).Outcome);
        var model = r.Store.Current!;
        Assert.True(model.Index.Patched);
        Assert.Equal([Flow], model.ReferencesTo(Quarantine).Select(x => x.FromElementId).ToList());
        Assert.Equal(["escalate", "isolate", "promote"], model.Summaries().Single(s => s.Id == Flow).Verbs);
    }

    [Fact]
    public async Task A_save_that_changes_the_key_of_an_own_verb_in_use_is_refused_and_an_unused_one_may_change()
    {
        await using var r = await RepoAsync();

        var refused = await SaveAsync(r, Vocabulary, n => n["verbs"]![0]!["key"] = "raise");
        Assert.Equal(SaveOutcome.Invalid, refused.Outcome);
        Assert.Equal("/verbs/0/key", Assert.Single(refused.Diagnostics, d => d.Rule == "MQ9707").JsonPointer);

        Assert.Equal(SaveOutcome.Saved, (await SaveAsync(r, Vocabulary, n => n["verbs"]![1]!["key"] = "isolate")).Outcome);
        Assert.Single(Maquettiste.Engine.Validation.SaveRules.Check(r.Store.Current!, r.Store.Current!.Get<VerbVocabulary>(Vocabulary)! with
        {
            Verbs = [new OwnVerb { Id = Escalate, Key = "raise" }],
        }, "x"));
    }

    [Fact]
    public async Task Rename_verb_rewrites_every_use_and_keeps_the_id()
    {
        await using var r = await RepoAsync();

        var result = await ApplyAsync(r, """{"op":"rename-verb","verb":"escalate","to":"raise"}""");

        Assert.Equal(SaveOutcome.Saved, result.Outcome);
        var model = r.Store.Current!;
        Assert.Equal("raise", model.Get<VerbVocabulary>(Vocabulary)!.Verbs.Single(v => v.Id == Escalate).Key);
        Assert.Equal("raise", model.Get<Process>(Flow)!.Events[1].Verb);
        Assert.Equal("raise", model.Get<Operation>(Assign)!.Verb);
        Assert.Equal(2, model.ReferencesTo(Escalate).Count);
    }

    [Fact]
    public async Task Rename_verb_to_another_own_verb_or_a_built_in_merges_and_drops_the_definition()
    {
        await using var r = await RepoAsync();
        Assert.Equal(SaveOutcome.Saved, (await ApplyAsync(r, """{"op":"rename-verb","verb":"escalate","to":"quarantine"}""")).Outcome);
        var model = r.Store.Current!;
        Assert.DoesNotContain(model.Get<VerbVocabulary>(Vocabulary)!.Verbs, v => v.Id == Escalate);
        Assert.Equal("quarantine", model.Get<Operation>(Assign)!.Verb);
        Assert.Equal(2, model.ReferencesTo(Quarantine).Count);

        Assert.Equal(SaveOutcome.Saved, (await ApplyAsync(r, """{"op":"rename-verb","verb":"quarantine","to":"connect"}""")).Outcome);
        model = r.Store.Current!;
        Assert.Empty(model.Get<VerbVocabulary>(Vocabulary)!.Verbs);
        Assert.Equal("connect", model.Get<Process>(Flow)!.Events[1].Verb);
        Assert.Empty(await VerbFindingsAsync(r));
    }

    [Fact]
    public async Task Rename_verb_to_its_own_key_adopts_the_built_in_verb_of_that_key()
    {
        await using var r = await RepoAsync();
        await WriteAsync(r, Assign, OperationJson("route"));
        await WriteAsync(r, Flow, ProcessJson(raiseVerb: "route"));
        await WriteAsync(r, Vocabulary, VocabularyJson(escalateKey: "route"));
        Assert.Contains(("MQ9702", "/verbs/0/key"), await VerbFindingsAsync(r));

        Assert.Equal(SaveOutcome.Saved, (await ApplyAsync(r, """{"op":"rename-verb","verb":"route","to":"route"}""")).Outcome);

        Assert.DoesNotContain(r.Store.Current!.Get<VerbVocabulary>(Vocabulary)!.Verbs, v => v.Id == Escalate);
        Assert.Equal("route", r.Store.Current!.Get<Operation>(Assign)!.Verb);
        Assert.Empty(await VerbFindingsAsync(r));
    }

    [Fact]
    public async Task Remove_verb_clears_every_use_and_the_definition()
    {
        await using var r = await RepoAsync();

        Assert.Equal(SaveOutcome.Saved, (await ApplyAsync(r, """{"op":"remove-verb","verb":"escalate"}""")).Outcome);

        var model = r.Store.Current!;
        Assert.Null(model.Get<Operation>(Assign)!.Verb);
        Assert.Null(model.Get<Process>(Flow)!.Events[1].Verb);
        Assert.Equal("promote", model.Get<Process>(Flow)!.Events[0].Verb);
        Assert.Equal(["quarantine"], model.Get<VerbVocabulary>(Vocabulary)!.Verbs.Select(v => v.Key));
    }

    [Theory]
    [InlineData("""{"op":"rename-verb","verb":"promote","to":"push"}""")]
    [InlineData("""{"op":"remove-verb","verb":"nothing"}""")]
    [InlineData("""{"op":"rename-verb","verb":"escalate","to":"escalate"}""")]
    public async Task Verb_operations_on_a_built_in_or_unknown_key_are_refused_and_write_nothing(string operation)
    {
        await using var r = await RepoAsync();
        var before = r.Store.Current!.GetDocument(Vocabulary)!.Hash;

        var result = await ApplyAsync(r, operation);

        Assert.Equal(SaveOutcome.Invalid, result.Outcome);
        Assert.Contains(result.Items.SelectMany(i => i.Diagnostics), d => d.Rule == "MQ1002");
        Assert.Equal(before, r.Store.Current!.GetDocument(Vocabulary)!.Hash);
    }

    [Fact]
    public async Task A_verb_operation_with_a_stale_expected_hash_is_a_conflict()
    {
        await using var r = await RepoAsync();

        var result = await ApplyAsync(r, "{\"op\":\"remove-verb\",\"verb\":\"escalate\",\"expectedHashes\":{\"" + Assign + "\":\"stale\"}}");

        Assert.Equal(SaveOutcome.Conflict, result.Outcome);
        Assert.Equal("escalate", r.Store.Current!.Get<Operation>(Assign)!.Verb);
    }

    [Theory]
    [InlineData(DeleteResolution.RemoveReferences)]
    [InlineData(DeleteResolution.DeleteDependents)]
    public async Task Deleting_the_vocabulary_is_refused_while_own_verbs_are_named_and_either_choice_clears_them(DeleteResolution resolution)
    {
        await using var r = await RepoAsync();
        var hash = r.Store.Current!.GetDocument(Vocabulary)!.Hash;
        Assert.NotEqual(SaveOutcome.Saved, (await r.Store.DeleteAsync(Vocabulary, hash, DeleteResolution.Refuse, ChangeSource.Editor, Ct)).Outcome);

        var deleted = await r.Store.DeleteAsync(Vocabulary, hash, resolution, ChangeSource.Editor, Ct);

        Assert.Equal(SaveOutcome.Saved, deleted.Outcome);
        var model = r.Store.Current!;
        Assert.Null(model.Verbs);
        Assert.Null(model.Get<Operation>(Assign)!.Verb);
        Assert.Null(model.Get<Process>(Flow)!.Events[1].Verb);
        Assert.Equal("promote", model.Get<Process>(Flow)!.Events[0].Verb);
        Assert.Equal("promote", model.Get<Process>(Flow)!.Transitions[1].Verb);
    }

    [Fact]
    public async Task A_second_verb_vocabulary_is_MQ1009_and_ignored()
    {
        await using var r = await RepoAsync();
        var folder = Path.Combine(r.Repo.ModelRoot, "model", "vocabularies");
        File.WriteAllText(Path.Combine(folder, "verbs2.json"),
            """{"$schema":"../../.schema/v1/verb-vocabulary.json","kind":"verb-vocabulary","id":"01K9VRB0000000000000000099","name":"verbs","verbs":[{"id":"01K9VRB0000000000000000098","key":"typo"}]}""" + "\n");
        await r.Store.RefreshAsync([Path.Combine(folder, "verbs2.json")], Ct);

        Assert.Equal(Vocabulary, r.Store.Current!.Verbs!.Id);
        Assert.Contains(r.Store.Current!.LoadDiagnostics, d => d.Rule == "MQ1009" && d.Message.Contains("verb-vocabulary", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Own_verbs_are_localizable_and_named_by_key()
    {
        await using var r = await RepoAsync();
        var model = r.Store.Current!;

        Assert.Contains("verb", (IReadOnlySet<string>)Maquettiste.Engine.Localization.LocaleChains.NodeKinds);
        Assert.True(model.TryGetEntry(Quarantine, out var entry) && entry.Kind == "verb");
    }
}

/// <summary>The engine's table of built-in verbs, the schema's <c>builtInVerb</c>, the editor's list and the user guide's table agree.</summary>
public sealed class BuiltInVerbsTests
{
    private static string Root
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "maquettiste.slnx")))
                dir = dir.Parent;
            return dir!.FullName;
        }
    }

    private static readonly string[] Keys = [.. BuiltInVerbs.All.Select(v => v.Key)];

    [Fact]
    public void Keys_are_unique_kebab_case_and_their_pascal_names_are_unique()
    {
        Assert.Equal(27, Keys.Length);
        Assert.Equal(Keys.Length, Keys.Distinct(StringComparer.Ordinal).Count());
        Assert.All(Keys, k => Assert.True(BuiltInVerbs.IsKey(k), k));
        Assert.Equal(Keys.Length, Keys.Select(BuiltInVerbs.PascalName).Distinct(StringComparer.Ordinal).Count());
        Assert.All(BuiltInVerbs.All, v => Assert.Contains(v.Group, BuiltInVerbs.Groups));
        Assert.DoesNotContain(BuiltInVerbs.Suggestions, BuiltInVerbs.IsBuiltIn);
    }

    [Fact]
    public void The_schema_lists_each_built_in_verb_with_its_meaning()
    {
        var common = JsonNode.Parse(File.ReadAllText(Path.Combine(Root, "schemas/v1/common.json")))!;
        var items = common["$defs"]!["builtInVerb"]!["oneOf"]!.AsArray();
        Assert.Equal(BuiltInVerbs.All.Select(v => (v.Key, v.DisplayName, v.Meaning)),
            items.Select(i => (i!["const"]!.GetValue<string>(), i["title"]!.GetValue<string>(), i["description"]!.GetValue<string>())));
    }

    [Fact]
    public void The_editor_list_and_the_user_guide_table_follow_the_engine()
    {
        var editor = File.ReadAllText(Path.Combine(Root, "src/editor/src/model/verbs.ts"));
        Assert.Equal(Keys, Regex.Matches(editor, "\\{ key: \"([a-z0-9-]+)\"").Select(m => m.Groups[1].Value));

        var guide = File.ReadAllText(Path.Combine(Root, "docs/user-guide.md"));
        var start = guide.IndexOf("<!-- built-in-verbs:start -->", StringComparison.Ordinal);
        var end = guide.IndexOf("<!-- built-in-verbs:end -->", StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        var rows = Regex.Matches(guide[start..end], "^\\| [^|]* \\| `([a-z0-9-]+)` \\| ([^|]+) \\| ([^|]+) \\|$", RegexOptions.Multiline);
        Assert.Equal(BuiltInVerbs.All.Select(v => (v.Key, v.DisplayName, v.Meaning)),
            rows.Select(m => (m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value)));
    }
}
