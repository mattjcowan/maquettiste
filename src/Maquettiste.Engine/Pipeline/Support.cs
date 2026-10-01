using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Resolution;
using Maquettiste.Engine.SchemaDiff;

namespace Maquettiste.Engine.Pipeline;

/// <summary>Current hashes of dependency keys, and unit input hashes (W6; engine-design.md section 11).</summary>
public interface IDependencyHasher
{
    /// <summary>Returns the current hash of a dependency key, or <c>"absent"</c> when it no longer resolves.</summary>
    /// <param name="dependencyKey">The key.</param>
    /// <returns>The hash.</returns>
    string CurrentHash(string dependencyKey);

    /// <summary>Computes <c>H(staticHash, key₁, hash₁, …)</c> over read keys in ordinal order.</summary>
    /// <param name="staticHash">The unit's static hash.</param>
    /// <param name="sortedReadKeys">The read keys, sorted ordinal.</param>
    /// <returns>The input hash.</returns>
    string InputHash(string staticHash, IReadOnlyList<string> sortedReadKeys);
}

/// <summary>Stores unit states per pack in <c>CacheDirectory/units/&lt;pack&gt;.v1.bin</c> (W6).</summary>
public interface IUnitStateStore
{
    /// <summary>Loads a pack's unit states.</summary>
    /// <param name="pack">The pack name.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>States by unit key.</returns>
    Task<IReadOnlyDictionary<string, UnitState>> LoadAsync(string pack, CancellationToken ct);

    /// <summary>Saves a pack's unit states, replacing the stored ones.</summary>
    /// <param name="pack">The pack name.</param>
    /// <param name="states">The states.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>A task.</returns>
    Task SaveAsync(string pack, IReadOnlyCollection<UnitState> states, CancellationToken ct);

    /// <summary>
    /// Whether the last load of a pack found a state file it could not use (another engine version or format), so every unit renders
    /// with the cause <c>state-reset</c> (generation-ui.md section 4.2).
    /// </summary>
    /// <param name="pack">The pack name.</param>
    /// <returns><see langword="true"/> after such a load, until the pack's states are saved.</returns>
    bool WasReset(string pack) => false;
}

/// <summary>Where an engine write that is not generated output goes.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<WriteTarget>))]
public enum WriteTarget
{
    /// <summary>Generated output under an output root: <c>output</c>.</summary>
    [JsonStringEnumMemberName("output")] Output,

    /// <summary>Under <c>ModelRoot</c>: model files, manifests, snapshots, <c>.schema</c>: <c>model</c>.</summary>
    [JsonStringEnumMemberName("model")] Model,

    /// <summary>Under <c>CacheDirectory</c> or <c>JournalDirectory</c>: <c>cache</c>.</summary>
    [JsonStringEnumMemberName("cache")] Cache,

    /// <summary>
    /// <c>init</c> only: the git hooks (<c>--hooks</c>), <c>.mcp.json</c>, the modeling skill, and the repository's <c>.gitignore</c>
    /// only for <c>--gitignore</c>: <c>setup</c>.
    /// </summary>
    [JsonStringEnumMemberName("setup")] Setup,
}

/// <summary>The output path allowlist, and the guard for every engine write (W7; engine-design.md section 12.1; S23).</summary>
public interface IOutputPathPolicy
{
    /// <summary>Checks a generated output path.</summary>
    /// <param name="repoRelativePath">A repo-relative path with <c>/</c> separators.</param>
    /// <returns>The decision.</returns>
    PathCheck Check(string repoRelativePath);

    /// <summary>Checks an engine write that is not generated output.</summary>
    /// <param name="target">The write's target.</param>
    /// <param name="fullPath">The absolute path.</param>
    /// <returns>The decision.</returns>
    PathCheck CheckEngineWrite(WriteTarget target, string fullPath);
}

/// <summary>A path policy decision.</summary>
/// <param name="Allowed">Whether the write is allowed.</param>
/// <param name="NormalizedPath">The normalized path.</param>
/// <param name="Root">The containing output root, for output paths.</param>
/// <param name="RuleId">The rule of a refusal (MQ6004, MQ6005).</param>
/// <param name="Reason">Why the write was refused.</param>
public sealed record PathCheck(bool Allowed, string NormalizedPath, OutputRootInfo? Root, string? RuleId, string? Reason);

/// <summary>An immutable view of every pack's manifest (W7).</summary>
public sealed class ManifestSet
{
    /// <summary>Creates an empty manifest set.</summary>
    internal ManifestSet()
        : this(Writing.ManifestSetData.Empty)
    {
    }

