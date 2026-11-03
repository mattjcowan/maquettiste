using System.Text;
using System.Text.Json.Nodes;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Json;

namespace Maquettiste.Engine.Tests.Json;

/// <summary>The in-house RFC 6902 JSON Patch (next-phase-1-design.md section 5.2, decision 3).</summary>
public sealed class JsonPatchTests
{
    /// <summary>RFC 6902 appendix A: document, patch, result (null where the patch fails).</summary>
    public static TheoryData<string, string, string, string?> AppendixA => new()
    {
        { "A.1", """{ "foo": "bar" }""", """[ { "op": "add", "path": "/baz", "value": "qux" } ]""", """{ "baz": "qux", "foo": "bar" }""" },
        { "A.2", """{ "foo": [ "bar", "baz" ] }""", """[ { "op": "add", "path": "/foo/1", "value": "qux" } ]""", """{ "foo": [ "bar", "qux", "baz" ] }""" },
        { "A.3", """{ "baz": "qux", "foo": "bar" }""", """[ { "op": "remove", "path": "/baz" } ]""", """{ "foo": "bar" }""" },
        { "A.4", """{ "foo": [ "bar", "qux", "baz" ] }""", """[ { "op": "remove", "path": "/foo/1" } ]""", """{ "foo": [ "bar", "baz" ] }""" },
        { "A.5", """{ "baz": "qux", "foo": "bar" }""", """[ { "op": "replace", "path": "/baz", "value": "boo" } ]""", """{ "baz": "boo", "foo": "bar" }""" },
        {
            "A.6", """{ "foo": { "bar": "baz", "waldo": "fred" }, "qux": { "corge": "grault" } }""",
            """[ { "op": "move", "from": "/foo/waldo", "path": "/qux/thud" } ]""",
            """{ "foo": { "bar": "baz" }, "qux": { "corge": "grault", "thud": "fred" } }"""
        },
        {
            "A.7", """{ "foo": [ "all", "grass", "cows", "eat" ] }""", """[ { "op": "move", "from": "/foo/1", "path": "/foo/3" } ]""",
            """{ "foo": [ "all", "cows", "eat", "grass" ] }"""
        },
        {
            "A.8", """{ "baz": "qux", "foo": [ "a", 2, "c" ] }""",
            """[ { "op": "test", "path": "/baz", "value": "qux" }, { "op": "test", "path": "/foo/1", "value": 2 } ]""",
            """{ "baz": "qux", "foo": [ "a", 2, "c" ] }"""
        },
        { "A.9", """{ "baz": "qux" }""", """[ { "op": "test", "path": "/baz", "value": "bar" } ]""", null },
        { "A.10", """{ "foo": "bar" }""", """[ { "op": "add", "path": "/child", "value": { "grandchild": { } } } ]""", """{ "foo": "bar", "child": { "grandchild": { } } }""" },
        { "A.11", """{ "foo": "bar" }""", """[ { "op": "add", "path": "/baz", "value": "qux", "xyz": 123 } ]""", """{ "foo": "bar", "baz": "qux" }""" },
        { "A.12", """{ "foo": "bar" }""", """[ { "op": "add", "path": "/baz/bat", "value": "qux" } ]""", null },
        { "A.13", """{ "foo": "bar" }""", """[ { "op": "add", "path": "/baz", "value": "qux", "op": "remove" } ]""", null },
        { "A.14", """{ "/": 9, "~1": 10 }""", """[ { "op": "test", "path": "/~01", "value": 10 } ]""", """{ "/": 9, "~1": 10 }""" },
        { "A.15", """{ "/": 9, "~1": 10 }""", """[ { "op": "test", "path": "/~01", "value": "10" } ]""", null },
        { "A.16", """{ "foo": [ "bar" ] }""", """[ { "op": "add", "path": "/foo/-", "value": [ "abc", "def" ] } ]""", """{ "foo": [ "bar", [ "abc", "def" ] ] }""" },
    };

    [Theory]
    [MemberData(nameof(AppendixA))]
    public void Applies_the_rfc6902_appendix_a_examples(string example, string document, string patch, string? expected)
    {
        var target = JsonNode.Parse(document);
        var before = target!.ToJsonString();

        var applied = JsonPatch.TryApply(target, Encoding.UTF8.GetBytes(patch), [], null, out var result, out var error);

        Assert.Equal(before, target.ToJsonString()); // the document itself never changes
        if (expected is null)
        {
            Assert.False(applied, example);
            Assert.Equal("MQ1016", error!.Rule);
            Assert.Null(result);
        }
        else
        {
            Assert.True(applied, example + ": " + error?.Message);
            Assert.True(JsonNode.DeepEquals(JsonNode.Parse(expected), result), $"{example}: {result?.ToJsonString()}");
        }
    }

