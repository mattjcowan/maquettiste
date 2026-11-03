using System.Text.Json.Nodes;
using Maquettiste.Engine.Pipeline;
using Maquettiste.Engine.Rendering;
using Maquettiste.Engine.Resolution;
using Maquettiste.Engine.Tests.Integration;
using static Maquettiste.Engine.Tests.Integration.ReferenceDataRepo;

namespace Maquettiste.Engine.Tests.Rendering;

/// <summary>
/// The model helpers of next-phase-1-design.md section 5.5: <c>array_index_of</c> and <c>array_find</c> (ordinal, by member),
/// <c>table_named</c> and <c>column_named</c> (with the <c>n:table:</c> key that renders a unit again when a table is renamed into
/// the name), <c>toposort</c> (stable, cycles released in input order), <c>json</c> with fields, the builtin names list and the
/// names a pack helper may no longer take (MQ6013).
/// </summary>
[Collection(IntegrationCollection.Name)]
public sealed class ModelHelperTests
{
    private const string Names = "names";
    private const string Output = "gen/names/names.txt";
    private const string OpsSchema = "01K7MHT0000000000000000001";
    private const string MainSettings = "01K7MHT0000000000000000010";
    private const string AuditLog = "01K7MHT0000000000000000020";
    private const string ReportingSettings = "01K7MHT0000000000000000030";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Dictionary<string, string> Js(string code) => new() { ["helpers.js"] = code };

