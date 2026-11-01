using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Loading;

namespace Maquettiste.Engine.Json;

/// <summary>
/// RFC 6902 JSON Patch over <see cref="JsonNode"/> trees (next-phase-1-design.md section 5.2, decision 3): <c>add</c>, <c>remove</c>,
/// <c>replace</c>, <c>move</c>, <c>copy</c> and <c>test</c>, applied in order to a clone, all or nothing. Paths are RFC 6901 pointers
/// (<see cref="JsonPointer"/>); numbers in <c>test</c> compare by <see cref="CanonicalJson.CanonicalNumber"/>, so <c>1</c> and
/// <c>1.0</c> are equal. A failure is one MQ1016 diagnostic at <c>/&lt;operation index&gt;</c> (<c>""</c> when the patch itself is not
/// an array of operations), and the document is left as it was.
/// </summary>
internal static class JsonPatch
{
    /// <summary>Applies a patch given as UTF-8 JSON text (a byte order mark is dropped; duplicated member names are refused).</summary>
    /// <param name="document">The document; never changed.</param>
    /// <param name="patch">The patch text: a JSON array of operations.</param>
    /// <param name="fixedMembers">Top-level members no operation may change (the element's <c>id</c> and <c>kind</c>).</param>
    /// <param name="elementId">The element id the diagnostic names, if any.</param>
    /// <param name="result">The patched clone when the patch applies.</param>
    /// <param name="error">The MQ1016 diagnostic when it does not.</param>
    /// <returns><see langword="true"/> when every operation applied.</returns>
    public static bool TryApply(JsonNode? document, ReadOnlySpan<byte> patch, IReadOnlyCollection<string> fixedMembers, string? elementId,
        out JsonNode? result, [NotNullWhen(false)] out Diagnostic? error)
    {
        JsonNode? operations;
        try
        {
            var bytes = patch.ToArray();
            operations = JsonNode.Parse(DocumentReader.StripBom(bytes).Span, documentOptions: DocumentReader.ParseOptions);
        }
        catch (JsonException ex)
        {
            result = null;
            error = Error(elementId, "", "The patch is not valid JSON: " + ex.Message);
            return false;
        }

        return TryApply(document, operations, fixedMembers, elementId, out result, out error);
    }

    /// <summary>Applies a patch given as a node.</summary>
    /// <param name="document">The document; never changed.</param>
    /// <param name="patch">The patch: a JSON array of operations.</param>
    /// <param name="fixedMembers">Top-level members no operation may change.</param>
    /// <param name="elementId">The element id the diagnostic names, if any.</param>
    /// <param name="result">The patched clone when the patch applies.</param>
    /// <param name="error">The MQ1016 diagnostic when it does not.</param>
    /// <returns><see langword="true"/> when every operation applied.</returns>
    public static bool TryApply(JsonNode? document, JsonNode? patch, IReadOnlyCollection<string> fixedMembers, string? elementId,
        out JsonNode? result, [NotNullWhen(false)] out Diagnostic? error)
    {
        ArgumentNullException.ThrowIfNull(fixedMembers);
        result = null;
        if (patch is not JsonArray operations)
        {
            error = Error(elementId, "", "The patch is not a JSON array of operations.");
            return false;
        }

        var root = document?.DeepClone();
        for (var i = 0; i < operations.Count; i++)
        {
            var pointer = "/" + i.ToString(CultureInfo.InvariantCulture);
            if (Apply(ref root, operations[i]) is { } failure)
            {
                error = Error(elementId, pointer, $"Operation {i.ToString(CultureInfo.InvariantCulture)}{Describe(operations[i])}: {failure}");
                return false;
            }

            foreach (var member in fixedMembers.Order(StringComparer.Ordinal))
            {
                if (!SameMember(document, root, member))
                {
                    error = Error(elementId, pointer,
                        $"Operation {i.ToString(CultureInfo.InvariantCulture)}{Describe(operations[i])}: it changes /{JsonPointer.Escape(member)}, which a patch keeps.");
                    return false;
                }
            }
        }

        result = root;
        error = null;
        return true;
    }

