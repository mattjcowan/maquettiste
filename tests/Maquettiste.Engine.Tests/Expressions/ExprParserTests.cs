using Maquettiste.Engine.Expressions;
using static Maquettiste.Engine.Tests.Expressions.ExprScopes;

namespace Maquettiste.Engine.Tests.Expressions;

/// <summary>The text form read into the tree of ids (next-phase-3-design.md section 3.1, task H2).</summary>
public sealed class ExprParserTests
{
    [Theory]
    [InlineData("12", """{"value":12}""")]
    [InlineData("3000000000", """{"value":3000000000}""")]
    [InlineData("-5", """{"value":-5}""")]
    [InlineData("12.50", """{"value":"12.50","type":"decimal"}""")]
    [InlineData("-0.5", """{"value":"-0.5","type":"decimal"}""")]
    [InlineData("'it''s'", """{"value":"it's"}""")]
    [InlineData("true", """{"value":true}""")]
    [InlineData("FALSE", """{"value":false}""")]
    [InlineData("null", """{"null":true}""")]
    [InlineData("date '2026-01-31'", """{"value":"2026-01-31","type":"date"}""")]
    [InlineData("time '09:30'", """{"value":"09:30","type":"time"}""")]
    [InlineData("datetime '2026-01-31T09:30'", """{"value":"2026-01-31T09:30","type":"datetime"}""")]
    [InlineData("datetimeoffset '2026-01-31T09:30Z'", """{"value":"2026-01-31T09:30Z","type":"datetimeoffset"}""")]
    [InlineData("uuid '0b5c3f4e-8a7d-4c1b-9e2f-1a2b3c4d5e6f'", """{"value":"0b5c3f4e-8a7d-4c1b-9e2f-1a2b3c4d5e6f","type":"uuid"}""")]
    [InlineData("double '1.5'", """{"value":"1.5","type":"double"}""")]
    [InlineData("decimal '7'", """{"value":"7","type":"decimal"}""")]
    [InlineData("now()", """{"now":true}""")]
    public void Literals(string text, string expected) => Assert.Equal(expected, Show(Ok(text)));

    [Theory]
    [InlineData("total = 1", "eq")]
    [InlineData("total <> 1", "ne")]
    [InlineData("total != 1", "ne")]
    [InlineData("total < 1", "lt")]
    [InlineData("total <= 1", "le")]
    [InlineData("total > 1", "gt")]
    [InlineData("total >= 1", "ge")]
    [InlineData("total + 1", "add")]
    [InlineData("total - 1", "sub")]
    [InlineData("total * 1", "mul")]
    [InlineData("total / 1", "div")]
    [InlineData("code || 1", "concat")]
    [InlineData("total is distinct from 1", "distinct")]
    [InlineData("total is not distinct from 1", "notDistinct")]
    [InlineData("flag and 1", "and")]
    [InlineData("flag or 1", "or")]
    [InlineData("flag implies 1", "implies")]
    public void Binary_operators(string text, string op) =>
        Assert.Equal("{\"op\":\"" + op + "\",\"args\":[{\"path\":[\"" + (text.StartsWith("code", StringComparison.Ordinal) ? "Code" : text.StartsWith("flag", StringComparison.Ordinal) ? "Flag" : "Total") + "\"]},{\"value\":1}]}", Show(Ok(text)));

