using Maquettiste.Testing;

namespace Maquettiste.Packs.Tests;

/// <summary>
/// The process-docs pages with processes linked to operations (processes-operations-design.md sections 13 and 15): the invoice example shows
/// all four columns (Fired by on Transitions and Events, Calls on Actions, Operation on Invokes), pinned under
/// <c>tests/fixtures/golden/process-docs/operations-linked</c>; operations that fire nothing change no page, and adding one renders no
/// process page, while an operation that starts firing an event renders its page again.
/// </summary>
public sealed partial class ProcessDocsTests
{
    private const string BillingPackage = "01J92P0V01KDRN8GX5PGYCNKSX";
    private const string BillingInvoice = "01J92P0V0FJ23CGSNKM7P1W5V7";
    private const string SalesPackage = "01JQPKG0000000000000000001";
    private const string SalesOrderEntity = "01JQENT0000000000000000001";
    private const string SubmitEvent = "01JQPRX0000000000000000001";

    private static string OperationFile(string id, string name, string package, string subject, string input = "", string extra = "") => $$"""
        {"$schema":"{{(subject.Length > 0 ? "../../../" : "../../")}}.schema/v1/operation.json","kind":"operation","id":"{{id}}","name":"{{name}}","package":"{{package}}"{{(subject.Length > 0 ? ",\"subject\":\"" + subject + "\"" : "")}},
         "effect":"change","input":[{{input}}],"outcomes":[{"id":"{{id[..^3]}}9{{id[^2..]}}","name":"done"}]{{extra}}}

        """;

    [Fact]
    public async Task The_invoice_example_shows_who_fires_each_event_what_each_action_calls_and_what_each_step_runs()
    {
        using var repo = new DocsRepo("billing");
        repo.Write(".maquettiste/model/processes/invoice-lifecycle.json", $$"""
            {"$schema":"../../.schema/v1/process.json","kind":"process","id":"01JR6PD0000000000000000001","name":"InvoiceLifecycle","package":"{{BillingPackage}}",
             "use":"lifecycle","subjects":[{"id":"01JR6PD0000000000000000002","entity":"{{BillingInvoice}}"}],
             "context":[{"id":"01JR6PD0000000000000000003","name":"termsDays","type":"int32","default":30}],
             "events":[
               {"id":"01JR6PD0000000000000000010","name":"Issue","payload":[{"id":"01JR6PD0000000000000000011","name":"dueDays","type":"int32","required":true},
                 {"id":"01JR6PD0000000000000000012","name":"issuedOn","type":"date","required":true}]},
               {"id":"01JR6PD0000000000000000013","name":"Pay"}],
             "actions":[{"id":"01JR6PD0000000000000000020","name":"RequestReminder","call":{"operation":"01JR6PQ0000000000000000020",
               "inputs":[{"id":"01JR6PD0000000000000000021","member":"01JR6PQ0000000000000000021","value":{"path":["01JR6PD0000000000000000003"]} }]} }],
             "states":[{"id":"01JR6PD0000000000000000030","name":"Draft"},
               {"id":"01JR6PD0000000000000000031","name":"Issued","invoke":[{"id":"01JR6PD0000000000000000034","name":"ChargeCard","type":"service","operation":"01JR6PQ0000000000000000030"}]},
               {"id":"01JR6PD0000000000000000032","name":"Paid","type":"final"}],
             "initial":"01JR6PD0000000000000000030",
             "transitions":[
               {"id":"01JR6PD0000000000000000040","source":"01JR6PD0000000000000000030","event":"01JR6PD0000000000000000010","targets":["01JR6PD0000000000000000031"],"actions":["01JR6PD0000000000000000020"]},
               {"id":"01JR6PD0000000000000000041","source":"01JR6PD0000000000000000031","event":"01JR6PD0000000000000000013","targets":["01JR6PD0000000000000000032"]},
               {"id":"01JR6PD0000000000000000042","source":"01JR6PD0000000000000000031","trigger":"invoke-done","invoke":"01JR6PD0000000000000000034","targets":["01JR6PD0000000000000000032"]}]}

            """);
        repo.Write(".maquettiste/model/operations/invoice/issue.json", OperationFile("01JR6PQ0000000000000000010", "issue", BillingPackage, BillingInvoice,
            """{"id":"01JR6PQ0000000000000000011","name":"dueDays","type":"int32","required":true},{"id":"01JR6PQ0000000000000000012","name":"issuedOn","type":"date","required":true}""",
            ""","fires":["01JR6PD0000000000000000010"]"""));
        repo.Write(".maquettiste/model/operations/invoice/remind.json", OperationFile("01JR6PQ0000000000000000020", "remind", BillingPackage, BillingInvoice,
            """{"id":"01JR6PQ0000000000000000021","name":"days","type":"int32","required":true}"""));
        repo.Write(".maquettiste/model/operations/invoice/charge-card.json", OperationFile("01JR6PQ0000000000000000030", "chargeCard", BillingPackage, BillingInvoice));
        await repo.GenerateCleanlyAsync();

        var page = repo.Read("docs/processes/invoice-lifecycle.md");
        Assert.Contains("| # | Source | Trigger | Fired by | Guard |", page, StringComparison.Ordinal);
        Assert.Contains("| Event | Raised by | Fired by | Payload |", page, StringComparison.Ordinal);
        Assert.Contains("`remind (days = termsDays)`", page, StringComparison.Ordinal);
        Assert.Contains("| Operation |", page, StringComparison.Ordinal);
        Assert.Contains("`chargeCard`", page, StringComparison.Ordinal);
        Golden.AssertMatches(Fixtures.Path("golden", "process-docs", "operations-linked"), repo.PathOf("docs"));
    }

    [Fact]
    public async Task Operations_that_fire_nothing_change_no_page_and_one_that_starts_firing_renders_its_page_again()
    {
        using var repo = new DocsRepo("processes");
        repo.Write(".maquettiste/model/operations/sales-order/archive.json", OperationFile("01JR6PQ0000000000000000040", "archive", SalesPackage, SalesOrderEntity));
        await repo.GenerateCleanlyAsync();
        Golden.AssertMatches(Fixtures.Path("golden", "process-docs", "processes"), repo.PathOf("docs"));

        // An unrelated operation (no subject, so no entity's referrers change either) renders no page: an empty fired_by keys on its event's
        // referrers only, not on every operation.
        repo.Write(".maquettiste/model/operations/touch.json", OperationFile("01JR6PQ0000000000000000050", "touch", SalesPackage, ""));
        var unrelated = await repo.GenerateCleanlyAsync();
        Assert.Equal(0, unrelated.UnitsRendered);

        // The same operation starting to fire submit renders the lifecycle's page, which gains its Fired by columns.
        repo.Write(".maquettiste/model/operations/touch.json", OperationFile("01JR6PQ0000000000000000050", "touch", SalesPackage, "",
            extra: $$""","fires":["{{SubmitEvent}}"]"""));
        var firing = await repo.GenerateCleanlyAsync();
        Assert.Contains(firing.Changes, c => c.Path.EndsWith("sales-order-lifecycle.md", StringComparison.Ordinal) && Writes.Contains(c.Kind));
        Assert.Contains("| Fired by |", repo.Read("docs/processes/sales-order-lifecycle.md"), StringComparison.Ordinal);
    }
}
