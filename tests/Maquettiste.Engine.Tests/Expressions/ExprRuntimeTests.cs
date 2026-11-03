using System.Text.Json;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Expressions;
using static Maquettiste.Engine.Tests.Expressions.ExprScopes;

namespace Maquettiste.Engine.Tests.Expressions;

/// <summary>The JavaScript runtime's semantics, run in the engine's sandbox (next-phase-3-design.md section 3.1, task H3).</summary>
public sealed class ExprRuntimeTests
{
    /// <summary>Runs one raw function over a row.</summary>
    internal static object? Run(string function, string row = "{}")
    {
        using var evaluator = ExprEvaluator.Create([KeyValuePair.Create("f", function)]);
        using var doc = JsonDocument.Parse(row);
        return evaluator.Evaluate("f", doc.RootElement);
    }

    /// <summary>Types the text at a site of the invoice scope, lowers it to JavaScript and runs it over a row.</summary>
    internal static object? Eval(string text, string row = "{}", ExprSiteKind site = ExprSiteKind.Value)
    {
        var typed = Typed(text, site);
        return Run(ExprLowering.ToJavaScript(typed, site, Columns()), row);
    }

    internal static TypedExpr Typed(string text, ExprSiteKind site, ExprType? valueType = null)
    {
        var scope = Invoice(site, valueType);
        var parsed = ExprText.Parse(text, scope);
        Assert.True(parsed.Tree is not null, parsed.Problem?.Message);
        var problems = new List<Diagnostic>();
        var typed = ExprTyper.Type(parsed.Tree!, scope, problems);
        Assert.True(typed is not null, string.Join("; ", problems.Select(p => p.Message)));
        return typed!;
    }

    /// <summary>The API forms of the scope's enum members and codes.</summary>
    internal static ExprColumns Columns() => new()
    {
        Ref = (id, form) => id == Paid ? (form == ExprStoredForm.Integer ? 2L : "PAID")
            : id == Draft ? (form == ExprStoredForm.Integer ? 1L : "DRAFT")
            : "NET30",
    };

    [Theory]
    [InlineData("and", "true", "true", true)]
    [InlineData("and", "true", "null", null)]
    [InlineData("and", "false", "null", false)]
    [InlineData("and", "null", "null", null)]
    [InlineData("or", "false", "null", null)]
    [InlineData("or", "true", "null", true)]
    [InlineData("or", "false", "false", false)]
    [InlineData("implies", "null", "true", true)]
    [InlineData("implies", "false", "null", true)]
    [InlineData("implies", "true", "null", null)]
    [InlineData("implies", "true", "false", false)]
    public void Kleene_logic_answers_as_sql(string op, string a, string b, bool? expected) =>
        Assert.Equal(expected, Run("(row) => mq." + op + "(" + a + ", " + b + ")"));

    [Fact]
    public void Not_null_is_null_and_sites_judge_unknown_by_their_mode()
    {
        Assert.Null(Run("(row) => mq.not(null)"));
        Assert.Equal(true, Run("(row) => mq.passes(null)"));
        Assert.Equal(false, Run("(row) => mq.holds(null)"));
        Assert.Equal(true, Eval("issuedOn <= dueOn", """{ "issuedOn": "2026-01-31" }""", ExprSiteKind.Check));
        Assert.Equal(false, Eval("issuedOn <= dueOn", """{ "issuedOn": "2026-01-31" }""", ExprSiteKind.Guard));
    }

    [Fact]
    public void Decimals_are_exact() => Assert.Equal(true, Eval("0.1 + 0.2 = 0.3", site: ExprSiteKind.Guard));

    [Theory]
    [InlineData("1 / 3", "0.3333333333333333")]
    [InlineData("1 / 1000000", "0.0000010000000000")]
    [InlineData("2 / 10673", "0.0001873887379369")]
    [InlineData("-2 / 10673", "-0.0001873887379369")]
    [InlineData("0 / 7", "0.0000000000000000")]
    [InlineData("total / 0", null)]
    [InlineData("10.5 * 2.25", "23.625")]
    [InlineData("round(2.345, 2)", "2.35")]
    [InlineData("round(-2.345, 2)", "-2.35")]
    [InlineData("round(5, 2)", "5.00")]
    public void Arithmetic_keeps_postgresql_scales(string text, string? expected) =>
        Assert.Equal(expected, Eval(text, """{ "total": 12.5 }"""));

