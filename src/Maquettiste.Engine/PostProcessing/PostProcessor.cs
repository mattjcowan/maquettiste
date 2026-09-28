using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Hashing;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Pipeline;

namespace Maquettiste.Engine.PostProcessing;

/// <summary>
/// Stage 7 (W8; engine-design.md section 13). Per rendered file, in order: (1) normalize line endings to LF, strip a BOM, encode
/// UTF-8; (2) format with the unit's formatter (by name, else by extension; <c>"none"</c> disables it); (3) in <c>regions</c> mode,
/// move each protected region body of the file on disk into the same region of the new output; (4) hash (the skeleton, prefixed
/// <c>r:</c>, for regions; <c>o:</c> for owned files) and classify the root through <see cref="IOutputPathPolicy"/>.
/// Nothing is written: the result is in memory, so dry runs and <c>--check</c> use the same code.
/// </summary>
/// <param name="options">The engine options (the repo root when the context names none).</param>
/// <param name="formatters">The formatter runner.</param>
internal sealed class PostProcessor(EngineOptions options, IFormatterRunner formatters) : IPostProcessor
{
    /// <summary>
    /// Per-run state keyed by the run's context (so nothing outlives a run in a long-lived host): formatter version checks made
    /// when the orchestrator did not check them up front, and the units already warned about an unknown formatter.
    /// </summary>
    private readonly ConditionalWeakTable<PostProcessContext, RunState> _runs = new();

    /// <inheritdoc/>
    public async Task<ProcessedUnit> ProcessAsync(RenderedUnit unit, PostProcessContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentNullException.ThrowIfNull(context);
        if (unit.Failed)
            return new ProcessedUnit(unit, [], [], true);

        var diagnostics = new List<Diagnostic>();
        var files = new List<OutputFile>(unit.Files.Count);
        var failed = false;
        var packUnit = unit.Unit.Unit;
        var elementId = unit.Unit.Element?.Id;
        var repoRoot = string.IsNullOrEmpty(context.RepoRoot) ? options.RepoRoot : context.RepoRoot;
        var run = _runs.GetValue(context, _ => new RunState());
        var named = SelectNamedFormatter(unit.Unit, context, run, diagnostics);

        foreach (var file in unit.Files)
        {
            ct.ThrowIfCancellationRequested();
            var processed = await ProcessFileAsync(file, packUnit, named, elementId, repoRoot, context, diagnostics, ct).ConfigureAwait(false);
            if (processed is null)
                failed = true;
            else
                files.Add(processed);
        }

        return failed ? new ProcessedUnit(unit, [], diagnostics, true) : new ProcessedUnit(unit, files, diagnostics, false);
    }

    /// <summary>The effective output mode of one file: a <c>pair</c> unit's file blocks are <c>overwrite</c> (engine-design.md section 8).</summary>
    /// <param name="unitMode">The unit's mode.</param>
    /// <param name="role">The file's role.</param>
    /// <returns>The file's mode.</returns>
    internal static OutputMode FileMode(OutputMode unitMode, FileRole role) =>
        unitMode == OutputMode.Pair && role == FileRole.Block ? OutputMode.Overwrite : unitMode;

    /// <summary>The formatter for a file of a unit that names none: the configured formatter with the longest matching extension, first wins.</summary>
    /// <param name="path">The repo-relative path.</param>
    /// <param name="configured">The configured formatters.</param>
    /// <returns>The formatter, or <see langword="null"/>.</returns>
    internal static FormatterSettings? ByExtension(string path, IReadOnlyList<FormatterSettings> configured)
    {
        FormatterSettings? best = null;
        var bestLength = 0;
        foreach (var formatter in configured)
        {
            foreach (var extension in formatter.Extensions)
            {
                if (extension.Length > bestLength && path.EndsWith(extension, StringComparison.Ordinal))
                {
                    best = formatter;
                    bestLength = extension.Length;
                }
            }
        }

        return best;
    }

    /// <summary>
    /// Resolves a unit's named formatter: <see langword="null"/> for none, by extension or unknown (MQ6014, warning). The warning
    /// is raised once per pack unit per run and points at the unit's <c>formatter</c> in its <c>pack.json</c>, never at an element
    /// or output file, so which worker gets there first cannot change what it says.
    /// </summary>
    private static Selection SelectNamedFormatter(PlannedUnit planned, PostProcessContext context, RunState run, List<Diagnostic> diagnostics)
    {
        var unit = planned.Unit;
        if (unit.Formatter is null)
            return Selection.ByExtension;
        if (string.Equals(unit.Formatter, "none", StringComparison.Ordinal))
            return Selection.None;
        var formatter = context.Formatters.FirstOrDefault(f => string.Equals(f.Name, unit.Formatter, StringComparison.Ordinal));
        if (formatter is not null)
            return new Selection(formatter, false);
        if (!run.WarnedUnits.TryAdd(planned.Pack.Name + "/" + unit.Id, 0))
            return Selection.None;
        var units = planned.Pack.Manifest.Units;
        var index = IndexOf(units, u => ReferenceEquals(u, unit));
        if (index < 0)
            index = IndexOf(units, u => string.Equals(u.Id, unit.Id, StringComparison.Ordinal));

        var packFile = string.IsNullOrEmpty(planned.Pack.RelativePath) ? null : planned.Pack.RelativePath.TrimEnd('/') + "/pack.json";
        var pointer = index >= 0 ? "/units/" + index.ToString(CultureInfo.InvariantCulture) + "/formatter" : null;
        diagnostics.Add(new Diagnostic("MQ6014", DiagnosticSeverity.Warning,
            $"Unit '{unit.Id}' of pack '{planned.Pack.Name}' names formatter '{unit.Formatter}', which maquettiste.json does not configure; its files are not formatted.",
            null, packFile, pointer, null, null));
        return Selection.None;
    }

