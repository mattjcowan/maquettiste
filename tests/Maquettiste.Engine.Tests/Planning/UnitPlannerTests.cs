using System.Text.Json;
using Maquettiste.Engine.Hashing;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Pipeline;
using Maquettiste.Engine.Planning;
using Maquettiste.Engine.Resolution;
using Maquettiste.Engine.Scripting;
using Maquettiste.Testing;
using static Maquettiste.Engine.Tests.Planning.PlanningKit;
using Defaults = Maquettiste.Engine.Tests.Model.StereotypeDefaultsTests;

namespace Maquettiste.Engine.Tests.Planning;

public sealed class UnitPlannerTests
{
    private static readonly ResolvedModel Model = Resolve(Shop().Build());

    [Fact]
    public async Task Scopes_expand_to_one_unit_per_element_with_keys_in_pack_then_key_order()
    {
        var packs = new PackSet([
            Pack("b", [Unit("model", "model"), Unit("entity", "each entity"), Unit("package", "each package"), Unit("table", "each table")], order: 1),
            Pack("a", [Unit("enum", "each enum"), Unit("vo", "each value object"), Unit("rel", "each relation")], order: 0)], []);

        var plan = await Plan(packs);

        Assert.Empty(plan.Diagnostics);
        Assert.Contains("b/model", plan.Units.Select(u => u.Key));
        Assert.Null(plan.Units.Single(u => u.Key == "b/model").Element);
        Assert.Equal(4, plan.Units.Count(u => u.Unit.Id == "entity")); // Hidden is skipped by its generation hint
        Assert.Equal(3, plan.Units.Count(u => u.Unit.Id == "package"));
        Assert.Equal(Model.Databases.Sum(d => d.Tables.Count), plan.Units.Count(u => u.Unit.Id == "table"));
        Assert.DoesNotContain(plan.Units, u => u.Pack.Name == "a");
        Assert.Equal(plan.Units.OrderBy(u => u.Pack.Order).ThenBy(u => u.Key, StringComparer.Ordinal).Select(u => u.Key), plan.Units.Select(u => u.Key));
        var customer = Entity("Customer");
        Assert.Contains(plan.Units, u => u.Key == "b/entity:" + customer.Id && ReferenceEquals(u.Element, customer));
        Assert.Equal(plan.Units.Count, plan.Units.Select(u => u.StaticHash).Distinct().Count());
    }

    [Theory]
    [InlineData("tags", "pii", "Customer")]
    [InlineData("stereotypes", "audited", "Customer")]
    [InlineData("categories", "Core", "Customer")]
    [InlineData("packages", "Sales", "Customer,Order")]
    [InlineData("packages", "Sales.Orders", "Order")]
    public async Task Where_filters_match_any_value_and_descendants(string filter, string value, string expected)
    {
        var where = filter switch
        {
            "tags" => new UnitWhere { Tags = [value] },
            "stereotypes" => new UnitWhere { Stereotypes = [value] },
            "categories" => new UnitWhere { Categories = [value] },
            _ => new UnitWhere { Packages = [value] },
        };
        Assert.Equal(expected, await Names(where));
    }

    [Fact]
    public async Task Where_negative_filters_exclude()
    {
        Assert.Equal("Customer,Order,Party", await Names(new UnitWhere { NotTags = ["external"] }));
        Assert.Equal("Order,Party,Product", await Names(new UnitWhere { NotStereotypes = ["audited"] }));
        Assert.Equal("Customer,Order,Party", await Names(new UnitWhere { NotPackages = ["Catalog"] }));
        Assert.Equal("Customer,Order,Party", await Names(new UnitWhere { NotPackages = [Model.Packages.Single(p => p.Name == "Catalog").Id] }));
    }

    [Fact]
    public async Task Where_abstract_and_database()
    {
        Assert.Equal("Party", await Names(new UnitWhere { Abstract = true }));
        Assert.Equal("Customer,Order,Product", await Names(new UnitWhere { Abstract = false }));
        var mapped = await Names(new UnitWhere { Database = "main" });
        Assert.Contains("Customer", mapped, StringComparison.Ordinal);
        Assert.DoesNotContain("Product", mapped, StringComparison.Ordinal);
        Assert.Equal("", await Names(new UnitWhere { Database = "elsewhere" }));
    }

