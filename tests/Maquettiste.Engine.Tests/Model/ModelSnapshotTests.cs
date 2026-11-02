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
    public void A_column_typeRef_is_indexed_as_a_type_reference()
    {
        // next-phase-2-design.md section 3.2 (D20): Column.TypeRef has type TypeRef, so the index walks it as a type reference held by
        // the column, as it walks an attribute's type.
        var b = new ModelBuilder(seed: 9);
        var state = b.Enum("InvoiceState").Member("Draft", code: "D");
        var money = b.ScalarType("Money", "decimal");
        var main = b.Database("main", Dialect.PostgreSql);
        var events = b.Table("invoice_events", main).Column("status", "string", length: 1).Column("amount", "decimal").Column("note", "text")
            .Edit(t => t with
            {
                Columns =
                [
                    t.Columns[0] with { TypeRef = new TypeRef { Ref = state.Id } },
                    t.Columns[1] with { TypeRef = new TypeRef { Ref = money.Id } },
                    t.Columns[2],
                ],
            });
        var model = b.Build();

        var toState = Assert.Single(model.ReferencesTo(state.Id));
        Assert.Equal((events.Id, events.ColumnId("status"), "/columns/0/typeRef/ref", "typeRef"), (toState.FromElementId, toState.FromId, toState.JsonPointer, toState.Field));
        var toMoney = Assert.Single(model.ReferencesTo(money.Id));
        Assert.Equal((events.ColumnId("amount"), "/columns/1/typeRef/ref"), (toMoney.FromId, toMoney.JsonPointer));
        Assert.DoesNotContain(model.ReferencesFrom(events.Id), r => r.FromId == events.ColumnId("note"));
    }

    [Fact]
    public void A_primary_key_with_an_id_is_a_key_sub_element()
    {
        // next-phase-2-design.md section 3.1 (D1): a primary key may carry an id, which makes it a key sub-element as a unique
        // constraint is; one without an id stays a plain part of its table.
        var b = new ModelBuilder(seed: 7);
        var main = b.Database("main", Dialect.PostgreSql);
        var keyId = b.NewId();
        var keyed = b.Table("keyed", main).Column("id", "uuid", nullable: false).Edit(t => t with { PrimaryKey = new PrimaryKey { Id = keyId, Columns = [t.Columns[0].Id] } });
        var plain = b.Table("plain", main).Column("id", "uuid", nullable: false).PrimaryKey("id");
        var model = b.Build();

        Assert.True(model.TryGetEntry(keyId, out var entry));
        Assert.Equal(new IndexEntry(keyId, keyed.Id, "key", "/primaryKey"), entry);
        Assert.Same(model.GetDocument(keyed.Id), model.GetDocument(keyId));
        // The key's column list is a reference held by the key.
        Assert.Contains(model.ReferencesTo(keyed.ColumnId("id")), r => r.FromId == keyId && r.JsonPointer == "/primaryKey/columns/0");
        Assert.DoesNotContain(model.ReferencesFrom(plain.Id), r => r.FromId != plain.Id && r.JsonPointer.StartsWith("/primaryKey", StringComparison.Ordinal));
        Assert.Equal(3, model.Summaries().Count); // the database and the two tables: a key is no element of its own
    }

    [Fact]
    public void Provenance_ids_are_not_references()
    {
        // next-phase-2-design.md section 3.3 (D30, decision 2): a binding's provenance is keyed by column ids, but it is a record, not a
        // link: the index lists the binding's source and fields as references and none of the provenance keys.
        var b = new ModelBuilder(seed: 11);
        var main = b.Database("main", Dialect.PostgreSql);
        var notes = b.Table("notes", main).Column("id", "uuid", nullable: false).Column("body", "text").Column("spare", "text").PrimaryKey("id");
        var note = b.Entity("Note").Key("id", "uuid");
        note.Bind(new EntityBinding
        {
            Id = "",
            Database = main.Id,
            Source = notes.Id,
            Fields = [new BindingField { Attribute = note.AttrId("id"), Column = notes.ColumnId("id") }],
            Provenance = new BindingProvenance
            {
                Columns = new Dictionary<string, string>
                {
                    [notes.ColumnId("id")] = "00000000:00000000",
                    [notes.ColumnId("spare")] = "11111111:11111111",
                    [notes.Id + "." + notes.ColumnId("body")] = "22222222:22222222",
                },
            },
        });
        var model = b.Build();

        var from = model.ReferencesFrom(note.Id);
        Assert.DoesNotContain(from, r => r.JsonPointer.Contains("/provenance", StringComparison.Ordinal));
        Assert.Empty(model.ReferencesTo(notes.ColumnId("spare")));
        Assert.Single(model.ReferencesTo(notes.ColumnId("id")), r => r.FromElementId == note.Id);
        Assert.Contains(from, r => r.ToId == notes.Id && r.JsonPointer == "/bindings/0/source");
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
    public void Requires_conflicts_and_implies_are_indexed_as_references()
    {
        var b = new ModelBuilder(seed: 6);
        var audited = b.Stereotype("audited");
        var softDelete = b.Stereotype("soft-delete").Group("deletion");
        var hardDelete = b.Stereotype("hard-delete").Group("deletion");
        var tracked = b.Stereotype("tracked").Requires("audited").Conflicts("hard-delete", "missing").Implies("soft-delete");
        b.Entity("Invoice").Key("id", "uuid").Stereotype("tracked");

        var model = b.Build();

        Assert.Equal(
            [
                (hardDelete.Id, "/conflicts/0", "conflicts"),
                (softDelete.Id, "/implies/0", "implies"),
                (audited.Id, "/requires/0", "requires"),
            ],
            model.ReferencesFrom(tracked.Id).Select(r => (r.ToId, r.JsonPointer, r.Field)).OrderBy(r => r.JsonPointer, StringComparer.Ordinal));
        // An unknown key is no reference (validation reports it); the group is a label, not a link.
        Assert.DoesNotContain(model.ReferencesFrom(tracked.Id), r => r.JsonPointer == "/conflicts/1");
        Assert.Empty(model.ReferencesFrom(softDelete.Id));
        // The referrers keep a named stereotype from a plain delete.
        Assert.Contains(model.ReferencesTo(softDelete.Id), r => r.FromElementId == tracked.Id);
        // The effective set: implied first (at the implying entry), then own.
        Assert.Equal(
            [("soft-delete", 0, "tracked"), ("tracked", 0, null)],
            model.EffectiveStereotypes(["tracked", "missing"]).Select(s => (s.Stereotype.Key, s.Entry, s.ImpliedBy)));
        Assert.Equal(
            [("tracked", 0, null), ("soft-delete", 1, null)],
            model.EffectiveStereotypes(["tracked", "soft-delete"]).Select(s => (s.Stereotype.Key, s.Entry, s.ImpliedBy)));
    }

    /// <summary>
    /// A model for the default-stereotype cases (next-phase-2-design.md section 3.1, D2): database main with schemas sales (the default
    /// schema) and archive, package Billing, a designed table and a view in sales, both in Billing, and the stereotypes the cases list.
    /// </summary>
    private static (ModelSnapshot Model, Table Table, View View, string Sales, string Billing) Defaults(
        Action<ModelBuilder> stereotypes, string[] schemaDefaults, string[] packageDefaults, string[] own, string[]? excludes = null)
    {
        var b = new ModelBuilder(seed: 21);
        stereotypes(b);
        var billing = b.Package("Billing");
        var main = b.Database("main", Dialect.PostgreSql).DefaultSchema("sales");
        var sales = main.Schema("sales");
        main.Schema("archive");
        var table = b.Table("ledger", main).Column("id", "uuid");
        foreach (var key in own)
            table.Stereotype(key);
        table.Edit(t => t with { Package = billing.Id, Schema = sales, ExcludeStereotypes = excludes });
        var viewId = b.NewId();
        b.Add(new View { Id = viewId, Name = "open_ledger", Database = main.Id, Schema = sales, Package = billing.Id, Body = new Dictionary<string, string> { ["*"] = "SELECT 1" } });
        var model = StereotypeDefaultsTests.Rebuild(b, e => e is Package p && p.Id == billing.Id ? p with { DefaultStereotypes = packageDefaults }
            : StereotypeDefaultsTests.SchemaDefaults(e, sales, schemaDefaults));
        return (model, model.Get<Table>(table.Id)!, model.Get<View>(viewId)!, sales, billing.Id);
    }

    [Fact]
    public void Schema_and_package_defaults_join_the_effective_set_in_layers()
    {
        var (model, table, _, sales, billing) = Defaults(b =>
        {
            b.Stereotype("audited");
            b.Stereotype("tracked");
            b.Stereotype("owned").Implies("tracked");
            b.Stereotype("partitioned");
        }, ["audited"], ["owned"], ["partitioned"]);

        var holder = model.HolderOf(table);
        Assert.Equal((sales, billing), (holder.Schema, holder.Package));
        var set = model.EffectiveStereotypes(table);
        // The schema's layer, then the package's (implied first, then the entry), then the own list.
        Assert.Equal(
            [
                ("audited", StereotypeSource.SchemaDefault, sales), ("tracked", StereotypeSource.PackageDefault, billing),
                ("owned", StereotypeSource.PackageDefault, billing), ("partitioned", StereotypeSource.Own, null),
            ],
            StereotypeDefaultsTests.Sources(set));
        Assert.Equal([(-1, (string?)null), (-1, "owned"), (-1, null), (0, null)], set.Select(s => (s.Entry, s.ImpliedBy)));
        Assert.Equal(set.Select(s => s.Stereotype.Key), model.EffectiveStereotypes(holder).Select(s => s.Stereotype.Key));
        // The key-list form stays the file's own list.
        Assert.Equal(["partitioned"], model.EffectiveStereotypes(table.Stereotypes).Select(s => s.Stereotype.Key));
    }

    [Fact]
    public void Exclude_stereotypes_removes_a_default_and_its_implied_ones()
    {
        void Stereotypes(ModelBuilder b)
        {
            b.Stereotype("tracked");
            b.Stereotype("logged");
            b.Stereotype("owned").Implies("tracked", "logged");
            b.Stereotype("versioned").Implies("tracked");
            b.Stereotype("audited");
        }

        // The excluded default leaves with the copies it implies; a copy another default still implies stays, implied by it.
        var (model, table, _, _, billing) = Defaults(Stereotypes, ["audited"], ["owned", "versioned"], [], ["owned"]);
        Assert.Equal(["owned"], model.HolderOf(table).Excludes);
        Assert.Equal(
            [("audited", StereotypeSource.SchemaDefault), ("tracked", StereotypeSource.PackageDefault), ("versioned", StereotypeSource.PackageDefault)],
            model.EffectiveStereotypes(table).Select(s => (s.Stereotype.Key, s.Source)));
        Assert.Equal("versioned", model.EffectiveStereotypes(table)[1].ImpliedBy);
        Assert.Equal(billing, model.EffectiveStereotypes(table)[1].DefaultHolder);

        // A stereotype a default implies can be excluded on its own; an own stereotype cannot (MQ2029 reports it), so it stays.
        (model, table, _, _, _) = Defaults(Stereotypes, ["audited"], ["owned"], ["audited"], ["tracked", "audited"]);
        Assert.Equal(["logged", "owned", "audited"], model.EffectiveStereotypes(table).Select(s => s.Stereotype.Key));
        Assert.Equal(StereotypeSource.Own, model.EffectiveStereotypes(table)[2].Source);
    }

    [Fact]
    public void A_default_skips_kinds_it_does_not_apply_to()
    {
        var (model, table, view, sales, billing) = Defaults(b =>
        {
            b.Stereotype("audited").AppliesTo("table");
            b.Stereotype("table-only").AppliesTo("table");
            b.Stereotype("owned").Implies("table-only");
            b.Stereotype("entity-only").AppliesTo("entity");
        }, ["audited"], ["owned", "entity-only"], []);

        Assert.Equal(
            [("audited", StereotypeSource.SchemaDefault, sales), ("table-only", StereotypeSource.PackageDefault, billing), ("owned", StereotypeSource.PackageDefault, billing)],
            StereotypeDefaultsTests.Sources(model.EffectiveStereotypes(table)));
        // The view takes neither the table-only default nor the table-only stereotype a default implies.
        Assert.Equal([("owned", StereotypeSource.PackageDefault, billing)], StereotypeDefaultsTests.Sources(model.EffectiveStereotypes(view)));
        // A schema reaches physical objects only; a package reaches every kind with a package, here the database (in no package) none.
        Assert.Null(model.HolderOf(model.All<Database>().Single()).Schema);
        Assert.Empty(model.EffectiveStereotypes(model.All<Database>().Single()));
    }

    [Fact]
    public void A_later_layer_keeps_the_last_place_of_a_key()
    {
        void Stereotypes(ModelBuilder b)
        {
            b.Stereotype("audited");
            b.Stereotype("owned");
            b.Stereotype("rooted").Implies("audited");
        }

        // audited is listed by the schema and the package: the package's place wins; owned is listed by the schema and own: own wins.
        var (model, table, _, _, billing) = Defaults(Stereotypes, ["audited", "owned"], ["audited"], ["owned"]);
        Assert.Equal(
            [("audited", StereotypeSource.PackageDefault, billing), ("owned", StereotypeSource.Own, null)],
            StereotypeDefaultsTests.Sources(model.EffectiveStereotypes(table)));

        // An own entry's implied copy is a later layer than every default.
        (model, table, _, _, _) = Defaults(Stereotypes, ["audited"], [], ["rooted"]);
        Assert.Equal(
            [("audited", StereotypeSource.Implied, "rooted"), ("rooted", StereotypeSource.Own, null)],
            model.EffectiveStereotypes(table).Select(s => (s.Stereotype.Key, s.Source, s.ImpliedBy)));
    }

    [Fact]
    public void Without_defaults_the_set_is_unchanged()
    {
        var b = new ModelBuilder(seed: 22);
        b.Stereotype("audited");
        b.Stereotype("soft-delete");
        b.Stereotype("tracked").Implies("soft-delete");
        var billing = b.Package("Billing");
        b.Entity("Invoice", billing).Key("id", "uuid").Stereotype("tracked").Stereotype("audited");
        var main = b.Database("main", Dialect.PostgreSql);
        main.Schema("billing");
        b.Table("ledger", main).Column("id", "uuid").Stereotype("audited").Edit(t => t with { ExcludeStereotypes = ["audited", "tracked"] });
        var model = b.Build();

        Assert.False(model.StereotypeDefaults.HasDefaults);
        foreach (var element in model.All<Element>())
        {
            Assert.Equal(
                model.EffectiveStereotypes(element.Stereotypes).Select(s => (s.Stereotype.Key, s.Entry, s.ImpliedBy, s.Source, s.DefaultHolder)),
                model.EffectiveStereotypes(element).Select(s => (s.Stereotype.Key, s.Entry, s.ImpliedBy, s.Source, s.DefaultHolder)));
        }

        // The phase 1 order, with the implied copy marked as implied.
        var invoice = model.All<Entity>().Single();
        Assert.Equal(
            [("soft-delete", 0, "tracked", StereotypeSource.Implied), ("tracked", 0, null, StereotypeSource.Own), ("audited", 1, null, StereotypeSource.Own)],
            model.EffectiveStereotypes(invoice).Select(s => (s.Stereotype.Key, s.Entry, s.ImpliedBy, s.Source)));
    }

    [Fact]
    public void Redefines_and_excludes_are_references_to_stereotype_members()
    {
        // next-phase-2-design.md section 3.1 (D6): a stereotype's members are sub-elements of the stereotype (a column, and a key for a
        // primary key, unique, foreign key, index or check), and a table's redefines and excludes entries reference them by member id.
        var b = new ModelBuilder(seed: 23);
        var main = b.Database("main", Dialect.PostgreSql);
        string created = b.NewId(), updated = b.NewId(), keyId = b.NewId(), foreignKey = b.NewId(), index = b.NewId();
        var parent = b.Table("parents", main).Column("id", "uuid", nullable: false).PrimaryKey("id");
        var audited = new Stereotype
        {
            Id = b.NewId(), Key = "audited", Name = "Audited",
            Columns = [new Column { Id = created, Name = "created_at", Type = "datetimeoffset" }, new Column { Id = updated, Name = "updated_at", Type = "datetimeoffset" }],
            PrimaryKey = new PrimaryKey { Id = keyId, Columns = [created] },
            ForeignKeys = [new MemberForeignKey { Id = foreignKey, Columns = [updated], ReferencesTable = parent.Id }],
            Indexes = [new TableIndex { Id = index, Columns = [new IndexColumn { Column = created }] }],
        };
        b.Add(audited);
        var notes = b.Table("notes", main).Column("body", "text").Stereotype("audited").Edit(t => t with
        {
            Redefines = [new TableRedefinition { Member = created, Nullable = true }, new TableRedefinition { Member = keyId, Name = "pk_notes_audit" }],
            Excludes = [updated, foreignKey],
        });
        var model = b.Build();

        Assert.Equal(new IndexEntry(created, audited.Id, "column", "/columns/0"), Entry(created));
        Assert.Equal(new IndexEntry(keyId, audited.Id, "key", "/primaryKey"), Entry(keyId));
        Assert.Equal(new IndexEntry(foreignKey, audited.Id, "key", "/foreignKeys/0"), Entry(foreignKey));
        Assert.Equal(new IndexEntry(index, audited.Id, "key", "/indexes/0"), Entry(index));
        // A member's column list references the stereotype's own column, held by the member.
        Assert.Contains(model.ReferencesTo(created), r => r.FromElementId == audited.Id && r.FromId == index && r.JsonPointer == "/indexes/0/columns/0/column");
        Assert.Contains(model.ReferencesTo(parent.Id), r => r.FromId == foreignKey && r.JsonPointer == "/foreignKeys/0/referencesTable");

        Assert.Equal(
            [
                (updated, "/excludes/0", "excludes"), (foreignKey, "/excludes/1", "excludes"),
                (created, "/redefines/0/member", "member"), (keyId, "/redefines/1/member", "member"),
            ],
            model.ReferencesFrom(notes.Id).Where(r => r.JsonPointer.StartsWith("/excludes", StringComparison.Ordinal) || r.JsonPointer.StartsWith("/redefines", StringComparison.Ordinal))
                .Select(r => (r.ToId, r.JsonPointer, r.Field)).OrderBy(r => r.JsonPointer, StringComparer.Ordinal));
        // So a member a table names keeps the stereotype from a plain delete, as listing the stereotype does.
        Assert.Contains(model.ReferencesTo(keyId), r => r.FromElementId == notes.Id);

        IndexEntry Entry(string id) => model.TryGetEntry(id, out var entry) ? entry : throw new InvalidOperationException(id);
    }

    [Fact]
    public void Default_and_exclude_lists_are_references_to_stereotypes()
    {
        // next-phase-2-design.md section 3.1: a package's and a schema entry's defaultStereotypes and an element's excludeStereotypes
        // list stereotype keys, each a reference to the stereotype's id (an unknown key is none; validation reports it).
        var (model, table, _, sales, billing) = Defaults(b =>
        {
            b.Stereotype("audited");
            b.Stereotype("owned");
        }, ["audited", "missing"], ["owned"], [], ["owned"]);
        var audited = model.GetStereotype("audited")!;
        var owned = model.GetStereotype("owned")!;
        var database = model.All<Database>().Single();

        Assert.Contains(model.ReferencesTo(audited.Id),
            r => r.FromElementId == database.Id && r.FromId == sales && r.JsonPointer.EndsWith("/defaultStereotypes/0", StringComparison.Ordinal) && r.Field == "defaultStereotypes");
        Assert.DoesNotContain(model.ReferencesFrom(database.Id), r => r.JsonPointer.EndsWith("/defaultStereotypes/1", StringComparison.Ordinal));
        Assert.Equal(
            [(billing, "/defaultStereotypes/0", "defaultStereotypes"), (table.Id, "/excludeStereotypes/0", "excludeStereotypes")],
            model.ReferencesTo(owned.Id).Select(r => (r.FromElementId, r.JsonPointer, r.Field)).OrderBy(r => r.JsonPointer, StringComparer.Ordinal));
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
