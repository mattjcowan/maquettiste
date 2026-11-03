using Maquettiste.Engine.Expressions;
using static Maquettiste.Engine.Tests.Expressions.ExprRuntimeTests;
using static Maquettiste.Engine.Tests.Expressions.ExprScopes;

namespace Maquettiste.Engine.Tests.Expressions;

/// <summary>The fixed JavaScript each operator and function lowers to (next-phase-3-design.md section 3.1, task H3).</summary>
public sealed class ExprJsLoweringTests
{
    private static string Js(string text, ExprSiteKind site = ExprSiteKind.Check, ExprColumns? columns = null) =>
        ExprLowering.ToJavaScript(Typed(text, site), site, columns ?? Columns());

    [Theory]
    [InlineData("issuedOn <= dueOn", "mq.le(mq.date(mq.at(row, \"issuedOn\")), mq.date(mq.at(row, \"dueOn\")))")]
    [InlineData("code <> 'a'", "mq.ne(mq.str(mq.at(row, \"code\")), \"a\")")]
    [InlineData("quantity between 1 and 9", "mq.between(mq.dec(mq.at(row, \"quantity\")), mq.dec(\"1\"), mq.dec(\"9\"))")]
    [InlineData("quantity in (1, 2)", "mq.in(mq.dec(mq.at(row, \"quantity\")), [mq.dec(\"1\"), mq.dec(\"2\")])")]
    [InlineData("quantity not in (1)", "mq.notIn(mq.dec(mq.at(row, \"quantity\")), [mq.dec(\"1\")])")]
    [InlineData("dueOn is null", "mq.isNull(mq.date(mq.at(row, \"dueOn\")))")]
    [InlineData("dueOn is not null", "mq.isNotNull(mq.date(mq.at(row, \"dueOn\")))")]
    [InlineData("dueOn is distinct from issuedOn", "mq.distinct(mq.date(mq.at(row, \"dueOn\")), mq.date(mq.at(row, \"issuedOn\")))")]
    [InlineData("flag and not flag or flag", "mq.or(mq.and(mq.bool(mq.at(row, \"flag\")), mq.not(mq.bool(mq.at(row, \"flag\")))), mq.bool(mq.at(row, \"flag\")))")]
    [InlineData("flag implies flag", "mq.implies(mq.bool(mq.at(row, \"flag\")), mq.bool(mq.at(row, \"flag\")))")]
    [InlineData("total * 2 - 1 > -total / 3", "mq.gt(mq.sub(mq.mul(mq.dec(mq.at(row, \"total\"), 2), mq.dec(\"2\")), mq.dec(\"1\")), mq.div(mq.neg(mq.dec(mq.at(row, \"total\"), 2)), mq.dec(\"3\")))")]
    [InlineData("rate > quantity", "mq.gt(mq.dbl(mq.at(row, \"rate\")), mq.toDouble(mq.dec(mq.at(row, \"quantity\"))))")]
    [InlineData("code || 'x' = 'ax'", "mq.eq(mq.concat(mq.str(mq.at(row, \"code\")), \"x\"), \"ax\")")]
    [InlineData("coalesce(dueOn, issuedOn) = date '2026-01-31'", "mq.eq(mq.coalesce(mq.date(mq.at(row, \"dueOn\")), mq.date(mq.at(row, \"issuedOn\"))), mq.date(\"2026-01-31\"))")]
    [InlineData("case when flag then 1 else 2 end = 1", "mq.eq(mq.case(mq.bool(mq.at(row, \"flag\")), mq.dec(\"1\"), mq.dec(\"2\")), mq.dec(\"1\"))")]
    [InlineData("case when flag then 1 end = 1", "mq.eq(mq.case(mq.bool(mq.at(row, \"flag\")), mq.dec(\"1\"), null), mq.dec(\"1\"))")]
    [InlineData("price.amount > 0", "mq.gt(mq.dec(mq.at(row, \"price\", \"amount\")), mq.dec(\"0\"))")]
    [InlineData("padded = 'A'", "mq.eq(mq.fixed(mq.at(row, \"padded\")), \"A\")")]
    [InlineData("opens < time '09:30'", "mq.lt(mq.time(mq.at(row, \"opens\")), mq.time(\"09:30\"))")]
    [InlineData("createdAt < sentAt", "mq.lt(mq.ts(mq.at(row, \"createdAt\")), mq.tsz(mq.at(row, \"sentAt\")))")]
    [InlineData("reference = uuid '0190a5c4-0000-7000-8000-000000000001'", "mq.eq(mq.uuid(mq.at(row, \"reference\")), mq.uuid(\"0190a5c4-0000-7000-8000-000000000001\"))")]
    [InlineData("rate < double '1.5'", "mq.lt(mq.dbl(mq.at(row, \"rate\")), mq.dbl(\"1.5\"))")]
    public void Each_operator_lowers_to_a_runtime_call(string text, string body) =>
        Assert.Equal("(row) => mq.passes(" + body + ")", Js(text));

