using System.Text.Json;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Processes;
using Maquettiste.Engine.Resolution;
using Maquettiste.Testing;
using static Maquettiste.Engine.Tests.Expressions.ExpressionRuleTests;

namespace Maquettiste.Engine.Tests.Integration;

/// <summary>
/// The acceptance case of "Lifecycles any implementation can follow" (lifecycle-design.md section 14), on an invented model: one
/// lifecycle, Fulfilment, governs orders, whose state is bound to the reference type OrderStatus, and return notes, with no binding; both
/// derive from Document, which holds <c>total</c>. Its state codes resolve as the output templates read them, its guard reads
/// <c>subject.total &gt; 0</c> in simulation, and a scenario with an order and its customer replays.
/// </summary>
public sealed class LifecycleAcceptanceTests
{
    private const string Shop = "01K6BFX0000000000000000001";
    private const string Fulfilment = "01K9MQC0000000000000000P01";
    private const string OrderStatus = "01K9MQC0000000000000000T01";
    private const string Document = "01K9MQC0000000000000000E01";
    private const string Order = "01K9MQC0000000000000000E02";
    private const string ReturnNote = "01K9MQC0000000000000000E03";
    private const string Customer = "01K9MQC0000000000000000E04";
    private const string Total = "01K9MQC0000000000000000A12";
    private const string Status = "01K9MQC0000000000000000A21";
    private const string CustomerName = "01K9MQC0000000000000000A42";
    private const string CustomerEnd = "01K9MQC0000000000000000R03";
    private const string Open = "01K9MQC0000000000000000S01";
    private const string Shipped = "01K9MQC0000000000000000S02";
    private const string Ship = "01K9MQC0000000000000000V01";
    private const string Positive = "01K9MQC0000000000000000G01";
    private const string Scenario = "01K9MQC0000000000000000C01";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static TempRepo AcceptanceRepo()
    {
        var repo = Repo();
        repo.WriteFile(".maquettiste/model/reference-types/order-status.json", $$"""
            { "kind": "reference-type", "id": "{{OrderStatus}}", "name": "OrderStatus", "package": "{{Shop}}",
              "code": { "id": "01K9MQC0000000000000000T02", "length": 8 }, "label": { "id": "01K9MQC0000000000000000T03" } }
            """);
        repo.WriteFile(".maquettiste/model/seeds/order-status/order-status.json", $$"""
            { "kind": "seed", "id": "01K9MQC0000000000000000D01", "name": "OrderStatus", "target": "{{OrderStatus}}", "columns": ["code", "label"],
              "rows": [ { "id": "01K9MQC0000000000000000D02", "values": ["OPEN", "open"] }, { "id": "01K9MQC0000000000000000D03", "values": ["SHIP", "shipped"] } ] }
            """);
        repo.WriteFile(".maquettiste/model/entities/document.json", $$"""
            { "kind": "entity", "id": "{{Document}}", "name": "Document", "package": "{{Shop}}", "key": { "attributes": ["01K9MQC0000000000000000A11"] },
              "attributes": [ { "id": "01K9MQC0000000000000000A11", "name": "id", "type": "uuid", "required": true },
                              { "id": "{{Total}}", "name": "total", "type": "decimal", "default": 0 } ] }
            """);
        repo.WriteFile(".maquettiste/model/entities/order.json", $$"""
            { "kind": "entity", "id": "{{Order}}", "name": "Order", "package": "{{Shop}}", "base": "{{Document}}",
              "attributes": [ { "id": "{{Status}}", "name": "status", "type": { "ref": "{{OrderStatus}}" }, "default": "OPEN" } ] }
            """);
        repo.WriteFile(".maquettiste/model/entities/return-note.json", $$"""
            { "kind": "entity", "id": "{{ReturnNote}}", "name": "ReturnNote", "package": "{{Shop}}", "base": "{{Document}}",
              "attributes": [ { "id": "01K9MQC0000000000000000A31", "name": "reason", "type": "string", "length": 200 } ] }
            """);
        repo.WriteFile(".maquettiste/model/entities/customer.json", $$"""
            { "kind": "entity", "id": "{{Customer}}", "name": "Customer", "package": "{{Shop}}", "key": { "attributes": ["01K9MQC0000000000000000A41"] },
              "attributes": [ { "id": "01K9MQC0000000000000000A41", "name": "id", "type": "uuid", "required": true },
                              { "id": "{{CustomerName}}", "name": "name", "type": "string", "length": 80 } ] }
            """);
        repo.WriteFile(".maquettiste/model/relations/order-customer.json", $$"""
            { "kind": "relation", "id": "01K9MQC0000000000000000R01", "name": "OrderCustomer",
              "ends": [ { "id": "01K9MQC0000000000000000R02", "entity": "{{Order}}", "role": "orders" },
                        { "id": "{{CustomerEnd}}", "entity": "{{Customer}}", "role": "customer", "navigation": "customer", "max": 1 } ] }
            """);
        repo.WriteFile(".maquettiste/model/processes/fulfilment.json", $$"""
            {
              "kind": "process", "id": "{{Fulfilment}}", "name": "Fulfilment", "package": "{{Shop}}", "use": "lifecycle",
              "subjects": [ { "id": "01K9MQC0000000000000000B01", "entity": "{{Order}}", "boundAttribute": "{{Status}}" },
                            { "id": "01K9MQC0000000000000000B02", "entity": "{{ReturnNote}}" } ],
              "events": [ { "id": "{{Ship}}", "name": "ship" } ],
              "guards": [ { "id": "{{Positive}}", "name": "positive", "condition": { "op": "gt", "args": [{ "path": ["{{Total}}"] }, { "value": 0 }] } } ],
              "states": [ { "id": "{{Open}}", "name": "Open", "code": "OPEN" }, { "id": "{{Shipped}}", "name": "Shipped", "code": "SHIP", "type": "final" } ],
              "initial": "{{Open}}",
              "transitions": [ { "id": "01K9MQC0000000000000000X01", "source": "{{Open}}", "event": "{{Ship}}", "guard": "{{Positive}}", "targets": ["{{Shipped}}"] } ]
            }
            """);
        repo.WriteFile(".maquettiste/model/scenarios/fulfilment/ships-an-order.json", $$"""
            {
              "kind": "scenario", "id": "{{Scenario}}", "name": "ShipsAnOrder", "process": "{{Fulfilment}}",
              "objects": [
                { "id": "01K9MQC0000000000000000K01", "name": "order", "entity": "{{Order}}", "values": { "{{Total}}": 12.5, "{{Status}}": "OPEN" },
                  "links": [ { "end": "{{CustomerEnd}}", "object": "01K9MQC0000000000000000K02" } ] },
                { "id": "01K9MQC0000000000000000K02", "name": "customer", "entity": "{{Customer}}", "values": { "{{CustomerName}}": "Ada" } }
              ],
              "subject": "01K9MQC0000000000000000K01",
              "outcome": "final",
              "steps": [ { "id": "01K9MQC0000000000000000C02", "event": "{{Ship}}", "expect": { "accepted": true, "states": ["{{Shipped}}"] } } ]
            }
            """);
        return repo;
    }

