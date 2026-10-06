using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using Maquettiste.Engine.Hashing;
using Maquettiste.Engine.Loading;
using Maquettiste.Engine.Localization;

namespace Maquettiste.Engine.Snapshots;

/// <summary>
/// The layout of a snapshot archive (docs/engineering/snapshots.md section 3): the model folder's documents at their model-relative
/// paths (<c>maquettiste.json</c>, <c>model/**</c>, <c>extensions/**</c>, <c>branding/**</c>, and <c>templates/**</c> when the packs
/// are included), in ordinal order, then <c>snapshot-index.json</c> and, last, <c>snapshot.json</c> (so a metadata edit rewrites only
/// the archive's tail). Every entry carries the same time and the same compression, so the same content gives the same bytes.
/// </summary>
internal static class SnapshotLayout
{
    /// <summary>The metadata entry, always the last one.</summary>
    public const string MetadataEntry = "snapshot.json";

    /// <summary>The index entry: one row per document (path, hash, and the id, kind and name of an element).</summary>
    public const string IndexEntry = "snapshot-index.json";

    /// <summary>The folder of the model folder that holds the snapshots (beside <c>snapshots/</c>, the schema snapshots migrations diff against).</summary>
    public const string Folder = "model-snapshots";

    /// <summary>The folder of the packs, included only on request.</summary>
    public const string TemplatesFolder = "templates";

    /// <summary>The value of the metadata's <c>kind</c>.</summary>
    public const string Kind = "maquettiste-model-snapshot";

    /// <summary>The archive format this engine writes and reads.</summary>
    public const int Format = 1;

    /// <summary>The time every entry carries (the zip format's earliest).</summary>
    public static readonly DateTimeOffset EntryTime = new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>The compression of every entry.</summary>
    public const CompressionLevel Compression = CompressionLevel.Fastest;

    /// <summary>The folders whose documents a snapshot holds, besides <c>maquettiste.json</c> (and <c>templates</c> on request).</summary>
    public static readonly IReadOnlyList<string> ContentFolders = ["model", "extensions", "branding"];

    /// <summary>Whether a path is a document a snapshot may hold: <c>maquettiste.json</c>, or a safe path under a content folder or the packs.</summary>
    /// <param name="path">The model-relative path.</param>
    /// <returns><see langword="true"/> when it is.</returns>
    public static bool IsContentPath(string path)
    {
        if (path == ModelPaths.SettingsFile)
            return true;
        if (!IsSafePath(path))
            return false;
        var slash = path.IndexOf('/', StringComparison.Ordinal);
        if (slash <= 0)
            return false;
        var top = path[..slash];
        return top == TemplatesFolder || ContentFolders.Contains(top, StringComparer.Ordinal);
    }

    /// <summary>Whether a path is under the packs.</summary>
    /// <param name="path">The model-relative path.</param>
    /// <returns><see langword="true"/> when it is.</returns>
    public static bool IsTemplatePath(string path) => path.StartsWith(TemplatesFolder + "/", StringComparison.Ordinal);

    /// <summary>
    /// Whether a relative path is safe to hold and to write back: <c>/</c> separators, no empty, <c>.</c>, <c>..</c> or hidden segment,
    /// no drive, colon, backslash or control character, at most 1,024 characters.
    /// </summary>
    /// <param name="path">The path.</param>
    /// <returns><see langword="true"/> when it is.</returns>
    public static bool IsSafePath(string? path)
    {
        if (string.IsNullOrEmpty(path) || path.Length > 1024 || path[0] == '/' || path[^1] == '/')
            return false;
        foreach (var c in path)
        {
            if (c < 0x20 || c == 0x7F || c is '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|')
                return false;
        }

        foreach (var segment in path.Split('/'))
        {
            if (segment.Length == 0 || segment[0] == '.' || segment[^1] is ' ' or '.')
                return false;
        }

        return true;
    }
}

/// <summary>One document of a snapshot: its model-relative path and content hash, and for an element document its id, kind and name.</summary>
/// <param name="Path">The model-relative path.</param>
/// <param name="Hash">The SHA-256 of its bytes (<see cref="ContentHash"/>).</param>
/// <param name="Id">The element id, for an element document.</param>
/// <param name="Kind">The element kind, for an element document.</param>
/// <param name="Name">The element name, for an element document with one.</param>
internal sealed record SnapshotIndexRow(string Path, string Hash, string? Id, string? Kind, string? Name)
{
    private static readonly JsonReaderOptions ReaderOptions = new() { CommentHandling = JsonCommentHandling.Disallow, MaxDepth = 256 };

