using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Resolution;
using Maquettiste.Engine.Tests.Rendering;
using Maquettiste.Engine.Tests.Resolution;
using Maquettiste.Engine.Validation;

namespace Maquettiste.Engine.Tests.Validation;

/// <summary>Where the checks after resolution put a finding (next-phase-2-design.md section 5.5), one case per rule.</summary>
public sealed class ResolvedLocationsTests
{
    private static async Task<(ResolvedModel Model, Maquettiste.Engine.Model.ModelSnapshot Snapshot)> Billing()
    {
        var model = await BillingModel.GetAsync();
        return (model, model.Source);
    }

    private static string FileOf(Maquettiste.Engine.Model.ModelSnapshot snapshot, string id) =>
        snapshot.GetDocument(snapshot.TryGetEntry(id, out var entry) ? entry.OwnerId : id)!.Path;

    [Fact]
    public async Task A_virtual_attribute_is_located_on_its_entity()
    {
        var (model, snapshot) = await Billing();
        var (entity, attribute) = model.Entities.SelectMany(e => e.Attributes.Select(a => (e, a)))
            .First(p => ResolvedKinds.SiteOf(p.a, snapshot) != p.a.Id);

        var at = ResolvedLocations.Of(attribute, snapshot);

        Assert.Equal(FileOf(snapshot, entity.Id), at.FilePath);
        Assert.True(at.Holder);
        var placed = ResolvedLocations.Place(new Diagnostic("x/r", DiagnosticSeverity.Warning, "m", attribute.Id, null, "/name", null, null), attribute, snapshot);
        Assert.Equal(attribute.Id, placed.ElementId); // the object's own id, so a suppression can name it
        Assert.Equal(ResolvedLocations.Of(entity, snapshot).JsonPointer, placed.JsonPointer); // the relative pointer is dropped
    }

    [Fact]
    public async Task An_id_of_the_index_is_located_on_its_owner_file_and_entry()
    {
        var (model, snapshot) = await Billing();
        var entity = model.Entity("Invoice");
        var attribute = entity.Attributes.First(a => ResolvedKinds.SiteOf(a, snapshot) == a.Id);
        snapshot.TryGetEntry(attribute.Id, out var entry);

        var at = ResolvedLocations.Of(attribute, snapshot);

        Assert.Equal(FileOf(snapshot, entity.Id), at.FilePath);
        Assert.Equal(entry!.JsonPointer, at.JsonPointer);
        Assert.False(at.Holder);
        var placed = ResolvedLocations.Place(new Diagnostic("x/r", DiagnosticSeverity.Warning, "m", attribute.Id, null, "/name", null, null), attribute, snapshot);
        Assert.Equal(entry.JsonPointer + "/name", placed.JsonPointer);
    }

    [Fact]
    public async Task A_synthesized_table_and_its_columns_are_located_on_the_overlay_file()
    {
        var (model, snapshot) = await Billing();
        var table = model.Databases.SelectMany(d => d.Tables).First(t => !snapshot.TryGetEntry(t.Id, out _) && t.FileId is not null);
        var column = table.Columns.First(c => !snapshot.TryGetEntry(c.Id, out _));

        Assert.Equal(FileOf(snapshot, table.FileId!), ResolvedLocations.Of(table, snapshot).FilePath);
        Assert.Equal(FileOf(snapshot, table.FileId!), ResolvedLocations.Of(column, snapshot).FilePath);
        Assert.True(ResolvedLocations.Of(column, snapshot).Holder);
    }

    [Fact]
    public async Task A_synthesized_table_without_an_overlay_is_located_on_its_entity()
    {
        var (model, snapshot) = await Billing();
        var table = model.Databases.SelectMany(d => d.Tables).First(t => !snapshot.TryGetEntry(t.Id, out _) && t.FileId is null && t.Entity is not null);

        var at = ResolvedLocations.Of(table, snapshot);

        Assert.Equal(FileOf(snapshot, table.Entity!.Id), at.FilePath);
        Assert.True(at.Holder);
    }

    [Fact]
    public async Task A_synthesized_key_is_located_on_the_id_it_starts_with()
    {
        var (model, snapshot) = await Billing();
        var table = model.Databases.SelectMany(d => d.Tables).First(t => t.Entity is not null && !snapshot.TryGetEntry(t.Id, out _));
        var sequence = new RSequence { Id = table.Entity!.Id + ".sequence@" + table.Database.Id };

        Assert.Equal(FileOf(snapshot, table.Entity.Id), ResolvedLocations.Of(sequence, snapshot).FilePath);
        Assert.Null(ResolvedLocations.Of(new RSequence { Id = "nowhere" }, snapshot).FilePath);
        var placed = ResolvedLocations.Place(new Diagnostic("x/r", DiagnosticSeverity.Warning, "m", null, null, "/x", null, null), new RSequence { Id = "nowhere" }, snapshot);
        Assert.Equal(("nowhere", (string?)null, (string?)null), (placed.ElementId, placed.FilePath, placed.JsonPointer));
    }
}