    private static async Task<bool> ShipsAsync(ModelStore store, string? objects)
    {
        var list = objects is null ? null : JsonDocument.Parse(objects).RootElement.EnumerateArray().Select(o => o.Clone()).ToList();
        var request = new SimulateRequest
        {
            Objects = list,
            Subject = list?[0].GetProperty("id").GetString(),
            Steps = [JsonDocument.Parse($$"""{ "input": "event", "event": "{{Ship}}" }""").RootElement.Clone()],
        };
        var call = await store.SimulateProcessAsync(Fulfilment, request, Ct);
        Assert.True(call.Status == ProcessCallStatus.Ok, call.Detail);
        return call.Value!.Trace[^1].Accepted;
    }

    private static string One(string entity, decimal total) =>
        $$"""[ { "id": "01K9MQC0000000000000000K09", "name": "it", "entity": "{{entity}}", "values": { "{{Total}}": {{total.ToString(System.Globalization.CultureInfo.InvariantCulture)}} } } ]""";

    [Fact]
    public async Task One_lifecycle_governs_orders_and_return_notes_through_codes_a_guard_on_the_object_and_a_scenario_with_objects()
    {
        using var repo = AcceptanceRepo();
        await using var store = new ModelStore(repo.Options);
        var errors = (await store.ValidateAsync(ValidationScope.All, Ct)).Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.True(errors.Count == 0, string.Join("\n", errors.Select(d => $"{d.Rule} {d.FilePath} {d.JsonPointer} {d.Message}")));

        // The codes, as the output templates read them: each state's code, forming a map, with the order's state bound to OrderStatus.
        var model = await new ModelResolver(repo.Options).ResolveAsync(await store.GetSnapshotAsync(Ct), null, Ct);
        var process = model.Processes.Single(p => p.Id == Fulfilment);
        Assert.True(process.CodesMap);
        Assert.Equal([("Open", (object?)"OPEN"), ("Shipped", "SHIP")], process.States.Select(s => (s.Name, s.Code)));
        Assert.Equal([(Order, (string?)OrderStatus), (ReturnNote, null)], process.Subjects.Select(s => (s.Entity.Id, s.BoundReferenceType?.Id)));

        // The guard reads the base's total on either subject: one target, Document's own attribute.
        var guard = process.Guards.Single(g => g.Id == Positive);
        Assert.Equal("subject.total > 0", guard.Condition!.Text);
        Assert.Same(model.Entities.Single(e => e.Id == Document).OwnAttributes.Single(a => a.Id == Total), guard.Condition.Args[0].Target);

        // In simulation: an order or a return note ships when its total is positive; with no object, total's default (0) holds it.
        Assert.True(await ShipsAsync(store, One(Order, 12.5m)));
        Assert.False(await ShipsAsync(store, One(Order, 0m)));
        Assert.True(await ShipsAsync(store, One(ReturnNote, 3m)));
        Assert.False(await ShipsAsync(store, null));

        // The scenario with an order and its customer replays.
        var verified = await store.VerifyScenariosAsync(Fulfilment, null, Ct);
        Assert.True(verified.Status == ProcessCallStatus.Ok, verified.Detail);
        var result = Assert.Single(verified.Value!.Results);
        Assert.True(result.Passed, result.Failure?.Message);
    }
}
