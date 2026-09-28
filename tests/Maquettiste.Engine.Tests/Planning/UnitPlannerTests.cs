using Maquettiste.Engine.Hashing;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Pipeline;
using Maquettiste.Engine.Planning;
using Maquettiste.Engine.Resolution;
using Maquettiste.Engine.Scripting;
using Maquettiste.Testing;
using static Maquettiste.Engine.Tests.Planning.PlanningKit;

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
            var templates = HashBuilder.Of(u.Template, PackFiles.Hash(pack.RootPath, u.Template) ?? DependencyHasher.Absent, null, null);
            var expected = HashBuilder.Of("mq-unit-1", EngineVersion.Value, pack.Name, pack.Manifest.Version, CanonicalForm.Json(u), pack.ScriptsHash,
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
    public void Static_hash_depends_on_the_pack_version()
    {
        var unit = Unit("u", "each entity");
        var pack = Pack("p", [unit]);
        Assert.NotEqual(UnitPlanner.StaticHash(pack, unit, [], "p/u:1"),
            UnitPlanner.StaticHash(pack with { Manifest = pack.Manifest with { Version = "1.0.1" } }, unit, [], "p/u:1"));
    }

    private static REntity Entity(string name) => Model.Entities.Single(e => e.Name == name);

    private static Task<UnitPlan> Plan(PackSet packs) => new UnitPlanner(Options).PlanAsync(Model, packs, new ScriptSandboxFactory(), null, Ct);

    private static async Task<string> Names(UnitWhere where)
    {
        var plan = await Plan(new PackSet([Pack("p", [Unit("u", "each entity", where)])], []));
        Assert.Empty(plan.Diagnostics);
        return string.Join(",", plan.Units.Select(u => ((REntity)u.Element!).Name).Order(StringComparer.Ordinal));
    }
}