    /// <summary>The row of a document: its hash, and the identity of an element document (a top-level scan, no tree).</summary>
    /// <param name="path">The model-relative path.</param>
    /// <param name="bytes">The bytes.</param>
    /// <returns>The row.</returns>
    public static SnapshotIndexRow Of(string path, ReadOnlySpan<byte> bytes)
    {
        var hash = ContentHash.Of(bytes);
        if (ModelPaths.Classify(path) != ModelFileKind.Element || LocaleShardReader.IsShard(bytes))
            return new SnapshotIndexRow(path, hash, null, null, null);
        string? id = null, kind = null, name = null;
        try
        {
            var reader = new Utf8JsonReader(bytes, ReaderOptions);
            if (reader.Read() && reader.TokenType == JsonTokenType.StartObject)
            {
                while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
                {
                    var which = reader.ValueTextEquals("id"u8) ? 1 : reader.ValueTextEquals("kind"u8) ? 2 : reader.ValueTextEquals("name"u8) ? 3 : 0;
                    if (!reader.Read())
                        break;
                    if (which > 0 && reader.TokenType == JsonTokenType.String)
                    {
                        var value = reader.GetString();
                        if (which == 1)
                            id = value;
                        else if (which == 2)
                            kind = value;
                        else
                            name = value;
                    }
                    else
                    {
                        reader.Skip();
                    }
                }
            }
        }
        catch (JsonException)
        {
            // Not JSON: a plain document (the loader reports it as MQ1001).
        }

        return id is null || kind is null ? new SnapshotIndexRow(path, hash, null, null, null) : new SnapshotIndexRow(path, hash, id, kind, name);
    }

    /// <summary>Writes the index entry: <c>{"format":1,"files":[{"path","hash","id","kind","name"}...]}</c>, rows in path order.</summary>
    /// <param name="rows">The rows, in path order.</param>
    /// <returns>The bytes.</returns>
    public static byte[] Write(IEnumerable<SnapshotIndexRow> rows)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("format", SnapshotLayout.Format);
            writer.WriteStartArray("files");
            foreach (var row in rows)
            {
                writer.WriteStartObject();
                writer.WriteString("path", row.Path);
                writer.WriteString("hash", row.Hash);
                if (row.Id is not null)
                {
                    writer.WriteString("id", row.Id);
                    writer.WriteString("kind", row.Kind);
                    if (row.Name is not null)
                        writer.WriteString("name", row.Name);
                }

                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        buffer.WriteByte((byte)'\n');
        return buffer.ToArray();
    }

    /// <summary>Reads the index entry.</summary>
    /// <param name="bytes">The bytes.</param>
    /// <returns>The rows.</returns>
    /// <exception cref="FormatException">The entry is not an index.</exception>
    public static List<SnapshotIndexRow> Read(ReadOnlySpan<byte> bytes)
    {
        try
        {
            var rows = new List<SnapshotIndexRow>();
            using var document = JsonDocument.Parse(bytes.ToArray());
            if (!document.RootElement.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array)
                throw new FormatException("The snapshot index has no files.");
            foreach (var file in files.EnumerateArray())
            {
                string? Text(string name) => file.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                rows.Add(new SnapshotIndexRow(Text("path") ?? throw new FormatException("An index row has no path."), Text("hash") ?? "", Text("id"), Text("kind"), Text("name")));
            }

            return rows;
        }
        catch (JsonException ex)
        {
            throw new FormatException("The snapshot index is not valid JSON: " + ex.Message, ex);
        }
    }
}

/// <summary>The metadata entry of a snapshot (<c>snapshot.json</c>).</summary>
internal sealed record SnapshotMetadata
{
    /// <summary>The snapshot's name.</summary>
    public required string Name { get; init; }

    /// <summary>What it is for; empty when none.</summary>
    public string Description { get; init; } = "";

    /// <summary>Who took it; empty when unknown.</summary>
    public string Author { get; init; } = "";

    /// <summary>When it was taken, UTC, <c>yyyy-MM-ddTHH:mm:ssZ</c>.</summary>
    public required string CreatedUtc { get; init; }

