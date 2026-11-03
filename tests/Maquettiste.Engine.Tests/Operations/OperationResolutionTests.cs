using Maquettiste.Engine.Model;
using Maquettiste.Engine.Resolution;
using Maquettiste.Engine.Tests.Resolution;
using Maquettiste.Testing;
using static Maquettiste.Engine.Tests.Operations.OperationFixture;

namespace Maquettiste.Engine.Tests.Operations;

/// <summary>Resolution of operations (next-phase-2-design.md section 4.1, task E3); inherited errors are in <see cref="OperationInheritanceTests"/>.</summary>
public sealed class OperationResolutionTests
{
    private static Operation Op(ModelBuilder b, string name, string? subject, Func<Operation, Operation>? change = null)
    {
        var operation = new Operation
        {
            Id = b.NewId(), Name = name, Subject = subject, Effect = OperationEffect.Read, Outcomes = [new OperationOutcome { Id = b.NewId(), Name = "done" }],
        };
        operation = change is null ? operation : change(operation);
        b.Add(operation);
        return operation;
    }

    [Fact]
    public async Task Send_resolves_subject_level_effect_input_outcomes_errors_and_fires()
    {
        await using var s = await OpenAsync();
        var model = ResolutionKit.Resolve(s.Model);

        var send = Assert.Single(model.Operations);
        Assert.Equal(("operation", "send", "send", "Billing", "Invoice", "record", "change"),
            (send.Kind, send.Name, send.DisplayName, send.Package?.Name, send.Subject?.Name, send.Level, send.Effect));
        Assert.Same(send, model.Find(Send));

        var recipient = Assert.Single(send.Input);
        Assert.Equal(("operation-member", "recipient", "string", 254, true, 0), (recipient.Kind, recipient.Name, recipient.Type?.Name, recipient.Length, recipient.Required, recipient.Order));
        Assert.Same(send, recipient.Operation);
        Assert.Null(recipient.Outcome);
        Assert.Same(recipient, model.Find(Recipient));

        Assert.Equal(["applied", "waiting"], send.Outcomes.Select(o => o.Name));
        var applied = send.Outcomes[0];
        Assert.Equal(("now", true, "Invoice", false), (applied.Completes, applied.IsDefault, applied.Returns?.Entity?.Name, applied.Returns!.IsList));
        Assert.Empty(applied.Returns.Members);
        var waiting = send.Outcomes[1];
        Assert.Equal(("later", false), (waiting.Completes, waiting.IsDefault));
        var trackingId = Assert.Single(waiting.Returns!.Members);
        Assert.Equal(("trackingId", "ulid"), (trackingId.Name, trackingId.Type?.Name));
        Assert.Same(waiting, trackingId.Outcome);
        Assert.Same(trackingId, model.Find(TrackingId));
        Assert.Same(applied, send.DefaultOutcome);

        Assert.Equal([("already_sent", "already_sent", (string?)null), ("missing_recipient", "missing_recipient", "recipient")],
            send.Errors.Select(e => (e.Name, e.Code, e.Input?.Name)));
        Assert.All(send.Errors, e => Assert.Equal(("own", send), (e.MemberOrigin.Source, e.Operation)));
        Assert.Same(send.Errors[1], model.Find(MissingRecipient));

        var sent = Assert.Single(send.Fires);
        Assert.Equal(("event", "sent"), (sent.Kind, sent.Name));
        Assert.Empty(send.Requires);

        // Every object depends on the operation's file; the operation on its subject's, the applied outcome on Invoice's.
        Assert.Contains("e:" + Send, send.Dependencies);
        Assert.Contains("e:" + Invoice, send.Dependencies);
        Assert.Contains("e:" + Send, recipient.Dependencies);
        Assert.Contains("e:" + Invoice, applied.Dependencies);
        Assert.Contains("e:" + Send, send.Errors[0].Dependencies);
        Assert.Contains("k:operation", model.Operations.MembershipKeys);
    }

