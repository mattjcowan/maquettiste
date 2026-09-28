using System.Text.Json;
using System.Text.Json.Serialization;

namespace Maquettiste.Engine.Diagnostics;

/// <summary>The severity of a diagnostic. JSON <c>error|warning|info</c>; SARIF <c>error|warning|note</c>.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<DiagnosticSeverity>))]
public enum DiagnosticSeverity
{
    /// <summary>Stops generation: <c>error</c>.</summary>
    [JsonStringEnumMemberName("error")] Error,

    /// <summary>Reported, does not stop generation: <c>warning</c>.</summary>
    [JsonStringEnumMemberName("warning")] Warning,

    /// <summary>Informational: <c>info</c>.</summary>
    [JsonStringEnumMemberName("info")] Info,
}

/// <summary>
/// One finding, as a plain record. The same records feed the API, <c>validate --format json</c> and SARIF (host-contracts requirement 21).
/// </summary>
/// <param name="Rule">The rule id: <c>MQ1001</c>…<c>MQ6018</c> (see <see cref="RuleCatalog"/>) or <c>x/&lt;id&gt;</c> for JavaScript rules.</param>
/// <param name="Severity">The effective severity.</param>
/// <param name="Message">A one-sentence message.</param>
/// <param name="ElementId">The id of the element (or sub-element) concerned, when known.</param>
/// <param name="FilePath">The repo-relative file path with <c>/</c> separators, when known.</param>
/// <param name="JsonPointer">A JSON pointer into the file, when known.</param>
/// <param name="Line">The 1-based line, when known.</param>
/// <param name="Column">The 1-based column, when known.</param>
public sealed record Diagnostic(
    string Rule,
    DiagnosticSeverity Severity,
    string Message,
    string? ElementId,
    string? FilePath,
    string? JsonPointer,
    int? Line,
    int? Column)
{
    /// <summary>
    /// The ordinal diagnostic order: (<see cref="FilePath"/>, <see cref="Line"/>, <see cref="Column"/>, <see cref="Rule"/>, <see cref="Message"/>).
    /// </summary>
    public static IComparer<Diagnostic> Order { get; } = Comparer<Diagnostic>.Create(Compare);

    private static int Compare(Diagnostic? a, Diagnostic? b)
    {
        if (ReferenceEquals(a, b)) return 0;
        if (a is null) return -1;
        if (b is null) return 1;
        var c = string.CompareOrdinal(a.FilePath, b.FilePath);
        if (c != 0) return c;
        c = Nullable.Compare(a.Line, b.Line);
        if (c != 0) return c;
        c = Nullable.Compare(a.Column, b.Column);
        if (c != 0) return c;
        c = string.CompareOrdinal(a.Rule, b.Rule);
        return c != 0 ? c : string.CompareOrdinal(a.Message, b.Message);
    }
}

/// <summary>What to validate.</summary>
/// <param name="ElementIds">The elements to validate; <see langword="null"/> means the whole model.</param>
/// <param name="IncludeReferrers">Whether elements that reference the listed ones are validated too.</param>
/// <param name="IncludeScriptRules">Whether JavaScript rules run.</param>
public sealed record ValidationScope(IReadOnlyList<string>? ElementIds = null, bool IncludeReferrers = true, bool IncludeScriptRules = true)
{
    /// <summary>The whole model, with referrers and script rules.</summary>
    public static ValidationScope All { get; } = new();
}

/// <summary>The result of validating a model (host-contracts requirements 20 and 22).</summary>
/// <param name="Diagnostics">Every diagnostic, in <see cref="Diagnostic.Order"/> (a leading part of them when <paramref name="Truncated"/>).</param>
/// <param name="Errors">The number of errors, counted before any truncation.</param>
/// <param name="Warnings">The number of warnings, counted before any truncation.</param>
/// <param name="Infos">The number of infos, counted before any truncation.</param>
/// <param name="Truncated">Whether diagnostics were dropped to fit a size limit; the receiver must refetch the full report.</param>
public sealed record ValidationReport(IReadOnlyList<Diagnostic> Diagnostics, int Errors, int Warnings, int Infos, bool Truncated = false)
{
    private static readonly JsonSerializerOptions SizeOptions = CreateSizeOptions();

    /// <summary>Whether any diagnostic is an error.</summary>
    public bool HasErrors => Errors > 0;

    /// <summary>
    /// Returns a report whose <see cref="JsonSerializerDefaults.Web"/> JSON fits in <paramref name="maxJsonBytes"/>, keeping the counts,
    /// dropping trailing diagnostics and setting <see cref="Truncated"/> when it had to (for the 256 KB realtime payload limit).
    /// </summary>
    /// <param name="maxJsonBytes">The size limit in bytes.</param>
    /// <returns>This report when it fits, else a truncated copy.</returns>
    public ValidationReport TruncateTo(int maxJsonBytes)
    {
        if (Size(this) <= maxJsonBytes)
            return this;

        int lo = 0, hi = Diagnostics.Count;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) / 2;
            if (Size(Take(mid)) <= maxJsonBytes)
                lo = mid;
            else
                hi = mid - 1;
        }

        return Take(lo);
    }

    private ValidationReport Take(int count) => this with { Diagnostics = Diagnostics.Take(count).ToArray(), Truncated = true };

    private static int Size(ValidationReport report) => JsonSerializer.SerializeToUtf8Bytes(report, SizeOptions).Length;

    private static JsonSerializerOptions CreateSizeOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }

    /// <summary>Builds a report from diagnostics: sorts them and counts by severity.</summary>
    /// <param name="diagnostics">The diagnostics, in any order.</param>
    /// <returns>The report.</returns>
    public static ValidationReport From(IEnumerable<Diagnostic> diagnostics)
    {
        var list = diagnostics.ToList();
        list.Sort(Diagnostic.Order);
        int errors = 0, warnings = 0, infos = 0;
        foreach (var d in list)
        {
            switch (d.Severity)
            {
                case DiagnosticSeverity.Error: errors++; break;
                case DiagnosticSeverity.Warning: warnings++; break;
                default: infos++; break;
            }
        }

        return new ValidationReport(list, errors, warnings, infos);
    }

    /// <summary>Groups the diagnostics by element id, or by file path when a diagnostic has no element; keys in first-seen order.</summary>
    /// <returns>The groups.</returns>
    public IReadOnlyDictionary<string, IReadOnlyList<Diagnostic>> ByElement()
    {
        var groups = new Dictionary<string, List<Diagnostic>>(StringComparer.Ordinal);
        foreach (var d in Diagnostics)
        {
            var key = d.ElementId ?? d.FilePath ?? "";
            if (!groups.TryGetValue(key, out var list))
                groups[key] = list = [];
            list.Add(d);
        }

        return groups.ToDictionary(g => g.Key, g => (IReadOnlyList<Diagnostic>)g.Value, StringComparer.Ordinal);
    }
}
