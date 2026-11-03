using System.Collections.Frozen;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Pipeline;
using Maquettiste.Engine.Planning;
using Maquettiste.Engine.Resolution;
using Maquettiste.Engine.Tests.Resolution;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Rendering;

/// <summary>Templates and pack helpers read calculated values (next-phase-2-design.md section 5.5).</summary>
public sealed class CalculatedValueReadTests
{
    private const string FileKey = "x:calculated/names.js";

    private static ScriptSource Names(string body = "return entity.name.toLowerCase() + 's';") => CalculationKit.File("names.js",
        "maquettiste.calculated({ name: 'publicName', kinds: ['entity'], schema: { type: 'string' }, value(entity) { " + body + " } });\n" +
        "maquettiste.calculated({ name: 'route', kinds: ['entity'], schema: { type: 'string' }, value(entity) { return '/' + entity.calculated.publicName; } });");

    private static (CalculationKit Kit, ModelSnapshot Snapshot, ResolvedModel Model) Resolve(ModelBuilder builder, params ScriptSource[] files)
    {
        var kit = new CalculationKit();
        var snapshot = CalculationKit.Snapshot(builder, files);
        return (kit, snapshot, kit.Resolve(snapshot));
    }

    private static Task<RenderedUnit> Render(string template, ResolvedModel model, string entity = "Invoice",
        IReadOnlyDictionary<string, string>? files = null) =>
        Adhoc.RenderAsync(template, model.Entity(entity), model: model, files: files);

    private static DependencyHasher Hasher(ModelSnapshot snapshot, ResolvedModel model) =>
        new(snapshot, model, new PackSet([], []), FrozenDictionary<string, Maquettiste.Engine.SchemaDiff.SchemaDiffResult>.Empty);

    [Fact]
    public async Task A_template_reads_a_calculated_value_and_records_its_file_and_keys()
    {
        var (_, _, model) = Resolve(CalculationKit.Billing(), Names());

        var unit = await Render("{{ element.calculated.publicName }} {{ element.calculated.route }}|{{ for e in element.calculated }}{{ e.key }}={{ e.value }};{{ end }}|{{ json element.calculated }}", model);

        Assert.Equal("invoices /invoices|publicName=invoices;route=/invoices;|{\"publicName\":\"invoices\",\"route\":\"/invoices\"}", unit.Text());
        Assert.Contains(FileKey, unit.ReadKeys);
        Assert.Contains("e:" + model.Entity("Invoice").Id, unit.ReadKeys);
        Assert.Contains(CalculationHost.DeclarationKey("entity"), unit.ReadKeys); // the loop listed the names
    }

    [Fact]
    public async Task A_unit_that_reads_a_calculated_value_is_skipped_until_the_calculated_value_changes()
    {
        var builder = CalculationKit.Billing;
        var (_, before, beforeModel) = Resolve(builder(), Names());
        var unit = await Render("{{ element.calculated.publicName }}", beforeModel);
        Assert.DoesNotContain(CalculationHost.DeclarationKey("entity"), unit.ReadKeys); // a plain read does not list the names
        var inputs = Hasher(before, beforeModel).InputHash("static", unit.ReadKeys);

        string After(ModelBuilder changed, ScriptSource file)
        {
            var (_, snapshot, model) = Resolve(changed, file);
            return Hasher(snapshot, model).InputHash("static", unit.ReadKeys);
        }

        Assert.Equal(inputs, After(builder(), Names()));
        Assert.Equal(inputs, After(CalculationKit.Billing(line: false), Names())); // another entity's change is not read
        Assert.NotEqual(inputs, After(CalculationKit.Billing(invoice: "Bill"), Names()));
        Assert.NotEqual(inputs, After(builder(), Names("return entity.name.toUpperCase();")));
    }

    [Fact]
    public async Task An_undeclared_name_fails_the_unit_with_MQ5012()
    {
        var (_, _, model) = Resolve(CalculationKit.Billing(), Names());

        var error = (await Render("line one\n{{ element.calculated.missing }}", model)).Error();

        Assert.Equal("MQ5012", error.Rule);
        Assert.Equal(2, error.Line);
        Assert.EndsWith("main.scriban", error.FilePath, StringComparison.Ordinal);
        Assert.Contains("'missing'", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Has_calculated_answers_without_evaluating()
    {
        var (kit, _, model) = Resolve(CalculationKit.Billing(), Names());

        var unit = await Render("{{ has_calculated element 'publicName' }} {{ has_calculated element 'missing' }} {{ has_calculated element.attributes[0] 'publicName' }}", model);

        Assert.Equal("true false false", unit.Text());
        Assert.Equal(0, kit.Factory.CalculationCalls);
        Assert.Contains(CalculationHost.DeclarationKey("entity"), unit.ReadKeys);
        Assert.Contains(CalculationHost.DeclarationKey("attribute"), unit.ReadKeys);
        Assert.DoesNotContain(FileKey, unit.ReadKeys);
    }

    [Fact]
    public async Task A_unit_that_tested_for_a_calculated_value_renders_again_when_a_file_declares_it()
    {
        var (_, without, withoutModel) = Resolve(CalculationKit.Billing());
        var unit = await Render("{{ if has_calculated element 'publicName' }}yes{{ else }}no{{ end }}", withoutModel);
        Assert.Equal("no", unit.Text());
        var key = CalculationHost.DeclarationKey("entity");
        Assert.Contains(key, unit.ReadKeys);

        var (_, with, withModel) = Resolve(CalculationKit.Billing(), Names());
        Assert.NotEqual(Hasher(without, withoutModel).InputHash("static", unit.ReadKeys), Hasher(with, withModel).InputHash("static", unit.ReadKeys));
        Assert.Equal("yes", (await Render("{{ if has_calculated element 'publicName' }}yes{{ else }}no{{ end }}", withModel)).Text());
    }

    [Fact]
    public async Task A_helper_reads_calculated_values_through_the_proxy()
    {
        var (_, _, model) = Resolve(CalculationKit.Billing(), Names());
        var files = new Dictionary<string, string>
        {
            ["helpers.js"] = "maquettiste.helper('pub', (e) => e.calculated.publicName + ':' + ('route' in e.calculated) + ':' + Object.keys(e.calculated).join(','));" +
                             "maquettiste.helper('safe', (e) => { try { return e.calculated.publicName; } catch (x) { return 'caught'; } });",
        };

        var unit = await Render("{{ pub element }}", model, files: files);
        Assert.Equal("invoices:true:publicName,route", unit.Text());
        Assert.Contains(FileKey, unit.ReadKeys);

        // A value that failed fails the unit with that value's diagnostic, even when the helper caught the error.
        var (_, _, failing) = Resolve(CalculationKit.Billing(), Names("throw new Error('no name');"));
        var error = (await Render("{{ safe element }}", failing, files: files)).Diagnostics.First();
        Assert.Equal("MQ5010", error.Rule);
        Assert.Equal(CalculationKit.Folder + "names.js", error.FilePath);
    }

    [Fact]
    public async Task A_plain_helper_gets_every_value_at_hop_0_only()
    {
        var (_, _, model) = Resolve(CalculationKit.Billing(), Names());
        var files = new Dictionary<string, string>
        {
            ["helpers.js"] = "maquettiste.helper('flat', (e) => JSON.stringify(e.calculated) + '|' + (e.attributes[0].calculated === undefined), { plain: true });",
        };

        var unit = await Render("{{ flat element }}", model, files: files);

        Assert.Equal("{\"publicName\":\"invoices\",\"route\":\"/invoices\"}|true", unit.Text());
    }
}
