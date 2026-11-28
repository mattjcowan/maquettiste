using System.Collections.Frozen;
using System.Collections.Immutable;

namespace Maquettiste.Engine.Model;

/// <summary>The built-in scalar type keywords (SPEC section 6). <c>decimal(p,s)</c> is written as <c>decimal</c> with facets (D3).</summary>
public static class BuiltinTypes
{
    private static readonly ImmutableArray<string> Keywords =
    [
        "string", "text", "bool", "int16", "int32", "int64", "decimal", "float", "double",
        "date", "time", "datetime", "datetimeoffset", "duration", "uuid", "ulid", "binary", "json",
    ];

    private static readonly FrozenSet<string> KeywordSet = Keywords.ToFrozenSet(StringComparer.Ordinal);

    private static readonly ImmutableArray<string> ColumnOnlyKeywords = ["range", "multirange"];

    private static readonly ImmutableArray<string> RangeSubtypeKeywords = ["int32", "int64", "decimal", "date", "datetime", "datetimeoffset"];

    /// <summary>Every keyword, in SPEC order.</summary>
    public static IReadOnlyList<string> All => Keywords;

    /// <summary>
    /// The keywords only a column takes (next-phase-1-design.md section 3.4): <c>range</c> and <c>multirange</c>, PostgreSQL's built-in
    /// range types, with a <see cref="Column.Subtype"/>. Attributes never take them, since attribute types drive code types.
    /// </summary>
    public static IReadOnlyList<string> ColumnOnly => ColumnOnlyKeywords;

    /// <summary>
    /// The keywords a range or multirange column's subtype takes: those PostgreSQL has a built-in range for (<c>int4range</c>,
    /// <c>int8range</c>, <c>numrange</c>, <c>daterange</c>, <c>tsrange</c>, <c>tstzrange</c>).
    /// </summary>
    public static IReadOnlyList<string> RangeSubtypes => RangeSubtypeKeywords;

    /// <summary>Whether a string is a built-in keyword (case-sensitive).</summary>
    /// <param name="keyword">The candidate keyword.</param>
    /// <returns><see langword="true"/> for a built-in keyword.</returns>
    public static bool IsBuiltin(string? keyword) => keyword is not null && KeywordSet.Contains(keyword);

    /// <summary>Whether a string is a column type keyword: a built-in keyword or one of <see cref="ColumnOnly"/> (case-sensitive).</summary>
    /// <param name="keyword">The candidate keyword.</param>
    /// <returns><see langword="true"/> for a column type keyword.</returns>
    public static bool IsColumnType(string? keyword) => IsBuiltin(keyword) || IsRange(keyword);

    /// <summary>Whether a string is <c>range</c> or <c>multirange</c> (case-sensitive).</summary>
    /// <param name="keyword">The candidate keyword.</param>
    /// <returns><see langword="true"/> for a range keyword.</returns>
    public static bool IsRange(string? keyword) => keyword is "range" or "multirange";

    /// <summary>Every column type keyword with its UTF-8 bytes, for <see cref="Keyword"/>.</summary>
    private static readonly (string Keyword, byte[] Utf8)[] KeywordBytes =
        [.. Keywords.Concat(ColumnOnlyKeywords).Select(static k => (k, System.Text.Encoding.UTF8.GetBytes(k)))];

    /// <summary>The longest column type keyword, in bytes.</summary>
    private static readonly int LongestKeyword = KeywordBytes.Max(static k => k.Utf8.Length);

    /// <summary>
    /// The one instance of a column type keyword given as UTF-8 bytes, or <see langword="null"/> when the bytes are none: model files
    /// name the same few types tens of thousands of times, and a reader that takes this instance keeps one string for all of them.
    /// </summary>
    /// <param name="utf8">The bytes.</param>
    /// <returns>The keyword, or <see langword="null"/>.</returns>
    internal static string? Keyword(ReadOnlySpan<byte> utf8)
    {
        if (utf8.Length > LongestKeyword || utf8.Length < 3 || !KeywordStarts.Contains(utf8[0]))
            return null;
        foreach (var (keyword, bytes) in KeywordBytes)
        {
            if (bytes.Length == utf8.Length && utf8.SequenceEqual(bytes))
                return keyword;
        }

        return null;
    }

    /// <summary>The first bytes of the column type keywords, so most other strings are turned away at once.</summary>
    private static readonly System.Buffers.SearchValues<byte> KeywordStarts =
        System.Buffers.SearchValues.Create([.. KeywordBytes.Select(static k => k.Utf8[0]).Distinct()]);
}
