using Maquettiste.Engine.Model;
using Maquettiste.Engine.Pipeline;
using Maquettiste.Engine.Planning;
using Maquettiste.Engine.Resolution;
using Maquettiste.Engine.SchemaDiff;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Resolution;

/// <summary>
/// Dependency keys (engine-design.md sections 7 and 11, D38): every resolved object derived from an entity or relation lists
/// <c>r:&lt;id&gt;</c>, so a mapping, overlay or relation created after a unit was rendered changes that unit's input hash.
/// </summary>
public sealed class DependencyKeyTests
{
    private sealed record Fixture(ModelBuilder B, EntityBuilder Customer, EntityBuilder Invoice, RelationBuilder Places, EnumBuilder Status,
        RelationBuilder Tags, DatabaseBuilder Db);

    private static Fixture Create()
    {
        var b = new ModelBuilder(seed: 70);
        var status = b.Enum("Status").Member("Open").Member("Closed");
        var customer = b.Entity("Customer").Key("id", "uuid").Attr("name", "string");
        var invoice = b.Entity("Invoice").Key("id", "uuid").Attr("status", status);
        var places = b.Relation("places", customer, invoice, fromMax: MaxCardinality.One, fromRole: "customer", toRole: "invoice",
            fromNavigation: "customer", toNavigation: "invoices");
        var tags = b.Relation("tags", invoice, customer, fromRole: "tagged", toRole: "tagger").Attr("at", "date");
        var db = b.Database("main", Dialect.PostgreSql);
        b.Settings(s => s with { Conventions = new Conventions { EnumStorage = StorageKind.String } });
        return new Fixture(b, customer, invoice, places, status, tags, db);
    }

    [Fact]
    public void Entities_relations_and_navigations_list_referrer_keys_of_what_they_derive_from()
    {
        var f = Create();
        var mapping = f.B.Mapping(f.Db, f.Invoice);
        var model = ResolutionKit.Resolve(f.B);
        var invoice = model.Entity("Invoice");
        Assert.Contains($"e:{f.Invoice.Id}", invoice.Dependencies);
        Assert.Contains($"r:{f.Invoice.Id}", invoice.Dependencies);
        Assert.Contains($"e:{mapping.Id}", invoice.Dependencies);
        Assert.Contains($"e:{f.Db.Id}", invoice.Dependencies);
        Assert.Contains("s:conventions", invoice.Dependencies);

        var places = model.Relation("places");
        Assert.Contains($"r:{f.Places.Id}", places.Dependencies);
        Assert.Contains($"r:{f.Customer.Id}", places.Dependencies);
        Assert.Contains($"r:{f.Invoice.Id}", places.Dependencies);

        var navigation = model.Entity("Customer").Navigations.Single(n => n.Name == "invoices");
        Assert.Contains($"r:{f.Places.Id}", navigation.Dependencies);
        Assert.Contains($"r:{f.Customer.Id}", navigation.Dependencies);
        Assert.Contains($"r:{f.Invoice.Id}", navigation.Dependencies);
        Assert.Equal(invoice.Dependencies.Order(StringComparer.Ordinal), invoice.Dependencies);
        Assert.Equal(invoice.Dependencies.Distinct(StringComparer.Ordinal).Count(), invoice.Dependencies.Count);
    }

