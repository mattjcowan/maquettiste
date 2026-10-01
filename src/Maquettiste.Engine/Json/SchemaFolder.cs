using System.Globalization;
using System.Text.Json;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Loading;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Pipeline;
using Maquettiste.Engine.Validation;
using Maquettiste.Engine.Writing;

namespace Maquettiste.Engine.Json;

/// <summary>
/// The copies of the embedded JSON schemas in <c>&lt;ModelRoot&gt;/.schema/v1/</c>, which every model file names through
/// <c>$schema</c> so an IDE validates and completes it (engine-design.md section 3). <c>init</c> and the editor's startup refresh them;
/// <c>validate</c> and <c>generate</c> warn MQ1008 when they differ from the embedded schemas.
/// </summary>
public static class SchemaFolder
{
    /// <summary>The folder, relative to the model root.</summary>
    public const string RelativePath = ".schema/v1";

    /// <summary>The rule a folder that differs from the embedded schemas raises.</summary>
    internal const string Rule = "MQ1008";

    /// <summary>How many file names the MQ1008 message lists before "and N more".</summary>
    internal const int NamedFiles = 5;

    private static readonly SchemaRegistry Embedded = new();

    /// <summary>Compares the folder with the embedded schemas, byte for byte. Reads only; never throws for the folder's content.</summary>
    /// <param name="options">The engine options (the model root).</param>
    /// <returns>The missing, stale and extra file names.</returns>
    public static SchemaFolderStatus Status(EngineOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var folder = FolderOf(options);
        var missing = new List<string>();
        var stale = new List<string>();
        foreach (var name in Embedded.FileNames)
        {
            switch (Compare(Path.Combine(folder, name), Embedded.GetFileBytes(name)))
            {
                case false:
                    stale.Add(name);
                    break;
                case null:
                    missing.Add(name);
                    break;
            }
        }

        return new SchemaFolderStatus(missing, stale, Extras(folder));
    }

    /// <summary>
    /// Brings the folder in line with the embedded schemas: files the engine no longer ships are deleted, then missing and stale files
    /// are written (staged and renamed). Every path goes through the engine's write guard (<see cref="WriteTarget.Model"/>). A folder that
    /// is already current is not touched.
    /// </summary>
    /// <param name="options">The engine options (the model root).</param>
    /// <param name="ct">Cancellation, observed between files.</param>
    /// <returns>The status before the refresh: what was written and deleted.</returns>
    /// <exception cref="UnauthorizedAccessException">The write guard refused a path, or the operating system did.</exception>
    /// <exception cref="IOException">A file could not be written or deleted.</exception>
    public static async Task<SchemaFolderStatus> RefreshAsync(EngineOptions options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(options);
        var status = Status(options);
        if (status.IsCurrent)
            return status;

        var folder = FolderOf(options);
        var guard = new OutputPathPolicy(options, null);
        var writes = status.Missing.Concat(status.Stale).Order(StringComparer.Ordinal).ToList();
        foreach (var name in status.Extra.Concat(writes))
            Guard(guard, Path.Combine(folder, name));
        Directory.CreateDirectory(folder);

        // Deletes first: on a case-insensitive disk an extra name may be another spelling of a file about to be written.
        foreach (var name in status.Extra)
        {
            ct.ThrowIfCancellationRequested();
            File.Delete(Path.Combine(folder, name));
        }

        // The tag keeps two processes refreshing at once (the editor and an MCP server) off each other's staged files.
        var tag = string.Create(CultureInfo.InvariantCulture, $"schema{Environment.ProcessId}-0");
        foreach (var name in writes)
        {
            var full = Path.Combine(folder, name);
            Guard(guard, AtomicFile.TempPath(full, tag));
            await AtomicFile.WriteAsync(full, Embedded.GetFileBytes(name), tag, ct).ConfigureAwait(false);
        }

        return status;
    }

    /// <summary>
    /// The MQ1008 finding for a folder that differs from the embedded schemas, on the project's settings file and with the severity
    /// <c>validation.rules</c> sets; empty when the folder is current. <c>validate</c> and <c>generate</c> report it; <c>init</c> does not.
    /// </summary>
    /// <param name="options">The engine options (the model root).</param>
    /// <param name="settings">The project settings, for a severity override; <see langword="null"/> reads them from the settings file (a run
    /// answered from the last-run record loads no snapshot), and settings that cannot be read keep the default (warning).</param>
    /// <returns>Zero or one diagnostic.</returns>
    internal static IReadOnlyList<Diagnostic> Findings(EngineOptions options, ProjectSettings? settings)
    {
        var status = Status(options);
        if (status.IsCurrent)
            return [];
        var paths = new ModelPaths(options);
        var diagnostic = RuleCatalog.Create(Rule, Message(status, paths.ToRepoPath(RelativePath)), null, paths.ToRepoPath(ModelPaths.SettingsFile), null);
        settings ??= ReadSettings(paths.ModelRoot);
        return settings is null ? [diagnostic] : ModelValidator.ApplySettings(settings, [diagnostic]);
    }