    /// <summary>Whether two JSON values are equal: objects by members in any order, arrays item by item, numbers by canonical text.</summary>
    /// <param name="a">One value.</param>
    /// <param name="b">The other.</param>
    /// <returns><see langword="true"/> when equal.</returns>
    public static bool ValuesEqual(JsonNode? a, JsonNode? b)
    {
        var kindA = a?.GetValueKind() ?? JsonValueKind.Null;
        var kindB = b?.GetValueKind() ?? JsonValueKind.Null;
        if (kindA != kindB)
            return false;
        switch (kindA)
        {
            case JsonValueKind.Object:
                var objectA = (JsonObject)a!;
                var objectB = (JsonObject)b!;
                if (objectA.Count != objectB.Count)
                    return false;
                foreach (var (name, value) in objectA)
                {
                    if (!objectB.TryGetPropertyValue(name, out var other) || !ValuesEqual(value, other))
                        return false;
                }

                return true;
            case JsonValueKind.Array:
                var arrayA = (JsonArray)a!;
                var arrayB = (JsonArray)b!;
                if (arrayA.Count != arrayB.Count)
                    return false;
                for (var i = 0; i < arrayA.Count; i++)
                {
                    if (!ValuesEqual(arrayA[i], arrayB[i]))
                        return false;
                }

                return true;
            case JsonValueKind.String:
                return string.Equals(a!.GetValue<string>(), b!.GetValue<string>(), StringComparison.Ordinal);
            case JsonValueKind.Number:
                return string.Equals(CanonicalJson.CanonicalNumber(a!.ToJsonString()), CanonicalJson.CanonicalNumber(b!.ToJsonString()), StringComparison.Ordinal);
            default:
                return true; // true, false and null: the kind is the value
        }
    }

    /// <summary>Applies one operation to <paramref name="root"/>; returns why it cannot, or <see langword="null"/>.</summary>
    private static string? Apply(ref JsonNode? root, JsonNode? operation)
    {
        if (operation is not JsonObject o)
            return "an operation must be a JSON object.";
        if (!TryString(o, "op", out var op))
            return "op is required: add, remove, replace, move, copy or test.";
        if (!TryString(o, "path", out var path))
            return "path is required: a JSON pointer string.";
        if (!JsonPointer.TryParse(path, out var segments))
            return $"path '{path}' is not a JSON pointer (\"\" or starting with /).";
        string[]? from = null;
        if (op is "move" or "copy")
        {
            if (!TryString(o, "from", out var fromText))
                return "from is required: a JSON pointer string.";
            if (!JsonPointer.TryParse(fromText, out from))
                return $"from '{fromText}' is not a JSON pointer (\"\" or starting with /).";
        }

        JsonNode? value = null;
        if (op is "add" or "replace" or "test")
        {
            if (!o.TryGetPropertyValue("value", out value))
                return "value is required.";
        }

        switch (op)
        {
            case "add":
                return Add(ref root, segments, value?.DeepClone());
            case "remove":
                return Remove(ref root, segments, out _);
            case "replace":
                return Replace(ref root, segments, value?.DeepClone());
            case "move":
                if (from!.SequenceEqual(segments, StringComparer.Ordinal))
                    return Locate(root, from!, out _) ? null : $"from {JsonPointer.Build(from!)} does not resolve.";
                if (from!.Length < segments.Length && segments.Take(from!.Length).SequenceEqual(from!, StringComparer.Ordinal))
                    return $"from {JsonPointer.Build(from!)} is a proper prefix of path {path}: a value cannot move into itself.";
                if (!Locate(root, from!, out _))
                    return $"from {JsonPointer.Build(from!)} does not resolve.";
                // Remove, then add at path as the tree is after the removal (RFC 6902 section 4.4); a failure discards the whole clone.
                return Remove(ref root, from!, out var moved) ?? Add(ref root, segments, moved);
            case "copy":
                if (!Locate(root, from!, out var copied))
                    return $"from {JsonPointer.Build(from!)} does not resolve.";
                return Add(ref root, segments, copied?.DeepClone());
            case "test":
                if (!Locate(root, segments, out var actual))
                    return $"path {path} does not resolve.";
                return ValuesEqual(actual, value) ? null : $"the test failed: {path} holds {Text(actual)}, not {Text(value)}.";
            default:
                return $"op '{op}' is not add, remove, replace, move, copy or test.";
        }
    }

    private static string? Add(ref JsonNode? root, string[] segments, JsonNode? value)
    {
        if (segments.Length == 0)
        {
            root = value;
            return null;
        }

        if (!Locate(root, segments[..^1], out var parent) || parent is not (JsonObject or JsonArray))
            return $"the parent of {JsonPointer.Build(segments)} does not resolve to an object or an array.";
        var last = segments[^1];
        if (parent is JsonObject o)
        {
            o[last] = value;
            return null;
        }

        var array = (JsonArray)parent;
        if (last == "-")
        {
            array.Add(value);
            return null;
        }

        if (!JsonPointer.TryIndex(last, out var index))
            return $"'{last}' in {JsonPointer.Build(segments)} is not an array index or -.";
        if (index > array.Count)
            return $"index {last} in {JsonPointer.Build(segments)} is out of range: the array has {array.Count.ToString(CultureInfo.InvariantCulture)} items.";
        array.Insert(index, value);
        return null;
    }

