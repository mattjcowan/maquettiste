using System.Text.Json;
using Maquettiste.Engine.Expressions;
using Maquettiste.Engine.Model;
using static Maquettiste.Engine.Tests.Expressions.ExprScopes;

namespace Maquettiste.Engine.Tests.Expressions;

/// <summary>The text form printed from the tree of ids (next-phase-3-design.md section 3.1, task H2).</summary>
public sealed class ExprPrinterTests
{
    [Theory]
    [InlineData("issuedOn <= dueOn")]
    [InlineData("total = 1 and quantity <> 2")]
    [InlineData("flag or flag and flag")]
    [InlineData("(flag or flag) and flag")]
    [InlineData("flag implies flag implies flag")]
    [InlineData("(flag implies flag) implies flag")]
    [InlineData("not total < 1")]
    [InlineData("not (flag and flag)")]
    [InlineData("total + 1 > quantity * 2")]
    [InlineData("total - (quantity - 1) = 0")]
    [InlineData("total - quantity - 1 = 0")]
    [InlineData("total / (quantity * 2) >= 0.5")]
    [InlineData("-total < -1")]
    [InlineData("-(-total) < -2.50")]
    [InlineData("-(total + 1) < 0")]
    [InlineData("code || 'x' || code = 'it''s'")]
    [InlineData("code || ('x' || code) = 'a'")]
    [InlineData("quantity between 1 and 9")]
    [InlineData("quantity + 1 between 1 and quantity * 2")]
    [InlineData("status in (InvoiceStatus.Draft, InvoiceStatus.Paid)")]
    [InlineData("status not in (InvoiceStatus.Draft)")]
    [InlineData("terms = PaymentTerm.NET30")]
    [InlineData("dueOn is null or dueOn is not null")]
    [InlineData("(quantity = 1) is null")]
    [InlineData("total is distinct from 1 and total is not distinct from 2")]
    [InlineData("coalesce(dueOn, issuedOn) >= date '2026-01-31'")]
    [InlineData("case when flag then 1 when quantity > 2 then 2 else 3 end = quantity")]
    [InlineData("matches(code, '^[A-Z]{3}$') and length(trim(code)) = 3")]
    [InlineData("date_part('isodow', issuedOn) < 6")]
    [InlineData("add_days(issuedOn, 30) >= dueOn")]
    [InlineData("date_of(createdAt) = issuedOn and sentAt <= now()")]
    [InlineData("round(total / 3, 2) > 0 and nonnull_count(dueOn, terms) = 1")]
    [InlineData("json_type(payload) = 'object' and json_length(payload) > 0")]
    [InlineData("satisfies PositiveAmount(amount: total)")]
    [InlineData("customer.creditLimit >= total and customer.region.country.code = 'FR'")]
    [InlineData("price.amount > 0 and \"order date\" <= issuedOn")]
    [InlineData("opens > time '09:30' and reference <> uuid '0b5c3f4e-8a7d-4c1b-9e2f-1a2b3c4d5e6f'")]
    [InlineData("createdAt < datetime '2026-01-31T09:30' and sentAt > datetimeoffset '2026-01-31T09:30+02:00'")]
    [InlineData("rate > double '1.5' and total > decimal '7'")]
    [InlineData("big > 3000000000 and flag = true and flag <> false and dueOn <> null")]
    public void Print_of_parse_is_the_text(string text)
    {
        var scope = Invoice(ExprSiteKind.Guard);
        var tree = Parse(text, scope);
        var printed = ExprText.Print(tree, scope);
        Assert.Equal(text, printed);
        Assert.Equal(Show(tree), Show(Parse(printed, scope)));
    }

