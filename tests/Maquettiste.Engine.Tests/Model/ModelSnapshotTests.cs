using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Hashing;
using Maquettiste.Engine.Model;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Model;

public sealed class ModelSnapshotTests
{
    private sealed record Sample(ModelSnapshot Model, EntityBuilder Customer, EntityBuilder Invoice, RelationBuilder Places, EnumBuilder Status, PackageBuilder Billing);

    private static Sample Build()
    {
        var b = new ModelBuilder(seed: 1);
        var billing = b.Package("Billing");
        var status = b.Enum("InvoiceStatus", billing).Member("Draft", 0).Member("Issued", 1);
        var customer = b.Entity("Customer", billing).Key("id", "uuid", IdentityStrategy.UuidV7).Attr("name", "string", a => a.Length(120).Required());
        var invoice = b.Entity("Invoice", billing).Stereotype("audited").Key("id", "uuid").Attr("status", status, a => a.Required());
        var places = b.Relation("places", customer, invoice, fromMax: MaxCardinality.One, toMax: MaxCardinality.Many, fromRole: "customer", toNavigation: "invoices");
        b.Stereotype("audited").Attr("createdAt", "datetimeoffset", a => a.Required());
        var main = b.Database("main", Dialect.PostgreSql);
        b.Mapping(main, invoice).Storage("status", StorageKind.String);
        return new Sample(b.Build(), customer, invoice, places, status, billing);
    }

    [Fact]
    public void Indexes_elements_and_sub_elements()
    {
        var s = Build();

        Assert.True(s.Model.TryGetEntry(s.Invoice.Id, out var entity));
        Assert.Equal(new IndexEntry(s.Invoice.Id, s.Invoice.Id, "entity", ""), entity);

        Assert.True(s.Model.TryGetEntry(s.Invoice.AttrId("status"), out var attribute));
        Assert.Equal("attribute", attribute.Kind);
        Assert.Equal(s.Invoice.Id, attribute.OwnerId);
        Assert.Equal("/attributes/1", attribute.JsonPointer);

        Assert.True(s.Model.TryGetEntry(s.Places.EndIds[1], out var end));
        Assert.Equal(new IndexEntry(s.Places.EndIds[1], s.Places.Id, "end", "/ends/1"), end);

        Assert.True(s.Model.TryGetEntry(s.Status.MemberId("Issued"), out var member));
        Assert.Equal("enum-member", member.Kind);

        Assert.Same(s.Model.GetDocument(s.Invoice.Id), s.Model.GetDocument(s.Invoice.AttrId("status")));
        Assert.Null(s.Model.GetDocument("01ZZZZZZZZZZZZZZZZZZZZZZZZ"));
    }

    [Fact]
    public void Builds_reverse_references_from_element_ref_attributes()
    {
        var s = Build();

        var toCustomer = s.Model.ReferencesTo(s.Customer.Id);
        Assert.Contains(toCustomer, r => r.FromElementId == s.Places.Id && r.FromId == s.Places.EndIds[0] && r.JsonPointer == "/ends/0/entity" && r.Field == "entity");

        var toStatusEnum = s.Model.ReferencesTo(s.Status.Id);
        Assert.Contains(toStatusEnum, r => r.FromElementId == s.Invoice.Id && r.FromId == s.Invoice.AttrId("status") && r.JsonPointer == "/attributes/1/type/ref");

        var toKeyAttribute = s.Model.ReferencesTo(s.Invoice.AttrId("id"));
        Assert.Contains(toKeyAttribute, r => r.JsonPointer == "/key/attributes/0");

        var toPackage = s.Model.ReferencesTo(s.Billing.Id);
        Assert.Equal(3, toPackage.Count); // the two entities and the enum
        Assert.All(toPackage, r => Assert.Equal("package", r.Field));

        var fromMapping = s.Model.ReferencesFrom(s.Model.All<Mapping>().Single().Id);
        Assert.Contains(fromMapping, r => r.Field == "attribute" && r.ToId == s.Invoice.AttrId("status"));
        Assert.Contains(fromMapping, r => r.Field == "database");
    }

