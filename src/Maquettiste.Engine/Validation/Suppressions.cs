using System.Collections.Frozen;
using System.Runtime.CompilerServices;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Model;

namespace Maquettiste.Engine.Validation;

/// <summary>
/// The suppressions of one snapshot (next-phase-2-design.md section 5.6), built once per snapshot as a lookup by file and rule, so
/// applying them never scans: a finding is on element E when its file is E's file, and on member M of E when, in addition, its element
/// id is M or its pointer is M's entry pointer or lies under it (looked up in the index at match time, so moving a part in its array
/// changes nothing). The first matching entry wins. A snapshot without suppressions (counted by its index walk) shares one empty lookup
/// without reading a document, and every apply returns its input.
/// </summary>
internal sealed class Suppressions
{
    private static readonly ConditionalWeakTable<ModelSnapshot, Suppressions> Cache = new();

    /// <summary>The lookup of every snapshot without suppressions.</summary>
    internal static Suppressions None { get; } = new(null, FrozenDictionary<(string, string), Entry[]>.Empty, []);

    /// <summary>The documents the lookups built on this thread have read (tests count the work: none for a model without suppressions).</summary>
    [ThreadStatic]
    internal static int DocumentsRead;

    private readonly ModelSnapshot? _model;
    private readonly FrozenDictionary<(string File, string Rule), Entry[]> _entries;
    private readonly (ElementDocument Document, int Index)[] _all;

    private Suppressions(ModelSnapshot? model, FrozenDictionary<(string File, string Rule), Entry[]> entries, (ElementDocument, int)[] all)
    {
        _model = model;
        _entries = entries;
        _all = all;
    }

    /// <summary>One entry: the element that holds it, its index and the entry.</summary>
    private sealed record Entry(string ElementId, int Index, Suppression Suppression);

    /// <summary>Whether the snapshot declares no suppression that may match.</summary>
    public bool IsEmpty => _entries.Count == 0;

    /// <summary>The snapshot's suppressions, built on first use.</summary>
    /// <param name="model">The snapshot.</param>
    /// <returns>The lookup.</returns>
    public static Suppressions Of(ModelSnapshot model)
    {
        ArgumentNullException.ThrowIfNull(model);
        return model.SuppressionCount == 0 ? None : Cache.GetValue(model, Build);
    }

    private static Suppressions Build(ModelSnapshot model)
    {
        Dictionary<(string, string), List<Entry>>? found = null;
        List<(ElementDocument, int)>? all = null;
        foreach (var document in model.Documents)
        {
            DocumentsRead++;
            var list = document.Element.Suppressions;
            for (var i = 0; i < list.Count; i++)
            {
                if (!SuppressionRules.CanBeSuppressed(list[i].Rule))
                    continue;
                (all ??= []).Add((document, i));
                found ??= [];
                var key = (document.Path, list[i].Rule);
                if (!found.TryGetValue(key, out var entries))
                    found[key] = entries = [];
                entries.Add(new Entry(document.Element.Id, i, list[i]));
            }
        }

        return new Suppressions(model, found is null ? FrozenDictionary<(string, string), Entry[]>.Empty
            : found.ToFrozenDictionary(p => p.Key, p => p.Value.ToArray()), all?.ToArray() ?? []);
    }

    /// <summary>Moves the findings an entry matches out of a list.</summary>
    /// <param name="diagnostics">The findings, after severities.</param>
    /// <param name="suppressed">The findings moved, with their entries, in input order.</param>
    /// <returns>The findings kept, in input order (the input itself when none moved).</returns>
    public IReadOnlyList<Diagnostic> Apply(IReadOnlyList<Diagnostic> diagnostics, out IReadOnlyList<SuppressedFinding> suppressed)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        suppressed = [];
        if (IsEmpty || diagnostics.Count == 0)
            return diagnostics;

        List<Diagnostic>? kept = null;
        List<SuppressedFinding>? moved = null;
        for (var i = 0; i < diagnostics.Count; i++)
        {
            var d = diagnostics[i];
            if (Match(d) is { } entry)
            {
                kept ??= [.. diagnostics.Take(i)];
                (moved ??= []).Add(new SuppressedFinding(d, entry.Suppression.Reason, entry.ElementId, Ptr.At("/suppressions", entry.Index)));
            }
            else
            {
                kept?.Add(d);
            }
        }

        if (moved is null)
            return diagnostics;
        suppressed = moved;
        return kept!;
    }

    private Entry? Match(Diagnostic d)
    {
        if (d.FilePath is null || !_entries.TryGetValue((d.FilePath, d.Rule), out var entries))
            return null;
        foreach (var entry in entries)
        {
            if (entry.Suppression.Member is not { } member || string.Equals(d.ElementId, member, StringComparison.Ordinal))
                return entry;
            if (d.JsonPointer is { } pointer && _model!.TryGetEntry(member, out var part) && part.OwnerId == entry.ElementId
                && part.JsonPointer.Length > 0 && pointer.StartsWith(part.JsonPointer, StringComparison.Ordinal)
                && (pointer.Length == part.JsonPointer.Length || pointer[part.JsonPointer.Length] == '/'))
                return entry;
        }

        return null;
    }

    /// <summary>
    /// The MQ5020 warnings of a whole-model report whose model resolved: one for each entry that no finding matched, at the entry, except
    /// entries MQ5018 or MQ5019 already reports and entries that name a rule that cannot be suppressed.
    /// </summary>
    /// <param name="model">The snapshot.</param>
    /// <param name="report">The report, every suppressed list merged.</param>
    /// <returns>The warnings, unsorted and without positions.</returns>
    public static List<Diagnostic> Unused(ModelSnapshot model, ValidationReport report)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(report);
        var found = new List<Diagnostic>();
        var suppressions = Of(model);
        if (suppressions.IsEmpty)
            return found;
        var used = report.Suppressed.Select(s => (s.ElementId, s.JsonPointer)).ToHashSet();
        var flagged = report.Diagnostics.Where(d => d.Rule is "MQ5018" or "MQ5019" && d.FilePath is not null && d.JsonPointer is not null)
            .Select(d => (d.FilePath!, Entry: EntryOf(d.JsonPointer!))).ToHashSet();
        foreach (var (document, i) in suppressions._all)
        {
            var entry = document.Element.Suppressions[i];
            var at = Ptr.At("/suppressions", i);
            if (used.Contains((document.Element.Id, at)) || flagged.Contains((document.Path, at)))
                continue;
            found.Add(RuleCatalog.Create("MQ5020",
                $"The suppression of {entry.Rule}{(entry.Member is { } m ? " on " + m : "")} matched no finding.", document.Element.Id, document.Path, at));
        }

        return found;
    }

    /// <summary>The entry part of a pointer under <c>/suppressions/&lt;i&gt;</c>.</summary>
    private static string EntryOf(string pointer)
    {
        if (!pointer.StartsWith("/suppressions/", StringComparison.Ordinal))
            return pointer;
        var end = pointer.IndexOf('/', "/suppressions/".Length);
        return end < 0 ? pointer : pointer[..end];
    }
}
