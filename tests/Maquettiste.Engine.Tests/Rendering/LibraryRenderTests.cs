using Maquettiste.Engine.Generation;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Pipeline;
using Maquettiste.Engine.Tests.Generation;
using Maquettiste.Engine.Planning;
using Maquettiste.Engine.Writing;

namespace Maquettiste.Engine.Tests.Rendering;

/// <summary>A billing-style library pack, <c>naming</c>, and packs that use it (library packs, next-phase-2-design.md section 5.2).</summary>
internal static class LibraryPacks
{
    public const string NamingHelpers = """
        function pascal(s) { return String(s).replace(/(^|[-_ ])([a-z])/g, (_, __, c) => c.toUpperCase()); }
        maquettiste.helper('class_name', e => pascal(e.name) + maquettiste.params.suffix);
        """;

    /// <summary>Writes a pack.json: the uses entries and units (JSON text) given.</summary>
    public static void Pack(GenerationFixture f, string name, string units = "", string uses = "", string extra = "")
    {
        var members = new List<string> { $"\"name\": \"{name}\"", "\"version\": \"1.4.0\"", "\"engine\": \">=1.0\"" };
        if (uses.Length > 0)
            members.Add($"\"uses\": [{uses}]");
        if (extra.Length > 0)
            members.Add(extra);
        if (units.Length > 0)
            members.Add($"\"units\": [{units}]");
        f.WritePackFile(name, "pack.json", "{ " + string.Join(", ", members) + " }");
    }

    /// <summary>One <c>each entity</c> unit writing <c>out/&lt;folder&gt;/...</c>.</summary>
    public static string EntityUnit(string template, string output, string extra = "") =>
        $"{{ \"id\": \"record\", \"template\": \"{template}\", \"for\": \"each entity\", \"output\": \"{output}\"{extra} }}";

    /// <summary>The naming library (helpers and a header partial) and a records pack that uses it.</summary>
    public static async Task<GenerationFixture> CreateAsync(string record = "{{ include \"naming:partials/header.scriban\" }}class {{ entity | naming.class_name }}")
    {
        // The real renderer (the fixture's default is a fake one): includes, namespaces and sandboxes are what is tested.
        var f = await GenerationFixture.CreateAsync(b => Models.Shop(b), s => s with { RendererFactory = EngineServices.Create(s.Options).RendererFactory });
        Pack(f, "naming", extra: "\"parameters\": { \"suffix\": \"Record\" }");
        f.WritePackFile("naming", "helpers.js", NamingHelpers);
        f.WritePackFile("naming", "partials/header.scriban", "// {{ element.name }} ({{ naming.class_name element }})\n");
        Pack(f, "records", EntityUnit("record.scriban", "out/records/{{ naming.class_name entity }}.cs"),
            "{ \"pack\": \"naming\", \"version\": \"^1.2.0\" }", "\"parameters\": { \"suffix\": \"Other\" }");
        f.WritePackFile("records", "record.scriban", record);
        return f;
    }
}