    [Fact]
    public void Synthesized_tables_and_their_columns_list_entity_relation_and_enum_referrer_keys()
    {
        var f = Create();
        var model = ResolutionKit.Resolve(f.B);
        var db = model.Db("main");

        var invoices = db.Table("invoices");
        Assert.Contains($"r:{f.Invoice.Id}", invoices.Dependencies);
        Assert.Contains($"r:{f.Places.Id}", invoices.Dependencies);     // it holds the relation's foreign key
        Assert.Contains($"r:{f.Customer.Id}", invoices.Dependencies);   // the key type comes from the principal
        Assert.Contains("s:conventions", invoices.Dependencies);
        Assert.Contains("s:typeMaps", invoices.Dependencies);
        Assert.Contains("s:inflection", invoices.Dependencies);
        Assert.All(invoices.Columns, c => Assert.Same(invoices.Dependencies, c.Dependencies));
        Assert.Equal(invoices.Dependencies, invoices.Columns.MembershipKeys);

        var junction = db.Tables.Single(t => t.IsJunction);
        Assert.Contains($"r:{f.Tags.Id}", junction.Dependencies);
        Assert.Contains($"r:{f.Invoice.Id}", junction.Dependencies);
        Assert.Contains($"r:{f.Customer.Id}", junction.Dependencies);
    }

    [Fact]
    public void A_typed_column_table_depends_on_the_type_and_reference_data()
    {
        // next-phase-2-design.md section 3.2 (D21): a designed column's typeRef adds the type's keys to its table as a projected column's
        // do: the element key, s:referenceData for a reference type, and the enum's and scalar type's keys.
        var f = Create();
        var state = new ReferenceType
        {
            Id = f.B.NewId(), Name = "InvoiceState",
            Code = new ReferenceCode { Id = f.B.NewId(), Length = 1 }, Label = new ReferenceLabel { Id = f.B.NewId() },
        };
        f.B.Add(state);
        var money = f.B.ScalarType("Money", "decimal").Decimal(12, 2);
        var plain = f.B.Table("plain_events", f.Db).Column("id", "uuid");
        var events = f.B.Table("invoice_events", f.Db).Column("status", "string").Column("kind", "string").Column("amount", "decimal")
            .Edit(t => t with
            {
                Columns =
                [
                    t.Columns[0] with { TypeRef = new TypeRef { Ref = state.Id } },
                    t.Columns[1] with { TypeRef = new TypeRef { Ref = f.Status.Id } },
                    t.Columns[2] with { TypeRef = new TypeRef { Ref = money.Id } },
                ],
            });
        var db = ResolutionKit.Resolve(f.B).Db("main");

        var table = db.Table("invoice_events");
        Assert.Contains($"e:{state.Id}", table.Dependencies);
        Assert.Contains("s:referenceData", table.Dependencies);
        Assert.Contains($"e:{f.Status.Id}", table.Dependencies);
        Assert.Contains($"e:{money.Id}", table.Dependencies);
        Assert.All(table.Columns, c => Assert.Same(table.Dependencies, c.Dependencies));
        var untyped = db.Table("plain_events");
        Assert.DoesNotContain("s:referenceData", untyped.Dependencies);
        Assert.DoesNotContain($"e:{state.Id}", untyped.Dependencies);
        Assert.Equal(plain.Id, untyped.Id);
        Assert.Equal(events.Id, table.Id);
    }

    [Fact]
    public void An_attribute_borrowing_sensitivity_depends_on_the_column_table()
    {
        // next-phase-2-design.md section 3.2 (D23): a bound attribute that states no sensitivity shows its columns' and depends on their
        // tables whether or not a column states one, so a later sensitive on the column replans what reads the attribute.
        var f = Create();
        var notes = f.B.Table("notes", f.Db).Column("id", "uuid", nullable: false).Column("body", "text").Column("author", "string").PrimaryKey("id");
        // Both attributes state a description, so only the sensitivity makes the dependency (section 3.3, decision 12).
        var note = f.B.Entity("Note").Key("id", "uuid").Attr("body", "text", a => a.Description("What was written."))
            .Attr("author", "string", a => a.Sensitive(Sensitivity.Pii).Description("Who wrote it."));
        note.Bind(new EntityBinding
        {
            Id = "", Database = f.Db.Id, Source = notes.Id,
            Fields =
            [
                new BindingField { Attribute = note.AttrId("id"), Column = notes.ColumnId("id") },
                new BindingField { Attribute = note.AttrId("body"), Column = notes.ColumnId("body") },
                new BindingField { Attribute = note.AttrId("author"), Column = notes.ColumnId("author") },
            ],
        });

        var entity = ResolutionKit.Resolve(f.B).Entity("Note");

        var body = entity.Attributes.Single(a => a.Name == "body");
        Assert.Null(body.Sensitive);
        Assert.Contains($"e:{notes.Id}", body.Dependencies);
        Assert.Equal(body.Dependencies.Order(StringComparer.Ordinal), body.Dependencies);
        var author = entity.Attributes.Single(a => a.Name == "author");
        Assert.Equal("pii", author.Sensitive);
        Assert.DoesNotContain($"e:{notes.Id}", author.Dependencies); // it states its own, so the column cannot change it
    }