    [Fact]
    public void File_members_file_results_and_paging_resolve()
    {
        var b = new ModelBuilder(77);
        var invoice = b.Entity("Invoice").Key("id", "uuid");
        var operation = Op(b, "attach", invoice.Id, o => o with
        {
            Input = [new OperationMember { Id = b.NewId(), Name = "scan", File = true, MediaTypes = ["image/png", "application/pdf"], Length = 1048576 }],
            Outcomes = [new OperationOutcome { Id = b.NewId(), Name = "pdf", Returns = new OperationReturns { File = true, MediaTypes = ["application/pdf"] } },
                new OperationOutcome { Id = b.NewId(), Name = "found", Returns = new OperationReturns { Entity = invoice.Id, List = true, Paging = OperationPaging.Cursor, MaxPageSize = 100 } }],
        });

        var resolved = ResolutionKit.Resolve(b).Operations.Single(o => o.Id == operation.Id);

        var scan = Assert.Single(resolved.Input);
        Assert.Equal((true, 1048576, (object?)null), (scan.IsFile, scan.Length, (object?)scan.Type));
        Assert.Equal(["image/png", "application/pdf"], scan.MediaTypes);
        var pdf = resolved.Outcomes[0].Returns!;
        Assert.Equal((true, false, (string?)null), (pdf.IsFile, pdf.IsList, pdf.Paging));
        Assert.Equal(["application/pdf"], pdf.MediaTypes);
        var found = resolved.Outcomes[1].Returns!;
        Assert.Equal((false, true, "cursor", 100), (found.IsFile, found.IsList, found.Paging, found.MaxPageSize));
    }

    [Fact]
    public void The_default_outcome_is_the_named_one_else_the_first()
    {
        var b = new ModelBuilder(61);
        var invoice = b.Entity("Invoice").Key("id", "uuid");
        string applied = b.NewId(), waiting = b.NewId();
        OperationOutcome[] outcomes = [new() { Id = applied, Name = "applied" }, new() { Id = waiting, Name = "waiting" }];
        Op(b, "send", invoice.Id, o => o with { Outcomes = outcomes, DefaultOutcome = waiting });
        Op(b, "remind", invoice.Id, o => o with { Outcomes = [new() { Id = b.NewId(), Name = "first" }, new() { Id = b.NewId(), Name = "second" }] });

        var model = ResolutionKit.Resolve(b);

        var send = model.Operations.Single(o => o.Name == "send");
        Assert.Equal("waiting", send.DefaultOutcome!.Name);
        Assert.Equal([false, true], send.Outcomes.Select(o => o.IsDefault));
        var remind = model.Operations.Single(o => o.Name == "remind");
        Assert.Equal("first", remind.DefaultOutcome!.Name);
        Assert.Equal([true, false], remind.Outcomes.Select(o => o.IsDefault));
    }

    [Fact]
    public void A_key_member_takes_its_entitys_key_type()
    {
        var b = new ModelBuilder(62);
        var customer = b.Entity("Customer").Key("id", "uuid");
        var line = b.Entity("InvoiceLine").Key("invoice", "uuid").Attr("position", "int32").CompositeKey("invoice", "position");
        string single = b.NewId(), composite = b.NewId();
        Op(b, "merge", null, o => o with
        {
            Input =
            [
                new OperationMember { Id = single, Name = "customers", KeyOf = customer.Id, Collection = true },
                new OperationMember { Id = composite, Name = "line", KeyOf = line.Id },
            ],
        });

        var model = ResolutionKit.Resolve(b);

        var members = model.Operations.Single().Input;
        Assert.Equal(("Customer", "uuid", true), (members[0].KeyOf?.Name, members[0].Type?.Name, members[0].Collection));
        Assert.Contains("e:" + customer.Id, members[0].Dependencies);
        Assert.Equal("InvoiceLine", members[1].KeyOf?.Name);
        Assert.Null(members[1].Type); // a key of two attributes has no single type
        Assert.Null(model.Operations.Single().Level); // no subject, no level
    }

    [Fact]
    public async Task Invoice_lists_its_operations_and_an_event_lists_what_fires_it()
    {
        await using var s = await OpenAsync();
        await CreateAsync(s, $$"""{ "kind": "operation", "name": "archive", "subject": "{{Invoice}}", "effect": "change", "fires": [ "{{Sent}}" ], "outcomes": [ { "id": "01JR5NDZ000000000000000001", "name": "done" } ] }""");
        await CreateAsync(s, $$"""{ "kind": "operation", "name": "audit", "subject": "{{Customer}}", "effect": "read", "outcomes": [ { "id": "01JR5NDZ000000000000000002", "name": "done" } ] }""");

        var model = ResolutionKit.Resolve(s.Model);

        Assert.Equal(["archive", "send"], model.Entity("Invoice").Operations.Select(o => o.Name));
        Assert.Equal(["audit"], model.Entity("Customer").Operations.Select(o => o.Name));
        Assert.Empty(model.Entity("Product").Operations);
        Assert.Equal(["r:" + Invoice, "k:operation"], model.Entity("Invoice").Operations.MembershipKeys);
        var sent = Assert.IsType<REvent>(model.Find(Sent));
        Assert.Equal(["archive", "send"], sent.FiredBy.Select(o => o.Name));
        Assert.Equal(["r:" + Sent, "k:operation"], sent.FiredBy.MembershipKeys);
    }

