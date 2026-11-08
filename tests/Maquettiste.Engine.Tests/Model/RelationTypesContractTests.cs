using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Tests.Loading;
using Maquettiste.Engine.Tests.Processes;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Model;

/// <summary>
/// The contract of relations with several types at an end, part 1 (several-types-design.md decisions 1, 2, 4, 5 and 35, sections 5 and 9): an
/// end names exactly one of <c>entity</c>, <c>types</c> and <c>anyEntity</c>; today's <c>entity</c> form keeps its bytes; a model holding the
/// new forms loads and indexes the entries as references; decision 2's test; and the end coverage helper's covered set, types, type order and
/// common base. Over the <c>process-basics</c> fixture with an invented model in package Sales: Part with PurchasedPart and SpecialPart
/// derived from it, Assembly, Document (which carries <c>approvable</c>) with Memo derived from it, Agreement with Contract derived from it
/// (Contract carries <c>approvable</c> itself), and Comment.
/// </summary>
public sealed class RelationTypesContractTests
{
    private const string Sales = "01JPKG00000000000000000001";
    private const string Order = "01JENT00000000000000000001";
    private const string Approvable = "01JSTR00000000000000000701";
    private const string Part = "01JENT00000000000000000701";
    private const string PurchasedPart = "01JENT00000000000000000702";
    private const string SpecialPart = "01JENT00000000000000000703";
    private const string Assembly = "01JENT00000000000000000704";
    private const string Document = "01JENT00000000000000000705";
    private const string Memo = "01JENT00000000000000000706";
    private const string Agreement = "01JENT00000000000000000707";
    private const string Contract = "01JENT00000000000000000708";
    private const string Comment = "01JENT00000000000000000709";
    private const string AttachedTo = "01JRXN00000000000000000701";
    private const string Mentions = "01JRXN00000000000000000702";
    private const string Covers = "01JRXN00000000000000000703";
    private const string Single = "01JRXN00000000000000000704";
    private const string Siblings = "01JRXN00000000000000000705";
    private const string AttachedEnd = "01JEND00000000000000000702";
    private const string MentionsEnd = "01JEND00000000000000000704";
    private const string CoversEnd = "01JEND00000000000000000706";
    private const string SingleEnd = "01JEND00000000000000000708";
    private const string SiblingsEnd = "01JEND00000000000000000712";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string EntityFile(string id, string name, string? @base = null, string? stereotype = null) =>
        $$"""{"kind":"entity","id":"{{id}}","name":"{{name}}","package":"{{Sales}}"{{(@base is null ? "" : ",\"base\":\"" + @base + "\"")}}{{(stereotype is null ? "" : ",\"stereotypes\":[\"" + stereotype + "\"]")}}}""";

    // The comment end's id is the far end's, less one.
    private static string RelationFile(string id, string name, string farEnd, string far) =>
        $$"""{"kind":"relation","id":"{{id}}","name":"{{name}}","package":"{{Sales}}","ends":[{"id":"{{farEnd[..^1]}}{{(char)(farEnd[^1] - 1)}}","entity":"{{Comment}}","role":"comment","max":"*"},{"id":"{{farEnd}}",{{far}},"role":"target","max":1}]}""";

    private static string End(string far) =>
        $$"""{"kind":"relation","id":"{{AttachedTo}}","name":"AttachedTo","ends":[{"id":"01JEND00000000000000000701","entity":"{{Comment}}","role":"comment"},{"id":"{{AttachedEnd}}",{{far}},"role":"attachedTo"}]}""";

    private static IReadOnlyList<Diagnostic> Evaluate(string schema, string json)
    {
        using var document = JsonDocument.Parse(json);
        return TestServices.Schemas.Evaluate(schema, document.RootElement, "model/x.json");
    }

    private static void Valid(string json) =>
        Assert.True(Evaluate("relation.json", json) is { Count: 0 }, string.Join("; ", Evaluate("relation.json", json).Select(d => d.JsonPointer + " " + d.Message)));

    private static void Invalid(string json, string pointer)
    {
        var found = Evaluate("relation.json", json);
        Assert.True(found.Any(d => d.Rule == "MQ1002" && (d.JsonPointer ?? "").StartsWith(pointer, StringComparison.Ordinal)),
            pointer + ": " + string.Join("; ", found.Select(d => d.JsonPointer + " " + d.Message)));
    }

