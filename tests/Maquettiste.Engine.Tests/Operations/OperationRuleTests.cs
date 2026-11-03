using System.Text.Json;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Tests.Validation;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Operations;

/// <summary>
/// The rules on an operation's own structure (next-phase-2-design.md section 4.1, task E2): one failing and one passing case per rule
/// from MQ3023 to MQ3031 (MQ3029, on a stereotype's errors, is in <see cref="OperationInheritanceTests"/>), and the rules its parts
/// share with other kinds.
/// </summary>
public sealed class OperationRuleTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly string[] OperationRuleIds =
        ["MQ3001", "MQ3013", "MQ3017", "MQ3018", "MQ3019", "MQ3023", "MQ3024", "MQ3025", "MQ3026", "MQ3027", "MQ3028", "MQ3030", "MQ3031", "MQ2003", "MQ2004", "MQ2007"];

    /// <summary>Billing-shaped parts: Invoice and Customer, a status enum, a dispatch process with the event <c>sent</c>.</summary>
    private sealed class Scene
    {
        public Scene(int seed)
        {
            B = new ModelBuilder(seed);
            Invoice = B.Entity("Invoice").Key("id", "uuid");
            Customer = B.Entity("Customer").Key("id", "uuid");
            Sent = B.NewId();
            B.Add(new Process
            {
                Id = B.NewId(), Name = "InvoiceDispatch", Events = [new ProcessEvent { Id = Sent, Name = "sent" }],
                States = [new ProcessState { Id = B.NewId(), Name = "draft" }],
            });
        }

        public ModelBuilder B { get; }

        public EntityBuilder Invoice { get; }

        public EntityBuilder Customer { get; }

        public string Sent { get; }

        public string Id() => B.NewId();

        public OperationMember Typed(string name, string type, Func<OperationMember, OperationMember>? change = null)
        {
            var member = new OperationMember { Id = Id(), Name = name, Type = new TypeRef { Builtin = type } };
            return change is null ? member : change(member);
        }

        public OperationOutcome Outcome(string name, OperationReturns? returns = null) => new() { Id = Id(), Name = name, Returns = returns };

        public Operation Add(Func<Operation, Operation> change, string name = "send", bool subject = true)
        {
            var operation = change(new Operation
            {
                Id = Id(), Name = name, Subject = subject ? Invoice.Id : null, Effect = OperationEffect.Change, Outcomes = [Outcome("applied")],
            });
            B.Add(operation);
            return operation;
        }

        /// <summary>The findings of the operation rules on one operation's file: on it or on one of its parts.</summary>
        public async Task<List<(string Rule, string Pointer, string? Id)>> FindingsAsync(string on)
        {
            var model = B.Build();
            var ids = Maquettiste.Engine.Validation.ModelValidator.SubElementIds(model.Get<Operation>(on)!).Prepend(on).ToHashSet(StringComparer.Ordinal);
            var report = await ValidationFixture.Validator().ValidateAsync(model, ValidationScope.All, null, Ct);
            return [.. report.Diagnostics
                .Where(d => OperationRuleIds.Contains(d.Rule) && d.ElementId is { } id && ids.Contains(id))
                .Select(d => (d.Rule, d.JsonPointer ?? "", d.ElementId))];
        }

        public async Task<List<Diagnostic>> DiagnosticsAsync()
        {
            var report = await ValidationFixture.Validator().ValidateAsync(B.Build(), ValidationScope.All, null, Ct);
            return [.. report.Diagnostics.Where(d => OperationRuleIds.Contains(d.Rule))];
        }
    }

    [Fact]
    public async Task MQ3023_a_collection_level_without_a_subject_warns()
    {
        var s = new Scene(41);
        s.Add(o => o with { Level = OperationLevel.Collection }, "search");
        var bare = s.Add(o => o with { Level = OperationLevel.Collection }, "ping", subject: false);

        var found = await s.DiagnosticsAsync();

        var warning = Assert.Single(found);
        Assert.Equal(("MQ3023", "/level", DiagnosticSeverity.Warning), (warning.Rule, warning.JsonPointer, warning.Severity));
        Assert.Equal(bare.Id, warning.ElementId);
        Assert.Contains("set a subject or remove level", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MQ3024_a_member_is_one_form_and_a_key_or_a_returns_member_takes_no_value_facets()
    {
        var s = new Scene(42);
        var clean = s.Add(o => o with
        {
            Input = [s.Typed("recipient", "string", m => m with { Length = 254, Required = true, Default = Json("\"billing@example.test\"") }),
                new OperationMember { Id = s.Id(), Name = "lines", KeyOf = s.Customer.Id, Collection = true, Required = true }],
        }, "send");
        OperationMember neither = new() { Id = s.Id(), Name = "neither" };
        OperationMember both = s.Typed("both", "string") with { KeyOf = s.Customer.Id };
        OperationMember valued = new() { Id = s.Id(), Name = "valued", KeyOf = s.Customer.Id, Length = 10, Default = Json("\"x\""), Validation = new AttributeValidation() };
        var returned = s.Typed("trackingId", "string", m => m with { Default = Json("\"none\"") });
        var broken = s.Add(o => o with
        {
            Input = [neither, both, valued],
            Outcomes = [s.Outcome("waiting", new OperationReturns { Members = [returned] })],
        }, "remind");

        Assert.Empty(await s.FindingsAsync(clean.Id));
        Assert.Equal(
            [
                ("MQ3024", "/input/0", neither.Id),
                ("MQ3024", "/input/1/keyOf", both.Id),
                ("MQ3024", "/input/2/default", valued.Id),
                ("MQ3024", "/input/2/length", valued.Id),
                ("MQ3024", "/input/2/validation", valued.Id),
                ("MQ3024", "/outcomes/0/returns/members/0/default", returned.Id),
            ],
            (await s.FindingsAsync(broken.Id)).Order());
    }

    [Fact]
    public async Task MQ3025_a_returns_is_exactly_one_of_members_or_entity()
    {
        var s = new Scene(43);
        var clean = s.Add(o => o with
        {
            Outcomes = [s.Outcome("applied", new OperationReturns { Entity = s.Invoice.Id, List = true }), s.Outcome("waiting", new OperationReturns { Members = [s.Typed("trackingId", "ulid")] }), s.Outcome("none")],
        }, "send");
        var neither = s.Outcome("empty", new OperationReturns { List = true });
        var both = s.Outcome("both", new OperationReturns { Entity = s.Invoice.Id, Members = [s.Typed("trackingId", "ulid")] });
        var broken = s.Add(o => o with { Outcomes = [neither, both] }, "remind");

        Assert.Empty(await s.FindingsAsync(clean.Id));
        Assert.Equal([("MQ3025", "/outcomes/0/returns", neither.Id), ("MQ3025", "/outcomes/1/returns", both.Id)], await s.FindingsAsync(broken.Id));
    }

    [Fact]
    public async Task MQ3024_a_file_member_takes_media_types_and_a_byte_cap_only()
    {
        var s = new Scene(141);
        var clean = s.Add(o => o with
        {
            Input = [new OperationMember { Id = s.Id(), Name = "attachment", File = true, MediaTypes = ["application/pdf"], Length = 1048576, Required = true }],
        }, "attach");
        var typed = s.Typed("note", "string", m => m with { MediaTypes = ["text/plain"] });
        var both = s.Typed("both", "string") with { File = true };
        OperationMember valued = new() { Id = s.Id(), Name = "valued", File = true, Default = Json("\"x\""), Precision = 2 };
        var broken = s.Add(o => o with { Input = [typed, both, valued] }, "remind");

        Assert.Empty(await s.FindingsAsync(clean.Id));
        Assert.Equal(
            [
                ("MQ3024", "/input/0/mediaTypes", typed.Id),
                ("MQ3024", "/input/1/file", both.Id),
                ("MQ3024", "/input/2/default", valued.Id),
                ("MQ3024", "/input/2/precision", valued.Id),
            ],
            (await s.FindingsAsync(broken.Id)).Order());
    }

    [Fact]
    public async Task MQ3025_a_file_result_is_no_list_and_paging_comes_with_a_list_and_a_max_page_size()
    {
        var s = new Scene(142);
        var clean = s.Add(o => o with
        {
            Outcomes = [s.Outcome("download", new OperationReturns { File = true, MediaTypes = ["application/pdf"] }),
                s.Outcome("found", new OperationReturns { Entity = s.Invoice.Id, List = true, Paging = OperationPaging.Cursor, MaxPageSize = 100 })],
        }, "export");
        var files = s.Outcome("files", new OperationReturns { File = true, List = true });
        var typed = s.Outcome("typed", new OperationReturns { Entity = s.Invoice.Id, MediaTypes = ["text/csv"] });
        var unlisted = s.Outcome("unlisted", new OperationReturns { Entity = s.Invoice.Id, Paging = OperationPaging.Page, MaxPageSize = 50 });
        var noMax = s.Outcome("noMax", new OperationReturns { Entity = s.Invoice.Id, List = true, Paging = OperationPaging.Page });
        var noPaging = s.Outcome("noPaging", new OperationReturns { Entity = s.Invoice.Id, List = true, MaxPageSize = 50 });
        var two = s.Outcome("two", new OperationReturns { Entity = s.Invoice.Id, File = true });
        var broken = s.Add(o => o with { Outcomes = [files, typed, unlisted, noMax, noPaging, two] }, "remind");

        Assert.Empty(await s.FindingsAsync(clean.Id));
        Assert.Equal(
            [
                ("MQ3025", "/outcomes/0/returns/list", files.Id),
                ("MQ3025", "/outcomes/1/returns/mediaTypes", typed.Id),
                ("MQ3025", "/outcomes/2/returns/paging", unlisted.Id),
                ("MQ3025", "/outcomes/3/returns/paging", noMax.Id),
                ("MQ3025", "/outcomes/4/returns/maxPageSize", noPaging.Id),
                ("MQ3025", "/outcomes/5/returns", two.Id),
            ],
            (await s.FindingsAsync(broken.Id)).Order());
    }

    [Fact]
    public async Task MQ3026_errors_share_no_name_and_no_code()
    {
        var s = new Scene(44);
        var clean = s.Add(o => o with
        {
            Errors = [new OperationError { Id = s.Id(), Name = "already_sent" }, new OperationError { Id = s.Id(), Name = "missing_recipient", Code = "invoice.no-recipient" }],
        }, "send");
        OperationError first = new() { Id = s.Id(), Name = "already_sent" };
        OperationError sameName = new() { Id = s.Id(), Name = "already_sent" };
        OperationError sameCode = new() { Id = s.Id(), Name = "sent_before", Code = "already_sent" };
        var broken = s.Add(o => o with { Errors = [first, sameName, sameCode] }, "remind");

        Assert.Empty(await s.FindingsAsync(clean.Id));
        var found = (await s.DiagnosticsAsync()).Where(d => d.ElementId != clean.Id && d.Rule == "MQ3026").ToList();
        Assert.Equal([("/errors/1/name", sameName.Id), ("/errors/2/code", sameCode.Id)], found.Select(d => (d.JsonPointer!, d.ElementId!)));
        Assert.Contains("clients could not tell the errors apart", found[1].Message, StringComparison.Ordinal);
        Assert.DoesNotContain(await s.FindingsAsync(broken.Id), f => f.Rule != "MQ3026");
    }

    [Fact]
    public async Task MQ3027_an_errors_input_is_one_of_the_operations_input_members()
    {
        var s = new Scene(45);
        var recipient = s.Typed("recipient", "string");
        var clean = s.Add(o => o with { Input = [recipient], Errors = [new OperationError { Id = s.Id(), Name = "missing_recipient", Input = recipient.Id }] }, "send");
        var trackingId = s.Typed("trackingId", "ulid");
        OperationError own = new() { Id = s.Id(), Name = "bad_tracking", Input = trackingId.Id };
        OperationError other = new() { Id = s.Id(), Name = "bad_recipient", Input = recipient.Id };
        var broken = s.Add(o => o with
        {
            Outcomes = [s.Outcome("waiting", new OperationReturns { Members = [trackingId] })], Errors = [own, other],
        }, "remind");

        Assert.Empty(await s.FindingsAsync(clean.Id));
        var found = (await s.DiagnosticsAsync()).Where(d => d.Rule == "MQ3027").ToList();
        Assert.Equal([("/errors/0/input", own.Id), ("/errors/1/input", other.Id)], found.Select(d => (d.JsonPointer!, d.ElementId!)));
        Assert.Contains("a member of one of its returns", found[0].Message, StringComparison.Ordinal);
        Assert.Contains("a member of another operation", found[1].Message, StringComparison.Ordinal);
        Assert.NotNull(broken);
    }

    [Fact]
    public async Task MQ3028_the_default_outcome_is_one_of_its_own()
    {
        var s = new Scene(46);
        var waiting = s.Outcome("waiting");
        var clean = s.Add(o => o with { Outcomes = [s.Outcome("applied"), waiting], DefaultOutcome = waiting.Id }, "send");
        var broken = s.Add(o => o with { DefaultOutcome = waiting.Id }, "remind");

        Assert.Empty(await s.FindingsAsync(clean.Id));
        var found = Assert.Single(await s.DiagnosticsAsync(), d => d.Rule == "MQ3028");
        Assert.Equal((broken.Id, "/defaultOutcome"), (found.ElementId, found.JsonPointer));
    }

    [Fact]
    public async Task MQ3030_a_read_operation_that_fires_an_event_warns()
    {
        var s = new Scene(47);
        var clean = s.Add(o => o with { Fires = [s.Sent] }, "send");
        var broken = s.Add(o => o with { Effect = OperationEffect.Read, Fires = [s.Sent] }, "preview");

        Assert.Empty(await s.FindingsAsync(clean.Id));
        var found = Assert.Single(await s.DiagnosticsAsync(), d => d.Rule == "MQ3030");
        Assert.Equal((broken.Id, "/fires/0", DiagnosticSeverity.Warning), (found.ElementId, found.JsonPointer, found.Severity));
        Assert.Contains("fires the event 'InvoiceDispatch.sent'", found.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MQ3031_requires_names_no_part_of_itself_no_diagram_and_no_migration_fact()
    {
        var s = new Scene(48);
        var database = s.B.Database("main", Dialect.PostgreSql);
        var diagram = new Diagram { Id = s.Id(), Name = "Overview" };
        var fact = new Migration { Id = s.Id(), Database = database.Id, Fact = MigrationFact.Raw };
        s.B.Add(diagram).Add(fact);
        var clean = s.Add(o => o with { Requires = [s.Customer.Id, s.Customer.AttrId("id"), s.Sent] }, "send");
        var outcome = s.Outcome("applied");
        Operation? broken = null;
        broken = s.Add(o => o with { Outcomes = [outcome], Requires = [o.Id, outcome.Id, diagram.Id, fact.Id, s.Invoice.Id] }, "remind");

        Assert.Empty(await s.FindingsAsync(clean.Id));
        var found = (await s.DiagnosticsAsync()).Where(d => d.Rule == "MQ3031").ToList();
        Assert.Equal(["/requires/0", "/requires/1", "/requires/2", "/requires/3"], found.Select(d => d.JsonPointer));
        Assert.All(found, d => Assert.Equal(broken.Id, d.ElementId));
        Assert.Contains("the operation itself", found[0].Message, StringComparison.Ordinal);
        Assert.Contains("the diagram 'Overview'", found[2].Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Archive_on_two_subjects_is_clean_and_twice_on_one_subject_is_MQ3001()
    {
        var s = new Scene(49);
        s.Add(o => o, "archive");
        s.Add(o => o with { Subject = s.Customer.Id }, "archive");
        s.Add(o => o, "ping", subject: false);
        var billing = s.B.Package("Billing");
        s.Add(o => o with { Package = billing.Id }, "ping", subject: false);

        Assert.Empty(await s.DiagnosticsAsync());

        var twice = s.Add(o => o, "archive");
        var again = s.Add(o => o, "ping", subject: false);
        var found = (await s.DiagnosticsAsync()).Where(d => d.Rule == "MQ3001").ToList();
        Assert.Equal(2, found.Count);
        Assert.Contains(found, d => d.Message.Contains("among operations of the same subject", StringComparison.Ordinal));
        Assert.Contains(found, d => d.Message.Contains("among operations without a subject in the same package", StringComparison.Ordinal));
        Assert.All(found, d => Assert.Equal("/name", d.JsonPointer));
        Assert.NotNull(twice);
        Assert.NotNull(again);
    }

    [Fact]
    public async Task Sub_element_names_are_MQ3001_and_MQ3018()
    {
        var s = new Scene(50);
        var dupInput = s.Typed("recipient", "string");
        var badInput = s.Typed("2nd", "string");
        var dupOutcome = s.Outcome("applied");
        var dupReturns = s.Typed("trackingId", "ulid");
        OperationError badError = new() { Id = s.Id(), Name = "already-sent" };
        var operation = s.Add(o => o with
        {
            Input = [s.Typed("recipient", "string"), dupInput, badInput],
            Outcomes = [s.Outcome("applied", new OperationReturns { Members = [s.Typed("trackingId", "ulid"), dupReturns] }), dupOutcome],
            Errors = [badError],
        }, "send");

        Assert.Equal(
            [
                ("MQ3001", "/input/1/name", dupInput.Id),
                ("MQ3001", "/outcomes/0/returns/members/1/name", dupReturns.Id),
                ("MQ3001", "/outcomes/1/name", dupOutcome.Id),
                ("MQ3018", "/errors/0/name", badError.Id),
                ("MQ3018", "/input/2/name", badInput.Id),
            ],
            (await s.FindingsAsync(operation.Id)).Order());
    }

    [Fact]
    public async Task Member_facets_defaults_and_rule_ids_are_MQ3013_MQ3019_and_MQ2007()
    {
        var s = new Scene(51);
        var length = s.Typed("count", "int32", m => m with { Length = 4 });
        var keyword = s.Typed("weird", "money");
        var mismatch = s.Typed("total", "decimal", m => m with { Precision = 12, Scale = 2, Default = Json("\"lots\"") });
        var credential = s.Typed("apiKey", "string", m => m with { Default = Json("\"sk_live_0123456789abcdefghij\"") });
        var rules = s.Typed("note", "string", m => m with { Validation = new AttributeValidation { Rules = ["checked"] } });
        var operation = s.Add(o => o with { Input = [length, keyword, mismatch, credential, rules] }, "send");

        var found = await s.FindingsAsync(operation.Id);

        Assert.Contains(("MQ3013", "/input/0/length", length.Id), found);
        Assert.Contains(("MQ3013", "/input/1/type", keyword.Id), found);
        Assert.Contains(("MQ3019", "/input/2/default", mismatch.Id), found);
        Assert.Contains(("MQ3017", "/input/3/default", credential.Id), found);
        Assert.Contains(("MQ2007", "/input/4/validation/rules/0", rules.Id), found);
        var message = (await s.DiagnosticsAsync()).Single(d => d.Rule == "MQ3019").Message;
        Assert.StartsWith("The default of member 'total' does not fit decimal", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Stereotypes_on_sub_elements_are_MQ2003_and_MQ2004()
    {
        var s = new Scene(52);
        s.B.Stereotype("public-api").AppliesTo("operation", "operation-member", "outcome", "error");
        s.B.Stereotype("audited").AppliesTo("entity");
        var member = s.Typed("recipient", "string", m => m with { Stereotypes = ["public-api"] });
        var unknown = s.Typed("cc", "string", m => m with { Stereotypes = ["no-such-mark"] });
        var outcome = s.Outcome("applied") with { Stereotypes = ["audited"] };
        OperationError error = new() { Id = s.Id(), Name = "already_sent", Stereotypes = ["audited"] };
        var operation = s.Add(o => o with { Stereotypes = ["public-api"], Input = [member, unknown], Outcomes = [outcome], Errors = [error] }, "send");

        Assert.Equal(
            [
                ("MQ2003", "/input/1/stereotypes/0", unknown.Id),
                ("MQ2004", "/errors/0/stereotypes/0", error.Id),
                ("MQ2004", "/outcomes/0/stereotypes/0", outcome.Id),
            ],
            (await s.FindingsAsync(operation.Id)).Order());
    }

    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
