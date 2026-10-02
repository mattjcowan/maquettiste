using Maquettiste.Engine.Pipeline;

namespace Maquettiste.Engine.Writing;

/// <summary>
/// The engine's own folder in the repository, <c>&lt;ModelRoot&gt;/.cache</c> (the default journal folder: run journal, run lock,
/// and the cache when a host points <c>CacheDirectory</c> under it). It ignores itself: before the engine
/// writes anything under it, the folder gets a <c>.gitignore</c> holding the single line <c>*</c>, so nothing in it is committed by
/// accident and the repository's own <c>.gitignore</c> is never touched. A journal folder the host chose (<c>JournalDirectory</c>
/// set) is the host's, and gets no file.
/// </summary>
internal static class CacheFolder
{
    /// <summary>The name of the file that makes git ignore the folder.</summary>
    public const string IgnoreFileName = ".gitignore";

    /// <summary>The file's content: one line, <c>*</c>.</summary>
    public static ReadOnlySpan<byte> IgnoreContent => "*\n"u8;

    /// <summary>The engine's own folder, or <see langword="null"/> when the host set <see cref="EngineOptions.JournalDirectory"/>.</summary>
    /// <param name="options">The engine options.</param>
    /// <returns>The absolute folder.</returns>
    public static string? OwnFolder(EngineOptions options) =>
        options.JournalDirectory is null ? Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.EffectiveJournalDirectory)) : null;

    /// <summary>
    /// Called before a write at or under <paramref name="target"/>: when the target lies in the engine's own folder, creates the folder
    /// and writes its <c>.gitignore</c> unless one is there. The file goes through the guard as a <see cref="WriteTarget.Cache"/>
    /// write; a refusal or an I/O failure leaves it out, and the write that follows reports its own error.
    /// </summary>
    /// <param name="options">The engine options.</param>
    /// <param name="paths">The engine-write guard.</param>
    /// <param name="target">The absolute file or folder about to be written.</param>
    public static void EnsureIgnored(EngineOptions options, IOutputPathPolicy paths, string target)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(paths);
        if (OwnFolder(options) is not { } own || !FileSystemPaths.IsUnder(Path.GetFullPath(target), own, allowEqual: true))
            return;
        var file = Path.Combine(own, IgnoreFileName);
        if (File.Exists(file) || !paths.CheckEngineWrite(WriteTarget.Cache, file).Allowed)
            return;
        try
        {
            Directory.CreateDirectory(own);
            using var stream = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            stream.Write(IgnoreContent);
        }
        catch (IOException)
        {
            // Another run wrote it first, or the folder cannot be written (the write that follows says so).
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