    [Fact]
    public async Task Array_index_of_and_array_find_compare_ordinally_by_member()
    {
        var model = await BillingModel.GetAsync();
        var invoice = model.Entities.Single(e => e.Name == "Invoice");
        var index = model.Entities.ToList().FindIndex(e => e.Name == "Invoice");
        var unit = await Adhoc.RenderAsync("""
            {{ array_index_of ["b", "a", "B"] "B" }} {{ array_index_of ["b", "a"] "A" }} {{ array_index_of [1, 2.0, 3] 2 }} {{ array_index_of null "x" }}
            {{ array_index_of model.entities "name" "Invoice" }} {{ array_index_of model.entities "name" "invoice" }}
            {{ (array_find model.entities "name" "Invoice").name }} {{ (array_find model.entities "name" "invoice") == null }}
            {{ array_index_of model.entities model.entities[1] }} {{ (array_find entity.attributes "type.kind" "builtin").name }}
            """, invoice);

        var firstBuiltin = invoice.Attributes.First(a => a.Type.Kind == "builtin").Name;
        Assert.Equal($"2 -1 1 -1\n{index} -1\nInvoice true\n1 {firstBuiltin}", unit.Text());
        // Members are read through the template accessors, so the reads are recorded.
        Assert.Contains(model.Entities.MembershipKeys[0], unit.ReadKeys);
        Assert.Contains(invoice.Dependencies[0], unit.ReadKeys);

        var map = (await Adhoc.RenderAsync("{{ array_index_of entity.properties \"x\" }}", invoice)).Error();
        Assert.Equal("MQ6006", map.Rule);
        Assert.Contains("expects a list, not a map", map.Message, StringComparison.Ordinal);
        var path = (await Adhoc.RenderAsync("{{ array_find model.entities \"\" \"x\" }}")).Error();
        Assert.Contains("takes a member path", path.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Table_named_finds_by_schema_and_name_and_fails_across_two_databases()
    {
        await using var repo = await CreateWithTablesAsync("""
            a {{ (table_named "settings" "main").database.name }}
            b {{ (table_named "public.settings").database.name }}
            c {{ (table_named "ops.audit_log").schema }}
            d {{ (table_named "audit_log") == null }} {{ (table_named "nothing") == null }}
            e {{ (column_named (table_named "ops.audit_log") "at").name }} {{ (column_named (table_named "ops.audit_log") "nope") == null }}
            f {{ (table_named "settings" "reporting").database.name }} {{ (table_named "settings" "missing") == null }}

            """);
        await repo.ApplyAsync();
        Assert.Equal("a main\nb main\nc ops\nd true true\ne at true\nf reporting true\n", repo.Repo.ReadFile(Output));

        WriteTemplate(repo, "{{ table_named \"settings\" }}\n");
        var result = await RunAsync(repo);
        E2ERepo.AssertOutcome(RunOutcome.Invalid, result);
        var error = Assert.Single(result.Diagnostics, d => d.Rule == "MQ6006");
        Assert.Contains("'settings' names 2 tables: main.public.settings, reporting.settings", error.Message, StringComparison.Ordinal);

        WriteTemplate(repo, "{{ table_named \"ops.\" }}\n");
        var malformed = await RunAsync(repo);
        Assert.Contains(malformed.Diagnostics, d => d.Rule == "MQ6006" && d.Message.Contains("is not a table's name", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_table_renamed_into_the_name_renders_the_unit_again()
    {
        await using var repo = await CreateWithTablesAsync("{{ t = table_named \"ledger\" }}{{ if t }}{{ t.columns.size }} columns{{ else }}none{{ end }}\n");
        await repo.ApplyAsync();
        Assert.Equal("none\n", repo.Repo.ReadFile(Output));
        Assert.True(NamesUnit(await repo.PlanAsync()).Skipped);

        // The unit read no table: only the n:table:ledger key tells it that a table now has the name.
        await repo.EditAsync(MainSettings, n => n["name"] = "ledger");
        var unit = NamesUnit(await repo.PlanAsync());
        Assert.False(unit.Skipped);
        var cause = Assert.Single(unit.Causes, c => c.Key.StartsWith("n:", StringComparison.Ordinal));
        Assert.Equal(("inputs", "n:table:ledger", "The tables named ledger changed"), (cause.Kind, cause.Key, cause.Detail));
        await repo.ApplyAsync();
        Assert.Equal("1 columns\n", repo.Repo.ReadFile(Output));

        // A second table of the name in another database: the name's hash changes, so the unit renders again and now fails (MQ6006).
        await repo.EditAsync(ReportingSettings, n => n["name"] = "ledger");
        var both = await RunAsync(repo);
        E2ERepo.AssertOutcome(RunOutcome.Invalid, both);
        Assert.Contains(both.Diagnostics, d => d.Rule == "MQ6006" && d.Message.Contains("names 2 tables", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Toposort_is_stable_and_releases_cycles_in_input_order()
    {
        var model = await BillingModel.GetAsync();
        var unit = await Adhoc.RenderAsync("""
            {{ toposort ["a", "b", "c", "d"] [] | array.join "," }}
            {{ toposort ["a", "b", "c", "d"] [["c", "a"]] | array.join "," }}
            {{ toposort ["a", "b", "c", "d"] [["x", "a"], ["d", "y"]] | array.join "," }}
            {{ toposort ["a", "b", "c"] [["b", "a"], ["a", "b"]] | array.join "," }}
            {{ toposort ["a", "b", "c", "d"] [["d", "c"], ["c", "b"], ["b", "a"]] | array.join "," }}
            {{ toposort [1, 2, 3] [[3, 1.0]] | array.join "," }}
            {{ toposort model.entities [[model.entities[2], model.entities[0]]] | array.map "name" | array.join "," }}
            """);

        var entities = model.Entities.Select(e => e.Name).ToList();
        string[] moved = [entities[1], entities[2], entities[0], .. entities.Skip(3)];
        Assert.Equal(string.Join('\n',
            "a,b,c,d",
            "b,c,a,d",
            "a,b,c,d",
            "c,a,b",
            "d,c,b,a",
            "2,3,1",
            string.Join(',', moved)), unit.Text());

        var error = (await Adhoc.RenderAsync("{{ toposort [1, 2] [[1]] }}")).Error();
        Assert.Equal("MQ6006", error.Rule);
        Assert.Contains("[before, after] pairs", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Json_projects_member_paths_through_lists()
    {
        var model = await BillingModel.GetAsync();
        var invoice = model.Entities.Single(e => e.Name == "Invoice");
        var unit = await Adhoc.RenderAsync("""
            {{ json entity ["name", "attributes.name", "attributes.required"] }}
            {{ json entity "name" }}
            {{ json entity ["base.name", "properties.nope", "tags"] }}
            {{ json entity.attributes ["name"] }}
            """, invoice);

        var attributes = string.Join(',', invoice.Attributes.Select(a => $"{{\"name\":\"{a.Name}\",\"required\":{(a.Required ? "true" : "false")}}}"));
        var names = string.Join(',', invoice.Attributes.Select(a => $"{{\"name\":\"{a.Name}\"}}"));
        var tags = string.Join(',', invoice.Tags.Select(t => $"\"{t}\""));
        var parent = invoice.Base is null ? "null" : $"{{\"name\":\"{invoice.Base.Name}\"}}";
        Assert.Equal(string.Join('\n',
            $"{{\"attributes\":[{attributes}],\"name\":\"Invoice\"}}",
            "{\"name\":\"Invoice\"}",
            $"{{\"base\":{parent},\"properties\":{{\"nope\":null}},\"tags\":[{tags}]}}",
            $"[{names}]"), unit.Text());
        Assert.Contains(invoice.Dependencies[0], unit.ReadKeys);

        var ends = (await Adhoc.RenderAsync("{{ json entity [\"attributes\"] }}", invoice)).Error();
        Assert.Equal("MQ6006", ends.Rule);
        Assert.Contains("the field 'attributes' ends on an attribute", ends.Message, StringComparison.Ordinal);
        var missing = (await Adhoc.RenderAsync("{{ json entity [\"nope\"] }}", invoice)).Error();
        Assert.Contains("an entity has no member 'nope'", missing.Message, StringComparison.Ordinal);
        var scalar = (await Adhoc.RenderAsync("{{ json entity [\"name.first\"] }}", invoice)).Error();
        Assert.Contains("the field 'name.first' reads a member of", scalar.Message, StringComparison.Ordinal);
        // Without fields a resolved object stays refused.
        var whole = (await Adhoc.RenderAsync("{{ json entity }}", invoice)).Error();
        Assert.Contains("cannot serialize a entity", whole.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Builtin_names_list_every_registered_helper()
    {
        var builtins = BuiltinHelpers.CreateBuiltins();
        var registered = builtins.GetMembers().Where(m => builtins[m] is HelperFunction).Order(StringComparer.Ordinal);

        Assert.Equal(BuiltinHelpers.Names.Order(StringComparer.Ordinal), registered);
        foreach (var name in (string[])["array_index_of", "array_find", "table_named", "column_named", "toposort", "json", "seed_named", "seed_row", "ledger_ddl"])
            Assert.True(BuiltinHelpers.Names.Contains(name), name);
        Assert.True(BuiltinHelpers.Variables.Contains("database") && BuiltinHelpers.Variables.Contains("attribute"));
    }

    [Theory]
    [InlineData("toposort")]
    [InlineData("array_index_of")]
    [InlineData("array_find")]
    [InlineData("table_named")]
    [InlineData("column_named")]
    public async Task A_pack_helper_named_toposort_is_MQ6013(string name)
    {
        var error = (await Adhoc.RenderAsync("x", files: Js($"maquettiste.helper('{name}', () => 1);"))).Error();
        Assert.Equal("MQ6013", error.Rule);
        Assert.Contains($"'{name}' collides", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("database")]
    [InlineData("attribute")]
    public async Task A_pack_helper_named_database_or_attribute_is_MQ6013(string name)
    {
        var error = (await Adhoc.RenderAsync("x", files: Js($"maquettiste.helper('{name}', () => 1);"))).Error();
        Assert.Equal("MQ6013", error.Rule);
        Assert.Equal(".maquettiste/templates/adhoc/helpers.js", error.FilePath);

        // The reserved names are the scope variables of each database and each attribute.
        var model = await BillingModel.GetAsync();
        IResolvedObject element = name == "database" ? model.Databases[0] : model.Entities.Single(e => e.Name == "Invoice").OwnAttributes[0];
        var unit = await Adhoc.RenderAsync($"{{{{ {name}.name }}}} {{{{ element.id == {name}.id }}}}", element, u => u with { For = "each " + name });
        Assert.Equal(((RAnnotated)element) switch
        {
            RDatabase d => d.Name,
            RElement e => e.Name,
            _ => "?",
        } + " true", unit.Text());
    }

    /// <summary>
    /// The reference-data repo (no migration facts recorded) with the <c>names</c> pack, whose model unit renders the template, and
    /// three tables: <c>settings</c> in main (default schema <c>public</c>), <c>ops.audit_log</c> in main and <c>settings</c> in reporting
    /// (sqlite, no schema).
    /// </summary>
    private static async Task<ReferenceDataRepo> CreateWithTablesAsync(string template)
    {
        var repo = ReferenceDataRepo.Create(s =>
        {
            s["packs"]![Names] = new JsonObject { ["output"] = "gen/names" };
            s["migrations"] = new JsonObject { ["record"] = "off" };
        });
        var folder = Path.Combine(repo.Repo.ModelRoot, "templates", Names);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "pack.json"), """
            {"name": "names", "version": "1.0.0", "engine": ">=1.0", "units": [
              {"id": "names", "template": "names.scriban", "for": "model", "output": "names.txt"}]}
            """);
        WriteTemplate(repo, template);
        await repo.EditAsync(MainDatabaseId, n => n["schemas"] = new JsonArray(new JsonObject { ["id"] = OpsSchema, ["name"] = "ops" }));
        await repo.CreateAsync($$"""
            {"kind":"table","id":"{{MainSettings}}","name":"settings","database":"{{MainDatabaseId}}","columns":[
              {"id":"01K7MHT0000000000000000011","name":"id","type":"int32","nullable":false}]}
            """);
        await repo.CreateAsync($$"""
            {"kind":"table","id":"{{AuditLog}}","name":"audit_log","database":"{{MainDatabaseId}}","schema":"{{OpsSchema}}","columns":[
              {"id":"01K7MHT0000000000000000021","name":"at","type":"int64","nullable":false}]}
            """);
        await repo.CreateAsync($$"""
            {"kind":"table","id":"{{ReportingSettings}}","name":"settings","database":"{{ReportingDatabaseId}}","columns":[
              {"id":"01K7MHT0000000000000000031","name":"id","type":"int32","nullable":false}]}
            """);
        return repo;
    }

    private static void WriteTemplate(ReferenceDataRepo repo, string template) =>
        File.WriteAllText(Path.Combine(repo.Repo.ModelRoot, "templates", Names, "names.scriban"), template);

    private static Task<GenerationResult> RunAsync(ReferenceDataRepo repo) =>
        repo.Service.RunAsync(new GenerationRequest { Mode = GenerationMode.Apply, Jobs = 2 }, null, Ct);

    private static PlanUnit NamesUnit(GenerationPlan plan) => plan.Units.Single(u => u.Key == Names + "/names");
}
