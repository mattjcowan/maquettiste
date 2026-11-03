using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Maquettiste.Engine;

/// <summary>
/// The review form of a model comparison (next-phase-2-design.md section 6.2, decision 6), one renderer for the command line and the
/// MCP server so both print the same bytes. It renders a comparison made with <see cref="SnapshotCompareOptions.ByPackage"/> and
/// <see cref="SnapshotCompareOptions.FieldChanges"/>: a heading naming both sides, one counts line, a section per group (packages by
/// qualified name, "(no package)", then the migration facts of each database), one line per element (change, kind, name, id) with one
/// nested line per field change (at most <see cref="MaxFieldLines"/>), then the other files on the last page. Values are compact JSON
/// cut at <see cref="MaxValueLength"/> characters. Text is the same lines without markdown marks.
/// </summary>
public static class ModelDiffFormat
{
    /// <summary>The most field lines under one element; the rest are counted.</summary>
    public const int MaxFieldLines = 20;

    /// <summary>The longest value shown, in characters, before <c>...</c>.</summary>
    public const int MaxValueLength = 80;

    private static readonly JsonWriterOptions Compact = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, Indented = false };

    /// <summary>The comparison as markdown, for a review comment or a change log.</summary>
    /// <param name="comparison">A comparison made with <c>ByPackage</c> and <c>FieldChanges</c> (one page, or every page joined).</param>
    /// <returns>The markdown, lines ending in <c>\n</c>.</returns>
    /// <exception cref="ArgumentException">The comparison was not made with ByPackage and FieldChanges.</exception>
    public static string Markdown(SnapshotComparison comparison) => Render(comparison, markdown: true);

    /// <summary>The comparison as plain text: the markdown's lines without its marks.</summary>
    /// <param name="comparison">A comparison made with <c>ByPackage</c> and <c>FieldChanges</c>.</param>
    /// <returns>The text, lines ending in <c>\n</c>.</returns>
    /// <exception cref="ArgumentException">The comparison was not made with ByPackage and FieldChanges.</exception>
    public static string Text(SnapshotComparison comparison) => Render(comparison, markdown: false);

    /// <summary>One element's detail as markdown: its line, then every field change it lists.</summary>
    /// <param name="diff">The detail (<see cref="SnapshotLibrary.CompareElementAsync(string, string, string, SnapshotCompareOptions?, CancellationToken)"/>).</param>
    /// <returns>The markdown, lines ending in <c>\n</c>.</returns>
    public static string Markdown(SnapshotElementDiff diff) => Render(diff, markdown: true);

    /// <summary>One element's detail as plain text.</summary>
    /// <param name="diff">The detail.</param>
    /// <returns>The text, lines ending in <c>\n</c>.</returns>
    public static string Text(SnapshotElementDiff diff) => Render(diff, markdown: false);

    private static string Render(SnapshotElementDiff diff, bool markdown)
    {
        ArgumentNullException.ThrowIfNull(diff);
        var w = new Writer(markdown);
        var row = new SnapshotElementChange(diff.Id, diff.Kind, diff.Name, diff.Change, diff.ToPath ?? diff.FromPath ?? "");
        w.Item(1, Head(w, row));
        foreach (var field in diff.Fields)
            w.Item(2, Field(w, field));
        if (diff.FieldsTruncated)
            w.Item(2, "and more fields");
        return w.ToString();
    }

    private static string Render(SnapshotComparison comparison, bool markdown)
    {
        ArgumentNullException.ThrowIfNull(comparison);
        const string Needs = "The comparison was not made with ByPackage and FieldChanges.";
        if (comparison.Packages is not { } groups || comparison.Elements.Any(e => e.Change == "changed" && e.Fields is null))
            throw new ArgumentException(Needs, nameof(comparison));
        var total = comparison.Added + comparison.Removed + comparison.Changed;
        var offset = (comparison.Next ?? total) - comparison.Elements.Count;
        if (offset < 0 || groups.Sum(g => g.Added + g.Removed + g.Changed) != total)
            throw new ArgumentException(Needs, nameof(comparison));

        var w = new Writer(markdown);
        w.Line((markdown ? "# " : "") + "Model diff: " + w.Code(comparison.From) + " to " + w.Code(comparison.To));
        w.Line();
        w.Line(string.Create(CultureInfo.InvariantCulture,
            $"{comparison.Added} added, {comparison.Removed} removed, {comparison.Changed} changed."));
        if (offset > 0 || comparison.Next is not null)
        {
            w.Line(string.Create(CultureInfo.InvariantCulture, $"Elements {offset + 1} to {offset + comparison.Elements.Count} of {total}")
                + (comparison.Next is { } next ? "; the next page starts at " + next.ToString(CultureInfo.InvariantCulture) + "." : "."));
        }

        var start = 0;
        foreach (var group in groups)
        {
            var count = group.Added + group.Removed + group.Changed;
            var from = Math.Max(start, offset);
            var to = Math.Min(start + count, offset + comparison.Elements.Count);
            start += count;
            if (from >= to)
                continue;
            w.Line();
            w.Line((markdown ? "## " : "") + group.Name);
            w.Line();
            for (var i = from; i < to; i++)
                Element(w, comparison.Elements[i - offset]);
        }

        if (comparison.Next is null && comparison.Files.Count > 0)
        {
            w.Line();
            w.Line((markdown ? "## " : "") + "Other files");
            w.Line();
            foreach (var file in comparison.Files)
                w.Item(1, file.Change + " " + w.Code(file.Path));
            if (comparison.FilesTruncated)
                w.Item(1, "and more files");
        }

        return w.ToString();
    }

    private static string Head(Writer w, SnapshotElementChange element)
    {
        var text = new StringBuilder(element.Change).Append(' ').Append(element.Kind);
        if ((element.Label ?? (element.Name.Length > 0 ? element.Name : null)) is { } name)
            text.Append(' ').Append(w.Code(name));
        text.Append(" (").Append(w.Code(element.Id)).Append(')');
        if (element.PreviousName is { } previous)
            text.Append(", renamed from ").Append(w.Code(previous));
        return text.ToString();
    }

    private static void Element(Writer w, SnapshotElementChange element)
    {
        w.Item(1, Head(w, element));
        if (element.Fields is not { } fields)
            return;
        foreach (var field in fields.Take(MaxFieldLines))
            w.Item(2, Field(w, field));
        if (fields.Count > MaxFieldLines)
        {
            var more = (fields.Count - MaxFieldLines).ToString(CultureInfo.InvariantCulture);
            w.Item(2, element.FieldsTruncated ? "and over " + more + " more fields" : "and " + more + " more fields");
        }
    }

    private static string Field(Writer w, SnapshotFieldChange field)
    {
        var parts = new List<string>(2);
        if (field.SubId is { } id)
            parts.Add((field.SubKind ?? "sub-element") + " " + w.Code(field.SubName ?? id));
        var pointer = field.SubId is null ? field.Pointer : field.SubPointer ?? "";
        if (pointer.Length > 0)
            parts.Add(w.Code(pointer));
        var head = parts.Count == 0 ? "the document" : string.Join(", ", parts);
        return field.Change switch
        {
            "added" => head + " added: " + w.Code(Value(field.After)),
            "removed" => head + " removed: " + w.Code(Value(field.Before)),
            _ => head + " from " + w.Code(Value(field.Before)) + " to " + w.Code(Value(field.After)),
        };
    }

    /// <summary>A value as compact JSON, cut at <see cref="MaxValueLength"/> characters with <c>...</c>.</summary>
    private static string Value(JsonElement? value)
    {
        if (value is not { } json)
            return "null";
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, Compact))
            json.WriteTo(writer);
        var text = Encoding.UTF8.GetString(buffer.ToArray());
        if (text.Length <= MaxValueLength)
            return text;
        var cut = char.IsHighSurrogate(text[MaxValueLength - 1]) ? MaxValueLength - 1 : MaxValueLength;
        return text[..cut] + "...";
    }

    /// <summary>The lines: markdown's bullets and code spans, or the same lines indented without them.</summary>
    private sealed class Writer(bool markdown)
    {
        private readonly StringBuilder _text = new();

        public void Line(string line = "") => _text.Append(line).Append('\n');

        public void Item(int depth, string text) =>
            Line(markdown ? new string(' ', (depth - 1) * 2) + "- " + text : new string(' ', depth * 2) + text);

        /// <summary>A code span; one that holds a backtick is wrapped in double backticks.</summary>
        public string Code(string text)
        {
            if (!markdown)
                return text;
            return text.Contains('`', StringComparison.Ordinal) ? "`` " + text + " ``" : "`" + text + "`";
        }

        public override string ToString() => _text.ToString();
    }
}
