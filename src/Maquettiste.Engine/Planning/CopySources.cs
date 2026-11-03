using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Hashing;
using Maquettiste.Engine.Json;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Pipeline;
using Maquettiste.Engine.Writing;

namespace Maquettiste.Engine.Planning;

/// <summary>One pack file a copy unit's sources match, read once.</summary>
/// <param name="Path">The pack-relative path, <c>/</c> separators.</param>
/// <param name="Bytes">The file's bytes.</param>
/// <param name="Hash">The content hash of <paramref name="Bytes"/>.</param>
/// <param name="Base">
/// The literal base of the first pattern that matched the file: the pattern's text up to the last <c>/</c> before its first wildcard
/// (the file's folder for a pattern without one), <c>/</c> included, or empty. The file lands at its path after this base.
/// </param>
internal sealed record CopySourceFile(string Path, byte[] Bytes, string Hash, string Base);

/// <summary>
/// The one matcher of a <c>copy</c> unit (next-phase-1-design.md section 5.4), shared by the planner, the preview and the renderer:
/// each of the unit's <see cref="PackUnit.Sources"/> patterns is matched case-sensitively against the pack's file list
/// (<see cref="PackFileRoles.EnumerateFiles"/>, dot files and folders left out), with the expression
/// <see cref="OutputPathPolicy.GlobPattern"/> builds, compiled non-backtracking so matching is linear and no wall clock enters. The
/// files come back ordinal by path, each once, with its bytes and content hash. A pattern that matches nothing is MQ6030; a token
/// whose parameter is undeclared or whose effective value is not a string is MQ6031. The result is kept per <see cref="LoadedPack"/>
/// instance and unit id, so a run or a preview session matches and reads each unit's files once.
/// </summary>
internal sealed class CopySources
{
    private const string NoMatch = "MQ6030";
    private const string BadToken = "MQ6031";
    private const RegexOptions MatchOptions = RegexOptions.CultureInvariant | RegexOptions.NonBacktracking;

    private static readonly ConditionalWeakTable<LoadedPack, PackEntry> Cache = new();

    private static readonly CopySources None = new([], [], [], ImmutableSortedDictionary<string, string>.Empty);

    private readonly IReadOnlyList<string> _sources;
    private readonly IReadOnlyDictionary<string, string> _tokens;

    private CopySources(IReadOnlyList<CopySourceFile> files, IReadOnlyList<Diagnostic> diagnostics, IReadOnlyList<string> sources,
        IReadOnlyDictionary<string, string> tokens)
    {
        Files = files;
        Diagnostics = diagnostics;
        _sources = sources;
        _tokens = tokens;
    }

    /// <summary>The matched files, ordinal by path, each once.</summary>
    public IReadOnlyList<CopySourceFile> Files { get; }

    /// <summary>MQ6030 for each pattern that matches no file and MQ6031 for each bad token, in pattern then token order; empty when the unit can render.</summary>
    public IReadOnlyList<Diagnostic> Diagnostics { get; }

    /// <summary>The match of a unit of a loaded pack; empty for a unit that is not in mode <c>copy</c>.</summary>
    /// <param name="pack">The loaded pack.</param>
    /// <param name="unit">The unit.</param>
    /// <returns>The match, computed once per pack instance and unit id.</returns>
    public static CopySources Of(LoadedPack pack, PackUnit unit)
    {
        ArgumentNullException.ThrowIfNull(pack);
        ArgumentNullException.ThrowIfNull(unit);
        if (unit.Mode != OutputMode.Copy)
            return None;
        var entry = Cache.GetValue(pack, static p => new PackEntry(p.RootPath));
        var cached = entry.Units.GetOrAdd(unit.Id, _ => new Lazy<CopySources>(() => Compute(pack, unit, entry.Files.Value))).Value;
        // A unit is fixed for one pack instance (a preview with an unsaved unit builds a new one); a different definition under the same
        // id is matched afresh rather than served stale.
        return cached.SameInputs(unit) ? cached : Compute(pack, unit, entry.Files.Value);
    }

    /// <summary>
    /// Why a copy unit's source pattern is malformed (MQ6001 at load), or <see langword="null"/>: it is absolute, has an empty,
    /// <c>.</c> or <c>..</c> segment, or holds a backslash.
    /// </summary>
    /// <param name="pattern">The pattern.</param>
    /// <returns>The reason, or <see langword="null"/>.</returns>
    public static string? PatternProblem(string pattern)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        if (pattern.Contains('\\', StringComparison.Ordinal))
            return "it holds a backslash; use / between folders";
        if (pattern.StartsWith('/') || (pattern.Length > 1 && pattern[1] == ':'))
            return "it is absolute; patterns are relative to the pack folder";
        foreach (var segment in pattern.Split('/'))
        {
            if (segment.Length == 0)
                return "it has an empty segment";
            if (segment is "." or "..")
                return "it has a '.' or '..' segment";
        }

