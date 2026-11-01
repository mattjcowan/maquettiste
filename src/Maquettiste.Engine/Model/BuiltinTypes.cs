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
}