    /// <summary>Creates a manifest set over loaded data (W7's <see cref="Writing.ManifestStore"/>).</summary>
    /// <param name="data">The data.</param>
    internal ManifestSet(Writing.ManifestSetData data) => Data = data;

    /// <summary>The data behind the set (W7).</summary>
    internal Writing.ManifestSetData Data { get; }

    /// <summary>Packs with any manifest file.</summary>
    public IReadOnlyCollection<string> Packs => Data.Packs;

    /// <summary>Finds the manifest entry of a path.</summary>
    /// <param name="path">A repo-relative path.</param>
    /// <param name="entry">The entry when found.</param>
    /// <param name="pack">The pack that owns it when found.</param>
    /// <returns><see langword="true"/> when the path is in a manifest.</returns>
    public bool TryGet(string path, [MaybeNullWhen(false)] out ManifestEntry entry, [MaybeNullWhen(false)] out string pack) =>
        Data.TryGet(path, out entry, out pack);

    /// <summary>Returns a pack's entries for committed or built roots, ordinal by path.</summary>
    /// <param name="pack">The pack name.</param>
    /// <param name="committed">Committed (<see langword="true"/>) or built roots.</param>
    /// <returns>The entries.</returns>
    public IReadOnlyList<ManifestEntry> Entries(string pack, bool committed) => Data.Entries(pack, committed);

    /// <summary>Overlays an unfinished journal's entries for packs without a <c>pack</c> line (resume; engine-design.md section 12.4).</summary>
    /// <param name="records">The journal records.</param>
    /// <returns>A new set.</returns>
    public ManifestSet WithJournalOverlay(IReadOnlyList<JournalRecord> records) => new(Data.WithJournalOverlay(records));
}

/// <summary>One line of the run journal.</summary>
/// <param name="Type"><c>begin</c>, <c>write</c>, <c>delete</c>, <c>pack</c> or <c>end</c> (JSON <c>t</c>).</param>
/// <param name="Pack">The pack.</param>
/// <param name="Path">The file path.</param>
/// <param name="Hash">The manifest hash written.</param>
/// <param name="Unit">The unit.</param>
public sealed record JournalRecord(string Type, string? Pack, string? Path, string? Hash, string? Unit);

/// <summary>Loads and saves per-pack manifests (W7; engine-design.md section 12.2).</summary>
public interface IManifestStore
{
    /// <summary>Loads the manifests of packs (committed and built).</summary>
    /// <param name="packs">Pack names; every manifest file found is included when the set is empty.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The set.</returns>
    Task<ManifestSet> LoadAsync(IReadOnlyCollection<string> packs, CancellationToken ct);

    /// <summary>Saves a pack's manifest for committed or built roots; an empty list deletes the file.</summary>
    /// <param name="pack">The pack name.</param>
    /// <param name="committed">Committed or built roots.</param>
    /// <param name="entries">The entries, in any order.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>A task.</returns>
    Task SavePackAsync(string pack, bool committed, IReadOnlyList<ManifestEntry> entries, CancellationToken ct);
}

/// <summary>One manifest entry.</summary>
/// <param name="Path">The repo-relative path.</param>
/// <param name="Hash">The manifest hash (with any <c>r:</c> or <c>o:</c> prefix).</param>
/// <param name="Unit"><c>&lt;unitId&gt;</c> or <c>&lt;unitId&gt;:&lt;elementId&gt;</c>, plus <c>#companion</c> for companions.</param>
public sealed record ManifestEntry(string Path, string Hash, string Unit);

/// <summary>The run journal: <c>JournalDirectory/journal.jsonl</c> (W7; engine-design.md section 12.4).</summary>
public interface IRunJournal : IAsyncDisposable
{
    /// <summary>Reads a journal left without an <c>end</c> line.</summary>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The records, or <see langword="null"/> when there is no unfinished journal.</returns>
    Task<IReadOnlyList<JournalRecord>?> ReadUnfinishedAsync(CancellationToken ct);

    /// <summary>Starts a journal.</summary>
    /// <param name="runId">The run id.</param>
    /// <param name="planId">The applied plan's id, if any.</param>
    /// <param name="packs">The run's packs.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>A task.</returns>
    Task BeginAsync(string runId, string? planId, IReadOnlyList<string> packs, CancellationToken ct);

