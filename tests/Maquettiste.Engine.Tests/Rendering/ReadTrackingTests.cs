using Maquettiste.Engine.Model;
using Maquettiste.Engine.Pipeline;
using Maquettiste.Engine.Resolution;
using Maquettiste.Engine.SchemaDiff;

namespace Maquettiste.Engine.Tests.Rendering;

/// <summary>
/// The tracking context records exactly what a template read (engine-design.md section 9): an object's dependencies on any member
/// read, a list's membership keys on enumeration, indexing or size, <c>e:&lt;id&gt;</c> for a missing lookup, and template keys.
/// </summary>
public sealed class ReadTrackingTests
{
    private const string MainKey = "t:adhoc/main.scriban";

    private static IReadOnlyList<string> Sorted(IEnumerable<string> keys) => [.. keys.Distinct().Order(StringComparer.Ordinal)];

    [Fact]
    public async Task A_template_that_reads_nothing_records_only_its_template()
    {
        var unit = await Adhoc.RenderAsync("static text");
        Assert.Equal([MainKey], unit.ReadKeys);
        Assert.Equal(new TestHasher().InputHash("static:adhoc/main", [MainKey]), unit.InputHash);
    }

    [Fact]
    public async Task Reading_a_member_records_that_element_and_no_other()
    {
        var model = await BillingModel.GetAsync();
        var invoice = model.Entities.Single(e => e.Name == "Invoice");
        var unit = await Adhoc.RenderAsync("{{ entity.name }} {{ entity.display_name }}", invoice);

        Assert.Equal("Invoice Invoice", unit.Text());
        Assert.Equal(Sorted(invoice.Dependencies.Append(MainKey)), unit.ReadKeys);
        foreach (var other in model.Entities.Where(e => e != invoice))
            Assert.DoesNotContain("e:" + other.Id, unit.ReadKeys);
    }

    [Fact]
    public async Task Holding_an_element_without_reading_it_records_nothing()
    {
        var model = await BillingModel.GetAsync();
        var invoice = model.Entities.Single(e => e.Name == "Invoice");
        var unit = await Adhoc.RenderAsync("{{ x = entity }}{{ if x }}yes{{ end }}", invoice);
        Assert.Equal("yes", unit.Text());
        Assert.Equal([MainKey], unit.ReadKeys);
    }

    [Fact]
    public async Task Enumerating_indexing_or_sizing_a_list_records_its_membership_keys()
    {
        var model = await BillingModel.GetAsync();
        var membership = model.Entities.MembershipKeys;

        var loop = await Adhoc.RenderAsync("{{ for e in model.entities }}.{{ end }}");
        Assert.Equal(new string('.', model.Entities.Count), loop.Text());
        Assert.Equal(Sorted(membership.Append(MainKey)), loop.ReadKeys);

        var size = await Adhoc.RenderAsync("{{ model.entities.size }} {{ model.entities | array.size }}");
        Assert.Equal(Sorted(membership.Append(MainKey)), size.ReadKeys);

        var first = model.Entities[0];
        var index = await Adhoc.RenderAsync("{{ model.entities[0].name }}");
        Assert.Equal(first.Name, index.Text());
        Assert.Equal(Sorted(membership.Concat(first.Dependencies).Append(MainKey)), index.ReadKeys);
    }

    [Fact]
    public async Task Nested_reads_record_every_object_on_the_path()
    {
        var model = await BillingModel.GetAsync();
        var invoice = model.Entities.Single(e => e.Name == "Invoice");
        var attribute = invoice.Attributes[0];
        var unit = await Adhoc.RenderAsync("{{ entity.attributes[0].name }}:{{ entity.package.qualified_name }}", invoice);

        Assert.Equal(attribute.Name + ":" + invoice.Package!.QualifiedName, unit.Text());
        var expected = invoice.Dependencies.Concat(invoice.Attributes.MembershipKeys).Concat(attribute.Dependencies)
            .Concat(invoice.Package.Dependencies).Append(MainKey);
        Assert.Equal(Sorted(expected), unit.ReadKeys);
    }

