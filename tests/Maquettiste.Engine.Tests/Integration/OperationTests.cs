using System.Text;
using System.Text.Json.Nodes;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Pipeline;
using Maquettiste.Engine.Resolution;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Integration;

/// <summary>
/// The engine acceptance tests of the <c>operation</c> kind (next-phase-2-design.md section 4.1, task E9), each on a copy of the
/// billing fixture. The copy gains the lifecycle <c>InvoiceLifecycle</c> (subject Invoice, states <c>Draft</c> and <c>Sent</c>, event
/// <c>sent</c> from <c>Draft</c> to <c>Sent</c>), Invoice's <c>lifecycle</c> naming it, and <c>send</c> on Invoice firing <c>sent</c>.
/// The first cut keeps typed and key members, outcomes returning members or an entity (a list too), errors with a code and an
/// input, <c>fires</c>, <c>requires</c> and the <c>each operation</c> scope; shared errors are the second cut.
/// </summary>
[Collection(IntegrationCollection.Name)]
public sealed class OperationTests
{
    private const string Billing = "01J92P0V01KDRN8GX5PGYCNKSX";
    private const string Invoice = E2ERepo.InvoiceId;
    private const string Customer = E2ERepo.CustomerId;
    private const string Lifecycle = "01JR5NE0000000000000000001";
    private const string Draft = "01JR5NE0000000000000000002";
    private const string SentState = "01JR5NE0000000000000000003";
    private const string Sent = "01JR5NE0000000000000000004";
    private const string DraftToSent = "01JR5NE0000000000000000005";
    private const string Send = "01JR5NE0000000000000000010";
    private const string Recipient = "01JR5NE0000000000000000011";
    private const string Applied = "01JR5NE0000000000000000012";
    private const string Waiting = "01JR5NE0000000000000000013";
    private const string TrackingId = "01JR5NE0000000000000000014";
    private const string AlreadySent = "01JR5NE0000000000000000015";
    private const string MissingRecipient = "01JR5NE0000000000000000016";
    private const string Clerk = "01JR5NE0000000000000000020";
    private const string Search = "01JR5NE0000000000000000021";
    private const string Customers = "01JR5NE0000000000000000022";
    private const string Found = "01JR5NE0000000000000000023";
    private const string TooBroad = "01JR5NE0000000000000000024";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---- setup ----

    /// <summary>The copy with the lifecycle, Invoice's <c>lifecycle</c> and <c>send</c>.</summary>
    private static async Task<E2ERepo> SendCopyAsync()
    {
        var repo = E2ERepo.Create(demo: false);
        // The lifecycle and Invoice's lifecycle in one batch: either alone is MQ9201.
        var invoice = (await repo.Store.GetElementAsync(Invoice, Ct))!;
        var edited = JsonNode.Parse(invoice.Json.GetRawText())!.AsObject();
        edited["lifecycle"] = Lifecycle;
        var parsed = repo.Store.ParseBatch(Encoding.UTF8.GetBytes($$"""
            { "operations": [
              { "op": "create", "element": { "kind": "process", "id": "{{Lifecycle}}", "name": "InvoiceLifecycle", "package": "{{Billing}}", "use": "lifecycle",
                  "subject": "{{Invoice}}", "events": [ { "id": "{{Sent}}", "name": "sent" } ],
                  "states": [ { "id": "{{Draft}}", "name": "Draft" }, { "id": "{{SentState}}", "name": "Sent", "type": "final" } ],
                  "transitions": [ { "id": "{{DraftToSent}}", "source": "{{Draft}}", "event": "{{Sent}}", "targets": [ "{{SentState}}" ] } ] } },
              { "op": "update", "id": "{{Invoice}}", "expectedHash": "{{invoice.Hash}}", "element": {{edited.ToJsonString()}} }
            ] }
            """));
        Assert.True(parsed.Batch is not null, string.Join("; ", parsed.Diagnostics.Select(d => d.Message)));
        var applied = await repo.Store.ApplyBatchAsync(parsed.Batch, ChangeSource.Editor, Ct);
        Assert.True(applied.Outcome == SaveOutcome.Saved, Describe(applied.Items.SelectMany(i => i.Diagnostics)));
        await repo.CreateElementAsync($$"""
            {
              "kind": "operation", "id": "{{Send}}", "name": "send", "package": "{{Billing}}", "subject": "{{Invoice}}", "effect": "change",
              "input": [ { "id": "{{Recipient}}", "name": "recipient", "type": "string", "length": 254, "required": true } ],
              "outcomes": [
                { "id": "{{Applied}}", "name": "applied", "returns": { "entity": "{{Invoice}}" } },
                { "id": "{{Waiting}}", "name": "waiting", "completes": "later",
                  "returns": { "members": [ { "id": "{{TrackingId}}", "name": "trackingId", "type": "ulid" } ] } }
              ],
              "errors": [
                { "id": "{{AlreadySent}}", "name": "already_sent" },
                { "id": "{{MissingRecipient}}", "name": "missing_recipient", "input": "{{Recipient}}" }
              ],
              "fires": [ "{{Sent}}" ]
            }
            """);
        return repo;
    }