    [Fact]
    public void An_attribute_borrowing_from_a_column_replans_for_the_column_not_for_the_rest_of_the_table()
    {
        // next-phase-2-design.md section 3.2: a borrowing attribute depends on what states its column's entry (the table's file), not on
        // everything the table reads, such as another entity bound to it or a type a column holds. Editing those leaves the input hash
        // of a unit reading the attribute alone; a sensitive added to the column changes it.
        var f = Create();
        var notes = f.B.Table("notes", f.Db).Column("id", "uuid", nullable: false).Column("body", "text").Column("status", "string")
            .PrimaryKey("id")
            .Edit(t => t with { Columns = [.. t.Columns.Select(c => c.Name == "status" ? c with { TypeRef = new TypeRef { Ref = f.Status.Id } } : c)] });
        var note = f.B.Entity("Note").Key("id", "uuid").Attr("body", "text");
        var other = f.B.Entity("OtherNote").Key("id", "uuid").Attr("body", "text", a => a.Description("Kept elsewhere."));
        foreach (var entity in new[] { note, other })
        {
            entity.Bind(new EntityBinding
            {
                Id = "", Database = f.Db.Id, Source = notes.Id,
                Fields =
                [
                    new BindingField { Attribute = entity.AttrId("id"), Column = notes.ColumnId("id") },
                    new BindingField { Attribute = entity.AttrId("body"), Column = notes.ColumnId("body") },
                ],
            });
        }

        var before = f.B.Build();
        var resolved = ResolutionKit.Resolve(before);
        var body = resolved.Entity("Note").Attributes.Single(a => a.Name == "body");
        var table = resolved.Db("main").Table("notes");
        Assert.Contains($"e:{notes.Id}", body.Dependencies);
        foreach (var unrelated in new[] { $"e:{other.Id}", $"e:{f.Status.Id}" })
        {
            Assert.Contains(unrelated, table.Dependencies);
            Assert.DoesNotContain(unrelated, body.Dependencies);
        }

        var none = new Dictionary<string, SchemaDiffResult>();
        var unit = new DependencyHasher(before, resolved, new PackSet([], []), none).InputHash("unit", body.Dependencies);
        string After(Func<Element, Element> edit)
        {
            var after = Tests.Model.StereotypeDefaultsTests.Rebuild(f.B, edit);
            return new DependencyHasher(after, ResolutionKit.Resolve(after), new PackSet([], []), none).InputHash("unit", body.Dependencies);
        }

        Assert.Equal(unit, After(e => e is Entity x && x.Id == other.Id ? x with { Tags = ["edited"] } : e));
        Assert.Equal(unit, After(e => e is EnumType x && x.Id == f.Status.Id ? x with { Tags = ["edited"] } : e));
        Assert.NotEqual(unit, After(e => e is Table t && t.Id == notes.Id
            ? t with { Columns = [.. t.Columns.Select(c => c.Name == "body" ? c with { Sensitive = Sensitivity.Pii } : c)] }
            : e));
    }

