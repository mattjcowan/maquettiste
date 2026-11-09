using Maquettiste.Engine.Model;
using Maquettiste.Engine.Planning;
using Maquettiste.Engine.Resolution;
using Maquettiste.Engine.Tests.Loading;
using static Maquettiste.Engine.Tests.Validation.SeveralTypesKit;

namespace Maquettiste.Engine.Tests.Resolution;

/// <summary>
/// The types key of an end with several types (several-types-design.md decision 20 and section 7): <c>ec:&lt;end id&gt;</c> hashes the end's
/// types in order and its common base, and everything that reads them records it: the relation, the end, its navigations, every entity's
/// <c>relations</c> and <c>navigations</c>, and an expression path through the end. A one-type end records none. The planner's dry-run test
/// fires it only when the hash moves. Over <see cref="Validation.SeveralTypesKit"/>.
/// </summary>
public sealed class RelationTypesKeyTests
{
    [Fact]
    public async Task Every_reader_of_an_ends_types_records_its_types_key_and_a_one_type_end_records_none()
    {
        var (h, store) = await OpenAsync();
        using var _ = h;
        await using var __ = store;
        var model = ResolutionKit.Resolve(store.Current!);
        var key = EndCoverage.TypesKey(ConcernsEnd);
        Assert.Equal("ec:" + ConcernsEnd, key);

        var concerns = model.Relations.Single(r => r.Id == Concerns);
        Assert.Contains(key, concerns.Dependencies);
        Assert.Contains(key, concerns.Ends[1].Dependencies);
        Assert.Contains(key, model.Entities.Single(e => e.Id == Comment).Navigations.Single(n => n.Name == "concerns").Dependencies);
        Assert.Contains(key, model.Entities.Single(e => e.Id == Document).Navigations.Single(n => n.Name == "concernedBy").Dependencies);
        foreach (var entity in model.Entities.Where(e => e.Id is Tool or Item))
        {
            Assert.Contains(key, entity.Relations.MembershipKeys);
            Assert.Contains(key, entity.Navigations.MembershipKeys);
            Assert.Contains(EndCoverage.TypesKey(AttachedEnd), entity.Navigations.MembershipKeys); // entity entries only: still recorded
        }

        // An expression path: Triage's guard reads in_state through Concerns; the one through the one-type OnPart records no types key.
        var triage = model.Processes.Single(p => p.Id == Triage);
        Assert.Contains(key, triage.Guards.Single(g => g.Name == "concernsOpen").Dependencies);
        Assert.DoesNotContain(triage.Guards.Single(g => g.Name == "onPartOpen").Dependencies, k => k.StartsWith(EndCoverage.TypesPrefix, StringComparison.Ordinal));

        var onPart = model.Relations.Single(r => r.Id == OnPart);
        Assert.DoesNotContain(onPart.Dependencies, k => k.StartsWith(EndCoverage.TypesPrefix, StringComparison.Ordinal));
        Assert.All(onPart.Ends, e => Assert.DoesNotContain(e.Dependencies, k => k.StartsWith(EndCoverage.TypesPrefix, StringComparison.Ordinal)));
        Assert.Null(EndCoverage.TypesHash(store.Current!, OnPartEnd));
    }

