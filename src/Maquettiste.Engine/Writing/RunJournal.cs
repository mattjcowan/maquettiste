using System.Buffers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Maquettiste.Engine.Pipeline;

namespace Maquettiste.Engine.Writing;

/// <summary>
/// The run journal <c>JournalDirectory/journal.jsonl</c> (W7; engine-design.md section 12.4): a <c>begin</c> line, one
/// <c>write</c> or <c>delete</c> line per file (flushed to the OS), a <c>pack</c> line once a pack's manifest and unit state are saved
/// (flushed to disk), and <c>end</c>, after which the file is deleted (unless lines of packs without a <c>pack</c> line remain; see
/// <see cref="EndAsync"/>). The orchestrator calls <see cref="BeginAsync"/> and
/// <see cref="EndAsync"/>; the writer records files and packs. Records are serialized, so concurrent writer tasks may call it.
/// </summary>
/// <param name="options">The engine options.</param>
/// <param name="paths">The engine-write guard (<see cref="WriteTarget.Cache"/>).</param>
internal sealed class RunJournal(EngineOptions options, IOutputPathPolicy paths) : IRunJournal
{
    private static readonly JsonWriterOptions LineOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, Indented = false };

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, List<JournalRecord>> _pending = new(StringComparer.Ordinal);
    private FileStream? _stream;
    private string? _runId;
    private byte[]? _begin;

    /// <summary>The journal file.</summary>
    internal string FilePath => Path.Combine(options.EffectiveJournalDirectory, "journal.jsonl");

    /// <inheritdoc/>
    public async Task<IReadOnlyList<JournalRecord>?> ReadUnfinishedAsync(CancellationToken ct)
    {
        byte[]? bytes;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_stream is not null)
                throw new InvalidOperationException("The journal is open for a run.");
            bytes = await AtomicFile.ReadIfExistsAsync(FilePath, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        if (bytes is null)
            return null;
        var records = ParseLines(bytes);
        return records.Count > 0 && records[^1].Type == JournalTypes.End ? null : records;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// An unfinished journal is replaced, but the <c>write</c> and <c>delete</c> lines of its packs without a <c>pack</c> line are
    /// carried into the new journal after <c>begin</c>, so a resumed run that is itself interrupted still resumes without reporting
    /// the first run's files as hand edits. The new content is written to a temporary file, flushed to disk and renamed over the
    /// old journal, so a crash at any point leaves either the old journal or the new one, never a truncated file.
    /// </remarks>
    public async Task BeginAsync(string runId, string? planId, IReadOnlyList<string> packs, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(runId);
        ArgumentNullException.ThrowIfNull(packs);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_stream is not null)
                throw new InvalidOperationException("The journal is already open for a run.");
            var file = FilePath;
            Guard(file);
            var carried = new List<JournalRecord>();
            if (await AtomicFile.ReadIfExistsAsync(file, ct).ConfigureAwait(false) is { } previous)
            {
                var records = ParseLines(previous);
                if (records.Count == 0 || records[^1].Type != JournalTypes.End)
                {
                    var done = records.Where(r => r.Type == JournalTypes.Pack && r.Pack is not null).Select(r => r.Pack!).ToHashSet(StringComparer.Ordinal);
                    carried.AddRange(records.Where(r => r.Type is JournalTypes.Write or JournalTypes.Delete && r.Pack is not null && !done.Contains(r.Pack)));
                }
            }

            var begin = Line(w =>
            {
                w.WriteString("t", JournalTypes.Begin);
                w.WriteString("run", runId);
                if (planId is null)
                    w.WriteNull("plan");
                else
                    w.WriteString("plan", planId);
                w.WriteStartArray("packs");
                foreach (var pack in packs)
                    w.WriteStringValue(pack);
                w.WriteEndArray();
            });
            await ReplaceDurablyAsync(file, runId, begin, carried, ct).ConfigureAwait(false);

            _stream = new FileStream(file, new FileStreamOptions
            {
                Mode = FileMode.Append,
                Access = FileAccess.Write,
                Share = FileShare.Read,
                Options = FileOptions.Asynchronous,
            });
            _runId = runId;
            _begin = begin;
            _pending.Clear();
            foreach (var record in carried)
                Pending(record.Pack!).Add(record);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc/>
    public ValueTask RecordWriteAsync(string pack, ManifestEntry entry, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(pack);
        ArgumentNullException.ThrowIfNull(entry);
        return AppendAsync(new JournalRecord(JournalTypes.Write, pack, entry.Path, entry.Hash, entry.Unit), flushToDisk: false, ct);
    }

    /// <inheritdoc/>
    public ValueTask RecordDeleteAsync(string pack, string path, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(pack);
        ArgumentNullException.ThrowIfNull(path);
        return AppendAsync(new JournalRecord(JournalTypes.Delete, pack, path, null, null), flushToDisk: false, ct);
    }

    /// <inheritdoc/>
    public ValueTask RecordPackCompleteAsync(string pack, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(pack);
        return AppendAsync(new JournalRecord(JournalTypes.Pack, pack, null, null, null), flushToDisk: true, ct);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// When <c>write</c> or <c>delete</c> lines remain for packs that got no <c>pack</c> line (packs of an earlier interrupted run
    /// that this run did not cover), the journal is not deleted: it is replaced, durably, by <c>begin</c> plus those lines and no
    /// <c>end</c>, so the next run still overlays them and never reports those files as hand edits (host-contracts 33).
    /// </remarks>
    public async Task EndAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var stream = _stream ?? throw new InvalidOperationException("The journal is not open.");
            var file = FilePath;
            if (_pending.Count > 0)
            {
                await stream.DisposeAsync().ConfigureAwait(false);
                _stream = null;
                var remaining = _pending.OrderBy(p => p.Key, StringComparer.Ordinal).SelectMany(p => p.Value).ToList();
                _pending.Clear();
                Guard(file);
                await ReplaceDurablyAsync(file, _runId!, _begin!, remaining, CancellationToken.None).ConfigureAwait(false);
                return;
            }

            await stream.WriteAsync(Encode(new JournalRecord(JournalTypes.End, null, null, null, null)), CancellationToken.None).ConfigureAwait(false);
            await stream.FlushAsync(CancellationToken.None).ConfigureAwait(false);
            await stream.DisposeAsync().ConfigureAwait(false);
            _stream = null;
            Guard(file);
            File.Delete(file);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc/>
    /// <remarks>Closes an open journal without an <c>end</c> line, so the next run resumes it.</remarks>
    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_stream is not null)
            {
                await _stream.DisposeAsync().ConfigureAwait(false);
                _stream = null;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Parses journal bytes; lines that do not parse (a line cut short by a crash) are skipped.</summary>
    /// <param name="bytes">The file bytes.</param>
    /// <returns>The records in file order.</returns>
    internal static List<JournalRecord> ParseLines(byte[] bytes)
    {
        var records = new List<JournalRecord>();
        foreach (var line in Encoding.UTF8.GetString(bytes).Split('\n'))
        {
            if (line.Length == 0)
                continue;
            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("t", out var t) || t.ValueKind != JsonValueKind.String)
                    continue;
                records.Add(new JournalRecord(t.GetString()!, Str(root, "pack"), Str(root, "path"), Str(root, "hash"), Str(root, "unit")));
            }
            catch (JsonException)
            {
            }
        }

        return records;
    }

    private static string? Str(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static byte[] Encode(JournalRecord record) => Line(w =>
    {
        w.WriteString("t", record.Type);
        if (record.Pack is not null)
            w.WriteString("pack", record.Pack);
        if (record.Path is not null)
            w.WriteString("path", record.Path);
        if (record.Hash is not null)
            w.WriteString("hash", record.Hash);
        if (record.Unit is not null)
            w.WriteString("unit", record.Unit);
    });

    private static byte[] Line(Action<Utf8JsonWriter> body)
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var writer = new Utf8JsonWriter(buffer, LineOptions))
        {
            writer.WriteStartObject();
            body(writer);
            writer.WriteEndObject();
        }

        var bytes = new byte[buffer.WrittenCount + 1];
        buffer.WrittenSpan.CopyTo(bytes);
        bytes[^1] = (byte)'\n';
        return bytes;
    }

    private List<JournalRecord> Pending(string pack)
    {
        if (!_pending.TryGetValue(pack, out var list))
        {
            list = [];
            _pending[pack] = list;
        }

        return list;
    }

    /// <summary>
    /// Replaces the journal with <paramref name="begin"/> and <paramref name="records"/>: a temporary file in the journal folder is
    /// written and flushed to disk, then renamed over the journal, so the old content stays durable until the new content is.
    /// </summary>
    private async Task ReplaceDurablyAsync(string file, string runId, byte[] begin, IReadOnlyList<JournalRecord> records, CancellationToken ct)
    {
        var temp = AtomicFile.TempPath(file, runId);
        Guard(temp);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        try
        {
            var stream = new FileStream(temp, new FileStreamOptions
            {
                Mode = FileMode.Create,
                Access = FileAccess.Write,
                Share = FileShare.None,
                Options = FileOptions.Asynchronous,
            });
            await using (stream.ConfigureAwait(false))
            {
                await stream.WriteAsync(begin, ct).ConfigureAwait(false);
                foreach (var record in records)
                    await stream.WriteAsync(Encode(record), ct).ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temp, file, overwrite: true);
        }
        catch
        {
            AtomicFile.TryDelete(temp);
            throw;
        }
    }

    private void Guard(string file)
    {
        var check = paths.CheckEngineWrite(WriteTarget.Cache, file);
        if (!check.Allowed)
            throw new UnauthorizedAccessException($"{check.RuleId}: journal write refused for {file}: {check.Reason}");
    }

    private async ValueTask AppendAsync(JournalRecord record, bool flushToDisk, CancellationToken ct)
    {
        // Not cancellable: a file already written must be recorded, or the next run would take it for a hand edit.
        _ = ct;
        var line = Encode(record);
        await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            var stream = _stream ?? throw new InvalidOperationException("The journal is not open.");
            // The line is written whole even when cancellation arrives, so the journal stays consistent.
            await stream.WriteAsync(line, CancellationToken.None).ConfigureAwait(false);
            await stream.FlushAsync(CancellationToken.None).ConfigureAwait(false);
            if (flushToDisk)
                stream.Flush(flushToDisk: true);
            if (record.Type == JournalTypes.Pack)
                _pending.Remove(record.Pack!);
            else
                Pending(record.Pack!).Add(record);
        }
        finally
        {
            _gate.Release();
        }
    }
}