    [Fact]
    public void An_attribute_without_a_description_depends_on_its_bound_column_table()
    {
        // next-phase-2-design.md section 3.3, decision 12 (D34): an attribute with no description of its own shows its bound column's
        // comment and depends on the column's table whether or not the column carries one, so a comment added later replans what reads
        // it. Both attributes state a sensitivity here, so only the description makes the dependency.
        var f = Create();
        var notes = f.B.Table("notes", f.Db).Column("id", "uuid", nullable: false).Column("body", "text").Column("author", "string").PrimaryKey("id");
        var note = f.B.Entity("Note").Key("id", "uuid")
            .Attr("body", "text", a => a.Sensitive(Sensitivity.Pii))
            .Attr("author", "string", a => a.Sensitive(Sensitivity.Pii).Description("Who wrote it."));
        note.Bind(new EntityBinding
        {
            Id = "", Database = f.Db.Id, Source = notes.Id,
            Fields =
            [
                new BindingField { Attribute = note.AttrId("id"), Column = notes.ColumnId("id") },
                new BindingField { Attribute = note.AttrId("body"), Column = notes.ColumnId("body") },
                new BindingField { Attribute = note.AttrId("author"), Column = notes.ColumnId("author") },
            ],
        });

        var entity = ResolutionKit.Resolve(f.B).Entity("Note");

        var body = entity.Attributes.Single(a => a.Name == "body");
        Assert.Null(body.Description);
        Assert.Contains($"e:{notes.Id}", body.Dependencies);
        Assert.Equal(body.Dependencies.Order(StringComparer.Ordinal), body.Dependencies);
        var author = entity.Attributes.Single(a => a.Name == "author");
        Assert.Equal("Who wrote it.", author.Description);
        Assert.DoesNotContain($"e:{notes.Id}", author.Dependencies); // its own text and sensitivity: the column cannot change it
    }

    [Fact]
    public void A_mapping_overlay_or_relation_created_later_changes_the_referrer_hash_the_resolved_objects_list()
    {
        var f = Create();
        var before = f.B.Build();
        var resolved = ResolutionKit.Resolve(before);
        var key = $"r:{f.Invoice.Id}";
        Assert.Contains(key, resolved.Entity("Invoice").Dependencies);
        Assert.Contains(key, resolved.Db("main").Table("invoices").Dependencies);
        var hashBefore = before.ReferrersHash(f.Invoice.Id);

        var withMapping = Create();
        withMapping.B.Mapping(withMapping.Db, withMapping.Invoice).Storage("status", StorageKind.String);
        Assert.NotEqual(hashBefore, withMapping.B.Build().ReferrersHash(f.Invoice.Id));

        var withOverlay = Create();
        withOverlay.B.Table("", withOverlay.Db).OverlayFor(withOverlay.Invoice).Overlay(withOverlay.Invoice.AttrId("status"), name: "state");
        Assert.NotEqual(hashBefore, withOverlay.B.Build().ReferrersHash(f.Invoice.Id));

        var withRelation = Create();
        withRelation.B.Relation("follows", withRelation.Invoice, withRelation.Invoice);
        Assert.NotEqual(hashBefore, withRelation.B.Build().ReferrersHash(f.Invoice.Id));
    }

    [Fact]
    public void Promoted_entities_and_their_tables_list_the_relation_referrer_key()
    {
        var f = Create();
        f.B.Add(new Mapping { Id = f.B.NewId(), Name = "tags", Database = f.Db.Id, Relation = f.Tags.Id, Shape = RelationShape.Promoted });
        var model = ResolutionKit.Resolve(f.B);
        var promoted = model.Entities.Single(e => e.IsPromoted);
        Assert.Contains($"r:{f.Tags.Id}", promoted.Dependencies);
        Assert.Contains($"r:{f.Tags.Id}", promoted.Mappings["main"].Table.Dependencies);
    }