    [Fact]
    public async Task Every_resolved_object_records_its_dependencies_when_read()
    {
        var model = await BillingModel.GetAsync();
        var objects = model.ById.Values.OrderBy(o => o.Id, StringComparer.Ordinal).ToList();
        using var pack = new TempPack("adhoc", new Dictionary<string, string> { ["main.scriban"] = "{{ element.id }}|{{ element.dependencies }}|{{ element.membership_keys }}" });
        var packUnit = new PackUnit { Id = "main", Template = "main.scriban", For = "select all", Output = "out.txt" };
        var loaded = pack.Load(RenderKit.Manifest("adhoc", packUnit));
        var units = objects.Select(o => new PlannedUnit("adhoc/main:" + o.Id, loaded, packUnit, o, "s")).ToList();

        var rendered = await RenderKit.RenderAllAsync(RenderKit.NewRenderer(), units, RenderKit.Context(model, [loaded], 4));

        Assert.Equal(objects.Count, rendered.Count);
        foreach (var (unit, obj) in rendered.Zip(objects))
        {
            Assert.Equal(obj.Id + "||", unit.Text());
            Assert.Equal(Sorted(obj.Dependencies.Append(MainKey)), unit.ReadKeys);
        }
    }

    [Fact]
    public async Task Lookup_records_the_missing_id_or_the_found_object()
    {
        var model = await BillingModel.GetAsync();
        var invoice = model.Entities.Single(e => e.Name == "Invoice");
        var missing = await Adhoc.RenderAsync("{{ lookup \"01J92P0V0000000000000000ZZ\" }}");
        Assert.Equal("", missing.Text());
        Assert.Equal(Sorted(["e:01J92P0V0000000000000000ZZ", MainKey]), missing.ReadKeys);

        var found = await Adhoc.RenderAsync($"{{{{ if lookup \"{invoice.Id}\" }}}}found{{{{ end }}}}");
        Assert.Equal("found", found.Text());
        Assert.Equal(Sorted(invoice.Dependencies.Append(MainKey)), found.ReadKeys);
    }

    [Fact]
    public async Task Mapping_and_hints_record_the_element_they_come_from()
    {
        var model = await BillingModel.GetAsync();
        var invoice = model.Entities.Single(e => e.Name == "Invoice");
        var mapping = invoice.Mappings["main"];
        var unit = await Adhoc.RenderAsync("{{ mapping.table.name }}", invoice);

        Assert.Equal(mapping.Table.Name, unit.Text());
        Assert.Equal(Sorted(invoice.Dependencies.Concat(mapping.Table.Dependencies).Append(MainKey)), unit.ReadKeys);

        var hints = await Adhoc.RenderAsync("{{ hints.skip }}", invoice);
        Assert.Equal("false", hints.Text());
        Assert.Equal(Sorted(invoice.Dependencies.Append(MainKey)), hints.ReadKeys);

        var mappings = await Adhoc.RenderAsync("{{ for m in mappings }}{{ m.key }}{{ end }}", invoice);
        Assert.Equal("main", mappings.Text());
        Assert.Equal(Sorted(invoice.Dependencies.Append(MainKey)), mappings.ReadKeys);
    }

    [Fact]
    public async Task Schema_diffs_record_the_database_diff_key()
    {
        var model = await BillingModel.GetAsync();
        var main = model.Databases.Single(d => d.Name == "main");
        var diffs = new Dictionary<string, SchemaDiffResult>(StringComparer.Ordinal) { ["main"] = new("main", 3, 4, false, "hash", [], [], []) };
        var unit = await Adhoc.RenderAsync("{{ schema_diff.main.to_revision }} {{ schema_diff.main.is_empty }}", diffs: diffs);

        Assert.Equal("4 false", unit.Text());
        Assert.Equal(Sorted([.. main.Dependencies, "d:" + main.Id, "k:database", MainKey]), unit.ReadKeys);
    }

    [Fact]
    public async Task A_schema_diff_miss_records_the_database_set_and_every_database()
    {
        var model = await BillingModel.GetAsync();
        var unit = await Adhoc.RenderAsync("{{ if schema_diff.reporting }}yes{{ else }}no{{ end }}");

        Assert.Equal("no", unit.Text());
        Assert.Equal(Sorted([.. model.Databases.SelectMany(d => d.Dependencies), "k:database", MainKey]), unit.ReadKeys);
    }

