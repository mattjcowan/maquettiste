using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Generation;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Pipeline;
using Maquettiste.Engine.Tests.Model;
using Maquettiste.Engine.Tests.Store;
using Maquettiste.Testing;
using static Maquettiste.Engine.Tests.Operations.OperationFixture;

namespace Maquettiste.Engine.Tests.Operations;

/// <summary>
/// The operation kind's records, schema, index rows, folders and name forms (next-phase-2-design.md section 4.1, task E1).
/// </summary>
public sealed class OperationModelTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string SendPath = ".maquettiste/model/operations/invoice/send.json";

    [Fact]
    public void An_operation_round_trips_canonically_with_every_key()
    {
        var json = $$"""
            {
              "$schema": "../../../.schema/v1/operation.json",
              "kind": "operation",
              "id": "{{Send}}",
              "name": "send",
              "displayName": "Send",
              "pluralName": "Sends",
              "package": "{{Billing}}",
              "subject": "{{Invoice}}",
              "level": "collection",
              "effect": "change",
              "input": [
                {
                  "id": "{{Recipient}}",
                  "name": "recipient",
                  "displayName": "Recipient",
                  "pluralName": "Recipients",
                  "type": "decimal",
                  "length": 254,
                  "precision": 12,
                  "scale": 2,
                  "required": true,
                  "default": 1,
                  "collection": true,
                  "sensitive": "pii",
                  "validation": {
                    "min": 0
                  },
                  "description": "Who gets it.",
                  "rationale": "Mail.",
                  "decision": "ADR-0042",
                  "status": "proposed",
                  "stereotypes": [
                    "audited"
                  ],
                  "tags": [
                    "api"
                  ],
                  "category": "{{Draft}}",
                  "properties": {
                    "audience": "public"
                  },
                  "generation": {
                    "*": {
                      "skip": true
                    }
                  },
                  "source": {
                    "format": "openapi"
                  }
                },
                {
                  "id": "{{TrackingId}}",
                  "name": "lines",
                  "keyOf": "{{Product}}"
                }
              ],
              "outcomes": [
                {
                  "id": "{{Applied}}",
                  "name": "applied",
                  "displayName": "Applied",
                  "pluralName": "Applieds",
                  "completes": "later",
                  "returns": {
                    "members": [
                      {
                        "id": "{{Sent}}",
                        "name": "trackingId",
                        "type": "ulid"
                      }
                    ],
                    "entity": "{{Invoice}}",
                    "list": true
                  },
                  "description": "Done."
                }
              ],
              "defaultOutcome": "{{Applied}}",
              "errors": [
                {
                  "id": "{{AlreadySent}}",
                  "name": "already_sent",
                  "displayName": "Already sent",
                  "pluralName": "Already sents",
                  "code": "invoice.already-sent",
                  "input": "{{Recipient}}",
                  "description": "It went out before."
                }
              ],
              "fires": [
                "{{Sent}}"
              ],
              "requires": [
                "{{Customer}}"
              ],
              "description": "Sends the invoice.",
              "rationale": "Customers ask for it.",
              "decision": "ADR-0043",
              "status": "approved",
              "stereotypes": [
                "audited"
              ],
              "excludeStereotypes": [
                "owned"
              ],
              "tags": [
                "api"
              ],
              "category": "{{Draft}}",
              "properties": {
                "audience": "public"
              },
              "generation": {
                "*": {
                  "skip": true
                }
              },
              "source": {
                "format": "openapi"
              }
            }

            """.ReplaceLineEndings("\n");
        var bytes = Encoding.UTF8.GetBytes(json);
        using var parsed = JsonDocument.Parse(bytes);

        Assert.Empty(TestServices.Schemas.Evaluate("operation.json", parsed.RootElement, SendPath));
        Assert.True(TestServices.Json.IsCanonical(bytes, "operation.json", SendPath));
        var operation = JsonSerializer.Deserialize<Operation>(bytes, EngineJson.Options)!;
        Assert.Equal((OperationLevel.Collection, OperationEffect.Change), (operation.Level, operation.Effect));
        Assert.Equal(Product, operation.Input[1].KeyOf);
        Assert.Equal(OperationCompletion.Later, operation.Outcomes[0].Completes);
        Assert.True(operation.Outcomes[0].Returns!.List);
        Assert.Equal("invoice.already-sent", operation.Errors[0].Code);
        Assert.Equal(json, Encoding.UTF8.GetString(TestServices.Json.Serialize(operation, "operation.json", SendPath)));

        // Defaults stay out of the file: the default level, completes, list and the empty lists.
        var minimal = new Operation
        {
            Id = Send, Name = "ping", Effect = OperationEffect.Read,
            Outcomes = [new OperationOutcome { Id = Applied, Name = "done", Returns = new OperationReturns { Entity = Invoice } }],
        };
        Assert.Equal($$"""
            {
              "$schema": "../../.schema/v1/operation.json",
              "kind": "operation",
              "id": "{{Send}}",
              "name": "ping",
              "effect": "read",
              "outcomes": [
                {
                  "id": "{{Applied}}",
                  "name": "done",
                  "returns": {
                    "entity": "{{Invoice}}"
                  }
                }
              ]
            }

            """.ReplaceLineEndings("\n"), Encoding.UTF8.GetString(TestServices.Json.Serialize(minimal, "operation.json", ".maquettiste/model/operations/ping.json")));
    }

    [Fact]
    public async Task With_a_subject_it_lives_in_the_subjects_folder_and_without_one_in_model_operations()
    {
        await using var s = await OpenAsync();
        await CreateAsync(s, $$"""{ "kind": "operation", "name": "archive", "subject": "{{Invoice}}", "effect": "change", "outcomes": [ { "id": "{{NewId(1)}}", "name": "done" } ] }""");
        await CreateAsync(s, $$"""{ "kind": "operation", "name": "archive", "subject": "{{Customer}}", "effect": "change", "outcomes": [ { "id": "{{NewId(2)}}", "name": "done" } ] }""");
        var ping = await CreateAsync(s, $$"""{ "kind": "operation", "name": "ping", "effect": "read", "outcomes": [ { "id": "{{NewId(3)}}", "name": "pong" } ] }""");

        Assert.Equal(SendPath, s.Model.GetDocument(Send)!.Path);
        Assert.True(s.Harness.Exists("model/operations/invoice/archive.json"));
        Assert.True(s.Harness.Exists("model/operations/customer/archive.json"));
        Assert.Equal(".maquettiste/model/operations/ping.json", s.Model.GetDocument(ping.Id!)!.Path);
        Assert.StartsWith("{\n  \"$schema\": \"../../../.schema/v1/operation.json\",", s.Harness.Read("model/operations/invoice/send.json"), StringComparison.Ordinal);

        // A file in another folder loads and reports MQ1005 with the conventional place.
        s.Harness.Write("model/operations/stray.json", s.Harness.Read("model/operations/invoice/archive.json"));
        File.Delete(s.Harness.Model("model/operations/invoice/archive.json"));
        var loaded = await s.Harness.NewLoader().LoadAsync(new LoadRequest(null, null, false), null, Ct);
        var misplaced = Assert.Single(loaded.Snapshot.LoadDiagnostics, d => d.Rule == "MQ1005");
        Assert.Contains("belongs at .maquettiste/model/operations/invoice/archive.json", misplaced.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(loaded.Snapshot.LoadDiagnostics, d => d.Rule == "MQ1005" && d.ElementId == Send);
    }

    [Fact]
    public async Task Changing_the_subject_moves_the_file()
    {
        await using var s = await OpenAsync();
        var document = s.Model.GetDocument(Send)!;

        var saved = await s.Store.SaveAsync(Send, BillingStore.Edit(document, o => o["subject"] = Customer), document.Hash, ChangeSource.Cli, Ct);

        Assert.True(saved.Outcome == SaveOutcome.Saved, string.Join("; ", saved.Diagnostics.Select(d => d.Message)));
        Assert.Equal(".maquettiste/model/operations/customer/send.json", s.Model.GetDocument(Send)!.Path);
        Assert.False(s.Harness.Exists("model/operations/invoice/send.json"));

        // Dropping the subject moves it to model/operations/.
        document = s.Model.GetDocument(Send)!;
        saved = await s.Store.SaveAsync(Send, BillingStore.Edit(document, o => o.Remove("subject")), document.Hash, ChangeSource.Cli, Ct);
        Assert.Equal(SaveOutcome.Saved, saved.Outcome);
        Assert.Equal(".maquettiste/model/operations/send.json", s.Model.GetDocument(Send)!.Path);
    }

    [Fact]
    public async Task Clearing_the_subject_in_a_delete_moves_the_file_and_loads_clean()
    {
        await using var s = await OpenAsync(withSend: false);
        var voucher = await CreateAsync(s, $$"""
            { "kind": "entity", "name": "Voucher", "key": { "attributes": [ "{{NewId(11)}}" ] },
              "attributes": [ { "id": "{{NewId(11)}}", "name": "id", "type": "uuid" } ] }
            """);
        var issue = await CreateAsync(s, $$"""{ "kind": "operation", "name": "issue", "subject": "{{voucher.Id}}", "effect": "change", "outcomes": [ { "id": "{{NewId(12)}}", "name": "done" } ] }""");
        Assert.Equal(".maquettiste/model/operations/voucher/issue.json", s.Model.GetDocument(issue.Id!)!.Path);

        var deleted = await s.Store.DeleteAsync(voucher.Id!, s.Model.GetDocument(voucher.Id!)!.Hash, DeleteResolution.RemoveReferences, ChangeSource.Cli, Ct);

        Assert.True(deleted.Outcome == SaveOutcome.Saved, string.Join("; ", deleted.Diagnostics.Select(d => $"{d.Rule} {d.Message}")));
        Assert.Equal(".maquettiste/model/operations/issue.json", s.Model.GetDocument(issue.Id!)!.Path);
        Assert.False(s.Harness.Exists("model/operations/voucher/issue.json"));
        var loaded = await s.Harness.NewLoader().LoadAsync(new LoadRequest(null, null, false), null, Ct);
        Assert.DoesNotContain(loaded.Snapshot.LoadDiagnostics, d => d.Rule == "MQ1005");
    }

    [Fact]
    public async Task Members_outcomes_and_errors_are_indexed_by_their_kinds()
    {
        await using var s = await OpenAsync();
        var model = s.Model;

        (string Owner, string Kind, string Pointer) Entry(string id) =>
            model.TryGetEntry(id, out var entry) ? (entry.OwnerId, entry.Kind, entry.JsonPointer) : default;

        Assert.Equal((Send, "operation", ""), Entry(Send));
        Assert.Equal((Send, "operation-member", "/input/0"), Entry(Recipient));
        Assert.Equal((Send, "outcome", "/outcomes/0"), Entry(Applied));
        Assert.Equal((Send, "outcome", "/outcomes/1"), Entry(Waiting));
        Assert.Equal((Send, "operation-member", "/outcomes/1/returns/members/0"), Entry(TrackingId));
        Assert.Equal((Send, "error", "/errors/0"), Entry(AlreadySent));
        Assert.Equal((Send, "error", "/errors/1"), Entry(MissingRecipient));

        // Every link is a reference: the subject, a returns' entity, an error's input, a fired event.
        Assert.Contains(model.ReferencesTo(Invoice), r => r is { FromElementId: Send, FromId: Send, JsonPointer: "/subject", Owning: false });
        Assert.Contains(model.ReferencesTo(Invoice), r => r is { FromElementId: Send, FromId: Applied, JsonPointer: "/outcomes/0/returns/entity" });
        Assert.Contains(model.ReferencesTo(Recipient), r => r is { FromElementId: Send, FromId: MissingRecipient, JsonPointer: "/errors/1/input" });
        Assert.Contains(model.ReferencesTo(Sent), r => r is { FromElementId: Send, JsonPointer: "/fires/0" });
        Assert.Equal([Recipient, Applied, Waiting, TrackingId, AlreadySent, MissingRecipient],
            Maquettiste.Engine.Validation.ModelValidator.SubElementIds(model.Get<Operation>(Send)!));

        // The model validates clean apart from the fixture's own findings.
        var report = await Validator(s).ValidateAsync(model, ValidationScope.All, null, Ct);
        Assert.DoesNotContain(report.Diagnostics, d => d.ElementId is Send or Dispatch && d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public async Task The_index_row_carries_subject_and_effect()
    {
        await using var s = await OpenAsync();
        var ping = await CreateAsync(s, $$"""{ "kind": "operation", "name": "ping", "effect": "read", "outcomes": [ { "id": "{{NewId(3)}}", "name": "pong" } ] }""");

        var row = s.Model.Summaries().Single(r => r.Id == Send);
        var bare = s.Model.Summaries().Single(r => r.Id == ping.Id);

        Assert.Equal(("operation", "send", Billing, Invoice, "change", SendPath), (row.Kind, row.Name, row.Package, row.Subject, row.Effect, row.Path));
        Assert.Equal(((string?)null, (string?)null, "read"), (bare.Package, bare.Subject, bare.Effect));
        var json = JsonSerializer.SerializeToNode(row, Web)!.AsObject();
        Assert.Equal("change", json["effect"]!.GetValue<string>());
        Assert.Equal(Invoice, json["subject"]!.GetValue<string>());
        Assert.False(JsonSerializer.SerializeToNode(s.Model.Summaries().First(r => r.Kind == "entity"), Web)!.AsObject().ContainsKey("effect"));
        Assert.NotEqual(row, row with { Effect = "read" });
        Assert.Equal("maquettiste-index/e10", ModelReads.IndexFormat);
    }

    [Fact]
    public async Task Batch_names_reach_an_operation_through_its_subject_and_its_parts_through_it()
    {
        await using var s = await OpenAsync();
        var batch = s.Store.ParseBatch(Encoding.UTF8.GetBytes("""
            { "operations": [ { "op": "create", "element": { "kind": "operation", "name": "remind", "subject": "@entity:Invoice", "effect": "read",
                "outcomes": [ { "name": "done" } ],
                "requires": [ "@operation:Invoice.send", "@operation:Billing.Invoice.send", "@operation:Billing.send",
                  "@operation-member:Invoice.send.recipient", "@outcome:Invoice.send.waiting", "@operation-member:send.waiting.trackingId",
                  "@error:Billing.Invoice.send.already_sent" ] } } ] }
            """));
        Assert.True(batch.Batch is not null, string.Join("; ", batch.Diagnostics.Select(d => d.Message)));

        var applied = await s.Store.ApplyBatchAsync(batch.Batch, ChangeSource.Cli, Ct);

        Assert.True(applied.Outcome == SaveOutcome.Saved, string.Join("; ", applied.Items.SelectMany(i => i.Diagnostics).Select(d => $"{d.Rule} {d.Message}")));
        var remind = s.Model.All<Operation>().Single(o => o.Name == "remind");
        Assert.Equal(Invoice, remind.Subject);
        Assert.Equal([Send, Send, Send, Recipient, Waiting, TrackingId, AlreadySent], remind.Requires);
        Assert.Equal(".maquettiste/model/operations/invoice/remind.json", s.Model.GetDocument(remind.Id)!.Path);

        // A bare name shared by two subjects is ambiguous; the subject form tells them apart.
        await CreateAsync(s, $$"""{ "kind": "operation", "name": "send", "subject": "{{Customer}}", "effect": "change", "outcomes": [ { "id": "{{NewId(4)}}", "name": "done" } ] }""");
        var ambiguous = s.Store.ParseBatch(Encoding.UTF8.GetBytes("""
            { "operations": [ { "op": "create", "element": { "kind": "operation", "name": "audit", "effect": "read", "outcomes": [ { "name": "done" } ],
                "requires": [ "@operation:send" ] } } ] }
            """));
        var refused = await s.Store.ApplyBatchAsync(ambiguous.Batch!, ChangeSource.Cli, Ct);
        Assert.Contains(refused.Items.SelectMany(i => i.Diagnostics), d => d.Rule == "MQ1013" && d.Message.Contains("@operation:Billing.Invoice.send", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_new_operations_parts_resolve_through_its_subject_form_in_the_same_create()
    {
        await using var s = await OpenAsync(withSend: false);
        var batch = s.Store.ParseBatch(Encoding.UTF8.GetBytes("""
            { "operations": [ { "op": "create", "element": { "kind": "operation", "name": "remind", "subject": "@entity:Invoice", "effect": "change",
                "input": [ { "name": "recipient", "type": "string" } ],
                "outcomes": [ { "name": "done" } ],
                "errors": [ { "name": "no_recipient", "input": "@operation-member:Invoice.remind.recipient" } ] } } ] }
            """));
        Assert.True(batch.Batch is not null, string.Join("; ", batch.Diagnostics.Select(d => d.Message)));

        var applied = await s.Store.ApplyBatchAsync(batch.Batch, ChangeSource.Cli, Ct);

        Assert.True(applied.Outcome == SaveOutcome.Saved, string.Join("; ", applied.Items.SelectMany(i => i.Diagnostics).Select(d => $"{d.Rule} {d.Message}")));
        var remind = s.Model.All<Operation>().Single(o => o.Name == "remind");
        Assert.Equal(Invoice, remind.Subject);
        Assert.Equal(remind.Input[0].Id, Assert.Single(remind.Errors).Input);
    }

    [Fact]
    public void A_package_default_stereotype_reaches_an_operation_through_its_subjects_package()
    {
        var b = new ModelBuilder(25);
        b.Stereotype("owned").AppliesTo("operation", "entity");
        var billing = b.Package("Billing");
        var sales = b.Package("Sales");
        var invoice = b.Entity("Invoice", billing).Key("id", "uuid");
        string inherited = b.NewId(), own = b.NewId(), excluded = b.NewId(), bare = b.NewId();
        Operation Op(string id, string name, string? package, string? subject, IReadOnlyList<string>? exclude = null) => new()
        {
            Id = id, Name = name, Package = package, Subject = subject, Effect = OperationEffect.Read, ExcludeStereotypes = exclude,
            Outcomes = [new OperationOutcome { Id = b.NewId(), Name = "done" }],
        };
        b.Add(Op(inherited, "send", null, invoice.Id));
        b.Add(Op(own, "archive", sales.Id, invoice.Id));
        b.Add(Op(excluded, "remind", null, invoice.Id, ["owned"]));
        b.Add(Op(bare, "ping", null, null));
        var model = StereotypeDefaultsTests.Rebuild(b, e => e is Package p && p.Id == billing.Id ? p with { DefaultStereotypes = ["owned"] } : e);

        IEnumerable<string> Keys(string id) => model.EffectiveStereotypes(model.Get<Operation>(id)!).Select(a => a.Stereotype.Key);

        Assert.Equal(billing.Id, model.HolderOf(model.Get<Operation>(inherited)!).Package);
        Assert.Equal(["owned"], Keys(inherited));
        Assert.Empty(Keys(own)); // its own package has no default; the subject's is not consulted
        Assert.Empty(Keys(excluded));
        Assert.Empty(Keys(bare));
    }

    private static string NewId(int n) => "01JR5NDZ" + n.ToString("D18", System.Globalization.CultureInfo.InvariantCulture);
}