    [Fact]
    public void Division_by_zero_is_null_for_doubles_too() => Assert.Null(Eval("rate / 0", """{ "rate": 1.5 }"""));

    [Theory]
    [InlineData("add_months(issuedOn, 1)", "2026-01-31", "2026-02-28")]
    [InlineData("add_months(issuedOn, 1)", "2024-02-29", "2024-03-29")]
    [InlineData("add_months(issuedOn, 12)", "2024-02-29", "2025-02-28")]
    [InlineData("add_months(issuedOn, -1)", "2024-03-31", "2024-02-29")]
    [InlineData("add_days(issuedOn, 1)", "2024-02-28", "2024-02-29")]
    [InlineData("add_days(issuedOn, -1)", "1970-01-01", "1969-12-31")]
    public void Dates_move_by_calendar(string text, string issuedOn, string expected) =>
        Assert.Equal(expected, Eval(text, "{ \"issuedOn\": \"" + issuedOn + "\" }"));

    [Theory]
    [InlineData("add_days(issuedOn, quantity)", "2026-01-02")]
    [InlineData("add_months(issuedOn, quantity)", "2026-02-01")]
    public void A_count_with_a_scale_counts_its_value(string text, string expected) =>
        Assert.Equal(expected, Eval(text, """{ "issuedOn": "2026-01-01", "quantity": 1.0 }"""));

    [Theory]
    [InlineData("createdAt", "2026-01-31T09:30:00.1234565", "2026-01-31T09:30:00.123456")]
    [InlineData("createdAt", "2026-01-31T09:30:00.1234555", "2026-01-31T09:30:00.123456")]
    [InlineData("createdAt", "2026-01-31T09:30:00.1234575", "2026-01-31T09:30:00.123458")]
    [InlineData("createdAt", "2026-01-31T09:30:00.1234565000001", "2026-01-31T09:30:00.123457")]
    [InlineData("createdAt", "2026-01-31T09:30:00.12345650000000000001", "2026-01-31T09:30:00.123456")] // as a double, exactly a half
    [InlineData("createdAt", "2026-01-31T09:30:00.9999995", "2026-01-31T09:30:01")]
    [InlineData("opens", "09:30:00.1234565", "09:30:00.123456")]
    public void Fractional_seconds_round_to_microseconds_as_postgresql_rounds_them(string text, string value, string expected) =>
        Assert.Equal(expected, Eval(text, "{ \"" + text + "\": \"" + value + "\" }"));

    [Theory]
    [InlineData("date_part('isodow', issuedOn)", "3")] // 2026-01-07 is a Wednesday
    [InlineData("date_part('year', issuedOn)", "2026")]
    [InlineData("date_part('hour', sentAt)", "23")]
    [InlineData("date_of(sentAt)", "2026-01-06")]
    [InlineData("add_days(sentAt, 1)", "2026-01-07T23:30:00Z")]
    public void Offset_timestamps_compute_in_utc(string text, string expected) =>
        Assert.Equal(expected, Eval(text, """{ "issuedOn": "2026-01-07", "sentAt": "2026-01-07T01:30:00+02:00" }"""));

    [Fact]
    public void A_datetime_meets_a_datetimeoffset_as_utc()
    {
        const string Row = """{ "createdAt": "2026-01-07T10:00:00", "sentAt": "2026-01-07T11:00:00+01:00" }""";
        Assert.Equal(true, Eval("createdAt = sentAt", Row, ExprSiteKind.Guard));
    }

    [Fact]
    public void Trim_removes_spaces_only() => Assert.Equal("\tx", Eval("trim(code)", """{ "code": "  \tx  " }"""));