        return null;
    }

    /// <summary>The literal base of a pattern: its text up to the last <c>/</c> before the first wildcard (<c>*</c> or <c>?</c>), <c>/</c> included, or empty.</summary>
    /// <param name="pattern">The pattern.</param>
    /// <returns>The base.</returns>
    public static string LiteralBase(string pattern)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        var wildcard = pattern.AsSpan().IndexOfAny('*', '?');
        var literal = wildcard < 0 ? pattern : pattern[..wildcard];
        var slash = literal.LastIndexOf('/');
        return slash < 0 ? "" : literal[..(slash + 1)];
    }

    /// <summary>The paths of a file list that any well-formed pattern matches (malformed ones are skipped), for the pack file roles.</summary>
    /// <param name="paths">Pack-relative paths.</param>
    /// <param name="patterns">The patterns.</param>
    /// <returns>The matched paths.</returns>
    public static IEnumerable<string> Matching(IReadOnlyList<string> paths, IReadOnlyList<string> patterns)
    {
        var regexes = patterns.Where(p => PatternProblem(p) is null).Select(Compile).ToList();
        return regexes.Count == 0 ? [] : paths.Where(p => regexes.Any(r => r.IsMatch(p)));
    }

    private static Regex Compile(string pattern) => new(OutputPathPolicy.GlobPattern(pattern), MatchOptions);

    private bool SameInputs(PackUnit unit) =>
        _sources.SequenceEqual(unit.Sources, StringComparer.Ordinal)
        && _tokens.Count == unit.Tokens.Count
        && _tokens.All(t => unit.Tokens.TryGetValue(t.Key, out var value) && string.Equals(value, t.Value, StringComparison.Ordinal));

    private static CopySources Compute(LoadedPack pack, PackUnit unit, IReadOnlyList<string> files)
    {
        var packFile = pack.RelativePath + "/pack.json";
        var index = -1;
        for (var i = 0; i < pack.Manifest.Units.Count; i++)
        {
            if (string.Equals(pack.Manifest.Units[i].Id, unit.Id, StringComparison.Ordinal))
            {
                index = i;
                break;
            }
        }

        var pointer = index < 0 ? null : "/units/" + index.ToString(CultureInfo.InvariantCulture);
        var diagnostics = new List<Diagnostic>();

        // Each path once, with the literal base of the first pattern (in pattern order) that matched it.
        var matched = new SortedDictionary<string, string>(StringComparer.Ordinal);
        for (var j = 0; j < unit.Sources.Count; j++)
        {
            var pattern = unit.Sources[j];
            var any = false;
            if (PatternProblem(pattern) is null)
            {
                var regex = Compile(pattern);
                var literalBase = LiteralBase(pattern);
                foreach (var path in files)
                {
                    if (!regex.IsMatch(path))
                        continue;
                    any = true;
                    matched.TryAdd(path, literalBase);
                }
            }

            if (!any)
            {
                diagnostics.Add(RuleCatalog.Create(NoMatch,
                    $"Copy unit '{unit.Id}' of pack '{pack.Name}': the source pattern '{pattern}' matches no pack file (patterns are case-sensitive and skip dot files and folders); the unit is skipped.",
                    filePath: packFile, jsonPointer: pointer is null ? null : pointer + "/sources/" + j.ToString(CultureInfo.InvariantCulture)));
            }
        }

        var declared = DeclaredParameters(pack.Manifest);
        foreach (var (token, parameter) in unit.Tokens.OrderBy(t => t.Key, StringComparer.Ordinal))
        {
            var at = pointer is null ? null : pointer + "/tokens/" + JsonPointer.Escape(token);
            if (!declared.Contains(parameter))
            {
                diagnostics.Add(RuleCatalog.Create(BadToken,
                    $"Copy unit '{unit.Id}' of pack '{pack.Name}': the token '{token}' names the parameter '{parameter}', which the pack does not declare (in parameters or parameterSchema); the unit is skipped.",
                    filePath: packFile, jsonPointer: at));
            }
            else if (!pack.Parameters.TryGetValue(parameter, out var value) || value.ValueKind != JsonValueKind.String)
            {
                var what = pack.Parameters.TryGetValue(parameter, out var present) ? "a value of type " + Kind(present.ValueKind) : "no value";
                diagnostics.Add(RuleCatalog.Create(BadToken,
                    $"Copy unit '{unit.Id}' of pack '{pack.Name}': the token '{token}' names the parameter '{parameter}', which has {what}, not a string; the unit is skipped.",
                    filePath: packFile, jsonPointer: at));
            }
        }

        var result = new List<CopySourceFile>(matched.Count);
        foreach (var (path, literalBase) in matched)
        {
            byte[] bytes;
            try
            {
                bytes = File.ReadAllBytes(Path.Combine(pack.RootPath, path.Replace('/', Path.DirectorySeparatorChar)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue; // removed or unreadable since the file list was read: as if it were not there, as PackFiles.Hash treats it
            }

            result.Add(new CopySourceFile(path, bytes, ContentHash.Of(bytes), literalBase));
        }

        return new CopySources(result, diagnostics, unit.Sources, unit.Tokens);
    }

    private static HashSet<string> DeclaredParameters(PackManifest manifest)
    {
        var declared = new HashSet<string>(manifest.Parameters.Keys, StringComparer.Ordinal);
        if (manifest.ParameterSchema is { ValueKind: JsonValueKind.Object } schema && schema.TryGetProperty("properties", out var properties)
            && properties.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in properties.EnumerateObject())
                declared.Add(property.Name);
        }

        return declared;
    }

    private static string Kind(JsonValueKind kind) => kind switch
    {
        JsonValueKind.Object => "object",
        JsonValueKind.Array => "array",
        JsonValueKind.Number => "number",
        JsonValueKind.True or JsonValueKind.False => "boolean",
        JsonValueKind.Null => "null",
        _ => "unknown",
    };

    /// <summary>One pack instance's file list (enumerated once, on first use) and its units' matches.</summary>
    private sealed class PackEntry(string root)
    {
        public Lazy<IReadOnlyList<string>> Files { get; } = new(() => PackFileRoles.EnumerateFiles(root));

        public ConcurrentDictionary<string, Lazy<CopySources>> Units { get; } = new(StringComparer.Ordinal);
    }
}