    [Theory]
    [InlineData("trim(code) = ''", "mq.eq(mq.trim(mq.str(mq.at(row, \"code\"))), \"\")")]
    [InlineData("length(code) > 1", "mq.gt(mq.length(mq.str(mq.at(row, \"code\"))), mq.dec(\"1\"))")]
    [InlineData("matches(code, '^[A-Z]{2}$')", "mq.matches(mq.str(mq.at(row, \"code\")), \"^[A-Z]{2}$\")")]
    [InlineData("nonnull_count(dueOn, flag) = 1", "mq.eq(mq.nonnullCount(mq.date(mq.at(row, \"dueOn\")), mq.bool(mq.at(row, \"flag\"))), mq.dec(\"1\"))")]
    [InlineData("add_days(issuedOn, 30) >= dueOn", "mq.ge(mq.addDays(mq.date(mq.at(row, \"issuedOn\")), mq.dec(\"30\")), mq.date(mq.at(row, \"dueOn\")))")]
    [InlineData("add_months(issuedOn, 1) >= dueOn", "mq.ge(mq.addMonths(mq.date(mq.at(row, \"issuedOn\")), mq.dec(\"1\")), mq.date(mq.at(row, \"dueOn\")))")]
    [InlineData("date_part('isodow', issuedOn) < 6", "mq.lt(mq.datePart(\"isodow\", mq.date(mq.at(row, \"issuedOn\"))), mq.dec(\"6\"))")]
    [InlineData("date_of(sentAt) = issuedOn", "mq.eq(mq.dateOf(mq.tsz(mq.at(row, \"sentAt\"))), mq.date(mq.at(row, \"issuedOn\")))")]
    [InlineData("round(total, 1) > 0", "mq.gt(mq.round(mq.dec(mq.at(row, \"total\"), 2), 1), mq.dec(\"0\"))")]
    [InlineData("json_type(payload) = 'array'", "mq.eq(mq.jsonType(mq.json(mq.at(row, \"payload\"))), \"array\")")]
    [InlineData("json_length(payload) > 0", "mq.gt(mq.jsonLength(mq.json(mq.at(row, \"payload\"))), mq.dec(\"0\"))")]
    public void Each_function_lowers_to_a_runtime_call(string text, string body) =>
        Assert.Equal("(row) => mq.passes(" + body + ")", Js(text));

    [Fact]
    public void Sites_wrap_the_result()
    {
        Assert.Equal("(row) => mq.holds(mq.le(mq.date(mq.at(row, \"issuedOn\")), mq.date(mq.at(row, \"dueOn\"))))", Js("issuedOn <= dueOn", ExprSiteKind.Guard));
        Assert.Equal("(row) => mq.out(mq.add(mq.dec(mq.at(row, \"total\"), 2), mq.dec(\"1\")), \"decimal\")", Js("total + 1", ExprSiteKind.Value));
        Assert.Equal("(row) => mq.holds(mq.ge(mq.tsz(mq.at(row, \"sentAt\")), mq.now()))", Js("sentAt >= now()", ExprSiteKind.Guard));
    }

    [Fact]
    public void A_guard_reads_through_a_navigation() =>
        Assert.Equal("(row) => mq.holds(mq.gt(mq.dec(mq.at(row, \"customer\", \"creditLimit\")), mq.dec(\"0\")))", Js("customer.creditLimit > 0", ExprSiteKind.Guard));

    [Fact]
    public void A_constraint_inlines_with_its_arguments() =>
        Assert.Equal("(row) => mq.passes(mq.gt(mq.dec(mq.at(row, \"total\"), 2), mq.dec(\"0\")))", Js("satisfies PositiveAmount(amount: total)"));

    [Fact]
    public void An_enum_member_lowers_to_the_api_form_or_the_stored_form_at_a_table()
    {
        Assert.Equal("(row) => mq.passes(mq.eq(mq.str(mq.at(row, \"status\")), \"PAID\"))", Js("status = InvoiceStatus.Paid"));
        var integer = new ExprColumns { Physical = true, Ref = Columns().Ref, Column = t => new ExprColumn("status") { Form = ExprStoredForm.Integer } };
        Assert.Equal("(row) => mq.passes(mq.in(mq.dec(mq.at(row, \"status\")), [mq.dec(\"2\"), mq.dec(\"1\")]))",
            Js("status in (InvoiceStatus.Paid, InvoiceStatus.Draft)", columns: integer));
        var text = new ExprColumns { Physical = true, Ref = Columns().Ref, Column = t => new ExprColumn("status_code") { Form = ExprStoredForm.Text } };
        Assert.Equal("(row) => mq.passes(mq.eq(mq.str(mq.at(row, \"status_code\")), \"PAID\"))", Js("status = InvoiceStatus.Paid", columns: text));
    }

    [Fact]
    public void Lowering_is_byte_identical_on_every_run() =>
        Assert.Equal(Js("coalesce(dueOn, issuedOn) <= add_months(issuedOn, 1) and code in ('a', 'b')"),
            Js("coalesce(dueOn, issuedOn) <= add_months(issuedOn, 1) and code in ('a', 'b')"));

    [Fact]
    public void Every_runtime_call_the_tables_name_exists()
    {
        var names = ExprFunctions.Operators.Values.Select(o => o.JavaScript).Concat(ExprFunctions.Functions.Values.Select(f => f.JavaScript))
            .Concat(["mq.case", "mq.passes", "mq.holds", "mq.out", "mq.at", "mq.now", "mq.toDouble"]);
        Assert.Equal(true, Run("(row) => [" + string.Join(", ", names.Select(n => "typeof " + n)) + "].every((t) => t === \"function\")"));
    }
}
