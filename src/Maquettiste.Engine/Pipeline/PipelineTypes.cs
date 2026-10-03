using System.Text.Json;
using System.Text.Json.Serialization;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Resolution;

namespace Maquettiste.Engine.Pipeline;

/// <summary>The eight pipeline stages (SPEC section 12).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<PipelineStage>))]
public enum PipelineStage
{
    /// <summary>1: load model files and the index cache.</summary>
    [JsonStringEnumMemberName("load")] Load = 1,

    /// <summary>2: validate; any error stops the run.</summary>
    [JsonStringEnumMemberName("validate")] Validate,

    /// <summary>3: resolve conventions and mappings.</summary>
    [JsonStringEnumMemberName("resolve")] Resolve,

    /// <summary>4: plan units (template × element).</summary>
    [JsonStringEnumMemberName("plan")] Plan,

    /// <summary>5: skip units whose input hash is unchanged.</summary>
    [JsonStringEnumMemberName("skip")] Skip,

    /// <summary>6: render in parallel.</summary>
    [JsonStringEnumMemberName("render")] Render,

    /// <summary>7: post-process (line endings, regions, formatters).</summary>
    [JsonStringEnumMemberName("post-process")] PostProcess,

    /// <summary>8: write changed files, the manifest and orphans.</summary>
    [JsonStringEnumMemberName("write")] Write,
}

/// <summary>A progress report at file (or element) granularity.</summary>
/// <param name="Stage">The current stage.</param>
/// <param name="Done">Items done in the stage.</param>
/// <param name="Total">Items in the stage.</param>
/// <param name="CurrentPath">The file or element being processed, when relevant.</param>
/// <param name="Pack">The pack being processed, when relevant.</param>
public sealed record ProgressUpdate(PipelineStage Stage, int Done, int Total, string? CurrentPath, string? Pack);

/// <summary>Timing of one stage.</summary>
/// <param name="Stage">The stage.</param>
/// <param name="Wall">Wall-clock time from the stage's start to its end.</param>
/// <param name="Busy">Summed busy time of the stage's workers.</param>
/// <param name="Items">Items processed.</param>
public sealed record StageTiming(PipelineStage Stage, TimeSpan Wall, TimeSpan Busy, int Items);

/// <summary>How a generation run treats its output.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<GenerationMode>))]
public enum GenerationMode
{
    /// <summary>Write changed files: <c>apply</c>.</summary>
    [JsonStringEnumMemberName("apply")] Apply,

    /// <summary>Compute changes and diffs, write nothing: <c>dry-run</c>.</summary>
    [JsonStringEnumMemberName("dry-run")] DryRun,

    /// <summary>Render every unit in memory over every root and report drift: <c>check</c>.</summary>
    [JsonStringEnumMemberName("check")] Check,
}

/// <summary>The role of a rendered file within its unit.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<FileRole>))]
public enum FileRole
{
    /// <summary>The unit's <c>output</c> file: <c>main</c>.</summary>
    [JsonStringEnumMemberName("main")] Main,

    /// <summary>A file emitted by a <c>file</c> block: <c>block</c>.</summary>
    [JsonStringEnumMemberName("block")] Block,

    /// <summary>A <c>pair</c> unit's companion: <c>companion</c>.</summary>
    [JsonStringEnumMemberName("companion")] Companion,
}

/// <summary>Records the dependency keys a unit reads (engine-design.md section 11). One per unit; single-threaded.</summary>
public interface IReadRecorder
{
    /// <summary>Records a dependency key such as <c>e:&lt;id&gt;</c>, <c>k:entity</c> or <c>t:&lt;pack&gt;/&lt;path&gt;</c>.</summary>
    /// <param name="dependencyKey">The key.</param>
    void Record(string dependencyKey);
}

