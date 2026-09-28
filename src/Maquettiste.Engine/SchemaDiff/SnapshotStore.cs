using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Maquettiste.Engine.Json;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Pipeline;

namespace Maquettiste.Engine.SchemaDiff;

/// <summary>
/// Loads and saves the committed physical snapshots <c>&lt;ModelRoot&gt;/snapshots/&lt;database kebab name&gt;.json</c> (W8;
/// engine-design.md section 14, D15). Files are canonical JSON (<c>snapshot.json</c> layout) with every keyed list sorted by key.
/// A save checks the target and its temp file with <see cref="IOutputPathPolicy.CheckEngineWrite"/> (<see cref="WriteTarget.Model"/>)
/// first, writes the temp file in the same folder and moves it into place; identical bytes are not rewritten.
/// </summary>
/// <param name="options">The engine options.</param>
/// <param name="json">The canonical writer.</param>
/// <param name="paths">The engine-write guard (<see cref="WriteTarget.Model"/>).</param>
internal sealed class SnapshotStore(EngineOptions options, ICanonicalJson json, IOutputPathPolicy paths) : ISnapshotStore
{
    /// <summary>The snapshot folder under the model root.</summary>
    internal const string Folder = "snapshots";

    /// <summary>The schema file of snapshots.</summary>
    internal const string SchemaFile = "snapshot.json";

    /// <summary>
    /// The last snapshot parsed per file (by full path), with the SHA-256 of the bytes it was parsed from: a load whose file holds
    /// the same bytes reuses it instead of parsing again. The value is a task because a snapshot prepared with <c>parse</c> is parsed
    /// on the thread pool beside the run, and <see cref="WriteAsync"/> holds that parse for the file it wrote. An entry is removed when its file
    /// is found missing and by <see cref="Retain"/> (the generation run keeps only its own databases' files). Instance state of the
    /// long-lived store, guarded by <see cref="_gate"/>.
    /// </summary>
    private readonly Dictionary<string, (byte[] Hash, Task<PhysicalSnapshot> Snapshot)> _parsed = new(StringComparer.Ordinal);

    private readonly Lock _gate = new();

    /// <inheritdoc/>
    public async Task<PhysicalSnapshot?> LoadAsync(string databaseName, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(databaseName);
        var path = FullPath(databaseName);
        if (!File.Exists(path))
        {
            lock (_gate)
                _parsed.Remove(path);
            return null;
        }

        var bytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
        var hash = SHA256.HashData(bytes);
        Task<PhysicalSnapshot>? known = null;
        lock (_gate)
        {
            if (_parsed.TryGetValue(path, out var entry) && entry.Hash.AsSpan().SequenceEqual(hash))
                known = entry.Snapshot;
        }

        if (known is not null)
        {
            // A background parse of bytes this store serialized. If it failed or was cancelled with the run that started it, parse
            // the file instead; only this load's own cancellation propagates.
            try
            {
                return await known.WaitAsync(ct).ConfigureAwait(false);
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
            }
        }

        var snapshot = Parse(bytes, databaseName);
        Remember(path, hash, Task.FromResult(snapshot));
        return snapshot;
    }

    /// <inheritdoc/>
    public Task SaveAsync(PhysicalSnapshot snapshot, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ct.ThrowIfCancellationRequested();
        return WriteAsync(Prepare(snapshot, parse: false, ct), ct);
    }

