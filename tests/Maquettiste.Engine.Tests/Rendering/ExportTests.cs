using Maquettiste.Engine.Generation;
using Maquettiste.Engine.Pipeline;
using Maquettiste.Engine.Tests.Generation;

namespace Maquettiste.Engine.Tests.Rendering;

/// <summary>A records pack that exports names per entity and a services pack that reads them (next-phase-2-design.md section 5.3).</summary>
internal static class ExportPacks
{
    public const string RecordUnit = """
        { "id": "record", "template": "record.scriban", "for": "each entity", "output": "out/records/{{ exports.class_name }}.cs",
          "exports": { "class_name": "{{ pascal entity.name }}Record", "members": { "each": "attributes", "value": "{{ pascal item.name }}" } } }
        """;

    public const string ServiceTemplate = """
        {{ r = entity | exported "records" }}class {{ entity.name }}Service uses {{ r.class_name }}:{{ for a in entity.attributes }} {{ r.members[a.id] }}{{ end }}
        """;

    /// <summary>The records and services packs over the shop model (Customer and Product), with the real renderer.</summary>
    public static async Task<GenerationFixture> CreateAsync(string recordUnit = RecordUnit, string serviceTemplate = ServiceTemplate, string extraServiceUnits = "")
    {
        var f = await GenerationFixture.CreateAsync(b => Models.Shop(b), s => s with { RendererFactory = EngineServices.Create(s.Options).RendererFactory });
        LibraryPacks.Pack(f, "records", recordUnit);
        f.WritePackFile("records", "record.scriban", "class {{ exports.class_name }}");
        LibraryPacks.Pack(f, "services",
            """{ "id": "service", "template": "service.scriban", "for": "each entity", "output": "out/services/{{ entity.name }}Service.cs" }""" + extraServiceUnits,
            """{ "pack": "records", "version": "^1.0.0" }""");
        f.WritePackFile("services", "service.scriban", serviceTemplate);
        return f;
    }

    /// <summary>Adds <c>generation[pack].skip</c> to an entity's file and reloads the store.</summary>
    public static async Task SkipAsync(GenerationFixture f, string entity, string pack)
    {
        var path = f.Repo.ListFiles().First(p => p.EndsWith(".json", StringComparison.Ordinal) && !p.Contains("templates/", StringComparison.Ordinal)
            && f.Repo.ReadFile(p).Contains($"\"name\": \"{entity}\"", StringComparison.Ordinal) && f.Repo.ReadFile(p).Contains("\"entity\"", StringComparison.Ordinal));
        var text = f.Repo.ReadFile(path);
        var brace = text.IndexOf('{', StringComparison.Ordinal);
        f.Repo.WriteFile(path, text[..(brace + 1)] + $" \"generation\": {{ \"{pack}\": {{ \"skip\": true }} }}," + text[(brace + 1)..]);
        await f.Store.LoadAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>The shop model with an <c>audited</c> stereotype on Customer.</summary>
    public static void AuditedCustomer(Maquettiste.Testing.ModelBuilder b)
    {
        b.Stereotype("audited");
        Models.Shop(b, extra: (_, customer, _) => customer.Stereotype("audited"));
    }

    public static string Problems(GenerationResult result) =>
        string.Join("\n", result.Diagnostics.Select(d => $"{d.Rule} {d.FilePath} {d.JsonPointer} {d.Message}"));
}

public sealed class ExportTests
{
    [Fact]
    public async Task A_unit_reads_its_own_exports_in_its_template_and_output()
    {
        await using var f = await ExportPacks.CreateAsync();
        var result = await f.RunAsync();
        Assert.True(result.Outcome == RunOutcome.Succeeded, ExportPacks.Problems(result));
        Assert.Equal("class CustomerRecord", f.Outputs()["out/records/CustomerRecord.cs"]);
    }

    [Fact]
    public async Task Exported_returns_a_used_packs_record_for_an_element()
    {
        await using var f = await ExportPacks.CreateAsync();
        var result = await f.RunAsync();
        Assert.True(result.Outcome == RunOutcome.Succeeded, ExportPacks.Problems(result));
        Assert.Equal("class CustomerService uses CustomerRecord: Id Name", f.Outputs()["out/services/CustomerService.cs"].TrimEnd());
        Assert.Equal("class ProductService uses ProductRecord: Id Title", f.Outputs()["out/services/ProductService.cs"].TrimEnd());
    }

