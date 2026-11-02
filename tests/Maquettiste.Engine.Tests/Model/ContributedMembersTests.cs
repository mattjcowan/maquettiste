using Maquettiste.Engine.Model;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Model;

/// <summary>
/// The contributed-members mechanism (next-phase-2-design.md section 3.1, D6): what a holder inherits from the stereotypes of its
/// effective set, keyed by holder and member id, and the kind-free problems of its redefines and excludes entries.
/// </summary>
public sealed class ContributedMembersTests
{
    private const string Holder = "01J92P0V0JR8BE8253SKT29ZG7@01J92P0V1QRN2181XM2ZWE02W4";

    private static readonly IReadOnlySet<string> NoSkip = new HashSet<string>(StringComparer.Ordinal);

    private static readonly SequentialIdGenerator Ids = new(31);

    private static Column Col(string name, string? id = null) => new() { Id = id ?? Ids.NewId(), Name = name, Type = "datetimeoffset" };

    private static AppliedStereotype Applied(string key, params Column[] columns) =>
        new(new Stereotype { Id = Ids.NewId(), Key = key, Name = key, Columns = columns }, 0, null);

    private static IReadOnlyList<InheritedMember<Column>> Inherit(IReadOnlyList<AppliedStereotype> effective, IReadOnlyList<TableRedefinition>? redefines = null,
        IReadOnlyList<string>? excludes = null, IReadOnlySet<string>? skip = null) =>
        ContributedMembers.Inherit(Holder, effective, s => s.Columns, c => c.Id, redefines ?? [], excludes ?? [], skip ?? NoSkip,
            (c, r) => c with { Nullable = r.Nullable ?? c.Nullable, Comment = r.Comment ?? c.Comment });

    [Fact]
    public void Inherit_keys_members_by_holder_and_member_id()
    {
        var created = Col("created_at");
        var updated = Col("updated_at");
        var audited = Applied("audited", created, updated);

        var members = Inherit([audited]);

        Assert.Equal([Holder + "." + created.Id, Holder + "." + updated.Id], members.Select(m => m.Key));
        Assert.Equal([created.Id, updated.Id], members.Select(m => m.MemberId));
        Assert.All(members, m => Assert.Same(audited, m.From));
        Assert.All(members, m => Assert.Empty(m.Redefined));
        // Without a redefinition the member is the stereotype's own record; keying the record's ids is the holder kind's job.
        Assert.Same(created, members[0].Member);
        Assert.Equal(Holder + "." + created.Id, ContributedMembers.KeyOf(Holder, created.Id));
    }

    [Fact]
    public void Excluded_and_skipped_members_are_left_out()
    {
        var created = Col("created_at");
        var updated = Col("updated_at");
        var unnamed = Col("no_id", id: "");
        var owner = Col("owner_id");

        var members = Inherit([Applied("audited", created, updated, unnamed), Applied("owned", owner)], excludes: [created.Id], skip: new HashSet<string>(["owned"]));

        Assert.Equal([updated.Id], members.Select(m => m.MemberId));
        // A member both excluded and redefined stays out: the exclusion wins (CheckLinks reports the pair).
        members = Inherit([Applied("audited", created, updated)], [new TableRedefinition { Member = created.Id, Nullable = true }], [created.Id]);
        Assert.Equal([updated.Id], members.Select(m => m.MemberId));
    }

    [Fact]
    public void A_redefinition_applies_only_its_facets_and_lists_them()
    {
        var created = Col("created_at") with { Nullable = false, Comment = "When the row was written" };
        var updated = Col("updated_at");
        var redefinition = new TableRedefinition { Member = created.Id, Nullable = true };

        var members = Inherit([Applied("audited", created, updated)], [redefinition]);

        Assert.Equal(["nullable"], members[0].Redefined);
        Assert.True(members[0].Member.Nullable);
        Assert.Equal("When the row was written", members[0].Member.Comment);
        Assert.Equal(created.Type, members[0].Member.Type);
        Assert.Equal(Holder + "." + created.Id, members[0].Key);
        // The other member is untouched, and a second entry naming the member is not applied.
        Assert.Same(updated, members[1].Member);
        members = Inherit([Applied("audited", created, updated)], [redefinition, new TableRedefinition { Member = created.Id, Comment = "Other" }]);
        Assert.Equal("When the row was written", members[0].Member.Comment);
        Assert.Equal(["comment", "nullable"], new TableRedefinition { Member = created.Id, Nullable = false, Comment = "x" }.Facets);
    }