    private static async Task<(LoaderHarness Harness, ModelStore Store)> OpenAsync() =>
        await ActorKit.OpenAsync(h =>
        {
            h.Write("model/stereotypes/approvable.json", $$"""{"kind":"stereotype","id":"{{Approvable}}","key":"approvable","name":"Approvable","appliesTo":["entity"]}""");
            h.Write("model/entities/part.json", EntityFile(Part, "Part"));
            h.Write("model/entities/purchased-part.json", EntityFile(PurchasedPart, "PurchasedPart", Part));
            h.Write("model/entities/special-part.json", EntityFile(SpecialPart, "SpecialPart", Part));
            h.Write("model/entities/assembly.json", EntityFile(Assembly, "Assembly"));
            h.Write("model/entities/document.json", EntityFile(Document, "Document", stereotype: "approvable"));
            h.Write("model/entities/memo.json", EntityFile(Memo, "Memo", Document));
            h.Write("model/entities/agreement.json", EntityFile(Agreement, "Agreement"));
            h.Write("model/entities/contract.json", EntityFile(Contract, "Contract", Agreement, "approvable"));
            h.Write("model/entities/comment.json", EntityFile(Comment, "Comment"));
            h.Write("model/relations/attached-to.json", RelationFile(AttachedTo, "AttachedTo", AttachedEnd,
                $$""" "types":[{"entity":"{{Part}}"},{"entity":"{{Assembly}}"},{"stereotype":"approvable"}] """));
            h.Write("model/relations/mentions.json", RelationFile(Mentions, "Mentions", MentionsEnd, """ "anyEntity":true """));
            // A carrier derived from an entity listed later is never one of the types (decision 5).
            h.Write("model/relations/covers.json", RelationFile(Covers, "Covers", CoversEnd, $$""" "types":[{"stereotype":"approvable"},{"entity":"{{Agreement}}"}] """));
            h.Write("model/relations/single.json", RelationFile(Single, "Single", SingleEnd, $$""" "types":[{"entity":"{{Part}}"}] """));
            h.Write("model/relations/siblings.json", RelationFile(Siblings, "Siblings", SiblingsEnd, $$""" "types":[{"entity":"{{SpecialPart}}"},{"entity":"{{PurchasedPart}}"}] """));
        });

    private static RelationEnd EndOf(ModelSnapshot model, string relation, string end) =>
        model.Get<Relation>(relation)!.Ends.Single(e => e.Id == end);

    // ---- schema ----

    [Fact]
    public void An_end_names_exactly_one_of_entity_types_and_any_entity()
    {
        Valid(End($$""" "entity":"{{Part}}" """));
        Valid(End($$""" "types":[{"entity":"{{Part}}"}] """));
        Valid(End($$""" "types":[{"entity":"{{Part}}"},{"stereotype":"approvable"}] """));
        Valid(End(""" "types":[{"stereotype":"approvable"},{"stereotype":"tracked"}] """));
        Valid(End(""" "anyEntity":true """));

        // None, or two of the three.
        Invalid(End(""" "navigation":"" """), "/ends/1");
        Invalid(End($$""" "entity":"{{Part}}","types":[{"entity":"{{Assembly}}"}] """), "/ends/1");
        Invalid(End($$""" "entity":"{{Part}}","anyEntity":true """), "/ends/1");
        Invalid(End($$""" "types":[{"entity":"{{Part}}"}],"anyEntity":true """), "/ends/1");

        // anyEntity is only ever true; types holds at least one entry, without repeats, each naming exactly one of entity and stereotype.
        Invalid(End(""" "anyEntity":false """), "/ends/1");
        Invalid(End(""" "types":[] """), "/ends/1/types");
        Invalid(End($$""" "types":[{"entity":"{{Part}}"},{"entity":"{{Part}}"}] """), "/ends/1/types");
        Invalid(End($$""" "types":[{"entity":"{{Part}}","stereotype":"approvable"}] """), "/ends/1/types/0");
        Invalid(End(""" "types":[{}] """), "/ends/1/types/0");
        Invalid(End(""" "types":[{"stereotype":""}] """), "/ends/1/types/0/stereotype");
        Invalid(End(""" "types":[{"entity":"Part"}] """), "/ends/1/types/0/entity");
        Invalid(End($$""" "types":[{"entity":"{{Part}}","role":"x"}] """), "/ends/1/types/0");
        Invalid(End($$""" "types":["{{Part}}"] """), "/ends/1/types/0");
    }

