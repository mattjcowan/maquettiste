using System.Collections.Immutable;
using System.Text.Json;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Validation;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Validation;

/// <summary>
/// Literal column defaults (MQ3019, next-phase-1-design.md section 3.5): a string column's default against its resolved length,
/// a column with a native type checked for the kind of value only, a json column's default as a string of JSON text and a range
/// column's default as a string.
/// </summary>
public sealed class ColumnDefaultTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A PostgreSQL table <c>invoices</c> with an id and one column <c>value</c> of the given type and default.</summary>
    private static (ModelBuilder Builder, TableBuilder Table) Model(string type, string defaultJson, int? length = null, string? nativeType = null,
        string? subtype = null)
    {
        var b = new ModelBuilder(seed: 3001);
        var db = b.Database("db", Dialect.PostgreSql);
        var table = b.Table("invoices", db).Column("id", "int64", nullable: false).Column("value", type, nullable: true, length: length, nativeType: nativeType)
            .PrimaryKey("id")
            .Edit(t => t with { Columns = [t.Columns[0], t.Columns[1] with { Default = JsonDocument.Parse(defaultJson).RootElement.Clone(), Subtype = subtype }] });
        return (b, table);
    }

    private static async Task<List<Diagnostic>> FindingsAsync(ModelBuilder b) =>
        [.. (await ValidationFixture.Validator().ValidateAsync(b.Build(), ValidationScope.All, null, Ct)).Diagnostics.Where(d => d.Rule == "MQ3019")];

    [Fact]
    public async Task A_string_default_is_checked_against_the_resolved_length()
    {
        var text = JsonSerializer.Serialize(new string('x', 256));
        var (b, _) = Model("string", text);
        var finding = Assert.Single(await FindingsAsync(b));
        Assert.Equal("/columns/1/default", finding.JsonPointer);
        Assert.Equal(
            "The default of column 'value' does not fit string: the string is longer than length 255, the default string length (set the column's length).",
            finding.Message);

        // The column's own length, the project's conventions and the database's conventions each set the resolved length.
        Assert.Empty(await FindingsAsync(Model("string", text, length: 300).Builder));
        var (wide, _) = Model("string", text);
        wide.Settings(s => s with { Conventions = new Conventions { DefaultStringLength = 300 } });
        Assert.Empty(await FindingsAsync(wide));
        var (narrow, _) = Model("string", "\"awaiting payment\"");
        narrow.Settings(s => s with { Databases = ImmutableDictionary<string, Conventions>.Empty.Add("db", new Conventions { DefaultStringLength = 8 }) });
        Assert.Contains("longer than length 8", Assert.Single(await FindingsAsync(narrow)).Message, StringComparison.Ordinal);
        Assert.Contains("longer than length 4", Assert.Single(await FindingsAsync(Model("string", "\"pending\"", length: 4).Builder)).Message,
            StringComparison.Ordinal);

        // A text column has no length.
        Assert.Empty(await FindingsAsync(Model("text", text).Builder));
    }

    [Fact]
    public async Task A_native_type_column_checks_the_kind_of_value_only()
    {
        Assert.Empty(await FindingsAsync(Model("string", JsonSerializer.Serialize(new string('x', 300)), nativeType: "citext").Builder));
        Assert.Empty(await FindingsAsync(Model("decimal", "12345.678", nativeType: "numeric(5,2)").Builder));
        Assert.Empty(await FindingsAsync(Model("datetime", "\"now\"", nativeType: "timestamptz").Builder));

        var finding = Assert.Single(await FindingsAsync(Model("string", "42", nativeType: "citext").Builder));
        Assert.Equal("The default of column 'value' does not fit string with nativeType 'citext': expected a string.", finding.Message);
        Assert.EndsWith("expected a number.", Assert.Single(await FindingsAsync(Model("int32", "\"1\"", nativeType: "bigint").Builder)).Message,
            StringComparison.Ordinal);
        Assert.EndsWith("expected true or false.", Assert.Single(await FindingsAsync(Model("bool", "1", nativeType: "boolean").Builder)).Message,
            StringComparison.Ordinal);
        Assert.Contains("a json column takes a string of JSON text",
            Assert.Single(await FindingsAsync(Model("json", "{\"a\": 1}", nativeType: "jsonb").Builder)).Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{\"days\": 30}")]
    [InlineData("[7, 14]")]
    [InlineData("5")]
    [InlineData("true")]
    public async Task A_json_default_is_a_string_of_json_text(string value)
    {
        var finding = Assert.Single(await FindingsAsync(Model("json", value).Builder));
        Assert.Equal(
            "The default of column 'value' does not fit json: a json column takes a string of JSON text; an object, array, number or boolean has no "
            + "literal for it (write the JSON as a string, or use defaultSql).",
            finding.Message);
        Assert.Equal(DiagnosticSeverity.Error, finding.Severity);

        Assert.Empty(await FindingsAsync(Model("json", JsonSerializer.Serialize(value)).Builder));
        Assert.EndsWith("does not fit json: the string is not valid JSON text.",
            Assert.Single(await FindingsAsync(Model("json", JsonSerializer.Serialize(value + "}")).Builder)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_range_default_is_a_string()
    {
        var finding = Assert.Single(await FindingsAsync(Model("range", "[1, 10]", subtype: "int32").Builder));
        Assert.Equal("The default of column 'value' does not fit range: expected a string (the range's text form).", finding.Message);
        Assert.Empty(await FindingsAsync(Model("range", "\"[1,10)\"", subtype: "int32").Builder));
        Assert.Empty(await FindingsAsync(Model("multirange", "\"{[1,5),[8,9)}\"", subtype: "int32").Builder));

        Assert.Equal("expected a string", AttributeRules.FitsKind("range", JsonDocument.Parse("5").RootElement));
        Assert.Null(AttributeRules.FitsKind("range", JsonDocument.Parse("\"[1,10)\"").RootElement));
    }
}