    [Theory]
    [InlineData("{{ for d in schema_diff }}{{ d.key }}{{ end }}")]
    [InlineData("{{ schema_diff | object.size }}")]
    [InlineData("{{ schema_diff | object.keys | array.size }}")]
    public async Task Enumerating_schema_diffs_records_the_database_set(string template)
    {
        var none = await Adhoc.RenderAsync(template);
        Assert.False(none.Failed);
        Assert.Equal(Sorted(["k:database", MainKey]), none.ReadKeys);

        var model = await BillingModel.GetAsync();
        var main = model.Databases.Single(d => d.Name == "main");
        var diffs = new Dictionary<string, SchemaDiffResult>(StringComparer.Ordinal) { ["main"] = new("main", 3, 4, false, "hash", [], [], []) };
        var one = await Adhoc.RenderAsync(template, diffs: diffs);
        Assert.False(one.Failed);
        Assert.Equal(Sorted([.. main.Dependencies, "d:" + main.Id, "k:database", MainKey]), one.ReadKeys);
    }

    [Fact]
    public async Task Helpers_record_settings_type_maps_and_partials()
    {
        var model = await BillingModel.GetAsync();
        var invoice = model.Entities.Single(e => e.Name == "Invoice");
        var files = new Dictionary<string, string>
        {
            ["types/csharp.json"] = "{ \"uuid\": \"Guid\", \"string\": \"string\" }",
            ["_part.scriban"] = "part",
        };

        var typeMap = await Adhoc.RenderAsync("{{ type_of \"uuid\" \"csharp\" }}", files: files);
        Assert.Equal("Guid", typeMap.Text());
        Assert.Equal(Sorted(["t:adhoc/types/csharp.json", MainKey]), typeMap.ReadKeys);

        var dialect = await Adhoc.RenderAsync("{{ type_of \"string\" \"postgresql\" }}");
        Assert.Equal("varchar(255)", dialect.Text());
        Assert.Equal(Sorted(["s:conventions", "s:typeMaps", MainKey]), dialect.ReadKeys);

        var plural = await Adhoc.RenderAsync("{{ pluralize \"person\" }} {{ pluralize entity }}", invoice);
        Assert.Equal("people Invoices", plural.Text());
        Assert.Equal(Sorted(invoice.Dependencies.Append("s:inflection").Append(MainKey)), plural.ReadKeys);

        var include = await Adhoc.RenderAsync("{{ include \"_part.scriban\" }}", files: files);
        Assert.Equal("part", include.Text());
        Assert.Equal(Sorted(["t:adhoc/_part.scriban", MainKey]), include.ReadKeys);
    }

    [Fact]
    public async Task Script_helpers_record_what_they_read_through_the_sandbox()
    {
        var model = await BillingModel.GetAsync();
        var files = new Dictionary<string, string>
        {
            ["helpers.js"] = "maquettiste.helper('names', (list) => list.map((e) => e.name).join(','));",
        };
        var unit = await Adhoc.RenderAsync("{{ names model.entities }}", files: files);

        Assert.Equal(string.Join(',', model.Entities.Select(e => e.Name)), unit.Text());
        var expected = model.Entities.MembershipKeys.Concat(model.Entities.SelectMany(e => e.Dependencies)).Append(MainKey);
        Assert.Equal(Sorted(expected), unit.ReadKeys);
    }

    [Fact]
    public async Task Output_expressions_are_tracked_like_the_body()
    {
        var model = await BillingModel.GetAsync();
        var invoice = model.Entities.Single(e => e.Name == "Invoice");
        var unit = await Adhoc.RenderAsync("body", invoice, u => u with { Output = "{{ snake entity.name }}.txt" });
        Assert.Equal("invoice.txt", unit.Files.Single().Path);
        Assert.Equal(Sorted(invoice.Dependencies.Append(MainKey)), unit.ReadKeys);
    }
}
