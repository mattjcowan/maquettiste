using System.Globalization;
using System.Text.RegularExpressions;
using Maquettiste.Engine.Hashing;

namespace Maquettiste.Engine.PostProcessing;

/// <summary>
/// Protected regions for <c>regions</c> mode (SPEC section 12, engine-design.md section 13). A region starts at a line holding
/// <c>maquettiste:keep id=&lt;id&gt;</c> and ends at the next line holding <c>maquettiste:end-keep</c>, in any comment syntax
/// (<c>// …</c>, <c>-- …</c>, <c>&lt;!-- … --&gt;</c>, <c># …</c>). The body is the lines strictly between the two marker lines.
/// Region ids are unique within a file; nesting is not allowed.
/// </summary>
/// <remarks>
/// The writer (W7) compares a regions file on disk with its manifest entry through <see cref="SkeletonHash(string)"/>, so it must
/// parse the disk file with this class too: an edit inside a region is not a hand edit, an edit outside one is.
/// </remarks>
internal static partial class ProtectedRegions
{
    /// <summary>The start marker text.</summary>
    public const string KeepMarker = "maquettiste:keep";

    /// <summary>The end marker text.</summary>
    public const string EndMarker = "maquettiste:end-keep";

    [GeneratedRegex(@"maquettiste:keep\s+id=(?<id>[A-Za-z0-9_.:]+(?:-[A-Za-z0-9_.:]+)*)", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex KeepLine();

    /// <summary>Parses a normalized (LF) text into lines and regions.</summary>
    /// <param name="text">The text, already normalized.</param>
    /// <returns>The parse result; <see cref="RegionParse.Error"/> is set when the markers are malformed.</returns>
    public static RegionParse Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var lines = text.Split('\n');
        var regions = new List<Region>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        Region? open = null;
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.Contains(EndMarker, StringComparison.Ordinal))
            {
                if (open is null)
                    return RegionParse.Fail(lines, $"line {Line(i)}: '{EndMarker}' without an open region");
                regions.Add(open with { EndLine = i });
                open = null;
                continue;
            }

            if (!line.Contains(KeepMarker, StringComparison.Ordinal))
                continue;
            var match = KeepLine().Match(line);
            if (!match.Success)
                return RegionParse.Fail(lines, $"line {Line(i)}: '{KeepMarker}' without a valid 'id=<id>'");
            var id = match.Groups["id"].Value;
            if (open is not null)
                return RegionParse.Fail(lines, $"line {Line(i)}: region '{id}' starts inside region '{open.Id}', which is not terminated");
            if (!seen.Add(id))
                return RegionParse.Fail(lines, $"line {Line(i)}: duplicate region id '{id}'");
            open = new Region(id, i, -1);
        }

        if (open is not null)
            return RegionParse.Fail(lines, $"line {Line(open.StartLine)}: region '{open.Id}' is not terminated by '{EndMarker}'");
        return new RegionParse(lines, regions, null);
    }

    /// <summary>The text with every region body emptied (marker lines kept): what a regions file is hashed as.</summary>
    /// <param name="parse">A successful parse.</param>
    /// <returns>The skeleton text.</returns>
    public static string Skeleton(RegionParse parse)
    {
        ArgumentNullException.ThrowIfNull(parse);
        if (parse.Error is not null)
            throw new InvalidOperationException("The text has malformed region markers: " + parse.Error);
        if (parse.Regions.Count == 0)
            return string.Join('\n', parse.Lines);
        var result = new List<string>(parse.Lines.Count);
        var next = 0;
        foreach (var region in parse.Regions)
        {
            for (var i = next; i <= region.StartLine; i++)
                result.Add(parse.Lines[i]);
            next = region.EndLine;
        }

        for (var i = next; i < parse.Lines.Count; i++)
            result.Add(parse.Lines[i]);
        return string.Join('\n', result);
    }

    /// <summary>The manifest hash of a regions file's content: <c>r:</c> + the content hash of its skeleton.</summary>
    /// <param name="normalizedText">The file text, normalized.</param>
    /// <returns>The hash, or <see langword="null"/> when the region markers are malformed.</returns>
    public static string? SkeletonHash(string normalizedText)
    {
        var parse = Parse(normalizedText);
        return parse.Error is null ? "r:" + ContentHash.Of(TextNormalizer.StrictUtf8.GetBytes(Skeleton(parse))) : null;
    }

    /// <summary>
    /// Moves each region body of <paramref name="existing"/> into the region with the same id in <paramref name="generated"/>.
    /// Marker lines come from the generated text; a generated region the existing file lacks keeps its generated body.
    /// </summary>
    /// <param name="generated">The parsed new output.</param>
    /// <param name="existing">The parsed disk file.</param>
    /// <param name="lost">The ids of existing regions the new output no longer has, ordinal.</param>
    /// <returns>The merged text.</returns>
    public static string Merge(RegionParse generated, RegionParse existing, out IReadOnlyList<string> lost)
    {
        ArgumentNullException.ThrowIfNull(generated);
        ArgumentNullException.ThrowIfNull(existing);
        if (generated.Error is not null || existing.Error is not null)
            throw new InvalidOperationException("Cannot merge texts with malformed region markers.");
        var bodies = new Dictionary<string, (int Start, int End)>(StringComparer.Ordinal);
        foreach (var region in existing.Regions)
            bodies[region.Id] = (region.StartLine + 1, region.EndLine);
        var generatedIds = new HashSet<string>(generated.Regions.Select(r => r.Id), StringComparer.Ordinal);
        lost = [.. existing.Regions.Select(r => r.Id).Where(id => !generatedIds.Contains(id)).Order(StringComparer.Ordinal)];

        var result = new List<string>(generated.Lines.Count);
        var next = 0;
        foreach (var region in generated.Regions)
        {
            for (var i = next; i <= region.StartLine; i++)
                result.Add(generated.Lines[i]);
            if (bodies.TryGetValue(region.Id, out var body))
            {
                for (var i = body.Start; i < body.End; i++)
                    result.Add(existing.Lines[i]);
            }
            else
            {
                for (var i = region.StartLine + 1; i < region.EndLine; i++)
                    result.Add(generated.Lines[i]);
            }

            next = region.EndLine;
        }

        for (var i = next; i < generated.Lines.Count; i++)
            result.Add(generated.Lines[i]);
        return string.Join('\n', result);
    }

    private static string Line(int index) => (index + 1).ToString(CultureInfo.InvariantCulture);
}

/// <summary>One protected region: the 0-based line indexes of its start and end marker lines.</summary>
/// <param name="Id">The region id.</param>
/// <param name="StartLine">The start marker line.</param>
/// <param name="EndLine">The end marker line.</param>
internal sealed record Region(string Id, int StartLine, int EndLine);

/// <summary>A parsed text: its lines (split on LF) and its regions in file order, or an error.</summary>
/// <param name="Lines">The lines; joining them with LF gives the text back.</param>
/// <param name="Regions">The regions, in file order.</param>
/// <param name="Error">Why the markers are malformed, or <see langword="null"/>.</param>
internal sealed record RegionParse(IReadOnlyList<string> Lines, IReadOnlyList<Region> Regions, string? Error)
{
    /// <summary>A failed parse.</summary>
    /// <param name="lines">The lines.</param>
    /// <param name="error">The error.</param>
    /// <returns>The result.</returns>
    public static RegionParse Fail(IReadOnlyList<string> lines, string error) => new(lines, [], error);
}