    [Theory]
    [InlineData("flag AND flag", "flag and flag")]
    [InlineData("flag   OR(flag)", "flag or flag")]
    [InlineData("((total))!=1", "total <> 1")]
    [InlineData("total IS NOT NULL", "total is not null")]
    [InlineData("Length(code)>1", "length(code) > 1")]
    [InlineData("NOW()>sentAt", "now() > sentAt")]
    [InlineData("CASE WHEN flag THEN 'a' END = code", "case when flag then 'a' end = code")]
    [InlineData("-(1)= -1", "-1 = -1")]
    [InlineData("\"total\" > 1", "total > 1")]
    public void Printing_a_parsed_text_twice_gives_the_same_text(string text, string expected)
    {
        var scope = Invoice(ExprSiteKind.Guard);
        var once = ExprText.Print(Parse(text, scope), scope);
        Assert.Equal(expected, once);
        Assert.Equal(once, ExprText.Print(Parse(once, scope), scope));
    }

    [Fact]
    public void Renaming_an_attribute_changes_the_printed_text_and_not_the_file()
    {
        var before = Invoice();
        var tree = Parse("issuedOn <= dueOn", before);
        var file = JsonSerializer.Serialize(tree, EngineJson.Options);

        var after = Invoice(dueOnName: "dueDate");
        Assert.Equal("issuedOn <= dueDate", ExprText.Print(tree, after));
        Assert.Equal(file, JsonSerializer.Serialize(tree, EngineJson.Options));
        Assert.Equal(file, JsonSerializer.Serialize(Parse("issuedOn <= dueDate", after), EngineJson.Options));
    }

    [Fact]
    public void Trees_print_with_the_fewest_parentheses()
    {
        var scope = Invoice();
        Assert.Equal("total - (quantity + 1)", Print("""{ "op": "sub", "args": [{ "path": ["{{Total}}"] }, { "op": "add", "args": [{ "path": ["{{Quantity}}"] }, { "value": 1 }] }] }"""));
        Assert.Equal("-(-total)", Print("""{ "op": "neg", "args": [{ "op": "neg", "args": [{ "path": ["{{Total}}"] }] }] }"""));
        Assert.Equal("-(-1)", Print("""{ "op": "neg", "args": [{ "value": -1 }] }"""));
        Assert.Equal("(total = 1) = flag", Print("""{ "op": "eq", "args": [{ "op": "eq", "args": [{ "path": ["{{Total}}"] }, { "value": 1 }] }, { "path": ["{{Flag}}"] }] }"""));
        Assert.Equal("flag and flag and flag", Print("""{ "op": "and", "args": [{ "op": "and", "args": [{ "path": ["{{Flag}}"] }, { "path": ["{{Flag}}"] }] }, { "path": ["{{Flag}}"] }] }"""));
        Assert.Equal("code || (code || code)", Print("""{ "op": "concat", "args": [{ "path": ["{{Code}}"] }, { "op": "concat", "args": [{ "path": ["{{Code}}"] }, { "path": ["{{Code}}"] }] }] }"""));

        // A hand-written text node prints as written, in parentheses inside a tree; unknown ids print quoted, so they do not parse.
        Assert.Equal("flag and (total > 1)", Print("""{ "op": "and", "args": [{ "path": ["{{Flag}}"] }, { "text": "total > 1" }] }"""));
        Assert.Equal("total > 1", Print("""{ "text": "total > 1" }"""));
        Assert.Equal("\"01JEXT00000000000000000077\" > 1", Print("""{ "op": "gt", "args": [{ "path": ["01JEXT00000000000000000077"] }, { "value": 1 }] }"""));
        Assert.Equal("MQ3102", ExprText.Parse("\"01JEXT00000000000000000077\" > 1", scope).Problem!.Rule);

        string Print(string json) => ExprText.Print(ExprScopes.Parse(json), scope);
    }

    private static Expr Parse(string text, ExprScope scope)
    {
        var parse = ExprText.Parse(text, scope);
        Assert.True(parse.Succeeded, text + ": " + parse.Problem?.Message);
        return parse.Tree!;
    }
}
