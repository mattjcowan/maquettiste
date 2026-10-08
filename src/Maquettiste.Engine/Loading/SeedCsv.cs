using System.Globalization;
using System.Text;
using System.Text.Json;
using Maquettiste.Engine.Model;

namespace Maquettiste.Engine.Loading;

/// <summary>
/// The CSV form of a seed's rows (<c>rowsFrom</c>, 2026-10-07): a header of <c>@id</c> and the seed's column ids, then one row per line,
/// UTF-8 without a byte order mark, LF line ends, comma separated, RFC 4180 quoting. A cell keeps its JSON type: a quoted cell is always
/// text; an unquoted one is a JSON number, <c>true</c>, <c>false</c>, an array or an object when it parses as one, else text; an empty
/// unquoted cell is null; an array or an object is written as quoted JSON text and read back as JSON (so a text cell that is itself
/// valid JSON array or object text reads as that array or object). The writer quotes text whenever its unquoted form would read back
/// as anything else, so the same rows always give the same bytes and a read gives back the rows written.
/// </summary>
public static class SeedCsv
{
    private static readonly UTF8Encoding Utf8 = new(false);

    /// <summary>Writes rows in the canonical CSV form.</summary>
    /// <param name="columns">The seed's columns, in order.</param>
    /// <param name="rows">The rows.</param>
    /// <returns>The bytes.</returns>
    public static byte[] Write(IReadOnlyList<string> columns, IReadOnlyList<SeedRow> rows)
    {
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(rows);
        var text = new StringBuilder();
        text.Append("@id");
        foreach (var column in columns)
            text.Append(',').Append(Quote(column, force: false));
        text.Append('\n');
        foreach (var row in rows)
        {
            text.Append(Quote(row.Id, force: false));
            for (var k = 0; k < columns.Count; k++)
                text.Append(',').Append(k < row.Values.Count ? Cell(row.Values[k]) : "");
            text.Append('\n');
        }

        return Utf8.GetBytes(text.ToString());
    }

    /// <summary>Reads rows written by <see cref="Write"/> (or by hand, or by a spreadsheet saved as CSV).</summary>
    /// <param name="text">The file's text.</param>
    /// <param name="columns">The seed's columns: the header must name exactly these (in any order) after <c>@id</c>.</param>
    /// <param name="problem">Why the file could not be read; <see langword="null"/> when it was.</param>
    /// <returns>The rows, cells in the seed's column order (trailing nulls dropped).</returns>
    public static IReadOnlyList<SeedRow> Read(string text, IReadOnlyList<string> columns, out string? problem)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(columns);
        problem = null;
        var records = Parse(text.StartsWith('﻿') ? text[1..] : text, out var parseProblem);
        if (parseProblem is not null)
        {
            problem = parseProblem;
            return [];
        }

        if (records.Count == 0)
        {
            problem = "The rows file is empty: its first line names @id and the seed's columns.";
            return [];
        }

        var header = records[0].Select(f => f.Text).ToList();
        if (header.Count == 0 || header[0] != "@id")
        {
            problem = "The first column of the rows file is not @id.";
            return [];
        }

        var position = new int[columns.Count];
        for (var k = 0; k < columns.Count; k++)
        {
            position[k] = header.IndexOf(columns[k], 1);
            if (position[k] < 0)
            {
                problem = $"The rows file has no column '{columns[k]}', which the seed lists.";
                return [];
            }
        }

        if (header.Skip(1).FirstOrDefault(h => !columns.Contains(h, StringComparer.Ordinal)) is { } extra)
        {
            problem = $"The rows file has a column '{extra}' the seed does not list.";
            return [];
        }

        var rows = new List<SeedRow>(records.Count - 1);
        for (var r = 1; r < records.Count; r++)
        {
            var record = records[r];
            if (record.Count == 1 && record[0].Text.Length == 0 && !record[0].Quoted)
                continue; // a blank line
            if (record.Count != header.Count)
            {
                problem = $"Line {(r + 1).ToString(CultureInfo.InvariantCulture)} of the rows file has {record.Count.ToString(CultureInfo.InvariantCulture)} cells for {header.Count.ToString(CultureInfo.InvariantCulture)} columns.";
                return [];
            }

            var values = new JsonElement[columns.Count];
            for (var k = 0; k < columns.Count; k++)
                values[k] = Value(record[position[k]]);
            var count = values.Length;
            while (count > 0 && values[count - 1].ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                count--;
            rows.Add(new SeedRow { Id = record[0].Text, Values = values[..count] });
        }

        return rows;
    }

    private readonly record struct Field(string Text, bool Quoted);

    private static List<List<Field>> Parse(string text, out string? problem)
    {
        problem = null;
        var records = new List<List<Field>>();
        var record = new List<Field>();
        var field = new StringBuilder();
        var quoted = false;
        var inQuotes = false;
        var i = 0;
        var any = false;
        while (i < text.Length)
        {
            var c = text[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"')
                    {
                        field.Append('"');
                        i += 2;
                        continue;
                    }

                    inQuotes = false;
                    i++;
                    continue;
                }

                field.Append(c);
                i++;
                continue;
            }

            switch (c)
            {
                case '"' when field.Length == 0 && !quoted:
                    inQuotes = quoted = true;
                    any = true;
                    break;
                case ',':
                    record.Add(new Field(field.ToString(), quoted));
                    field.Clear();
                    quoted = false;
                    any = true;
                    break;
                case '\r':
                    break;
                case '\n':
                    record.Add(new Field(field.ToString(), quoted));
                    records.Add(record);
                    record = [];
                    field.Clear();
                    quoted = false;
                    any = false;
                    break;
                default:
                    field.Append(c);
                    any = true;
                    break;
            }

            i++;
        }

        if (inQuotes)
        {
            problem = "The rows file ends inside a quoted cell.";
            return records;
        }

        if (any || field.Length > 0)
        {
            record.Add(new Field(field.ToString(), quoted));
            records.Add(record);
        }

        return records;
    }

    private static JsonElement Value(Field field)
    {
        if (field.Quoted)
            return field.Text.Length > 0 && field.Text[0] is '[' or '{' && Typed(field.Text) is { } structured
                ? structured
                : JsonSerializer.SerializeToElement(field.Text);
        if (field.Text.Length == 0)
            return JsonSerializer.SerializeToElement<object?>(null);
        if (Typed(field.Text) is { } typed)
            return typed;
        return JsonSerializer.SerializeToElement(field.Text);
    }

    /// <summary>The JSON value an unquoted cell stands for, when it is a number, a boolean, an array or an object.</summary>
    private static JsonElement? Typed(string text)
    {
        var first = text[0];
        if (!(first is '-' or '[' or '{' or 't' or 'f' or (>= '0' and <= '9')))
            return null;
        try
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            return root.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False or JsonValueKind.Array or JsonValueKind.Object
                ? root.Clone()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Cell(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Undefined or JsonValueKind.Null => "",
        JsonValueKind.String => Quote(value.GetString()!, force: false),
        JsonValueKind.Number => value.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => Quote(JsonSerializer.Serialize(value, Canonical), force: false, json: true),
    };

    private static readonly JsonSerializerOptions Canonical = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>
    /// A cell's text: quoted when it holds a comma, a quote or a line break, or (for text) when it is empty or would read back as a
    /// number, a boolean, an array or an object.
    /// </summary>
    private static string Quote(string text, bool force, bool json = false)
    {
        var must = force || text.IndexOfAny([',', '"', '\n', '\r']) >= 0 || (!json && (text.Length == 0 || Typed(text) is not null));
        return must ? "\"" + text.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : text;
    }
}