public sealed class LibraryRenderTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_prefixed_include_renders_the_used_partial_with_the_units_variables()
    {
        await using var f = await LibraryPacks.CreateAsync();

        var result = await f.RunAsync();

        Assert.True(result.Outcome == RunOutcome.Succeeded, string.Join("\n", result.Diagnostics.Select(d => $"{d.Rule} {d.FilePath} {d.JsonPointer} {d.Message}")));
        Assert.Equal("// Customer (CustomerRecord)\nclass CustomerRecord", f.Outputs()["out/records/CustomerRecord.cs"]);
        Assert.Equal("// Product (ProductRecord)\nclass ProductRecord", f.Outputs()["out/records/ProductRecord.cs"]);
    }

    [Fact]
    public async Task An_unprefixed_include_in_a_used_partial_resolves_in_that_pack()
    {
        await using var f = await LibraryPacks.CreateAsync();
        f.WritePackFile("naming", "partials/header.scriban", "{{ include \"partials/line.scriban\" }}");
        f.WritePackFile("naming", "partials/line.scriban", "// naming line\n");
        f.WritePackFile("records", "partials/line.scriban", "// records line\n");

        await f.RunAsync();

        Assert.Equal("// naming line\nclass CustomerRecord", f.Outputs()["out/records/CustomerRecord.cs"]);
    }

    [Fact]
    public async Task A_used_partial_parses_with_default_delimiters_under_a_unit_with_custom_ones()
    {
        await using var f = await LibraryPacks.CreateAsync();
        LibraryPacks.Pack(f, "records", LibraryPacks.EntityUnit("record.scriban", "out/records/{{ entity.name }}.cs",
            ", \"delimiters\": { \"open\": \"<%\", \"close\": \"%>\" }"), "{ \"pack\": \"naming\", \"version\": \"^1.2.0\" }");
        f.WritePackFile("records", "record.scriban", "<% include \"naming:partials/header.scriban\" %>{{ kept }} <% element.name %>");

        var result = await f.RunAsync();

        Assert.Equal(RunOutcome.Succeeded, result.Outcome);
        Assert.Equal("// Customer (CustomerRecord)\n{{ kept }} Customer", f.Outputs()["out/records/Customer.cs"]);
    }

    [Fact]
    public async Task A_namespaced_helper_runs_in_the_used_packs_pool_and_pipes()
    {
        await using var f = await LibraryPacks.CreateAsync("{{ entity | naming.class_name }} {{ naming.class_name entity }} {{ suffix entity }}");
        f.WritePackFile("records", "helpers.js", """
            const naming = maquettiste.use('naming');
            maquettiste.helper('suffix', e => maquettiste.params.suffix + '/' + naming.class_name(e));
            """);

        var result = await f.RunAsync();

        Assert.Equal(RunOutcome.Succeeded, result.Outcome);
        Assert.Equal("CustomerRecord CustomerRecord Other/CustomerRecord", f.Outputs()["out/records/CustomerRecord.cs"]);
    }

    [Fact]
    public async Task A_used_pack_reaches_its_own_namespace_in_its_units()
    {
        await using var f = await LibraryPacks.CreateAsync();
        LibraryPacks.Pack(f, "naming", LibraryPacks.EntityUnit("index.scriban", "out/naming/{{ class_name entity }}.txt"),
            extra: "\"parameters\": { \"suffix\": \"Record\" }");
        f.WritePackFile("naming", "index.scriban", "{{ include \"partials/header.scriban\" }}");

        var result = await f.RunAsync();

        Assert.Equal(RunOutcome.Succeeded, result.Outcome);
        Assert.Equal("// Customer (CustomerRecord)\n", f.Outputs()["out/naming/CustomerRecord.txt"]);
        Assert.Contains("out/records/CustomerRecord.cs", f.Outputs().Keys);
    }

    [Fact]
    public async Task An_include_of_a_pack_outside_the_closure_is_MQ6006()
    {
        await using var f = await LibraryPacks.CreateAsync("{{ include \"services:partials/x.scriban\" }}");
        LibraryPacks.Pack(f, "services", LibraryPacks.EntityUnit("x.scriban", "out/services/{{ entity.name }}.cs"));
        f.WritePackFile("services", "x.scriban", "x");
        f.WritePackFile("services", "partials/x.scriban", "x");

        var result = await f.RunAsync();

        var errors = result.Diagnostics.Where(d => d.Rule == "MQ6006").ToList();
        Assert.Equal(2, errors.Count);
        Assert.All(errors, d =>
        {
            Assert.Contains("pack 'services' is not in the uses of pack 'records'", d.Message, StringComparison.Ordinal);
            Assert.Equal(".maquettiste/templates/records/record.scriban", d.FilePath);
        });
        Assert.DoesNotContain(f.Outputs().Keys, k => k.StartsWith("out/records/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_namespace_equal_to_a_root_helper_is_MQ6045()
    {
        await using var f = await LibraryPacks.CreateAsync();
        f.WritePackFile("records", "helpers.js", "maquettiste.helper('naming', () => 1);");

        var result = await f.RunAsync();

        Assert.Contains(result.Diagnostics, d => d.Rule == "MQ6045" && d.FilePath == ".maquettiste/templates/records/helpers.js");
        Assert.Empty(f.Outputs());
    }

    [Fact]
    public async Task A_used_partial_records_its_own_template_key()
    {
        await using var f = await LibraryPacks.CreateAsync();
        await f.RunAsync();

        var states = await new UnitStateStore(f.Repo.Options, new OutputPathPolicy(f.Repo.Options, null)).LoadAsync("records", Ct);

        Assert.All(states.Values, s =>
        {
            Assert.Contains("t:naming/partials/header.scriban", s.ReadKeys);
            Assert.Contains("t:records/record.scriban", s.ReadKeys);
        });
        Assert.Equal(2, states.Count);
    }

    [Fact]
    public async Task A_used_pack_whose_scripts_fail_fails_every_unit_that_sees_it()
    {
        await using var f = await LibraryPacks.CreateAsync();
        LibraryPacks.Pack(f, "services", LibraryPacks.EntityUnit("x.scriban", "out/services/{{ entity.name }}.cs"),
            "{ \"pack\": \"naming\", \"version\": \"1.x\" }");
        f.WritePackFile("services", "x.scriban", "x");
        f.WritePackFile("naming", "helpers.js", "maquettiste.helper('class_name', e => e.name);\nthrow new Error('broken library');");

        var result = await f.RunAsync();

        var failures = result.Diagnostics.Where(d => d.Rule == "MQ6016").ToList();
        Assert.NotEmpty(failures);
        Assert.All(failures, d => Assert.Equal(".maquettiste/templates/naming/helpers.js", d.FilePath));
        Assert.Contains(failures, d => d.Line == 2);
        Assert.Empty(f.Outputs());
    }

    [Fact]
    public async Task A_library_helper_that_throws_under_a_pack_script_names_the_library_file()
    {
        await using var f = await LibraryPacks.CreateAsync("{{ suffix entity }}");
        LibraryPacks.Pack(f, "records", LibraryPacks.EntityUnit("record.scriban", "out/records/{{ entity.name }}.cs"),
            "{ \"pack\": \"naming\", \"version\": \"^1.2.0\" }");
        f.WritePackFile("naming", "helpers.js", "maquettiste.helper('class_name', e => {\n  throw new Error('no class name for ' + e.name);\n});");
        f.WritePackFile("records", "helpers.js", "const naming = maquettiste.use('naming');\nmaquettiste.helper('suffix', e => naming.class_name(e));");

        var result = await f.RunAsync();

        var errors = result.Diagnostics.Where(d => d.Rule == "MQ6016").ToList();
        Assert.Equal(2, errors.Count);
        Assert.All(errors, d =>
        {
            Assert.Equal(".maquettiste/templates/naming/helpers.js", d.FilePath);
            Assert.Equal(2, d.Line);
            Assert.Contains("no class name for ", d.Message, StringComparison.Ordinal);
        });
        Assert.Empty(f.Outputs());
    }

    [Fact]
    public async Task A_selector_calling_a_library_helper_is_MQ6016()
    {
        await using var f = await LibraryPacks.CreateAsync();
        LibraryPacks.Pack(f, "records", "{ \"id\": \"record\", \"template\": \"record.scriban\", \"for\": \"select picked\", \"output\": \"out/records/{{ entity.name }}.cs\" }",
            "{ \"pack\": \"naming\", \"version\": \"^1.2.0\" }");
        f.WritePackFile("records", "helpers.js", """
            maquettiste.selector('picked', model => model.entities.filter(e => maquettiste.use('naming').class_name(e).length > 3));
            """);

        var result = await f.RunAsync();

        var error = Assert.Single(result.Diagnostics, d => d.Rule == "MQ6016");
        Assert.Equal(".maquettiste/templates/records/helpers.js", error.FilePath);
        Assert.Contains("while planning", error.Message, StringComparison.Ordinal);
    }
}