    private static async Task<ResolvedModel> ResolveAsync(E2ERepo repo) =>
        await repo.Store.Services.Resolver.ResolveAsync(await repo.Store.GetSnapshotAsync(Ct), null, Ct);

    private static async Task<IReadOnlyList<Diagnostic>> DiagnosticsAsync(E2ERepo repo) =>
        (await repo.Store.ValidateAsync(ValidationScope.All, Ct)).Diagnostics;

    private static string Describe(IEnumerable<Diagnostic> diagnostics) =>
        string.Join("\n", diagnostics.Select(d => $"{d.Rule} {d.FilePath} {d.JsonPointer} {d.Message}"));

    /// <summary>Saves an element's edited document with its current hash and returns the result, whatever it is.</summary>
    private static async Task<SaveResult> TrySaveAsync(E2ERepo repo, string id, Action<JsonObject> edit)
    {
        var document = (await repo.Store.GetElementAsync(id, Ct))!;
        var node = JsonNode.Parse(document.Json.GetRawText())!.AsObject();
        edit(node);
        return await repo.Store.SaveAsync(id, Encoding.UTF8.GetBytes(node.ToJsonString()), document.Hash, ChangeSource.Editor, Ct);
    }

    // ---- acceptance ----

    [Fact]
    public async Task Send_on_invoice_changes_data_has_two_outcomes_two_errors_and_fires_sent()
    {
        await using var repo = await SendCopyAsync();

        var diagnostics = await DiagnosticsAsync(repo);

        Assert.True(diagnostics.Count == 0, Describe(diagnostics));
        var model = await ResolveAsync(repo);
        var send = model.Operations.Single(o => o.Id == Send);
        Assert.Equal((Invoice, "record", "change"), (send.Subject?.Id, send.Level, send.Effect));
        Assert.Equal(["recipient"], send.Input.Select(m => m.Name));
        Assert.Equal(["applied", "waiting"], send.Outcomes.Select(o => o.Name));
        var applied = send.Outcomes[0];
        Assert.Same(applied, send.DefaultOutcome);
        Assert.Equal((true, "now", Invoice, false), (applied.IsDefault, applied.Completes, applied.Returns?.Entity?.Id, applied.Returns!.IsList));
        var waiting = send.Outcomes[1];
        Assert.Equal((false, "later"), (waiting.IsDefault, waiting.Completes));
        Assert.Equal(["trackingId"], waiting.Returns!.Members.Select(m => m.Name));
        Assert.Equal(["already_sent", "missing_recipient"], send.Errors.Select(e => e.Name));
        Assert.Equal(("already_sent", null), (send.Errors[0].Code, send.Errors[0].Input?.Name));
        Assert.Equal(("missing_recipient", "recipient"), (send.Errors[1].Code, send.Errors[1].Input?.Name));
        Assert.Equal([Sent], send.Fires.Select(e => e.Id));
        Assert.Equal([Send], model.Entities.Single(e => e.Id == Invoice).Operations.Select(o => o.Id));
        var sent = model.Processes.Single(p => p.Id == Lifecycle).Events.Single(e => e.Id == Sent);
        Assert.Equal([Send], sent.FiredBy.Select(o => o.Id));
    }

    [Fact]
    public async Task Removing_sent_or_recipient_is_refused_while_send_names_them()
    {
        await using var repo = await SendCopyAsync();

        var withoutSent = await TrySaveAsync(repo, Lifecycle, n =>
        {
            n["events"] = new JsonArray();
            n["transitions"]![0]!.AsObject().Remove("event");
        });
        Assert.Equal(SaveOutcome.Invalid, withoutSent.Outcome);
        Assert.Contains(withoutSent.Diagnostics, d => d is { Rule: "MQ2001", ElementId: Send, JsonPointer: "/fires/0" });

        var withoutRecipient = await TrySaveAsync(repo, Send, n => n["input"] = new JsonArray());
        Assert.Equal(SaveOutcome.Invalid, withoutRecipient.Outcome);
        Assert.Contains(withoutRecipient.Diagnostics, d => d is { Rule: "MQ2001", JsonPointer: "/errors/1/input" });

        // Renaming keeps the id, so every reference holds.
        await repo.EditAsync(Send, n => n["input"]![0]!["name"] = "to");
        var diagnostics = await DiagnosticsAsync(repo);
        Assert.True(diagnostics.Count == 0, Describe(diagnostics));
        var send = (await ResolveAsync(repo)).Operations.Single(o => o.Id == Send);
        Assert.Equal("to", send.Errors[1].Input?.Name);
    }

