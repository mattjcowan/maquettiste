using System.Text.Json;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Planning;
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

        Assert.Empty(set.Diagnostics);
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
    [InlineData("{ \"name\": \"basic\", \"version\": \"1.0.0\", \"engine\": \">=1.0\", \"units\": [ { \"id\": \"a\", \"template\": \"missing.tpl\", \"for\": \"model\" } ] }", "MQ6001", "/units/0/template")]
    [InlineData("{ \"name\": \"basic\", \"version\": \"1.0.0\", \"engine\": \">=1.0\", \"units\": [ { \"id\": \"a\", \"template\": \"../escape.tpl\", \"for\": \"model\" } ] }", "MQ6001", "/units/0/template")]
    [InlineData("{ \"name\": \"basic\", \"version\": \"1.0.0\", \"engine\": \">=1.0\", \"units\": [ { \"id\": \"a\", \"template\": \"index.tpl\", \"for\": \"model\" }, { \"id\": \"a\", \"template\": \"index.tpl\", \"for\": \"model\" } ] }", "MQ6001", "/units/1/id")]
    [InlineData("{ \"name\": \"basic\", \"version\": \"1.0.0\", \"engine\": \">=1.0\", \"units\": [ { \"id\": \"a\", \"template\": \"index.tpl\", \"for\": \"model\", \"where\": { \"tags\": [\"x\"] } } ] }", "MQ6001", "/units/0/where")]
    [InlineData("{ \"name\": \"basic\", \"version\": \"1.0.0\", \"engine\": \">=1.0\", \"units\": [ { \"id\": \"a\", \"template\": \"index.tpl\", \"for\": \"each thing\" } ] }", "MQ6001", "/units/0/for")]
    [InlineData("{ \"name\": \"basic\", \"version\": \"1.0.0\", \"engine\": \">=1.0\", \"scripts\": [\"nope.js\"], \"units\": [ { \"id\": \"a\", \"template\": \"index.tpl\", \"for\": \"model\" } ] }", "MQ6001", "/scripts/0")]
    public async Task Invalid_pack_json_is_reported_and_the_pack_left_out(string json, string rule, string pointer)
    {
        await using var f = await GenerationFixture.CreateAsync(b => Models.Shop(b), "basic");
        f.WritePackFile("basic", "pack.json", json);
        var snapshot = await f.Store.GetSnapshotAsync(Ct);

        var set = await new PackLoader(f.Repo.Options, TestServices.Schemas).LoadAsync(snapshot, null, null, Ct);

        Assert.Empty(set.Packs);
        Assert.Contains(set.Diagnostics, d => d.Rule == rule && d.JsonPointer == pointer && d.FilePath == ".maquettiste/templates/basic/pack.json");
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
}
