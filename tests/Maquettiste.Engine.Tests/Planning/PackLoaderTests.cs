using System.Text.Json;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Pipeline;
using Maquettiste.Engine.Planning;
using Maquettiste.Engine.Resolution;
using Maquettiste.Engine.Scripting;
using Maquettiste.Engine.Tests.Generation;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Planning;

public sealed class PackLoaderTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Loads_packs_in_name_order_with_parameters_scripts_and_type_maps()
    {
        await using var f = await GenerationFixture.CreateAsync(b => Models.Shop(b), "scripted", "basic");
        f.WritePackFile("basic", "types/csharp.json", "{ \"string\": \"string\", \"nullable\": \"{type}?\" }");
        await f.WriteModelAsync(b => Models.Shop(b), s => s with
        {
            Packs = new Dictionary<string, PackSettings> { ["basic"] = new() { Output = "gen", Parameters = new Dictionary<string, JsonElement> { ["extra"] = JsonSerializer.SerializeToElement(3) } } },
        });
        var snapshot = await f.Store.GetSnapshotAsync(Ct);

        var set = await new PackLoader(f.Repo.Options, TestServices.Schemas).LoadAsync(snapshot, null, null, Ct);

        // 'extra' is a project value the pack neither defaults nor declares: MQ6024, a warning, and still passed to the templates.
        var undeclared = Assert.Single(set.Diagnostics);
        Assert.Equal(("MQ6024", "/packs/basic/parameters/extra"), (undeclared.Rule, undeclared.JsonPointer));
        Assert.Equal(["basic", "scripted"], set.Packs.Select(p => p.Name));
        Assert.Equal([0, 1], set.Packs.Select(p => p.Order));
        var basic = set.Packs[0];
        Assert.Equal(".maquettiste/templates/basic", basic.RelativePath);
        Assert.Equal("gen", basic.Settings.Output);
        Assert.Equal(["extra", "greeting"], basic.Parameters.Keys);
        Assert.Equal("{type}?", basic.TypeMaps["csharp"]["nullable"]);
        var scripted = set.Packs[1];
        var script = Assert.Single(scripted.Scripts);
        Assert.Equal(".maquettiste/templates/scripted/helpers.js", script.Path);
        Assert.NotEqual(basic.ScriptsHash, scripted.ScriptsHash);
    }

    [Fact]
    public async Task Disabled_packs_are_skipped_and_unknown_requested_packs_are_errors()
    {
        await using var f = await GenerationFixture.CreateAsync(b => Models.Shop(b), "scripted", "basic");
        await f.WriteModelAsync(b => Models.Shop(b), s => s with { Packs = new Dictionary<string, PackSettings> { ["scripted"] = new() { Enabled = false } } });
        var snapshot = await f.Store.GetSnapshotAsync(Ct);
        var loader = new PackLoader(f.Repo.Options, TestServices.Schemas);

        var all = await loader.LoadAsync(snapshot, null, null, Ct);
        Assert.Equal(["basic"], all.Packs.Select(p => p.Name));

        var named = await loader.LoadAsync(snapshot, ["basic", "ghost"], null, Ct);
        Assert.Equal(["basic"], named.Packs.Select(p => p.Name));
        var error = Assert.Single(named.Diagnostics);
        Assert.Equal("MQ6001", error.Rule);
        Assert.Contains("ghost", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{ \"name\": \"basic\", \"version\": \"1.0.0\", \"engine\": \">=2.0\", \"units\": [ { \"id\": \"a\", \"template\": \"index.tpl\", \"for\": \"model\" } ] }", "MQ6002", "/engine")]
    [InlineData("{ \"name\": \"basic\", \"version\": \"1.0.0\", \"engine\": \"banana\", \"units\": [ { \"id\": \"a\", \"template\": \"index.tpl\", \"for\": \"model\" } ] }", "MQ6001", "/engine")]
    [InlineData("{ \"name\": \"other\", \"version\": \"1.0.0\", \"engine\": \">=1.0\", \"units\": [ { \"id\": \"a\", \"template\": \"index.tpl\", \"for\": \"model\" } ] }", "MQ6001", "/name")]
    [InlineData("{ \"name\": \"basic\", \"version\": \"1.0.0\", \"engine\": \">=1.0\", \"units\": [ { \"id\": \"a\", \"template\": \"missing.tpl\", \"for\": \"model\" } ] }", "MQ6022", "/units/0/template")]
    [InlineData("{ \"name\": \"basic\", \"version\": \"1.0.0\", \"engine\": \">=1.0\", \"units\": [ { \"id\": \"a\", \"template\": \"../escape.tpl\", \"for\": \"model\" } ] }", "MQ6001", "/units/0/template")]
    [InlineData("{ \"name\": \"basic\", \"version\": \"1.0.0\", \"engine\": \">=1.0\", \"units\": [ { \"id\": \"a\", \"template\": \"index.tpl\", \"for\": \"model\" }, { \"id\": \"a\", \"template\": \"index.tpl\", \"for\": \"model\" } ] }", "MQ6001", "/units/1/id")]
    [InlineData("{ \"name\": \"basic\", \"version\": \"1.0.0\", \"engine\": \">=1.0\", \"units\": [ { \"id\": \"a\", \"template\": \"index.tpl\", \"for\": \"model\", \"where\": { \"tags\": [\"x\"] } } ] }", "MQ6001", "/units/0/where")]
    [InlineData("{ \"name\": \"basic\", \"version\": \"1.0.0\", \"engine\": \">=1.0\", \"units\": [ { \"id\": \"a\", \"template\": \"index.tpl\", \"for\": \"each thing\" } ] }", "MQ6021", "/units/0/for")]
    [InlineData("{ \"name\": \"basic\", \"version\": \"1.0.0\", \"engine\": \">=1.0\", \"units\": [ { \"id\": \"a\", \"template\": \"index.tpl\", \"for\": \"each process\", \"where\": { \"database\": \"main\" } } ] }", "MQ6001", "/units/0/where/database")]
    [InlineData("{ \"name\": \"basic\", \"version\": \"1.0.0\", \"engine\": \">=1.0\", \"units\": [ { \"id\": \"a\", \"template\": \"index.tpl\", \"for\": \"each scenario\", \"where\": { \"database\": \"main\" } } ] }", "MQ6001", "/units/0/where/database")]
    [InlineData("{ \"name\": \"basic\", \"version\": \"1.0.0\", \"engine\": \">=1.0\", \"units\": [ { \"id\": \"a\", \"template\": \"index.tpl\", \"for\": \"each actor\", \"where\": { \"abstract\": false } } ] }", "MQ6001", "/units/0/where/abstract")]
    [InlineData("{ \"name\": \"basic\", \"version\": \"1.0.0\", \"engine\": \">=1.0\", \"units\": [ { \"id\": \"a\", \"template\": \"index.tpl\", \"for\": \"each sequence\", \"where\": { \"abstract\": false } } ] }", "MQ6001", "/units/0/where/abstract")]
    [InlineData("{ \"name\": \"basic\", \"version\": \"1.0.0\", \"engine\": \">=1.0\", \"units\": [ { \"id\": \"a\", \"template\": \"index.tpl\", \"for\": \"each database\", \"where\": { \"abstract\": false } } ] }", "MQ6001", "/units/0/where/abstract")]
    [InlineData("{ \"name\": \"basic\", \"version\": \"1.0.0\", \"engine\": \">=1.0\", \"units\": [ { \"id\": \"a\", \"template\": \"index.tpl\", \"for\": \"each attribute\", \"where\": { \"abstract\": true } } ] }", "MQ6001", "/units/0/where/abstract")]
    [InlineData("{ \"name\": \"basic\", \"version\": \"1.0.0\", \"engine\": \">=1.0\", \"scripts\": [\"nope.js\"], \"units\": [ { \"id\": \"a\", \"template\": \"index.tpl\", \"for\": \"model\" } ] }", "MQ6001", "/scripts/0")]
    public async Task Invalid_pack_json_is_reported_and_the_pack_left_out(string json, string rule, string pointer)
        => await AssertLeftOut(json, rule, pointer);

    [Theory]
    [InlineData("{ \"database\": \"main\" }", "/units/0/where/database")]
    [InlineData("{ \"abstract\": false }", "/units/0/where/abstract")]
    public async Task An_each_operation_unit_refuses_database_and_abstract_filters(string where, string pointer) => await AssertLeftOut(
        "{ \"name\": \"basic\", \"version\": \"1.0.0\", \"engine\": \">=1.0\", \"units\": [ { \"id\": \"a\", \"template\": \"index.tpl\", \"for\": \"each operation\", \"where\": "
        + where + " } ] }", "MQ6001", pointer);

    private static async Task AssertLeftOut(string json, string rule, string pointer)
    {
        await using var f = await GenerationFixture.CreateAsync(b => Models.Shop(b), "basic");
        f.WritePackFile("basic", "pack.json", json);
        var snapshot = await f.Store.GetSnapshotAsync(Ct);

        var set = await new PackLoader(f.Repo.Options, TestServices.Schemas).LoadAsync(snapshot, null, null, Ct);

        Assert.Empty(set.Packs);
        Assert.Contains(set.Diagnostics, d => d.Rule == rule && d.JsonPointer == pointer && d.FilePath == ".maquettiste/templates/basic/pack.json");
    }

    [Fact]
    public async Task Physical_object_units_filter_on_packages()
    {
        // next-phase-1-design.md section 4.3: views, sequences, routines, database types, SQL objects and queries may join a
        // package, so their units filter on it like a table's.
        await using var f = await GenerationFixture.CreateAsync(b => Models.Shop(b), "basic");
        string[] scopes = ["each view", "each sequence", "each routine", "each database type", "each sql object", "each query"];
        var units = string.Join(", ", scopes.Select((scope, i) =>
            $"{{ \"id\": \"in{i}\", \"template\": \"index.tpl\", \"for\": \"{scope}\", \"output\": \"out/in{i}/{{{{ name }}}}\", \"where\": {{ \"packages\": [\"Sales\"] }} }}, " +
            $"{{ \"id\": \"out{i}\", \"template\": \"index.tpl\", \"for\": \"{scope}\", \"output\": \"out/out{i}/{{{{ name }}}}\", \"where\": {{ \"notPackages\": [\"Sales\"] }} }}"));
        f.WritePackFile("basic", "pack.json", $"{{ \"name\": \"basic\", \"version\": \"1.0.0\", \"engine\": \">=1.0\", \"units\": [ {units} ] }}");
        await f.WriteModelAsync(b => Models.Shop(b, extra: (m, _, main) =>
        {
            var sales = m.Package("Sales");
            m.Add(new View { Id = m.NewId(), Name = "sales_view", Database = main.Id, Package = sales.Id, Body = new Dictionary<string, string> { ["*"] = "select 1" } });
            m.Add(new View { Id = m.NewId(), Name = "loose_view", Database = main.Id, Body = new Dictionary<string, string> { ["*"] = "select 1" } });
            m.Add(new Sequence { Id = m.NewId(), Name = "sales_seq", Database = main.Id, Package = sales.Id });
        }));
        var snapshot = await f.Store.GetSnapshotAsync(Ct);

        var set = await new PackLoader(f.Repo.Options, TestServices.Schemas).LoadAsync(snapshot, null, null, Ct);

        Assert.Empty(set.Diagnostics);
        Assert.Equal(["basic"], set.Packs.Select(p => p.Name));
        var model = await new ModelResolver(f.Repo.Options).ResolveAsync(snapshot, null, Ct);
        var plan = await new UnitPlanner(f.Repo.Options).PlanAsync(model, set, new ScriptSandboxFactory(), null, Ct);
        Assert.Empty(plan.Diagnostics);
        string Names(string unit) => string.Join(",", plan.Units.Where(u => u.Unit.Id == unit)
            .Select(u => u.Element switch { RView v => v.Name, RSequence q => q.Name, var other => other!.Id }).Order(StringComparer.Ordinal));
        Assert.Equal("sales_view", Names("in0"));
        Assert.Equal("loose_view", Names("out0"));
        Assert.Equal("sales_seq", Names("in1"));
        Assert.Equal("", Names("out1"));
    }

    [Fact]
    public async Task Pack_parameter_xrefs_are_checked()
    {
        // next-phase-1-design.md section 4.1: a pack's parameterSchema may type a parameter as a link. The load reports a malformed or
        // misplaced keyword in pack.json (MQ2009), and each project value it types in maquettiste.json that is a name (MQ2010),
        // dangles (MQ2001), names another kind (MQ2002) or a row of another list (MQ2011). The pack still loads, as with MQ6023.
        string customer = "", main = "", euro = "";
        void Model(ModelBuilder b) => Models.Shop(b, extra: (m, c, db) =>
        {
            customer = c.Id;
            main = db.Id;
            var currency = m.Entity("Currency").Key("code", "string");
            euro = m.NewId();
            m.Add(new Seed { Id = m.NewId(), Name = "currencies", Target = currency.Id, Columns = [currency.AttrId("code")], Rows = [new SeedRow { Id = euro, Values = [JsonSerializer.SerializeToElement("EUR")] }] });
        });
        await using var f = await GenerationFixture.CreateAsync(Model, "basic");
        var ghost = "01JB9EE0ZBGJ09TQM83XSSSS6Y";
        f.WritePackFile("basic", "pack.json", """
            { "name": "basic", "version": "1.0.0", "engine": ">=1.0", "units": [ { "id": "index", "template": "index.tpl", "for": "model", "output": "out/index.txt" } ],
              "parameterSchema": { "properties": {
                "root": { "type": "string", "x-ref": { "kind": "entity" } },
                "audited": { "type": "array", "items": { "type": "string", "x-ref": { "kind": "entity", "optional": true } } },
                "target": { "type": "string", "x-ref": { "kind": "database" } },
                "sortBy": { "type": "string", "x-ref": { "kind": "attribute", "of": "$element" } },
                "region": { "type": "string", "x-ref": { "kind": "table", "sameDatabase": true } },
                "first": { "type": "string", "x-ref": { "kind": "row", "of": "CUSTOMER" } },
                "broken": { "type": "string", "x-ref": { "kind": "widget" } } } } }
            """.Replace("CUSTOMER", customer, StringComparison.Ordinal));
        await f.WriteModelAsync(Model, s => s with
        {
            Packs = new Dictionary<string, PackSettings>
            {
                ["basic"] = new()
                {
                    Parameters = new Dictionary<string, JsonElement>
                    {
                        ["root"] = JsonSerializer.SerializeToElement("Customer"),
                        ["audited"] = JsonSerializer.SerializeToElement(new[] { customer, ghost }),
                        ["target"] = JsonSerializer.SerializeToElement(customer),
                        ["first"] = JsonSerializer.SerializeToElement(euro),
                        ["region"] = JsonSerializer.SerializeToElement(main),
                    },
                },
            },
        });
        var snapshot = await f.Store.GetSnapshotAsync(Ct);
        var loader = new PackLoader(f.Repo.Options, TestServices.Schemas);

        var set = await loader.LoadAsync(snapshot, null, null, Ct);

        const string Settings = ".maquettiste/maquettiste.json";
        const string Pack = ".maquettiste/templates/basic/pack.json";
        Assert.Equal(
            [
                // Sorted as reports sort them: path, rule, message.
                ("MQ2001", Settings, "/packs/basic/parameters/audited/1"),
                ("MQ2002", Settings, "/packs/basic/parameters/region"),
                ("MQ2002", Settings, "/packs/basic/parameters/target"),
                ("MQ2010", Settings, "/packs/basic/parameters/root"),
                ("MQ2011", Settings, "/packs/basic/parameters/first"),
                ("MQ2009", Pack, "/parameterSchema/properties/broken/x-ref/kind"),
                ("MQ2009", Pack, "/parameterSchema/properties/sortBy/x-ref/of"),
                ("MQ2009", Pack, "/parameterSchema/properties/region/x-ref/sameDatabase"),
            ],
            set.Diagnostics.Select(d => (d.Rule, d.FilePath!, d.JsonPointer!)));
        Assert.Equal(["basic"], set.Packs.Select(p => p.Name));
        Assert.Contains("Parameter 'root' of pack 'basic' holds 'Customer', which is a name or other text, not an id", set.Diagnostics[3].Message, StringComparison.Ordinal);
        Assert.Contains("a pack parameter is held by no element", set.Diagnostics[6].Message, StringComparison.Ordinal);
        // The pack read (MCP get_pack) reports the same findings.
        var (pack, named) = await loader.LoadNamedAsync(snapshot, "basic", Ct);
        Assert.NotNull(pack);
        Assert.Equal(set.Diagnostics, named);
    }

    [Fact]
    public async Task Malformed_json_and_bad_type_maps_are_reported()
    {
        await using var f = await GenerationFixture.CreateAsync(b => Models.Shop(b), "basic", "scripted");
        f.WritePackFile("basic", "pack.json", "{ \"name\": ");
        f.WritePackFile("scripted", "types/sql.json", "{ \"string\": 3 }");
        var snapshot = await f.Store.GetSnapshotAsync(Ct);

        var set = await new PackLoader(f.Repo.Options, TestServices.Schemas).LoadAsync(snapshot, null, null, Ct);

        Assert.Empty(set.Packs);
        Assert.Contains(set.Diagnostics, d => d.FilePath == ".maquettiste/templates/basic/pack.json" && d.Line is not null);
        Assert.Contains(set.Diagnostics, d => d.FilePath == ".maquettiste/templates/scripted/types/sql.json" && d.JsonPointer == "/string");
    }

    private const string CopyPack = "{ \"name\": \"basic\", \"version\": \"1.0.0\", \"engine\": \">=1.0\", \"units\": [ { \"id\": \"index\", \"template\": \"index.tpl\", \"for\": \"model\" }, UNIT ] }";

    [Theory]
    [InlineData("{ \"id\": \"skeleton\", \"template\": \"index.tpl\", \"for\": \"model\", \"mode\": \"copy\", \"sources\": [\"skeleton/**\"] }", "/units/1/template", "takes no template")]
    [InlineData("{ \"id\": \"skeleton\", \"template\": \"index.tpl\", \"for\": \"model\", \"sources\": [\"skeleton/**\"] }", "/units/1/sources", "only mode copy")]
    [InlineData("{ \"id\": \"skeleton\", \"template\": \"index.tpl\", \"for\": \"model\", \"tokens\": { \"Skeleton\": \"greeting\" } }", "/units/1/tokens", "only mode copy")]
    public async Task A_copy_unit_with_a_template_is_MQ6001(string unit, string pointer, string text)
    {
        await using var f = await GenerationFixture.CreateAsync(b => Models.Shop(b), "basic");
        f.WritePackFile("basic", "skeleton/Program.cs", "class Program { }\n");
        f.WritePackFile("basic", "pack.json", CopyPack.Replace("UNIT", unit, StringComparison.Ordinal));
        var snapshot = await f.Store.GetSnapshotAsync(Ct);

        var set = await new PackLoader(f.Repo.Options, TestServices.Schemas).LoadAsync(snapshot, null, null, Ct);

        Assert.Empty(set.Packs);
        var error = Assert.Single(set.Diagnostics);
        Assert.Equal(("MQ6001", pointer, ".maquettiste/templates/basic/pack.json"), (error.Rule, error.JsonPointer, error.FilePath));
        Assert.Contains("Unit 'skeleton'", error.Message, StringComparison.Ordinal);
        Assert.Contains(text, error.Message, StringComparison.Ordinal);
        Assert.NotNull(error.Line);

        // A copy unit without sources, and a template unit without a template, fail the schema too.
        f.WritePackFile("basic", "pack.json", CopyPack.Replace("UNIT", "{ \"id\": \"skeleton\", \"for\": \"model\", \"mode\": \"copy\" }", StringComparison.Ordinal));
        var missing = await new PackLoader(f.Repo.Options, TestServices.Schemas).LoadAsync(snapshot, null, null, Ct);
        Assert.Contains(missing.Diagnostics, d => d.Rule == "MQ6001" && d.JsonPointer == "/units/1" && d.Message.Contains("sources", StringComparison.Ordinal));
        f.WritePackFile("basic", "pack.json", CopyPack.Replace("UNIT", "{ \"id\": \"skeleton\", \"for\": \"model\" }", StringComparison.Ordinal));
        var untemplated = await new PackLoader(f.Repo.Options, TestServices.Schemas).LoadAsync(snapshot, null, null, Ct);
        Assert.Contains(untemplated.Diagnostics, d => d.Rule == "MQ6001" && d.JsonPointer == "/units/1" && d.Message.Contains("template", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("/skeleton/**", "absolute")]
    [InlineData("C:/skeleton/**", "absolute")]
    [InlineData("skeleton//*.cs", "empty segment")]
    [InlineData("skeleton/", "empty segment")]
    [InlineData("./skeleton/*.cs", "'.' or '..'")]
    [InlineData("skeleton/../*.cs", "'.' or '..'")]
    [InlineData("skeleton\\*.cs", "backslash")]
    public async Task A_malformed_source_pattern_is_MQ6001(string pattern, string text)
    {
        await using var f = await GenerationFixture.CreateAsync(b => Models.Shop(b), "basic");
        f.WritePackFile("basic", "skeleton/Program.cs", "class Program { }\n");
        var unit = $"{{ \"id\": \"skeleton\", \"for\": \"model\", \"mode\": \"copy\", \"sources\": [\"skeleton/*.cs\", {JsonSerializer.Serialize(pattern)}] }}";
        f.WritePackFile("basic", "pack.json", CopyPack.Replace("UNIT", unit, StringComparison.Ordinal));
        var snapshot = await f.Store.GetSnapshotAsync(Ct);

        var set = await new PackLoader(f.Repo.Options, TestServices.Schemas).LoadAsync(snapshot, null, null, Ct);

        Assert.Empty(set.Packs);
        var error = Assert.Single(set.Diagnostics);
        Assert.Equal(("MQ6001", "/units/1/sources/1"), (error.Rule, error.JsonPointer));
        Assert.Contains(text, error.Message, StringComparison.Ordinal);

        // A well-formed copy unit loads: it has no template to check.
        f.WritePackFile("basic", "pack.json", CopyPack.Replace("UNIT", "{ \"id\": \"skeleton\", \"for\": \"model\", \"mode\": \"copy\", \"sources\": [\"skeleton/**\"], \"tokens\": { \"Skeleton\": \"greeting\" } }", StringComparison.Ordinal));
        var loaded = await new PackLoader(f.Repo.Options, TestServices.Schemas).LoadAsync(snapshot, null, null, Ct);
        Assert.Empty(loaded.Diagnostics);
        var copy = Assert.Single(loaded.Packs).Manifest.Units[1];
        Assert.Equal((OutputMode.Copy, null, "greeting"), (copy.Mode, copy.Template, copy.Tokens["Skeleton"]));
    }

    [Theory]
    [InlineData(">=1.0 <2.0", true)]
    [InlineData(">= 1.0 < 2.0", true)]
    [InlineData(">=1.1", false)]
    [InlineData("<1.0", false)]
    [InlineData("1.x", true)]
    [InlineData("1", true)]
    [InlineData("1.0.0", true)]
    [InlineData("=1.0.1", false)]
    [InlineData("^1.0", true)]
    [InlineData("~1.0", true)]
    [InlineData("~0.9", false)]
    [InlineData("*", true)]
    [InlineData("<1.0 || >=1.0", true)]
    [InlineData(">2.0 || <0.5", false)]
    public void Engine_ranges(string range, bool expected)
    {
        Assert.Equal(expected, EngineRange.Satisfies(range, "1.0.0", out var valid));
        Assert.True(valid);
    }

    [Theory]
    [InlineData("banana")]
    [InlineData(">=")]
    [InlineData("")]
    [InlineData("1.2.3.4")]
    public void Unparseable_engine_ranges(string range)
    {
        Assert.False(EngineRange.Satisfies(range, "1.0.0", out var valid));
        Assert.False(valid);
    }

    // Library packs (scope 5.1, next-phase-2-design.md section 5.2): uses, the closure, ranges, cycles and dependency order.

    private static void Pack(GenerationFixture f, string name, string version = "1.0.0", bool units = true, params (string Pack, string Range)[] uses)
    {
        var entries = string.Join(", ", uses.Select(u => $"{{ \"pack\": \"{u.Pack}\", \"version\": \"{u.Range}\" }}"));
        var unitList = units ? ", \"units\": [ { \"id\": \"a\", \"template\": \"index.tpl\", \"for\": \"model\" } ]" : "";
        f.WritePackFile(name, "pack.json", $"{{ \"name\": \"{name}\", \"version\": \"{version}\", \"engine\": \">=1.0\", \"uses\": [{entries}]{unitList} }}");
        if (units)
            f.WritePackFile(name, "index.tpl", "x");
    }

    private static async Task<PackSet> LoadAsync(GenerationFixture f, IReadOnlyCollection<string>? names = null)
    {
        var snapshot = await f.Store.GetSnapshotAsync(Ct);
        return await new PackLoader(f.Repo.Options, TestServices.Schemas).LoadAsync(snapshot, names, null, Ct);
    }

    private static Diagnostic Only(PackSet set, string rule) => Assert.Single(set.Diagnostics, d => d.Rule == rule);

    [Fact]
    public async Task Uses_naming_a_missing_pack_is_MQ6042()
    {
        await using var f = await GenerationFixture.CreateAsync(b => Models.Shop(b));
        Pack(f, "records", uses: [("naming", "^1.0.0")]);

        var set = await LoadAsync(f);

        Assert.Empty(set.Packs);
        var d = Only(set, "MQ6042");
        Assert.Equal((".maquettiste/templates/records/pack.json", "/uses/0/pack"), (d.FilePath, d.JsonPointer));
        Assert.NotNull(d.Line);
        Assert.Contains("'naming'", d.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_pack_listed_twice_in_uses_is_MQ6042()
    {
        await using var f = await GenerationFixture.CreateAsync(b => Models.Shop(b));
        Pack(f, "naming", units: false);
        Pack(f, "records", uses: [("naming", "^1.0.0"), ("naming", "1.x")]);

        var set = await LoadAsync(f);

        Assert.Equal(["naming"], set.Packs.Select(p => p.Name));
        Assert.Equal("/uses/1/pack", Only(set, "MQ6042").JsonPointer);
    }

    [Fact]
    public async Task A_version_outside_the_range_is_MQ6043()
    {
        await using var f = await GenerationFixture.CreateAsync(b => Models.Shop(b));
        Pack(f, "naming", "1.4.0", units: false);
        Pack(f, "records", uses: [("naming", "^2.0.0")]);

        var set = await LoadAsync(f);

        Assert.Equal(["naming"], set.Packs.Select(p => p.Name));
        var d = Only(set, "MQ6043");
        Assert.Equal((".maquettiste/templates/records/pack.json", "/uses/0/version"), (d.FilePath, d.JsonPointer));
        Assert.Contains("1.4.0", d.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unparseable_range_is_MQ6043()
    {
        await using var f = await GenerationFixture.CreateAsync(b => Models.Shop(b));
        Pack(f, "naming", units: false);
        Pack(f, "records", uses: [("naming", "banana")]);

        var set = await LoadAsync(f);

        Assert.Equal("/uses/0/version", Only(set, "MQ6043").JsonPointer);
        Assert.DoesNotContain(set.Packs, p => p.Name == "records");
    }

    [Fact]
    public async Task A_pack_using_itself_is_MQ6044()
    {
        await using var f = await GenerationFixture.CreateAsync(b => Models.Shop(b));
        Pack(f, "records", uses: [("records", "^1.0.0")]);

        var set = await LoadAsync(f);

        Assert.Empty(set.Packs);
        Assert.Equal("/uses/0/pack", Only(set, "MQ6044").JsonPointer);
    }

    [Fact]
    public async Task A_cycle_is_MQ6044_on_every_pack_in_it()
    {
        await using var f = await GenerationFixture.CreateAsync(b => Models.Shop(b));
        Pack(f, "naming", units: false, uses: [("headers", "^1.0.0")]);
        Pack(f, "headers", units: false, uses: [("audit", "^1.0.0")]);
        Pack(f, "audit", units: false, uses: [("currency", "^1.0.0"), ("naming", "^1.0.0")]);
        Pack(f, "currency", units: false);
        Pack(f, "records", uses: [("naming", "^1.0.0")]);

        var set = await LoadAsync(f);

        Assert.Equal(["currency"], set.Packs.Select(p => p.Name));
        var cycles = set.Diagnostics.Where(d => d.Rule == "MQ6044").Select(d => (d.FilePath, d.JsonPointer)).ToList();
        Assert.Equal([(".maquettiste/templates/audit/pack.json", "/uses/1/pack"), (".maquettiste/templates/headers/pack.json", "/uses/0/pack"),
            (".maquettiste/templates/naming/pack.json", "/uses/0/pack")], cycles);
        Assert.Contains("audit -> naming -> headers -> audit", set.Diagnostics.First(d => d.Rule == "MQ6044").Message, StringComparison.Ordinal);
        Assert.Equal((".maquettiste/templates/records/pack.json", "/uses/0/pack"), (Only(set, "MQ6042").FilePath, Only(set, "MQ6042").JsonPointer));
    }

    [Fact]
    public async Task A_requested_pack_loads_its_closure_as_used_packs()
    {
        await using var f = await GenerationFixture.CreateAsync(b => Models.Shop(b));
        Pack(f, "currency", units: false);
        Pack(f, "naming", "1.4.0", units: false, uses: [("currency", "1.x")]);
        Pack(f, "records", uses: [("naming", "^1.2.0")]);
        Pack(f, "services");

        var set = await LoadAsync(f, ["records"]);

        Assert.Empty(set.Diagnostics);
        var records = Assert.Single(set.Packs);
        Assert.Equal("records", records.Name);
        Assert.Equal(["currency", "naming"], set.Used.Select(p => p.Name));
        Assert.Equal(["naming"], records.Uses);
        Assert.Equal(["currency", "naming"], records.Sees);
        Assert.Equal(["naming"], set.Find("currency")!.UsedBy);
        Assert.Same(set.Used[1], set.Find("naming"));
        Assert.Null(set.Find("services"));
    }

    [Fact]
    public async Task A_disabled_pack_loads_when_an_enabled_pack_uses_it()
    {
        await using var f = await GenerationFixture.CreateAsync(b => Models.Shop(b));
        Pack(f, "naming");
        Pack(f, "records", uses: [("naming", "^1.0.0")]);
        await f.WriteModelAsync(b => Models.Shop(b), s => s with
        {
            Packs = new Dictionary<string, PackSettings> { ["naming"] = new() { Enabled = false } },
            Uses = [new PackUse { Pack = "naming", Version = "^1.0.0" }],
        });

        var set = await LoadAsync(f);

        Assert.Empty(set.Diagnostics);
        Assert.Equal(["records"], set.Packs.Select(p => p.Name));
        var naming = Assert.Single(set.Used);
        Assert.Equal(("naming", 0, 1), (naming.Name, naming.Order, set.Packs[0].Order));
        Assert.Equal(["records", "maquettiste.json"], naming.UsedBy);
    }

    [Fact]
    public async Task Packs_order_after_the_packs_they_use_then_by_name()
    {
        await using var f = await GenerationFixture.CreateAsync(b => Models.Shop(b));
        Pack(f, "zeta", units: false);
        Pack(f, "alpha", uses: [("zeta", "^1.0.0")]);
        Pack(f, "beta");
        Pack(f, "mid", units: false, uses: [("zeta", "^1.0.0")]);
        Pack(f, "able", uses: [("mid", "^1.0.0")]);

        var set = await LoadAsync(f);

        Assert.Equal(["beta", "zeta", "alpha", "mid", "able"], set.Packs.Select(p => p.Name));
        Assert.Equal([0, 1, 2, 3, 4], set.Packs.Select(p => p.Order));
        Assert.Equal(["zeta", "mid"], set.Packs[4].Sees);
        var model = await new ModelResolver(f.Repo.Options).ResolveAsync(await f.Store.GetSnapshotAsync(Ct), null, Ct);
        var plan = await new UnitPlanner(f.Repo.Options).PlanAsync(model, set, new ScriptSandboxFactory(), null, Ct);
        Assert.Equal(["beta", "alpha", "able"], plan.Units.Select(u => u.Pack.Name));
    }

    [Fact]
    public async Task A_project_without_uses_keeps_ordinal_order()
    {
        await using var f = await GenerationFixture.CreateAsync(b => Models.Shop(b), "scripted", "basic");
        Pack(f, "audit");

        var set = await LoadAsync(f);

        Assert.Equal(["audit", "basic", "scripted"], set.Packs.Select(p => p.Name));
        Assert.Equal([0, 1, 2], set.Packs.Select(p => p.Order));
        Assert.Empty(set.Used);
        Assert.All(set.Packs, p => Assert.True(p.Uses.Count + p.Sees.Count + p.UsedBy.Count == 0));
    }

    [Fact]
    public async Task A_pack_without_units_loads()
    {
        await using var f = await GenerationFixture.CreateAsync(b => Models.Shop(b));
        Pack(f, "naming", units: false);
        f.WritePackFile("naming", "helpers.js", "maquettiste.helper('class_name', e => e.name);");
        Pack(f, "records", uses: [("naming", "^1.0.0")]);

        var set = await LoadAsync(f);

        Assert.Empty(set.Diagnostics);
        var naming = set.Find("naming")!;
        Assert.Empty(naming.Manifest.Units);
        Assert.Single(naming.Scripts);
    }

    [Fact]
    public async Task A_pack_without_units_that_nothing_uses_is_MQ6046()
    {
        await using var f = await GenerationFixture.CreateAsync(b => Models.Shop(b));
        Pack(f, "naming", units: false);
        Pack(f, "records");

        var set = await LoadAsync(f);

        var d = Only(set, "MQ6046");
        Assert.Equal((DiagnosticSeverity.Warning, ".maquettiste/templates/naming/pack.json", "/name"), (d.Severity, d.FilePath, d.JsonPointer));
        Assert.Equal(["naming", "records"], set.Packs.Select(p => p.Name));
        Assert.DoesNotContain((await LoadAsync(f, ["naming"])).Diagnostics, x => x.Rule == "MQ6046");
    }

    [Fact]
    public async Task A_pack_whose_closure_failed_is_MQ6042()
    {
        await using var f = await GenerationFixture.CreateAsync(b => Models.Shop(b));
        f.WritePackFile("currency", "pack.json", "{ \"name\": \"currency\", \"version\": \"1.0.0\", \"engine\": \">=9.0\" }");
        Pack(f, "naming", units: false, uses: [("currency", "^1.0.0")]);
        Pack(f, "records", uses: [("naming", "^1.0.0")]);

        var set = await LoadAsync(f, ["records"]);

        Assert.Empty(set.Packs);
        Assert.Empty(set.Used);
        Assert.Equal("MQ6002", Assert.Single(set.Diagnostics, d => d.FilePath!.EndsWith("currency/pack.json", StringComparison.Ordinal)).Rule);
        var naming = Assert.Single(set.Diagnostics, d => d.FilePath!.EndsWith("naming/pack.json", StringComparison.Ordinal));
        Assert.Equal(("MQ6042", "/uses/0/pack"), (naming.Rule, naming.JsonPointer));
        var records = Assert.Single(set.Diagnostics, d => d.FilePath!.EndsWith("records/pack.json", StringComparison.Ordinal));
        Assert.Equal("MQ6042", records.Rule);
        Assert.Contains("reach 'currency'", records.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Load_closure_returns_the_pack_and_its_used_packs()
    {
        await using var f = await GenerationFixture.CreateAsync(b => Models.Shop(b));
        Pack(f, "naming", units: false);
        Pack(f, "records", uses: [("naming", "^1.0.0")]);
        Pack(f, "services", uses: [("naming", "^1.0.0")]);
        await f.WriteModelAsync(b => Models.Shop(b), s => s with { Packs = new Dictionary<string, PackSettings> { ["records"] = new() { Enabled = false } } });
        var snapshot = await f.Store.GetSnapshotAsync(Ct);
        var loader = new PackLoader(f.Repo.Options, TestServices.Schemas);

        var set = await loader.LoadClosureAsync(snapshot, "records", Ct);

        Assert.Empty(set.Diagnostics);
        Assert.Equal(["records"], set.Packs.Select(p => p.Name));
        Assert.Equal(["naming"], set.Used.Select(p => p.Name));
        Assert.Equal(["records", "services"], set.Used[0].UsedBy);
        var ghost = await loader.LoadClosureAsync(snapshot, "ghost", Ct);
        Assert.Equal(("MQ6001", 0), (Assert.Single(ghost.Diagnostics).Rule, ghost.Packs.Count));
    }

    [Theory]
    [InlineData("entity")]
    [InlineData("value-object")]
    public async Task A_used_pack_named_like_a_variable_is_MQ6045(string name)
    {
        await using var f = await GenerationFixture.CreateAsync(b => Models.Shop(b));
        Pack(f, name, units: false);
        f.WritePackFile(name, "helpers.js", "maquettiste.helper('code', v => v);");
        Pack(f, "records", uses: [(name, "^1.0.0")]);

        var set = await LoadAsync(f);

        var d = Only(set, "MQ6045");
        Assert.Equal((".maquettiste/templates/records/pack.json", "/uses/0/pack"), (d.FilePath, d.JsonPointer));
        Assert.Equal([name], set.Packs.Select(p => p.Name));
    }

    [Theory]
    [InlineData("include", true)]
    [InlineData("string", true)]
    [InlineData("include", false)]
    public async Task A_used_pack_named_like_a_keyword_is_MQ6045(string name, bool scripts)
    {
        await using var f = await GenerationFixture.CreateAsync(b => Models.Shop(b));
        Pack(f, name, units: false);
        if (scripts)
            f.WritePackFile(name, "helpers.js", "maquettiste.helper('code', v => v);");
        Pack(f, "records", uses: [(name, "^1.0.0")]);

        var set = await LoadAsync(f);

        // Only a used pack with scripts gets a namespace; partials alone take no name.
        Assert.Equal(scripts ? 1 : 0, set.Diagnostics.Count(d => d.Rule == "MQ6045"));
        Assert.Equal(!scripts, set.Packs.Any(p => p.Name == "records"));
    }
}