/// <summary>A loaded, enabled template pack.</summary>
/// <param name="Name">The pack name.</param>
/// <param name="Order">The pack's position in ordinal name order.</param>
/// <param name="RootPath">The absolute pack folder.</param>
/// <param name="RelativePath">The repo-relative pack folder.</param>
/// <param name="Manifest">The pack manifest.</param>
/// <param name="Settings">The effective pack settings.</param>
/// <param name="Parameters">Effective parameters: the manifest's defaults overridden by settings.</param>
/// <param name="Scripts">The pack's scripts, in load order.</param>
/// <param name="ScriptsHash">The hash over the scripts.</param>
/// <param name="TypeMaps">Type maps from <c>types/&lt;target&gt;.json</c>: target → keyword or pattern name → language type.</param>
public sealed record LoadedPack(
    string Name,
    int Order,
    string RootPath,
    string RelativePath,
    PackManifest Manifest,
    PackSettings Settings,
    IReadOnlyDictionary<string, JsonElement> Parameters,
    IReadOnlyList<ScriptSource> Scripts,
    string ScriptsHash,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> TypeMaps);

/// <summary>A JavaScript source file.</summary>
/// <param name="Path">The repo-relative path.</param>
/// <param name="Code">The source text.</param>
/// <param name="Hash">The file hash.</param>
public sealed record ScriptSource(string Path, string Code, string Hash);

/// <summary>The packs of a run.</summary>
/// <param name="Packs">Enabled packs, in ordinal name order.</param>
/// <param name="Diagnostics">Pack loading diagnostics (MQ6001, MQ6002).</param>
public sealed record PackSet(IReadOnlyList<LoadedPack> Packs, IReadOnlyList<Diagnostic> Diagnostics)
{
    /// <summary>
    /// Whether the planner skips a unit whose output pattern cannot stay under an allowed root (MQ6019). The paths operation turns it
    /// off, because it reports MQ6019 itself and still shows where each path would go.
    /// </summary>
    public bool CheckOutputRoots { get; init; } = true;
}

/// <summary>One planned render unit.</summary>
/// <param name="Key"><c>&lt;pack&gt;/&lt;unitId&gt;</c> for model scope, <c>&lt;pack&gt;/&lt;unitId&gt;:&lt;elementId&gt;</c> otherwise.</param>
/// <param name="Pack">The pack.</param>
/// <param name="Unit">The unit definition.</param>
/// <param name="Element">The resolved element, or <see langword="null"/> for model scope.</param>
/// <param name="StaticHash">The unit's static input hash (engine-design.md section 11).</param>
public sealed record PlannedUnit(string Key, LoadedPack Pack, PackUnit Unit, IResolvedObject? Element, string StaticHash)
{
    /// <summary>
    /// The parts <see cref="StaticHash"/> combines, one <c>name=value</c> line each, ordinal by name (pack version, unit definition,
    /// each parameter, scripts, output base, formatter, templates), shared by every element of one pack unit; only for explanation
    /// (generation-ui.md section 4.2), <see langword="null"/> when unknown.
    /// </summary>
    public string? StaticParts { get; init; }
}

/// <summary>The planned units of a run.</summary>
/// <param name="Units">Units in pack order, then ordinal by key.</param>
/// <param name="Diagnostics">Planning diagnostics.</param>
public sealed record UnitPlan(IReadOnlyList<PlannedUnit> Units, IReadOnlyList<Diagnostic> Diagnostics);

/// <summary>One output file recorded in a unit's state.</summary>
/// <param name="Path">The repo-relative path.</param>
/// <param name="ManifestHash">The manifest hash (with any <c>r:</c> or <c>o:</c> prefix).</param>
/// <param name="Length">The file length when written.</param>
/// <param name="LastWriteUtcTicks">The file's last-write time when written.</param>
public sealed record UnitOutput(string Path, string ManifestHash, long Length, long LastWriteUtcTicks);