    [Fact]
    public void Lists_elements_by_kind_ordinal_by_name_then_id()
    {
        var s = Build();

        Assert.Equal(["Customer", "Invoice"], s.Model.All<Entity>().Select(e => e.Name));
        Assert.Equal(s.Invoice.Id, s.Model.Get<Entity>(s.Invoice.Id)?.Id);
        Assert.Null(s.Model.Get<Relation>(s.Invoice.Id));
        Assert.Null(s.Model.Get<Entity>(s.Invoice.AttrId("id")));
        Assert.Equal(s.Model.Documents.Count, s.Model.All<Element>().Count);
        Assert.NotNull(s.Model.GetStereotype("audited"));
        Assert.Null(s.Model.GetStereotype("versioned"));
    }

    [Fact]
    public void Documents_and_summaries_are_ordinal_by_path()
    {
        var s = Build();

        var paths = s.Model.Documents.Select(d => d.Path).ToList();
        Assert.Equal(paths.Order(StringComparer.Ordinal), paths);
        Assert.Contains(".maquettiste/model/entities/invoice.json", paths);
        Assert.Contains(".maquettiste/model/databases/main/database.json", paths);
        Assert.Contains(".maquettiste/model/vocabularies/stereotypes/audited.json", paths);

        var summary = s.Model.Summaries().Single(x => x.Id == s.Invoice.Id);
        Assert.Equal("entity", summary.Kind);
        Assert.Equal(s.Billing.Id, summary.Package);
        Assert.Equal(s.Model.GetDocument(s.Invoice.Id)!.Hash, summary.Hash);
        Assert.True(ContentHash.IsValid(summary.Hash));
    }

    [Fact]
    public void Kind_set_hash_depends_only_on_the_ids_of_the_kind()
    {
        var a = Build().Model;
        var b = Build().Model;

        Assert.Equal(a.KindSetHash(ElementKind.Entity), b.KindSetHash(ElementKind.Entity));
        Assert.NotEqual(a.KindSetHash(ElementKind.Entity), a.KindSetHash(ElementKind.Relation));
        Assert.Equal(HashBuilder.Of(), a.KindSetHash(ElementKind.View));

        var more = new ModelBuilder(seed: 1);
        more.Entity("Other");
        Assert.NotEqual(a.KindSetHash(ElementKind.Entity), more.Build().KindSetHash(ElementKind.Entity));
    }

    [Fact]
    public void Duplicate_ids_keep_the_ordinally_first_path()
    {
        var first = new Package { Id = "01JAX3JZ0H6N2P5R8S1T4V7W9Y", Name = "Alpha" };
        var second = new Package { Id = "01JAX3JZ0H6N2P5R8S1T4V7W9Y", Name = "Beta" };
        var docs = new[]
        {
            new ElementDocument(second, ".maquettiste/model/packages/beta.json", "h2", "d2", default, null),
            new ElementDocument(first, ".maquettiste/model/packages/alpha.json", "h1", "d1", default, null),
        };

        var model = ModelSnapshot.Create(docs, new ProjectSettings { FormatVersion = 1 }, "s", [], [], 1);

        Assert.Equal("Alpha", model.Get<Package>(first.Id)!.Name);
        Assert.Single(model.All<Package>());
        Assert.Equal(2, model.Documents.Count);
    }
    [Fact]
    public void Physical_keys_are_indexed_by_their_id_segments()
    {
        var b = new ModelBuilder(seed: 3);
        var invoice = b.Entity("Invoice").Key("id", "uuid").Attr("number", "string");
        var main = b.Database("main", Dialect.PostgreSql);
        var customer = b.Table("customer", main).Column("id", "uuid", nullable: false).PrimaryKey("id");
        var order = b.Table("order", main).Column("id", "uuid").Column("customer_id", "uuid").ForeignKey(customer, "customer_id").Index(false, "customer_id");
        var overlay = b.Table("invoice", main).OverlayFor(invoice).Overlay(invoice.AttrId("number"), name: "invoice_no");
        var keyed = new Table
        {
            Id = b.NewId(), Name = "audit", Database = main.Id,
            Columns = [new Column { Id = b.NewId(), Name = "invoice_id", Type = "uuid" }],
            ForeignKeys = [new ForeignKey { Id = b.NewId(), Columns = ["x"], ReferencesTable = invoice.Id + "@" + main.Id, ReferencesColumns = [invoice.AttrId("id")] }],
        };
        b.Add(keyed);
        var model = b.Build();

        // A designed table's foreign key that targets another table is a referrer of that table (deleting it reports the FK).
        Assert.Contains(model.ReferencesTo(customer.Id), r => r.FromElementId == order.Id && r.Field == "referencesTable" && r.JsonPointer == "/foreignKeys/0/referencesTable");
        // Constraint and index column lists refer to the table's own column ids.
        Assert.Contains(model.ReferencesTo(order.ColumnId("customer_id")), r => r.JsonPointer == "/indexes/0/columns/0/column");
        Assert.Contains(model.ReferencesTo(customer.ColumnId("id")), r => r.JsonPointer == "/primaryKey/columns/0");
        // An overlay column's attribute key refers to the attribute it overrides.
        Assert.Contains(model.ReferencesTo(invoice.AttrId("number")), r => r.FromElementId == overlay.Id && r.Field == "attribute");
        // A synthesized table key <entityId>@<databaseId> refers to both ids; non-id segments are no reference.
        Assert.Contains(model.ReferencesTo(invoice.Id), r => r.FromElementId == keyed.Id && r.JsonPointer == "/foreignKeys/0/referencesTable");
        Assert.Contains(model.ReferencesTo(main.Id), r => r.FromElementId == keyed.Id && r.JsonPointer == "/foreignKeys/0/referencesTable");
        Assert.DoesNotContain(model.ReferencesFrom(keyed.Id), r => r.ToId == "x");
    }

