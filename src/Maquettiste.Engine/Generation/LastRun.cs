using System.Collections.Frozen;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Hashing;
using Maquettiste.Engine.Loading;
using Maquettiste.Engine.Pipeline;
using Maquettiste.Engine.Planning;
using Maquettiste.Engine.Writing;

namespace Maquettiste.Engine.Generation;

/// <summary>One output recorded in a planned unit's state, as the skip check compares it with the disk.</summary>
/// <param name="Path">The repo-relative path.</param>
/// <param name="Owned">Whether the output is owned (<c>o:</c>): it only needs to exist.</param>
/// <param name="Length">The recorded length.</param>
/// <param name="LastWriteTicks">The recorded last-write time, UTC ticks.</param>
internal readonly record struct OutputStamp(string Path, bool Owned, long Length, long LastWriteTicks);

/// <summary>The files directly in one engine folder (every name, dot files included) with their stats.</summary>
/// <param name="Folder">The absolute folder.</param>
/// <param name="Files">The files, by name, ordinal.</param>
internal sealed record FolderStamp(string Folder, IReadOnlyList<FileStamp> Files);

/// <summary>
/// What a one-shot process needs to know about the last apply run to answer a run with unchanged inputs without loading,
/// validating, resolving or planning (Generation/README.md, "Last-run record"): the key of the run (engine build, folders, request
/// shape), the stat of every model file and referenced sidecar as the run read it, the content hash of the templates folder, the
/// engine's own files after the run (the stat of unit states and built-root manifests, the content hash of committed manifests and
/// schema snapshots), every output the planned units' states record, the number of planned units and the diagnostics of stages 1
/// to 5.
/// </summary>
/// <param name="Key">The run key (<see cref="LastRun.Key"/>).</param>
/// <param name="UnitsSkipped">The planned units, all of which a run with unchanged inputs skips.</param>
/// <param name="Diagnostics">The diagnostics of stages 1 to 5 (load, validate, pack load, resolve, plan), sorted.</param>
/// <param name="ModelFiles">The model files the load read (model-relative), ordinal.</param>
/// <param name="Sidecars">The referenced description sidecars that existed (model-relative), ordinal.</param>
/// <param name="MissingSidecars">The referenced sidecars that did not exist, ordinal.</param>
/// <param name="TemplatesHash">The content hash of every file under <c>&lt;ModelRoot&gt;/templates</c>, taken before the packs loaded
/// (<see cref="LastRun.HashTemplatesAsync"/>).</param>
/// <param name="EngineFolders">The stats of the engine folders after the run (<see cref="LastRun.EngineFolderPaths"/>).</param>
/// <param name="CommittedHash">The content hash of the committed engine files after the run (<see cref="LastRun.HashCommittedAsync"/>).</param>
/// <param name="Outputs">The outputs of every planned unit's state.</param>
internal sealed record RunRecord(
    string Key,
    int UnitsSkipped,
    IReadOnlyList<Diagnostic> Diagnostics,
    IReadOnlyList<FileStamp> ModelFiles,
    IReadOnlyList<FileStamp> Sidecars,
    IReadOnlyList<string> MissingSidecars,
    string TemplatesHash,
    IReadOnlyList<FolderStamp> EngineFolders,
    string CommittedHash,
    IReadOnlyList<OutputStamp> Outputs)
{
    /// <summary>The record file name in <see cref="EngineOptions.CacheDirectory"/>.</summary>
    public const string FileName = "last-run.v1.bin";

    private const int Format = 2;
    private const int HashLength = 32;
    private static ReadOnlySpan<byte> Magic => "MQLR"u8;

    /// <summary>
    /// The identity of the running engine build: the contract version, the module version ids of the engine and of the entry
    /// assembly (a new id for any change to their code, kept by ReadyToRun compilation), the runtime version and the application's
    /// dependency manifests (package versions). <see cref="EngineVersion.Value"/> alone is not enough: a build that changes a
    /// validation or resolution rule, or a message, without changing rendered bytes keeps it, and would replay the old diagnostics.
    /// </summary>
    internal static string CurrentBuild { get; } = BuildIdentity();

    /// <summary>
    /// Encodes the record: <c>MQLR</c>, format, engine version, engine build, then the fields in declaration order (strings
    /// length-prefixed UTF-8, integers little-endian), then the SHA-256 of everything before it, so a damaged file is ignored.
    /// </summary>
    /// <returns>The bytes.</returns>
    public byte[] Encode() => Encode([OutputChunk.Of(Outputs)]);

    /// <summary>Encodes the record with its outputs given as encoded chunks (<see cref="Outputs"/> is not used).</summary>
    /// <param name="outputs">The output chunks, in order.</param>
    /// <param name="build">The engine build written in the header (tests); <see cref="CurrentBuild"/> when omitted.</param>
    /// <returns>The bytes.</returns>
    public byte[] Encode(IReadOnlyList<OutputChunk> outputs, string? build = null)
    {
        ArgumentNullException.ThrowIfNull(outputs);
        using var buffer = new MemoryStream();
        using (var w = new BinaryWriter(buffer, Encoding.UTF8, leaveOpen: true))
        {
            w.Write(Magic);
            w.Write(Format);
            w.Write(EngineVersion.Value);
            w.Write(build ?? CurrentBuild);
            w.Write(Key);
            w.Write(UnitsSkipped);
            w.Write(Diagnostics.Count);
            foreach (var d in Diagnostics)
            {
                w.Write(d.Rule);
                w.Write((byte)d.Severity);
                w.Write(d.Message);
                WriteOptional(w, d.ElementId);
                WriteOptional(w, d.FilePath);
                WriteOptional(w, d.JsonPointer);
                WriteOptional(w, d.Line);
                WriteOptional(w, d.Column);
            }

            WriteStamps(w, ModelFiles);
            WriteStamps(w, Sidecars);
            w.Write(MissingSidecars.Count);
            foreach (var path in MissingSidecars)
                w.Write(path);
            w.Write(TemplatesHash);
            w.Write(EngineFolders.Count);
            foreach (var folder in EngineFolders)
            {
                w.Write(folder.Folder);
                WriteStamps(w, folder.Files);
            }

            w.Write(CommittedHash);

            w.Write(outputs.Sum(c => c.Count));
            foreach (var chunk in outputs)
                w.Write(chunk.Bytes);
        }

        var payload = buffer.GetBuffer().AsSpan(0, (int)buffer.Length);
        var result = new byte[payload.Length + HashLength];
        payload.CopyTo(result);
        SHA256.HashData(payload, result.AsSpan(payload.Length));
        return result;
    }

    /// <summary>Whether the bytes end with the SHA-256 of what precedes it (a damaged or truncated file does not).</summary>
    /// <param name="bytes">The file bytes.</param>
    /// <returns><see langword="true"/> when intact.</returns>
    public static bool Intact(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.Length < Magic.Length + HashLength)
            return false;
        Span<byte> hash = stackalloc byte[HashLength];
        SHA256.HashData(bytes.AsSpan(0, bytes.Length - HashLength), hash);
        return hash.SequenceEqual(bytes.AsSpan(bytes.Length - HashLength));
    }

    /// <summary>
    /// Decodes a record; <see langword="null"/> when the bytes are truncated or malformed, or from another format, engine version or
    /// engine build. The trailing hash is not checked here: a caller checks <see cref="Intact"/> before using any decoded field.
    /// Without <paramref name="outputs"/> decoding stops before the outputs (<see cref="Outputs"/> is then empty).
    /// </summary>
    /// <param name="bytes">The file bytes.</param>
    /// <param name="outputs">Whether to decode the outputs.</param>
    /// <param name="build">The engine build the header must name (tests); <see cref="CurrentBuild"/> when omitted.</param>
    /// <returns>The record, or <see langword="null"/>.</returns>
    public static RunRecord? Decode(byte[] bytes, bool outputs = true, string? build = null)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.Length < Magic.Length + HashLength)
            return null;
        var payloadLength = bytes.Length - HashLength;

        try
        {
            using var r = new BinaryReader(new MemoryStream(bytes, 0, payloadLength, writable: false), Encoding.UTF8);
            if (!r.ReadBytes(Magic.Length).AsSpan().SequenceEqual(Magic) || r.ReadInt32() != Format || r.ReadString() != EngineVersion.Value
                || r.ReadString() != (build ?? CurrentBuild))
                return null;
            var key = r.ReadString();
            var skipped = r.ReadInt32();
            var diagnostics = new Diagnostic[Count(r)];
            for (var i = 0; i < diagnostics.Length; i++)
            {
                var rule = r.ReadString();
                var severity = (DiagnosticSeverity)r.ReadByte();
                if (!Enum.IsDefined(severity))
                    return null;
                diagnostics[i] = new Diagnostic(rule, severity, r.ReadString(), ReadOptionalString(r), ReadOptionalString(r), ReadOptionalString(r),
                    ReadOptionalInt(r), ReadOptionalInt(r));
            }

            var model = ReadStamps(r);
            var sidecars = ReadStamps(r);
            var missing = new string[Count(r)];
            for (var i = 0; i < missing.Length; i++)
                missing[i] = r.ReadString();
            var templates = r.ReadString();
            var folders = new FolderStamp[Count(r)];
            for (var i = 0; i < folders.Length; i++)
                folders[i] = new FolderStamp(r.ReadString(), ReadStamps(r));
            var committed = r.ReadString();
            if (!outputs)
                return new RunRecord(key, skipped, diagnostics, model, sidecars, missing, templates, folders, committed, []);
            var stamps = new OutputStamp[Count(r)];
            for (var i = 0; i < stamps.Length; i++)
                stamps[i] = new OutputStamp(r.ReadString(), r.ReadBoolean(), r.ReadInt64(), r.ReadInt64());
            return r.BaseStream.Position == payloadLength
                ? new RunRecord(key, skipped, diagnostics, model, sidecars, missing, templates, folders, committed, stamps)
                : null;
        }
        catch (Exception ex) when (ex is EndOfStreamException or IOException or FormatException or ArgumentException or OverflowException)
        {
            return null;
        }
    }

    private static string BuildIdentity()
    {
        using var hash = new HashBuilder();
        hash.Add("mq-build-1").Add(EngineVersion.Value)
            .Add(typeof(RunRecord).Assembly.ManifestModule.ModuleVersionId.ToString("N"))
            .Add(Assembly.GetEntryAssembly()?.ManifestModule.ModuleVersionId.ToString("N"))
            .Add(Environment.Version.ToString());
        var depsFiles = AppContext.GetData("APP_CONTEXT_DEPS_FILES") as string;
        foreach (var file in (depsFiles ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            hash.Add(file);
            try
            {
                hash.Add(File.ReadAllBytes(file));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                hash.Add((string?)null);
            }
        }

        return hash.Finish();
    }

    private static int Count(BinaryReader r)
    {
        var count = r.ReadInt32();
        if (count < 0 || count > r.BaseStream.Length)
            throw new FormatException("Bad count.");
        return count;
    }

    private static void WriteStamps(BinaryWriter w, IReadOnlyList<FileStamp> stamps)
    {
        w.Write(stamps.Count);
        foreach (var stamp in stamps)
        {
            w.Write(stamp.Path);
            w.Write(stamp.Length);
            w.Write(stamp.LastWriteTicks);
        }
    }

    private static FileStamp[] ReadStamps(BinaryReader r)
    {
        var stamps = new FileStamp[Count(r)];
        for (var i = 0; i < stamps.Length; i++)
            stamps[i] = new FileStamp(r.ReadString(), r.ReadInt64(), r.ReadInt64());
        return stamps;
    }

    private static void WriteOptional(BinaryWriter w, string? value)
    {
        w.Write(value is not null);
        if (value is not null)
            w.Write(value);
    }

    private static void WriteOptional(BinaryWriter w, int? value)
    {
        w.Write(value.HasValue);
        if (value is { } v)
            w.Write(v);
    }

    private static string? ReadOptionalString(BinaryReader r) => r.ReadBoolean() ? r.ReadString() : null;

    private static int? ReadOptionalInt(BinaryReader r) => r.ReadBoolean() ? r.ReadInt32() : null;
}