    [Fact]
    public void The_schema_titles_the_entry_and_orders_the_new_keys_after_entity()
    {
        var schema = JsonNode.Parse(File.ReadAllText(Path.Combine(Fixtures.RepoRoot, "schemas", "v1", "relation.json")))!;
        var end = schema["properties"]!["ends"]!["items"]!;
        Assert.Equal(["id", "entity", "types", "anyEntity", "role"], end["x-order"]!.AsArray().Select(n => n!.GetValue<string>()).Take(5));
        Assert.Equal("End type", end["properties"]!["types"]!["items"]!["title"]!.GetValue<string>());
        Assert.Equal(["id", "role"], end["required"]!.AsArray().Select(n => n!.GetValue<string>()));
    }

    // ---- today's form ----

    [Fact]
    public async Task Todays_entity_form_keeps_its_bytes_and_its_meaning()
    {
        using var h = new LoaderHarness();
        h.CopyFixture("models", "billing");
        foreach (var file in new[] { "contains.json", "places.json", "refers-to.json", "settles.json" })
        {
            var path = "model/relations/" + file;
            var text = h.Read(path);
            Assert.Equal(text, Encoding.UTF8.GetString(TestServices.Json.Write(JsonNode.Parse(text)!, "relation.json", ".maquettiste/" + path)));
        }

        await using var store = h.NewStore();
        await store.LoadAsync(Ct);
        var model = store.Current!;
        Assert.DoesNotContain(model.LoadDiagnostics, d => d.Rule is "MQ1002" or "MQ1003");
        foreach (var relation in model.All<Relation>())
        {
            Assert.False(EndCoverage.HasSeveralTypes(relation));
            foreach (var end in relation.Ends)
            {
                Assert.Equal((end.Entity, (IReadOnlyList<RelationEndType>?)null, (bool?)null, false), (end.SingleEntity, end.Types, end.AnyEntity, end.HasSeveralTypes));

                // The record writes no new key.
                var json = JsonSerializer.SerializeToNode(end, EngineJson.Options)!.AsObject();
                Assert.DoesNotContain(json, p => p.Key is "types" or "anyEntity" or "hasSeveralTypes" or "singleEntity");
                Assert.Equal(end.Entity, json["entity"]!.GetValue<string>());
            }
        }

        // No end with several types: the helper is empty, and the naming key hashes the same as an empty model's.
        var coverage = EndCoverage.Of(model);
        Assert.False(coverage.Any);
        Assert.Empty(coverage.Ends);
        Assert.Same(coverage, EndCoverage.Of(model));
        using var empty = new LoaderHarness();
        await using var emptyStore = empty.NewStore();
        await emptyStore.LoadAsync(Ct);
        Assert.Equal(EndCoverage.NamingHash(emptyStore.Current!), EndCoverage.NamingHash(model));
    }

    // ---- the new forms load ----

    [Fact]
    public async Task Types_and_any_entity_load_round_trip_and_index_their_entries_as_references()
    {
        var (h, store) = await OpenAsync();
        using var _ = h;
        await using var __ = store;
        var model = store.Current!;
        Assert.DoesNotContain(model.LoadDiagnostics, d => (d.FilePath ?? "").Contains("/relations/", StringComparison.Ordinal) && d.Rule == "MQ1002");

        // The files keep their keys in schema order.
        foreach (var file in new[] { "attached-to.json", "mentions.json", "covers.json", "single.json", "siblings.json" })
        {
            var path = "model/relations/" + file;
            var canonical = Encoding.UTF8.GetString(TestServices.Json.Write(JsonNode.Parse(h.Read(path))!, "relation.json", ".maquettiste/" + path));
            Assert.Equal(canonical, Encoding.UTF8.GetString(TestServices.Json.Write(JsonNode.Parse(canonical)!, "relation.json", ".maquettiste/" + path)));
        }

        var written = JsonNode.Parse(Encoding.UTF8.GetString(TestServices.Json.Write(JsonNode.Parse(h.Read("model/relations/attached-to.json"))!, "relation.json",
            ".maquettiste/model/relations/attached-to.json")))!["ends"]![1]!.AsObject();
        Assert.Equal(["id", "types", "role", "max"], written.Select(p => p.Key));
        Assert.Equal($$"""[{"entity":"{{Part}}"},{"entity":"{{Assembly}}"},{"stereotype":"approvable"}]""", written["types"]!.ToJsonString());

        // The records.
        var attached = EndOf(model, AttachedTo, AttachedEnd);
        Assert.Null(attached.Entity);
        Assert.Equal([(Part, (string?)null), (Assembly, null), (null, "approvable")], attached.Types!.Select(t => (t.Entity, t.Stereotype)));
        Assert.Null(attached.AnyEntity);
        var mentions = EndOf(model, Mentions, MentionsEnd);
        Assert.Equal(((string?)null, (IReadOnlyList<RelationEndType>?)null, (bool?)true), (mentions.Entity, mentions.Types, mentions.AnyEntity));
        var record = JsonSerializer.SerializeToNode(mentions, EngineJson.Options)!.AsObject();
        Assert.Equal(["id", "anyEntity", "role", "navigation", "min", "max", "onDelete", "ordered"], record.Select(p => p.Key));

        // The entries are references: an entity entry at its pointer, a stereotype entry to the stereotype.
        Assert.Contains(model.ReferencesTo(Assembly), r => r.FromElementId == AttachedTo && r.JsonPointer == "/ends/1/types/1/entity");
        Assert.Contains(model.ReferencesTo(Part), r => r.FromElementId == AttachedTo && r.JsonPointer == "/ends/1/types/0/entity");
        Assert.Contains(model.ReferencesTo(Approvable), r => r.FromElementId == AttachedTo);
        Assert.Contains(model.ReferencesTo(Approvable), r => r.FromElementId == Covers);

        // The index row leaves entity out on an end with several types, and keeps it on a one-entry list.
        var rows = model.Summaries().ToDictionary(r => r.Id, StringComparer.Ordinal);
        Assert.Equal([Comment, null], rows[AttachedTo].Ends!.Select(e => e.Entity));
        Assert.Equal([Comment, null], rows[Mentions].Ends!.Select(e => e.Entity));
        Assert.Equal([Comment, Part], rows[Single].Ends!.Select(e => e.Entity));
    }

