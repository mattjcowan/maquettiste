using System.Collections.Immutable;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Pipeline;
using Maquettiste.Engine.Resolution;
using Maquettiste.Engine.Tests.Rendering;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Resolution;

/// <summary>Conceptual resolution, conventions, identifier limits, lookup, progress and cancellation.</summary>
public sealed class ResolverBehaviorTests
{
    [Fact]
    public void Attributes_flatten_base_first_then_own_then_virtual_then_stable_by_order()
    {
        var b = new ModelBuilder(seed: 80);
        b.Stereotype("audited").Attr("createdAt", "datetime", a => a.Order(900)).Attr("tenant", "string");
        var root = b.Entity("Root").Key("id", "uuid").Attr("code", "string").Stereotype("audited");
        b.Entity("Leaf").Base(root).Attr("size", "int32").Attr("first", "string", a => a.Order(-1)).Attr("rank", "int32");
        var model = ResolutionKit.Resolve(b);
        var leaf = model.Entity("Leaf");
        Assert.Equal(["first", "id", "code", "tenant", "size", "rank", "createdAt"], leaf.Attributes.Select(a => a.Name));
        Assert.Equal([0, 1, 2, 3, 4, 5, 6], leaf.Attributes.Select(a => a.Order));
        Assert.Equal(["size", "first", "rank"], leaf.OwnAttributes.Select(a => a.Name));
        var id = leaf.Attributes.Single(a => a.Name == "id");
        Assert.True(id.IsInherited);
        Assert.Same(model.Entity("Root"), id.DeclaringEntity);
        Assert.Same(leaf, id.Owner);
        var tenant = leaf.Attributes.Single(a => a.Name == "tenant");
        Assert.True(tenant.IsVirtual);
        Assert.Equal("audited", tenant.FromStereotype!.Key);
        Assert.Equal(["id"], leaf.Key!.Attributes.Select(a => a.Name));
        Assert.Same(model.Entity("Root"), leaf.Base);
        Assert.Equal(["Leaf"], model.Entity("Root").Derived.Select(e => e.Name));
    }

    [Fact]
    public void Common_members_fall_back_and_merge_stereotype_properties_under_the_elements_own()
    {
        var b = new ModelBuilder(seed: 81);
        var pkg = b.Package("Sales");
        var sub = b.Package("Orders", pkg);
        var parent = b.Category("Money");
        var child = b.Category("Receivables", parent);
        b.Stereotype("aggregate-root").DefaultProperty("retention", 7).DefaultProperty("owner", "platform");
        b.Entity("Person", sub).Key("id", "uuid").Stereotype("aggregate-root").Tag("pii").Category(child).Property("owner", "sales")
            .DisplayName("Human").Description("A person.");
        var model = ResolutionKit.Resolve(b);
        var person = model.Entity("Person");
        Assert.Equal("Human", person.DisplayName);
        Assert.Equal("People", person.PluralName);
        Assert.Contains("s:inflection", person.Dependencies);
        Assert.Equal("A person.", person.Description);
        Assert.Equal("Money/Receivables", person.Category!.Path);
        Assert.True(person.HasStereotype("aggregate-root"));
        Assert.True(person.HasTag("pii"));
        Assert.Equal(7L, person.Properties["retention"]);
        Assert.Equal("sales", person.Properties["owner"]);
        Assert.Equal("Sales.Orders", person.Package!.QualifiedName);
        Assert.Equal(["Sales", "Sales.Orders"], model.Packages.Select(p => p.QualifiedName));
        Assert.Equal(["Sales.Orders"], model.Packages[0].Children.Select(p => p.QualifiedName));
        Assert.Same(person, Assert.Single(model.Packages[1].Entities));
    }

    [Fact]
    public void Implied_stereotypes_merge_under_own_and_carry_implied_by()
    {
        var b = new ModelBuilder(seed: 82);
        b.Stereotype("soft-delete").Attr("deletedAt", "datetimeoffset").DefaultProperty("retention", 30).DefaultProperty("purge", "nightly");
        b.Stereotype("archived").DefaultProperty("archive", true);
        b.Stereotype("aggregate-root").Implies("soft-delete", "archived").DefaultProperty("retention", 90);
        b.Entity("Invoice").Key("id", "uuid").Stereotype("aggregate-root").Property("purge", "weekly");
        var model = ResolutionKit.Resolve(b);
        var invoice = model.Entity("Invoice");

        // Implied first (in implies order), then own; the own stereotype's default wins over the implied one's, the element's own
        // value over both.
        Assert.Equal([("soft-delete", "aggregate-root"), ("archived", "aggregate-root"), ("aggregate-root", null)],
            invoice.Stereotypes.Select(s => (s.Key, s.ImpliedBy)));
        Assert.True(invoice.HasStereotype("soft-delete"));
        Assert.Equal(90L, invoice.Properties["retention"]);
        Assert.Equal("weekly", invoice.Properties["purge"]);
        Assert.Equal(true, invoice.Properties["archive"]);
        // An implied stereotype gives virtual attributes as an own one does, and the carrier depends on it.
        var deletedAt = invoice.Attributes.Single(a => a.Name == "deletedAt");
        Assert.True(deletedAt.IsVirtual);
        Assert.Equal("soft-delete", deletedAt.FromStereotype!.Key);
        Assert.Contains("e:" + model.Source.GetStereotype("soft-delete")!.Id, invoice.Dependencies);
        Assert.Contains("e:" + model.Source.GetStereotype("aggregate-root")!.Id, invoice.Dependencies);
    }