    private static int IndexOf(IReadOnlyList<PackUnit> units, Func<PackUnit, bool> match)
    {
        for (var i = 0; i < units.Count; i++)
        {
            if (match(units[i]))
                return i;
        }

        return -1;
    }

    /// <summary>Processes one file; <see langword="null"/> when it failed (diagnostics added).</summary>
    private async Task<OutputFile?> ProcessFileAsync(RenderedFile file, PackUnit unit, Selection selection, string? elementId, string repoRoot,
        PostProcessContext context, List<Diagnostic> diagnostics, CancellationToken ct)
    {
        var check = context.Paths.Check(file.Path);
        if (!check.Allowed || check.Root is null)
        {
            diagnostics.Add(Error("MQ6004", $"Output path '{file.Path}' refused: {check.Reason ?? "outside every output root"}.", elementId, file.Path));
            return null;
        }

        var path = check.NormalizedPath;
        var mode = FileMode(unit.Mode, file.Role);
        if (mode == OutputMode.Regions && !check.Root.Commit)
        {
            diagnostics.Add(Error("MQ6015",
                $"Unit '{unit.Id}' uses regions mode, but '{path}' is under the built root '{check.Root.Path}'; regions work only on committed roots (use pair or once).",
                elementId, path));
            return null;
        }

        // (1) Normalize and encode. A template that slices a string inside a surrogate pair fails this unit, not the run.
        var text = TextNormalizer.Normalize(file.Text);
        if (!TextNormalizer.TryEncode(text, out var bytes))
        {
            diagnostics.Add(NotUnicode(path, elementId));
            return null;
        }

        // (2) Format.
        var formatter = selection.UseExtension ? ByExtension(path, context.Formatters) : selection.Formatter;
        if (formatter is not null)
        {
            var formatted = await FormatAsync(formatter, path, bytes, elementId, context, diagnostics, ct).ConfigureAwait(false);
            if (formatted is null)
                return null;
            text = formatted;
        }

        // (3) Protected regions.
        string? skeletonHash = null;
        if (mode == OutputMode.Regions)
        {
            var merged = await MergeRegionsAsync(path, text, elementId, repoRoot, diagnostics, ct).ConfigureAwait(false);
            if (merged is null)
                return null;
            text = merged.Value.Text;
            skeletonHash = merged.Value.SkeletonHash;
        }

        // (4) Hash. Formatter output and kept region bodies were decoded from valid UTF-8, so this re-encode cannot fail in
        // practice; it is checked anyway so no text can throw out of the stage.
        if (!TextNormalizer.TryEncode(text, out bytes))
        {
            diagnostics.Add(NotUnicode(path, elementId));
            return null;
        }

        var contentHash = ContentHash.Of(bytes);
        var owned = mode == OutputMode.Once || file.Role == FileRole.Companion;
        var manifestHash = skeletonHash ?? (owned ? "o:" + contentHash : contentHash);
        return new OutputFile(path, bytes, contentHash, manifestHash, mode, file.Role, check.Root);
    }

    private async Task<string?> FormatAsync(FormatterSettings formatter, string path, byte[] input, string? elementId, PostProcessContext context,
        List<Diagnostic> diagnostics, CancellationToken ct)
    {
        if (!context.FormatterVersionsVerified)
        {
            var versionError = await VerifyOnceAsync(formatter, context, ct).ConfigureAwait(false);
            if (versionError is not null)
            {
                diagnostics.Add(versionError with { ElementId = elementId, Message = versionError.Message + $" '{path}' is not formatted." });
                return null;
            }
        }

        var result = await formatters.FormatAsync(formatter, path, input, ct).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            var error = result.Error ?? new Diagnostic("MQ6008", DiagnosticSeverity.Error, $"Formatter '{formatter.Name}' failed.", null, path, null, null, null);
            diagnostics.Add(error with { ElementId = error.ElementId ?? elementId, FilePath = error.FilePath ?? path });
            return null;
        }

        if (!TextNormalizer.TryDecode(result.Output.Span, out var formatted))
        {
            diagnostics.Add(Error("MQ6008", $"Formatter '{formatter.Name}' wrote output that is not valid UTF-8 for '{path}'.", elementId, path));
            return null;
        }

