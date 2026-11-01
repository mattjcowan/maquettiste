using System.Collections.Immutable;
using System.Text.Json;
using Maquettiste.Engine.Generation;
using Maquettiste.Engine.Hashing;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Pipeline;
using Maquettiste.Engine.Planning;
using Maquettiste.Engine.Resolution;
using Maquettiste.Engine.Scripting;
using Maquettiste.Engine.Tests.Generation;
using Maquettiste.Testing;
using static Maquettiste.Engine.Tests.Planning.PlanningKit;

namespace Maquettiste.Engine.Tests.Planning;

/// <summary>Copy units in the matcher and the planner (next-phase-1-design.md section 5.4): MQ6030, MQ6031 and the static hash.</summary>
public sealed class CopyUnitPlanTests : IDisposable
{
    private static readonly ResolvedModel Model = Resolve(Shop().Build());

    private readonly TempRepo _repo = new();

    public CopyUnitPlanTests()
    {
        Write("skeleton/Program.cs", "namespace Skeleton.Api;\n");
        Write("skeleton/Api/Invoices.cs", "namespace Skeleton.Api.Api;\n");
        Write("skeleton/logo.png", "\u0089PNG");
        Write("t.tpl", "x");
    }

    private string Root => Path.Combine(_repo.Root, "pack");

    public void Dispose() => _repo.Dispose();

