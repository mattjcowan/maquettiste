using System.Globalization;
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

    /// <inheritdoc/>
    public async Task<PhysicalSnapshot?> LoadAsync(string databaseName, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(databaseName);
        var path = FullPath(databaseName);
        if (!File.Exists(path))
            return null;
        var bytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
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

    /// <inheritdoc/>
    public async Task SaveAsync(PhysicalSnapshot snapshot, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ct.ThrowIfCancellationRequested();
        var path = FullPath(snapshot.Name);
        var fileName = Path.GetFileName(path);
        var temp = Path.Combine(Path.GetDirectoryName(path)!, "." + fileName + ".mq-snapshot.tmp");
        Guard(path);
        Guard(temp);

        var bytes = json.Serialize(SnapshotCapture.Sorted(snapshot), SchemaFile, Folder + "/" + fileName);
        if (File.Exists(path))
        {
            var existing = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
            if (existing.AsSpan().SequenceEqual(bytes))
                return;
        }

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
