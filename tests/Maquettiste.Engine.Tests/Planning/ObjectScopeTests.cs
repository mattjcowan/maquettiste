using System.Collections.Immutable;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Pipeline;
using Maquettiste.Engine.Planning;
using Maquettiste.Engine.Resolution;
using Maquettiste.Engine.Scripting;
using Maquettiste.Testing;
using static Maquettiste.Engine.Tests.Planning.PlanningKit;

namespace Maquettiste.Engine.Tests.Planning;

/// <summary>
/// The <c>each view</c> and <c>each sequence</c> scopes (engine-design.md section 7.0a, erratum E38): one unit per view or sequence of
/// every database, the key sequences the resolver creates included, keyed by the object's id; <c>generation.skip</c> and <c>where</c>
/// (tags, stereotypes, categories, database) read the object's own file. The <c>each database</c> and <c>each attribute</c> scopes
/// (next-phase-1-design.md section 5.5): one unit per database, filtered on the database file; one unit per declared attribute at its
/// declaring owner, filtered on its own tags and stereotypes and its owner's package and databases.
/// </summary>
public sealed class ObjectScopeTests
{
    private static readonly ResolvedModel Model = Build();
    private static readonly ResolvedModel Scoped = BuildScoped();

    private static ResolvedModel Build()
    {
        var b = new ModelBuilder(seed: 71);
        b.Stereotype("ledger").AppliesTo("view", "sequence");
        var receivables = b.Category("Receivables");
        b.Entity("Counter").Key("id", "int64", IdentityStrategy.Sequence);
        var main = b.Database("main", Dialect.PostgreSql);
        var reporting = b.Database("reporting", Dialect.SqlServer);
        View(b, "open_ledger", main.Id, v => v with { Tags = ["finance"], Stereotypes = ["ledger"], Category = receivables });
        View(b, "monthly_totals", reporting.Id, v => v);
        View(b, "scratch", main.Id, v => v with { Generation = new Dictionary<string, GenerationHints> { ["p"] = new() { Skip = true } } });
        b.Add(new Sequence { Id = b.NewId(), Name = "posting_seq", Database = main.Id, Tags = ["finance"] });
        return Resolve(b.Build());
    }

    /// <summary>
    /// Packages Sales and Catalog; entities Party (Sales, abstract: id, name tagged pii), Customer (Sales, derives from Party: email,
    /// stereotype audited with the virtual createdAt), Product (Catalog: sku, title with stereotype label, ignored in reporting); value
    /// object Money (Catalog: amount, currency); relation buys (Customer to Product: quantity, audited), promoted; databases main
    /// (tag primary, stereotype ops, category Finance), reporting and scratch (package Catalog, <c>generation["p"].skip</c>).
    /// </summary>
    private static ResolvedModel BuildScoped()
    {
        var b = new ModelBuilder(seed: 72);
        var sales = b.Package("Sales");
        var catalog = b.Package("Catalog");
        var finance = b.Category("Finance");
        b.Stereotype("audited").Attr("createdAt", "datetime");
        b.Stereotype("label");
        b.Stereotype("ops").AppliesTo("database");
        var party = b.Entity("Party", sales).Abstract().Key("id", "uuid").Attr("name", "string", a => a.Tag("pii"));
        var customer = b.Entity("Customer", sales).Base(party).Attr("email", "string").Stereotype("audited");
        var product = b.Entity("Product", catalog).Key("sku", "string").Attr("title", "string", a => a.Stereotype("label"));
        b.ValueObject("Money", catalog).Attr("amount", "decimal").Attr("currency", "string");
        b.Relation("buys", customer, product, fromRole: "buyer", toRole: "item").Attr("quantity", "int32").Stereotype("audited");
        b.Settings(s => s with { Conventions = s.Conventions with { RelationsWithAttributes = RelationShape.Promoted } });
        b.Database("main", Dialect.PostgreSql).Tag("primary").Stereotype("ops").Category(finance);
        b.Mapping(b.Database("reporting", Dialect.SqlServer), product).Ignore();
        b.Add(new Database
        {
            Id = b.NewId(),
            Name = "scratch",
            Dialect = Dialect.Sqlite,
            Package = catalog.Id,
            Generation = new Dictionary<string, GenerationHints> { ["p"] = new() { Skip = true } },
        });
        return Resolve(b.Build());
    }

    private static void View(ModelBuilder b, string name, string database, Func<View, View> change) =>
        b.Add(change(new View { Id = b.NewId(), Name = name, Database = database, Body = ImmutableDictionary<string, string>.Empty.Add("*", "select 1") }));