        return formatted;
    }

    /// <summary>Checks a formatter's pinned version once per run (per context) when the orchestrator did not.</summary>
    private Task<Diagnostic?> VerifyOnceAsync(FormatterSettings formatter, PostProcessContext context, CancellationToken ct)
    {
        var cache = _runs.GetValue(context, _ => new RunState()).Versions;
        var index = -1;
        for (var i = 0; i < context.Formatters.Count; i++)
        {
            if (ReferenceEquals(context.Formatters[i], formatter))
            {
                index = i;
                break;
            }
        }

        var key = index.ToString(CultureInfo.InvariantCulture) + "/" + formatter.Name;
        var lazy = cache.GetOrAdd(key, _ => new Lazy<Task<Diagnostic?>>(async () =>
        {
            var found = await formatters.VerifyVersionsAsync([formatter], ct).ConfigureAwait(false);
            if (found.Count == 0)
                return null;
            var first = found[0];
            return index >= 0 ? first with { JsonPointer = "/formatters/" + index.ToString(CultureInfo.InvariantCulture) } : first;
        }));
        return lazy.Value;
    }

    /// <summary>Step 3: merges the disk file's region bodies into the new text and computes the skeleton hash.</summary>
    private static async Task<(string Text, string SkeletonHash)?> MergeRegionsAsync(string path, string text, string? elementId, string repoRoot,
        List<Diagnostic> diagnostics, CancellationToken ct)
    {
        var generated = ProtectedRegions.Parse(text);
        if (generated.Error is not null)
        {
            diagnostics.Add(Error("MQ6006", $"The generated '{path}' has malformed protected regions: {generated.Error}.", elementId, path));
            return null;
        }

        var merged = text;
        var fullPath = Path.Combine(repoRoot, path.Replace('/', Path.DirectorySeparatorChar));
        if (File.Exists(fullPath))
        {
            byte[] disk;
            try
            {
                disk = await File.ReadAllBytesAsync(fullPath, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                diagnostics.Add(Error("MQ6010", $"Cannot read '{path}' to keep its protected regions: {ex.Message}", elementId, path));
                return null;
            }

            if (!TextNormalizer.TryDecode(disk, out var diskText))
            {
                diagnostics.Add(Error("MQ6010", $"'{path}' on disk is not valid UTF-8, so its protected regions cannot be kept.", elementId, path));
                return null;
            }

            var existing = ProtectedRegions.Parse(diskText);
            if (existing.Error is not null)
            {
                diagnostics.Add(Error("MQ6010", $"'{path}' on disk has malformed protected regions ({existing.Error}), so they cannot be kept.", elementId, path));
                return null;
            }

            merged = ProtectedRegions.Merge(generated, existing, out var lost);
            if (lost.Count > 0)
            {
                diagnostics.Add(Error("MQ6010",
                    $"'{path}' on disk has protected regions the new output no longer has ({string.Join(", ", lost.Select(id => "'" + id + "'"))}); " +
                    "their content would be lost. Move it, or restore the regions in the template.",
                    elementId, path));
                return null;
            }

            generated = ProtectedRegions.Parse(merged);
            if (generated.Error is not null)
            {
                // A kept body that itself holds marker lines cannot happen (the disk parse rejects it); guard anyway.
                diagnostics.Add(Error("MQ6010", $"Merging the protected regions of '{path}' produced malformed markers: {generated.Error}.", elementId, path));
                return null;
            }
        }

        var skeleton = ProtectedRegions.Skeleton(generated);
        return (merged, "r:" + ContentHash.Of(TextNormalizer.StrictUtf8.GetBytes(skeleton)));
    }

    private static Diagnostic NotUnicode(string path, string? elementId) =>
        Error("MQ6006", $"The rendered text of '{path}' is not valid Unicode (it holds an unpaired UTF-16 surrogate, for example a string cut inside an emoji).",
            elementId, path);

    private static Diagnostic Error(string rule, string message, string? elementId, string? path) =>
        new(rule, DiagnosticSeverity.Error, message, elementId, path, null, null, null);

    /// <summary>State kept for one run.</summary>
    private sealed class RunState
    {
        /// <summary>Version checks by formatter index and name.</summary>
        public ConcurrentDictionary<string, Lazy<Task<Diagnostic?>>> Versions { get; } = new(StringComparer.Ordinal);

        /// <summary>The <c>&lt;pack&gt;/&lt;unit id&gt;</c> of units already warned about an unknown formatter (MQ6014 once per unit per run).</summary>
        public ConcurrentDictionary<string, byte> WarnedUnits { get; } = new(StringComparer.Ordinal);
    }

    /// <summary>A unit's formatter choice.</summary>
    /// <param name="Formatter">The named formatter.</param>
    /// <param name="UseExtension">Whether to pick per file by extension.</param>
    private sealed record Selection(FormatterSettings? Formatter, bool UseExtension)
    {
        public static readonly Selection ByExtension = new(null, true);
        public static readonly Selection None = new(null, false);
    }
}
