using System.Collections.Immutable;
using System.Text.Json;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Resolution;
using Maquettiste.Engine.Tests.Resolution;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Rendering;

/// <summary>Templates read a table's annotations from its own file (the billing fixture's invoice table overlay), never the entity's.</summary>
public sealed class PhysicalAnnotationRenderTests
{
    private const string Template =
        "{{ table.display_name }}|{{ table.description }}|{{ table.stereotypes.size }}:{{ table.stereotypes[0].key }}/{{ table.stereotypes[0].name }}|"
        + "{{ table.tags | array.join ',' }}|{{ table.category.name }}/{{ table.category.path }}|{{ table.properties.tablespace }}/{{ table.properties.retentionDays }}|"
        + "{{ has_stereotype table 'audited' }}/{{ has_tag table 'billing' }}/{{ in_category table 'Receivables' }}|{{ table.entity.display_name }}";

    [Fact]
    public async Task A_table_reads_the_annotations_of_its_overlay()
    {
        var unit = await Adhoc.RenderAsync(Template, await TableAsync("invoices"), u => u with { For = "each table" });

        Assert.Equal("Invoice register|One row per issued invoice; finance reconciles it monthly.|1:audited/Audited|billing|Receivables/Receivables|"
            + "billing_data/2555|true/true/true|Invoice", unit.Text());
    }

    [Fact]
    public async Task A_table_without_a_file_has_no_annotations_even_when_its_entity_has()
    {
        var table = await TableAsync("customers");
        Assert.True(table.Entity!.HasStereotype("audited"));

        var unit = await Adhoc.RenderAsync(
            "[{{ table.display_name }}|{{ table.description }}|{{ table.stereotypes.size }}|{{ table.tags.size }}|{{ table.category }}|{{ table.properties.tablespace }}|"
            + "{{ has_stereotype table 'audited' }}|{{ has_stereotype table.entity 'audited' }}]",
            table, u => u with { For = "each table" });

        Assert.Equal("[||0|0|||false|true]", unit.Text());
    }

    [Fact]
    public async Task A_table_unit_sees_its_table_files_generation_hints()
    {
        var b = new ModelBuilder(seed: 67);
        var db = b.Database("main", Dialect.PostgreSql);
        var variables = ImmutableDictionary<string, JsonElement>.Empty.Add("audience", JsonDocument.Parse("\"finance\"").RootElement);
        b.Add(new Table
        {
            Id = b.NewId(), Name = "ledger", Database = db.Id, Origin = TableOrigin.Designed,
            Generation = new Dictionary<string, GenerationHints> { ["adhoc"] = new() { Rename = "LedgerRow", Variables = variables } },
            Columns = [new Column { Id = b.NewId(), Name = "id", Type = "int64", Nullable = false }],
        });
        var model = ResolutionKit.Resolve(b);

        var unit = await Adhoc.RenderAsync("{{ hints.rename }}|{{ hints.variables.audience }}|{{ table.generation.adhoc.rename }}",
            model.Db("main").Table("ledger"), u => u with { For = "each table" }, model: model);

        Assert.Equal("LedgerRow|finance|LedgerRow", unit.Text());
    }

    private static async Task<RTable> TableAsync(string name) =>
        (await BillingModel.GetAsync()).Databases.Single(d => d.Name == "main").Tables.Single(t => t.Name == name);
}
