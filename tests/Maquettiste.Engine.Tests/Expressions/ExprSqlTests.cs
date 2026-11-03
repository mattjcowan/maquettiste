using System.Text.RegularExpressions;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Expressions;
using static Maquettiste.Engine.Tests.Expressions.ExprRuntimeTests;
using static Maquettiste.Engine.Tests.Expressions.ExprScopes;

namespace Maquettiste.Engine.Tests.Expressions;

/// <summary>The fixed PostgreSQL each operator and function lowers to, through the query tree's row mode (next-phase-3-design.md
/// section 3.1, task H4).</summary>
public sealed partial class ExprSqlTests
{
    /// <summary>The invoice's attributes as an invented <c>invoices</c> table's snake-case columns.</summary>
    private static ExprColumns Table(ExprStoredForm status = ExprStoredForm.Text, string dialect = "postgresql") => new()
    {
        Dialect = dialect,
        Ref = Columns().Ref,
        Physical = true,
        Column = t => new ExprColumn(Snake().Replace(t.Member!.Name, m => "_" + m.Value.ToLowerInvariant()))
        {
            FixedLength = t.Member.FixedLength,
            Scale = t.Member.Scale,
            Form = t.Member.Id == Status ? status : ExprStoredForm.Text,
        },
    };

    [GeneratedRegex("[A-Z]")]
    private static partial Regex Snake();

    private static string? Sql(string text, ExprSiteKind site = ExprSiteKind.Check, ExprColumns? columns = null, List<Diagnostic>? problems = null) =>
        ExprLowering.ToPostgreSql(Typed(text, site), columns ?? Table(), site, problems ?? []);

    [Theory]
    [InlineData("issuedOn <= dueOn", "issued_on <= due_on")]
    [InlineData("code = 'a'", "code COLLATE \"C\" = 'a' COLLATE \"C\"")]
    [InlineData("code <> 'a'", "code COLLATE \"C\" <> 'a' COLLATE \"C\"")]
    [InlineData("code < 'b'", "code COLLATE \"C\" < 'b' COLLATE \"C\"")]
    [InlineData("code in ('a', 'b')", "code COLLATE \"C\" IN ('a' COLLATE \"C\", 'b' COLLATE \"C\")")]
    [InlineData("code not in ('a')", "code COLLATE \"C\" NOT IN ('a' COLLATE \"C\")")]
    [InlineData("code between 'a' and 'b'", "code COLLATE \"C\" BETWEEN 'a' COLLATE \"C\" AND 'b' COLLATE \"C\"")]
    [InlineData("quantity between 1 and 9", "quantity BETWEEN 1 AND 9")]
    [InlineData("dueOn is null", "due_on IS NULL")]
    [InlineData("dueOn is not null", "due_on IS NOT NULL")]
    [InlineData("dueOn is distinct from issuedOn", "due_on IS DISTINCT FROM issued_on")]
    [InlineData("code is not distinct from 'a'", "code COLLATE \"C\" IS NOT DISTINCT FROM 'a' COLLATE \"C\"")]
    [InlineData("flag and not flag or flag", "(flag = true AND NOT (flag = true)) OR flag = true")]
    [InlineData("flag implies flag", "NOT (flag = true) OR flag = true")]
    [InlineData("total * 2 - 1 > -total / 3", "((total * 2) - 1) > round((((-total) + 0.000000000000000000) / NULLIF(3, 0)), 16)")]
    [InlineData("quantity + 1 > 2", "(CAST(quantity AS numeric) + 1) > 2")]
    [InlineData("quantity * 2 + 1 > 2", "((CAST(quantity AS numeric) * 2) + 1) > 2")]
    [InlineData("-quantity < 0", "(-CAST(quantity AS numeric)) < 0")]
    [InlineData("rate / 2 > rate", "(rate / NULLIF(2, 0)) > rate")]
    [InlineData("rate > quantity", "rate > quantity")]
    [InlineData("code || 'x' = 'ax'", "(code || 'x') COLLATE \"C\" = 'ax' COLLATE \"C\"")]
    [InlineData("coalesce(dueOn, issuedOn) = date '2026-01-31'", "COALESCE(due_on, issued_on) = DATE '2026-01-31'")]
    [InlineData("case when flag then 1 else 2 end = 1", "CASE WHEN flag = true THEN 1 ELSE 2 END = 1")]
    [InlineData("padded = 'A'", "rtrim(padded) COLLATE \"C\" = 'A' COLLATE \"C\"")]
    [InlineData("createdAt < sentAt", "(created_at AT TIME ZONE 'UTC') < sent_at")]
    [InlineData("opens < time '09:30'", "opens < TIME '09:30'")]
    [InlineData("reference = uuid '0190a5c4-0000-7000-8000-000000000001'", "reference = UUID '0190a5c4-0000-7000-8000-000000000001'")]
    [InlineData("rate < double '1.5'", "rate < float8 '1.5'")]
    [InlineData("decimal '100000' * decimal '100000' > total * 1.5", "(NUMERIC '100000' * NUMERIC '100000') > (total * 1.5)")]
    [InlineData("satisfies PositiveAmount(amount: total)", "total > 0")]
    [InlineData("\"order date\" <= issuedOn", "\"order date\" <= issued_on")]
    public void Each_operator_lowers_to_row_sql(string text, string expected) => Assert.Equal(expected, Sql(text));