    [Fact]
    public void A_package_that_gains_a_default_list_re_renders_its_members()
    {
        // next-phase-2-design.md section 3.1: a resolved object whose holder has a package lists the e: key of every package on that
        // package's chain, own first, so a package that gains its first default list changes the input hash of its members' units,
        // a table without a file included (its holder's package is its entity's).
        var b = new ModelBuilder(seed: 71);
        b.Stereotype("audited");
        var billing = b.Package("Billing");
        var invoices = b.Package("Invoices", billing);
        var invoice = b.Entity("Invoice", invoices).Key("id", "uuid");
        b.Database("main", Dialect.PostgreSql);
        var before = b.Build();
        var resolved = ResolutionKit.Resolve(before);
        var entity = resolved.Entity("Invoice");
        var table = resolved.Db("main").Table("invoices");
        foreach (var dependencies in new[] { entity.Dependencies, table.Dependencies })
        {
            Assert.Contains($"e:{invoices.Id}", dependencies);
            Assert.Contains($"e:{billing.Id}", dependencies);
        }

        Assert.Contains($"e:{billing.Id}", resolved.Packages.Single(p => p.Name == "Invoices").Dependencies);
        Assert.DoesNotContain(resolved.Packages.Single(p => p.Name == "Billing").Dependencies, k => k == $"e:{invoices.Id}");

        var after = Tests.Model.StereotypeDefaultsTests.Rebuild(b, e => e is Package p && p.Id == billing.Id ? p with { DefaultStereotypes = ["audited"] } : e);
        var resolvedAfter = ResolutionKit.Resolve(after);
        Assert.Equal([("audited", "package-default")], resolvedAfter.Entity("Invoice").Stereotypes.Select(s => (s.Key, s.Source)));
        var none = new Dictionary<string, SchemaDiffResult>();
        var hashBefore = new DependencyHasher(before, resolved, new PackSet([], []), none);
        var hashAfter = new DependencyHasher(after, resolvedAfter, new PackSet([], []), none);
        Assert.Equal(hashBefore.CurrentHash($"e:{invoice.Id}"), hashAfter.CurrentHash($"e:{invoice.Id}"));
        Assert.NotEqual(hashBefore.InputHash("unit", entity.Dependencies), hashAfter.InputHash("unit", entity.Dependencies));
        Assert.NotEqual(hashBefore.InputHash("unit", table.Dependencies), hashAfter.InputHash("unit", table.Dependencies));
    }

    [Fact]
    public void An_x_key_hashes_the_extension_file()
    {
        // next-phase-2-design.md section 3.4: x:<path under extensions> is the content hash of the file, or absent.
        var b = new ModelBuilder(seed: 72);
        b.Entity("Customer").Key("id", "uuid");
        var built = b.Build();
        ModelSnapshot With(string hash) => ModelSnapshot.Create(built.Documents, built.Settings, built.SettingsHash, [],
            [new ScriptSource(".maquettiste/extensions/rules/naming.js", "", "rule-hash")], 1,
            seedScripts: [new ScriptSource(".maquettiste/extensions/seeds/audited-records.js", "maquettiste.rows(() => []);", hash)]);
        var none = new Dictionary<string, SchemaDiffResult>();
        var first = With("one");
        var resolved = ResolutionKit.Resolve(first);
        var hasher = new DependencyHasher(first, resolved, new PackSet([], []), none);

        Assert.Equal("one", hasher.CurrentHash("x:seeds/audited-records.js"));
        Assert.Equal("rule-hash", hasher.CurrentHash("x:rules/naming.js"));
        Assert.Equal(DependencyHasher.Absent, hasher.CurrentHash("x:seeds/missing.js"));
        var second = With("two");
        Assert.Equal("two", new DependencyHasher(second, resolved, new PackSet([], []), none).CurrentHash("x:seeds/audited-records.js"));
        Assert.NotEqual(hasher.InputHash("unit", ["x:seeds/audited-records.js"]),
            new DependencyHasher(second, resolved, new PackSet([], []), none).InputHash("unit", ["x:seeds/audited-records.js"]));
    }

