using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace Maquettiste.Engine.Loading;

/// <summary>
/// A qualified name of a batch (next-phase-1-design.md section 5.1): <c>@kind:path</c>, the path's segments separated by dots, a
/// segment that holds a dot, a double quote or white space written in double quotes with each quote doubled. Names exist only in
/// requests; files stay id-based. No id starts with <c>@</c> or <c>$</c>, so a string at a name site that starts with either is a name
/// or a placeholder. Segments compare ordinally, as written in the files; <see cref="Text"/> is the canonical spelling.
/// </summary>
internal sealed class QualifiedName : IEquatable<QualifiedName>
{
    private QualifiedName(string kind, ImmutableArray<string> segments)
    {
        Kind = kind;
        Segments = segments;
        Path = FormatPath(segments);
    }

    /// <summary>The kind: an element kind or an index kind (<c>attribute</c>, <c>column</c>, ...).</summary>
    public string Kind { get; }

    /// <summary>The unquoted segments.</summary>
    public ImmutableArray<string> Segments { get; }

    /// <summary>The canonical path: segments quoted only where they must be.</summary>
    public string Path { get; }

    /// <summary>The canonical text, <c>@kind:path</c>.</summary>
    public string Text => "@" + Kind + ":" + Path;

    /// <summary>Whether a value at a name site is read as a name or a placeholder: it starts with <c>@</c> or <c>$</c>.</summary>
    /// <param name="value">The value.</param>
    /// <returns><see langword="true"/> for a name or a placeholder, well formed or not.</returns>
    public static bool IsName([NotNullWhen(true)] string? value) => value is { Length: > 0 } && value[0] is '@' or '$';

