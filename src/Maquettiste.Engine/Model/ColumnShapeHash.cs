using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Maquettiste.Engine.Model;

/// <summary>
/// The shape hash of one column of a table file (next-phase-2-design.md section 3.3, "The shape hash"): the value a binding's
/// <see cref="BindingProvenance"/> records per column, <c>"&lt;name part&gt;:&lt;type part&gt;"</c>. Each part is the first 8 lowercase hex
/// digits of SHA-256 over UTF-8 text, so a binding records that a column changed, and how, without holding a copy of the table. Both
/// parts read the table file's own values (an inherited member's entry with the table's redefinition applied), never resolved ones,
/// so type maps and conventions do not move them, and the resolver, the operations and the report compute the same value.
/// </summary>
/// <remarks>
/// The type part's eight lines are part of the format: a later facet joins only with a new part, never by changing these lines, or
/// every binding would drift at once. Eight digits are 32 bits: a collision hides one change to one column and corrupts nothing.
/// </remarks>
internal static class ColumnShapeHash
{
    /// <summary>The number of hex digits of each part.</summary>
    public const int PartLength = 8;

    /// <summary>The column's recorded value: its name part and its type part joined by <c>:</c>.</summary>
    /// <param name="column">A column entry of a table file; an inherited member's entry with its redefinition applied.</param>
    /// <returns>The value, for example <c>8a1f3c20:5be0d7a9</c>.</returns>
    public static string Of(Column column)
    {
        ArgumentNullException.ThrowIfNull(column);
        return NamePart(column.Name) + ":" + TypePart(column);
    }

    /// <summary>
    /// The name part: the hash of the loose name (<see cref="Loose"/>), so a change of case or separators, which no attribute name
    /// follows, is not a rename.
    /// </summary>
    /// <param name="name">The column's name.</param>
    /// <returns>8 lowercase hex digits.</returns>
    public static string NamePart(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return Digest(Loose(name));
    }

    /// <summary>
    /// The type part: the hash of eight lines joined by <c>\n</c>: <c>type</c>, <c>subtype</c>, <c>typeRef.ref</c>, <c>length</c>,
    /// <c>precision</c>, <c>scale</c>, <c>nativeType</c> and <c>nullable</c>. Numbers are invariant text, <c>nullable</c> is
    /// <c>true</c> or <c>false</c> with absent read as <c>true</c>, and any other absent value is empty.
    /// </summary>
    /// <param name="column">A column entry of a table file.</param>
    /// <returns>8 lowercase hex digits.</returns>
    public static string TypePart(Column column)
    {
        ArgumentNullException.ThrowIfNull(column);
        var lines = string.Join('\n',
            column.Type ?? "",
            column.Subtype ?? "",
            column.TypeRef?.Ref ?? "",
            Number(column.Length),
            Number(column.Precision),
            Number(column.Scale),
            column.NativeType ?? "",
            (column.Nullable ?? true) ? "true" : "false");
        return Digest(lines);
    }

    /// <summary>The name part of a recorded value, or <see langword="null"/> when the value is not two parts.</summary>
    /// <param name="recorded">A recorded value.</param>
    /// <returns>The part before <c>:</c>.</returns>
    public static string? NamePartOf(string recorded) =>
        recorded.IndexOf(':', StringComparison.Ordinal) is var colon and > 0 ? recorded[..colon] : null;

    /// <summary>The type part of a recorded value, or <see langword="null"/> when the value is not two parts.</summary>
    /// <param name="recorded">A recorded value.</param>
    /// <returns>The part after <c>:</c>.</returns>
    public static string? TypePartOf(string recorded) =>
        recorded.IndexOf(':', StringComparison.Ordinal) is var colon and > 0 ? recorded[(colon + 1)..] : null;

    /// <summary>
    /// The loose form of a name, as map by name compares names and as the editor does: lowercase, with every character that is not an
    /// ASCII letter or digit dropped (<c>customer_id</c>, <c>customerId</c> and <c>CustomerID</c> read the same).
    /// </summary>
    /// <param name="name">A name.</param>
    /// <returns>The loose form.</returns>
    public static string Loose(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var lower = name.ToLowerInvariant();
        var builder = new StringBuilder(lower.Length);
        foreach (var ch in lower)
        {
            if (ch is (>= 'a' and <= 'z') or (>= '0' and <= '9'))
                builder.Append(ch);
        }

        return builder.ToString();
    }

    private static string Number(int? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "";

    private static string Digest(string text) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)).AsSpan(0, PartLength / 2));
}