/// <summary>What a unit read and wrote in its last rendered run.</summary>
/// <param name="Key">The unit key.</param>
/// <param name="InputHash">The input hash.</param>
/// <param name="ReadKeys">The recorded dependency keys, ordinal.</param>
/// <param name="Outputs">The files the unit produced.</param>
public sealed record UnitState(string Key, string InputHash, IReadOnlyList<string> ReadKeys, IReadOnlyList<UnitOutput> Outputs)
{
    /// <summary>
    /// Each read key's hash at render time, truncated to 16 bytes, in <see cref="ReadKeys"/> order (unit state format 3); empty when
    /// not recorded. Only for explanation: the skip decision uses <see cref="InputHash"/>.
    /// </summary>
    public ReadOnlyMemory<byte> KeyHashes { get; init; }

    /// <summary>The static parts at render time (<see cref="PlannedUnit.StaticParts"/>), or <see langword="null"/>.</summary>
    public string? StaticParts { get; init; }

    /// <summary>
    /// The label (<c>Name (kind)</c>) of each element key (<c>e:&lt;id&gt;</c>) read, at render time (unit state format 4), so a cause can
    /// name an element the model no longer has ("Customer (entity) was deleted"); <see langword="null"/> when not recorded.
    /// </summary>
    public IReadOnlyDictionary<string, string>? Names { get; init; }
}

/// <summary>A unit skipped because its inputs and outputs are unchanged.</summary>
/// <param name="Unit">The unit.</param>
/// <param name="Previous">Its stored state.</param>
public sealed record SkippedUnit(PlannedUnit Unit, UnitState Previous);

/// <summary>The result of stage 5.</summary>
/// <param name="ToRender">Units to render, in plan order.</param>
/// <param name="Skipped">Units skipped.</param>
public sealed record SkipResult(IReadOnlyList<PlannedUnit> ToRender, IReadOnlyList<SkippedUnit> Skipped);

/// <summary>One rendered file.</summary>
/// <param name="Path">The repo-relative path with <c>/</c> separators.</param>
/// <param name="Text">The rendered text.</param>
/// <param name="Role">The file's role.</param>
public sealed record RenderedFile(string Path, string Text, FileRole Role);

/// <summary>The result of rendering one unit.</summary>
/// <param name="Unit">The unit.</param>
/// <param name="Files">The rendered files.</param>
/// <param name="ReadKeys">The dependency keys read, ordinal.</param>
/// <param name="InputHash">The input hash computed from the static hash and the read keys.</param>
/// <param name="Diagnostics">Render diagnostics.</param>
/// <param name="Failed">Whether the unit failed; a failed unit keeps its previous outputs and state.</param>
public sealed record RenderedUnit(
    PlannedUnit Unit,
    IReadOnlyList<RenderedFile> Files,
    IReadOnlyList<string> ReadKeys,
    string InputHash,
    IReadOnlyList<Diagnostic> Diagnostics,
    bool Failed)
{
    /// <summary>Each read key's current hash, truncated to 16 bytes, in <see cref="ReadKeys"/> order (see <see cref="UnitState.KeyHashes"/>).</summary>
    public ReadOnlyMemory<byte> KeyHashes { get; init; }

    /// <summary>The labels of the element keys read (see <see cref="UnitState.Names"/>).</summary>
    public IReadOnlyDictionary<string, string>? Names { get; init; }
}

/// <summary>The output root that contains a file.</summary>
/// <param name="Path">The root's repo-relative folder, or the file it names (an <c>outputs.allow</c> entry that is the file's own path).</param>
public sealed record OutputRootInfo(string Path);