    [Theory]
    [InlineData("trim(code) = ''", "btrim(code, ' ') COLLATE \"C\" = '' COLLATE \"C\"")]
    [InlineData("length(code) > 1", "char_length(code) > 1")]
    [InlineData("matches(code, '^[A-Z]{2}$')", "code COLLATE \"C\" ~ '^[A-Z]{2}$'")]
    [InlineData("nonnull_count(dueOn, flag) = 1", "num_nonnulls(due_on, flag) = 1")]
    [InlineData("add_days(issuedOn, 30) >= dueOn", "(issued_on + CAST(30 AS integer)) >= due_on")]
    [InlineData("add_days(createdAt, 1) > createdAt", "(created_at + make_interval(days => CAST(1 AS integer))) > created_at")]
    [InlineData("add_days(sentAt, 1) > sentAt", "(sent_at + make_interval(hours => CAST(1 AS integer) * 24)) > sent_at")]
    [InlineData("add_months(issuedOn, 1) >= dueOn", "CAST(issued_on + make_interval(months => CAST(1 AS integer)) AS date) >= due_on")]
    [InlineData("add_months(sentAt, 1) > sentAt", "(((sent_at AT TIME ZONE 'UTC') + make_interval(months => CAST(1 AS integer))) AT TIME ZONE 'UTC') > sent_at")]
    [InlineData("date_part('isodow', issuedOn) < 6", "CAST(extract('isodow' from issued_on) AS integer) < 6")]
    [InlineData("date_part('hour', sentAt) = 9", "CAST(extract('hour' from (sent_at AT TIME ZONE 'UTC')) AS integer) = 9")]
    [InlineData("date_of(sentAt) = issuedOn", "CAST((sent_at AT TIME ZONE 'UTC') AS date) = issued_on")]
    [InlineData("date_of(createdAt) = issuedOn", "CAST(created_at AS date) = issued_on")]
    [InlineData("round(total, 1) > 0", "round(total, 1) > 0")]
    [InlineData("json_type(payload) = 'array'", "jsonb_typeof(CAST(payload AS jsonb)) COLLATE \"C\" = 'array' COLLATE \"C\"")]
    [InlineData("json_length(payload) > 0", "CASE WHEN jsonb_typeof(CAST(payload AS jsonb)) = 'array' THEN jsonb_array_length(CAST(payload AS jsonb)) END > 0")]
    public void Each_function_lowers_to_row_sql(string text, string expected) => Assert.Equal(expected, Sql(text));