    private static string? Remove(ref JsonNode? root, string[] segments, out JsonNode? removed)
    {
        removed = null;
        if (segments.Length == 0)
            return "path \"\" would remove the whole document.";
        if (!Locate(root, segments[..^1], out var parent) || parent is not (JsonObject or JsonArray))
            return $"path {JsonPointer.Build(segments)} does not resolve.";
        var last = segments[^1];
        if (parent is JsonObject o)
        {
            if (!o.TryGetPropertyValue(last, out removed))
                return $"path {JsonPointer.Build(segments)} does not resolve.";
            o.Remove(last);
            return null;
        }

        var array = (JsonArray)parent;
        if (IndexProblem(array, last, segments) is { } problem)
            return problem;
        var index = int.Parse(last, NumberStyles.None, CultureInfo.InvariantCulture);
        removed = array[index];
        array.RemoveAt(index);
        return null;
    }

    private static string? Replace(ref JsonNode? root, string[] segments, JsonNode? value)
    {
        if (segments.Length == 0)
        {
            root = value;
            return null;
        }

        if (!Locate(root, segments[..^1], out var parent) || parent is not (JsonObject or JsonArray))
            return $"path {JsonPointer.Build(segments)} does not resolve.";
        var last = segments[^1];
        if (parent is JsonObject o)
        {
            if (!o.ContainsKey(last))
                return $"path {JsonPointer.Build(segments)} does not resolve.";
            o[last] = value;
            return null;
        }

        var array = (JsonArray)parent;
        if (IndexProblem(array, last, segments) is { } problem)
            return problem;
        array[int.Parse(last, NumberStyles.None, CultureInfo.InvariantCulture)] = value;
        return null;
    }

    /// <summary>Why a segment does not name an existing item of an array, or <see langword="null"/>.</summary>
    private static string? IndexProblem(JsonArray array, string segment, string[] segments)
    {
        if (!JsonPointer.TryIndex(segment, out var index))
            return $"'{segment}' in {JsonPointer.Build(segments)} is not an array index.";
        return index < array.Count ? null
            : $"index {segment} in {JsonPointer.Build(segments)} is out of range: the array has {array.Count.ToString(CultureInfo.InvariantCulture)} items.";
    }

    /// <summary>Finds the value a pointer names; a JSON null that exists is found.</summary>
    private static bool Locate(JsonNode? root, string[] segments, out JsonNode? value)
    {
        value = root;
        foreach (var segment in segments)
        {
            switch (value)
            {
                case JsonObject o when o.TryGetPropertyValue(segment, out var child):
                    value = child;
                    break;
                case JsonArray a when JsonPointer.TryIndex(segment, out var index) && index < a.Count:
                    value = a[index];
                    break;
                default:
                    value = null;
                    return false;
            }
        }

        return true;
    }

    private static bool SameMember(JsonNode? before, JsonNode? after, string member)
    {
        var had = before is JsonObject b && b.ContainsKey(member);
        var has = after is JsonObject a && a.ContainsKey(member);
        return had == has && (!had || ValuesEqual(before![member], after![member]));
    }

    private static bool TryString(JsonObject o, string name, [NotNullWhen(true)] out string? value)
    {
        value = o.TryGetPropertyValue(name, out var node) && node?.GetValueKind() == JsonValueKind.String ? node.GetValue<string>() : null;
        return value is not null;
    }

    /// <summary>The operation as <c> (op path)</c>, when it has them, for messages.</summary>
    private static string Describe(JsonNode? operation) =>
        operation is JsonObject o && TryString(o, "op", out var op) ? TryString(o, "path", out var path) ? $" ({op} {path})" : $" ({op})" : "";

    private static string Text(JsonNode? value)
    {
        var text = value?.ToJsonString() ?? "null";
        return text.Length <= 80 ? text : string.Concat(text.AsSpan(0, 77), "...");
    }

    private static Diagnostic Error(string? elementId, string pointer, string message) => RuleCatalog.Create("MQ1016", message, elementId, null, pointer);
}