/// <summary>A post-processed file ready for stage 8.</summary>
/// <param name="Path">The repo-relative path.</param>
/// <param name="Content">The bytes to write (UTF-8, LF, no BOM); for a <c>block</c> file, the block's lines only (the writer puts them in the file).</param>
/// <param name="ContentHash">The hash of <paramref name="Content"/>.</param>
/// <param name="ManifestHash">The manifest hash: the content hash, <c>r:</c> + skeleton hash, <c>o:</c> + content hash, or for a
/// <c>block</c> file <c>b:</c> + the hash of the block's lines (the writer turns it into <c>bc:</c> when it creates the file).</param>
/// <param name="Mode">The output mode.</param>
/// <param name="Role">The file's role.</param>
/// <param name="Root">The containing output root.</param>
/// <param name="ContentOmitted">
/// Whether <paramref name="Content"/> is not carried: an apply of a stored plan passes a file the plan found unchanged by hash only.
/// The writer counts it as produced (never an orphan), never writes it, and treats a disk hash other than
/// <paramref name="ContentHash"/> as a conflict.
/// </param>
public sealed record OutputFile(
    string Path,
    ReadOnlyMemory<byte> Content,
    string ContentHash,
    string ManifestHash,
    OutputMode Mode,
    FileRole Role,
    OutputRootInfo Root,
    bool ContentOmitted = false);

/// <summary>The result of stage 7 for one unit.</summary>
/// <param name="Rendered">The rendered unit.</param>
/// <param name="Files">The processed files.</param>
/// <param name="Diagnostics">Post-processing diagnostics.</param>
/// <param name="Failed">Whether the unit failed.</param>
public sealed record ProcessedUnit(RenderedUnit Rendered, IReadOnlyList<OutputFile> Files, IReadOnlyList<Diagnostic> Diagnostics, bool Failed);

/// <summary>The decision for one output file (engine-design.md section 12.3).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<FileChangeKind>))]
public enum FileChangeKind
{
    /// <summary>A new file: <c>added</c> (dry-run letter A).</summary>
    [JsonStringEnumMemberName("added")] Added,

    /// <summary>A changed file: <c>modified</c> (M).</summary>
    [JsonStringEnumMemberName("modified")] Modified,

    /// <summary>An orphan removed: <c>deleted</c> (D).</summary>
    [JsonStringEnumMemberName("deleted")] Deleted,

    /// <summary>Identical bytes; not written: <c>unchanged</c>.</summary>
    [JsonStringEnumMemberName("unchanged")] Unchanged,

    /// <summary>Disk content differs from the manifest: <c>hand-edited</c> (H).</summary>
    [JsonStringEnumMemberName("hand-edited")] HandEdited,

    /// <summary>An owned file that exists and is kept: <c>kept</c> (K).</summary>
    [JsonStringEnumMemberName("kept")] Kept,

    /// <summary>An owned file no longer produced; kept on disk, dropped from the manifest: <c>orphaned-owned</c> (O).</summary>
    [JsonStringEnumMemberName("orphaned-owned")] OrphanedOwned,

    /// <summary>A hand edit under the <c>fail</c> policy, or a lost region: <c>conflict</c>.</summary>
    [JsonStringEnumMemberName("conflict")] Conflict,

    /// <summary>
    /// Plans only: an output of a unit the plan skipped because nothing it read changed, listed from the unit's stored state so the
    /// plan names every file; nothing is written: <c>not-rendered</c>.
    /// </summary>
    [JsonStringEnumMemberName("not-rendered")] NotRendered,
}

/// <summary>One file decision.</summary>
/// <param name="Path">The repo-relative path.</param>
/// <param name="Kind">The decision.</param>
/// <param name="Pack">The pack.</param>
/// <param name="UnitKey">The producing unit's key.</param>
/// <param name="OldHash">The previous manifest (or disk) hash.</param>
/// <param name="NewHash">The new manifest hash.</param>
/// <param name="Diff">A unified diff, only when requested.</param>
public sealed record FileChange(string Path, FileChangeKind Kind, string Pack, string UnitKey, string? OldHash, string? NewHash, string? Diff);

/// <summary>The result of stage 8.</summary>
/// <param name="Changes">Every file except unchanged ones (unless <see cref="WriteContext.ListUnchanged"/>), sorted by path.</param>
/// <param name="Diagnostics">Write diagnostics.</param>
/// <param name="Written">Files written.</param>
/// <param name="Deleted">Files deleted.</param>
public sealed record WriteSummary(IReadOnlyList<FileChange> Changes, IReadOnlyList<Diagnostic> Diagnostics, int Written, int Deleted);