    [Fact]
    public async Task The_types_key_moves_where_the_carrier_set_and_kind_keys_do_not()
    {
        // The review's three cases: an entity moved from under a carrier inside a stereotype's coverage, an entity moved from under its base
        // under any entity, and a package rename that reorders the roots of any entity. Each changes the end's types; the carrier set of
        // approvable and the entity and package kinds keep their hashes, so only the types key follows them.
        const string Purchasing = "01JPKG00000000000000000902", Mentions = "01JRXN00000000000000000909", MentionsEnd = "01JEND00000000000000000992";
        static void Keyed(LoaderHarness x, string path, string id, string name, string package = Sales, string? stereotype = null) => x.Write(path,
            $$"""{"kind":"entity","id":"{{id}}","name":"{{name}}","package":"{{package}}"{{(stereotype is null ? "" : ",\"stereotypes\":[\"" + stereotype + "\"]")}},"key":{"attributes":["{{id[..^3]}}A{{id[^2..]}}"]},"attributes":[{"id":"{{id[..^3]}}A{{id[^2..]}}","name":"id","type":"uuid"}]}""");
        var (h, store) = await OpenAsync(x =>
        {
            x.Write("model/packages/purchasing.json", $$"""{"kind":"package","id":"{{Purchasing}}","name":"Purchasing"}""");
            Keyed(x, "model/entities/tool.json", Tool, "Tool", Purchasing);
            x.Write("model/entities/memo.json", EntityFile(Memo, "Memo", Document, "approvable"));
            x.Write("model/relations/mentions.json", RelationFile(Mentions, "Mentions", "01JEND00000000000000000991", "mentionedBy", "", MentionsEnd, """ "anyEntity":true """, "mentions", "mentions", "\"*\""));
        });
        using var _ = h;
        await using var __ = store;
        var model = store.Current!;
        string Fingerprint(ModelSnapshot m) => string.Join(" ", Engine.Processes.SubjectCoverage.CarrierSetHash(m, "approvable"), m.KindSetHash(ElementKind.Entity), m.KindSetHash(ElementKind.Package));
        string Types(ModelSnapshot m, string end) => string.Join(",", EndCoverage.TypesOf(m, EndOf(m, end == ConcernsEnd ? Concerns : Mentions, end)).Select(t => t.Name));

        async Task<ModelSnapshot> ChangeAsync(Action write, string path)
        {
            var before = store.Current!;
            write();
            await store.RefreshAsync([h.Model(path)], Ct);
            var after = store.Current!;
            Assert.Equal(Fingerprint(before), Fingerprint(after));
            return after;
        }

        Assert.Equal("Part,Document", Types(model, ConcernsEnd));
        var memo = await ChangeAsync(() => Keyed(h, "model/entities/memo.json", Memo, "Memo", stereotype: "approvable"), "model/entities/memo.json");
        Assert.Equal("Part,Document,Memo", Types(memo, ConcernsEnd));
        Assert.NotEqual(EndCoverage.TypesHash(model, ConcernsEnd), EndCoverage.TypesHash(memo, ConcernsEnd));

        Assert.DoesNotContain("PurchasedPart", Types(memo, MentionsEnd), StringComparison.Ordinal);
        var rooted = await ChangeAsync(() => Keyed(h, "model/entities/purchased-part.json", PurchasedPart, "PurchasedPart"), "model/entities/purchased-part.json");
        Assert.Contains("PurchasedPart", Types(rooted, MentionsEnd), StringComparison.Ordinal);
        Assert.NotEqual(EndCoverage.TypesHash(memo, MentionsEnd), EndCoverage.TypesHash(rooted, MentionsEnd));

        Assert.StartsWith("Tool,", Types(rooted, MentionsEnd), StringComparison.Ordinal); // Purchasing.Tool before Sales.*
        var renamed = await ChangeAsync(() => h.Write("model/packages/purchasing.json", $$"""{"kind":"package","id":"{{Purchasing}}","name":"Zeta"}"""), "model/packages/purchasing.json");
        Assert.EndsWith(",Tool", Types(renamed, MentionsEnd), StringComparison.Ordinal);
        Assert.NotEqual(EndCoverage.TypesHash(rooted, MentionsEnd), EndCoverage.TypesHash(renamed, MentionsEnd));
        Assert.True(new ImpactKeys(rooted, renamed, [Purchasing], [], [], []).IsTouched(EndCoverage.TypesKey(MentionsEnd)));
    }

    [Fact]
    public async Task The_types_key_moves_with_the_types_and_common_base_and_the_dry_run_test_fires_only_then()
    {
        var (h, store) = await OpenAsync();
        using var _ = h;
        await using var __ = store;
        var before = store.Current!;
        var attached = EndCoverage.TypesKey(AttachedEnd);
        var concerns = EndCoverage.TypesKey(ConcernsEnd);

        // A description on Tool changes no end's types: the dry-run test leaves both keys alone.
        h.Write("model/entities/tool.json", h.Read("model/entities/tool.json").Replace("\"name\":\"Tool\"", "\"name\":\"Tool\",\"description\":\"A tool.\"", StringComparison.Ordinal));
        await store.RefreshAsync([h.Model("model/entities/tool.json")], Ct);
        var same = store.Current!;
        Assert.Equal(EndCoverage.TypesHash(before, AttachedEnd), EndCoverage.TypesHash(same, AttachedEnd));
        var unchanged = new ImpactKeys(before, same, [Tool], [], [], []);
        Assert.False(unchanged.IsTouched(attached));
        Assert.False(unchanged.IsTouched(concerns));

        // Assembly no longer derives from Item: AttachedTo's types stay Part and Assembly, but they no longer share a base. Only Assembly's
        // file changes; Concerns (Part and what is approvable) keeps its types and its missing common base.
        h.Write("model/entities/assembly.json", EntityFile(Assembly, "Assembly"));
        await store.RefreshAsync([h.Model("model/entities/assembly.json")], Ct);
        var after = store.Current!;
        Assert.Equal(Item, EndCoverage.CommonBaseOf(before, EndOf(before, AttachedTo, AttachedEnd))!.Id);
        Assert.Null(EndCoverage.CommonBaseOf(after, EndOf(after, AttachedTo, AttachedEnd)));
        Assert.NotEqual(EndCoverage.TypesHash(same, AttachedEnd), EndCoverage.TypesHash(after, AttachedEnd));
        var moved = new ImpactKeys(same, after, [Assembly], [], [], []);
        Assert.True(moved.IsTouched(attached));
        Assert.False(moved.IsTouched(concerns));

        // A change that writes no entity, package, stereotype or relation never fires it.
        Assert.False(new ImpactKeys(same, after, [], [], [], []).IsTouched(attached));
    }
}