    /// <summary>
    /// The rest of what the first cut keeps, beside <c>send</c>: a collection-level read on Invoice taking a list of Customer keys,
    /// returning a list of invoices, with an error whose code differs from its name, requiring an actor; and an <c>each operation</c>
    /// unit rendering one file per operation.
    /// </summary>
    [Fact]
    public async Task Search_takes_keys_returns_a_list_requires_an_actor_and_each_operation_renders_both()
    {
        await using var repo = await SendCopyAsync();
        await repo.CreateElementAsync($$"""{ "kind": "actor", "id": "{{Clerk}}", "name": "Clerk", "package": "{{Billing}}", "type": "role" }""");
        await repo.EditAsync(Lifecycle, n => n["events"]![0]!["actors"] = new JsonArray(Clerk)); // the clerk raises sent
        await repo.CreateElementAsync($$"""
            {
              "kind": "operation", "id": "{{Search}}", "name": "search", "subject": "{{Invoice}}", "level": "collection", "effect": "read",
              "input": [ { "id": "{{Customers}}", "name": "customers", "keyOf": "{{Customer}}", "collection": true } ],
              "outcomes": [ { "id": "{{Found}}", "name": "found", "returns": { "entity": "{{Invoice}}", "list": true } } ],
              "errors": [ { "id": "{{TooBroad}}", "name": "too_broad", "code": "search.too_broad" } ],
              "requires": [ "{{Clerk}}" ]
            }
            """);
        var pack = Path.Combine(repo.Repo.ModelRoot, "templates", "ops");
        Directory.CreateDirectory(pack);
        File.WriteAllText(Path.Combine(pack, "pack.json"), """
            {"name": "ops", "version": "1.0.0", "engine": ">=1.0", "units": [
              {"id": "op", "template": "op.scriban", "for": "each operation", "output": "src/Generated/ops/{{ operation.name }}.txt"}]}
            """);
        File.WriteAllText(Path.Combine(pack, "op.scriban"),
            "{{ operation.name }} {{ operation.level }} {{ operation.effect }}"
            + "{{ for m in operation.input }} {{ m.name }}{{ if m.key_of }}:keys of {{ m.key_of.name }}{{ end }}{{ end }}"
            + "{{ for o in operation.outcomes }} {{ o.name }}{{ if o.returns && o.returns.is_list }}[]{{ end }}{{ end }}"
            + "{{ for e in operation.errors }} {{ e.code }}{{ end }}{{ for r in operation.requires }} requires {{ r.name }}{{ end }}\n");

        var diagnostics = await DiagnosticsAsync(repo);
        var result = await repo.ApplyAsync();

        Assert.True(diagnostics.Count == 0, Describe(diagnostics));
        E2ERepo.AssertOutcome(RunOutcome.Succeeded, result);
        var model = await ResolveAsync(repo);
        var search = model.Operations.Single(o => o.Id == Search);
        var customers = Assert.Single(search.Input);
        Assert.Equal((Customer, true, "uuid"), (customers.KeyOf?.Id, customers.Collection, customers.Type?.Builtin));
        Assert.Equal((Invoice, true), (search.Outcomes[0].Returns?.Entity?.Id, search.Outcomes[0].Returns!.IsList));
        Assert.Equal(("too_broad", "search.too_broad"), (search.Errors[0].Name, search.Errors[0].Code));
        Assert.Equal([Clerk], search.Requires.Select(r => r.Id));
        Assert.Equal([Search, Send], model.Entities.Single(e => e.Id == Invoice).Operations.Select(o => o.Id));
        Assert.Equal("search collection read customers:keys of Customer found[] search.too_broad requires Clerk\n", repo.Repo.ReadFile("src/Generated/ops/search.txt"));
        Assert.Equal("send record change recipient applied waiting already_sent missing_recipient\n", repo.Repo.ReadFile("src/Generated/ops/send.txt"));
    }
}