    private void Write(string path, string text)
    {
        var full = Path.Combine(Root, path.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
    }

    private static PackUnit Copy(IReadOnlyList<string> sources, IReadOnlyDictionary<string, string>? tokens = null) => new()
    {
        Id = "skeleton",
        For = "model",
        Mode = OutputMode.Copy,
        Sources = sources,
        Tokens = tokens ?? ImmutableSortedDictionary<string, string>.Empty,
        Output = "out/app",
    };

    /// <summary>A new pack instance over the folder, so nothing the matcher kept for an earlier instance is reused.</summary>
    private LoadedPack Pack(IReadOnlyList<PackUnit> units, IReadOnlyDictionary<string, JsonElement>? parameters = null,
        IReadOnlyDictionary<string, JsonElement>? defaults = null, JsonElement? parameterSchema = null) => new(
        "p", 0, Root, ".maquettiste/templates/p",
        new PackManifest
        {
            Name = "p",
            Version = "1.0.0",
            Engine = ">=1.0",
            Units = units,
            Parameters = defaults ?? ImmutableDictionary<string, JsonElement>.Empty,
            ParameterSchema = parameterSchema,
        },
        new PackSettings(), parameters ?? defaults ?? ImmutableSortedDictionary<string, JsonElement>.Empty, [], "scripts-p",
        ImmutableSortedDictionary<string, IReadOnlyDictionary<string, string>>.Empty);

    private static Task<UnitPlan> Plan(LoadedPack pack) =>
        new UnitPlanner(Options).PlanAsync(Model, new PackSet([pack], []), new ScriptSandboxFactory(), null, Ct);

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public async Task A_pattern_matching_nothing_is_MQ6030_and_other_units_are_planned()
    {
        var plan = await Plan(Pack([Copy(["skeleton/**", "missing/*.cs"]), Unit("model", "model")]));

        var error = Assert.Single(plan.Diagnostics);
        Assert.Equal(("MQ6030", ".maquettiste/templates/p/pack.json", "/units/0/sources/1"), (error.Rule, error.FilePath, error.JsonPointer));
        Assert.Contains("'missing/*.cs'", error.Message, StringComparison.Ordinal);
        Assert.True(Outcomes.IsUnitLevel(error));
        Assert.True(Outcomes.IsInvalid(error));
        Assert.Equal(["p/model"], plan.Units.Select(u => u.Key));

        var planned = await Plan(Pack([Copy(["skeleton/**"]), Unit("model", "model")]));
        Assert.Empty(planned.Diagnostics);
        Assert.Equal(["p/model", "p/skeleton"], planned.Units.Select(u => u.Key));
    }

    [Fact]
    public async Task A_token_naming_an_undeclared_parameter_is_MQ6031()
    {
        var tokens = new Dictionary<string, string> { ["Skeleton.Api"] = "rootNamespace" };

        var undeclared = await Plan(Pack([Copy(["skeleton/**"], tokens), Unit("model", "model")]));
        var error = Assert.Single(undeclared.Diagnostics);
        Assert.Equal(("MQ6031", "/units/0/tokens/Skeleton.Api"), (error.Rule, error.JsonPointer));
        Assert.Contains("does not declare", error.Message, StringComparison.Ordinal);
        Assert.True(Outcomes.IsUnitLevel(error));
        Assert.Equal(["p/model"], undeclared.Units.Select(u => u.Key));

        // Declared by a default that is not a string, or by the parameter schema alone with no value: still MQ6031.
        var number = await Plan(Pack([Copy(["skeleton/**"], tokens)], defaults: new Dictionary<string, JsonElement> { ["rootNamespace"] = Json("3") }));
        Assert.Contains("a value of type number, not a string", Assert.Single(number.Diagnostics).Message, StringComparison.Ordinal);
        var schemaOnly = await Plan(Pack([Copy(["skeleton/**"], tokens)], parameterSchema: Json("""{ "properties": { "rootNamespace": { "type": "string" } } }""")));
        Assert.Contains("no value, not a string", Assert.Single(schemaOnly.Diagnostics).Message, StringComparison.Ordinal);
        Assert.Empty(schemaOnly.Units);

        // The effective value counts: a project value that is a string makes the unit plan.
        var project = await Plan(Pack([Copy(["skeleton/**"], tokens)], parameters: new Dictionary<string, JsonElement> { ["rootNamespace"] = Json("\"Billing.Api\"") },
            defaults: new Dictionary<string, JsonElement> { ["rootNamespace"] = Json("3") }));
        Assert.Empty(project.Diagnostics);
        Assert.Equal(["p/skeleton"], project.Units.Select(u => u.Key));
    }

    [Fact]
    public async Task Patterns_match_case_sensitively()
    {
        var plan = await Plan(Pack([Copy(["skeleton/*.CS"])]));

        Assert.Equal(("MQ6030", "/units/0/sources/0"), (Assert.Single(plan.Diagnostics).Rule, plan.Diagnostics[0].JsonPointer));
        Assert.Empty(plan.Units);
        Assert.Equal(["skeleton/Program.cs"], CopySources.Of(Pack([Copy(["skeleton/*.cs"])]), Copy(["skeleton/*.cs"])).Files.Select(f => f.Path));
    }

    [Fact]
    public void Sources_are_listed_once_ordinal_with_the_base_of_the_first_pattern_that_matched()
    {
        Write("skeleton/.hidden/secret.cs", "x");
        Write("skeleton/.env", "x");
        var unit = Copy(["skeleton/Api/*.cs", "skeleton/**", "skeleton/logo.png"]);
        var pack = Pack([unit]);

        var sources = CopySources.Of(pack, unit);

        Assert.Empty(sources.Diagnostics);
        Assert.Equal(
            [("skeleton/Api/Invoices.cs", "skeleton/Api/"), ("skeleton/Program.cs", "skeleton/"), ("skeleton/logo.png", "skeleton/")],
            sources.Files.Select(f => (f.Path, f.Base)));
        Assert.Equal(ContentHash.Of(File.ReadAllBytes(Path.Combine(Root, "skeleton", "logo.png"))), sources.Files[2].Hash);
        Assert.Same(sources, CopySources.Of(pack, unit));
        Assert.Empty(CopySources.Of(pack, Unit("model", "model")).Files);
        Assert.Equal("", CopySources.LiteralBase("**/*.cs"));
        Assert.Equal("a/b/", CopySources.LiteralBase("a/b/c?.cs"));
        Assert.Equal("a/", CopySources.LiteralBase("a/logo.png"));
    }

    [Fact]
    public void Editing_adding_or_removing_a_source_changes_the_static_hash()
    {
        var unit = Copy(["skeleton/**"]);
        string Hash() => UnitPlanner.StaticHash(Pack([unit]), unit, [], "p/skeleton");
        var original = Hash();
        Assert.Equal(original, Hash());

        var parts = UnitPlanner.StaticParts(Pack([unit]), unit, []);
        Assert.Contains("source:skeleton/Program.cs=" + ContentHash.Of("namespace Skeleton.Api;\n")[..32], parts, StringComparison.Ordinal);
        Assert.DoesNotContain("template:", parts, StringComparison.Ordinal);

        Write("skeleton/Program.cs", "namespace Skeleton.Api; // edited\n");
        var edited = Hash();
        Assert.NotEqual(original, edited);

        Write("skeleton/Api/Customers.cs", "namespace Skeleton.Api.Api;\n");
        var added = Hash();
        Assert.NotEqual(edited, added);

        File.Delete(Path.Combine(Root, "skeleton", "Api", "Customers.cs"));
        Assert.Equal(edited, Hash());
        File.Delete(Path.Combine(Root, "skeleton", "logo.png"));
        Assert.NotEqual(edited, Hash());

        // Files no pattern matches do not count.
        Write("readme.md", "x");
        Write("skeleton/Program.cs", "namespace Skeleton.Api;\n");
        Write("skeleton/logo.png", "\u0089PNG");
        Assert.Equal(original, Hash());

        // The unit's definition counts too: tokens are part of its canonical form.
        var tokened = Copy(["skeleton/**"], new Dictionary<string, string> { ["Skeleton.Api"] = "rootNamespace" });
        Assert.NotEqual(original, UnitPlanner.StaticHash(Pack([tokened]), tokened, [], "p/skeleton"));
    }

    [Fact]
    public async Task Matched_files_are_copy_sources_and_never_parsed()
    {
        Write("skeleton/Broken.cs", "{{ if }}\n");
        var manifest = Pack([Copy(["skeleton/**"]), Unit("model", "model")]).Manifest;

        var files = await PackFileRoles.DescribeAsync(Root, manifest, Ct);
        var parse = await PackFileRoles.ParseAsync(Root, ".maquettiste/templates/p", manifest, files, Ct);

        Assert.Equal(
            [("skeleton/Api/Invoices.cs", "copy-source"), ("skeleton/Broken.cs", "copy-source"), ("skeleton/Program.cs", "copy-source"),
                ("skeleton/logo.png", "copy-source"), ("t.tpl", "template")],
            files.Select(f => (f.Path, f.Role)));
        Assert.All(files.Where(f => f.Role == "copy-source"), f => Assert.Empty(f.UsedBy));
        Assert.Empty(parse);
    }

    [Fact]
    public async Task Copied_scripts_and_type_maps_stay_copy_sources_and_are_never_loaded()
    {
        // Run as a pack helper, the skeleton's JavaScript would fail the pack (MQ6016); read as a type map, a copied types/*.json
        // that is not an object of strings would fail it (MQ6001).
        await using var f = await GenerationFixture.CreateAsync(b => Models.Shop(b), "basic");
        f.WritePackFile("basic", "skeleton/app.js", "throw new Error('ran');\n");
        f.WritePackFile("basic", "types/app.json", "[1]\n");
        f.WritePackFile("basic", "helpers.js", "function shout(s) { return s.toUpperCase(); }\n");
        f.WritePackFile("basic", "types/csharp.json", "{ \"string\": \"string\" }\n");
        const string Manifest = """
            { "name": "basic", "version": "1.0.0", "engine": ">=1.0", "scripts": SCRIPTS, "units": [
              { "id": "index", "template": "index.tpl", "for": "model" },
              { "id": "skeleton", "for": "model", "mode": "copy", "sources": ["skeleton/**", "types/app.json"], "output": "app" } ] }
            """;
        f.WritePackFile("basic", "pack.json", Manifest.Replace("SCRIPTS", "[]", StringComparison.Ordinal));
        var snapshot = await f.Store.GetSnapshotAsync(Ct);

        var set = await new PackLoader(f.Repo.Options, TestServices.Schemas).LoadAsync(snapshot, null, null, Ct);

        Assert.Empty(set.Diagnostics);
        var pack = Assert.Single(set.Packs);
        Assert.Equal([".maquettiste/templates/basic/helpers.js"], pack.Scripts.Select(s => s.Path));
        Assert.Equal(["csharp"], pack.TypeMaps.Keys);
        var roles = (await PackFileRoles.DescribeAsync(pack.RootPath, pack.Manifest, Ct)).ToDictionary(r => r.Path, r => r.Role);
        Assert.Equal(("copy-source", "copy-source", "script", "type-map"),
            (roles["skeleton/app.js"], roles["types/app.json"], roles["helpers.js"], roles["types/csharp.json"]));

        // A file the scripts list names is a script, and loads, even when a copy unit also copies it.
        f.WritePackFile("basic", "skeleton/app.js", "function app() { return 1; }\n");
        f.WritePackFile("basic", "pack.json", Manifest.Replace("SCRIPTS", "[\"skeleton/app.js\"]", StringComparison.Ordinal));
        var listed = Assert.Single((await new PackLoader(f.Repo.Options, TestServices.Schemas).LoadAsync(snapshot, null, null, Ct)).Packs);
        Assert.Equal([".maquettiste/templates/basic/skeleton/app.js"], listed.Scripts.Select(s => s.Path));
        Assert.Equal("script", (await PackFileRoles.DescribeAsync(listed.RootPath, listed.Manifest, Ct)).Single(r => r.Path == "skeleton/app.js").Role);
    }
}
