using Maquettiste.Engine.Model;
using Maquettiste.Engine.Revisions;
using Maquettiste.Engine.Tests.Loading;
using static Maquettiste.Engine.Tests.Revisions.RevisionFamily;

namespace Maquettiste.Engine.Tests.Revisions;

/// <summary>
/// The merged block per entity (revisions-design.md decision 9, section 8), over <see cref="RevisionFamily"/>: levels, separator and bound
/// attribute from the nearest statement; entries adding up through the bases, a nearer entry replacing a farther one in its place, and
/// <c>copy</c> and <c>restart</c> undoing a base's entry; <c>from</c> on each; the fallback filled in for every lifecycle that governs the
/// entity, in name order; a carried stereotype's attribute named by the carrier; ends the entity doesn't reach left out; and nothing for a
/// type with no block in its family, or a model with none.
/// </summary>
public sealed class RevisionCoverageTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static (string Lifecycle, LifecycleOnRevise OnRevise, string? State, string? From)[] Lifecycles(MergedRevisions merged) =>
        [.. merged.Lifecycles.Select(l => (l.Lifecycle.Id, l.OnRevise, l.State, l.From?.Id))];

    private static (string Attribute, AttributeOnRevise OnRevise, string From)[] Attributes(MergedRevisions merged) =>
        [.. merged.Attributes.Select(a => (a.Attribute, a.OnRevise, a.From.Id))];

    private static (string End, LinkOnRevise OnRevise, string From)[] Links(MergedRevisions merged) =>
        [.. merged.Links.Select(l => (l.End, l.OnRevise, l.From.Id))];

    [Fact]
    public async Task The_block_written_on_the_type_fills_in_the_fallback_for_every_governing_lifecycle()
    {
        var (h, store) = await OpenAsync();
        using var _ = h;
        await using var __ = store;
        var coverage = RevisionCoverage.Of(store.Current!);

        var part = coverage.For(store.Current!.Get<Entity>(Part)!)!;

        Assert.True(coverage.Any);
        Assert.Equal(Part, part.Entity.Id);
        Assert.Equal(["major", "minor"], part.Levels.Select(l => l.Name));
        Assert.Equal(Part, part.LevelsFrom!.Id);
        Assert.Equal(".", part.Separator); // the default, said by no block
        Assert.Null(part.SeparatorFrom);
        Assert.Equal((Revision, Part), (part.BoundAttribute, part.BoundAttributeFrom!.Id));
        // Approval and Delivery govern Part, by name: Approval says nothing, so it starts over (the fallback, from no one). Machining governs
        // only MadePart, so Part's entry for it doesn't apply here; Shipping is an orchestration.
        Assert.Equal([(Approval, LifecycleOnRevise.Restart, null, null), (Delivery, LifecycleOnRevise.Keep, null, Part)], Lifecycles(part));
        Assert.Equal([(ApprovedBy, AttributeOnRevise.Reset, Part), (Note, AttributeOnRevise.Clear, Part)], Attributes(part));
        // Part reaches documents; only MadePart reaches tools.
        Assert.Equal([(DocumentsEnd, LinkOnRevise.Duplicate, Part)], Links(part));
    }

    [Fact]
    public async Task A_derived_type_inherits_levels_and_entries_and_restates_only_what_differs()
    {
        var (h, store) = await OpenAsync();
        using var _ = h;
        await using var __ = store;
        var coverage = RevisionCoverage.Of(store.Current!);

        var purchased = coverage.For(PurchasedPart)!;

        // A block without levels takes the base's, with the separator and the bound attribute.
        Assert.Equal(["major", "minor"], purchased.Levels.Select(l => l.Name));
        Assert.Equal(Part, purchased.LevelsFrom!.Id);
        Assert.Equal((Revision, Part), (purchased.BoundAttribute, purchased.BoundAttributeFrom!.Id));
        // restart undoes the base's keep, said by PurchasedPart; Approval still falls back.
        Assert.Equal([(Approval, LifecycleOnRevise.Restart, null, null), (Delivery, LifecycleOnRevise.Restart, null, PurchasedPart)], Lifecycles(purchased));
        // copy undoes the base's clear of note, which leaves the list; its own entry adds after the base's.
        Assert.Equal([(ApprovedBy, AttributeOnRevise.Reset, Part), (SupplierQuote, AttributeOnRevise.Clear, PurchasedPart)], Attributes(purchased));
        Assert.Equal([(DocumentsEnd, LinkOnRevise.Duplicate, Part)], Links(purchased));
    }

    [Fact]
    public async Task A_derived_types_own_levels_separator_and_entries_replace_its_bases()
    {
        var (h, store) = await OpenAsync();
        using var _ = h;
        await using var __ = store;
        var coverage = RevisionCoverage.Of(store.Current!);

        var made = coverage.For(MadePart)!;

        var level = Assert.Single(made.Levels);
        Assert.Equal((RevisionFormat.Numbers, MadePart), (level.Format, made.LevelsFrom!.Id));
        Assert.Equal(("-", MadePart), (made.Separator, made.SeparatorFrom!.Id));
        Assert.Equal((Revision, Part), (made.BoundAttribute, made.BoundAttributeFrom!.Id));
        // Every governing lifecycle by name: its own entry names a state; the base's keeps; Machining governs it, so Part's entry applies; Tracking
        // governs it as a carrier of tracked and says nothing.
        Assert.Equal(
            [(Approval, LifecycleOnRevise.State, Approved, MadePart), (Delivery, LifecycleOnRevise.Keep, null, Part), (Machining, LifecycleOnRevise.Keep, null, Part),
             (Tracking, LifecycleOnRevise.Restart, null, null)],
            Lifecycles(made));
        // Its clear of approvedBy takes the place of the base's reset; the carried stereotype's trackingCode, named by the carrier, follows.
        Assert.Equal(
            [(ApprovedBy, AttributeOnRevise.Clear, MadePart), (Note, AttributeOnRevise.Clear, Part), (TrackingCode, AttributeOnRevise.Reset, MadePart)],
            Attributes(made));
        Assert.Equal([(DocumentsEnd, LinkOnRevise.Duplicate, Part), (ToolsEnd, LinkOnRevise.Move, Part)], Links(made));
    }

    [Fact]
    public async Task A_type_with_no_block_in_its_family_has_none_and_a_family_without_levels_has_empty_levels()
    {
        var (h, store) = await OpenAsync(h =>
            h.Write("model/entities/document.json", Entity(Document, "Document", null, "", $$"""{"attributes":[{"attribute":"{{Note}}","onRevise":"clear"}]}""")));
        using var _ = h;
        await using var __ = store;
        var coverage = RevisionCoverage.Of(store.Current!);

        // A base without a block has none, even when a derived type has one; so do unrelated types and an unknown id.
        Assert.Null(coverage.For(RecordType));
        Assert.Null(coverage.For(Tool));
        Assert.Null(coverage.For(store.Current!.Get<Entity>(Order)!));
        Assert.Null(coverage.For("01JENT00000000000000000999"));

        // A block whose family names no levels (MQ8301) still merges: no levels, the default separator, no bound attribute, no lifecycle.
        var document = coverage.For(Document)!;
        Assert.Empty(document.Levels);
        Assert.Null(document.LevelsFrom);
        Assert.Equal(".", document.Separator);
        Assert.Null(document.BoundAttribute);
        Assert.Empty(document.Lifecycles);
        Assert.Equal([(Note, AttributeOnRevise.Clear, Document)], Attributes(document));
        Assert.Empty(document.Links);
    }

    [Fact]
    public async Task A_model_without_a_block_has_no_coverage_and_the_coverage_is_built_once_per_snapshot()
    {
        using var h = new LoaderHarness();
        h.CopyFixture("models", "process-basics");
        await using var store = h.NewStore();
        await store.LoadAsync(Ct);
        var model = store.Current!;

        var coverage = RevisionCoverage.Of(model);

        Assert.False(coverage.Any);
        Assert.Null(coverage.For(model.Get<Entity>(Order)!));
        Assert.Same(coverage, RevisionCoverage.Of(model));

        var (family, familyStore) = await OpenAsync();
        using var _ = family;
        await using var __ = familyStore;
        Assert.Same(RevisionCoverage.Of(familyStore.Current!), RevisionCoverage.Of(familyStore.Current!));
        Assert.Same(RevisionCoverage.Of(familyStore.Current!).For(Part), RevisionCoverage.Of(familyStore.Current!).For(Part));
    }
}