    [Fact]
    public void Order_follows_the_effective_set_then_member_order()
    {
        var a1 = Col("a1");
        var a2 = Col("a2");
        var b1 = Col("b1");
        var b2 = Col("b2");
        var first = Applied("first", b2, b1);
        var second = Applied("second", a2, a1);

        Assert.Equal([b2.Id, b1.Id, a2.Id, a1.Id], Inherit([first, second]).Select(m => m.MemberId));
        Assert.Equal([a2.Id, a1.Id, b2.Id, b1.Id], Inherit([second, first]).Select(m => m.MemberId));
    }

    [Fact]
    public void TryParse_splits_at_the_last_dot_only_before_an_id()
    {
        var member = Ids.NewId();
        var entity = Ids.NewId();
        var attribute = Ids.NewId();

        Assert.True(ContributedMembers.TryParse(Holder + "." + member, out var holder, out var id));
        Assert.Equal((Holder, member), (holder, id));
        // A child table's key holds a dot of its own; only the last one splits.
        var child = entity + "." + attribute + "@" + Ids.NewId();
        Assert.True(ContributedMembers.TryParse(child + "." + member, out holder, out id));
        Assert.Equal((child, member), (holder, id));
        // A designed table's keyed id, and a relation end's key column, split alike: the caller confirms the member.
        Assert.True(ContributedMembers.TryParse(entity + "." + attribute, out holder, out id));
        Assert.Equal((entity, attribute), (holder, id));

        Assert.False(ContributedMembers.TryParse(entity + ".position", out holder, out id));
        Assert.Equal(("", ""), (holder, id));
        Assert.False(ContributedMembers.TryParse(member, out _, out _));
        Assert.False(ContributedMembers.TryParse("." + member, out _, out _));
        Assert.False(ContributedMembers.TryParse(Holder + "." + member + ".", out _, out _));
    }

    [Fact]
    public void CheckLinks_reports_each_problem_at_its_entry()
    {
        var created = Ids.NewId();
        var updated = Ids.NewId();
        var stranger = Ids.NewId();
        var inheritable = new HashSet<string>([created, updated], StringComparer.Ordinal);

        var findings = ContributedMembers.CheckLinks(inheritable,
            [
                new TableRedefinition { Member = created, Nullable = true },
                new TableRedefinition { Member = stranger, Nullable = true },
                new TableRedefinition { Member = created, Comment = "again" },
                new TableRedefinition { Member = updated },
            ],
            [stranger, updated, created]);

        Assert.Equal(
            [
                new MemberLinkFinding(MemberLinkProblem.NotInherited, stranger, "/redefines/1/member"),
                new MemberLinkFinding(MemberLinkProblem.RedefinedTwice, created, "/redefines/2/member"),
                new MemberLinkFinding(MemberLinkProblem.NoFacet, updated, "/redefines/3/member"),
                new MemberLinkFinding(MemberLinkProblem.NotInherited, stranger, "/excludes/0"),
                new MemberLinkFinding(MemberLinkProblem.ExcludedAndRedefined, updated, "/excludes/1"),
                new MemberLinkFinding(MemberLinkProblem.ExcludedAndRedefined, created, "/excludes/2"),
            ],
            findings);
        Assert.Empty(ContributedMembers.CheckLinks(inheritable, [new TableRedefinition { Member = created, Nullable = true }], [updated]));
    }
}
