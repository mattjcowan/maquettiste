using System.Text;
using Maquettiste.Engine.Generation;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Pipeline;
using Maquettiste.Engine.Tests.Store;

namespace Maquettiste.Engine.Tests.Operations;

/// <summary>
/// The operation <c>send</c> on Invoice over the billing fixture (next-phase-2-design.md section 4.1), with an invented process whose
/// event <c>sent</c> the operation fires.
/// </summary>
internal static class OperationFixture
{
    public const string Billing = "01J92P0V01KDRN8GX5PGYCNKSX";
    public const string Catalog = "01J92P0V025DRFTXKMS240G2NG";
    public const string Invoice = "01J92P0V0FJ23CGSNKM7P1W5V7";
    public const string InvoiceId = "01J92P0V0Q9EK961M5HAQ3C5MY";
    public const string Customer = "01J92P0V0ETQKXXP951CMMNHH3";
    public const string Product = "01J92P0V0JR8BE8253SKT29ZG7";
    public const string Send = "01JR5ND000000000000000000A";
    public const string Recipient = "01JR5ND000000000000000000B";
    public const string Applied = "01JR5ND000000000000000000C";
    public const string Waiting = "01JR5ND000000000000000000D";
    public const string TrackingId = "01JR5ND000000000000000000E";
    public const string AlreadySent = "01JR5ND000000000000000000F";
    public const string MissingRecipient = "01JR5ND000000000000000000G";
    public const string Sent = "01JR5ND000000000000000000H";
    public const string Dispatch = "01JR5ND000000000000000000J";
    public const string Draft = "01JR5ND000000000000000000K";

    /// <summary>The <c>send</c> document as the acceptance test writes it, without <c>$schema</c>.</summary>
    public static string SendJson(bool fires = true) => $$"""
        {
          "kind": "operation",
          "id": "{{Send}}",
          "name": "send",
          "package": "{{Billing}}",
          "subject": "{{Invoice}}",
          "effect": "change",
          "input": [
            { "id": "{{Recipient}}", "name": "recipient", "type": "string", "length": 254, "required": true }
          ],
          "outcomes": [
            { "id": "{{Applied}}", "name": "applied", "returns": { "entity": "{{Invoice}}" } },
            {
              "id": "{{Waiting}}",
              "name": "waiting",
              "completes": "later",
              "returns": { "members": [ { "id": "{{TrackingId}}", "name": "trackingId", "type": "ulid" } ] }
            }
          ],
          "errors": [
            { "id": "{{AlreadySent}}", "name": "already_sent" },
            { "id": "{{MissingRecipient}}", "name": "missing_recipient", "input": "{{Recipient}}" }
          ]{{(fires ? $",\n  \"fires\": [\"{Sent}\"]" : "")}}
        }
        """;

    /// <summary>An orchestration with the event <c>sent</c> and one state.</summary>
    public static string DispatchJson => $$"""
        { "kind": "process", "id": "{{Dispatch}}", "name": "InvoiceDispatch", "package": "{{Billing}}",
          "events": [ { "id": "{{Sent}}", "name": "sent" } ],
          "states": [ { "id": "{{Draft}}", "name": "draft" } ] }
        """;

    /// <summary>Opens billing with the real validator and creates the process and <c>send</c>.</summary>
    public static async Task<BillingStore> OpenAsync(bool withSend = true)
    {
        var s = await BillingStore.OpenAsync(validate: true);
        await CreateAsync(s, DispatchJson);
        if (withSend)
            await CreateAsync(s, SendJson());
        return s;
    }

    /// <summary>Creates an element and asserts it saved.</summary>
    public static async Task<SaveResult> CreateAsync(BillingStore s, string json)
    {
        var result = await s.Store.CreateAsync(Encoding.UTF8.GetBytes(json), ChangeSource.Cli, TestContext.Current.CancellationToken);
        Assert.True(result.Outcome == SaveOutcome.Saved, string.Join("; ", result.Diagnostics.Select(d => $"{d.Rule} {d.JsonPointer} {d.Message}")));
        return result;
    }

    /// <summary>The validator the store runs.</summary>
    public static IModelValidator Validator(BillingStore s) => EngineServices.Create(s.Harness.Options).Validator;
}