    /// <summary>
    /// Serializes a snapshot exactly as <see cref="SaveAsync"/> writes it, without any I/O, so a run can do this (the costly part:
    /// about 0.7 s for the benchmark's 10,005-table database) while it renders and write the bytes once the apply has succeeded.
    /// With <paramref name="parse"/>, parsing the bytes back starts on the thread pool as soon as they exist
    /// (<see cref="PreparedSnapshot.Parsed"/>); nothing waits for it here or in <see cref="WriteAsync"/>, which only hands it to the
    /// next <see cref="LoadAsync"/> of the file it wrote, so the parse is never on the run's critical path.
    /// </summary>
    /// <param name="snapshot">The snapshot.</param>
    /// <param name="parse">Whether to start parsing the bytes back for the next load.</param>
    /// <param name="ct">Cancellation, checked before sorting, before the (uninterruptible) serialization and before the parse starts.</param>
    /// <returns>The prepared snapshot.</returns>
    internal PreparedSnapshot Prepare(PhysicalSnapshot snapshot, bool parse, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ct.ThrowIfCancellationRequested();
        var fileName = FileNameOf(snapshot.Name);
        var sorted = SnapshotCapture.Sorted(snapshot);
        ct.ThrowIfCancellationRequested();
        var bytes = json.Serialize(sorted, SchemaFile, Folder + "/" + fileName);
        var prepared = new PreparedSnapshot(snapshot.Name, bytes);
        if (!parse)
            return prepared;
        var name = snapshot.Name;
        var parsing = Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            return Parse(bytes, name);
        }, ct);
        _ = parsing.ContinueWith(static t => _ = t.Exception, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return prepared with { Parsed = parsing };
    }

    /// <summary>
    /// Writes a prepared snapshot: checks the target and the temp file with the engine-write guard, skips identical bytes, writes the
    /// temp file in the folder and moves it into place. A prepared snapshot's <see cref="PreparedSnapshot.Parsed"/> is then held for
    /// the file (not awaited), so the next <see cref="LoadAsync"/> of it (the next run of a long-lived host) reuses that object.
    /// </summary>
    /// <param name="prepared">The prepared snapshot.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>A task.</returns>
    internal async Task WriteAsync(PreparedSnapshot prepared, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        ct.ThrowIfCancellationRequested();
        var path = FullPath(prepared.DatabaseName);
        var fileName = Path.GetFileName(path);
        var temp = Path.Combine(Path.GetDirectoryName(path)!, "." + fileName + ".mq-snapshot.tmp");
        Guard(path);
        Guard(temp);

        var bytes = prepared.Bytes;
        var written = false;
        if (File.Exists(path))
        {
            var existing = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
            written = existing.AsSpan().SequenceEqual(bytes);
        }

        if (!written)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            try
            {
                await File.WriteAllBytesAsync(temp, bytes, ct).ConfigureAwait(false);
                File.Move(temp, path, overwrite: true);
            }
            finally
            {
                if (File.Exists(temp))
                    File.Delete(temp);
            }
        }

        if (prepared.Parsed is { } parsed)
            Remember(path, SHA256.HashData(bytes), parsed);
    }

    /// <summary>
    /// Drops the parsed snapshots of every file other than those of <paramref name="databaseNames"/>, so a long-lived host does not
    /// keep the snapshot of a database it no longer has (renamed, removed, or no longer diffed) until the store goes away.
    /// </summary>
    /// <param name="databaseNames">The databases whose parsed snapshots to keep; empty drops them all.</param>
    internal void Retain(IEnumerable<string> databaseNames)
    {
        ArgumentNullException.ThrowIfNull(databaseNames);
        var keep = new HashSet<string>(databaseNames.Select(FullPath), StringComparer.Ordinal);
        lock (_gate)
        {
            foreach (var path in _parsed.Keys.Where(p => !keep.Contains(p)).ToList())
                _parsed.Remove(path);
        }
    }

    /// <summary>The parsed snapshot the store holds for a database's file, or <see langword="null"/> (for tests).</summary>
    /// <param name="databaseName">The database name.</param>
    /// <returns>The held parse, or <see langword="null"/>.</returns>
    internal Task<PhysicalSnapshot>? Held(string databaseName)
    {
        var path = FullPath(databaseName);
        lock (_gate)
            return _parsed.TryGetValue(path, out var entry) ? entry.Snapshot : null;
    }

    private PhysicalSnapshot Parse(byte[] bytes, string databaseName)
    {
        try
        {
            return JsonSerializer.Deserialize<PhysicalSnapshot>(bytes, EngineJson.Options)
                ?? throw new InvalidDataException($"The schema snapshot '{RelativePath(databaseName)}' is empty (JSON null).");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"The schema snapshot '{RelativePath(databaseName)}' is not a valid snapshot: {ex.Message}", ex);
        }
    }

    private void Remember(string path, byte[] hash, Task<PhysicalSnapshot> snapshot)
    {
        lock (_gate)
            _parsed[path] = (hash, snapshot);
    }

    /// <summary>The absolute path of a database's snapshot.</summary>
    /// <param name="databaseName">The database name.</param>
    /// <returns>The path.</returns>
    internal string FullPath(string databaseName) =>
        Path.Combine(options.EffectiveModelRoot, Folder, FileNameOf(databaseName));

    /// <summary>The snapshot file name of a database: its kebab-case name plus <c>.json</c>.</summary>
    /// <param name="databaseName">The database name.</param>
    /// <returns>The file name, for example <c>main-db.json</c> for <c>MainDB</c>.</returns>
    internal static string FileNameOf(string databaseName)
    {
        var kebab = Kebab(databaseName);
        return (kebab.Length == 0 ? "database" : kebab) + ".json";
    }

    /// <summary>
    /// Kebab case by the engine-design.md section 9 word rule: split on non-alphanumerics and at lower→upper and acronym→word
    /// boundaries, digits join the preceding word, words lowercased (D26).
    /// </summary>
    /// <param name="name">The name.</param>
    /// <returns>The kebab-case name.</returns>
    internal static string Kebab(string name)
    {
        var words = new List<string>();
        var current = new StringBuilder();
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (!char.IsAsciiLetterOrDigit(c) && !char.IsLetterOrDigit(c))
            {
                Flush();
                continue;
            }

            if (current.Length > 0 && char.IsUpper(c))
            {
                var previous = name[i - 1];
                var nextIsLower = i + 1 < name.Length && char.IsLower(name[i + 1]);
                if (char.IsLower(previous) || char.IsDigit(previous) || (char.IsUpper(previous) && nextIsLower))
                    Flush();
            }

            current.Append(c);
        }

        Flush();
        return string.Join('-', words);

        void Flush()
        {
            if (current.Length == 0)
                return;
            words.Add(current.ToString().ToLower(CultureInfo.InvariantCulture));
            current.Clear();
        }
    }

    private string RelativePath(string databaseName)
    {
        var relative = Path.GetRelativePath(options.RepoRoot, FullPath(databaseName));
        return relative.Replace('\\', '/');
    }

    private void Guard(string fullPath)
    {
        var check = paths.CheckEngineWrite(WriteTarget.Model, fullPath);
        if (!check.Allowed)
            throw new UnauthorizedAccessException($"Refused to write the schema snapshot '{fullPath}': {check.Reason ?? check.RuleId ?? "path policy"}.");
    }
}

/// <summary>A snapshot serialized for <see cref="SnapshotStore.WriteAsync"/>.</summary>
/// <param name="DatabaseName">The database name (the file name derives from it).</param>
/// <param name="Bytes">The canonical bytes.</param>
internal sealed record PreparedSnapshot(string DatabaseName, byte[] Bytes)
{
    /// <summary>The bytes being parsed back on the thread pool, which the store holds for its next load of the file once
    /// <see cref="SnapshotStore.WriteAsync"/> has written it; <see langword="null"/> when not parsed.</summary>
    public Task<PhysicalSnapshot>? Parsed { get; init; }
}