    /// <summary>Why it exists: <c>user</c>, or <c>before-restore</c> for the automatic safety snapshot.</summary>
    public string Origin { get; init; } = "user";

    /// <summary>Whether it is marked as ready for review.</summary>
    public bool Published { get; init; }

    /// <summary>Whether it holds the packs (<c>templates/</c>).</summary>
    public bool IncludesPacks { get; init; }

    /// <summary>The model format of <c>maquettiste.json</c> when it was taken.</summary>
    public int ModelFormat { get; init; } = EngineVersion.FormatVersion;

    /// <summary>The release that took it.</summary>
    public string Engine { get; init; } = EngineVersion.Product;

    /// <summary>The hash of the model it was taken from: over the path and hash of every document but the packs.</summary>
    public string ModelHash { get; init; } = "";

    /// <summary>The hash over the packs' documents, when it holds them.</summary>
    public string? PacksHash { get; init; }

    /// <summary>The number of documents.</summary>
    public int Files { get; init; }

    /// <summary>The number of elements.</summary>
    public int Elements { get; init; }

    /// <summary>The number of elements of each kind, ordinal by kind.</summary>
    public IReadOnlyDictionary<string, int> Kinds { get; init; } = new SortedDictionary<string, int>(StringComparer.Ordinal);

    /// <summary>The metadata with the counts and hashes of the rows filled in.</summary>
    /// <param name="rows">Every document's row.</param>
    /// <returns>The metadata.</returns>
    public SnapshotMetadata WithCounts(IReadOnlyList<SnapshotIndexRow> rows)
    {
        using var model = new HashBuilder();
        using var packs = new HashBuilder();
        var kinds = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var packFiles = 0;
        foreach (var row in rows)
        {
            if (SnapshotLayout.IsTemplatePath(row.Path))
            {
                packs.Add(row.Path).Add(row.Hash);
                packFiles++;
                continue;
            }

            model.Add(row.Path).Add(row.Hash);
            if (row is { Id: { } id, Kind: { } kind } && ids.Add(id))
                kinds[kind] = kinds.GetValueOrDefault(kind) + 1;
        }

        return this with
        {
            ModelHash = model.Finish(),
            PacksHash = IncludesPacks ? packs.Finish() : null,
            Files = rows.Count,
            Elements = ids.Count,
            Kinds = kinds,
        };
    }

