using System.Collections.Immutable;
using Maquettiste.Engine.Model;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Model;

/// <summary>Reference groups cannot be written through the public API, and the index build honours parallelism and cancellation.</summary>
public sealed class ModelSnapshotIndexTests
{
    [Fact]
    public void Reference_groups_are_immutable_views_shared_with_patched_snapshots()
    {
        var (model, target) = Model();
        var references = model.ReferencesTo(target);
        Assert.NotEmpty(references);
        Assert.IsNotType<ReferenceInfo[]>(references, exactMatch: false);
        Assert.IsType<ImmutableArray<ReferenceInfo>>(references);
        Assert.Throws<NotSupportedException>(() => ((IList<ReferenceInfo>)references)[0] = references[0]);
        var from = model.ReferencesFrom(references[0].FromElementId);
        Assert.IsType<ImmutableArray<ReferenceInfo>>(from);
        Assert.Empty(model.ReferencesTo("no-such-id"));
        Assert.Empty(model.ReferencesFrom("no-such-id"));
    }

    [Fact]
    public void The_index_is_the_same_at_any_parallelism()
    {
        var (model, _) = Model();
        var ct = TestContext.Current.CancellationToken;
        foreach (var parallelism in new[] { 1, 2, 7 })
        {
            var built = ModelSnapshot.CreateAfter(null, model.Documents, model.Settings, model.SettingsHash, [], [], 1, null, parallelism, ct);
            Assert.Equal(model.Summaries(), built.Summaries());
            foreach (var document in model.Documents)
            {
                Assert.Equal(model.ReferencesFrom(document.Element.Id), built.ReferencesFrom(document.Element.Id));
                Assert.Equal(model.ReferencesTo(document.Element.Id), built.ReferencesTo(document.Element.Id));
            }
        }
    }

    [Fact]
    public void A_cancelled_index_build_throws()
    {
        var (model, _) = Model();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() =>
            ModelSnapshot.CreateAfter(null, model.Documents, model.Settings, model.SettingsHash, [], [], 1, null, 4, cancelled.Token));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ModelSnapshot.CreateAfter(null, model.Documents, model.Settings, model.SettingsHash, [], [], 1, null, 0, CancellationToken.None));
    }

    [Fact]
    public void Xref_values_are_indexed_with_schema_and_optional()
    {
        // next-phase-1-design.md section 4.1: each id at an x-ref site of a holder's own properties is a reference from the holder,
        // with the top-level property as its field; a stereotype's defaults are walked against the extensions naming its key.
        var b = new ModelBuilder(12);
        var customer = b.Entity("Customer").Key("id", "uuid");
        var audited = b.Stereotype("audited").DefaultProperty("approver", customer.Id);
        var order = b.Entity("Order").Key("id", "uuid").Stereotype("audited")
            .Attr("label", "string", a => a.Property("subject", customer.Id))
            .Property("owner", customer.Id)
            .Property("reviewers", new[] { customer.Id, "Bob" })
            .Property("note", customer.Id)
            .Property("keyed", customer.Id + "." + customer.AttrId("id"));
        var extensions = new[]
        {
            PropertySchemaSetTests.Extension("""
                { "keyed": { "type": "string", "x-ref": { "kind": "attribute" } }, "note": { "type": "string" },
                  "owner": { "type": "string", "x-ref": { "kind": "entity" } },
                  "reviewers": { "type": "array", "items": { "type": "string", "x-ref": { "kind": "entity", "optional": true } } } }
                """, "links", path: ".maquettiste/extensions/a-links.json"),
            PropertySchemaSetTests.Extension("""{ "owner": { "type": "string", "x-ref": { "kind": "entity", "optional": true } } }""", "more",
                path: ".maquettiste/extensions/b-more.json"),
            PropertySchemaSetTests.Extension("""{ "subject": { "type": "string", "x-ref": { "kind": "entity", "optional": true } } }""", "labels",
                ["attribute"], path: ".maquettiste/extensions/c-labels.json"),
            PropertySchemaSetTests.Extension("""{ "approver": { "type": "string", "x-ref": { "kind": "entity" } } }""", "audit", [], ["audited"],
                ".maquettiste/extensions/d-audit.json"),
        };

        var model = ModelSnapshot.Create(b.BuildDocuments(), new ProjectSettings { FormatVersion = EngineVersion.FormatVersion }, "", extensions, [], 1);

        var typed = model.ReferencesTo(customer.Id).Where(r => r.Schema is not null).ToList();
        Assert.Equal(
            [
                // One value two extensions type is one reference: the first schema, optional only when both say so; text is data.
                new ReferenceInfo(order.Id, order.Id, "/properties/keyed", "keyed", customer.Id, false, false, "extension:links"),
                new ReferenceInfo(order.Id, order.Id, "/properties/owner", "owner", customer.Id, false, false, "extension:links"),
                new ReferenceInfo(order.Id, order.Id, "/properties/reviewers/0", "reviewers", customer.Id, false, true, "extension:links"),
                new ReferenceInfo(order.Id, order.AttrId("label"), "/attributes/1/properties/subject", "subject", customer.Id, false, true, "extension:labels"),
            ],
            typed.Where(r => r.FromElementId == order.Id));
        Assert.Equal(
            [new ReferenceInfo(audited.Id, audited.Id, "/defaultProperties/approver", "approver", customer.Id, false, false, "extension:audit")],
            typed.Where(r => r.FromElementId == audited.Id));
        Assert.Equal(5, typed.Count);
        // The keyed value names the attribute too; the untyped note and the name in reviewers are no references.
        Assert.Contains(model.ReferencesTo(customer.AttrId("id")), r => r.JsonPointer == "/properties/keyed" && r.Schema == "extension:links");
        Assert.DoesNotContain(model.ReferencesFrom(order.Id), r => r.JsonPointer is "/properties/note" or "/properties/reviewers/1");
        Assert.DoesNotContain(model.ReferencesFrom(order.Id), r => r.Schema is null && r.JsonPointer.Contains("/properties/", StringComparison.Ordinal));
    }

    private static (ModelSnapshot Model, string Target) Model()
    {
        var b = new ModelBuilder(11);
        var sales = b.Package("Sales");
        var customer = b.Entity("Customer", sales).Key("id", "uuid").Attr("name", "string");
        var order = b.Entity("Order", sales).Key("id", "uuid");
        b.Relation("placed", customer, order);
        return (b.Build(), customer.Id);
    }
}