    [Fact]
    public async Task Each_view_and_each_sequence_plan_one_unit_per_object_of_every_database()
    {
        var plan = await Plan(Pack("p", [Unit("view", "each view"), Unit("sequence", "each sequence")]));

        Assert.Empty(plan.Diagnostics);
        var views = Model.Databases.SelectMany(d => d.Views).ToList();
        Assert.Equal(3, views.Count);
        // The scratch view's hint names pack "p": this pack plans the other two, keyed by the view's id.
        Assert.Equal(["monthly_totals", "open_ledger"], Names(plan, "view"));
        Assert.Contains(plan.Units, u => u.Key == "p/view:" + views.Single(v => v.Name == "open_ledger").Id);
        // The sequence file and the key sequence the resolver creates for Counter in each database.
        Assert.Equal(["counters_seq", "counters_seq", "posting_seq"], Names(plan, "sequence"));
        Assert.All(plan.Units.Where(u => u.Unit.Id == "sequence"), u => Assert.IsType<RSequence>(u.Element));

        var other = await Plan(Pack("q", [Unit("view", "each view")]));
        Assert.Equal(["monthly_totals", "open_ledger", "scratch"], Names(other, "view"));
    }

    [Theory]
    [InlineData("each view", "tags", "finance", "open_ledger")]
    [InlineData("each view", "notTags", "finance", "monthly_totals")]
    [InlineData("each view", "stereotypes", "ledger", "open_ledger")]
    [InlineData("each view", "categories", "Receivables", "open_ledger")]
    [InlineData("each view", "database", "reporting", "monthly_totals")]
    [InlineData("each sequence", "database", "main", "counters_seq,posting_seq")]
    [InlineData("each sequence", "tags", "finance", "posting_seq")]
    public async Task Where_filters_views_and_sequences_by_their_own_files(string scope, string filter, string value, string expected)
    {
        var where = filter switch
        {
            "tags" => new UnitWhere { Tags = [value] },
            "notTags" => new UnitWhere { NotTags = [value] },
            "stereotypes" => new UnitWhere { Stereotypes = [value] },
            "categories" => new UnitWhere { Categories = [value] },
            _ => new UnitWhere { Database = value },
        };
        var plan = await Plan(Pack("p", [Unit("u", scope, where)]));

        Assert.Empty(plan.Diagnostics);
        Assert.Equal(expected, string.Join(",", Names(plan, "u")));
    }

    [Fact]
    public void Explain_names_the_hint_and_the_filter()
    {
        var pack = Pack("p", [Unit("view", "each view"), Unit("tagged", "each view", new UnitWhere { Tags = ["finance"] })]);
        var views = Model.Databases.SelectMany(d => d.Views).ToDictionary(v => v.Name, StringComparer.Ordinal);

        Assert.Equal("skip-hint", UnitPlanner.WhyNot(Model, pack, pack.Manifest.Units[0], views["scratch"], new ScriptSandboxFactory(), Ct)?.Kind);
        Assert.Null(UnitPlanner.WhyNot(Model, pack, pack.Manifest.Units[0], views["open_ledger"], new ScriptSandboxFactory(), Ct));
        var filtered = UnitPlanner.WhyNot(Model, pack, pack.Manifest.Units[1], views["monthly_totals"], new ScriptSandboxFactory(), Ct);
        Assert.Equal("filter", filtered?.Kind);
        Assert.Contains("where.tags: finance", filtered?.Detail, StringComparison.Ordinal);
        var sequence = Model.Databases.SelectMany(d => d.Sequences).First();
        Assert.Equal("scope", UnitPlanner.WhyNot(Model, pack, pack.Manifest.Units[0], sequence, new ScriptSandboxFactory(), Ct)?.Kind);
    }

    [Fact]
    public async Task Each_database_plans_one_unit_per_database_with_hints_and_where()
    {
        var plan = await Plan(Scoped, Pack("p", [Unit("db", "each database")]));

        Assert.Empty(plan.Diagnostics);
        // The scratch database's hint names pack "p": this pack plans the other two, keyed by the database's id.
        Assert.Equal(["main", "reporting"], Names(plan, "db"));
        Assert.Contains(plan.Units, u => u.Key == "p/db:" + Scoped.Databases.Single(d => d.Name == "main").Id);
        Assert.All(plan.Units, u => Assert.IsType<RDatabase>(u.Element));
        var other = await Plan(Scoped, Pack("q", [Unit("db", "each database")]));
        Assert.Equal(["main", "reporting", "scratch"], Names(other, "db"));

        // where reads the database file: its tags, stereotypes, category and package; database matches by name.
        (UnitWhere Where, string Expected)[] filters =
        [
            (new UnitWhere { Tags = ["primary"] }, "main"),
            (new UnitWhere { NotTags = ["primary"] }, "reporting,scratch"),
            (new UnitWhere { Stereotypes = ["ops"] }, "main"),
            (new UnitWhere { Categories = ["Finance"] }, "main"),
            (new UnitWhere { Packages = ["Catalog"] }, "scratch"),
            (new UnitWhere { Database = "reporting" }, "reporting"),
        ];
        foreach (var (where, expected) in filters)
            Assert.Equal(expected, string.Join(",", Names(await Plan(Scoped, Pack("q", [Unit("db", "each database", where)])), "db")));

        var pack = Pack("p", [Unit("db", "each database")]);
        var scratch = Scoped.Databases.Single(d => d.Name == "scratch");
        Assert.Equal("skip-hint", UnitPlanner.WhyNot(Scoped, pack, pack.Manifest.Units[0], scratch, new ScriptSandboxFactory(), Ct)?.Kind);
    }