    [Fact]
    public void An_own_entry_replaces_an_implied_copy_of_the_same_stereotype()
    {
        var b = new ModelBuilder(seed: 83);
        b.Stereotype("soft-delete").DefaultProperty("retention", 30);
        b.Stereotype("aggregate-root").Implies("soft-delete").DefaultProperty("retention", 90);
        // The element lists soft-delete after the stereotype that implies it: its own entry is kept, at its own position, and wins.
        b.Entity("Invoice").Key("id", "uuid").Stereotype("aggregate-root").Stereotype("soft-delete");
        var model = ResolutionKit.Resolve(b);
        var invoice = model.Entity("Invoice");

        Assert.Equal([("aggregate-root", null), ("soft-delete", null)], invoice.Stereotypes.Select(s => (s.Key, s.ImpliedBy)));
        Assert.Equal(30L, invoice.Properties["retention"]);
    }

    [Fact]
    public void Find_returns_every_kind_of_resolved_object()
    {
        var model = ResolutionKit.Resolve(BillingFixture.Create());
        var invoice = model.Entity("Invoice");
        Assert.Same(invoice, model.Find(invoice.Id));
        Assert.Same(invoice.OwnAttributes[1], model.Find(invoice.OwnAttributes[1].Id));
        var table = model.Db("main").Table("invoices");
        Assert.Same(table, model.Find(table.Key));
        Assert.Same(table.Columns[0], model.Find(table.Columns[0].Id));
        var navigation = invoice.Navigations[0];
        Assert.Same(navigation, model.Find(navigation.Id));
        Assert.Same(model.Relation("places").Ends[0], model.Find(model.Relation("places").Ends[0].Id));
        Assert.Null(model.Find("missing"));
    }

    [Fact]
    public void Naming_conventions_apply_per_project_and_per_database()
    {
        var b = new ModelBuilder(seed: 82);
        var customer = b.Entity("Customer").Key("id", "int64").Attr("fullName", "string").Attr("email", "string", a => a.Unique().Indexed());
        var order = b.Entity("SalesOrder").Key("id", "int64");
        b.Relation("places", customer, order, fromMax: MaxCardinality.One, fromRole: "buyer", toRole: "order");
        b.Database("pg", Dialect.PostgreSql);
        b.Database("ms", Dialect.SqlServer);
        b.Settings(s => s with
        {
            Conventions = new Conventions { KeyColumn = "{entity}_{attribute}", ForeignKeyColumn = "{role}_{key}", PrimaryKeyName = "{table}_pkey" },
            Databases = ImmutableDictionary<string, Conventions>.Empty.Add("ms", new Conventions
            {
                TableCase = CaseStyle.Pascal, ColumnCase = CaseStyle.Pascal, PluralTables = false, ForeignKeyName = "FK_{table}_{columns}",
            }),
        });
        var model = ResolutionKit.Resolve(b);
        var pg = model.Db("pg");
        Assert.Equal(["customers", "sales_orders"], pg.Tables.Select(t => t.Name));
        Assert.Equal(["customer_id", "full_name", "email"], pg.Table("customers").Columns.Names());
        Assert.Equal("customers_pkey", pg.Table("customers").PrimaryKey!.Name);
        Assert.Equal("buyer_id", pg.Table("sales_orders").Columns[1].Name);
        Assert.Equal("fk_sales_orders_buyer_id", pg.Table("sales_orders").ForeignKeys[0].Name);

        var ms = model.Db("ms");
        Assert.Equal(["Customer", "SalesOrder"], ms.Tables.Select(t => t.Name));
        Assert.Equal(["CustomerId", "FullName", "Email"], ms.Table("Customer").Columns.Names());
        Assert.Equal("CustomerPkey", ms.Table("Customer").PrimaryKey!.Name);
        Assert.Equal("FkSalesOrderBuyerId", ms.Table("SalesOrder").ForeignKeys[0].Name);
        Assert.Equal("UqCustomerEmail", ms.Table("Customer").Uniques[0].Name);
        Assert.Empty(ms.Table("Customer").Indexes); // unique wins over indexed: one constraint per attribute
    }

    [Fact]
    public void Preserve_case_keeps_names_as_written()
    {
        var b = new ModelBuilder(seed: 83);
        b.Entity("OrderLine").Key("ID", "int64").Attr("unitPrice", "decimal");
        b.Database("db", Dialect.Sqlite);
        b.Settings(s => s with { Conventions = new Conventions { TableCase = CaseStyle.Preserve, ColumnCase = CaseStyle.Preserve } });
        var table = ResolutionKit.Resolve(b).Db("db").Tables.Single();
        Assert.Equal("OrderLines", table.Name);
        Assert.Equal(["ID", "unitPrice"], table.Columns.Names());
    }

    [Fact]
    public void Identifiers_over_the_limit_are_reported_never_truncated()
    {
        var b = new ModelBuilder(seed: 84);
        var entity = b.Entity("AnExtremelyLongEntityNameThatKeepsGoingAndGoingPastTheLimit").Key("id", "int64")
            .Attr("anAttributeNameThatIsAlsoFarTooLongForAPostgreSqlIdentifierToHold", "string");
        b.Database("pg", Dialect.PostgreSql);
        b.Database("lite", Dialect.Sqlite);
        var model = ResolutionKit.Resolve(b);
        var table = model.Db("pg").Tables.Single();
        Assert.True(table.Name.Length > 63);
        var diagnostics = model.Diagnostics.Where(d => d.Rule == "MQ4001").ToList();
        Assert.Contains(diagnostics, d => d.Message.StartsWith("Table name", StringComparison.Ordinal) && d.ElementId == entity.Id);
        Assert.Contains(diagnostics, d => d.Message.StartsWith("Column name", StringComparison.Ordinal));
        Assert.Contains(diagnostics, d => d.Message.StartsWith("Primary key name", StringComparison.Ordinal));
        Assert.All(diagnostics, d => Assert.Equal(DiagnosticSeverity.Error, d.Severity));
        Assert.All(diagnostics, d => Assert.Contains("'pg'", d.Message, StringComparison.Ordinal));
        Assert.EndsWith(".json", diagnostics[0].FilePath, StringComparison.Ordinal);

        b.Settings(s => s with { Validation = new ValidationSettings { Rules = ImmutableDictionary<string, string>.Empty.Add("MQ4001", "warning") } });
        Assert.All(ResolutionKit.Resolve(b).Diagnostics, d => Assert.Equal(DiagnosticSeverity.Warning, d.Severity));
        b.Settings(s => s with { Validation = new ValidationSettings { Rules = ImmutableDictionary<string, string>.Empty.Add("MQ4001", "off") } });
        Assert.Empty(ResolutionKit.Resolve(b).Diagnostics);
    }

