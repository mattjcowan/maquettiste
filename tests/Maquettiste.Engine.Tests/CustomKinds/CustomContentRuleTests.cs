using System.Text.Json;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Tests.Model;
using Maquettiste.Testing;
using static Maquettiste.Engine.Tests.CustomKinds.CustomFixture;

namespace Maquettiste.Engine.Tests.CustomKinds;

/// <summary>A custom element's content against its custom kind, and its names (next-phase-2-design.md section 4.2, task E23).</summary>
public sealed class CustomContentRuleTests
{
    private static readonly CustomKindDocument Setting = KindDocument("""
        { "type": "setting", "properties": { "unit": { "type": "string", "enum": ["days", "months"] }, "value": { "type": "integer", "minimum": 0 } },
          "required": ["value"] }
        """);

    private static readonly CustomKindDocument Message = KindDocument(MessageKind);

    [Fact]
    public async Task Content_failing_its_kind_is_MQ3033_with_a_pointer()
    {
        var b = new ModelBuilder(71);
        string bad = b.NewId(), missing = b.NewId(), good = b.NewId();
        b.Add(Element(bad, "due_days", "setting", """{ "unit": "weeks", "value": -1 }"""));
        b.Add(Element(missing, "grace_days", "setting", """{ "unit": "days" }"""));
        b.Add(Element(good, "reminder_days", "setting", """{ "unit": "days", "value": 7 }"""));

        var found = await FindingsAsync(Snapshot(b, [Setting]), "MQ3033");

        Assert.Equal(
            [(bad, "/content/unit"), (bad, "/content/value"), (missing, "/content")],
            found.Select(d => (d.ElementId!, d.JsonPointer!)).OrderBy(f => f.Item1 == bad ? 0 : 1).ThenBy(f => f.Item2, StringComparer.Ordinal));
        Assert.All(found, d => Assert.StartsWith("Custom kind 'setting': content", d.Message, StringComparison.Ordinal));
        Assert.Contains("required", found.Single(d => d.ElementId == missing).Message, StringComparison.Ordinal);
        Assert.EndsWith("/due-days.json", found[0].FilePath, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_undeclared_content_key_is_MQ3033()
    {
        var b = new ModelBuilder(72);
        var id = b.NewId();
        b.Add(Element(id, "due_days", "setting", """{ "value": 30, "colour": "red" }"""));

        var finding = Assert.Single(await FindingsAsync(Snapshot(b, [Setting]), "MQ3033"));

        Assert.Equal((id, "/content/colour"), (finding.ElementId!, finding.JsonPointer!));
    }

    [Fact]
    public async Task A_member_without_an_id_is_MQ3033()
    {
        var b = new ModelBuilder(73);
        string id = b.NewId(), part = b.NewId();
        b.Add(Element(id, "invoice_sent", "message", $$"""{ "parts": [ { "id": "{{part}}", "name": "subject" }, { "name": "body" } ] }"""));

        var finding = Assert.Single(await FindingsAsync(Snapshot(b, [Message]), "MQ3033"));

        Assert.Equal((id, "/content/parts/1"), (finding.ElementId!, finding.JsonPointer!));
        Assert.Contains("id", finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_name_repeated_within_a_type_and_package_is_MQ3001()
    {
        var b = new ModelBuilder(74);
        var billing = b.Package("Billing");
        var catalog = b.Package("Catalog");
        string first = b.NewId(), second = b.NewId(), otherPackage = b.NewId(), otherType = b.NewId();
        b.Add(Element(first, "due_days", "setting", """{ "value": 30 }""", billing.Id));
        b.Add(Element(second, "due_days", "setting", """{ "value": 45 }""", billing.Id) with { DisplayName = "Due days again" });
        b.Add(Element(otherPackage, "due_days", "setting", """{ "value": 60 }""", catalog.Id));
        b.Add(Element(otherType, "due_days", "flag", """{ "on": true }""", billing.Id));

        var found = await FindingsAsync(Snapshot(b, [Setting, KindDocument(FlagKind)]), "MQ3001");

        var finding = Assert.Single(found);
        Assert.Equal("/name", finding.JsonPointer);
        Assert.Contains("among custom elements of type setting in the same package", finding.Message, StringComparison.Ordinal);
        Assert.Contains(finding.ElementId, new[] { first, second });
    }

    [Fact]
    public async Task A_member_name_repeated_in_one_array_is_MQ3001()
    {
        var b = new ModelBuilder(75);
        string id = b.NewId(), a = b.NewId(), c = b.NewId(), d = b.NewId(), e = b.NewId(), f = b.NewId();
        b.Add(Element(id, "invoice_layout", "layout", $$"""
            { "sections": [
                { "id": "{{a}}", "name": "header", "lines": [ { "id": "{{e}}", "name": "number" } ] },
                { "id": "{{c}}", "name": "footer", "lines": [ { "id": "{{f}}", "name": "number" } ] },
                { "id": "{{d}}", "name": "header" } ] }
            """));

        var finding = Assert.Single(await FindingsAsync(Snapshot(b, [KindDocument(LayoutKind)]), "MQ3001"));

        Assert.Equal((d, "/content/sections/2/name"), (finding.ElementId!, finding.JsonPointer!));
        Assert.Contains("Member name 'header' is already used at index 0 of sections", finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Stereotypes_and_extensions_apply_to_custom_elements()
    {
        var b = new ModelBuilder(76);
        b.Stereotype("audited").AppliesTo("custom");
        b.Stereotype("tabular").AppliesTo("table");
        string applied = b.NewId(), wrong = b.NewId();
        b.Add(Element(applied, "due_days", "setting", """{ "value": 30 }""") with
        {
            Stereotypes = ["audited"],
            Properties = new Dictionary<string, JsonElement> { ["owner"] = JsonSerializer.SerializeToElement(5) },
        });
        b.Add(Element(wrong, "grace_days", "setting", """{ "value": 3 }""") with { Stereotypes = ["tabular"] });
        var extension = PropertySchemaSetTests.Extension("""{ "owner": { "type": "string" } }""", "ownership", ["custom"]);

        var found = await FindingsAsync(Snapshot(b, [Setting], extension), "MQ2004", "MQ5001");

        Assert.Equal([("MQ2004", wrong), ("MQ5001", applied)], found.Select(d => (d.Rule, d.ElementId!)).Order());
        Assert.Equal("/properties/owner", found.Single(d => d.Rule == "MQ5001").JsonPointer);
    }
}
