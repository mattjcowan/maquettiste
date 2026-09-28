using System.Text;
using System.Text.Json;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Validation;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Validation;

/// <summary>Unit tests for the validator's building blocks: pointer positions, literal fit, credentials, dialects, the save rule.</summary>
public sealed class RuleHelperTests
{
    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Theory]
    [InlineData("", 1, 1)]
    [InlineData("/name", 3, 11)]
    [InlineData("/attributes/1", 6, 5)]
    [InlineData("/attributes/1/type/ref", 6, 24)]
    [InlineData("/a~1b", 8, 10)]
    [InlineData("/missing", null, null)]
    [InlineData("/attributes/7", null, null)]
    public void Pointer_locator_finds_the_value(string pointer, int? line, int? column)
    {
        const string json = "{\n  \"kind\": \"entity\",\n  \"name\": \"Ünïcode\",\n  \"attributes\": [\n    { \"id\": 1 },\n    { \"type\": { \"ref\": \"X\" } }\n  ],\n  \"a/b\": true\n}\n";

        var position = PointerLocator.Locate(Encoding.UTF8.GetBytes(json), pointer);

        Assert.Equal(line, position?.Line);
        Assert.Equal(column, position?.Column);
    }

    [Fact]
    public void Pointer_locator_counts_characters_not_bytes()
    {
        var json = "{ \"ünï\": 1, \"b\": 2 }";

        Assert.Equal((1, 18), PointerLocator.Locate(Encoding.UTF8.GetBytes(json), "/b"));
    }

    [Theory]
    [InlineData("int16", "32767", null)]
    [InlineData("int16", "32768", "the number is out of range for int16")]
    [InlineData("int32", "1.5", "expected an integer")]
    [InlineData("int64", "\"1\"", "a string on an integer type")]
    [InlineData("bool", "0", "expected true or false")]
    [InlineData("decimal", "12.345", null)]
    [InlineData("date", "\"2024-02-29\"", null)]
    [InlineData("date", "\"2023-02-29\"", "not an ISO 8601 date")]
    [InlineData("time", "\"23:59:59.1234567\"", null)]
    [InlineData("time", "\"24:00\"", "not an ISO 8601 time")]
    [InlineData("datetime", "\"2024-01-01T10:00:00Z\"", null)]
    [InlineData("datetime", "\"01/02/2024\"", "not an ISO 8601 datetime")]
    [InlineData("datetimeoffset", "\"2024-01-01T10:00:00\"", "not an ISO 8601 datetimeoffset")]
    [InlineData("datetimeoffset", "\"2024-01-01T10:00:00-05:00\"", null)]
    [InlineData("duration", "\"P1DT2H\"", null)]
    [InlineData("duration", "\"P\"", "not an ISO 8601 duration")]
    [InlineData("uuid", "\"not-a-uuid\"", "expected a UUID string (8-4-4-4-12 hex digits)")]
    [InlineData("ulid", "\"01JENT00000000000000000001\"", null)]
    [InlineData("binary", "\"aGVsbG8=\"", null)]
    [InlineData("binary", "\"***\"", "expected a base64 string")]
    [InlineData("json", "{\"any\": [1]}", null)]
    public void Literal_fit_follows_the_keyword(string keyword, string literal, string? reason) =>
        Assert.Equal(reason, AttributeRules.Fits(keyword, J(literal), null, null, null));

    [Theory]
    [InlineData(5, 2, "123.45", null)]
    [InlineData(5, 2, "1234.5", "the number is out of range for decimal(5,2)")]
    [InlineData(5, 2, "1.234", "more than 2 decimal places")]
    [InlineData(5, 2, "1.2e2", null)]
    [InlineData(5, 2, "1.2e3", "the number is out of range for decimal(5,2)")]
    public void Decimal_literals_respect_precision_and_scale(int precision, int scale, string literal, string? reason) =>
        Assert.Equal(reason, AttributeRules.Fits("decimal", J(literal), null, precision, scale));

    [Theory]
    [InlineData("password", "hunter2", null, true)]
    [InlineData("password", "changeme", null, false)]
    [InlineData("password", "${DB_PASSWORD}", null, false)]
    [InlineData("greeting", "hello", null, false)]
    [InlineData("greeting", "hello", Sensitivity.Secret, true)]
    [InlineData("url", "postgres://app:s3cret@db:5432/app", null, true)]
    [InlineData("conn", "Server=db;Database=app;Password=pa55;", null, true)]
    [InlineData("note", "ghp_abcdefghijklmnopqrstuvwxyz0123456789", null, true)]
    [InlineData("note", "-----BEGIN RSA PRIVATE KEY-----", null, true)]
    [InlineData("note", "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.abc", null, true)]
    [InlineData("apiKey", "", null, false)]
    public void Credential_heuristics(string name, string value, Sensitivity? sensitivity, bool flagged) =>
        Assert.Equal(flagged, Credentials.LooksLikeCredential(name, JsonSerializer.SerializeToElement(value), sensitivity) is not null);

    [Theory]
    [InlineData(Dialect.PostgreSql, "character varying(20)", true)]
    [InlineData(Dialect.PostgreSql, "timestamp(3) with time zone", true)]
    [InlineData(Dialect.PostgreSql, "int4[]", true)]
    [InlineData(Dialect.PostgreSql, "nvarchar(10)", false)]
    [InlineData(Dialect.SqlServer, "NVARCHAR(MAX)", true)]
    [InlineData(Dialect.MySql, "int(11) unsigned zerofill", true)]
    [InlineData(Dialect.Oracle, "interval day(2) to second(6)", true)]
    [InlineData(Dialect.Oracle, "text", false)]
    [InlineData(Dialect.Sqlite, "anything at all", true)]
    public void Native_types_are_checked_per_dialect(Dialect dialect, string nativeType, bool known) =>
        Assert.Equal(known, DialectInfo.IsKnownNativeType(dialect, nativeType, null));

    [Fact]
    public void Type_map_values_extend_the_known_native_types() =>
        Assert.True(DialectInfo.IsKnownNativeType(Dialect.PostgreSql, "ltree", new Dictionary<string, string> { ["string"] = "ltree" }));

    [Fact]
    public void Identifier_length_is_bytes_on_postgresql_and_characters_elsewhere()
    {
        Assert.Equal(4, DialectInfo.IdentifierLength(Dialect.PostgreSql, "éé"));
        Assert.Equal(2, DialectInfo.IdentifierLength(Dialect.SqlServer, "éé"));
    }

    [Fact]
    public void Changing_a_stereotype_key_is_MQ3020()
    {
        var b = new ModelBuilder();
        var audited = b.Stereotype("audited");
        b.Entity("Invoice").Key("id", "uuid").Stereotype("audited");
        var model = b.Build();
        var stored = model.Get<Stereotype>(audited.Id)!;

        var renamed = SaveRules.Check(model, stored with { Name = "Audited (v2)" }, "p.json");
        var rekeyed = SaveRules.Check(model, stored with { Key = "tracked" }, ".maquettiste/model/vocabularies/stereotypes/audited.json");
        var created = SaveRules.Check(model, stored with { Id = b.NewId(), Key = "tracked" }, "q.json");

        Assert.Empty(renamed);
        Assert.Empty(created);
        var d = Assert.Single(rekeyed);
        Assert.Equal(("MQ3020", DiagnosticSeverity.Error, "/key", audited.Id), (d.Rule, d.Severity, d.JsonPointer, d.ElementId));
        Assert.Contains("1 element(s) apply it", d.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Merged_properties_put_element_values_over_stereotype_defaults_in_order()
    {
        var b = new ModelBuilder();
        b.Stereotype("first").DefaultProperty("a", 1).DefaultProperty("b", 1);
        b.Stereotype("second").DefaultProperty("b", 2).DefaultProperty("c", 2);
        var e = b.Entity("E").Key("id", "uuid").Stereotype("first").Stereotype("second").Property("c", 3);
        var model = b.Build();

        var merged = ExtensionSet.Merge(model, model.Get<Entity>(e.Id)!);

        Assert.Equal("""{"a":1,"b":2,"c":3}""", merged.GetRawText());
    }
}