    /// <summary>Writes the entry: indented JSON in a fixed property order, ending with a newline.</summary>
    /// <returns>The bytes.</returns>
    public byte[] Write()
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            writer.WriteStartObject();
            writer.WriteString("kind", SnapshotLayout.Kind);
            writer.WriteNumber("format", SnapshotLayout.Format);
            writer.WriteString("name", Name);
            writer.WriteString("description", Description);
            writer.WriteString("author", Author);
            writer.WriteString("createdUtc", CreatedUtc);
            writer.WriteString("origin", Origin);
            writer.WriteBoolean("published", Published);
            writer.WriteBoolean("includesPacks", IncludesPacks);
            writer.WriteNumber("modelFormat", ModelFormat);
            writer.WriteString("engine", Engine);
            writer.WriteString("modelHash", ModelHash);
            if (PacksHash is not null)
                writer.WriteString("packsHash", PacksHash);
            writer.WriteNumber("files", Files);
            writer.WriteNumber("elements", Elements);
            writer.WriteStartObject("kinds");
            foreach (var (kind, count) in Kinds.OrderBy(k => k.Key, StringComparer.Ordinal))
                writer.WriteNumber(kind, count);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        buffer.WriteByte((byte)'\n');
        return buffer.ToArray();
    }

    /// <summary>Reads the entry.</summary>
    /// <param name="bytes">The bytes.</param>
    /// <returns>The metadata.</returns>
    /// <exception cref="FormatException">The entry is not snapshot metadata this engine reads.</exception>
    public static SnapshotMetadata Read(ReadOnlySpan<byte> bytes)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(bytes.ToArray());
        }
        catch (JsonException ex)
        {
            throw new FormatException("snapshot.json is not valid JSON: " + ex.Message, ex);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new FormatException("snapshot.json is not an object.");
            string? Text(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            int? Number(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : null;
            bool Flag(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
            if (Text("kind") != SnapshotLayout.Kind)
                throw new FormatException($"snapshot.json is not a model snapshot (kind must be {SnapshotLayout.Kind}).");
            if (Number("format") is not { } format || format != SnapshotLayout.Format)
                throw new FormatException($"snapshot.json has format {Number("format")?.ToString(CultureInfo.InvariantCulture) ?? "(none)"}; this release reads format {SnapshotLayout.Format}.");
            var name = Text("name");
            if (string.IsNullOrWhiteSpace(name))
                throw new FormatException("snapshot.json has no name.");
            var created = Text("createdUtc");
            if (created is null || !DateTimeOffset.TryParseExact(created, "yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out _))
                throw new FormatException("snapshot.json has no createdUtc of the form yyyy-MM-ddTHH:mm:ssZ.");
            var kinds = new SortedDictionary<string, int>(StringComparer.Ordinal);
            if (root.TryGetProperty("kinds", out var k) && k.ValueKind == JsonValueKind.Object)
            {
                foreach (var p in k.EnumerateObject())
                {
                    if (p.Value.ValueKind == JsonValueKind.Number && p.Value.TryGetInt32(out var count))
                        kinds[p.Name] = count;
                }
            }

            return new SnapshotMetadata
            {
                Name = name,
                Description = Text("description") ?? "",
                Author = Text("author") ?? "",
                CreatedUtc = created,
                Origin = Text("origin") ?? "user",
                Published = Flag("published"),
                IncludesPacks = Flag("includesPacks"),
                ModelFormat = Number("modelFormat") ?? EngineVersion.FormatVersion,
                Engine = Text("engine") ?? "",
                ModelHash = Text("modelHash") ?? "",
                PacksHash = Text("packsHash"),
                Files = Number("files") ?? 0,
                Elements = Number("elements") ?? 0,
                Kinds = kinds,
            };
        }
    }
}

/// <summary>
/// Writes a snapshot archive deterministically: content entries in strictly increasing ordinal order, then the index and the metadata,
/// every entry with <see cref="SnapshotLayout.EntryTime"/> and <see cref="SnapshotLayout.Compression"/>.
/// </summary>
internal sealed class SnapshotArchiveWriter : IDisposable
{
    private readonly ZipArchive _zip;
    private readonly List<SnapshotIndexRow> _rows = [];
    private bool _finished;

    /// <summary>Starts an archive on a stream (left open).</summary>
    /// <param name="output">The stream.</param>
    public SnapshotArchiveWriter(Stream output) => _zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);

    /// <summary>The rows added so far, in order.</summary>
    public IReadOnlyList<SnapshotIndexRow> Rows => _rows;

    /// <summary>Adds a document.</summary>
    /// <param name="row">Its row (path and hash).</param>
    /// <param name="bytes">Its bytes.</param>
    /// <exception cref="InvalidOperationException">The path is not after the previous one, or not a content path.</exception>
    public void Add(SnapshotIndexRow row, ReadOnlySpan<byte> bytes)
    {
        if (_rows.Count > 0 && string.CompareOrdinal(_rows[^1].Path, row.Path) >= 0)
            throw new InvalidOperationException($"Snapshot entries must be added in ordinal order: {row.Path} after {_rows[^1].Path}.");
        if (!SnapshotLayout.IsContentPath(row.Path))
            throw new InvalidOperationException($"{row.Path} is not a document a snapshot holds.");
        Write(row.Path, bytes);
        _rows.Add(row);
    }

    /// <summary>Writes the index and the metadata (with the counts and hashes of the rows) and closes the archive.</summary>
    /// <param name="metadata">The metadata.</param>
    /// <returns>The metadata as written.</returns>
    public SnapshotMetadata Finish(SnapshotMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        var complete = metadata.WithCounts(_rows);
        Write(SnapshotLayout.IndexEntry, SnapshotIndexRow.Write(_rows));
        Write(SnapshotLayout.MetadataEntry, complete.Write());
        _zip.Dispose();
        _finished = true;
        return complete;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (!_finished)
            _zip.Dispose();
    }

    private void Write(string name, ReadOnlySpan<byte> bytes)
    {
        var entry = _zip.CreateEntry(name, SnapshotLayout.Compression);
        entry.LastWriteTime = SnapshotLayout.EntryTime;
        using var stream = entry.Open();
        stream.Write(bytes);
    }
}
