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

    private sealed class SyncProgress(Action<ProgressUpdate> report) : IProgress<ProgressUpdate>
    {
        public void Report(ProgressUpdate value) => report(value);
    }
}