    [Fact]
    public void Other_operators_and_functions()
    {
        Assert.Equal("""{"op":"not","args":[{"path":["Flag"]}]}""", Show(Ok("not flag")));
        Assert.Equal("""{"op":"neg","args":[{"path":["Total"]}]}""", Show(Ok("-total")));
        Assert.Equal("""{"op":"isNull","args":[{"path":["DueOn"]}]}""", Show(Ok("dueOn is null")));
        Assert.Equal("""{"op":"isNotNull","args":[{"path":["DueOn"]}]}""", Show(Ok("dueOn IS NOT NULL")));
        Assert.Equal("""{"op":"between","args":[{"path":["Quantity"]},{"value":1},{"value":9}]}""", Show(Ok("quantity between 1 and 9")));
        Assert.Equal("""{"op":"in","args":[{"path":["Status"]},{"ref":"Draft"},{"ref":"Paid"}]}""", Show(Ok("status in (InvoiceStatus.Draft, InvoiceStatus.Paid)")));
        Assert.Equal("""{"op":"notIn","args":[{"path":["Quantity"]},{"value":1},{"value":-2}]}""", Show(Ok("quantity not in (1, -2)")));
        Assert.Equal("""{"op":"coalesce","args":[{"path":["DueOn"]},{"path":["IssuedOn"]}]}""", Show(Ok("coalesce(dueOn, issuedOn)")));
        Assert.Equal("""{"call":"length","args":[{"path":["Code"]}]}""", Show(Ok("LENGTH(code)")));
        Assert.Equal("""{"call":"date_part","args":[{"value":"year"},{"path":["IssuedOn"]}]}""", Show(Ok("date_part('year', issuedOn)")));
        Assert.Equal("""{"case":[{"when":{"path":["Flag"]},"then":{"value":1}}],"else":{"value":2}}""", Show(Ok("case when flag then 1 else 2 end")));
        Assert.Equal("""{"case":[{"when":{"path":["Flag"]},"then":{"value":1}},{"when":{"value":true},"then":{"value":3}}]}""",
            Show(Ok("case when flag then 1 when true then 3 end")));
        Assert.Equal("""{"satisfies":"Positive","with":[{"parameter":"Amount","value":{"path":["Total"]}}]}""", Show(Ok("satisfies PositiveAmount(amount: total)")));
    }

    [Theory]
    [InlineData("flag or flag and flag", """{"op":"or","args":[{"path":["Flag"]},{"op":"and","args":[{"path":["Flag"]},{"path":["Flag"]}]}]}""")]
    [InlineData("flag and flag and flag", """{"op":"and","args":[{"path":["Flag"]},{"path":["Flag"]},{"path":["Flag"]}]}""")]
    [InlineData("flag implies flag implies flag", """{"op":"implies","args":[{"path":["Flag"]},{"op":"implies","args":[{"path":["Flag"]},{"path":["Flag"]}]}]}""")]
    [InlineData("not total = 1", """{"op":"not","args":[{"op":"eq","args":[{"path":["Total"]},{"value":1}]}]}""")]
    [InlineData("not flag and flag", """{"op":"and","args":[{"op":"not","args":[{"path":["Flag"]}]},{"path":["Flag"]}]}""")]
    [InlineData("code || code = code", """{"op":"eq","args":[{"op":"concat","args":[{"path":["Code"]},{"path":["Code"]}]},{"path":["Code"]}]}""")]
    [InlineData("total between 1 and 2 and flag", """{"op":"and","args":[{"op":"between","args":[{"path":["Total"]},{"value":1},{"value":2}]},{"path":["Flag"]}]}""")]
    [InlineData("1 + 2 * 3", """{"op":"add","args":[{"value":1},{"op":"mul","args":[{"value":2},{"value":3}]}]}""")]
    [InlineData("1 - 2 - 3", """{"op":"sub","args":[{"op":"sub","args":[{"value":1},{"value":2}]},{"value":3}]}""")]
    [InlineData("(1 + 2) * 3", """{"op":"mul","args":[{"op":"add","args":[{"value":1},{"value":2}]},{"value":3}]}""")]
    [InlineData("-total * 2", """{"op":"mul","args":[{"op":"neg","args":[{"path":["Total"]}]},{"value":2}]}""")]
    [InlineData("code || 'a' + 1", """{"op":"concat","args":[{"path":["Code"]},{"op":"add","args":[{"value":"a"},{"value":1}]}]}""")]
    public void Precedence_levels(string text, string expected) => Assert.Equal(expected, Show(Ok(text)));