    [Fact]
    public void Stereotype_keys_are_indexed_as_references_to_the_stereotype_id()
    {
        var s = Build();
        var audited = s.Model.GetStereotype("audited")!;

        var references = s.Model.ReferencesTo(audited.Id);

        Assert.Contains(references, r => r.FromElementId == s.Invoice.Id && r.JsonPointer == "/stereotypes/0" && r.Field == "stereotypes");
        Assert.Equal("audited", audited.Key);
    }

    [Fact]
    public void A_second_tag_vocabulary_or_category_tree_is_reported()
    {
        var b = new ModelBuilder(seed: 4);
        b.Tags(false, "billing");
        b.Add(new TagVocabulary { Id = b.NewId(), Name = "more-tags" });
        b.Category("Receivables");
        b.Add(new CategoryTree { Id = b.NewId(), Name = "more-categories" });

        var model = b.Build();

        var duplicates = model.LoadDiagnostics.Where(d => d.Rule == "MQ1009").ToList();
        Assert.Equal(2, duplicates.Count);
        Assert.All(duplicates, d => Assert.Equal(DiagnosticSeverity.Error, d.Severity));
        Assert.Contains(duplicates, d => d.Message.Contains("tag-vocabulary", StringComparison.Ordinal));
        Assert.NotNull(model.Tags);
        Assert.NotNull(model.Categories);
    }

    [Fact]
    public void Referrers_hash_changes_when_a_file_starts_referencing_an_id_but_not_for_diagrams()
    {
        static (ModelBuilder Builder, EntityBuilder Invoice, DatabaseBuilder Main) Sample()
        {
            var b = new ModelBuilder(seed: 5);
            var invoice = b.Entity("Invoice").Key("id", "uuid").Attr("number", "string");
            var main = b.Database("main", Dialect.PostgreSql);
            return (b, invoice, main);
        }

        var (plain, invoice, _) = Sample();
        var before = plain.Build().ReferrersHash(invoice.Id);

        var (withMapping, invoice2, main2) = Sample();
        withMapping.Mapping(main2, invoice2).Storage("number", StorageKind.Json);
        var (withOverlay, invoice3, main3) = Sample();
        withOverlay.Table("invoice", main3).OverlayFor(invoice3);
        var (withDiagram, invoice4, _) = Sample();
        withDiagram.Add(new Diagram { Id = withDiagram.NewId(), Name = "overview", Members = [new DiagramMember { Element = invoice4.Id, X = 10 }] });

        Assert.Equal(invoice.Id, invoice2.Id);
        Assert.NotEqual(before, withMapping.Build().ReferrersHash(invoice.Id));
        Assert.NotEqual(before, withOverlay.Build().ReferrersHash(invoice.Id));
        Assert.Equal(before, withDiagram.Build().ReferrersHash(invoice.Id));
        Assert.Equal(before, plain.Build().ReferrersHash(invoice.Id));
    }
}