    private static ProjectSettings? ReadSettings(string modelRoot)
    {
        try
        {
            using var document = JsonDocument.Parse(DocumentReader.StripBom(File.ReadAllBytes(Path.Combine(modelRoot, ModelPaths.SettingsFile))));
            return ElementReader.Read<ProjectSettings>(document.RootElement);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException || DocumentReader.IsDeserializationFailure(ex))
        {
            return null;
        }
    }

    /// <summary>The MQ1008 message: the differing files (the first <see cref="NamedFiles"/>, ordinal) and how to refresh them.</summary>
    /// <param name="status">A status that is not current.</param>
    /// <param name="folder">The folder, repo-relative.</param>
    /// <returns>The message.</returns>
    internal static string Message(SchemaFolderStatus status, string folder)
    {
        var missing = status.Missing.ToHashSet(StringComparer.Ordinal);
        var stale = status.Stale.ToHashSet(StringComparer.Ordinal);
        var differing = status.Differing;
        var named = differing.Take(NamedFiles).Select(n => n + (missing.Contains(n) ? " (missing)" : stale.Contains(n) ? " (out of date)" : " (no longer shipped)"));
        var more = differing.Count > NamedFiles ? string.Create(CultureInfo.InvariantCulture, $", and {differing.Count - NamedFiles} more") : "";
        return $"The schema copies in {folder}/ differ from the ones this version ships: {string.Join(", ", named)}{more}. " +
            "Run 'maquettiste init', or start the editor, to refresh them; IDEs check model files against these copies.";
    }

    private static string FolderOf(EngineOptions options) =>
        Path.Combine(Path.GetFullPath(options.EffectiveModelRoot), ".schema", "v1");

    /// <summary>Whether a file holds the bytes: <see langword="null"/> when it does not exist; an unreadable file counts as different.</summary>
    private static bool? Compare(string path, ReadOnlyMemory<byte> expected)
    {
        try
        {
            if (!File.Exists(path))
                return null;
            var info = new FileInfo(path);
            return info.Length == expected.Length && File.ReadAllBytes(path).AsSpan().SequenceEqual(expected.Span);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>The <c>*.json</c> files of the folder the engine does not ship, ordinal; names compare as the disk does.</summary>
    private static List<string> Extras(string folder)
    {
        try
        {
            if (!Directory.Exists(folder))
                return [];
            var comparer = FileSystemPaths.Comparison == StringComparison.Ordinal ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
            var known = Embedded.FileNames.ToHashSet(comparer);
            return [.. Directory.EnumerateFiles(folder, "*.json").Select(Path.GetFileName).OfType<string>()
                .Where(n => !known.Contains(n)).Order(StringComparer.Ordinal)];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static void Guard(IOutputPathPolicy guard, string fullPath)
    {
        var check = guard.CheckEngineWrite(WriteTarget.Model, fullPath);
        if (!check.Allowed)
            throw new UnauthorizedAccessException($"Refused to write the schema copy '{fullPath}': {check.Reason ?? check.RuleId ?? "path policy"}.");
    }
}

/// <summary>How <c>.schema/v1/</c> differs from the embedded schemas (<see cref="SchemaFolder"/>). Each list is ordinal.</summary>
/// <param name="Missing">Embedded schemas with no file in the folder (all of them when the folder does not exist).</param>
/// <param name="Stale">Files whose bytes differ from the embedded schema of the same name.</param>
/// <param name="Extra"><c>*.json</c> files the engine does not ship.</param>
public sealed record SchemaFolderStatus(IReadOnlyList<string> Missing, IReadOnlyList<string> Stale, IReadOnlyList<string> Extra)
{
    /// <summary>Whether the folder holds exactly the embedded schemas.</summary>
    public bool IsCurrent => Missing.Count == 0 && Stale.Count == 0 && Extra.Count == 0;

    /// <summary>Every differing file name, ordinal.</summary>
    public IReadOnlyList<string> Differing => [.. Missing.Concat(Stale).Concat(Extra).Order(StringComparer.Ordinal)];

    /// <summary>
    /// What <see cref="SchemaFolder.RefreshAsync"/> changes for this status, five names per list at most, for a host's log line: for
    /// example <c>wrote entity.json, table.json; removed retired.json</c>. Empty when the folder is current.
    /// </summary>
    /// <returns>The text.</returns>
    public string Describe()
    {
        var parts = new List<string>();
        var written = Missing.Concat(Stale).Order(StringComparer.Ordinal).ToList();
        if (written.Count > 0)
            parts.Add("wrote " + Names(written));
        if (Extra.Count > 0)
            parts.Add("removed " + Names(Extra));
        return string.Join("; ", parts);

        static string Names(IReadOnlyList<string> names) =>
            string.Join(", ", names.Take(SchemaFolder.NamedFiles))
            + (names.Count > SchemaFolder.NamedFiles ? string.Create(CultureInfo.InvariantCulture, $" and {names.Count - SchemaFolder.NamedFiles} more") : "");
    }
}