    [Fact]
    public void Names_resolve_in_the_site_scope_to_ids()
    {
        Assert.Equal("""{"path":["Customer","CreditLimit"]}""", Show(Ok("customer.creditLimit")));
        Assert.Equal("""{"path":["Customer","Region","Country","CountryCode"]}""", Show(Ok("customer.region.country.code")));
        Assert.Equal("""{"path":["PriceAmount"]}""", Show(Ok("price.amount")));
        Assert.Equal("""{"path":["OrderDate"]}""", Show(Ok("\"order date\"")));
        Assert.Equal("""{"ref":"Net30"}""", Show(Ok("PaymentTerm.NET30")));
        Assert.Equal("""{"ref":"Paid"}""", Show(Ok("\"InvoiceStatus\".\"Paid\"")));

        // A word that names a function or a typed literal is a name where no call or text follows it.
        var scope = Invoice();
        scope.Root.Add(new ExprMember(Id(80), "date", ExprType.Date, false)).Add(new ExprMember(Id(81), "length", ExprType.Int32, false));
        Assert.Equal("""{"op":"eq","args":[{"path":["01JEXT00000000000000000080"]},{"path":["IssuedOn"]}]}""", Show(ExprText.Parse("date = issuedOn", scope).Tree!));
        Assert.Equal("""{"path":["01JEXT00000000000000000081"]}""", Show(ExprText.Parse("length", scope).Tree!));

        // Members of the selected object at a rule condition.
        var rule = new ExprScope(ExprSiteKind.RuleCondition)
        {
            MemberPaths = p => p == "properties.retentionDays" ? new ExprMember(p, p, ExprType.Int32, true) : null,
        };
        Assert.Equal("""{"op":"ge","args":[{"member":"properties.retentionDays"},{"value":30}]}""", Show(ExprText.Parse("properties.retentionDays >= 30", rule).Tree!));
    }

    [Theory]
    [InlineData("total >", "MQ3101", 7)]
    [InlineData("total = = 1", "MQ3101", 8)]
    [InlineData("(total = 1", "MQ3101", 10)]
    [InlineData("'open", "MQ3101", 0)]
    [InlineData("total = 1 = 2", "MQ3101", 10)]
    [InlineData("total between 1 and 2 between 1 and 2", "MQ3101", 22)]
    [InlineData("upper(code)", "MQ3101", 0)]
    [InlineData("12abc", "MQ3101", 2)]
    [InlineData("date '2026-02-30'", "MQ3101", 5)]
    [InlineData("99999999999999999999", "MQ3101", 0)]
    [InlineData("total # 1", "MQ3101", 6)]
    [InlineData("dueDate <= issuedOn", "MQ3102", 0)]
    [InlineData("InvoiceStatus.Void", "MQ3102", 0)]
    [InlineData("satisfies Missing(amount: total)", "MQ3109", 10)]
    [InlineData("satisfies PositiveAmount(value: total)", "MQ3109", 25)]
    [InlineData("case else 1 end", "MQ3101", 5)]
    [InlineData("is null", "MQ3101", 0)]
    public void Text_that_does_not_parse_reports_where(string text, string rule, int position)
    {
        var parse = ExprText.Parse(text, Invoice());
        Assert.False(parse.Succeeded);
        Assert.Null(parse.Tree);
        Assert.Equal((rule, position), (parse.Problem!.Rule, parse.Position));
        Assert.Equal(("/checks/0/condition", Id(99)), (parse.Problem.JsonPointer, parse.Problem.ElementId));
        Assert.Contains("at position " + (position + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), parse.Problem.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Text_that_nests_too_deep_is_MQ3113_not_a_crash()
    {
        var parse = ExprText.Parse(new string('(', 5000) + "1" + new string(')', 5000), Invoice());
        Assert.Equal("MQ3113", parse.Problem!.Rule);
        var negations = ExprText.Parse(string.Concat(Enumerable.Repeat("not ", 5000)) + "flag", Invoice());
        Assert.Equal("MQ3113", negations.Problem!.Rule);
    }

    private static Maquettiste.Engine.Model.Expr Ok(string text)
    {
        var parse = ExprText.Parse(text, Invoice());
        Assert.True(parse.Succeeded, parse.Problem?.Message);
        return parse.Tree!;
    }
}