    [Fact]
    public async Task Where_database_on_tables_and_model_units()
    {
        var plan = await Plan(new PackSet([Pack("p", [Unit("t", "each table", new UnitWhere { Database = "main", NotTags = ["external"] }),
            Unit("m", "model", new UnitWhere { Database = "main" }), Unit("none", "model", new UnitWhere { Database = "other" })])], []));
        var tables = plan.Units.Where(u => u.Unit.Id == "t").Select(u => ((RTable)u.Element!).Name).ToList();
        Assert.Contains("customers", tables);
        Assert.Contains("p/m", plan.Units.Select(u => u.Key));
        Assert.DoesNotContain("p/none", plan.Units.Select(u => u.Key));
    }

    [Fact]
    public async Task A_selector_and_a_script_filter_run_in_the_sandbox()
    {
        var script = Script("""
            maquettiste.selector('tagged', model => model.entities.filter(e => e.tags.length > 0));
            maquettiste.selector('ids', model => ['01ARZ3NDEKTSV4RRFFQ69G5FAV', model.entities.find(e => e.name === 'Customer').id]);
            maquettiste.filter('short', e => e.name.length <= 5);
            """);
        var pack = Pack("p", [Unit("sel", "select tagged"), Unit("filtered", "each entity", new UnitWhere { Script = "short" }), Unit("bad", "select ids")], [script]);

        var plan = await Plan(new PackSet([pack], []));

        Assert.Equal(["Customer", "Product"], plan.Units.Where(u => u.Unit.Id == "sel").Select(u => ((REntity)u.Element!).Name).Order(StringComparer.Ordinal));
        Assert.Equal(["Order", "Party"], plan.Units.Where(u => u.Unit.Id == "filtered").Select(u => ((REntity)u.Element!).Name).Order(StringComparer.Ordinal));
        var unknown = Assert.Single(plan.Diagnostics);
        Assert.Equal("MQ6017", unknown.Rule);
        Assert.Equal("/units/2/for", unknown.JsonPointer);
        Assert.Single(plan.Units, u => u.Unit.Id == "bad");
    }

    [Fact]
    public async Task Script_errors_are_diagnostics()
    {
        var pack = Pack("p", [Unit("sel", "select missing"), Unit("f", "each entity", new UnitWhere { Script = "boom" })],
            [Script("maquettiste.filter('boom', e => { throw new Error('no'); });")]);
        var plan = await Plan(new PackSet([pack], []));
        Assert.Empty(plan.Units);
        Assert.All(plan.Diagnostics, d => Assert.Equal("MQ6016", d.Rule));
        Assert.NotEmpty(plan.Diagnostics);

        var broken = await Plan(new PackSet([Pack("q", [Unit("sel", "select x")], [Script("this is not javascript")])], []));
        Assert.Contains(broken.Diagnostics, d => d.Rule == "MQ6016");
    }

    [Fact]
    public async Task Static_hash_depends_on_the_unit_parameters_formatters_and_key()
    {
        var unit = Unit("u", "each entity");
        var pack = Pack("p", [unit]);
        var a = UnitPlanner.StaticHash(pack, unit, [], "p/u:1");
        Assert.Equal(a, UnitPlanner.StaticHash(pack, unit, [], "p/u:1"));
        Assert.NotEqual(a, UnitPlanner.StaticHash(pack, unit, [], "p/u:2"));
        Assert.NotEqual(a, UnitPlanner.StaticHash(pack, unit with { Output = "out/y" }, [], "p/u:1"));
        Assert.NotEqual(a, UnitPlanner.StaticHash(pack with { ScriptsHash = "other" }, unit, [], "p/u:1"));
        Assert.NotEqual(a, UnitPlanner.StaticHash(pack with { Settings = new PackSettings { Output = "gen" } }, unit, [], "p/u:1"));
        var formatter = new FormatterSettings { Name = "fmt", Extensions = [".x"], Command = "fmt", Version = "1" };
        Assert.NotEqual(a, UnitPlanner.StaticHash(pack, unit, [formatter], "p/u:1"));
        Assert.Equal(UnitPlanner.StaticHash(pack, unit with { Formatter = "none" }, [], "p/u:1"),
            UnitPlanner.StaticHash(pack, unit with { Formatter = "none" }, [formatter], "p/u:1"));
    }

