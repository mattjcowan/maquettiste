using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Maquettiste.Engine.Localization;

/// <summary>One translation unit read from an exchange file: a node id, a field and the target text (empty: left untranslated).</summary>
/// <param name="Id">The node id.</param>
/// <param name="Field">displayName, pluralName, label or description.</param>
/// <param name="Value">The translated text.</param>
/// <param name="State">
/// The unit's state as the file states it (XLIFF: the segment's <c>state</c>, such as <c>initial</c>, <c>translated</c>, <c>reviewed</c> or
/// <c>final</c>; CSV: the <c>state</c> column), or <see langword="null"/> when the file has none.
/// </param>
public sealed record TranslationUnit(string Id, string Field, string Value, string? State = null);

/// <summary>
/// RFC 4180 CSV (reference-types-seeds-localization.md section 2.3): UTF-8, fields quoted only when they hold a comma, a quote or a line
/// break, LF line ends by default, a byte order mark and CRLF for spreadsheet programs that need them.
/// </summary>
public static class Csv
{
    /// <summary>Writes rows as CSV text.</summary>
    /// <param name="rows">The rows, the header first.</param>
    /// <param name="bom">Adds a byte order mark and CRLF line ends.</param>
    /// <returns>The text.</returns>
    public static string Write(IEnumerable<IReadOnlyList<string?>> rows, bool bom = false)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var text = new StringBuilder();
        if (bom)
            text.Append('﻿');
        var newline = bom ? "\r\n" : "\n";
        foreach (var row in rows)
        {
            for (var i = 0; i < row.Count; i++)
            {
                if (i > 0)
                    text.Append(',');
                var cell = row[i] ?? "";
                if (cell.AsSpan().IndexOfAny(",\"\r\n") >= 0)
                    text.Append('"').Append(cell.Replace("\"", "\"\"", StringComparison.Ordinal)).Append('"');
                else
                    text.Append(cell);
            }

            text.Append(newline);
        }

        return text.ToString();
    }

    /// <summary>Reads CSV text (a leading byte order mark, LF or CRLF line ends, quoted fields with doubled quotes and line breaks).</summary>
    /// <param name="text">The text.</param>
    /// <returns>The rows, the header first; blank lines are skipped.</returns>
    /// <exception cref="FormatException">A quoted field is not closed, or text follows a closing quote.</exception>
    public static IReadOnlyList<IReadOnlyList<string>> Read(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var rows = new List<IReadOnlyList<string>>();
        var row = new List<string>();
        var cell = new StringBuilder();
        var i = text.Length > 0 && text[0] == '﻿' ? 1 : 0;
        var line = 1;
        var quotedCell = false;
        void EndCell()
        {
            row.Add(cell.ToString());
            cell.Clear();
            quotedCell = false;
        }

        void EndRow()
        {
            EndCell();
            if (!(row.Count == 1 && row[0].Length == 0))
                rows.Add(row);
            row = [];
        }

        while (i < text.Length)
        {
            var c = text[i];
            if (c == '"' && cell.Length == 0 && !quotedCell)
            {
                quotedCell = true;
                i++;
                while (true)
                {
                    if (i >= text.Length)
                        throw new FormatException($"A quoted field opened on line {line.ToString(CultureInfo.InvariantCulture)} is not closed.");
                    if (text[i] == '"')
                    {
                        if (i + 1 < text.Length && text[i + 1] == '"')
                        {
                            cell.Append('"');
                            i += 2;
                            continue;
                        }

                        i++;
                        break;
                    }

                    if (text[i] == '\n')
                        line++;
                    cell.Append(text[i++]);
                }

                if (i < text.Length && text[i] is not (',' or '\r' or '\n'))
                    throw new FormatException($"Line {line.ToString(CultureInfo.InvariantCulture)}: text follows a closing quote.");
                continue;
            }

            switch (c)
            {
                case ',':
                    EndCell();
                    i++;
                    break;
                case '\r' when i + 1 < text.Length && text[i + 1] == '\n':
                    i++;
                    break;
                case '\n' or '\r':
                    EndRow();
                    line++;
                    i++;
                    break;
                default:
                    cell.Append(c);
                    i++;
                    break;
            }
        }

        if (cell.Length > 0 || row.Count > 0 || quotedCell)
            EndRow();
        return rows;
    }
}

/// <summary>
/// The translation exchange files of <c>GET /api/localization/{locale}/export</c> and <c>POST .../import</c>: XLIFF 2.1 with one unit per
/// (node id, field) keyed <c>id/field</c>, and CSV with the columns <c>id, field, source, translation, state, shard</c>.
/// </summary>
public static class TranslationFiles
{
    /// <summary>The XLIFF 2 namespace.</summary>
    public const string XliffNamespace = "urn:oasis:names:tc:xliff:document:2.0";

    /// <summary>The CSV header of an export.</summary>
    public static readonly IReadOnlyList<string> CsvHeader = ["id", "field", "source", "translation", "state", "shard"];