    [Fact]
    public void Rule_view_keys_hash_as_the_rule_cache_hashes_them()
    {
        // The rule view's ix: and rt: reads (a rows script's, section 3.4) hash in planning as the rule result cache hashes them, so a
        // unit that read scripted rows renders again exactly when one of those reads changes.
        var b = new ModelBuilder(seed: 73);
        b.Stereotype("audited");
        var customer = b.Entity("Customer").Key("id", "uuid");
        var invoice = b.Entity("Invoice").Key("id", "uuid");
        b.Relation("places", customer, invoice, fromMax: MaxCardinality.One, fromRole: "customer", toRole: "invoice");
        var before = b.Build();
        var after = Tests.Model.StereotypeDefaultsTests.Rebuild(b, e => e is Entity entity && entity.Id == invoice.Id ? entity with { Stereotypes = ["audited"] } : e);
        var none = new Dictionary<string, SchemaDiffResult>();
        var index = Engine.Validation.RuleModelIndex.Key(Engine.Validation.RuleModelIndex.Stereotype, "audited");
        var references = "rt:" + customer.Id;

        foreach (var model in new[] { before, after })
        {
            var hasher = new DependencyHasher(model, ResolutionKit.Resolve(model), new PackSet([], []), none);
            var rules = Engine.Validation.RuleKeyHasher.Of(model);
            Assert.Equal(rules.CurrentHash(index), hasher.CurrentHash(index));
            Assert.Equal(rules.CurrentHash(references), hasher.CurrentHash(references));
            Assert.NotEqual(DependencyHasher.Absent, hasher.CurrentHash(references));
        }

        Assert.NotEqual(Engine.Validation.RuleKeyHasher.Of(before).CurrentHash(index), Engine.Validation.RuleKeyHasher.Of(after).CurrentHash(index));
    }

    [Fact]
    public void Lists_carry_membership_keys()
    {
        var f = Create();
        var model = ResolutionKit.Resolve(f.B);
        Assert.Contains("k:entity", model.Entities.MembershipKeys);
        Assert.Contains("k:relation", model.Entities.MembershipKeys);
        Assert.Contains("k:mapping", model.Entities.MembershipKeys);
        Assert.Equal(["k:enum"], model.Enums.MembershipKeys);
        Assert.Equal(["k:database"], model.Databases.MembershipKeys);
        var tables = model.Db("main").Tables.MembershipKeys;
        foreach (var key in new[] { "k:entity", "k:relation", "k:enum", "k:table", "k:mapping", "s:conventions", $"e:{f.Invoice.Id}", $"r:{f.Db.Id}" })
            Assert.Contains(key, tables);
        Assert.Contains($"r:{f.Customer.Id}", model.Entity("Customer").Navigations.MembershipKeys);
        Assert.Equal([$"r:{f.Customer.Id}"], model.Entity("Customer").Derived.MembershipKeys);
    }

    [Fact]
    public void Referrer_keys_are_present_for_every_synthesized_object_kind()
    {
        var f = Create();
        var model = ResolutionKit.Resolve(f.B);
        foreach (var table in model.Db("main").Tables)
        {
            var sources = new List<string>();
            if (table.Entity is { } e)
                sources.Add(e.Id);
            if (table.Relation is { } r)
                sources.Add(r.Id);
            Assert.NotEmpty(sources);
            Assert.All(sources, id => Assert.Contains("r:" + id, table.Dependencies));
        }

        Assert.All(model.Entities, e => Assert.Contains("r:" + e.Id, e.Dependencies));
        Assert.All(model.Relations, r => Assert.Contains("r:" + r.Id, r.Dependencies));
        Assert.All(model.Entities.SelectMany(e => e.Navigations), n => Assert.Contains("r:" + n.Relation.Id, n.Dependencies));
        Assert.IsType<RDatabase>(model.Find(f.Db.Id));
    }
}