    /// <summary>Records a written file; flushed to the OS before returning.</summary>
    /// <param name="pack">The pack.</param>
    /// <param name="entry">The new manifest entry.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>A task.</returns>
    ValueTask RecordWriteAsync(string pack, ManifestEntry entry, CancellationToken ct);

    /// <summary>Records a deleted file; flushed to the OS before returning.</summary>
    /// <param name="pack">The pack.</param>
    /// <param name="path">The deleted path.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>A task.</returns>
    ValueTask RecordDeleteAsync(string pack, string path, CancellationToken ct);

    /// <summary>Records that a pack's manifest and unit state are saved; fsynced.</summary>
    /// <param name="pack">The pack.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>A task.</returns>
    ValueTask RecordPackCompleteAsync(string pack, CancellationToken ct);

    /// <summary>Writes <c>end</c> and deletes the journal.</summary>
    /// <param name="ct">Cancellation.</param>
    /// <returns>A task.</returns>
    Task EndAsync(CancellationToken ct);
}

/// <summary>The exclusive run lock: <c>JournalDirectory/run.lock</c> (W7).</summary>
public interface IRunLock
{
    /// <summary>Acquires the lock.</summary>
    /// <param name="wait">Whether to poll (every 100 ms) until the lock is free.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>A handle that releases the lock, or <see langword="null"/> when busy and not waiting.</returns>
    Task<IAsyncDisposable?> AcquireAsync(bool wait, CancellationToken ct);
}

/// <summary>Produces unified diffs (W7).</summary>
public interface IDiffGenerator
{
    /// <summary>Returns a unified diff of two UTF-8 texts.</summary>
    /// <param name="path">The path shown in the header.</param>
    /// <param name="before">The old bytes (empty for an added file).</param>
    /// <param name="after">The new bytes (empty for a deleted file).</param>
    /// <param name="context">Context lines.</param>
    /// <returns>The diff text.</returns>
    string Unified(string path, ReadOnlySpan<byte> before, ReadOnlySpan<byte> after, int context = 3);
}

/// <summary>Runs formatters over stdin and stdout (W8; engine-design.md section 13).</summary>
public interface IFormatterRunner
{
    /// <summary>Checks each formatter's pinned version once.</summary>
    /// <param name="formatters">The formatters used by the run.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>MQ6008 diagnostics for mismatches.</returns>
    Task<IReadOnlyList<Diagnostic>> VerifyVersionsAsync(IReadOnlyList<FormatterSettings> formatters, CancellationToken ct);

    /// <summary>Formats one file.</summary>
    /// <param name="formatter">The formatter.</param>
    /// <param name="path">The repo-relative path (for <c>{path}</c>).</param>
    /// <param name="input">The input bytes.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The result.</returns>
    Task<FormatResult> FormatAsync(FormatterSettings formatter, string path, ReadOnlyMemory<byte> input, CancellationToken ct);
}

/// <summary>The result of formatting one file.</summary>
/// <param name="Succeeded">Whether the formatter exited with 0 within its timeout.</param>
/// <param name="Output">The formatted bytes.</param>
/// <param name="Error">An MQ6008 diagnostic when it failed.</param>
public sealed record FormatResult(bool Succeeded, ReadOnlyMemory<byte> Output, Diagnostic? Error);

/// <summary>Loads and saves physical snapshots (W8; engine-design.md section 14).</summary>
public interface ISnapshotStore
{
    /// <summary>Loads a database's snapshot.</summary>
    /// <param name="databaseName">The database name.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The snapshot, or <see langword="null"/> when none exists.</returns>
    Task<PhysicalSnapshot?> LoadAsync(string databaseName, CancellationToken ct);

    /// <summary>Saves a snapshot through the canonical writer.</summary>
    /// <param name="snapshot">The snapshot.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>A task.</returns>
    Task SaveAsync(PhysicalSnapshot snapshot, CancellationToken ct);
}

/// <summary>Captures and diffs physical models (W8).</summary>
public interface ISchemaDiffer
{
    /// <summary>Captures a resolved database as a snapshot.</summary>
    /// <param name="database">The resolved database.</param>
    /// <param name="revision">The revision to stamp.</param>
    /// <returns>The snapshot.</returns>
    PhysicalSnapshot Capture(RDatabase database, int revision);

    /// <summary>Diffs a previous snapshot against the current resolved database.</summary>
    /// <param name="previous">The previous snapshot, or <see langword="null"/> (everything is added).</param>
    /// <param name="current">The current resolved database.</param>
    /// <returns>The diff.</returns>
    SchemaDiffResult Diff(PhysicalSnapshot? previous, RDatabase current);
}
