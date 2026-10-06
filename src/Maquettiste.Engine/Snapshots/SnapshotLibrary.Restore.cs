using System.Globalization;
using System.IO.Compression;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Generation;
using Maquettiste.Engine.Loading;
using Maquettiste.Engine.Pipeline;
using Maquettiste.Engine.Snapshots;
using Maquettiste.Engine.Writing;

namespace Maquettiste.Engine;

public sealed partial class SnapshotLibrary
{
    /// <summary>
    /// Restores a snapshot into the working model as one operation: refused while a generation run holds the run lock (which the restore
    /// then holds itself); first a safety snapshot of the working model (<c>before-restore-&lt;time&gt;</c>, holding the packs when they
    /// are restored too); then every document that differs is written and every document the snapshot does not have is deleted, all or
    /// nothing, through the store, which reloads and publishes one change set (realtime clients see it). Restoring the safety snapshot
    /// undoes it; the editor's undo stack is not involved.
    /// </summary>
    /// <param name="id">The snapshot to restore.</param>
    /// <param name="request">Whether to restore the packs (only when the snapshot holds them), the author and the change source.</param>
    /// <param name="ct">Cancellation, observed until the first document is replaced.</param>
    /// <returns>What happened.</returns>
    public async Task<SnapshotRestoreResult> RestoreAsync(string id, SnapshotRestoreRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!IsValidId(id) || TryInfo(id) is not { } snapshot)
            return Refusal(SnapshotRestoreOutcome.NotFound, null, []);
        await _writes.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var runLock = await _store.Services.RunLock.AcquireAsync(false, ct).ConfigureAwait(false);
            if (runLock is null)
            {
                return Refusal(SnapshotRestoreOutcome.Locked, snapshot, []);
            }

