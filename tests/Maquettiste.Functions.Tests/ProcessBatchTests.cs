using System.Text.Json.Nodes;
using Maquettiste.Functions.Tests.Support;

namespace Maquettiste.Functions.Tests;

/// <summary>The process batch operations over <c>POST /api/model/batch</c>, on the gate 3 fixture (phase-3-design.md section 4.4).</summary>
public sealed class ProcessBatchTests
{
    private const string Sales = "01JQPRC0000000000000000001";
    private const string Purchase = "01JQPRC0000000000000000002";
    private const string SalesOrder = "01JQENT0000000000000000001";
    private const string Drafting = "01JQSTA0000000000000000101";
    private const string Fulfilment = "01JQSTA0000000000000000004";
    private const string Resume = "01JQSTA0000000000000000014";
    private const string Paid = "01JQSTA0000000000000000009";

    [Fact]
    public async Task Set_initial_set_lifecycle_and_sync_enum_apply_or_refuse_with_MQ9019()
    {
        await using var host = EditorHost.CreateModel("processes");
        var purchaseFile = host.PathOf(".maquettiste/model/processes/purchase-approval.json");
        var salesFile = host.PathOf(".maquettiste/model/processes/sales-order-lifecycle.json");

        var inSync = await host.SendJsonAsync("POST", "/api/model/batch", $$"""{ "operations": [ { "op": "sync-enum", "id": "{{Sales}}" } ] }""");
        Assert.Equal(200, inSync.Status);
        Contract.AssertResponse(inSync, "/api/model/batch");
        Assert.Empty(inSync.Json["items"]!.AsArray());

        var initial = await host.SendJsonAsync("POST", "/api/model/batch", $$"""{ "operations": [ { "op": "set-initial", "id": "{{Purchase}}", "target": "{{Drafting}}" }, { "op": "set-initial", "id": "{{Fulfilment}}", "target": "{{Resume}}" } ] }""");
        Assert.Equal(200, initial.Status);
        Contract.AssertResponse(initial, "/api/model/batch");
        Assert.Contains($"\"initial\": \"{Drafting}\"", File.ReadAllText(purchaseFile), StringComparison.Ordinal);
        Assert.Contains($"\"initial\": \"{Resume}\"", File.ReadAllText(salesFile), StringComparison.Ordinal);

        var notChild = await host.SendJsonAsync("POST", "/api/model/batch", $$"""{ "operations": [ { "op": "set-initial", "id": "{{Sales}}", "target": "{{Paid}}" } ] }""");
        Assert.Equal(422, notChild.Status);
        Contract.AssertResponse(notChild, "/api/model/batch");
        Assert.Contains("MQ9019", notChild.Json.ToJsonString(), StringComparison.Ordinal);

        var noTarget = await host.SendJsonAsync("POST", "/api/model/batch", $$"""{ "operations": [ { "op": "set-initial", "id": "{{Sales}}" } ] }""");
        Assert.Equal(422, noTarget.Status);
        Assert.Null(noTarget.Json["batch"]);

        var cleared = await host.SendJsonAsync("POST", "/api/model/batch", $$"""{ "operations": [ { "op": "set-lifecycle", "id": "{{SalesOrder}}" } ] }""");
        Assert.Equal(200, cleared.Status);
        Contract.AssertResponse(cleared, "/api/model/batch");
        Assert.Equal(2, cleared.Json["changes"]!["changed"]!.AsArray().Count);
        Assert.DoesNotContain("\"boundAttribute\"", File.ReadAllText(salesFile), StringComparison.Ordinal);

        var notBound = await host.SendJsonAsync("POST", "/api/model/batch", $$"""{ "operations": [ { "op": "sync-enum", "id": "{{Sales}}" } ] }""");
        Assert.Equal(422, notBound.Status);
        Contract.AssertResponse(notBound, "/api/model/batch");
        var refusal = notBound.Json["items"]![0]!["diagnostics"]![0]!;
        Assert.Equal(("MQ9019", "/operations/0"), (refusal["rule"]!.GetValue<string>(), refusal["jsonPointer"]!.GetValue<string>()));

        var bound = await host.SendJsonAsync("POST", "/api/model/batch", $$"""{ "operations": [ { "op": "set-lifecycle", "id": "{{SalesOrder}}", "target": "{{Sales}}" } ] }""");
        Assert.Equal(200, bound.Status);
        Assert.Contains("\"lifecycle\"", File.ReadAllText(salesFile), StringComparison.Ordinal);
        Assert.IsType<JsonObject>(bound.Json);
    }
}