    [Fact]
    public void A_condition_where_a_value_goes_is_a_pred_node_and_a_bool_value_as_a_condition_is_x_equals_true()
    {
        Assert.Equal("(quantity > 1) = flag", Sql("(quantity > 1) = flag"));
        Assert.Equal("COALESCE((quantity > 1), false) = true", Sql("coalesce(quantity > 1, false)"));
        Assert.Equal("flag = true", Sql("flag"));
        Assert.Equal("CASE WHEN quantity > 1 THEN (code COLLATE \"C\" = 'a' COLLATE \"C\") ELSE flag END = true",
            Sql("case when quantity > 1 then code = 'a' else flag end"));
    }

    [Fact]
    public void Sites_wrap_the_result()
    {
        Assert.Equal("COALESCE((issued_on <= due_on), false)", Sql("issuedOn <= dueOn", ExprSiteKind.Guard));
        Assert.Equal("(total + 1)", Sql("total + 1", ExprSiteKind.Value));
    }

    [Fact]
    public void An_enum_member_lowers_to_its_columns_stored_form()
    {
        Assert.Equal("status IN (2, 1)", Sql("status in (InvoiceStatus.Paid, InvoiceStatus.Draft)", columns: Table(ExprStoredForm.Integer)));
        Assert.Equal("status = 2", Sql("status = InvoiceStatus.Paid", columns: Table(ExprStoredForm.Integer)));
        Assert.Equal("status COLLATE \"C\" = 'PAID' COLLATE \"C\"", Sql("status = InvoiceStatus.Paid", columns: Table(ExprStoredForm.Text)));
        Assert.Equal("COALESCE(status, 1) = 2", Sql("coalesce(status, InvoiceStatus.Draft) = InvoiceStatus.Paid", columns: Table(ExprStoredForm.Integer)));
    }

    [Fact]
    public void An_int_bound_to_a_decimal_or_double_parameter_computes_as_one()
    {
        // The condition's arithmetic sees decimal or double operands and leaves the cast out, so the argument brings its own.
        var scope = Invoice();
        scope.AddConstraint(new ExprConstraint(Id(50), "LargeSquare", [new ExprMember(Id(51), "amount", ExprType.Decimal, false)],
            Parse($$"""{ "op": "gt", "args": [{ "op": "mul", "args": [{ "path": ["{{Id(51)}}"] }, { "path": ["{{Id(51)}}"] }] }, { "value": 0 }] }""")));
        scope.AddConstraint(new ExprConstraint(Id(52), "LargeRateSquare", [new ExprMember(Id(53), "rate", ExprType.Double, false)],
            Parse($$"""{ "op": "gt", "args": [{ "op": "mul", "args": [{ "path": ["{{Id(53)}}"] }, { "path": ["{{Id(53)}}"] }] }, { "value": 0 }] }""")));
        string? Lower(string text)
        {
            var problems = new List<Diagnostic>();
            var typed = ExprTyper.Type(ExprText.Parse(text, scope).Tree!, scope, problems);
            Assert.True(typed is not null, string.Join("; ", problems.Select(p => p.Message)));
            return ExprLowering.ToPostgreSql(typed, Table(), ExprSiteKind.Check, problems);
        }

        Assert.Equal("(CAST(quantity AS numeric) * CAST(quantity AS numeric)) > 0", Lower("satisfies LargeSquare(amount: quantity)"));
        Assert.Equal("(CAST(quantity AS float8) * CAST(quantity AS float8)) > 0", Lower("satisfies LargeRateSquare(rate: quantity)"));
        Assert.Equal("(total * total) > 0", Lower("satisfies LargeSquare(amount: total)"));
    }

    [Fact]
    public void A_negative_operand_never_starts_a_comment() => Assert.DoesNotContain("--", Sql("-(-quantity) < 0 and -(-1) < 0"), StringComparison.Ordinal);

    [Fact]
    public void Another_dialect_gets_no_text_and_mq4056()
    {
        var problems = new List<Diagnostic>();
        Assert.Null(Sql("issuedOn <= dueOn", columns: Table(dialect: "sqlserver"), problems: problems));
        Assert.Equal("MQ4056", Assert.Single(problems).Rule);
    }
}