            await using (runLock.ConfigureAwait(false))
            {
                var packs = request.IncludePacks && snapshot.IncludesPacks;
                var stamp = _options.EffectiveTimeProvider.GetUtcNow().ToUniversalTime().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
                var (safety, current) = await CreateCoreAsync(new SnapshotCreateRequest("before-restore-" + stamp,
                    $"Taken automatically before restoring {id}; restore this snapshot to undo that restore.", packs, request.Author), "before-restore", ct).ConfigureAwait(false);

                var file = FileOf(id);
                List<SnapshotIndexRow> target;
                using (var zip = ZipFile.OpenRead(file))
                {
                    var index = zip.GetEntry(SnapshotLayout.IndexEntry) ?? throw new InvalidDataException($"{file} has no {SnapshotLayout.IndexEntry}.");
                    await using var read = index.Open();
                    target = [.. SnapshotIndexRow.Read(ReadBounded(read, Limits.MaxEntryBytes)).Where(r => packs || !SnapshotLayout.IsTemplatePath(r.Path))];
                }

                var now = current.ToDictionary(r => r.Path, r => r.Hash, StringComparer.Ordinal);
                var changed = target.Where(r => !now.TryGetValue(r.Path, out var hash) || hash != r.Hash).Select(r => r.Path).ToList();
                var kept = target.Select(r => r.Path).ToHashSet(StringComparer.Ordinal);
                var deletes = current.Select(r => r.Path).Where(p => !kept.Contains(p) && (packs || !SnapshotLayout.IsTemplatePath(p))).ToList();

                var writes = new List<(string Path, byte[] Bytes)>(changed.Count);
                using (var documents = new ZipDocumentStore(file, includeTemplates: packs))
                {
                    foreach (var path in changed)
                        writes.Add((path, await documents.ReadAsync(path, ct).ConfigureAwait(false) ?? throw new InvalidDataException($"{path} is missing from {file}.")));
                }

                var (changes, refused) = await _store.ReplaceDocumentsAsync(writes, deletes, request.Source, ct).ConfigureAwait(false);
                if (refused is not null)
                {
                    return Refusal(SnapshotRestoreOutcome.Refused, snapshot,
                        [RuleCatalog.Create("MQ6004", $"The model write to {refused.Path} was refused: {refused.Reason}", null, null, null)]) with { Safety = safety };
                }

                return new SnapshotRestoreResult(SnapshotRestoreOutcome.Restored, snapshot, safety, writes.Count, deletes.Count, packs,
                    $"Restore snapshot {safety.Id} to undo this restore.", changes!.Changed.Count, changes.Deleted.Count, []) { Changes = changes };
            }
        }
        finally
        {
            _writes.Release();
        }
    }

    private static SnapshotRestoreResult Refusal(SnapshotRestoreOutcome outcome, SnapshotInfo? snapshot, IReadOnlyList<Diagnostic> diagnostics) =>
        new(outcome, snapshot, null, 0, 0, false, null, 0, 0, diagnostics);

    /// <summary>
    /// Imports an exported snapshot: the archive is copied to the cache folder (refused past the size limit), then checked (a zip;
    /// every entry a safe path of the snapshot layout, no duplicate, within the size, count and expansion limits; a readable
    /// <c>snapshot.json</c> of a format and model format this release reads), rewritten as this engine writes archives (sorted
    /// entries, its own index), and loaded once from the new archive: a document that does not parse (MQ1001), is not canonical (MQ1003)
    /// or names an unsupported model format (MQ1007) refuses it. Then it is stored under a new id. The working model is never touched.
    /// </summary>
    /// <param name="archive">The archive's bytes, read to the end.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The stored snapshot, or why it was refused.</returns>
    public async Task<SnapshotImportResult> ImportAsync(Stream archive, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(archive);
        var uploads = Path.Combine(_options.CacheDirectory, "snapshot-imports");
        var upload = Path.Combine(uploads, Guid.NewGuid().ToString("N") + ".zip");
        var check = _policy.CheckEngineWrite(WriteTarget.Cache, upload);
        if (!check.Allowed)
            throw new UnauthorizedAccessException($"{check.RuleId}: the import's copy at {upload} was refused: {check.Reason}");
        Directory.CreateDirectory(uploads);
        try
        {
            await using (var copy = new FileStream(upload, FileMode.CreateNew, FileAccess.Write, FileShare.None, 256 * 1024, useAsync: true))
            {
                var buffer = new byte[256 * 1024];
                long total = 0;
                int read;
                while ((read = await archive.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    total += read;
                    if (total > Limits.MaxArchiveBytes)
                        return Invalid($"The archive is larger than {Limits.MaxArchiveBytes.ToString(CultureInfo.InvariantCulture)} bytes.", tooLarge: true);
                    await copy.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                }
            }

            await _writes.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                return await ImportCoreAsync(upload, ct).ConfigureAwait(false);
            }
            finally
            {
                _writes.Release();
            }
        }
        finally
        {
            AtomicFile.TryDelete(upload);
        }
    }

    private async Task<SnapshotImportResult> ImportCoreAsync(string upload, CancellationToken ct)
    {
        ZipArchive zip;
        try
        {
            zip = ZipFile.OpenRead(upload);
        }
        catch (InvalidDataException ex)
        {
            return Invalid("The file is not a zip archive: " + ex.Message);
        }

        using (zip)
        {
            if (zip.Entries.Count > Limits.MaxEntries)
                return Invalid($"The archive has {zip.Entries.Count.ToString(CultureInfo.InvariantCulture)} entries; at most {Limits.MaxEntries.ToString(CultureInfo.InvariantCulture)} are imported.", tooLarge: true);
            var content = new SortedDictionary<string, ZipArchiveEntry>(StringComparer.Ordinal);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            ZipArchiveEntry? metadataEntry = null;
            long total = 0;
            foreach (var entry in zip.Entries)
            {
                var name = entry.FullName;
                if (name.EndsWith('/') && entry.Length == 0)
                    continue; // a folder entry another tool wrote
                if (!seen.Add(name))
                    return Invalid($"The archive holds {name} twice (names are compared ignoring case).", name);
                if (entry.Length > Limits.MaxEntryBytes)
                    return Invalid($"{name} is larger than {Limits.MaxEntryBytes.ToString(CultureInfo.InvariantCulture)} bytes.", name, tooLarge: true);
                total += entry.Length;
                if (total > Limits.MaxTotalBytes)
                    return Invalid($"The archive's documents are larger than {Limits.MaxTotalBytes.ToString(CultureInfo.InvariantCulture)} bytes together.", tooLarge: true);
                if (entry.Length > 1024 * 1024 && entry.CompressedLength > 0 && entry.Length / entry.CompressedLength > Limits.MaxRatio)
                    return Invalid($"{name} expands more than {Limits.MaxRatio.ToString(CultureInfo.InvariantCulture)} times; it is not imported.", name, tooLarge: true);
                if (name == SnapshotLayout.MetadataEntry)
                    metadataEntry = entry;
                else if (name == SnapshotLayout.IndexEntry)
                    continue; // rebuilt from the documents
                else if (SnapshotLayout.IsContentPath(name))
                    content[name] = entry;
                else
                    return Invalid($"{name} is not a path a snapshot holds (maquettiste.json, model/, extensions/, branding/ or templates/, without '..', hidden or absolute segments).", name);
            }

            if (metadataEntry is null)
                return Invalid($"The archive has no {SnapshotLayout.MetadataEntry}.");
            SnapshotMetadata metadata;
            try
            {
                await using var read = metadataEntry.Open();
                metadata = SnapshotMetadata.Read(ReadBounded(read, 1024 * 1024));
            }
            catch (Exception ex) when (ex is FormatException or InvalidDataException)
            {
                return Invalid(ex.Message, SnapshotLayout.MetadataEntry);
            }

            if (metadata.ModelFormat > EngineVersion.FormatVersion)
            {
                return new SnapshotImportResult(null, [RuleCatalog.Create("MQ1007",
                    $"The snapshot was taken at model format {metadata.ModelFormat.ToString(CultureInfo.InvariantCulture)}; this release reads format {EngineVersion.FormatVersion.ToString(CultureInfo.InvariantCulture)}.",
                    null, SnapshotLayout.MetadataEntry, "/modelFormat")]);
            }

            if (metadata.Name.Length > MaxNameLength || metadata.Description.Length > MaxDescriptionLength)
                return Invalid("snapshot.json has a name or description that is too long.", SnapshotLayout.MetadataEntry);

            var created = DateTimeOffset.ParseExact(metadata.CreatedUtc, "yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
            var id = NewId(metadata.Name, created);
            var final = FileOf(id);
            var temp = Path.Combine(Folder, "." + id + ".zip.tmp");
            Guard(final);
            Guard(temp);
            Directory.CreateDirectory(Folder);
            try
            {
                SnapshotMetadata written;
                await using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 256 * 1024))
                {
                    using var writer = new SnapshotArchiveWriter(stream);
                    foreach (var (name, entry) in content)
                    {
                        ct.ThrowIfCancellationRequested();
                        byte[] bytes;
                        try
                        {
                            await using var read = entry.Open();
                            bytes = ReadBounded(read, entry.Length);
                        }
                        catch (InvalidDataException ex)
                        {
                            return Invalid($"{name} cannot be read: {ex.Message}", name);
                        }

                        writer.Add(SnapshotIndexRow.Of(name, bytes), bytes);
                    }

                    written = writer.Finish(metadata with { IncludesPacks = content.Keys.Any(SnapshotLayout.IsTemplatePath) });
                    await stream.FlushAsync(ct).ConfigureAwait(false);
                }

                if (await CheckDocumentsAsync(temp, ct).ConfigureAwait(false) is { Count: > 0 } problems)
                    return new SnapshotImportResult(null, problems);
                File.Move(temp, final, overwrite: false);
                return new SnapshotImportResult(InfoOf(id, written, new FileInfo(final).Length), []);
            }
            finally
            {
                AtomicFile.TryDelete(temp);
            }
        }
    }

    /// <summary>Loads an archive the way an opened snapshot is loaded and returns the documents that do not parse, are not canonical or name an unsupported format.</summary>
    private async Task<List<Diagnostic>> CheckDocumentsAsync(string file, CancellationToken ct)
    {
        using var documents = new ZipDocumentStore(file, includeTemplates: false);
        var services = EngineServices.CreateReadOnly(_options, documents);
        var result = await services.Loader.LoadAsync(new LoadRequest(null, null, false), null, ct).ConfigureAwait(false);
        return [.. result.Snapshot.LoadDiagnostics.Where(d => d.Rule is "MQ1001" or "MQ1003" or "MQ1007").Select(d => d with { Severity = DiagnosticSeverity.Error })];
    }

    private static SnapshotImportResult Invalid(string message, string? path = null, bool tooLarge = false) =>
        new(null, [RuleCatalog.Create("MQ1011", message, null, path, null)], tooLarge);
}
