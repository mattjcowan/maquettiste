using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Maquettiste.Engine.Json;

/// <summary>RFC 6901 JSON pointer helpers over <see cref="JsonElement"/> and <see cref="JsonNode"/> trees.</summary>
internal static class JsonPointer
{
    /// <summary>Splits a pointer into unescaped segments; <c>""</c> is the root.</summary>
    /// <param name="pointer">The pointer.</param>
    /// <param name="segments">The segments when well formed.</param>
    /// <returns><see langword="true"/> when the pointer is empty or starts with <c>/</c>.</returns>
    public static bool TryParse(string? pointer, [NotNullWhen(true)] out string[]? segments)
    {
        if (pointer is null)
        {
            segments = null;
            return false;
        }

        if (pointer.Length == 0)
        {
            segments = [];
            return true;
        }

        if (pointer[0] != '/')
        {
            segments = null;
            return false;
        }

        segments = pointer[1..].Split('/').Select(Unescape).ToArray();
        return true;
    }

    /// <summary>Escapes one segment (<c>~</c> → <c>~0</c>, <c>/</c> → <c>~1</c>).</summary>
    /// <param name="segment">The segment.</param>
    /// <returns>The escaped segment.</returns>
    public static string Escape(string segment) =>
        segment.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);

    /// <summary>Builds a pointer from segments.</summary>
    /// <param name="segments">The unescaped segments.</param>
    /// <returns>The pointer.</returns>
    public static string Build(IEnumerable<string> segments)
    {
        var sb = new StringBuilder();
        foreach (var s in segments)
            sb.Append('/').Append(Escape(s));
        return sb.ToString();
    }

    /// <summary>Resolves a pointer in an element tree.</summary>
    /// <param name="root">The root.</param>
    /// <param name="pointer">The pointer.</param>
    /// <param name="value">The value when found.</param>
    /// <returns><see langword="true"/> when the pointer resolves.</returns>
    public static bool TryGet(JsonElement root, string pointer, out JsonElement value)
    {
        value = default;
        if (!TryParse(pointer, out var segments))
            return false;
        var current = root;
        foreach (var segment in segments)
        {
            if (current.ValueKind == JsonValueKind.Object && current.TryGetProperty(segment, out var child))
                current = child;
            else if (current.ValueKind == JsonValueKind.Array && TryIndex(segment, out var index) && index < current.GetArrayLength())
                current = current[index];
            else
                return false;
        }

        value = current;
        return true;
    }

    /// <summary>Resolves the parent of a pointer's target in a node tree.</summary>
    /// <param name="root">The root.</param>
    /// <param name="pointer">A non-root pointer.</param>
    /// <param name="parent">The parent container when found.</param>
    /// <param name="last">The last segment.</param>
    /// <returns><see langword="true"/> when the parent resolves to an object or array.</returns>
    public static bool TryGetParent(JsonNode root, string pointer, [NotNullWhen(true)] out JsonNode? parent, [NotNullWhen(true)] out string? last)
    {
        parent = null;
        last = null;
        if (!TryParse(pointer, out var segments) || segments.Length == 0)
            return false;
        JsonNode? current = root;
        foreach (var segment in segments[..^1])
        {
            current = current switch
            {
                JsonObject o when o.TryGetPropertyValue(segment, out var child) => child,
                JsonArray a when TryIndex(segment, out var index) && index < a.Count => a[index],
                _ => null,
            };
            if (current is null)
                return false;
        }

        if (current is not (JsonObject or JsonArray))
            return false;
        parent = current;
        last = segments[^1];
        return true;
    }

    /// <summary>Parses an array index segment.</summary>
    /// <param name="segment">The segment.</param>
    /// <param name="index">The index.</param>
    /// <returns><see langword="true"/> for a canonical non-negative integer.</returns>
    public static bool TryIndex(string segment, out int index)
    {
        index = -1;
        return segment.Length > 0 && (segment.Length == 1 || segment[0] != '0')
            && int.TryParse(segment, NumberStyles.None, CultureInfo.InvariantCulture, out index);
    }

    private static string Unescape(string segment) =>
        segment.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
}