    [Fact]
    public void Static_hash_is_the_section_11_fields_hashed_with_the_hash_builder()
    {
        using var repo = new TempRepo();
        repo.WriteFile("pack/t.tpl", "template");
        var unit = Unit("u", "each entity");
        var pack = Pack("p", [unit]) with { RootPath = repo.PathOf("pack") };
        var formatter = new FormatterSettings { Name = "fmt", Extensions = [".x"], Command = "fmt", Version = "1" };
        foreach (var (u, formatters) in new (PackUnit, FormatterSettings[])[] { (unit, []), (unit, [formatter]), (unit with { Formatter = "fmt" }, [formatter]) })
        {
            var formatterField = u.Formatter switch
            {
                "none" => "none",
                null => formatters.Length == 0 ? "none" : CanonicalForm.Json(formatters),
                var name => CanonicalForm.Json(formatters.First(f => f.Name == name)),
            };
            var templates = HashBuilder.Of(u.Template, PackFiles.Hash(pack.RootPath, u.Template!) ?? DependencyHasher.Absent, null, null);
            // A unit outside mode copy hashes as it did before copy units: its empty 'sources' and 'tokens' are left out.
            var unitJson = JsonSerializer.SerializeToNode(u, EngineJson.Options)!.AsObject();
            Assert.True(unitJson.Remove("sources") && unitJson.Remove("tokens"));
            var expected = HashBuilder.Of("mq-unit-1", EngineVersion.Value, pack.Name, pack.Manifest.Version,
                CanonicalForm.Json(JsonSerializer.SerializeToElement(unitJson, EngineJson.Options)), pack.ScriptsHash,
                CanonicalForm.Json(pack.Parameters), pack.Settings.Output, formatterField, templates, "p/u:é" + new string('x', 2000));
            Assert.Equal(expected, UnitPlanner.StaticHash(pack, u, formatters, "p/u:é" + new string('x', 2000)));
        }
    }

