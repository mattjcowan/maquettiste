using System.Text.Json;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Resolution;
using Maquettiste.Engine.Tests.Loading;
using Maquettiste.Engine.Tests.Processes;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Resolution;

/// <summary>
/// Relations with several types at an end, part 1, in the resolved model, the records, the index and deletes (several-types-design.md
/// decisions 5, 19 to 21 and sections 7 and 9). Over the <c>process-basics</c> fixture with an invented model in package Sales: Part with
/// PurchasedPart and SpecialPart derived from it, Assembly, Document (which carries <c>approvable</c>) with Memo derived from it, Agreement
/// with Contract derived from it (Contract carries <c>approvable</c> itself), and Comment. AttachedTo links a comment to one part, assembly or
/// approvable thing; Mentions to any entities; Siblings to purchased or special parts (common base Part); Single writes a one-entry list.
/// </summary>
public sealed class RelationTypesResolverTests
{
    private const string Sales = "01JPKG00000000000000000001";
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
    private const string Siblings = "01JRXN00000000000000000703";
    private const string Single = "01JRXN00000000000000000704";
    private const string AttachedEnd = "01JEND00000000000000000702";
    private const string MentionsEnd = "01JEND00000000000000000704";
    private const string SiblingsEnd = "01JEND00000000000000000706";

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string EntityFile(string id, string name, string? @base = null, string? stereotype = null) =>
        $$"""{"kind":"entity","id":"{{id}}","name":"{{name}}","package":"{{Sales}}"{{(@base is null ? "" : ",\"base\":\"" + @base + "\"")}}{{(stereotype is null ? "" : ",\"stereotypes\":[\"" + stereotype + "\"]")}}}""";

    // The comment end's id is the far end's, less one; the comment end's navigation is generated on each of the far end's types.
    private static string RelationFile(string id, string name, string farEnd, string far, string max, string back, string forth) =>
        $$"""{"kind":"relation","id":"{{id}}","name":"{{name}}","package":"{{Sales}}","ends":[{"id":"{{farEnd[..^1]}}{{(char)(farEnd[^1] - 1)}}","entity":"{{Comment}}","role":"comment","max":"*","navigation":"{{back}}"},{"id":"{{farEnd}}",{{far}},"role":"target","max":{{max}},"navigation":"{{forth}}"}]}""";