/// <summary>Encoded <see cref="OutputStamp"/>s, as <see cref="RunRecord.Encode(IReadOnlyList{OutputChunk}, string)"/> writes them.</summary>
/// <param name="Count">The number of stamps.</param>
/// <param name="Bytes">Their encoding (path, owned, length, last-write ticks each).</param>
internal sealed record OutputChunk(int Count, byte[] Bytes)
{
    /// <summary>Encodes stamps.</summary>
    /// <param name="stamps">The stamps.</param>
    /// <returns>The chunk.</returns>
    public static OutputChunk Of(IEnumerable<OutputStamp> stamps)
    {
        ArgumentNullException.ThrowIfNull(stamps);
        using var buffer = new MemoryStream();
        var count = 0;
        using (var w = new BinaryWriter(buffer, Encoding.UTF8, leaveOpen: true))
        {
            foreach (var stamp in stamps)
            {
                w.Write(stamp.Path);
                w.Write(stamp.Owned);
                w.Write(stamp.Length);
                w.Write(stamp.LastWriteTicks);
                count++;
            }
        }

        return new OutputChunk(count, buffer.ToArray());
    }
}

/// <summary>
/// The last-run record of a one-shot host (Generation/README.md, "Last-run record"). After an apply run that leaves every planned
/// unit with a current state, <see cref="TryRecordAsync"/> writes <c>CacheDirectory/last-run.v1.bin</c>; a later apply run with the
/// same key in a fresh process, whose input files, engine files and outputs all still have the recorded stats or content, is answered by
/// <see cref="TryReplayAsync"/> with the result a full run would give (every planned unit skipped, nothing written, the same
/// diagnostics) without loading, validating, resolving or planning. Any difference, a missing or damaged record, or an unfinished
/// journal falls back to the full run.
/// </summary>
/// <param name="services">The generation service's services.</param>
internal sealed class LastRun(EngineServices services)
{
    /// <summary>
    /// Rules whose diagnostics mean a following run may not be a no-op although its inputs are unchanged (a refused path, a
    /// duplicate claim, hand edits, a lost region, regions on a built root): a run reporting any of them writes no record.
    /// </summary>
    private static readonly FrozenSet<string> Unsettled = new[] { "MQ6004", "MQ6005", "MQ6009", "MQ6010", "MQ6015" }.ToFrozenSet(StringComparer.Ordinal);