    [Fact]
    public void Text_beyond_the_basic_plane_counts_and_orders_by_code_point()
    {
        Assert.Equal("1", Eval("length(code)", """{ "code": "😀" }"""));
        Assert.Equal(true, Eval("code > '�'", """{ "code": "😀" }""", ExprSiteKind.Guard));
        Assert.Equal(true, Eval("code < 'b'", """{ "code": "a" }""", ExprSiteKind.Guard));
    }

    [Fact]
    public void A_fixed_length_value_reads_without_its_trailing_spaces()
    {
        Assert.Equal(true, Eval("padded = 'AB'", """{ "padded": "AB   " }""", ExprSiteKind.Guard));
        Assert.Equal("2", Eval("length(padded)", """{ "padded": "AB   " }"""));
    }

    [Fact]
    public void A_regex_dot_matches_a_newline_and_anchors_hold_the_whole_text()
    {
        Assert.Equal(true, Eval("matches(code, '^a.b$')", """{ "code": "a\nb" }""", ExprSiteKind.Guard));
        Assert.Equal(false, Eval("matches(code, '^a$')", """{ "code": "a\nb" }""", ExprSiteKind.Guard));
        Assert.Equal(true, Eval("matches(code, '^.$')", """{ "code": "😀" }""", ExprSiteKind.Guard));
    }

    [Fact]
    public void Large_integers_and_long_decimals_keep_every_digit()
    {
        Assert.Equal("9007199254740994", Eval("big + 1", """{ "big": 9007199254740993 }"""));
        Assert.Equal("123456789012345678.91", Eval("total", """{ "total": 123456789012345678.91 }"""));
        Assert.Equal(true, Eval("total = 123456789012345678.91", """{ "total": 123456789012345678.91 }""", ExprSiteKind.Guard));
        Assert.Equal("12.50", Eval("total", """{ "total": "12.5" }"""));
    }

    [Fact]
    public void In_lists_distinct_coalesce_and_case_follow_sql()
    {
        Assert.Null(Eval("quantity in (1, 2)", "{}"));
        Assert.Equal(false, Eval("quantity not in (1, 2)", """{ "quantity": 1 }"""));
        Assert.Equal(true, Eval("dueOn is distinct from issuedOn", """{ "issuedOn": "2026-01-01" }"""));
        Assert.Equal(false, Eval("dueOn is not distinct from issuedOn", """{ "issuedOn": "2026-01-01" }"""));
        Assert.Equal("2026-01-01", Eval("coalesce(dueOn, issuedOn)", """{ "issuedOn": "2026-01-01" }"""));
        Assert.Null(Eval("case when quantity > 1 then 'many' end", """{ "quantity": 1 }"""));
        Assert.Equal("one", Eval("case when quantity > 1 then 'many' else 'one' end", """{ "quantity": null }"""));
        Assert.Equal(true, Eval("status = InvoiceStatus.Paid", """{ "status": "PAID" }""", ExprSiteKind.Guard));
    }

    [Fact]
    public void Json_reads_its_kind_and_length()
    {
        Assert.Equal("number", Eval("json_type(payload)", """{ "payload": 5 }"""));
        Assert.Equal("object", Eval("json_type(payload)", """{ "payload": { "a": null } }"""));
        Assert.Null(Eval("json_type(payload)", """{ "payload": null }"""));
        Assert.Equal("3", Eval("json_length(payload)", """{ "payload": [1, null, "x"] }"""));
        Assert.Null(Eval("json_length(payload)", """{ "payload": { "a": 1 } }"""));
    }

    [Fact]
    public void Doubles_compute_as_ieee_doubles_and_meet_ints()
    {
        Assert.Equal(true, Eval("rate > quantity", """{ "rate": 1.5, "quantity": 1 }""", ExprSiteKind.Guard));
        Assert.Equal(0.30000000000000004, Eval("rate + double '0.2'", """{ "rate": 0.1 }"""));
        Assert.Equal(2.5, Eval("rate + quantity", """{ "rate": 1.5, "quantity": 1 }"""));
    }
}