    [Fact]
    public async Task Exported_model_returns_the_model_units_record()
    {
        await using var f = await ExportPacks.CreateAsync(ExportPacks.RecordUnit + """
            , { "id": "context", "template": "record.scriban", "for": "model", "output": "out/records/Context.cs", "exports": { "context": "{{ project.name }}Context" } }
            """, """{{ (model | exported "records").context }}""");
        var result = await f.RunAsync();
        Assert.True(result.Outcome == RunOutcome.Succeeded, ExportPacks.Problems(result));
        Assert.EndsWith("Context", f.Outputs()["out/services/CustomerService.cs"].TrimEnd(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Exported_takes_an_element_id()
    {
        await using var f = await ExportPacks.CreateAsync(serviceTemplate: """{{ (entity.id | exported "records").class_name }}""");
        await f.RunAsync();
        Assert.Equal("CustomerRecord", f.Outputs()["out/services/CustomerService.cs"]);
    }

    [Fact]
    public async Task Each_maps_items_by_id()
    {
        await using var f = await ExportPacks.CreateAsync(serviceTemplate: """{{ for p in (entity | exported "records").members }}{{ p.key == entity.attributes[0].id }}={{ p.value }};{{ end }}""");
        var result = await f.RunAsync();
        Assert.True(result.Outcome == RunOutcome.Succeeded, ExportPacks.Problems(result));
        Assert.Contains("=Name;", f.Outputs()["out/services/CustomerService.cs"], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Values_are_trimmed_with_lf_endings()
    {
        await using var f = await ExportPacks.CreateAsync(ExportPacks.RecordUnit.Replace("\"{{ pascal entity.name }}Record\"", "\"  {{ pascal entity.name }}\\r\\nRecord \\n\"", StringComparison.Ordinal),
            """[{{ (entity | exported "records").class_name }}]""");
        await f.RunAsync();
        Assert.Equal("[Customer\nRecord]", f.Outputs()["out/services/CustomerService.cs"]);
    }

    [Fact]
    public async Task An_element_the_pack_does_not_plan_reads_null()
    {
        await using var f = await ExportPacks.CreateAsync(ExportPacks.RecordUnit.Replace("\"for\": \"each entity\"", "\"for\": \"each entity\", \"where\": { \"stereotypes\": [\"audited\"] }", StringComparison.Ordinal),
            """{{ (entity | exported "records") == null }}""");
        await f.WriteModelAsync(ExportPacks.AuditedCustomer);
        var result = await f.RunAsync();
        Assert.True(result.Outcome == RunOutcome.Succeeded, ExportPacks.Problems(result));
        Assert.Equal("false", f.Outputs()["out/services/CustomerService.cs"]);
        Assert.Equal("true", f.Outputs()["out/services/ProductService.cs"]);
    }

    [Fact]
    public async Task A_skip_hint_leaves_no_record()
    {
        await using var f = await ExportPacks.CreateAsync(serviceTemplate: """{{ (entity | exported "records") == null }}""");
        await ExportPacks.SkipAsync(f, "Customer", "records");
        var result = await f.RunAsync();
        Assert.True(result.Outcome == RunOutcome.Succeeded, ExportPacks.Problems(result));
        Assert.Equal("true", f.Outputs()["out/services/CustomerService.cs"]);
        Assert.Equal("false", f.Outputs()["out/services/ProductService.cs"]);
    }

    [Fact]
    public async Task Reading_a_pack_outside_the_closure_is_MQ6047()
    {
        await using var f = await ExportPacks.CreateAsync();
        f.WritePackFile("records", "record.scriban", """{{ (entity | exported "services") }}""");
        var result = await f.RunAsync();
        Assert.Contains(result.Diagnostics, d => d.Rule == "MQ6047" && d.FilePath == ".maquettiste/templates/records/record.scriban");
    }

    [Fact]
    public async Task An_export_template_reading_its_own_pack_is_MQ6047()
    {
        await using var f = await ExportPacks.CreateAsync(ExportPacks.RecordUnit.Replace("\"{{ pascal entity.name }}Record\"", "\"{{ (entity | exported 'records').members }}\"", StringComparison.Ordinal));
        var result = await f.RunAsync();
        Assert.Contains(result.Diagnostics, d => d.Rule == "MQ6047" && d.JsonPointer == "/units/0/exports/class_name");
    }

    [Fact]
    public async Task Two_units_giving_one_name_different_values_is_MQ6048()
    {
        await using var f = await ExportPacks.CreateAsync(ExportPacks.RecordUnit + """
            , { "id": "row", "template": "record.scriban", "for": "each entity", "output": "out/records/{{ entity.name }}Row.cs", "exports": { "class_name": "{{ entity.name }}Row" } }
            """);
        var result = await f.RunAsync();
        Assert.Contains(result.Diagnostics, d => d.Rule == "MQ6048" && d.JsonPointer == "/units/1/exports/class_name");
        Assert.Contains(result.Diagnostics, d => d.Rule == "MQ6050" && d.FilePath == ".maquettiste/templates/services/service.scriban");
        Assert.False(f.Outputs().ContainsKey("out/services/CustomerService.cs"));
    }

    [Fact]
    public async Task Equal_values_from_two_units_merge()
    {
        await using var f = await ExportPacks.CreateAsync(ExportPacks.RecordUnit + """
            , { "id": "row", "template": "record.scriban", "for": "each entity", "output": "out/records/{{ entity.name }}Row.cs", "exports": { "class_name": "{{ pascal entity.name }}Record", "row": "{{ entity.name }}Row" } }
            """, """{{ r = entity | exported "records" }}{{ r.class_name }} {{ r.row }}""");
        var result = await f.RunAsync();
        Assert.True(result.Outcome == RunOutcome.Succeeded, ExportPacks.Problems(result));
        Assert.Equal("CustomerRecord CustomerRow", f.Outputs()["out/services/CustomerService.cs"]);
    }

    [Fact]
    public async Task Each_over_items_without_ids_is_MQ6049()
    {
        await using var f = await ExportPacks.CreateAsync(ExportPacks.RecordUnit.Replace("\"each\": \"attributes\"", "\"each\": \"name\"", StringComparison.Ordinal));
        var result = await f.RunAsync();
        Assert.Contains(result.Diagnostics, d => d.Rule == "MQ6049" && d.JsonPointer == "/units/0/exports/members/each");
    }

    [Fact]
    public async Task Two_items_with_one_id_is_MQ6049()
    {
        // The model gives no list member whose items repeat an id, so this checks the rule over a list of plain values: each item
        // must be an element or a map with a string id, and stereotype keys are neither.
        await using var f = await ExportPacks.CreateAsync(ExportPacks.RecordUnit.Replace("\"each\": \"attributes\"", "\"each\": \"stereotypes\"", StringComparison.Ordinal));
        await f.WriteModelAsync(ExportPacks.AuditedCustomer);
        var result = await f.RunAsync();
        Assert.Contains(result.Diagnostics, d => d.Rule == "MQ6049");
    }

    [Fact]
    public async Task A_reader_of_a_failed_record_is_MQ6050()
    {
        await using var f = await ExportPacks.CreateAsync(ExportPacks.RecordUnit.Replace("\"{{ pascal entity.name }}Record\"", "\"{{ entity.name | nope }}\"", StringComparison.Ordinal));
        var result = await f.RunAsync();
        var read = Assert.Single(result.Diagnostics, d => d.Rule == "MQ6050" && d.ElementId is not null && d.Message.Contains("Customer", StringComparison.Ordinal));
        Assert.Contains("records/record:", read.Message, StringComparison.Ordinal);
        Assert.Contains(result.Diagnostics, d => d.Rule == "MQ6006" && d.JsonPointer == "/units/0/exports/class_name");
    }

    [Fact]
    public async Task Export_templates_do_not_see_data_schema_diff_or_exports()
    {
        foreach (var variable in new[] { "data", "schema_diff", "exports" })
        {
            await using var f = await ExportPacks.CreateAsync(ExportPacks.RecordUnit.Replace("\"{{ pascal entity.name }}Record\"",
                "\"{{ unit.id }}{{ " + variable + " }}\"", StringComparison.Ordinal), """{{ (entity | exported "records").class_name }}""");
            var result = await f.RunAsync();
            Assert.Contains(result.Diagnostics, d => d.Rule == "MQ6006" && d.JsonPointer == "/units/0/exports/class_name"
                && d.Message.Contains("`" + variable + "`", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task A_helper_named_exported_is_MQ6013()
    {
        await using var f = await ExportPacks.CreateAsync();
        f.WritePackFile("services", "helpers.js", "maquettiste.helper('exported', x => x);");
        var result = await f.RunAsync();
        Assert.Contains(result.Diagnostics, d => d.Rule == "MQ6013");
    }
}