    [Fact]
    public async Task Each_attribute_plans_declared_attributes_once_without_inherited_virtual_or_promoted_copies()
    {
        var plan = await Plan(Scoped, Pack("p", [Unit("a", "each attribute")]));

        Assert.Empty(plan.Diagnostics);
        var attributes = plan.Units.Select(u => Assert.IsType<RAttribute>(u.Element)).ToList();
        Assert.Equal(["amount", "currency", "email", "id", "name", "quantity", "sku", "title"], Names(plan, "a"));
        Assert.Equal(attributes.Count, plan.Units.Select(u => u.Key).Distinct(StringComparer.Ordinal).Count());
        Assert.All(attributes, a => Assert.False(a.IsInherited || a.IsVirtual, a.Name));
        // Each at its declaring owner: Party's name (not Customer's inherited copy), the relation's quantity (not the promoted
        // entity's copy under the same id), Money's amount.
        Assert.Equal("Party", ((REntity)attributes.Single(a => a.Name == "name").Owner).Name);
        Assert.IsType<RRelation>(attributes.Single(a => a.Name == "quantity").Owner);
        Assert.IsType<RValueObject>(attributes.Single(a => a.Name == "amount").Owner);
        Assert.Contains(plan.Units, u => u.Key == "p/a:" + attributes.Single(a => a.Name == "email").Id);

        // The copies the scope leaves out are in the model.
        var customer = Scoped.Entities.Single(e => e.Name == "Customer");
        Assert.Contains(customer.Attributes, a => a.IsInherited && a.Name == "name");
        Assert.Contains(customer.Attributes, a => a.IsVirtual && a.Name == "createdAt");
        var promoted = Scoped.Entities.Single(e => e.IsPromoted);
        Assert.Contains(promoted.Attributes, a => a.Name == "quantity");
        Assert.Contains(Scoped.Relations.Single(r => r.Name == "buys").Attributes, a => a.IsVirtual);
    }

    [Theory]
    [InlineData("tags", "pii", "name")]
    [InlineData("notTags", "pii", "amount,currency,email,id,quantity,sku,title")]
    [InlineData("stereotypes", "label", "title")]
    [InlineData("packages", "Catalog", "amount,currency,sku,title")]
    [InlineData("packages", "Sales", "email,id,name")]
    [InlineData("database", "reporting", "amount,currency,email,id,name")]
    public async Task Each_attribute_filters_on_its_own_tags_and_its_owners_package_and_database(string filter, string value, string expected)
    {
        var where = filter switch
        {
            "tags" => new UnitWhere { Tags = [value] },
            "notTags" => new UnitWhere { NotTags = [value] },
            "stereotypes" => new UnitWhere { Stereotypes = [value] },
            "packages" => new UnitWhere { Packages = [value] },
            _ => new UnitWhere { Database = value },
        };
        var plan = await Plan(Scoped, Pack("p", [Unit("a", "each attribute", where)]));

        Assert.Empty(plan.Diagnostics);
        Assert.Equal(expected, string.Join(",", Names(plan, "a")));
    }

    private static IReadOnlyList<string> Names(UnitPlan plan, string unit) =>
        [.. plan.Units.Where(u => u.Unit.Id == unit).Select(u => u.Element switch
        {
            RView v => v.Name, RSequence s => s.Name, RDatabase d => d.Name, RAttribute a => a.Name, _ => "?",
        }).Order(StringComparer.Ordinal)];

    private static Task<UnitPlan> Plan(LoadedPack pack) => Plan(Model, pack);

    private static Task<UnitPlan> Plan(ResolvedModel model, LoadedPack pack) =>
        new UnitPlanner(Options).PlanAsync(model, new PackSet([pack], []), new ScriptSandboxFactory(), null, Ct);
}
