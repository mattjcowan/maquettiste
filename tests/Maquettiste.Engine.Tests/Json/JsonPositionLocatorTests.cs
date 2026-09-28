using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Maquettiste.Engine.Json;

namespace Maquettiste.Engine.Tests.Json;

public sealed class JsonPositionLocatorTests
{
    private const string Document = """
        {
          "kind": "entity",
          "name": "Café",
          "a/b": { "c~d": 1 },
          "attributes": [
            { "id": "X", "name": "first" },
            {
              "name": "second"
            }
          ],
          "tags": []
        }
        """;

    private static readonly byte[] Bytes = Encoding.UTF8.GetBytes(Document.Replace("\r\n", "\n", StringComparison.Ordinal));
    private readonly JsonPositionLocator _locator = new();

    [Theory]
    [InlineData("", 1, 1)]
    [InlineData("/kind", 2, 11)]
    [InlineData("/name", 3, 11)]
    [InlineData("/a~1b", 4, 10)]
    [InlineData("/a~1b/c~0d", 4, 19)]
    [InlineData("/attributes", 5, 17)]
    [InlineData("/attributes/0", 6, 5)]
    [InlineData("/attributes/0/name", 6, 26)]
    [InlineData("/attributes/1/name", 8, 15)]
    [InlineData("/tags", 11, 11)]
    public void Locates_values_by_pointer(string pointer, int line, int column) =>
        Assert.Equal((line, column), _locator.Locate(Bytes, pointer));

    [Theory]
    [InlineData("/missing")]
    [InlineData("/attributes/2")]
    [InlineData("/attributes/01")]
    [InlineData("/attributes/x")]
    [InlineData("/kind/deeper")]
    [InlineData("no-slash")]
    public void Returns_null_for_pointers_that_do_not_resolve(string pointer) => Assert.Null(_locator.Locate(Bytes, pointer));

    [Fact]
    public void Columns_count_characters_not_bytes_and_a_bom_is_skipped()
    {
        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes("{ \"é\": \"ü\", \"x\": 1 }")).ToArray();
        Assert.Equal((1, 18), _locator.Locate(bytes, "/x"));
        Assert.Equal((1, 8), _locator.Locate(bytes, "/é"));
    }

    [Fact]
    public void Positions_a_parse_error_from_its_exception()
    {
        var bytes = Encoding.UTF8.GetBytes("{\n  \"é\": \"ü\",\n  \"x\": oops\n}");
        var ex = Assert.ThrowsAny<JsonException>(() => JsonNode.Parse(bytes));
        Assert.Equal((3, 8), _locator.FromException(bytes, ex));
    }

    [Fact]
    public void Truncated_json_before_the_pointer_yields_null() =>
        Assert.Null(_locator.Locate(Encoding.UTF8.GetBytes("{ \"a\": [1, 2"), "/b"));
}