    private readonly EngineFiles _files = new(services.EnginePaths, WriteTarget.Cache);

    private EngineOptions Options => services.Options;

    /// <summary>The record file.</summary>
    internal string RecordPath => Path.Combine(Path.GetFullPath(Options.CacheDirectory), RunRecord.FileName);

    private string JournalPath => Path.Combine(Path.GetFullPath(Options.EffectiveJournalDirectory), "journal.jsonl");

    private string TemplatesFolder => Path.Combine(Path.GetFullPath(Options.EffectiveModelRoot), "templates");

    /// <summary>
    /// The engine folders a replay compares by stat: unit states and built-root manifests, which only the engine writes, under the
    /// cache and journal folders. Every file directly in them counts, so a new, changed or removed file falls back to the full run.
    /// </summary>
    internal IReadOnlyList<string> EngineFolderPaths =>
    [
        Path.Combine(Path.GetFullPath(Options.CacheDirectory), "units"),
        Path.Combine(Path.GetFullPath(Options.EffectiveJournalDirectory), "manifest"),
    ];

    /// <summary>
    /// The engine folders a replay compares by content: committed manifests and schema snapshots, which are committed with the model
    /// and can be rewritten by a checkout or a script. Every file directly in them counts.
    /// </summary>
    internal IReadOnlyList<string> CommittedFolderPaths =>
    [
        Path.Combine(Path.GetFullPath(Options.EffectiveModelRoot), "manifest"),
        Path.Combine(Path.GetFullPath(Options.EffectiveModelRoot), SchemaDiff.SnapshotStore.Folder),
    ];

