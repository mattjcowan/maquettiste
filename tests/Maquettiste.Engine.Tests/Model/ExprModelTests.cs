using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Maquettiste.Engine.Json;
using Maquettiste.Engine.Model;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Model;

/// <summary>The expression tree as a file holds it (next-phase-3-design.md section 3.1, task H1).</summary>
public sealed class ExprModelTests
{
    private const string Invoice = "01JEXP00000000000000000001";
    private const string IssuedOn = "01JEXP00000000000000000002";
    private const string DueOn = "01JEXP00000000000000000003";
    private const string Status = "01JEXP00000000000000000004";
    private const string Paid = "01JEXP00000000000000000005";
    private const string StatusEnum = "01JEXP00000000000000000006";
    private const string Positive = "01JEXP00000000000000000007";
    private const string Amount = "01JEXP00000000000000000008";
    private const string DueAfterIssue = "01JEXP00000000000000000009";
    private const string Binding = "01JEXP0000000000000000000A";
    private const string Main = "01JEXP0000000000000000000B";
    private const string Pay = "01JEXP0000000000000000000C";
    private const string PayAmount = "01JEXP0000000000000000000D";
    private const string Outcome = "01JEXP0000000000000000000E";
    private const string TooMuch = "01JEXP0000000000000000000F";
    private const string PayGuard = "01JEXP0000000000000000000G";
    private const string Total = "01JEXP0000000000000000000H";

    private static readonly ICanonicalJson Canonical = TestServices.Json;
    private static readonly ISchemaRegistry Schemas = TestServices.Schemas;

    /// <summary>Every node shape, each once: the condition of a constraint holds them all.</summary>
    private static readonly string[] Shapes =
    [
        """{ "value": 12 }""",
        """{ "value": "text" }""",
        """{ "value": true }""",
        """{ "value": "12.50", "type": "decimal" }""",
        """{ "value": "2026-01-31", "type": "date" }""",
        """{ "null": true }""",
        $$"""{ "path": ["{{Amount}}"] }""",
        $$"""{ "ref": "{{Paid}}" }""",
        """{ "member": "properties.retentionDays" }""",
        """{ "now": true }""",
        $$"""{ "op": "ge", "args": [{ "path": ["{{Amount}}"] }, { "value": 0 }] }""",
        """{ "call": "length", "args": [{ "value": "abc" }] }""",
        """{ "case": [{ "when": { "value": true }, "then": { "value": 1 } }], "else": { "value": 2 } }""",
        """{ "case": [{ "when": { "value": false }, "then": { "value": 1 } }] }""",
        $$"""{ "satisfies": "{{Positive}}", "with": [{ "parameter": "{{Amount}}", "value": { "value": 3 } }] }""",
        """{ "text": "amount >= 0" }""",
    ];

    [Fact]
    public void Every_node_shape_round_trips()
    {
        foreach (var shape in Shapes)
        {
            var json = Constraint(shape);
            using (var doc = JsonDocument.Parse(json))
                Assert.Empty(Schemas.Evaluate("constraint.json", doc.RootElement, "model/constraints/c.json"));

            var written = Encoding.UTF8.GetString(Canonical.Write(JsonNode.Parse(json)!, "constraint.json", ".maquettiste/model/constraints/c.json"));
            var element = Assert.IsType<Constraint>(ElementReader.ReadElement(Encoding.UTF8.GetBytes(written)));
            var again = Encoding.UTF8.GetString(Canonical.Serialize(element, "constraint.json", ".maquettiste/model/constraints/c.json"));
            Assert.Equal(written, again);
            Assert.True(JsonNode.DeepEquals(JsonNode.Parse(shape), JsonNode.Parse(written)!["condition"]), shape);
        }

        // Exactly one shape per node, and the partners each shape needs.
        string[] refused =
        [
            """{ "value": 1, "null": true }""",
            """{ "op": "eq" }""",
            """{ "call": "trim" }""",
            """{ "type": "decimal", "null": true }""",
            """{ "op": "plus", "args": [] }""",
            """{ "path": [] }""",
            """{ "null": false }""",
            """{ "value": 1.5 }""",
            $$"""{ "satisfies": "{{Positive}}" }""",
            """{ "case": [] }""",
            """{ "else": { "value": 1 }, "value": 2 }""",
            """{ "call": "Trim", "args": [] }""",
            """{ }""",
        ];
        foreach (var shape in refused)
        {
            using var doc = JsonDocument.Parse(Constraint(shape));
            Assert.True(Schemas.Evaluate("constraint.json", doc.RootElement, "model/constraints/c.json").Count > 0, shape);
        }
    }