    [Fact]
    public async Task Decision_two_reads_the_file_alone()
    {
        var (h, store) = await OpenAsync();
        using var _ = h;
        await using var __ = store;
        var model = store.Current!;

        // A list that holds one entity is a one-type end; two entries, a stereotype entry or anyEntity make an end with several types,
        // even where the entries cover one type once MQ3043 is fixed, or a stereotype one entity or none carries.
        var single = EndOf(model, Single, SingleEnd);
        Assert.Equal((false, Part), (single.HasSeveralTypes, single.SingleEntity));
        Assert.False(EndCoverage.HasSeveralTypes(model.Get<Relation>(Single)!));
        foreach (var (relation, end) in new[] { (AttachedTo, AttachedEnd), (Mentions, MentionsEnd), (Covers, CoversEnd), (Siblings, SiblingsEnd) })
        {
            var e = EndOf(model, relation, end);
            Assert.Equal((true, (string?)null), (e.HasSeveralTypes, e.SingleEntity));
            Assert.True(EndCoverage.HasSeveralTypes(model.Get<Relation>(relation)!));
        }

        var nobodyCarries = new RelationEnd { Id = "x", Role = "r", Types = [new RelationEndType { Stereotype = "unused" }] };
        Assert.True(nobodyCarries.HasSeveralTypes);
        var baseAndDerived = new RelationEnd { Id = "x", Role = "r", Types = [new RelationEndType { Entity = Part }, new RelationEndType { Entity = PurchasedPart }] };
        Assert.True(baseAndDerived.HasSeveralTypes);
        Assert.Equal([AttachedEnd, MentionsEnd, CoversEnd, SiblingsEnd], EndCoverage.Of(model).Ends.Order(StringComparer.Ordinal));
        Assert.True(EndCoverage.Of(model).Any);
    }

    [Fact]
    public async Task A_model_with_the_new_forms_validates_and_resolves_without_failing()
    {
        var (h, store) = await OpenAsync();
        using var _ = h;
        await using var __ = store;
        var model = store.Current!;

        // Until the rules and the resolver read the end coverage helper (wave 2), a relation with an end of several types is left out of the
        // resolved model, as one with a dangling end is; a one-entry list resolves as today's entity.
        var diagnostics = await ActorKit.ValidateAsync(h, model);
        Assert.DoesNotContain(diagnostics, d => d.Rule is "MQ2001" or "MQ1002" && (d.FilePath ?? "").Contains("/relations/", StringComparison.Ordinal));
        var resolved = Resolution.ResolutionKit.Resolve(model);
        Assert.DoesNotContain(resolved.Relations, r => r.Id is AttachedTo or Mentions or Covers or Siblings);
        Assert.Equal([Comment, Part], resolved.Relations.Single(r => r.Id == Single).Ends.Select(e => e.Entity.Id));
    }

    // ---- the end coverage helper ----