    /// <summary>Whether a value is a well-formed placeholder: <c>$</c>, a letter or <c>_</c>, then letters, digits, <c>_</c> or <c>-</c>.</summary>
    /// <param name="value">The value.</param>
    /// <returns><see langword="true"/> for a placeholder.</returns>
    public static bool IsPlaceholder([NotNullWhen(true)] string? value)
    {
        if (value is not { Length: >= 2 } || value[0] != '$' || !(char.IsAsciiLetter(value[1]) || value[1] == '_'))
            return false;
        foreach (var c in value.AsSpan(2))
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is '_' or '-'))
                return false;
        }

        return true;
    }

    /// <summary>Builds a name from its kind and unquoted segments.</summary>
    /// <param name="kind">The kind.</param>
    /// <param name="segments">The segments, none empty.</param>
    /// <returns>The name.</returns>
    public static QualifiedName Create(string kind, IEnumerable<string> segments)
    {
        ArgumentException.ThrowIfNullOrEmpty(kind);
        ArgumentNullException.ThrowIfNull(segments);
        var list = segments.ToImmutableArray();
        if (list.IsEmpty || list.Any(s => s.Length == 0))
            throw new ArgumentException("A qualified name has at least one segment and no empty segment.", nameof(segments));
        return new QualifiedName(kind, list);
    }

    /// <summary>
    /// Parses <c>@kind:path</c>. The kind must be one with names (<see cref="NameIndex.NamedKinds"/>); a kind listed by id only
    /// (<see cref="NameIndex.IdOnlyKinds"/>) and an unknown kind are errors, as are an empty segment and an empty or unclosed quoted
    /// segment (MQ1015).
    /// </summary>
    /// <param name="text">The text.</param>
    /// <param name="name">The name when well formed.</param>
    /// <param name="error">Why it is malformed, as the end of a sentence.</param>
    /// <returns><see langword="true"/> when well formed.</returns>
    public static bool TryParse(string? text, [NotNullWhen(true)] out QualifiedName? name, [NotNullWhen(false)] out string? error)
    {
        name = null;
        if (text is not { Length: > 0 } || text[0] != '@')
        {
            error = "it is not @kind:path or $name";
            return false;
        }

        var colon = text.IndexOf(':', StringComparison.Ordinal);
        if (colon < 2 || !char.IsAsciiLetterLower(text[1]) || text.AsSpan(2, colon - 2).ContainsAnyExcept(KindChars))
        {
            error = "it is not @kind:path or $name";
            return false;
        }

        var kind = text[1..colon];
        if (NameIndex.IdOnlyKinds.Contains(kind))
        {
            error = $"the kind '{kind}' has no names: use its id or a placeholder";
            return false;
        }

        if (!NameIndex.NamedKinds.Contains(kind))
        {
            error = $"the kind '{kind}' is unknown";
            return false;
        }

        if (!TryParsePath(text.AsSpan(colon + 1), out var segments, out error))
            return false;
        name = new QualifiedName(kind, segments);
        return true;
    }

    /// <summary>Writes one segment: bare when it holds no dot, double quote or white space, else quoted with quotes doubled.</summary>
    /// <param name="segment">The unquoted segment.</param>
    /// <returns>The segment as written in a name.</returns>
    public static string FormatSegment(string segment)
    {
        ArgumentNullException.ThrowIfNull(segment);
        var bare = segment.Length > 0;
        foreach (var c in segment)
        {
            if (c is '.' or '"' || char.IsWhiteSpace(c))
            {
                bare = false;
                break;
            }
        }

        return bare ? segment : "\"" + segment.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }

    /// <summary>Writes a path from unquoted segments.</summary>
    /// <param name="segments">The segments.</param>
    /// <returns>The canonical path.</returns>
    public static string FormatPath(IEnumerable<string> segments) => string.Join('.', segments.Select(FormatSegment));

    /// <inheritdoc/>
    public bool Equals(QualifiedName? other) =>
        other is not null && string.Equals(Kind, other.Kind, StringComparison.Ordinal) && string.Equals(Path, other.Path, StringComparison.Ordinal);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => Equals(obj as QualifiedName);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(StringComparer.Ordinal.GetHashCode(Kind), StringComparer.Ordinal.GetHashCode(Path));

    /// <inheritdoc/>
    public override string ToString() => Text;

    private static readonly System.Buffers.SearchValues<char> KindChars = System.Buffers.SearchValues.Create("abcdefghijklmnopqrstuvwxyz-");

    private static bool TryParsePath(ReadOnlySpan<char> path, out ImmutableArray<string> segments, [NotNullWhen(false)] out string? error)
    {
        var list = ImmutableArray.CreateBuilder<string>();
        var i = 0;
        while (true)
        {
            if (i < path.Length && path[i] == '"')
            {
                var segment = new StringBuilder();
                var closed = false;
                i++;
                while (i < path.Length)
                {
                    if (path[i] != '"')
                    {
                        segment.Append(path[i++]);
                    }
                    else if (i + 1 < path.Length && path[i + 1] == '"')
                    {
                        segment.Append('"');
                        i += 2;
                    }
                    else
                    {
                        closed = true;
                        i++;
                        break;
                    }
                }

                if (!closed)
                {
                    segments = default;
                    error = "a quoted segment is not closed";
                    return false;
                }

                if (segment.Length == 0)
                {
                    segments = default;
                    error = "a quoted segment is empty";
                    return false;
                }

                list.Add(segment.ToString());
            }
            else
            {
                var start = i;
                while (i < path.Length && path[i] != '.')
                {
                    if (path[i] == '"' || char.IsWhiteSpace(path[i]))
                    {
                        segments = default;
                        error = "a segment holds a double quote or white space: quote it";
                        return false;
                    }

                    i++;
                }

                if (i == start)
                {
                    segments = default;
                    error = "a segment is empty";
                    return false;
                }

                list.Add(path[start..i].ToString());
            }

            if (i == path.Length)
                break;
            if (path[i] != '.')
            {
                segments = default;
                error = "a quoted segment is followed by text other than a dot";
                return false;
            }

            i++;
        }

        segments = list.ToImmutable();
        error = null;
        return true;
    }
}