    [Fact]
    public void Database_max_identifier_length_overrides_the_dialect()
    {
        var b = new ModelBuilder(seed: 85);
        b.Entity("Customer").Key("id", "int64");
        b.Add(new Database { Id = b.NewId(), Name = "tight", Dialect = Dialect.SqlServer, MaxIdentifierLength = 8 });
        var model = ResolutionKit.Resolve(b);
        Assert.Equal(8, model.Db("tight").MaxIdentifierLength);
        Assert.Contains(model.Diagnostics, d => d.Message.StartsWith("Table name 'customers'", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Resolution_reports_progress_and_honours_cancellation()
    {
        var model = BillingFixture.Create().Build();
        var updates = new List<ProgressUpdate>();
        var resolver = new ModelResolver(ResolutionKit.Options);
        await resolver.ResolveAsync(model, new SyncProgress(updates.Add), TestContext.Current.CancellationToken);
        Assert.NotEmpty(updates);
        Assert.All(updates, u => Assert.Equal(PipelineStage.Resolve, u.Stage));
        Assert.Equal(updates[^1].Total, updates[^1].Done);

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => resolver.ResolveAsync(model, null, cts.Token));
    }

    [Fact]
    public async Task Cancellation_is_observed_between_elements_during_a_run()
    {
        var model = BillingFixture.Create().Build();
        using var cts = new CancellationTokenSource();
        var seen = 0;
        var progress = new SyncProgress(u =>
        {
            seen = u.Done;
            if (u.Done == 5)
                cts.Cancel();
        });
        var resolver = new ModelResolver(ResolutionKit.Options);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => resolver.ResolveAsync(model, progress, cts.Token));
        Assert.Equal(5, seen); // the next element saw the cancellation before reporting
    }

    [Fact]
    public void Dangling_references_do_not_break_resolution()
    {
        var b = new ModelBuilder(seed: 86);
        var a = b.Entity("A").Key("id", "uuid");
        b.Add(new Relation
        {
            Id = b.NewId(), Name = "broken",
            Ends = [new RelationEnd { Id = b.NewId(), Entity = a.Id, Role = "a" }, new RelationEnd { Id = b.NewId(), Entity = b.NewId(), Role = "x" }],
        });
        b.Add(new Entity { Id = b.NewId(), Name = "Orphan", Base = b.NewId(), Attributes = [new ModelAttribute { Id = b.NewId(), Name = "x", Type = new TypeRef { Ref = b.NewId() } }] });
        b.Database("main", Dialect.PostgreSql);
        var model = ResolutionKit.Resolve(b);
        Assert.Empty(model.Relations);
        Assert.Equal("string", model.Db("main").Table("orphans").Column("x").Type);
    }

    [Fact]
    public async Task Rationale_decision_and_status_reach_templates()
    {
        // next-phase-1-design.md section 4.5: each object reads the three fields from its own file or entry; status is its JSON value.
        var b = new ModelBuilder(seed: 87);
        var db = b.Database("main", Dialect.PostgreSql);
        b.Entity("Invoice").Key("id", "uuid").Attr("amount", "decimal", a => a.Precision(18).Scale(2));
        b.Table("ledger", db).Column("id", "int64", nullable: false).Column("code", "string", length: 20).PrimaryKey("id");
        var annotated = new ModelBuilder(seed: 88);
        foreach (var element in b.BuildElements())
        {
            annotated.Add(element switch
            {
                Entity e => e with
                {
                    Rationale = "One row per **issued** bill.", Decision = "ADR-0042", Status = ElementStatus.Approved,
                    Attributes = [e.Attributes[0], e.Attributes[1] with { Rationale = "Replaced by total.", Status = ElementStatus.Deprecated }],
                },
                Table t => t with { Status = ElementStatus.Retired, Columns = [t.Columns[0], t.Columns[1] with { Decision = "https://example.com/adr/7", Status = ElementStatus.Proposed }] },
                Database d => d with { Rationale = "The one store." },
                _ => element,
            });
        }

        var model = ResolutionKit.Resolve(annotated);
        var invoice = model.Entity("Invoice");
        Assert.Equal(("One row per **issued** bill.", "ADR-0042", "approved"), (invoice.Rationale, invoice.Decision, invoice.Status));
        Assert.Equal(("The one store.", null, null), (model.Db("main").Rationale, model.Db("main").Decision, model.Db("main").Status));

        var entityUnit = await Adhoc.RenderAsync(
            "{{ entity.rationale }}|{{ entity.decision }}|{{ entity.status }}|{{ for a in entity.attributes }}{{ a.name }}:{{ a.status }}:{{ a.rationale }};{{ end }}",
            invoice, model: model);
        Assert.Equal("One row per **issued** bill.|ADR-0042|approved|id::;amount:deprecated:Replaced by total.;", entityUnit.Text());

        var tableUnit = await Adhoc.RenderAsync(
            "{{ table.status }}|{{ for c in table.columns }}{{ c.name }}:{{ c.status }}:{{ c.decision }};{{ end }}",
            model.Db("main").Table("ledger"), u => u with { For = "each table" }, model: model);
        Assert.Equal("retired|id::;code:proposed:https://example.com/adr/7;", tableUnit.Text());
    }

    [Fact]
    public async Task Rationale_decision_and_status_reach_templates_on_enum_members_stereotypes_and_categories()
    {
        // next-phase-1-design.md section 4.5: an enum member, a stereotype definition and a category read the three fields from
        // their own entry or file, as elements do; status is its JSON value and an unwritten field is null.
        var b = new ModelBuilder(seed: 89);
        b.Enum("InvoiceState").Member("draft", 0).Member("paid", 1);
        b.Stereotype("audited");
        var billing = b.Category("Billing");
        b.Category("Archive", billing);
        b.Entity("Invoice").Key("id", "uuid").Category(billing);
        var annotated = new ModelBuilder(seed: 90);
        foreach (var element in b.BuildElements())
        {
            annotated.Add(element switch
            {
                EnumType e => e with
                {
                    Members = [e.Members[0] with { Rationale = "A bill not yet **issued**.", Decision = "ADR-0007", Status = ElementStatus.Deprecated }, e.Members[1]],
                },
                Stereotype s => s with { Rationale = "Who changed a row, and when.", Decision = "https://example.com/adr/12", Status = ElementStatus.Approved },
                CategoryTree t => t with
                {
                    Categories = [t.Categories[0] with { Rationale = "Everything we bill.", Decision = "ADR-0031", Status = ElementStatus.Proposed }, t.Categories[1]],
                },
                _ => element,
            });
        }

        var model = ResolutionKit.Resolve(annotated);
        var members = Assert.Single(model.Enums).Members;
        Assert.Equal(("A bill not yet **issued**.", "ADR-0007", "deprecated"), (members[0].Rationale, members[0].Decision, members[0].Status));
        Assert.Equal((null, null, null), (members[1].Rationale, members[1].Decision, members[1].Status));
        var audited = Assert.Single(model.Stereotypes);
        Assert.Equal(("Who changed a row, and when.", "https://example.com/adr/12", "approved"), (audited.Rationale, audited.Decision, audited.Status));
        var categories = Assert.Single(model.CategoryTrees).Categories;
        Assert.Equal(("Everything we bill.", "ADR-0031", "proposed"), (categories[0].Rationale, categories[0].Decision, categories[0].Status));
        Assert.Equal((null, null, null), (categories[1].Rationale, categories[1].Decision, categories[1].Status));

        var unit = await Adhoc.RenderAsync(
            "{{ for e in model.enums }}{{ for m in e.members }}{{ m.name }}:{{ m.rationale }}:{{ m.decision }}:{{ m.status }};{{ end }}{{ end }}"
            + "|{{ for s in model.stereotypes }}{{ s.key }}:{{ s.rationale }}:{{ s.decision }}:{{ s.status }};{{ end }}"
            + "|{{ for t in model.category_trees }}{{ for c in t.categories }}{{ c.name }}:{{ c.rationale }}:{{ c.decision }}:{{ c.status }};{{ end }}{{ end }}"
            + "|{{ entity.category.rationale }}:{{ entity.category.decision }}:{{ entity.category.status }}",
            model.Entity("Invoice"), model: model);
        Assert.Equal(
            "draft:A bill not yet **issued**.:ADR-0007:deprecated;paid:::;"
            + "|audited:Who changed a row, and when.:https://example.com/adr/12:approved;"
            + "|Billing:Everything we bill.:ADR-0031:proposed;Archive:::;"
            + "|Everything we bill.:ADR-0031:proposed",
            unit.Text());
    }

    [Fact]
    public void A_schema_default_reaches_a_table_without_a_file()
    {
        // next-phase-2-design.md section 3.1: a synthesized table without an overlay takes its schema's defaults (and the stereotypes
        // they imply): stereotypes, merged default properties, storage, and the keys of the stereotypes and its package chain.
        var b = new ModelBuilder(seed: 870);
        b.Stereotype("versioned").AppliesTo("table");
        b.Stereotype("audited").AppliesTo("table").Implies("versioned").DefaultProperty("retention", 7);
        var billing = b.Package("Billing");
        var invoice = b.Entity("Invoice", billing).Key("id", "uuid").Attr("notes", "string", a => a.Collection());
        var customer = b.Entity("Customer", billing).Key("id", "uuid");
        var main = b.Database("main", Dialect.PostgreSql);
        var sales = main.Schema("sales");
        var common = main.Schema("public");
        main.Package(billing, sales);
        b.Mapping(main, customer).Schema(common);
        var storage = new Dictionary<string, IReadOnlyDictionary<string, System.Text.Json.JsonElement>>(StringComparer.Ordinal)
        {
            ["postgresql"] = new Dictionary<string, System.Text.Json.JsonElement>(StringComparer.Ordinal) { ["fillfactor"] = System.Text.Json.JsonSerializer.SerializeToElement(80) },
        };
        var snapshot = Tests.Model.StereotypeDefaultsTests.Rebuild(b, e => e is Stereotype { Key: "audited" } s
            ? s with { Storage = storage }
            : Tests.Model.StereotypeDefaultsTests.SchemaDefaults(e, sales, "audited"));
        var model = ResolutionKit.Resolve(snapshot);
        var db = model.Db("main");

        var invoices = db.Table("invoices");
        Assert.Equal([("versioned", "schema-default", "audited"), ("audited", "schema-default", null)],
            invoices.Stereotypes.Select(s => (s.Key, s.Source, s.ImpliedBy)));
        Assert.True(invoices.HasStereotype("versioned"));
        Assert.Equal(7L, invoices.Properties["retention"]);
        Assert.Equal(["fillfactor = 80"], invoices.Storage.Select(p => p.Name + " = " + p.Value));
        Assert.Contains("e:" + snapshot.GetStereotype("audited")!.Id, invoices.Dependencies);
        Assert.Contains("e:" + snapshot.GetStereotype("versioned")!.Id, invoices.Dependencies);
        Assert.Contains("e:" + billing.Id, invoices.Dependencies);
        Assert.Null(invoices.Description);

        // A child table is in its owner's schema; a table its mapping puts in another schema, and the entity itself, take nothing.
        var notes = db.Tables.Single(t => t.Attribute is not null);
        Assert.Equal(["versioned", "audited"], notes.Stereotypes.Select(s => s.Key));
        Assert.Empty(db.Table("customers").Stereotypes);
        Assert.Empty(db.Table("customers").Storage);
        Assert.Empty(model.Entity("Invoice").Stereotypes);
        Assert.Equal(invoice.Id + "@" + main.Id, invoices.Key);
    }

    [Fact]
    public async Task A_default_entry_points_at_the_stereotypes_list()
    {
        // next-phase-2-design.md section 3.1: a default has no entry (-1), so a finding about it points at the element's stereotypes
        // list, or at the file root when the file lists none.
        var b = new ModelBuilder(seed: 871);
        b.Stereotype("owned").Parameters("""{ "owner": { "type": "string" } }""", "owner");
        b.Stereotype("tracked");
        var billing = b.Package("Billing");
        var plain = b.Entity("Invoice", billing).Key("id", "uuid");
        var listed = b.Entity("Payment", billing).Key("id", "uuid").Stereotype("tracked");
        var snapshot = Tests.Model.StereotypeDefaultsTests.Rebuild(b, e => e is Package p && p.Id == billing.Id ? p with { DefaultStereotypes = ["owned"] } : e);

        var applied = Assert.Single(snapshot.EffectiveStereotypes(snapshot.Get<Entity>(plain.Id)!));
        Assert.Equal((-1, StereotypeSource.PackageDefault, billing.Id), (applied.Entry, applied.Source, applied.DefaultHolder));
        var model = ResolutionKit.Resolve(snapshot);
        Assert.Equal([("owned", "package-default")], model.Entity("Invoice").Stereotypes.Select(s => (s.Key, s.Source)));
        Assert.Equal([("owned", "package-default"), ("tracked", "own")], model.Entity("Payment").Stereotypes.Select(s => (s.Key, s.Source)));

        var report = await Validation.ValidationFixture.Validator().ValidateAsync(snapshot, ValidationScope.All, null, TestContext.Current.CancellationToken);
        var findings = report.Diagnostics.Where(d => d.Rule == "MQ2018").OrderBy(d => d.ElementId, StringComparer.Ordinal).ToList();
        Assert.Equal(
            new[] { (plain.Id, ""), (listed.Id, "/stereotypes") }.OrderBy(f => f.Item1, StringComparer.Ordinal),
            findings.Select(d => (d.ElementId!, d.JsonPointer ?? "")));
    }

    // ---- inherited members (next-phase-2-design.md section 3.1, D7) ----

    private static readonly IReadOnlyDictionary<string, string> Now = new Dictionary<string, string>(StringComparer.Ordinal) { ["*"] = "CURRENT_TIMESTAMP" };

    /// <summary>
    /// The design's audited stereotype for tables: columns created_at (NOT NULL, defaulting to CURRENT_TIMESTAMP) and updated_at, an
    /// index on created_at and a check on updated_at; <paramref name="edit"/> changes it (attributes, more members).
    /// </summary>
    private static Stereotype Audited(ModelBuilder b, Func<Stereotype, Stereotype>? edit = null)
    {
        string created = b.NewId(), updated = b.NewId();
        var audited = new Stereotype
        {
            Id = b.NewId(), Key = "audited", Name = "Audited", AppliesTo = ["entity", "table", "column"],
            Columns =
            [
                new Column { Id = created, Name = "created_at", Type = "datetimeoffset", Nullable = false, DefaultSql = Now },
                new Column { Id = updated, Name = "updated_at", Type = "datetimeoffset" },
            ],
            Indexes = [new TableIndex { Id = b.NewId(), Columns = [new IndexColumn { Column = created }] }],
            Checks = [new CheckConstraint { Id = b.NewId(), Column = updated, Expression = new Dictionary<string, string> { ["*"] = "updated_at >= created_at" } }],
        };
        audited = edit?.Invoke(audited) ?? audited;
        b.Add(audited);
        return audited;
    }

    private static string Keyed(string tableKey, string memberId) => tableKey + "." + memberId;

    [Fact]
    public void Inherited_columns_follow_own_columns_with_their_member_origin()
    {
        var b = new ModelBuilder(seed: 880);
        var audited = Audited(b);
        var main = b.Database("main", Dialect.PostgreSql);
        var notes = b.Table("notes", main).Column("id", "uuid", nullable: false).Column("body", "text").PrimaryKey("id").Index(false, "body")
            .Stereotype("audited");
        var model = ResolutionKit.Resolve(b);
        var table = model.Db("main").Table("notes");
        var (created, updated) = (audited.Columns[0], audited.Columns[1]);

        Assert.Equal(["id", "body", "created_at", "updated_at"], table.Columns.Names());
        Assert.Equal([0, 1, 2, 3], table.Columns.Select(c => c.Position));
        var column = table.Column("created_at");
        Assert.Equal((Keyed(notes.Id, created.Id), Keyed(notes.Id, created.Id)), (column.Id, column.Key));
        Assert.Equal(("stereotype", "audited", created.Id), (column.MemberOrigin.Source, column.MemberOrigin.Stereotype!.Key, column.MemberOrigin.MemberId));
        Assert.Same(table.Stereotypes[0], column.MemberOrigin.Stereotype);
        Assert.Empty(column.MemberOrigin.Redefined);
        Assert.Equal((false, "CURRENT_TIMESTAMP"), (column.Nullable, column.DefaultSql));
        Assert.Null(column.Attribute);
        Assert.Same(model.ById[column.Id], column);
        // Own members share one origin.
        Assert.Equal("own", table.Column("id").MemberOrigin.Source);
        Assert.Same(table.Column("id").MemberOrigin, table.Column("body").MemberOrigin);
        Assert.Same(table.Column("id").MemberOrigin, table.PrimaryKey!.MemberOrigin);
        Assert.Null(table.Column("body").MemberOrigin.Stereotype);

        // Keys, indexes and checks follow the own ones and take their names from the conventions per table.
        Assert.Equal(["ix_notes_body", "ix_notes_created_at"], table.Indexes.Select(i => i.Name));
        var index = table.Indexes[1];
        Assert.Equal((Keyed(notes.Id, audited.Indexes[0].Id), audited.Indexes[0].Id), (index.Id, index.MemberOrigin.MemberId));
        Assert.Same(column, Assert.Single(index.Columns).Column);
        Assert.Equal("own", table.Indexes[0].MemberOrigin.Source);
        var check = Assert.Single(table.Checks);
        Assert.Equal(("ck_notes_1", Keyed(notes.Id, audited.Checks[0].Id), "updated_at"), (check.Name, check.Id, check.Column!.Name));
        Assert.Equal("stereotype", check.MemberOrigin.Source);
        Assert.Contains("e:" + audited.Id, table.Dependencies);
        Assert.DoesNotContain(model.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void A_redefined_column_keeps_its_key_and_lists_its_facets()
    {
        var b = new ModelBuilder(seed: 881);
        var audited = Audited(b);
        var created = audited.Columns[0];
        var main = b.Database("main", Dialect.PostgreSql);
        var notes = b.Table("notes", main).Column("id", "uuid", nullable: false).PrimaryKey("id").Stereotype("audited")
            .Edit(t => t with { Redefines = [new TableRedefinition { Member = created.Id, Nullable = true, Comment = "When the note was written." }] });
        var table = ResolutionKit.Resolve(b).Db("main").Table("notes");

        var column = table.Column("created_at");
        Assert.Equal(Keyed(notes.Id, created.Id), column.Key);
        Assert.True(column.Nullable);
        Assert.Equal("When the note was written.", column.Comment);
        Assert.Equal("CURRENT_TIMESTAMP", column.DefaultSql);
        Assert.Equal(["comment", "nullable"], column.MemberOrigin.Redefined);
        Assert.Empty(table.Column("updated_at").MemberOrigin.Redefined);
    }

    /// <summary>
    /// The design's child-row stereotype: a parent_id column and a foreign key whose target each carrier names in its parentTable
    /// parameter; tables parents and children (which names parents), and orphans, which carries the stereotype without the value.
    /// </summary>
    private static (ModelSnapshot Model, Stereotype ChildRow, string Parents, string Children, string Orphans) ChildRows()
    {
        var b = new ModelBuilder(seed: 882);
        b.Stereotype("child-row").AppliesTo("table")
            .Parameters("""{ "parentTable": { "type": "string", "x-ref": { "kind": "table", "sameDatabase": true } } }""", "parentTable");
        var main = b.Database("main", Dialect.PostgreSql);
        var parents = b.Table("parents", main).Column("id", "uuid", nullable: false).PrimaryKey("id");
        var children = b.Table("children", main).Column("id", "uuid", nullable: false).PrimaryKey("id").Stereotype("child-row")
            .Property("parentTable", parents.Id);
        var orphans = b.Table("orphans", main).Column("id", "uuid", nullable: false).PrimaryKey("id").Stereotype("child-row");
        string parentId = b.NewId(), foreignKey = b.NewId();
        var snapshot = Tests.Model.StereotypeDefaultsTests.Rebuild(b, e => e is Stereotype { Key: "child-row" } s
            ? s with
            {
                Columns = [new Column { Id = parentId, Name = "parent_id", Type = "uuid", Nullable = false }],
                ForeignKeys = [new MemberForeignKey { Id = foreignKey, Columns = [parentId], ReferencesParameter = "parentTable", OnDelete = ReferentialAction.Cascade }],
            }
            : e);
        return (snapshot, snapshot.GetStereotype("child-row")!, parents.Id, children.Id, orphans.Id);
    }

    [Fact]
    public void A_contributed_foreign_key_targets_the_carriers_parameter()
    {
        var (snapshot, childRow, parents, children, _) = ChildRows();
        var db = ResolutionKit.Resolve(snapshot).Db("main");

        var fk = Assert.Single(db.Table("children").ForeignKeys);
        Assert.Equal(("fk_children_parent_id", "parents", "cascade"), (fk.Name, fk.ReferencedTable.Name, fk.OnDelete));
        Assert.Equal(["parent_id"], fk.Columns.Names());
        Assert.Equal(["id"], fk.ReferencedColumns.Names());
        Assert.Equal(Keyed(children, childRow.ForeignKeys[0].Id), fk.Id);
        Assert.Equal(("stereotype", childRow.ForeignKeys[0].Id), (fk.MemberOrigin.Source, fk.MemberOrigin.MemberId));
        Assert.True(db.Table("children").Column("parent_id").IsForeignKey);
        Assert.Same(db.Table("parents"), fk.ReferencedTable);
        Assert.Equal(parents, fk.ReferencedTable.Id);
    }

    [Fact]
    public async Task A_carrier_without_the_parameter_gets_no_key_and_no_second_finding()
    {
        var (snapshot, _, _, _, orphans) = ChildRows();
        var model = ResolutionKit.Resolve(snapshot);

        var table = model.Db("main").Table("orphans");
        Assert.Empty(table.ForeignKeys);
        Assert.Equal(["id", "parent_id"], table.Columns.Names());
        Assert.DoesNotContain(model.Diagnostics, d => d.ElementId == orphans);
        var report = await Validation.ValidationFixture.Validator().ValidateAsync(snapshot, ValidationScope.All, null, TestContext.Current.CancellationToken);
        var finding = Assert.Single(report.Diagnostics, d => d.ElementId == orphans);
        Assert.Equal("MQ2018", finding.Rule);
    }

    [Fact]
    public void A_second_primary_key_is_MQ4076()
    {
        var b = new ModelBuilder(seed: 883);
        var keyed = Audited(b, s => s with
        {
            Columns = [.. s.Columns, new Column { Id = "01JC7AVD1TC0000000000000C9", Name = "row_id", Type = "uuid", Nullable = false }],
            PrimaryKey = new PrimaryKey { Id = "01JC7AVD1TC0000000000000K1", Columns = ["01JC7AVD1TC0000000000000C9"] },
        });
        var main = b.Database("main", Dialect.PostgreSql);
        var notes = b.Table("notes", main).Column("id", "uuid", nullable: false).PrimaryKey("id").Stereotype("audited");
        var logs = b.Table("logs", main).Column("line", "text").Stereotype("audited");
        var model = ResolutionKit.Resolve(b);
        var db = model.Db("main");

        // The table's own key stays; the inherited one is reported at the stereotype's entry.
        var finding = Assert.Single(model.Diagnostics, d => d.Rule == "MQ4076");
        Assert.Equal((notes.Id, "/stereotypes/0", DiagnosticSeverity.Error), (finding.ElementId, finding.JsonPointer, finding.Severity));
        Assert.Contains("its own primary key", finding.Message, StringComparison.Ordinal);
        Assert.Equal(["id"], db.Table("notes").PrimaryKey!.Columns.Names());
        Assert.Null(db.Table("notes").PrimaryKey!.Id);
        Assert.Equal("own", db.Table("notes").PrimaryKey!.MemberOrigin.Source);

        // A table without a key of its own takes the inherited one, with its keyed id and origin.
        var pk = db.Table("logs").PrimaryKey!;
        Assert.Equal(("pk_logs", Keyed(logs.Id, keyed.PrimaryKey!.Id!)), (pk.Name, pk.Id));
        Assert.Equal(["row_id"], pk.Columns.Names());
        Assert.Equal(("stereotype", keyed.PrimaryKey.Id), (pk.MemberOrigin.Source, pk.MemberOrigin.MemberId));
        Assert.True(db.Table("logs").Column("row_id").IsPrimaryKey);
    }

    [Fact]
    public void A_synthesized_table_skips_the_members_whose_attributes_it_stores()
    {
        var b = new ModelBuilder(seed: 884);
        var audited = Audited(b, s => s with
        {
            Attributes = [new ModelAttribute { Id = b.NewId(), Name = "createdAt", Type = new TypeRef { Builtin = "datetimeoffset" }, Required = true }],
        });
        var invoice = b.Entity("Invoice").Key("id", "uuid").Stereotype("audited");
        var product = b.Entity("Product").Key("id", "uuid");
        var main = b.Database("main", Dialect.PostgreSql);
        var billing = main.Schema("billing");
        main.DefaultSchema("billing");
        b.Mapping(main, invoice);
        b.Mapping(main, product);
        var snapshot = Tests.Model.StereotypeDefaultsTests.Rebuild(b, e => Tests.Model.StereotypeDefaultsTests.SchemaDefaults(e, billing, "audited"));
        var db = ResolutionKit.Resolve(snapshot).Db("main");

        // Invoice's table stores audited's attribute as a column and inherits no member; it still lists the stereotype.
        var invoices = db.Table("invoices");
        Assert.Equal(["id", "created_at"], invoices.Columns.Names());
        Assert.NotNull(invoices.Column("created_at").Attribute);
        Assert.Empty(invoices.Indexes);
        Assert.Empty(invoices.Checks);
        Assert.Contains(invoices.Stereotypes, s => s.Key == "audited");

        // Product's table inherits every member from the schema default, keyed by the synthesized table key.
        var products = db.Table("products");
        Assert.Equal(["id", "created_at", "updated_at"], products.Columns.Names());
        var created = products.Column("created_at");
        Assert.Equal(Keyed(product.Id + "@" + main.Id, audited.Columns[0].Id), created.Key);
        Assert.Equal(("schema-default", "schema-default"), (created.MemberOrigin.Source, created.MemberOrigin.Stereotype!.Source));
        Assert.Single(products.Indexes);
        Assert.Single(products.Checks);
    }

    [Fact]
    public void A_tph_root_table_skips_the_members_a_derived_entity_stores_through_attributes()
    {
        var b = new ModelBuilder(seed: 885);
        Audited(b, s => s with
        {
            Attributes = [new ModelAttribute { Id = b.NewId(), Name = "createdAt", Type = new TypeRef { Builtin = "datetimeoffset" } }],
        });
        var document = b.Entity("Document").Key("id", "uuid");
        b.Entity("Invoice").Base(document).Attr("total", "decimal").Stereotype("audited");
        var main = b.Database("main", Dialect.PostgreSql);
        var billing = main.Schema("billing");
        main.DefaultSchema("billing");
        b.Mapping(main, document);
        var snapshot = Tests.Model.StereotypeDefaultsTests.Rebuild(b, e => Tests.Model.StereotypeDefaultsTests.SchemaDefaults(e, billing, "audited"));
        var model = ResolutionKit.Resolve(snapshot);

        // The root entity does not carry audited, but its table stores the derived entity's audited attribute: no member.
        var documents = model.Db("main").Table("documents");
        Assert.Equal(1, documents.Columns.Count(c => c.Name == "created_at"));
        Assert.DoesNotContain(documents.Columns, c => c.Name == "updated_at");
        Assert.All(documents.Columns, c => Assert.Equal("own", c.MemberOrigin.Source));
        Assert.DoesNotContain(model.Diagnostics, d => d.Rule == "MQ4003");
    }

    [Fact]
    public void An_inherited_column_named_like_another_stereotypes_attribute_column_is_MQ4003()
    {
        var b = new ModelBuilder(seed: 886);
        Audited(b);
        b.Stereotype("timestamps").AppliesTo("entity").Attr("createdAt", "datetimeoffset");
        var invoice = b.Entity("Invoice").Key("id", "uuid").Stereotype("timestamps");
        var main = b.Database("main", Dialect.PostgreSql);
        var billing = main.Schema("billing");
        main.DefaultSchema("billing");
        b.Mapping(main, invoice);
        var snapshot = Tests.Model.StereotypeDefaultsTests.Rebuild(b, e => Tests.Model.StereotypeDefaultsTests.SchemaDefaults(e, billing, "audited"));
        var model = ResolutionKit.Resolve(snapshot);

        var finding = Assert.Single(model.Diagnostics, d => d.Rule == "MQ4003");
        Assert.Equal(invoice.Id, finding.ElementId);
        Assert.Contains("'created_at'", finding.Message, StringComparison.Ordinal);
        Assert.Contains("attribute 'createdAt'", finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_overlay_link_to_a_skipped_member_is_MQ2027_from_the_resolver()
    {
        var b = new ModelBuilder(seed: 887);
        var audited = Audited(b, s => s with
        {
            Attributes = [new ModelAttribute { Id = b.NewId(), Name = "createdAt", Type = new TypeRef { Builtin = "datetimeoffset" } }],
        });
        var created = audited.Columns[0];
        var invoice = b.Entity("Invoice").Key("id", "uuid").Stereotype("audited");
        var main = b.Database("main", Dialect.PostgreSql);
        b.Mapping(main, invoice);
        var key = invoice.Id + "@" + main.Id;
        var overlay = b.Table("invoices", main).OverlayFor(invoice).Stereotype("audited").Edit(t => t with
        {
            Redefines = [new TableRedefinition { Member = created.Id, Nullable = true }],
            Indexes = [new TableIndex { Id = "01JC7AVD1TC0000000000000X9", Columns = [new IndexColumn { Column = Keyed(key, created.Id) }] }],
        });
        var model = ResolutionKit.Resolve(b);

        var link = Assert.Single(model.Diagnostics, d => d.Rule == "MQ2027");
        Assert.Equal((overlay.Id, "/redefines/0/member"), (link.ElementId, link.JsonPointer));
        var missing = Assert.Single(model.Diagnostics, d => d.Rule == "MQ4008");
        Assert.Equal((overlay.Id, "/indexes/0/columns/0/column"), (missing.ElementId, missing.JsonPointer));
        Assert.Empty(model.Db("main").Table("invoices").Indexes);
    }

    [Fact]
    public void A_not_null_member_without_a_default_on_a_synthesized_table_is_MQ4079()
    {
        var b = new ModelBuilder(seed: 888);
        Audited(b, s => s with { Columns = [.. s.Columns, new Column { Id = "01JC7AVD1TC0000000000000C8", Name = "row_version", Type = "int64", Nullable = false }] });
        var product = b.Entity("Product").Key("id", "uuid");
        var main = b.Database("main", Dialect.PostgreSql);
        var billing = main.Schema("billing");
        main.DefaultSchema("billing");
        b.Mapping(main, product);
        b.Table("notes", main).Column("id", "uuid", nullable: false).PrimaryKey("id");
        var snapshot = Tests.Model.StereotypeDefaultsTests.Rebuild(b, e => Tests.Model.StereotypeDefaultsTests.SchemaDefaults(e, billing, "audited"));
        var model = ResolutionKit.Resolve(snapshot);

        // created_at has a default; row_version has none, and a designed table (notes) is not reported: its rows are written as seeds.
        var finding = Assert.Single(model.Diagnostics, d => d.Rule == "MQ4079");
        Assert.Equal((product.Id, DiagnosticSeverity.Warning), (finding.ElementId, finding.Severity));
        Assert.Contains("'row_version'", finding.Message, StringComparison.Ordinal);
        Assert.Contains("entity 'Product'", finding.Message, StringComparison.Ordinal);
        Assert.Equal(["id", "created_at", "updated_at", "row_version"], model.Db("main").Table("notes").Columns.Names());
    }

    [Fact]
    public void A_clash_between_defaults_on_a_table_without_a_file_goes_on_its_entity()
    {
        // next-phase-2-design.md section 3.1 (D7, decision 29): no file's validation judges the set a table without a file takes from
        // its schema's and package's defaults, so the resolver reports the clash on its entity at /stereotypes, naming both defaults.
        var b = new ModelBuilder(seed: 889);
        b.Stereotype("hot").AppliesTo("table").Group("temperature");
        b.Stereotype("cold").AppliesTo("table").Group("temperature");
        var billing = b.Package("Billing");
        var invoice = b.Entity("Invoice", billing).Key("id", "uuid");
        var main = b.Database("main", Dialect.PostgreSql);
        var sales = main.Schema("sales");
        main.DefaultSchema("sales");
        b.Mapping(main, invoice);
        var snapshot = Tests.Model.StereotypeDefaultsTests.Rebuild(b, e => e is Package p && p.Id == billing.Id
            ? p with { DefaultStereotypes = ["cold"] }
            : Tests.Model.StereotypeDefaultsTests.SchemaDefaults(e, sales, "hot"));
        var model = ResolutionKit.Resolve(snapshot);

        var finding = Assert.Single(model.Diagnostics, d => d.Rule == "MQ2013");
        Assert.Equal((invoice.Id, "/stereotypes"), (finding.ElementId, finding.JsonPointer));
        Assert.Contains("Table 'invoices' of database 'main', which has no table file", finding.Message, StringComparison.Ordinal);
        Assert.Contains("schema 'sales'", finding.Message, StringComparison.Ordinal);
        Assert.Contains("package 'Billing'", finding.Message, StringComparison.Ordinal);
        Assert.Equal(["hot", "cold"], model.Db("main").Table("invoices").Stereotypes.Select(s => s.Key));
    }

    private sealed class SyncProgress(Action<ProgressUpdate> report) : IProgress<ProgressUpdate>
    {
        public void Report(ProgressUpdate value) => report(value);
    }
}