    [Fact]
    public async Task The_helper_answers_the_covered_set_the_types_in_order_and_the_common_base()
    {
        var (h, store) = await OpenAsync();
        using var _ = h;
        await using var __ = store;
        var model = store.Current!;
        IEnumerable<string> Ids(IEnumerable<Entity> entities) => entities.Select(e => e.Id);

        // Entities with their derived entities, and the carriers of a stereotype with theirs (Memo derives from the carrier Document).
        var attached = EndOf(model, AttachedTo, AttachedEnd);
        Assert.Equal(new[] { Part, PurchasedPart, SpecialPart, Assembly, Document, Memo, Contract }.Order(StringComparer.Ordinal), EndCoverage.CoveredBy(model, attached));
        Assert.Equal([Part, Assembly, Contract, Document], Ids(EndCoverage.TypesOf(model, attached)));
        Assert.Null(EndCoverage.CommonBaseOf(model, attached));
        Assert.True(EndCoverage.Covers(model, attached, Memo));
        Assert.True(EndCoverage.Covers(model, attached, PurchasedPart));
        Assert.False(EndCoverage.Covers(model, attached, Agreement));
        Assert.False(EndCoverage.Covers(model, attached, Comment));

        // A carrier derived from an entity listed later is not a type: Contract comes under Agreement.
        var covers = EndOf(model, Covers, CoversEnd);
        Assert.Equal([Document, Agreement], Ids(EndCoverage.TypesOf(model, covers)));
        Assert.True(EndCoverage.Covers(model, covers, Contract));

        // Any entity: every entity of the model; its types are the entities with no base, by qualified name then id.
        var mentions = EndOf(model, Mentions, MentionsEnd);
        Assert.Equal(model.All<Entity>().Select(e => e.Id).Order(StringComparer.Ordinal), EndCoverage.CoveredBy(model, mentions));
        Assert.Equal([Agreement, Assembly, Comment, Document, Order, Part], Ids(EndCoverage.TypesOf(model, mentions)));

        // Siblings share their base.
        var siblings = EndOf(model, Siblings, SiblingsEnd);
        Assert.Equal([SpecialPart, PurchasedPart], Ids(EndCoverage.TypesOf(model, siblings)));
        Assert.Equal(Part, EndCoverage.CommonBaseOf(model, siblings)?.Id);

        // A one-type end, also written as a one-entry list: its entity with its derived entities, and itself as type and base.
        var single = EndOf(model, Single, SingleEnd);
        Assert.Equal(new[] { Part, PurchasedPart, SpecialPart }.Order(StringComparer.Ordinal), EndCoverage.CoveredBy(model, single));
        Assert.Equal([Part], Ids(EndCoverage.TypesOf(model, single)));
        Assert.Equal(Part, EndCoverage.CommonBaseOf(model, single)?.Id);
        Assert.True(EndCoverage.Covers(model, single, SpecialPart));
        Assert.False(EndCoverage.Covers(model, single, Assembly));
        var comment = model.Get<Relation>(Single)!.Ends[0];
        Assert.Equal([Comment], EndCoverage.CoveredBy(model, comment));
        Assert.Equal([Comment], Ids(EndCoverage.TypesOf(model, comment)));
    }

    [Fact]
    public async Task The_naming_key_follows_the_ends_that_name_a_stereotype_or_any_entity()
    {
        var (h, store) = await OpenAsync();
        using var _ = h;
        await using var __ = store;
        var before = store.Current!;

        // An edit to an entity entry, or to an end that names no stereotype, leaves it; an end that stops naming a stereotype changes it.
        var single = JsonNode.Parse(h.Read("model/relations/single.json"))!;
        single["ends"]![1]!["types"]![0]!["entity"] = Assembly;
        Assert.Equal(SaveOutcome.Saved, (await store.SaveAsync(Single, Encoding.UTF8.GetBytes(single.ToJsonString()), before.GetDocument(Single)!.Hash, ChangeSource.Editor, Ct)).Outcome);
        Assert.NotSame(before, store.Current);
        Assert.Equal(EndCoverage.NamingHash(before), EndCoverage.NamingHash(store.Current!));

        var mid = store.Current!;
        var covers = JsonNode.Parse(h.Read("model/relations/covers.json"))!;
        covers["ends"]![1]!["types"]![0] = new JsonObject { ["entity"] = Document };
        Assert.Equal(SaveOutcome.Saved, (await store.SaveAsync(Covers, Encoding.UTF8.GetBytes(covers.ToJsonString()), mid.GetDocument(Covers)!.Hash, ChangeSource.Editor, Ct)).Outcome);
        Assert.NotEqual(EndCoverage.NamingHash(mid), EndCoverage.NamingHash(store.Current!));
    }
}