    private static void Write(LoaderHarness h, bool withSingle = true)
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
            $$""" "types":[{"entity":"{{Part}}"},{"entity":"{{Assembly}}"},{"stereotype":"approvable"}] """, "1", "comments", "attachedTo"));
        h.Write("model/relations/mentions.json", RelationFile(Mentions, "Mentions", MentionsEnd, """ "anyEntity":true """, "\"*\"", "", "mentions"));
        h.Write("model/relations/siblings.json", RelationFile(Siblings, "Siblings", SiblingsEnd,
            $$""" "types":[{"entity":"{{PurchasedPart}}"},{"entity":"{{SpecialPart}}"}] """, "1", "siblingNotes", "sibling"));
        if (withSingle)
            h.Write("model/relations/single.json", RelationFile(Single, "Single", "01JEND00000000000000000708", $$""" "types":[{"entity":"{{Part}}"}] """, "1", "singles", "single"));
    }

    private static async Task<(LoaderHarness Harness, ModelStore Store)> OpenAsync() => await ActorKit.OpenAsync(h => Write(h));

    private static IEnumerable<string> Ids(IEnumerable<IResolvedObject> objects) => objects.Select(o => o.Id);

    private static REntity EntityOf(ResolvedModel model, string id) => model.Entities.Single(e => e.Id == id);

    private static RRelation RelationOf(ResolvedModel model, string id) => model.Relations.Single(r => r.Id == id);

    // ---- the resolved model ----

    [Fact]
    public async Task An_end_resolves_its_types_stereotypes_any_entity_and_common_base_for_each_form()
    {
        var (h, store) = await OpenAsync();
        using var _ = h;
        await using var __ = store;
        var model = ResolutionKit.Resolve(store.Current!);

        // Entity entries in file order, then the stereotype's carriers by qualified name; Memo is reached through Document.
        var attached = RelationOf(model, AttachedTo).Ends[1];
        Assert.Null(attached.Entity);
        Assert.Equal([Part, Assembly, Contract, Document], Ids(attached.Types));
        Assert.Equal((true, false), (attached.HasSeveralTypes, attached.AnyEntity));
        Assert.Equal([Approvable], Ids(attached.Stereotypes));
        Assert.Null(attached.CommonBase);
        Assert.Equal(new[] { "cs:approvable", "e:" + AttachedTo, "ec:" + AttachedEnd, "e:" + Assembly, "e:" + Part, "r:" + Assembly, "r:" + Part }.Order(StringComparer.Ordinal), attached.Dependencies.Order(StringComparer.Ordinal));

        // Any entity: every entity with no base, by qualified name, then id.
        var mentioned = RelationOf(model, Mentions).Ends[1];
        Assert.Equal((true, true), (mentioned.HasSeveralTypes, mentioned.AnyEntity));
        Assert.Contains(Comment, Ids(mentioned.Types));
        Assert.DoesNotContain(Memo, Ids(mentioned.Types));
        Assert.Empty(mentioned.Stereotypes);
        // Its types key, which a base change or a rename that reorders the roots moves; the entity and package kinds are not read.
        Assert.Contains("ec:" + MentionsEnd, mentioned.Dependencies);
        Assert.DoesNotContain("k:entity", mentioned.Dependencies);

        // Two derived entities share their base.
        Assert.Equal(Part, RelationOf(model, Siblings).Ends[1].CommonBase!.Id);

        // A one-entry list is a one-type end: its entity, alone in types, and its own common base.
        var single = RelationOf(model, Single).Ends[1];
        Assert.Equal((Part, false, false), (single.Entity!.Id, single.HasSeveralTypes, single.AnyEntity));
        Assert.Equal([Part], Ids(single.Types));
        Assert.Same(single.Entity, single.CommonBase);
        Assert.Empty(single.Stereotypes);
        var comment = RelationOf(model, AttachedTo).Ends[0];
        Assert.Equal([Comment], Ids(comment.Types));
        Assert.Equal(Comment, comment.CommonBase!.Id);
    }

    [Fact]
    public async Task Navigations_are_held_on_each_type_and_by_inheritance_and_relations_listed_on_each_type()
    {
        var (h, store) = await OpenAsync();
        using var _ = h;
        await using var __ = store;
        var model = ResolutionKit.Resolve(store.Current!);

        // The navigation to the end with several types: no target, its types as targets.
        var attachedTo = EntityOf(model, Comment).Navigations.Single(n => n.Name == "attachedTo");
        Assert.Null(attachedTo.Target);
        Assert.Equal([Part, Assembly, Contract, Document], Ids(attachedTo.Targets));
        Assert.Equal(Comment, attachedTo.From.Entity!.Id);

        // The other end's navigation: one object, held on each type and inherited by their derived entities.
        var comments = EntityOf(model, Part).Navigations.Single(n => n.Name == "comments");
        Assert.Null(comments.From.Entity);
        Assert.Equal(Comment, comments.Target!.Id);
        Assert.Equal([Comment], Ids(comments.Targets));
        foreach (var holder in new[] { Assembly, Contract, Document, Memo, PurchasedPart, SpecialPart })
            Assert.Same(comments, EntityOf(model, holder).Navigations.Single(n => n.Name == "comments"));
        Assert.DoesNotContain(EntityOf(model, Agreement).Navigations, n => n.Name == "comments");
        Assert.Contains("cs:approvable", comments.Dependencies);

        // Listed in entity.relations of each of its types and of the other end's entity, not of derived entities.
        foreach (var listing in new[] { Part, Assembly, Contract, Document, Comment })
            Assert.Contains(AttachedTo, Ids(EntityOf(model, listing).Relations));
        Assert.DoesNotContain(AttachedTo, Ids(EntityOf(model, Memo).Relations));

        // While an end names a stereotype or any entity, every entity's lists key on et:ends and the named carrier sets.
        Assert.Contains("et:ends", EntityOf(model, Agreement).Navigations.MembershipKeys);
        Assert.Contains("cs:approvable", EntityOf(model, Agreement).Relations.MembershipKeys);

        // No database stores such a relation in part 1.
        Assert.Empty(RelationOf(model, AttachedTo).Mappings);
    }

    [Fact]
    public async Task Records_carry_the_new_members_and_leave_them_out_on_one_type_ends()
    {
        var (h, store) = await OpenAsync();
        using var _ = h;
        await using var __ = store;
        var model = ResolutionKit.Resolve(store.Current!);
        Maquettiste.Engine.Tests.Editor.ContractCheck.Validate(model);
        RelationRecord Record(string id) => (RelationRecord)Engine.ResolvedRecords.Entries(model, "relations", null).Single(e => e.Id == id).Project();

        var attached = JsonSerializer.Serialize(Record(AttachedTo).Ends[1], Web);
        Assert.Contains("\"entity\":null", attached, StringComparison.Ordinal);
        Assert.Contains($"\"types\":[\"{Part}\",\"{Assembly}\",\"{Contract}\",\"{Document}\"],\"hasSeveralTypes\":true,\"stereotypes\":[\"{Approvable}\"]", attached, StringComparison.Ordinal);
        Assert.DoesNotContain("commonBase", attached, StringComparison.Ordinal);
        Assert.DoesNotContain("anyEntity", attached, StringComparison.Ordinal);
        Assert.Contains($"\"commonBase\":\"{Part}\"", JsonSerializer.Serialize(Record(Siblings).Ends[1], Web), StringComparison.Ordinal);
        Assert.Contains("\"anyEntity\":true", JsonSerializer.Serialize(Record(Mentions).Ends[1], Web), StringComparison.Ordinal);

        // A one-type end, a one-entry list included, writes today's members only.
        foreach (var end in new[] { Record(AttachedTo).Ends[0], Record(Single).Ends[1] })
        {
            var json = JsonSerializer.Serialize(end, Web);
            foreach (var member in new[] { "\"types\"", "hasSeveralTypes", "\"stereotypes\"", "anyEntity", "commonBase" })
                Assert.DoesNotContain(member, json, StringComparison.Ordinal);
        }

        var comment = (EntityRecord)Engine.ResolvedRecords.Entries(model, "entities", null).Single(e => e.Id == Comment).Project();
        var navigations = JsonSerializer.Serialize(comment.Navigations, Web);
        Assert.Contains($"\"target\":null,\"isCollection\":false,\"targets\":[\"{Part}\",\"{Assembly}\",\"{Contract}\",\"{Document}\"]", navigations, StringComparison.Ordinal);
        Assert.Contains($"\"target\":\"{Part}\",\"isCollection\":false}}", navigations, StringComparison.Ordinal); // single: no targets
    }

    [Fact]
    public async Task A_carrier_gaining_the_named_stereotype_changes_the_carrier_set_key_the_lists_and_paths_read()
    {
        var (h, store) = await OpenAsync();
        using var _ = h;
        await using var __ = store;
        var before = store.Current!;
        var model = ResolutionKit.Resolve(before);
        Assert.Contains("cs:approvable", EntityOf(model, Agreement).Navigations.MembershipKeys);

        h.Write("model/entities/agreement.json", EntityFile(Agreement, "Agreement", stereotype: "approvable"));
        await store.RefreshAsync([h.Model("model/entities/agreement.json")], Ct);
        var after = store.Current!;
        Assert.NotEqual(Engine.Processes.SubjectCoverage.CarrierSetHash(before, "approvable"), Engine.Processes.SubjectCoverage.CarrierSetHash(after, "approvable"));
        Assert.Equal(EndCoverage.NamingHash(before), EndCoverage.NamingHash(after)); // the ends still name the same stereotype
        var resolved = ResolutionKit.Resolve(after);
        Assert.Contains("comments", EntityOf(resolved, Agreement).Navigations.Select(n => n.Name));
        Assert.Equal([Part, Assembly, Agreement, Document], Ids(RelationOf(resolved, AttachedTo).Ends[1].Types)); // Contract now through Agreement
    }

    // ---- the index ----

    [Fact]
    public async Task Index_rows_carry_the_end_entries_any_entity_and_the_carriers_in_order()
    {
        var (h, store) = await OpenAsync();
        using var _ = h;
        await using var __ = store;
        var rows = store.Current!.Summaries();

        var attached = rows.Single(r => r.Id == AttachedTo).Ends![1];
        Assert.Null(attached.Entity);
        Assert.Equal([Part, Assembly], attached.Entities!);
        Assert.Equal([Approvable], attached.Stereotypes!);
        Assert.Equal([Contract, Document], attached.Covers!);
        Assert.False(attached.AnyEntity);
        var mentioned = rows.Single(r => r.Id == Mentions).Ends![1];
        Assert.Equal((null, null, null, null, true), (mentioned.Entity, mentioned.Entities, mentioned.Stereotypes, mentioned.Covers, mentioned.AnyEntity));
        var single = rows.Single(r => r.Id == Single).Ends![1];
        Assert.Equal((Part, null, null), (single.Entity, single.Stereotypes, single.Covers));
        Assert.Equal([Part], single.Entities!);

        // Equality compares the lists by value; one-type ends write today's JSON.
        Assert.Equal(attached, attached with { Entities = [Part, Assembly], Covers = [Contract, Document] });
        Assert.NotEqual(attached, attached with { Covers = [Document, Contract] });
        Assert.Equal($"{{\"entity\":\"{Comment}\",\"role\":\"comment\"}}", JsonSerializer.Serialize(rows.Single(r => r.Id == AttachedTo).Ends![0], Web));
        Assert.Equal("maquettiste-index/e14", Engine.ModelReads.IndexFormat);

        // Carriers come from other files: an entity gaining the stereotype refreshes covers.
        h.Write("model/entities/assembly.json", EntityFile(Assembly, "Assembly", stereotype: "approvable"));
        await store.RefreshAsync([h.Model("model/entities/assembly.json")], Ct);
        Assert.Equal([Assembly, Contract, Document], store.Current!.Summaries().Single(r => r.Id == AttachedTo).Ends![1].Covers!);
    }

    // ---- deletes and references ----

    [Fact]
    public async Task Deleting_a_listed_entity_drops_its_entry_under_either_choice_and_the_last_entry_takes_the_end()
    {
        var (h, store) = await OpenAsync();
        using var _ = h;
        await using var __ = store;
        var model = store.Current!;
        Assert.Contains(model.ReferencesTo(Assembly), r => r.FromElementId == AttachedTo && r.JsonPointer == "/ends/1/types/1/entity");
        Assert.Contains(model.ReferencesTo(Approvable), r => r.FromElementId == AttachedTo && r.JsonPointer == "/ends/1/types/2/stereotype");

        foreach (var resolution in new[] { DeleteResolution.RemoveReferences, DeleteResolution.DeleteDependents })
        {
            var plan = await store.GetDeletePlanAsync([Assembly], resolution, Ct);
            Assert.True(plan.Outcome == SaveOutcome.Saved, resolution + ": " + string.Join("; ", plan.Refused.Select(r => r.Why)));
            Assert.Contains(plan.Removes, r => (r.Id, r.Pointer) == (AttachedTo, "/ends/1/types/1"));
            Assert.DoesNotContain(plan.Deletes, d => d.Id == AttachedTo);
        }

        // Deleting PurchasedPart leaves Siblings one entity entry: a valid one-type end.
        var siblings = await store.GetDeletePlanAsync([PurchasedPart], DeleteResolution.RemoveReferences, Ct);
        Assert.Contains(siblings.Removes, r => (r.Id, r.Pointer) == (Siblings, "/ends/1/types/0"));

        // The last entry is today's relation that loses an end: refused, or the relation deleted.
        var refused = await store.GetDeletePlanAsync([Part], DeleteResolution.RemoveReferences, Ct);
        Assert.Contains(refused.Refused, r => r.Id == Single);
        var deleted = await store.GetDeletePlanAsync([Part], DeleteResolution.DeleteDependents, Ct);
        Assert.Contains(deleted.Deletes, d => d.Id == Single);
        Assert.Contains(deleted.Removes, r => (r.Id, r.Pointer) == (AttachedTo, "/ends/1/types/0"));

        // A stereotype's entry goes the same way.
        var stereotype = await store.GetDeletePlanAsync([Approvable], DeleteResolution.RemoveReferences, Ct);
        Assert.Contains(stereotype.Removes, r => (r.Id, r.Pointer) == (AttachedTo, "/ends/1/types/2"));
    }
}
