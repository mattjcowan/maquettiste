using Maquettiste.Engine.Model;
using Maquettiste.Engine.Pipeline;

namespace Maquettiste.Engine.Tests.Rendering;

/// <summary>File blocks (D10), pair companions, and the template loop and recursion limits.</summary>
public sealed class FileBlockAndLimitTests
{
    [Fact]
    public async Task A_unit_without_output_emits_zero_or_many_files_with_their_own_paths()
    {
        var none = await Adhoc.RenderAsync("{{ if false }}{{ file \"never.txt\" \"x\" }}{{ end }}\n", unit: u => u with { Output = null });
        Assert.False(none.Failed);
        Assert.Empty(none.Files);
        Assert.Empty(none.Diagnostics);

        var many = await Adhoc.RenderAsync(
            "{{ for i in 1..3 }}{{ capture body }}line {{ i }}\r\nnext{{ end }}{{ file (\"blocks/\" + i + \".txt\") body }}{{ end }}",
            unit: u => u with { Output = null }, settings: new PackSettings { Output = "gen" });
        Assert.Equal(["gen/blocks/1.txt", "gen/blocks/2.txt", "gen/blocks/3.txt"], many.Files.Select(f => f.Path));
        Assert.All(many.Files, f => Assert.Equal(FileRole.Block, f.Role));
        Assert.Equal("line 2\nnext", many.Files[1].Text);
        Assert.Empty(many.Diagnostics);
    }

    [Fact]
    public async Task Text_outside_file_blocks_without_an_output_is_a_warning()
    {
        var unit = await Adhoc.RenderAsync("stray text {{ file \"a.txt\" \"A\" }}", unit: u => u with { Output = null });
        Assert.False(unit.Failed);
        Assert.Equal(["a.txt"], unit.Files.Select(f => f.Path));
        var warning = Assert.Single(unit.Diagnostics);
        Assert.Equal("MQ6011", warning.Rule);
        Assert.Equal(Diagnostics.DiagnosticSeverity.Warning, warning.Severity);
        Assert.Equal(".maquettiste/templates/adhoc/main.scriban", warning.FilePath);

        var model = await BillingModel.GetAsync();
        var off = await Adhoc.RenderAsync("stray", unit: u => u with { Output = null },
            model: Adhoc.WithSettings(model, s => s with { Validation = new ValidationSettings { Rules = new Dictionary<string, string> { ["MQ6011"] = "off" } } }));
        Assert.Empty(off.Diagnostics);
        var info = await Adhoc.RenderAsync("stray", unit: u => u with { Output = null },
            model: Adhoc.WithSettings(model, s => s with { Validation = new ValidationSettings { Rules = new Dictionary<string, string> { ["MQ6011"] = "info" } } }));
        Assert.Equal(Diagnostics.DiagnosticSeverity.Info, Assert.Single(info.Diagnostics).Severity);
        Assert.False(info.Failed);
    }

    [Fact]
    public async Task File_blocks_come_after_the_main_output_and_the_companion()
    {
        var files = new Dictionary<string, string> { ["companion.scriban"] = "companion {{ unit.id }}" };
        var unit = await Adhoc.RenderAsync("main{{ file \"extra.txt\" \"block\" }}", files: files, unit: u => u with
        {
            Output = "main.g.txt",
            Mode = OutputMode.Pair,
            Companion = new PairCompanion { Template = "companion.scriban", Output = "{{ 'main' }}.txt" },
        });

        Assert.Equal(["main.g.txt", "main.txt", "extra.txt"], unit.Files.Select(f => f.Path));
        Assert.Equal([FileRole.Main, FileRole.Companion, FileRole.Block], unit.Files.Select(f => f.Role));
        Assert.Equal("companion main", unit.Files[1].Text);
        Assert.Contains("t:adhoc/companion.scriban", unit.ReadKeys);
    }

    [Fact]
    public async Task Empty_block_paths_and_empty_output_paths_fail_the_unit()
    {
        var block = (await Adhoc.RenderAsync("{{ file \"  \" \"x\" }}", unit: u => u with { Output = null })).Error();
        Assert.Equal("MQ6006", block.Rule);
        Assert.Equal(1, block.Line);

        var output = (await Adhoc.RenderAsync("x", unit: u => u with { Output = "{{ '' }}" })).Error();
        Assert.Equal("MQ6006", output.Rule);
    }

    [Fact]
    public async Task Output_expression_parse_errors_point_at_pack_json()
    {
        var unit = await Adhoc.RenderAsync("x", unit: u => u with { Output = "out/{{ for }}.txt" });
        Assert.True(unit.Failed);
        Assert.All(unit.Diagnostics, d =>
        {
            Assert.Equal("MQ6003", d.Rule);
            Assert.Equal(".maquettiste/templates/adhoc/pack.json", d.FilePath);
            Assert.Equal("/units/0/output", d.JsonPointer);
        });
    }

    [Fact]
    public async Task The_loop_limit_fails_the_unit_with_MQ6007()
    {
        var model = Adhoc.WithSettings(await BillingModel.GetAsync(), s => s with { Limits = s.Limits with { TemplateLoopLimit = 50 } });
        var ok = await Adhoc.RenderAsync("{{ for i in 1..50 }}.{{ end }}", model: model);
        Assert.Equal(new string('.', 50), ok.Text());

        var error = (await Adhoc.RenderAsync("a\n{{ for i in 1..51 }}.{{ end }}", model: model)).Error();
        Assert.Equal("MQ6007", error.Rule);
        Assert.Equal(2, error.Line);
        Assert.Contains("LoopLimit", error.Message, StringComparison.Ordinal);

        var list = (await Adhoc.RenderAsync("{{ for e in model.entities }}{{ for a in e.attributes }}{{ for b in e.attributes }}{{ end }}{{ end }}{{ end }}", model: model)).Error();
        Assert.Equal("MQ6007", list.Rule);
        Assert.Contains("iteration limit", list.Message, StringComparison.Ordinal);

        var endless = (await Adhoc.RenderAsync("{{ while true }}{{ end }}", model: model)).Error();
        Assert.True(endless.Rule == "MQ6007", endless.Message);

        var nested = (await Adhoc.RenderAsync("{{ for i in 1..10 }}{{ for j in 1..10 }}{{ end }}{{ end }}", model: model)).Error();
        Assert.True(nested.Rule == "MQ6007", nested.Message);
    }

    [Fact]
    public async Task The_recursion_limit_fails_the_unit_with_MQ6007()
    {
        var model = Adhoc.WithSettings(await BillingModel.GetAsync(), s => s with { Limits = s.Limits with { TemplateRecursionLimit = 10 } });
        var ok = await Adhoc.RenderAsync("{{ func down(n) }}{{ if n > 0 }}{{ down(n - 1) }}{{ end }}{{ end }}{{ down(3) }}done", model: model);
        Assert.Equal("done", ok.Text());

        var error = (await Adhoc.RenderAsync("{{ func forever(n) }}{{ forever(n + 1) }}{{ end }}\n{{ forever(0) }}", model: model)).Error();
        Assert.Equal("MQ6007", error.Rule);
        Assert.Contains("recursive", error.Message, StringComparison.Ordinal);

        var files = new Dictionary<string, string> { ["_self.scriban"] = "{{ include \"_self.scriban\" }}" };
        var include = (await Adhoc.RenderAsync("{{ include \"_self.scriban\" }}", files: files, model: model)).Error();
        Assert.Equal("MQ6007", include.Rule);
        Assert.Equal(".maquettiste/templates/adhoc/_self.scriban", include.FilePath);
    }
}