    [Fact]
    public void Expr_ids_are_index_references()
    {
        var b = new ModelBuilder(31);
        b.Add(Read($$"""
            { "kind": "enum", "id": "{{StatusEnum}}", "name": "InvoiceStatus", "members": [ { "id": "{{Paid}}", "name": "Paid", "value": 1 } ] }
            """));
        b.Add(Read(Constraint($$"""{ "op": "ge", "args": [{ "path": ["{{Amount}}"] }, { "value": 0 }] }""")));
        b.Add(Read($$"""
            {
              "kind": "entity", "id": "{{Invoice}}", "name": "Invoice",
              "checks": [
                {
                  "id": "{{DueAfterIssue}}", "name": "dueAfterIssue", "attribute": "{{IssuedOn}}",
                  "condition": { "op": "and", "args": [
                    { "op": "le", "args": [{ "path": ["{{IssuedOn}}"] }, { "path": ["{{DueOn}}"] }] },
                    { "op": "ne", "args": [{ "path": ["{{Status}}"] }, { "ref": "{{Paid}}" }] },
                    { "satisfies": "{{Positive}}", "with": [{ "parameter": "{{Amount}}", "value": { "path": ["{{Total}}"] } }] } ] }
                }
              ],
              "attributes": [
                { "id": "{{IssuedOn}}", "name": "issuedOn", "type": "date" },
                { "id": "{{DueOn}}", "name": "dueOn", "type": "date" },
                { "id": "{{Status}}", "name": "status", "type": { "ref": "{{StatusEnum}}" } },
                { "id": "{{Total}}", "name": "total", "type": "decimal" }
              ],
              "bindings": [ { "id": "{{Binding}}", "database": "{{Main}}", "source": "invoices", "checks": ["{{DueAfterIssue}}"] } ]
            }
            """));
        b.Add(Read($$"""
            {
              "kind": "operation", "id": "{{Pay}}", "name": "pay", "subject": "{{Invoice}}", "effect": "change",
              "input": [ { "id": "{{PayAmount}}", "name": "amount", "type": "decimal" } ],
              "outcomes": [ { "id": "{{Outcome}}", "name": "applied" } ],
              "errors": [ { "id": "{{TooMuch}}", "name": "too_much", "input": "{{PayAmount}}" } ],
              "guards": [
                { "id": "{{PayGuard}}", "name": "withinTotal", "error": "{{TooMuch}}",
                  "condition": { "op": "le", "args": [{ "path": ["{{PayAmount}}"] }, { "path": ["{{Total}}"] }] } }
              ]
            }
            """));
        var model = b.Build();

        // Each id a node holds is a reference from the sub-element that holds the node, at the node's pointer.
        Assert.Contains(new ReferenceInfo(Invoice, DueAfterIssue, "/checks/0/condition/args/0/args/0/path/0", "path", IssuedOn), model.ReferencesTo(IssuedOn));
        Assert.Contains(new ReferenceInfo(Invoice, DueAfterIssue, "/checks/0/attribute", "attribute", IssuedOn), model.ReferencesTo(IssuedOn));
        Assert.Contains(new ReferenceInfo(Invoice, DueAfterIssue, "/checks/0/condition/args/0/args/1/path/0", "path", DueOn), model.ReferencesTo(DueOn));
        Assert.Contains(new ReferenceInfo(Invoice, DueAfterIssue, "/checks/0/condition/args/1/args/1/ref", "ref", Paid), model.ReferencesTo(Paid));
        Assert.Contains(new ReferenceInfo(Invoice, DueAfterIssue, "/checks/0/condition/args/2/satisfies", "satisfies", Positive), model.ReferencesTo(Positive));
        Assert.Contains(new ReferenceInfo(Invoice, DueAfterIssue, "/checks/0/condition/args/2/with/0/parameter", "parameter", Amount), model.ReferencesTo(Amount));
        Assert.Contains(new ReferenceInfo(Invoice, DueAfterIssue, "/checks/0/condition/args/2/with/0/value/path/0", "path", Total), model.ReferencesTo(Total));
        Assert.Contains(new ReferenceInfo(Invoice, Binding, "/bindings/0/checks/0", "checks", DueAfterIssue), model.ReferencesTo(DueAfterIssue));
        Assert.Contains(new ReferenceInfo(Positive, Positive, "/condition/args/0/path/0", "path", Amount), model.ReferencesTo(Amount));
        Assert.Contains(new ReferenceInfo(Pay, PayGuard, "/guards/0/error", "error", TooMuch), model.ReferencesTo(TooMuch));
        Assert.Contains(new ReferenceInfo(Pay, PayGuard, "/guards/0/condition/args/1/path/0", "path", Total), model.ReferencesTo(Total));

        // Entity checks, operation guards and constraint parameters are sub-elements with names.
        Assert.True(model.TryGetEntry(DueAfterIssue, out var check));
        Assert.Equal(("key", Invoice, "/checks/0"), (check.Kind, check.OwnerId, check.JsonPointer));
        Assert.True(model.TryGetEntry(PayGuard, out var guard));
        Assert.Equal(("guard", Pay, "/guards/0"), (guard.Kind, guard.OwnerId, guard.JsonPointer));
        Assert.True(model.TryGetEntry(Amount, out var parameter));
        Assert.Equal(("parameter", Positive, "/parameters/0"), (parameter.Kind, parameter.OwnerId, parameter.JsonPointer));
        Assert.Equal("Invoice.dueAfterIssue", model.Names("key").Get(DueAfterIssue)!.Longest);
        Assert.Equal("Invoice.pay.withinTotal", model.Names("guard").Get(PayGuard)!.Longest);
        Assert.Equal("PositiveAmount.amount", model.Names("parameter").Get(Amount)!.Longest);
        Assert.Equal("PositiveAmount", model.Names("constraint").Get(Positive)!.Longest);
    }

    [Fact]
    public void A_constraint_is_an_element_kind_in_its_own_folder()
    {
        var info = KindInfo.Get(ElementKind.Constraint);
        Assert.Equal(("constraint", typeof(Constraint), "constraint.json", "model/constraints"), (info.Name, info.ClrType, info.SchemaFile, info.Folder));
        Assert.Equal(KindInfo.ConstraintsFolder, info.Folder);
    }

    private static string Constraint(string condition) => $$"""
        {
          "kind": "constraint", "id": "{{Positive}}", "name": "PositiveAmount",
          "parameters": [ { "id": "{{Amount}}", "name": "amount", "type": "decimal" } ],
          "condition": {{condition}},
          "message": "The amount is not positive."
        }
        """;

    private static Element Read(string json) => ElementReader.ReadElement(Encoding.UTF8.GetBytes(json));
}