    [Fact]
    public async Task Generation_skip_on_a_table_file_drops_its_table_units()
    {
        var b = new ModelBuilder(seed: 5);
        var customer = b.Entity("Customer").Key("id", "uuid");
        var order = b.Entity("Order").Key("id", "uuid");
        var main = b.Database("main", Dialect.PostgreSql);
        var colId = b.NewId();
        b.Add(new Table
        {
            Id = b.NewId(), Name = "legacy", Database = main.Id, Columns = [new Column { Id = colId, Name = "id", Type = "int" }],
            Generation = new Dictionary<string, GenerationHints> { ["*"] = new() { Skip = true } },
        });
        b.Add(new Table
        {
            Id = b.NewId(), Database = main.Id, Origin = TableOrigin.Synthesized, Entity = customer.Id, Name = "clients",
            Generation = new Dictionary<string, GenerationHints> { ["p"] = new() { Skip = true } },
        });
        _ = order;
        var model = Resolve(b.Build());
        Assert.Equal(["clients", "legacy", "orders"], model.Databases.Single().Tables.Select(t => t.Name).Order(StringComparer.Ordinal));

        var packs = new PackSet([Pack("p", [Unit("t", "each table")]), Pack("q", [Unit("t", "each table")], order: 1)], []);
        var plan = await new UnitPlanner(Options).PlanAsync(model, packs, new ScriptSandboxFactory(), null, Ct);

        Assert.Empty(plan.Diagnostics);
        // "*" drops the designed table for every pack; "p" drops the overlaid synthesized table for pack p only.
        Assert.Equal(["orders"], plan.Units.Where(u => u.Pack.Name == "p").Select(u => ((RTable)u.Element!).Name));
        Assert.Equal(["clients", "orders"], plan.Units.Where(u => u.Pack.Name == "q").Select(u => ((RTable)u.Element!).Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task A_table_file_package_wins_over_its_entitys()
    {
        // next-phase-1-design.md section 4.3: a table's own package wins; a table without one is in its entity's.
        var b = new ModelBuilder(seed: 6);
        var sales = b.Package("Sales");
        var catalog = b.Package("Catalog");
        var customer = b.Entity("Customer", sales).Key("id", "uuid");
        b.Entity("Order", sales).Key("id", "uuid");
        var main = b.Database("main", Dialect.PostgreSql);
        b.Add(new Table { Id = b.NewId(), Database = main.Id, Origin = TableOrigin.Synthesized, Entity = customer.Id, Name = "clients", Package = catalog.Id });
        b.Add(new Table { Id = b.NewId(), Name = "legacy", Database = main.Id, Package = catalog.Id, Columns = [new Column { Id = b.NewId(), Name = "id", Type = "int" }] });
        b.Add(new Table { Id = b.NewId(), Name = "loose", Database = main.Id, Columns = [new Column { Id = b.NewId(), Name = "id", Type = "int" }] });
        var model = Resolve(b.Build());
        var packages = model.Databases.Single().Tables.ToDictionary(t => t.Name, t => t.Package?.Name, StringComparer.Ordinal);
        Assert.Equal("Catalog", packages["clients"]);
        Assert.Equal("Sales", packages["orders"]);
        Assert.Equal("Catalog", packages["legacy"]);
        Assert.Null(packages["loose"]);

        var packs = new PackSet([Pack("p", [Unit("c", "each table", new UnitWhere { Packages = ["Catalog"] }),
            Unit("s", "each table", new UnitWhere { Packages = ["Sales"] }), Unit("n", "each table", new UnitWhere { NotPackages = ["Sales", "Catalog"] })])], []);
        var plan = await new UnitPlanner(Options).PlanAsync(model, packs, new ScriptSandboxFactory(), null, Ct);

        Assert.Empty(plan.Diagnostics);
        string[] Tables(string unit) => [.. plan.Units.Where(u => u.Unit.Id == unit).Select(u => ((RTable)u.Element!).Name).Order(StringComparer.Ordinal)];
        Assert.Equal(["clients", "legacy"], Tables("c"));
        Assert.Equal(["orders"], Tables("s"));
        Assert.Equal(["loose"], Tables("n"));
    }

    [Fact]
    public async Task Where_stereotypes_matches_a_table_that_a_schema_default_reaches()
    {
        // next-phase-2-design.md section 3.1: the filter reads the resolved table's stereotypes, so a synthesized table without a file
        // that a schema default reaches is selected, and a table the default does not reach is not.
        var b = new ModelBuilder(seed: 7);
        b.Stereotype("audited").AppliesTo("table");
        var billing = b.Package("Billing");
        b.Entity("Invoice", billing).Key("id", "uuid");
        var customer = b.Entity("Customer", billing).Key("id", "uuid");
        var main = b.Database("main", Dialect.PostgreSql);
        var sales = main.Schema("sales");
        var common = main.Schema("public");
        main.Package(billing, sales);
        b.Mapping(main, customer).Schema(common);
        b.Table("ledger", main).Column("id", "uuid").Edit(t => t with { Schema = sales });
        var model = Resolve(Defaults.Rebuild(b, e => Defaults.SchemaDefaults(e, sales, "audited")));

        var packs = new PackSet([Pack("p", [Unit("a", "each table", new UnitWhere { Stereotypes = ["audited"] }),
            Unit("n", "each table", new UnitWhere { NotStereotypes = ["audited"] })])], []);
        var plan = await new UnitPlanner(Options).PlanAsync(model, packs, new ScriptSandboxFactory(), null, Ct);

        Assert.Empty(plan.Diagnostics);
        string[] Tables(string unit) => [.. plan.Units.Where(u => u.Unit.Id == unit).Select(u => ((RTable)u.Element!).Name).Order(StringComparer.Ordinal)];
        Assert.Equal(["invoices", "ledger"], Tables("a"));
        Assert.Equal(["customers"], Tables("n"));
    }

    [Fact]
    public void Static_hash_depends_on_the_pack_version()
    {
        var unit = Unit("u", "each entity");
        var pack = Pack("p", [unit]);
        Assert.NotEqual(UnitPlanner.StaticHash(pack, unit, [], "p/u:1"),
            UnitPlanner.StaticHash(pack with { Manifest = pack.Manifest with { Version = "1.0.1" } }, unit, [], "p/u:1"));
    }

    private static REntity Entity(string name) => Model.Entities.Single(e => e.Name == name);

    private static Task<UnitPlan> Plan(PackSet packs) => new UnitPlanner(Options).PlanAsync(Model, packs, new ScriptSandboxFactory(), null, Ct);

    [Fact]
    public async Task Each_operation_renders_once_per_operation_and_filters_by_package_and_stereotype()
    {
        // Shop's ids are seeded, so a second Shop() names Customer and Product with the ids Model has.
        var b = Shop();
        b.Add(Operation("01JR5NDP000000000000000001", "archive", Entity("Customer").Id, "audited"));
        b.Add(Operation("01JR5NDP000000000000000003", "archive", Entity("Product").Id));
        b.Add(Operation("01JR5NDP000000000000000005", "ping", null));
        var model = Resolve(b.Build());
        Task<UnitPlan> PlanOps(UnitWhere? where) =>
            new UnitPlanner(Options).PlanAsync(model, new PackSet([Pack("p", [Unit("op", "each operation", where)])], []), new ScriptSandboxFactory(), null, Ct);

        var all = await PlanOps(null);

        Assert.Empty(all.Diagnostics);
        Assert.Equal(3, model.Operations.Count);
        Assert.Equal(model.Operations.Select(o => "p/op:" + o.Id).Order(StringComparer.Ordinal), all.Units.Select(u => u.Key));
        Assert.All(all.Units, u => Assert.Contains(Assert.IsType<ROperation>(u.Element), model.Operations));
        string Subjects(UnitPlan plan) => string.Join(",", plan.Units.Select(u => ((ROperation)u.Element!).Subject?.Name ?? "-").Order(StringComparer.Ordinal));
        Assert.Equal("Customer", Subjects(await PlanOps(new UnitWhere { Packages = ["Sales"] }))); // the subject's package
        Assert.Equal("Customer", Subjects(await PlanOps(new UnitWhere { Stereotypes = ["audited"] })));
        Assert.Equal("-,Product", Subjects(await PlanOps(new UnitWhere { NotStereotypes = ["audited"] })));
    }

    private static Operation Operation(string id, string name, string? subject, params string[] stereotypes) => new()
    {
        Id = id,
        Name = name,
        Subject = subject,
        Effect = OperationEffect.Change,
        Stereotypes = stereotypes,
        Outcomes = [new OperationOutcome { Id = id[..^1] + "2", Name = "done" }],
    };

    private static async Task<string> Names(UnitWhere where)
    {
        var plan = await Plan(new PackSet([Pack("p", [Unit("u", "each entity", where)])], []));
        Assert.Empty(plan.Diagnostics);
        return string.Join(",", plan.Units.Select(u => ((REntity)u.Element!).Name).Order(StringComparer.Ordinal));
    }

    /// <summary><c>each custom:&lt;type&gt;</c> (next-phase-2-design.md section 4.2): one unit per element of the type, filters apply,
    /// and a kind the project does not declare plans nothing.</summary>
    [Fact]
    public async Task Each_custom_kind_plans_one_unit_per_element_and_none_for_an_undeclared_kind()
    {
        var b = new ModelBuilder();
        var billing = b.Package("Billing");
        b.Add(CustomKinds.CustomFixture.Element("01JRCE00000000000000000011", "due_days", "setting", """{ "value": 30 }""", billing.Id));
        b.Add(CustomKinds.CustomFixture.Element("01JRCE00000000000000000012", "grace_days", "setting", """{ "value": 5 }"""));
        b.Add(CustomKinds.CustomFixture.Element("01JRCE00000000000000000013", "invoice_sent", "message"));
        var model = Resolve(CustomKinds.CustomFixture.Snapshot(b, [CustomKinds.CustomFixture.KindDocument(CustomKinds.CustomFixture.SettingKind),
            CustomKinds.CustomFixture.KindDocument(CustomKinds.CustomFixture.MessageKind)]));
        Task<UnitPlan> PlanFor(string scope, UnitWhere? where = null) =>
            new UnitPlanner(Options).PlanAsync(model, new PackSet([Pack("p", [Unit("c", scope, where)])], []), new ScriptSandboxFactory(), null, Ct);

        var settings = await PlanFor("each custom:setting");

        Assert.Empty(settings.Diagnostics);
        Assert.Equal(["p/c:01JRCE00000000000000000011", "p/c:01JRCE00000000000000000012"], settings.Units.Select(u => u.Key));
        Assert.All(settings.Units, u => Assert.Equal("setting", Assert.IsType<RCustom>(u.Element).Type));
        Assert.Equal(["due_days"], (await PlanFor("each custom:setting", new UnitWhere { Packages = ["Billing"] })).Units.Select(u => ((RCustom)u.Element!).Name));
        var undeclared = await PlanFor("each custom:flag");
        Assert.Empty(undeclared.Units);
        Assert.Empty(undeclared.Diagnostics);
    }
}