    [Fact]
    public void A_failed_test_or_missing_path_is_MQ1016_and_changes_nothing()
    {
        var document = JsonNode.Parse("""{ "kind": "table", "id": "01K6BND0000000000000000001", "name": "notes", "columns": [ { "name": "id" }, { "name": "body" } ], "size": 1.50 }""")!;
        var before = document.ToJsonString();

        Diagnostic Fails(string patch, string pointer)
        {
            Assert.False(JsonPatch.TryApply(document, Encoding.UTF8.GetBytes(patch), ["id", "kind"], "01K6BND0000000000000000001", out var result, out var error), patch);
            Assert.Null(result);
            Assert.Equal(("MQ1016", DiagnosticSeverity.Error, pointer, "01K6BND0000000000000000001"), (error.Rule, error.Severity, error.JsonPointer, error.ElementId));
            Assert.Equal(before, document.ToJsonString());
            return error;
        }

        // All or nothing: the first operation applied to the clone, the second failed, and the document is as it was.
        var failedTest = Fails("""[ { "op": "replace", "path": "/name", "value": "memos" }, { "op": "test", "path": "/name", "value": "notes" } ]""", "/1");
        Assert.Contains("the test failed", failedTest.Message, StringComparison.Ordinal);
        Assert.Contains("does not resolve", Fails("""[ { "op": "remove", "path": "/columns/0/type" } ]""", "/0").Message, StringComparison.Ordinal);
        Fails("""[ { "op": "add", "path": "/columns/3", "value": { "name": "late" } } ]""", "/0");
        Fails("""[ { "op": "replace", "path": "/columns/2/name", "value": "x" } ]""", "/0");
        Fails("""[ { "op": "replace", "path": "/columns/-", "value": "x" } ]""", "/0");
        Fails("""[ { "op": "add", "path": "/columns/01", "value": "x" } ]""", "/0");
        Fails("""[ { "op": "test", "path": "/missing", "value": null } ]""", "/0");
        Fails("""[ { "op": "rename", "path": "/name" } ]""", "/0");
        Fails("""[ { "op": "add", "path": "/name" } ]""", "/0");
        Fails("""[ { "op": "add", "path": "name", "value": "x" } ]""", "/0");
        Fails("""[ { "op": "move", "path": "/name" } ]""", "/0");
        Fails("""[ { "op": "move", "from": "/columns", "path": "/columns/0/all" } ]""", "/0");
        Fails("""[ "add" ]""", "/0");
        Fails("""{ "op": "add" }""", "");
        Fails("""[ { "op": "add" """, "");

        // A patch keeps the element's identity: id and kind cannot change, by any operation.
        Assert.Contains("/id", Fails("""[ { "op": "test", "path": "/name", "value": "notes" }, { "op": "replace", "path": "/id", "value": "01K6BND0000000000000000009" } ]""", "/1").Message, StringComparison.Ordinal);
        Fails("""[ { "op": "remove", "path": "/kind" } ]""", "/0");
        Fails("""[ { "op": "move", "from": "/id", "path": "/oldId" } ]""", "/0");
        Fails("""[ { "op": "replace", "path": "", "value": { "kind": "view", "id": "01K6BND0000000000000000001" } } ]""", "/0");
        Fails("""[ { "op": "remove", "path": "" } ]""", "/0");

        // Numbers compare by value in test; an id written back unchanged is no change; moving an item to itself is allowed.
        Assert.True(JsonPatch.TryApply(document, """
            [ { "op": "test", "path": "/size", "value": 1.5 }, { "op": "test", "path": "/size", "value": 15e-1 },
              { "op": "replace", "path": "/id", "value": "01K6BND0000000000000000001" }, { "op": "move", "from": "/columns/1", "path": "/columns/1" },
              { "op": "copy", "from": "/columns/0", "path": "/columns/-" }, { "op": "test", "path": "/columns/2", "value": { "name": "id" } } ]
            """u8, ["id", "kind"], null, out var patched, out var none), none?.Message);
        Assert.Equal(3, patched!["columns"]!.AsArray().Count);
        Assert.Equal(before, document.ToJsonString());
    }
}