    /// <summary>Writes items as XLIFF 2.1: one <c>file</c> per shard, units in item order.</summary>
    /// <param name="sourceLocale">The default locale.</param>
    /// <param name="targetLocale">The locale exported.</param>
    /// <param name="items">The items.</param>
    /// <returns>The XML text (UTF-8, LF line ends).</returns>
    public static string WriteXliff(string sourceLocale, string targetLocale, IEnumerable<TranslationItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        XNamespace ns = XliffNamespace;
        var root = new XElement(ns + "xliff", new XAttribute("version", "2.1"), new XAttribute("srcLang", sourceLocale), new XAttribute("trgLang", targetLocale));
        var files = 0;
        foreach (var shard in items.GroupBy(i => i.ShardPath, StringComparer.Ordinal))
        {
            files++;
            var file = new XElement(ns + "file", new XAttribute("id", "f" + files.ToString(CultureInfo.InvariantCulture)), new XAttribute("original", shard.Key));
            foreach (var item in shard)
            {
                var segment = new XElement(ns + "segment",
                    new XAttribute("state", item.State == "translated" ? "translated" : "initial"),
                    new XAttribute("subState", "maquettiste:" + item.State),
                    new XElement(ns + "source", item.Source ?? ""));
                if (item.Value is not null)
                    segment.Add(new XElement(ns + "target", item.Value));
                file.Add(new XElement(ns + "unit", new XAttribute("id", item.Id + "/" + item.Field), segment));
            }

            root.Add(file);
        }

        var settings = new XmlWriterSettings { Indent = true, IndentChars = "  ", NewLineChars = "\n", Encoding = new UTF8Encoding(false), OmitXmlDeclaration = false };
        using var buffer = new MemoryStream();
        using (var writer = XmlWriter.Create(buffer, settings))
            new XDocument(root).Save(writer);
        return Encoding.UTF8.GetString(buffer.ToArray()) + "\n";
    }

    /// <summary>Reads the units of an XLIFF 2 document; a unit without a target, or whose id is not <c>id/field</c>, is skipped.</summary>
    /// <param name="text">The XML text.</param>
    /// <returns>The units, in document order.</returns>
    /// <exception cref="FormatException">The text is not XML, or not XLIFF 2.</exception>
    public static IReadOnlyList<TranslationUnit> ReadXliff(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        XDocument document;
        try
        {
            using var reader = XmlReader.Create(new StringReader(text), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            document = XDocument.Load(reader);
        }
        catch (XmlException ex)
        {
            throw new FormatException("The XLIFF document is not well-formed XML: " + ex.Message, ex);
        }

        XNamespace ns = XliffNamespace;
        if (document.Root?.Name != ns + "xliff")
            throw new FormatException("The document is not XLIFF 2 (its root is not <xliff> in " + XliffNamespace + ").");
        var units = new List<TranslationUnit>();
        foreach (var unit in document.Root.Descendants(ns + "unit"))
        {
            var key = (string?)unit.Attribute("id") ?? "";
            var slash = key.LastIndexOf('/');
            if (slash <= 0)
                continue;
            var targets = unit.Descendants(ns + "target").ToList();
            if (targets.Count == 0)
                continue;
            var state = (string?)unit.Descendants(ns + "segment").FirstOrDefault()?.Attribute("state");
            units.Add(new TranslationUnit(key[..slash], key[(slash + 1)..], string.Concat(targets.Select(t => t.Value)), state));
        }

        return units;
    }

    /// <summary>Writes items as CSV with <see cref="CsvHeader"/>.</summary>
    /// <param name="items">The items.</param>
    /// <returns>The text.</returns>
    public static string WriteCsv(IEnumerable<TranslationItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        IEnumerable<IReadOnlyList<string?>> Rows()
        {
            yield return [.. CsvHeader];
            foreach (var item in items)
                yield return [item.Id, item.Field, item.Source, item.Value, item.State, item.ShardPath];
        }

        return Csv.Write(Rows());
    }

    /// <summary>Reads the <c>id</c>, <c>field</c> and <c>translation</c> columns of a translation CSV; other columns are ignored.</summary>
    /// <param name="text">The CSV text.</param>
    /// <returns>The units, in file order.</returns>
    /// <exception cref="FormatException">The CSV is malformed or lacks one of the three columns.</exception>
    public static IReadOnlyList<TranslationUnit> ReadCsv(string text)
    {
        var rows = Csv.Read(text);
        if (rows.Count == 0)
            return [];
        var header = rows[0];
        int Column(string name) => header.ToList().IndexOf(name) is var at and >= 0 ? at : throw new FormatException($"The CSV has no '{name}' column.");
        int id = Column("id"), field = Column("field"), translation = Column("translation"), state = header.ToList().IndexOf("state");
        return [.. rows.Skip(1).Where(r => r.Count > Math.Max(id, field)).Select(r => new TranslationUnit(r[id], r[field], translation < r.Count ? r[translation] : "",
            state >= 0 && state < r.Count && r[state].Length > 0 ? r[state] : null))];
    }
}
