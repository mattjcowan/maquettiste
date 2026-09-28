using Maquettiste.Engine.Model;
using Maquettiste.Engine.Rendering;
using Scriban;
using Scriban.Runtime;

namespace Maquettiste.Engine.Tests.Rendering;

/// <summary>Custom delimiters (D11): translation to {{ }} with escape blocks, raw blocks, strings, comments and positions.</summary>
public sealed class DelimiterTranslatorTests
{
    private static string Render(string source, string open, string close, object? model = null)
    {
        var translated = DelimiterTranslator.Translate(source, open, close);
        var template = Template.Parse(translated.Text);
        Assert.False(template.HasErrors, string.Join("; ", template.Messages) + "\n" + translated.Text);
        var globals = new ScriptObject();
        if (model is not null)
            globals.Import(model);
        var context = new TemplateContext { NewLine = "\n", StrictVariables = true };
        context.PushGlobal(globals);
        return template.Render(context);
    }

    [Fact]
    public void Handlebars_like_text_passes_through_and_code_spans_render()
    {
        var source = "{{!-- <% banner %> --}}\n<h1>{{title}}</h1>\n{{#each items}}<% for x in list %>[{{this.<% x %>}}]<% end %>{{/each}}\n";
        var output = Render(source, "<%", "%>", new { banner = "B", list = new[] { "a", "b" } });
        Assert.Equal("{{!-- B --}}\n<h1>{{title}}</h1>\n{{#each items}}[{{this.a}}][{{this.b}}]{{/each}}\n", output);
    }

    [Fact]
    public void Jsx_like_text_with_double_braces_and_expressions_passes_through()
    {
        var source = "<div style={{ padding: 8 }} onClick={() => go({ id: 1 })}>[% name %]</div>\n{items.map((i) => <I key={i} />)}\n{`${a}`}{";
        var output = Render(source, "[%", "%]", new { name = "Card" });
        Assert.Equal("<div style={{ padding: 8 }} onClick={() => go({ id: 1 })}>Card</div>\n{items.map((i) => <I key={i} />)}\n{`${a}`}{", output);
    }

    [Fact]
    public void Raw_blocks_pass_literal_braces_and_literal_delimiters_through()
    {
        var source = "a {%{ <% not code %> and {{ braces }} }%} b <% 1 + 1 %> {%%{ }%} inside }%%} c";
        Assert.Equal("a  <% not code %> and {{ braces }}  b 2  }%} inside  c", Render(source, "<%", "%>"));
    }

    [Fact]
    public void Escape_fences_grow_so_text_cannot_close_them()
    {
        var source = "x }%} y }%%} {{z}} <% 'ok' %>";
        var translated = DelimiterTranslator.Translate(source, "<%", "%>");
        Assert.Contains("{%%%{", translated.Text, StringComparison.Ordinal);
        Assert.Equal("x }%} y }%%} {{z}} ok", Render(source, "<%", "%>"));
    }

    [Fact]
    public void Close_delimiter_inside_a_string_or_comment_does_not_end_the_span()
    {
        Assert.Equal("A %> B|", Render("<% \"a %> b\" | string.upcase %>|", "<%", "%>"));
        Assert.Equal("x '%>' y", Render("x <% '\\'%>\\'' %> y", "<%", "%>"));
        Assert.Equal("after", Render("<% # it's a comment %>after", "<%", "%>"));
        Assert.Equal("2", Render("<% `%>` | string.size %>", "<%", "%>"));
    }

    [Fact]
    public void Whitespace_control_still_trims_around_escaped_text()
    {
        var source = "<ul>\n  <%- for x in list %>\n  <li>{{<% x %>}}</li>\n  <%- end %>\n</ul>";
        Assert.Equal("<ul>\n  <li>{{a}}</li>\n  <li>{{b}}</li>\n</ul>", Render(source, "<%", "%>", new { list = new[] { "a", "b" } }));
        Assert.Equal("-->{{x}}<!--", Render("<% '' %>-->{{x}}<!--", "<%", "%>"));
        Assert.Equal("a-{{b}}-~c", Render("a<% '-' %>{{b}}-~<% 'c' %>", "<%", "%>"));
    }

    [Fact]
    public void Default_delimiters_need_no_translation()
    {
        Assert.False(DelimiterTranslator.NeedsTranslation("{{", "}}"));
        Assert.True(DelimiterTranslator.NeedsTranslation("<%", "%>"));
    }

    [Fact]
    public void Unterminated_spans_report_their_offset()
    {
        var ex = Assert.Throws<DelimiterException>(() => DelimiterTranslator.Translate("line\n  <% oops", "<%", "%>"));
        Assert.Equal(7, ex.Offset);
        Assert.Throws<DelimiterException>(() => DelimiterTranslator.Translate("{%{ never closed", "<%", "%>"));
        Assert.Throws<DelimiterException>(() => DelimiterTranslator.Translate("x", "", "%>"));
    }

    [Fact]
    public void Positions_map_back_to_the_source()
    {
        var source = "{{a}} x\n  {{b}} <% y %> {{c}}\nend";
        var translated = DelimiterTranslator.Translate(source, "<%", "%>");
        Assert.Equal(source.Split('\n').Length, translated.Text.Split('\n').Length);
        var line = translated.Text.Split('\n')[1];
        var y = line.IndexOf(" y ", StringComparison.Ordinal) + 1;
        Assert.Equal((2, 12), translated.Map.Map(1, y));
        Assert.Equal((1, 1), translated.Map.Map(0, 0));
    }

    [Fact]
    public async Task Render_errors_in_translated_templates_point_at_the_source_line_and_column()
    {
        var unit = await Adhoc.RenderAsync("{{title}}\n  {{x}} <% nope_var %>\n", unit: u => u with { Delimiters = new Delimiters { Open = "<%", Close = "%>" } });
        var error = unit.Error();
        Assert.Equal("MQ6006", error.Rule);
        Assert.Equal(".maquettiste/templates/adhoc/main.scriban", error.FilePath);
        Assert.Equal(2, error.Line);
        Assert.Equal(12, error.Column);
    }

    [Fact]
    public async Task Parse_errors_in_translated_templates_point_at_the_source()
    {
        var unit = await Adhoc.RenderAsync("{{ok}}\n\n <% if %>", unit: u => u with { Delimiters = new Delimiters { Open = "<%", Close = "%>" } });
        Assert.True(unit.Failed);
        Assert.All(unit.Diagnostics, d => Assert.Equal("MQ6003", d.Rule));
        Assert.Contains(unit.Diagnostics, d => d.Line == 3 && d.Column >= 2);

        var open = await Adhoc.RenderAsync("a\n <% never closed", unit: u => u with { Delimiters = new Delimiters { Open = "<%", Close = "%>" } });
        var unterminated = open.Error();
        Assert.Equal("MQ6003", unterminated.Rule);
        Assert.Equal((2, 2), (unterminated.Line, unterminated.Column));
    }

    [Fact]
    public async Task Units_with_custom_delimiters_render_through_the_renderer()
    {
        var unit = await Adhoc.RenderAsync("<h1>{{title}}</h1> <% pascal \"invoice line\" %> {%{<% raw %>}%}",
            unit: u => u with { Delimiters = new Delimiters { Open = "<%", Close = "%>" } });
        Assert.Equal("<h1>{{title}}</h1> InvoiceLine <% raw %>", unit.Text());
    }
}