    /// <summary>
    /// The key of a run: engine build (<see cref="RunRecord.CurrentBuild"/>), repo, model, journal and cache folders, and the
    /// request's packs, roots and hand-edit override. A record answers only a run with the same key.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <returns>The key.</returns>
    internal string Key(GenerationRequest request)
    {
        using var hash = new HashBuilder();
        hash.Add("mq-last-run-2").Add(EngineVersion.Value).Add(RunRecord.CurrentBuild)
            .Add(Path.GetFullPath(Options.RepoRoot)).Add(Path.GetFullPath(Options.EffectiveModelRoot))
            .Add(Path.GetFullPath(Options.EffectiveJournalDirectory)).Add(Path.GetFullPath(Options.CacheDirectory));
        if (request.Packs is null)
        {
            hash.Add("all packs");
        }
        else
        {
            var packs = request.Packs.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
            hash.Add(packs.Count);
            foreach (var pack in packs)
                hash.Add(pack);
        }

        hash.Add((long)request.Roots).Add(request.HandEdits is { } policy ? ((int)policy).ToString(CultureInfo.InvariantCulture) : "settings");
        return hash.Finish();
    }

    /// <summary>Removes the record (an apply run that is not answered from it is about to change what it describes).</summary>
    public void Forget()
    {
        try
        {
            _files.Delete(RecordPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A record that cannot be removed is harmless: the run's own writes change the stats it compares.
        }
    }

    /// <summary>
    /// Answers the run from the record when it is intact, from this engine build, and every recorded stat and content hash still
    /// holds: the result a full run would give, with every planned unit skipped. <see langword="null"/> when the run must go through
    /// the full pipeline.
    /// </summary>
    /// <param name="request">The request (apply, not forced).</param>
    /// <param name="runId">The run id.</param>
    /// <param name="run">The run (its clock and diagnostics).</param>
    /// <param name="progress">Progress (reported only when the record answers).</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The result, or <see langword="null"/>.</returns>
    public async Task<GenerationResult?> TryReplayAsync(GenerationRequest request, string runId, GenerationRun run, IProgress<ProgressUpdate>? progress,
        CancellationToken ct)
    {
        var start = run.Clock.Now;
        if (services.Loader is not ModelLoader loader)
            return null;
        byte[] bytes;
        try
        {
            if (!File.Exists(RecordPath) || File.Exists(JournalPath))
                return null;
            bytes = await File.ReadAllBytesAsync(RecordPath, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        // The trailer before any decoded field is used (a damaged path must not reach the file system), then the inputs (the cheap
        // part, and where an edit shows) before the outputs are decoded.
        if (!RunRecord.Intact(bytes) || RunRecord.Decode(bytes, outputs: false) is not { } inputs
            || !string.Equals(inputs.Key, Key(request), StringComparison.Ordinal))
            return null;

        // The content hashes read the templates and committed files beside the model files' stat check (which blocks this thread);
        // a miss cancels them.
        RunRecord record;
        using var hashing = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var templatesHash = Task.Run(() => HashTemplatesAsync(hashing.Token), hashing.Token);
        var committedHash = Task.Run(() => HashCommittedAsync(hashing.Token), hashing.Token);
        try
        {
            if (!ModelFilesSame(loader, inputs, ct)
                || !string.Equals(await templatesHash.ConfigureAwait(false), inputs.TemplatesHash, StringComparison.Ordinal))
                return null;
            var folders = StampEngineFolders();
            if (folders.Count != inputs.EngineFolders.Count)
                return null;
            for (var i = 0; i < folders.Count; i++)
            {
                if (!string.Equals(folders[i].Folder, inputs.EngineFolders[i].Folder, StringComparison.Ordinal) || !SameStamps(folders[i].Files, inputs.EngineFolders[i].Files))
                    return null;
            }

            if (!string.Equals(await committedHash.ConfigureAwait(false), inputs.CommittedHash, StringComparison.Ordinal)
                || RunRecord.Decode(bytes) is not { } full)
                return null;
            record = full;
            run.Clock.Record(PipelineStage.Load, start, record.ModelFiles.Count + record.Sidecars.Count);
            var skipStart = run.Clock.Now;
            if (!OutputsIntact(record.Outputs, ct))
                return null;
            run.Clock.Record(PipelineStage.Skip, skipStart, record.UnitsSkipped);
        }
        catch (Exception ex) when (Unusable(ex))
        {
            // A file that cannot be read or a path the file system refuses: the full run decides.
            return null;
        }
        finally
        {
            // Never left running (or with an unobserved failure) behind the full run.
            await hashing.CancelAsync().ConfigureAwait(false);
            await Task.WhenAll((Task)templatesHash, committedHash).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }

        var files = record.ModelFiles.Count + record.Sidecars.Count;
        progress?.Report(new ProgressUpdate(PipelineStage.Load, 0, 0, null, null));
        progress?.Report(new ProgressUpdate(PipelineStage.Load, files, files, null, null));
        progress?.Report(new ProgressUpdate(PipelineStage.Skip, record.UnitsSkipped, record.UnitsSkipped, null, null));
        run.Add(record.Diagnostics);
        return new GenerationResult(runId, GenerationMode.Apply, RunOutcome.Succeeded, [], 0, record.UnitsSkipped, 0, 0, run.Diagnostics, run.Clock.Timings());
    }

    /// <summary>
    /// Encodes the outputs of the skipped units' stored states, for the record (<see cref="TryRecordAsync"/>): started right after
    /// the skip stage so it runs beside rendering and writing. An output is stamped with the stat the skip stage checked it with
    /// (taken before its bytes were re-hashed when the stat differed from the state's): a file whose time stamp alone changed was
    /// found intact under that stat, and a later run that sees the same stat would find it intact again.
    /// </summary>
    /// <param name="skip">The skip result.</param>
    /// <param name="stats">The skip stage's output stats, if it had them.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The chunk.</returns>
    public static Task<OutputChunk> EncodeSkippedAsync(SkipResult skip, Task<OutputStats>? stats, CancellationToken ct) =>
        Task.Run(async () =>
        {
            OutputStats? taken = null;
            if (stats is not null)
            {
                try
                {
                    taken = await stats.ConfigureAwait(false);
                }
                catch (Exception) when (!ct.IsCancellationRequested)
                {
                    // Without them, the states' own stats are recorded: a file whose stat changed then falls back to the full run.
                }
            }

            return OutputChunk.Of(skip.Skipped.SelectMany(s => s.Previous.Outputs.Select(o => StampAsChecked(o, taken))));
        }, ct);

    private static OutputStamp StampAsChecked(UnitOutput output, OutputStats? stats) =>
        !ManifestHashes.IsOwned(output.ManifestHash) && stats is not null && stats.TryGet(output.Path, out var stat) && stat.Exists
            ? new OutputStamp(output.Path, false, stat.Length, stat.LastWriteUtcTicks)
            : Stamp(output);

    /// <summary>
    /// Writes the record after an apply run that succeeded, when a run with the same inputs would skip every planned unit and change
    /// nothing: no schema diff pending, no unsettled diagnostic, no unfinished journal, and every planned unit's stored state current
    /// (a skipped unit keeps its state, with the same outputs; a rendered unit has a new state with the input hash it rendered with).
    /// Failures to write are ignored: the record only saves time.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <param name="store">The model store the run loaded through.</param>
    /// <param name="prepared">The prepared run.</param>
    /// <param name="skip">The skip result.</param>
    /// <param name="skippedOutputs">The skipped units' outputs (<see cref="EncodeSkippedAsync"/>).</param>
    /// <param name="rendered">The rendered units' input hashes by key (<see cref="GenerationRun.RenderedInputHashes"/>).</param>
    /// <param name="preparedDiagnostics">The diagnostics after the skip stage.</param>
    /// <param name="templatesHash">The templates folder's content hash, taken before the packs were loaded (<see cref="HashTemplatesAsync"/>).</param>
    /// <param name="diagnostics">Every diagnostic of the run.</param>
    /// <param name="ct">Cancellation (a cancelled recording writes nothing).</param>
    /// <returns>Whether a record was written.</returns>
    public async Task<bool> TryRecordAsync(GenerationRequest request, ModelStore store, PreparedRun prepared, SkipResult skip, Task<OutputChunk> skippedOutputs,
        IReadOnlyDictionary<string, (string InputHash, bool Failed)> rendered, IReadOnlyList<Diagnostic> preparedDiagnostics, string? templatesHash,
        IReadOnlyList<Diagnostic> diagnostics, CancellationToken ct)
    {
        try
        {
            if (templatesHash is null || prepared.SchemaDiffs.Values.Any(d => !d.IsEmpty) || diagnostics.Any(d => Unsettled.Contains(d.Rule)) || File.Exists(JournalPath))
                return false;
            if (store.LastFileStamps() is not { } stamps || !ReferenceEquals(stamps.Snapshot, prepared.Snapshot))
                return false;

            // The states as the writer saved them (the store remembers what it wrote; another store reads them back).
            var states = new Dictionary<string, IReadOnlyDictionary<string, UnitState>>(StringComparer.Ordinal);
            foreach (var pack in prepared.Packs.Packs)
            {
                states[pack.Name] = (services.UnitState as UnitStateStore)?.Remembered(pack.Name)
                    ?? await services.UnitState.LoadAsync(pack.Name, ct).ConfigureAwait(false);
            }

            UnitState? Final(PlannedUnit unit) =>
                states.TryGetValue(unit.Pack.Name, out var packStates) && packStates.TryGetValue(unit.Key, out var state) ? state : null;

            foreach (var skipped in skip.Skipped)
            {
                if (Final(skipped.Unit) is not { } state || !string.Equals(state.InputHash, skipped.Previous.InputHash, StringComparison.Ordinal)
                    || !SameOutputs(state.Outputs, skipped.Previous.Outputs))
                    return false;
            }

            var renderedOutputs = new List<OutputStamp>();
            foreach (var unit in skip.ToRender)
            {
                if (!rendered.TryGetValue(unit.Key, out var result) || result.Failed || Final(unit) is not { } state
                    || !string.Equals(state.InputHash, result.InputHash, StringComparison.Ordinal))
                    return false;
                renderedOutputs.AddRange(state.Outputs.Select(Stamp));
            }

            if (skip.Skipped.Count + skip.ToRender.Count != prepared.Plan.Units.Count)
                return false;
            var record = new RunRecord(Key(request), prepared.Plan.Units.Count, preparedDiagnostics, stamps.Primary, stamps.Sidecars, stamps.MissingSidecars,
                templatesHash, StampEngineFolders(), await HashCommittedAsync(ct).ConfigureAwait(false), []);
            var bytes = record.Encode([await skippedOutputs.ConfigureAwait(false), OutputChunk.Of(renderedOutputs)]);
            await _files.WriteAsync(RecordPath, bytes, ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is OperationCanceledException || Unusable(ex))
        {
            return false;
        }
    }

    private static OutputStamp Stamp(UnitOutput output) =>
        new(output.Path, ManifestHashes.IsOwned(output.ManifestHash), output.Length, output.LastWriteUtcTicks);

    private static bool SameOutputs(IReadOnlyList<UnitOutput> a, IReadOnlyList<UnitOutput> b)
    {
        if (ReferenceEquals(a, b))
            return true;
        if (a.Count != b.Count)
            return false;
        for (var i = 0; i < a.Count; i++)
        {
            if (!string.Equals(a[i].Path, b[i].Path, StringComparison.Ordinal) || !string.Equals(a[i].ManifestHash, b[i].ManifestHash, StringComparison.Ordinal)
                || a[i].Length != b[i].Length || a[i].LastWriteUtcTicks != b[i].LastWriteUtcTicks)
                return false;
        }

        return true;
    }

    /// <summary>
    /// The content hash of every file under the templates folder (dot files included): <c>H</c> over each file's relative path
    /// (<c>/</c>) and bytes, in ordinal path order. Templates, partials, helper scripts and pack manifests are what the units' static
    /// hashes are made of, which a full run reads on every run, so the record compares their bytes, not their stats.
    /// </summary>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The hash.</returns>
    /// <exception cref="IOException">A folder could not be listed or a file read.</exception>
    public Task<string> HashTemplatesAsync(CancellationToken ct)
    {
        var folder = TemplatesFolder;
        var enumeration = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0, IgnoreInaccessible = false, MatchType = MatchType.Simple };
        var files = Directory.Exists(folder)
            ? Directory.EnumerateFiles(folder, "*", enumeration).Select(full => (Path.GetRelativePath(folder, full).Replace('\\', '/'), full)).ToList()
            : [];
        return HashFilesAsync("mq-templates-1", [(folder, files)], ct);
    }

    /// <summary>The content hash of the files directly in each <see cref="CommittedFolderPaths"/> folder (a missing folder has none).</summary>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The hash.</returns>
    /// <exception cref="IOException">A folder could not be listed or a file read.</exception>
    public Task<string> HashCommittedAsync(CancellationToken ct)
    {
        var enumeration = new EnumerationOptions { RecurseSubdirectories = false, AttributesToSkip = 0, IgnoreInaccessible = false, MatchType = MatchType.Simple };
        var folders = CommittedFolderPaths.Select(folder => (folder, Directory.Exists(folder)
            ? Directory.EnumerateFiles(folder, "*", enumeration).Select(full => (Path.GetFileName(full), full)).ToList()
            : new List<(string, string)>())).ToList();
        return HashFilesAsync("mq-committed-1", folders, ct);
    }

    private static async Task<string> HashFilesAsync(string label, IReadOnlyList<(string Folder, List<(string Relative, string Full)> Files)> folders,
        CancellationToken ct)
    {
        using var hash = new HashBuilder();
        hash.Add(label).Add(folders.Count);
        foreach (var (folder, files) in folders)
        {
            files.Sort((a, b) => string.CompareOrdinal(a.Relative, b.Relative));
            hash.Add(folder).Add(files.Count);
            foreach (var (relative, full) in files)
            {
                var bytes = await File.ReadAllBytesAsync(full, ct).ConfigureAwait(false);
                hash.Add(relative).Add(bytes);
            }
        }

        return hash.Finish();
    }

    /// <summary>The stats of the files directly in each <see cref="EngineFolderPaths"/> folder (a missing folder has none).</summary>
    /// <returns>The folder stamps, in <see cref="EngineFolderPaths"/> order.</returns>
    /// <exception cref="IOException">A folder could not be listed.</exception>
    public IReadOnlyList<FolderStamp> StampEngineFolders()
    {
        var enumeration = new EnumerationOptions { RecurseSubdirectories = false, AttributesToSkip = 0, IgnoreInaccessible = false, MatchType = MatchType.Simple };
        var result = new List<FolderStamp>();
        foreach (var folder in EngineFolderPaths)
        {
            var stamps = new List<FileStamp>();
            if (Directory.Exists(folder))
            {
                foreach (var full in Directory.EnumerateFiles(folder, "*", enumeration))
                {
                    if (Stat(full) is { } stamp)
                        stamps.Add(stamp with { Path = Path.GetFileName(full) });
                }
            }

            stamps.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
            result.Add(new FolderStamp(folder, stamps));
        }

        return result;
    }

    /// <summary>
    /// Whether the model files are those the recorded load read, with the same stats: the enumerated set equals the recorded one,
    /// every file and referenced sidecar has its recorded length and last-write time, and every missing sidecar is still missing.
    /// </summary>
    private bool ModelFilesSame(ModelLoader loader, RunRecord record, CancellationToken ct)
    {
        // The recorded files' stats first (in parallel; an edited file shows here), then the listing (a new file), then sidecars.
        bool Same(FileStamp f) => Stat(loader.Paths.FullPath(f.Path)) is { } now && now.Length == f.Length && now.LastWriteTicks == f.LastWriteTicks;
        if (!AllMatch(record.ModelFiles, Same, ct))
            return false;
        var enumerated = loader.EnumerateModelFiles();
        if (enumerated.Count != record.ModelFiles.Count || record.ModelFiles.Any(f => !enumerated.Contains(f.Path)))
            return false;
        return !record.MissingSidecars.Any(p => File.Exists(loader.Paths.FullPath(p))) && AllMatch(record.Sidecars, Same, ct);
    }

    /// <summary>
    /// The skip check's disk half for every recorded output: an owned output exists; any other has its recorded length and
    /// last-write time (a changed stat falls back to the full run, which re-hashes the file).
    /// </summary>
    private bool OutputsIntact(IReadOnlyList<OutputStamp> outputs, CancellationToken ct)
    {
        var root = Path.GetFullPath(Options.RepoRoot);
        return AllMatch(outputs, o =>
        {
            var now = Stat(OutputStats.FullPath(root, o.Path));
            return now is { } stat && (o.Owned || (stat.Length == o.Length && stat.LastWriteTicks == o.LastWriteTicks));
        }, ct);
    }

    private bool AllMatch<T>(IReadOnlyList<T> items, Func<T, bool> matches, CancellationToken ct)
    {
        if (items.Count == 0)
            return true;
        var ok = true;
        Parallel.For(0, items.Count, new ParallelOptions { MaxDegreeOfParallelism = Options.EffectiveParallelism, CancellationToken = ct }, (i, loop) =>
        {
            if (!matches(items[i]))
            {
                Volatile.Write(ref ok, false);
                loop.Stop();
            }
        });
        return Volatile.Read(ref ok);
    }

    /// <summary>
    /// Whether an exception from checking the record against the disk means only that the record cannot answer the run: an I/O or
    /// access failure, or a path the file system refuses (possible only in a record whose trailer was forged to match); parallel
    /// checks wrap them in an <see cref="AggregateException"/>.
    /// </summary>
    private static bool Unusable(Exception ex) => ex switch
    {
        AggregateException aggregate => aggregate.Flatten().InnerExceptions.All(Unusable),
        IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException => true,
        _ => false,
    };

    private static bool SameStamps(IReadOnlyList<FileStamp> now, IReadOnlyList<FileStamp> recorded) =>
        now.Count == recorded.Count && now.Zip(recorded).All(p => p.First == p.Second);

    private static FileStamp? Stat(string fullPath)
    {
        var info = new FileInfo(fullPath);
        return info.Exists ? new FileStamp(fullPath, info.Length, info.LastWriteTimeUtc.Ticks) : null;
    }
}