    [Fact]
    public void Without_a_package_it_takes_its_subjects()
    {
        var b = new ModelBuilder(63);
        var billing = b.Package("Billing");
        var sales = b.Package("Sales");
        var invoice = b.Entity("Invoice", billing).Key("id", "uuid");
        Op(b, "send", invoice.Id);
        Op(b, "archive", invoice.Id, o => o with { Package = sales.Id });
        Op(b, "ping", null);

        var model = ResolutionKit.Resolve(b);

        string? PackageOf(string name) => model.Operations.Single(o => o.Name == name).Package?.Name;
        Assert.Equal("Billing", PackageOf("send"));
        Assert.Equal("Sales", PackageOf("archive"));
        Assert.Null(PackageOf("ping"));
        Assert.Equal("Billing", model.Operations.Single(o => o.Name == "send").Outcomes[0].Package?.Name);
    }

    [Fact]
    public void Operations_sort_by_package_subject_name_and_id()
    {
        var b = new ModelBuilder(64);
        var billing = b.Package("Billing");
        var customer = b.Entity("Customer", billing).Key("id", "uuid");
        var invoice = b.Entity("Invoice", billing).Key("id", "uuid");
        var product = b.Entity("Product").Key("id", "uuid");
        Op(b, "send", invoice.Id);
        Op(b, "archive", invoice.Id);
        Op(b, "merge", customer.Id);
        Op(b, "ping", null, o => o with { Package = billing.Id });
        Op(b, "restock", product.Id);
        Op(b, "health", null);

        var model = ResolutionKit.Resolve(b);

        Assert.Equal(["health", "restock", "ping", "merge", "archive", "send"], model.Operations.Select(o => o.Name));
    }

    [Fact]
    public void Requires_resolves_ids_of_any_kind_in_file_order()
    {
        var b = new ModelBuilder(65);
        var invoice = b.Entity("Invoice").Key("id", "uuid").Attr("number", "string");
        var status = b.Enum("InvoiceStatus").Member("Draft", 0);
        var actor = new Actor { Id = b.NewId(), Name = "Clerk", Type = ActorType.Role };
        b.Add(actor);
        var ghost = b.NewId();
        var send = Op(b, "send", invoice.Id);
        Op(b, "approve", invoice.Id, o => o with { Requires = [actor.Id, invoice.AttrId("number"), ghost, status.Id, send.Id, actor.Id, send.Outcomes[0].Id] });

        var model = ResolutionKit.Resolve(b);

        var approve = model.Operations.Single(o => o.Name == "approve");
        Assert.Equal([("actor", "Clerk"), ("attribute", "number"), ("enum", "InvoiceStatus"), ("operation", "send"), ("outcome", "done")],
            approve.Requires.Select(r => (r.Kind, ((RElement)r).Name)));
        Assert.Contains("e:" + actor.Id, approve.Requires.MembershipKeys);
        Assert.Contains("e:" + invoice.Id, approve.Requires.MembershipKeys); // the attribute's file
        Assert.Contains("e:" + ghost, approve.Requires.MembershipKeys); // a missing id still keys the list: it may appear
    }

    [Fact]
    public void Requires_reaches_a_stereotype_resolved_after_the_operations_pass()
    {
        var b = new ModelBuilder(66);
        var invoice = b.Entity("Invoice").Key("id", "uuid");
        b.Stereotype("approver-only").AppliesTo("operation");
        var model0 = b.BuildElements();
        var stereotype = model0.OfType<Stereotype>().Single();
        Op(b, "approve", invoice.Id, o => o with { Requires = [stereotype.Id] });

        var model = ResolutionKit.Resolve(b);

        var required = Assert.Single(model.Operations.Single().Requires);
        var definition = Assert.IsType<RStereotypeDefinition>(required);
        Assert.Equal("approver-only", definition.Key);
    }
}
