using System.Globalization;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Text;

namespace Maquettiste.Engine.Tests.Text;

public sealed class CasingTests
{
    [Theory]
    [InlineData("HTTPServer2Id", new[] { "http", "server2", "id" })]
    [InlineData("invoiceLine", new[] { "invoice", "line" })]
    [InlineData("InvoiceLine", new[] { "invoice", "line" })]
    [InlineData("invoice_line", new[] { "invoice", "line" })]
    [InlineData("invoice-line", new[] { "invoice", "line" })]
    [InlineData("  Invoice  line ", new[] { "invoice", "line" })]
    [InlineData("XMLHttpRequest", new[] { "xml", "http", "request" })]
    [InlineData("userID", new[] { "user", "id" })]
    [InlineData("Item2Name", new[] { "item2", "name" })]
    [InlineData("v2beta", new[] { "v2beta" })]
    [InlineData("ABC", new[] { "abc" })]
    [InlineData("", new string[0])]
    [InlineData("__", new string[0])]
    public void Words_split_on_boundaries_and_lowercase(string input, string[] expected) =>
        Assert.Equal(expected, Casing.Words(input));

    [Theory]
    [InlineData("HTTPServer2Id", CaseStyle.Snake, "http_server2_id")]
    [InlineData("HTTPServer2Id", CaseStyle.Pascal, "HttpServer2Id")]
    [InlineData("HTTPServer2Id", CaseStyle.Camel, "httpServer2Id")]
    [InlineData("HTTPServer2Id", CaseStyle.Kebab, "http-server2-id")]
    [InlineData("HTTPServer2Id", CaseStyle.UpperSnake, "HTTP_SERVER2_ID")]
    [InlineData("HTTPServer2Id", CaseStyle.Preserve, "HTTPServer2Id")]
    [InlineData("invoice_line", CaseStyle.Pascal, "InvoiceLine")]
    [InlineData("invoice line", CaseStyle.Camel, "invoiceLine")]
    [InlineData("InvoiceLine", CaseStyle.Snake, "invoice_line")]
    public void Apply_styles(string input, CaseStyle style, string expected) => Assert.Equal(expected, Casing.Apply(input, style));

    [Fact]
    public void Shortcuts_match_apply()
    {
        Assert.Equal("customer_id", Casing.Snake("CustomerId"));
        Assert.Equal("customer-id", Casing.Kebab("CustomerId"));
        Assert.Equal("CustomerId", Casing.Pascal("customer_id"));
        Assert.Equal("customerId", Casing.Camel("customer_id"));
        Assert.Equal("CUSTOMER_ID", Casing.UpperSnake("customerId"));
    }

    [Theory]
    [InlineData("tr-TR")]
    [InlineData("de-DE")]
    [InlineData("")]
    public void Casing_is_culture_invariant(string culture)
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo(culture);
            Assert.Equal("title_id", Casing.Snake("TitleID"));
            Assert.Equal("TitleId", Casing.Pascal("title_id"));
            Assert.Equal("INVOICE_ITEM", Casing.UpperSnake("invoiceItem"));
            Assert.Equal("Item", Casing.Capitalize("item"));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }
}
