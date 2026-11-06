using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Expressions;
using Maquettiste.Engine.Model;
using static Maquettiste.Engine.Tests.Expressions.ExprScopes;

namespace Maquettiste.Engine.Tests.Expressions;

/// <summary>Types, nullability and the typing rules of the core (next-phase-3-design.md section 3.1, task H2).</summary>
public sealed class ExprTyperTests
{
    [Theory]
    [InlineData("weight > 1", "float")]
    [InlineData("price > 1", "value object")]
    [InlineData("date_part('hour', issuedOn) > 1", "no hour")]
    [InlineData("date_part('minute', issuedOn) > 1", "no minute")]
    [InlineData("issuedOn < createdAt", "date_of")]
    [InlineData("issuedOn = sentAt", "date_of")]
    [InlineData("issuedOn + 1 > dueOn", "add_days")]
    [InlineData("createdAt - 1 > createdAt", "add_days")]
    [InlineData("total > rate", "decimal with double")]
    [InlineData("total + rate > 1", "decimal with double")]
    [InlineData("status = PaymentTerm.NET30", "belongs to code")]
    [InlineData("code = InvoiceStatus.Paid", "its own enum")]
    [InlineData("status < InvoiceStatus.Paid", "no order")]
    [InlineData("flag < flag", "no order")]
    [InlineData("reference < reference", "no order")]
    [InlineData("payload = payload", "does not apply to json")]
    [InlineData("status = 'Paid'", "its own enum")]
    [InlineData("code || quantity = 'a'", "joins strings")]
    [InlineData("flag and quantity", "bool operands")]
    [InlineData("total + 1", "must be bool")]
    [InlineData("length(quantity) > 1", "takes string")]
    [InlineData("round(rate, 2) > 1", "takes decimal or int")]
    [InlineData("round(total, 19) > 1", "0 to 18")]
    [InlineData("add_days(issuedOn, total) > dueOn", "takes int")]
    [InlineData("date_of(issuedOn) = issuedOn", "takes datetime or datetimeoffset")]
    [InlineData("json_length(code) > 0", "takes json")]
    [InlineData("case when quantity then 1 end = 1", "condition is bool")]
    [InlineData("case when flag then 1 else 'a' end = 1", "Cannot choose between")]
    [InlineData("coalesce(code, quantity) = 'a'", "Cannot coalesce")]
    [InlineData("null + null > 1", "no operand with a type")]
    [InlineData("null < null", "compares nothing but null")]
    public void Types_that_do_not_fit_are_MQ3103(string text, string words)
    {
        var problems = Problems(text);
        var problem = Assert.Single(problems);
        Assert.Equal("MQ3103", problem.Rule);
        Assert.Contains(words, problem.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_derived_value_has_its_attributes_type()
    {
        Assert.Empty(Problems("total * 2", Invoice(ExprSiteKind.Value, ExprType.Decimal)));
        Assert.Empty(Problems("quantity * 2", Invoice(ExprSiteKind.Value, ExprType.Decimal))); // an int fits a decimal attribute
        Assert.Empty(Problems("quantity * 2", Invoice(ExprSiteKind.Value, ExprType.Int32)));
        Assert.Empty(Problems("code || '-' || trim(code)", Invoice(ExprSiteKind.Value, ExprType.String)));
        Assert.Contains("must be int32", Assert.Single(Problems("total * 2", Invoice(ExprSiteKind.Value, ExprType.Int32))).Message, StringComparison.Ordinal);
        Assert.Equal("MQ3103", Assert.Single(Problems("quantity > 1", Invoice(ExprSiteKind.Value, ExprType.Decimal))).Rule);
    }

    [Fact]
    public void A_datetime_meets_now_as_utc_at_a_guard_only()
    {
        var guard = Typed("createdAt <= now()", Invoice(ExprSiteKind.Guard));
        Assert.Equal(ExprType.DateTimeOffset, guard.Args[1].Type);
        Assert.False(guard.Nullable);
        Assert.Empty(Problems("sentAt > createdAt", Invoice()));

        foreach (var site in new[] { ExprSiteKind.Check, ExprSiteKind.Value, ExprSiteKind.RuleCondition })
        {
            var problem = Assert.Single(Problems("createdAt <= now()", Invoice(site, ExprType.Bool)));
            Assert.Equal(("MQ3104", "/checks/0/condition/args/1"), (problem.Rule, problem.JsonPointer));

            // The text is the one these sites gave before sets could read now() (object-effects-design.md section 11).
            Assert.Equal("now() reads the clock; only a guard may read it.", problem.Message);
        }
    }

    [Fact]
    public void Integers_keep_their_width()
    {
        var scope = Invoice(ExprSiteKind.Value, ExprType.Int64);
        Assert.Equal(ExprType.Int32, Typed("1", scope).Type);
        Assert.Equal(ExprType.Int32, Typed("-2147483648", scope).Type);
        Assert.Equal(ExprType.Int64, Typed("2147483648", scope).Type);
        Assert.Equal(ExprType.Int16, Typed("small", scope).Type);
        Assert.Equal(ExprType.Int64, Typed("big", scope).Type);
        Assert.Equal(ExprType.Int64, Typed("small + 1", scope).Type); // arithmetic is 64, as PostgreSQL computes it in numeric
        Assert.Equal(ExprType.Int64, Typed("-quantity", scope).Type);
        Assert.Equal(ExprType.Int32, Typed("length(code)", scope).Type);
        Assert.Equal(ExprType.Int32, Typed("date_part('year', issuedOn)", scope).Type);
        Assert.Equal(ExprType.Int32, Typed("nonnull_count(dueOn, terms)", scope).Type);
        Assert.Equal(ExprType.Int32, Typed("json_length(payload)", scope).Type);
        Assert.Equal(ExprType.Int32, Typed("coalesce(small, quantity)", scope).Type);

        var decimals = Invoice(ExprSiteKind.Value, ExprType.Decimal);
        Assert.Equal(ExprType.Decimal, Typed("quantity / 2", decimals).Type);
        Assert.Equal(ExprType.Decimal, Typed("quantity + total", decimals).Type);
        Assert.Equal(ExprType.Decimal, Typed("round(quantity, 0)", decimals).Type);
        Assert.Equal(ExprType.Double, Typed("rate / 2", Invoice(ExprSiteKind.Value, ExprType.Double)).Type);
        Assert.Equal(ExprType.Double, Typed("rate + quantity", Invoice(ExprSiteKind.Value, ExprType.Double)).Type);
    }

    [Fact]
    public void Nullability_follows_optional_members_and_navigations()
    {
        var guard = Invoice(ExprSiteKind.Guard);
        Assert.True(Typed("issuedOn <= dueOn", guard).Nullable);
        Assert.False(Typed("issuedOn <= issuedOn", guard).Nullable);
        Assert.True(Typed("customer.creditLimit > 0", guard).Nullable); // the customer is optional, the limit is not
        var through = Typed("customer.region.country.code = 'FR'", guard).Args[0];
        Assert.Equal(["customer", "region", "country"], through.Through.Select(n => n.Name));
        Assert.Equal(CountryCode, through.Member!.Id);
        Assert.True(through.Nullable);
        Assert.False(Typed("dueOn is null", guard).Nullable);
        Assert.False(Typed("dueOn is distinct from issuedOn", guard).Nullable);
        Assert.False(Typed("coalesce(dueOn, issuedOn) > issuedOn", guard).Nullable);
        Assert.True(Typed("coalesce(dueOn, dueOn) > issuedOn", guard).Nullable);
        Assert.False(Typed("nonnull_count(dueOn) = 0", guard).Nullable);
        Assert.True(Typed("json_length(payload) = 0", guard).Nullable);
        Assert.True(Typed("quantity / 2 > 0", guard).Nullable); // division by zero is null
        Assert.True(Typed("case when flag then 1 end = 1", guard).Nullable);
        Assert.False(Typed("case when issuedOn = issuedOn then 1 else 2 end = 1", guard).Nullable);
        Assert.False(Typed("sentAt is null and now() > createdAt", guard).Nullable);
        Assert.True(Typed("satisfies PositiveAmount(amount: customer.creditLimit)", guard).Nullable);
        Assert.False(Typed("satisfies PositiveAmount(amount: total)", guard).Nullable);
    }

    [Fact]
    public async Task A_container_filled_on_first_read_fills_once_and_a_reader_meanwhile_waits_for_it()
    {
        // Threads that print one resolved condition's text read its scope at once: the one that comes while another fills it must see
        // the whole fill, not an empty container.
        var ct = TestContext.Current.CancellationToken;
        var filling = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var fills = 0;
        var container = new ExprContainer(c =>
        {
            Interlocked.Increment(ref fills);
            filling.SetResult();
            release.Wait(ct);
            c.Add(new ExprMember(CreditLimit, "creditLimit", ExprType.Decimal, false));
        });

        var first = Task.Run(() => container.Member(CreditLimit), ct);
        await filling.Task.WaitAsync(ct);
        var second = Task.Run(() => container.Member(CreditLimit), ct);
        await Task.WhenAny(second, Task.Delay(TimeSpan.FromMilliseconds(200), ct));
        Assert.False(second.IsCompleted, "A read during the fill answered before the fill ended.");
        release.Set();

        Assert.Equal(CreditLimit, (await first)?.Id);
        Assert.Equal(CreditLimit, (await second)?.Id);
        Assert.Equal(1, fills);
        Assert.Single(container.Members);
    }

    [Theory]
    [InlineData("customer.creditLimit > 0", "only a guard reads through a relation")]
    [InlineData("lines.amount > 0", "leads to many records")]
    [InlineData("notes = 'a'", "is a collection")]
    public void Paths_a_check_cannot_read_are_MQ3102(string text, string words)
    {
        var scope = Invoice(text.StartsWith("lines", StringComparison.Ordinal) ? ExprSiteKind.Guard : ExprSiteKind.Check);
        var problem = Assert.Single(Problems(text, scope));
        Assert.Equal("MQ3102", problem.Rule);
        Assert.Contains(words, problem.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Paths_and_members_are_read_by_id()
    {
        Assert.Equal("MQ3102", Single("""{ "path": ["01JEXT00000000000000000077"] }""").Rule);
        Assert.Equal("MQ3102", Single("""{ "op": "gt", "args": [{ "path": ["{{Customer}}"] }, { "value": 1 }] }""", Invoice(ExprSiteKind.Guard)).Rule);
        Assert.Equal("MQ3102", Single("""{ "path": ["{{Total}}", "{{Quantity}}"] }""").Rule);
        Assert.Equal("MQ3102", Single("""{ "op": "eq", "args": [{ "ref": "01JEXT00000000000000000077" }, { "path": ["{{Status}}"] }] }""").Rule);
        Assert.Equal("MQ3102", Single("""{ "op": "gt", "args": [{ "member": "properties.retentionDays" }, { "value": 1 }] }""").Rule);

        var rule = new ExprScope(ExprSiteKind.RuleCondition)
        {
            MemberPaths = p => p == "properties.retentionDays" ? new ExprMember(p, p, ExprType.Int32, true) : null,
        };
        var typed = ExprTyper.Type(Tree("""{ "op": "ge", "args": [{ "member": "properties.retentionDays" }, { "value": 30 }] }"""), rule, new List<Diagnostic>())!;
        Assert.True(typed.Nullable);
        Assert.Equal("MQ3102", Single("""{ "op": "ge", "args": [{ "member": "properties.other" }, { "value": 30 }] }""", rule).Rule);
    }

    [Theory]
    [InlineData("""{ "op": "plus", "args": [{ "value": 1 }, { "value": 2 }] }""", "not an operator")]
    [InlineData("""{ "op": "eq", "args": [{ "value": 1 }] }""", "takes 2 operands")]
    [InlineData("""{ "call": "upper", "args": [{ "value": "a" }] }""", "not a function")]
    [InlineData("""{ "call": "trim", "args": [] }""", "takes 1 arguments")]
    [InlineData("""{ "value": 1.5 }""", "an integer of at most 64 bits")]
    [InlineData("""{ "value": "2026-02-30", "type": "date" }""", "not a date literal")]
    [InlineData("""{ "value": 7, "type": "decimal" }""", "written as text")]
    [InlineData("""{ "value": 1, "null": true }""", "The node holds more than one of value, null, path, ref, member, now, op, call, case, satisfies and text.")]
    [InlineData("""{ "now": true, "currentActor": true }""", "The node holds more than one of value, null, path, ref, member, now, currentActor, inState, op, call, case, satisfies and text.")]
    [InlineData("""{ }""", "empty")]
    [InlineData("""{ "path": [] }""", "at least one id")]
    [InlineData("""{ "case": [] }""", "at least one branch")]
    [InlineData("""{ "null": true, "args": [] }""", "'args' goes with")]
    public void Malformed_nodes_are_MQ3101(string json, string words)
    {
        var problem = Single(json);
        Assert.Equal("MQ3101", problem.Rule);
        Assert.Contains(words, problem.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("quantity in (1, quantity)", "/checks/0/condition/args/2")]
    [InlineData("quantity in (1, null)", "/checks/0/condition/args/2")]
    [InlineData("quantity not in (1 + 1)", "/checks/0/condition/args/1")]
    [InlineData("matches(code, code)", "/checks/0/condition/args/1")]
    [InlineData("date_part(code, issuedOn) > 1", "/checks/0/condition/args/0/args/0")]
    [InlineData("round(total, quantity) > 1", "/checks/0/condition/args/0/args/1")]
    public void Arguments_that_must_be_literals_are_MQ3106(string text, string pointer)
    {
        var problem = Assert.Single(Problems(text));
        Assert.Equal(("MQ3106", pointer), (problem.Rule, problem.JsonPointer));
    }

    [Fact]
    public void In_lists_of_literals_and_refs_type()
    {
        Assert.Empty(Problems("quantity in (1, -2, 3)"));
        Assert.Empty(Problems("status in (InvoiceStatus.Draft, InvoiceStatus.Paid)"));
        Assert.Empty(Problems("terms not in (PaymentTerm.NET30)"));
        Assert.Equal("MQ3103", Assert.Single(Problems("status in (PaymentTerm.NET30)")).Rule);
        Assert.Equal("MQ3103", Assert.Single(Problems("quantity in ('a')")).Rule);
    }

    [Fact]
    public void Patterns_outside_the_portable_subset_are_MQ3105()
    {
        Assert.Empty(Problems("matches(code, '^[A-Z]{3}$')"));
        var problem = Assert.Single(Problems("matches(code, '^\\d{3}$')"));
        Assert.Equal(("MQ3105", "/checks/0/condition/args/1"), (problem.Rule, problem.JsonPointer));
        Assert.Contains("[0-9]", problem.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Constraints_bind_every_parameter_without_a_cycle()
    {
        var typed = Typed("satisfies PositiveAmount(amount: total)", Invoice());
        Assert.Equal(Positive, typed.Constraint!.Id);
        Assert.Equal(Total, Assert.Single(typed.Args).Member!.Id);
        Assert.Equal(Amount, typed.Body!.Args[0].Member!.Id);
        Assert.Empty(Problems("satisfies PositiveAmount(amount: quantity)")); // an int fits a decimal parameter

        Assert.Equal("MQ3109", Single("""{ "satisfies": "{{Positive}}", "with": [] }""").Rule);
        Assert.Equal("MQ3109", Single("""{ "satisfies": "01JEXT00000000000000000077", "with": [] }""").Rule);
        Assert.Equal("MQ3109", Single("""{ "satisfies": "{{Positive}}", "with": [{ "parameter": "{{Amount}}", "value": { "path": ["{{Code}}"] } }] }""").Rule);
        Assert.Equal("MQ3109", Single("""{ "satisfies": "{{Positive}}", "with": [{ "parameter": "{{Amount}}", "value": { "value": 1 } }, { "parameter": "{{Amount}}", "value": { "value": 2 } }] }""").Rule);
        Assert.Equal("MQ3109", Single("""{ "satisfies": "{{Positive}}", "with": [{ "parameter": "{{Total}}", "value": { "value": 1 } }] }""").Rule);

        var cycle = Assert.Single(Problems("satisfies LoopA(value: quantity)"));
        Assert.Equal("MQ3109", cycle.Rule);
        Assert.Contains("cycle", cycle.Message, StringComparison.Ordinal);

        // A constraint is typed against its parameters; one that cites itself is a cycle there too.
        var scope = Invoice();
        var loopA = new ExprConstraint(LoopA, "LoopA", [new ExprMember(ValueA, "value", ExprType.Int32, false)],
            Tree("""{ "satisfies": "{{LoopB}}", "with": [{ "parameter": "{{ValueB}}", "value": { "path": ["{{ValueA}}"] } }] }"""));
        var own = new List<Diagnostic>();
        Assert.Null(ExprTyper.Type(loopA.Condition, scope.ForConstraint(loopA), own));
        Assert.Equal(("MQ3109", LoopA), (Assert.Single(own).Rule, own[0].ElementId));
        var positive = new ExprConstraint(Positive, "PositiveAmount", [new ExprMember(Amount, "amount", ExprType.Decimal, false)],
            Tree("""{ "op": "gt", "args": [{ "path": ["{{Amount}}"] }, { "path": ["{{Total}}"] }] }"""));
        var outside = new List<Diagnostic>();
        Assert.Null(ExprTyper.Type(positive.Condition, scope.ForConstraint(positive), outside));
        Assert.Equal(("MQ3102", "/condition/args/1"), (Assert.Single(outside).Rule, outside[0].JsonPointer)); // its own parameters only
    }

    [Fact]
    public void A_satisfies_whose_constraint_does_not_type_is_MQ3109()
    {
        // The constraint's own validation reports what is wrong inside it; the satisfies says only that it cannot be cited.
        var scope = Invoice();
        scope.AddConstraint(new ExprConstraint(Id(50), "Broken", [new ExprMember(Id(51), "amount", ExprType.Decimal, false)],
            Tree($$"""{ "op": "gt", "args": [{ "path": ["{{Id(51)}}"] }, { "value": "x" }] }""")));
        scope.AddConstraint(new ExprConstraint(Id(52), "NotBool", [new ExprMember(Id(53), "amount", ExprType.Decimal, false)],
            Tree($$"""{ "op": "add", "args": [{ "path": ["{{Id(53)}}"] }, { "value": 1 }] }""")));
        var problem = Single($$"""{ "satisfies": "{{Id(50)}}", "with": [{ "parameter": "{{Id(51)}}", "value": { "path": ["{{Total}}"] } }] }""", scope);
        Assert.Equal(("MQ3109", "/checks/0/condition"), (problem.Rule, problem.JsonPointer));
        Assert.Contains("'Broken' does not type", problem.Message, StringComparison.Ordinal);
        problem = Single($$"""{ "satisfies": "{{Id(52)}}", "with": [{ "parameter": "{{Id(53)}}", "value": { "path": ["{{Total}}"] } }] }""", scope);
        Assert.Equal(("MQ3109", "/checks/0/condition"), (problem.Rule, problem.JsonPointer));
        Assert.Contains("'NotBool' does not type", problem.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_constraint_that_reads_now_is_cited_at_guards_only()
    {
        Assert.Empty(Problems("satisfies Recent(at: sentAt)", Invoice(ExprSiteKind.Guard)));
        var problem = Assert.Single(Problems("satisfies Recent(at: sentAt)"));
        Assert.Equal(("MQ3104", "/checks/0/condition"), (problem.Rule, problem.JsonPointer));
        var recent = new ExprConstraint(Recent, "Recent", [new ExprMember(At, "at", ExprType.DateTimeOffset, false)],
            Tree("""{ "op": "ge", "args": [{ "path": ["{{At}}"] }, { "now": true }] }"""));
        Assert.NotNull(ExprTyper.Type(recent.Condition, Invoice().ForConstraint(recent), new List<Diagnostic>()));
    }

    [Fact]
    public void Size_is_bounded()
    {
        // One 'and' of 512 operands holds 513 nodes; 64 negations of a path nest 65 deep, and so does a chain of 64 additions.
        Assert.Equal("MQ3113", Assert.Single(Problems(string.Join(" and ", Enumerable.Repeat("flag", 512)))).Rule);
        Assert.Empty(Problems(string.Join(" and ", Enumerable.Repeat("flag", 511))));
        Assert.Equal("MQ3113", Assert.Single(Problems(string.Join(" + ", Enumerable.Repeat("quantity", 65)) + " > 0")).Rule);
        Assert.Empty(Problems(string.Join(" + ", Enumerable.Repeat("quantity", 63)) + " > 0"));
        var deep = string.Concat(Enumerable.Repeat("not ", 64)) + "flag";
        Assert.Equal("MQ3113", Assert.Single(Problems(deep)).Rule);
        Assert.Empty(Problems(string.Concat(Enumerable.Repeat("not ", 63)) + "flag"));
    }

    [Fact]
    public void Text_in_a_file_is_typed_and_warned()
    {
        var problems = new List<Diagnostic>();
        var typed = ExprTyper.Type(Tree("""{ "op": "and", "args": [{ "text": "issuedOn <= dueOn" }, { "path": ["{{Flag}}"] }] }"""), Invoice(), problems);
        Assert.NotNull(typed);
        var warning = Assert.Single(problems);
        Assert.Equal(("MQ3108", DiagnosticSeverity.Warning, "/checks/0/condition/args/0"), (warning.Rule, warning.Severity, warning.JsonPointer));
        Assert.Equal("le", typed.Args[0].Node.Op);
        Assert.Equal("/checks/0/condition/args/0", typed.Args[0].Args[1].Pointer);

        problems.Clear();
        Assert.Null(ExprTyper.Type(Tree("""{ "text": "issuedOn <= " }"""), Invoice(), problems));
        Assert.Equal(["MQ3108", "MQ3101"], problems.Select(p => p.Rule));
        Assert.All(problems, p => Assert.Equal("/checks/0/condition", p.JsonPointer));
    }

    [Fact]
    public void Pointers_name_the_node()
    {
        var problem = Single("""{ "op": "and", "args": [{ "path": ["{{Flag}}"] }, { "case": [{ "when": { "path": ["{{Flag}}"] }, "then": { "path": ["{{Weight}}"] } }] }] }""");
        Assert.Equal(("MQ3103", "/checks/0/condition/args/1/case/0/then"), (problem.Rule, problem.JsonPointer));
        Assert.Equal((Id(99), ".maquettiste/model/entities/invoice.json"), (problem.ElementId, problem.FilePath));
    }

    private static TypedExpr Typed(string text, ExprScope scope)
    {
        var problems = new List<Diagnostic>();
        var typed = ExprTyper.Type(Parse(text, scope), scope, problems);
        Assert.True(typed is not null, text + ": " + string.Join("; ", problems.Select(p => p.Rule + " " + p.Message)));
        Assert.Empty(problems);
        return typed!;
    }

    private static List<Diagnostic> Problems(string text, ExprScope? scope = null)
    {
        scope ??= Invoice();
        var problems = new List<Diagnostic>();
        var typed = ExprTyper.Type(Parse(text, scope), scope, problems);
        Assert.Equal(typed is null, problems.Any(p => p.Severity == DiagnosticSeverity.Error));
        return problems;
    }

    private static Diagnostic Single(string json, ExprScope? scope = null)
    {
        var problems = new List<Diagnostic>();
        Assert.Null(ExprTyper.Type(Tree(json), scope ?? Invoice(), problems));
        return Assert.Single(problems);
    }

    private static Expr Tree(string json) => ExprScopes.Parse(json);

    private static Expr Parse(string text, ExprScope scope)
    {
        var parse = ExprText.Parse(text, scope);
        Assert.True(parse.Succeeded, text + ": " + parse.Problem?.Message);
        return parse.Tree!;
    }
}
