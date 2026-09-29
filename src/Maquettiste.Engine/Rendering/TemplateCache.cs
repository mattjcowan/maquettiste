using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Hashing;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Pipeline;
using Scriban;

namespace Maquettiste.Engine.Rendering;

/// <summary>Parses and caches Scriban templates for one run, keyed by (pack, path, file hash, delimiters) (W5).</summary>
internal interface ITemplateCache
{
    /// <summary>Returns a parsed template; parse errors surface as MQ6003 with line and column.</summary>
    /// <param name="pack">The pack.</param>
    /// <param name="path">The pack-relative template path.</param>
    /// <param name="delimiters">Custom delimiters, if any.</param>
    /// <returns>The template.</returns>
    /// <exception cref="TemplateException">The path is refused, the file cannot be read, or the template does not parse (MQ6003).</exception>
    Template Get(LoadedPack pack, string path, Delimiters? delimiters);

    /// <summary>Returns a parsed inline template (a unit's <c>output</c> expression), always with <c>{{ }}</c> delimiters.</summary>
    /// <param name="text">The template text.</param>
    /// <param name="sourcePath">The repo-relative file the text comes from (<c>pack.json</c>).</param>
    /// <param name="jsonPointer">The pointer of the text in that file.</param>
    /// <returns>The template.</returns>
    /// <exception cref="TemplateException">The text does not parse (MQ6003).</exception>
    Template GetInline(string text, string sourcePath, string jsonPointer);

    /// <summary>Reads a template file's text (LF line endings, no BOM) through the same cache.</summary>
    /// <param name="pack">The pack.</param>
    /// <param name="path">The pack-relative path.</param>
    /// <returns>The text.</returns>
    string ReadText(LoadedPack pack, string path);

    /// <summary>Describes a template this cache returned: its repo-relative path and how to map error positions back to it.</summary>
    /// <param name="template">The template.</param>
    /// <returns>The description, or <see langword="null"/> for a template this cache did not produce.</returns>
    TemplateInfo? Describe(Template template);
}

/// <summary>Where a cached template comes from.</summary>
/// <param name="RepoPath">The repo-relative path of the file (for <c>output</c> expressions: the pack's <c>pack.json</c>).</param>
/// <param name="JsonPointer">For an inline template, its pointer in <paramref name="RepoPath"/>.</param>
/// <param name="Map">For a template translated from custom delimiters, the map from translated to source positions.</param>
internal sealed record TemplateInfo(string RepoPath, string? JsonPointer, PositionMap? Map)
{
    /// <summary>Maps a 0-based Scriban position to a 1-based source line and column (<see langword="null"/> for inline templates).</summary>
    /// <param name="line">The 0-based line.</param>
    /// <param name="column">The 0-based column.</param>
    /// <returns>The source position.</returns>
    public (int? Line, int? Column) Position(int line, int column)
    {
        if (JsonPointer is not null || line < 0)
            return (null, null);
        if (Map is null)
            return (line + 1, Math.Max(column, 0) + 1);
        var (l, c) = Map.Map(line, column);
        return (l, c);
    }
}

/// <summary>Template errors that fail a unit: MQ6003 parse errors and refused template paths.</summary>
/// <param name="diagnostics">The diagnostics, at least one.</param>
internal sealed class TemplateException(IReadOnlyList<Diagnostic> diagnostics) : Exception(diagnostics[0].Message)
{
    /// <summary>The diagnostics.</summary>
    public IReadOnlyList<Diagnostic> Diagnostics { get; } = diagnostics;
}

/// <summary>
/// The template cache (W5; engine-design.md section 9). One instance per run (<c>EngineServices.CreateRenderer()</c> builds a fresh
/// one for each run and each preview), so parsed templates never outlive the run in a long-lived host. Each file is read and hashed
/// once per run, and each (pack, path, file hash, delimiters) is parsed once, whatever the number of units and threads using it;
/// failures are cached too, so every unit of a broken template reports the same MQ6003.
/// </summary>
internal sealed class TemplateCache : ITemplateCache
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private readonly ConcurrentDictionary<(string Root, string Path), Lazy<SourceFile>> _files = new();
    private readonly ConcurrentDictionary<(string Pack, string Path, string Hash, string? Open, string? Close), Lazy<Template>> _templates = new();
    private readonly ConcurrentDictionary<(string Text, string Source, string Pointer), Lazy<Template>> _inline = new();
    private readonly ConcurrentDictionary<Template, TemplateInfo> _info = new(ReferenceEqualityComparer.Instance);
    private readonly IReadOnlyDictionary<(string Pack, string Path), string>? _overlay;

    /// <summary>Creates a cache that reads every file from disk.</summary>
    public TemplateCache()
    {
    }

    /// <summary>
    /// Creates a cache that serves unsaved text for some pack files (generation-ui.md section 5.2): an overlay entry, keyed by pack
    /// name and pack-relative path, replaces the file's bytes; every other file is read from disk. A preview renders with it; nothing
    /// is written.
    /// </summary>
    /// <param name="overlay">Pack name and pack-relative path to text.</param>
    public TemplateCache(IReadOnlyDictionary<(string Pack, string Path), string>? overlay) => _overlay = overlay;

    /// <inheritdoc/>
    public Template Get(LoadedPack pack, string path, Delimiters? delimiters)
    {
        ArgumentNullException.ThrowIfNull(pack);
        var file = File(pack, path);
        var open = delimiters?.Open;
        var close = delimiters?.Close;
        if (open is not null && close is not null && !DelimiterTranslator.NeedsTranslation(open, close))
            open = close = null;
        var key = (pack.Name, file.Path, file.Hash, open, close);
        return _templates.GetOrAdd(key, k => new Lazy<Template>(() => Parse(file, k.Open, k.Close))).Value;
    }

    /// <inheritdoc/>
    public Template GetInline(string text, string sourcePath, string jsonPointer) =>
        _inline.GetOrAdd((text, sourcePath, jsonPointer), k => new Lazy<Template>(() =>
        {
            var template = Template.Parse(k.Text, k.Source + "#" + k.Pointer);
            var info = new TemplateInfo(k.Source, k.Pointer, null);
            if (template.HasErrors)
            {
                throw new TemplateException([.. template.Messages.Select(m => new Diagnostic("MQ6003", DiagnosticSeverity.Error,
                    $"The expression '{k.Text}' does not parse: {m.Message}", null, k.Source, k.Pointer, null, null))]);
            }

            _info[template] = info;
            return template;
        })).Value;

    /// <inheritdoc/>
    public string ReadText(LoadedPack pack, string path) => File(pack, path).Text;

    /// <inheritdoc/>
    public TemplateInfo? Describe(Template template) => _info.TryGetValue(template, out var info) ? info : null;

    private SourceFile File(LoadedPack pack, string path)
    {
        var repoPackPath = pack.RelativePath.TrimEnd('/');
        if (!PackPaths.TryNormalize(path, out var normalized, out var error))
            throw Refused(repoPackPath + "/" + path, $"The template path '{path}' is refused: {error}");
        if (_overlay is not null && _overlay.TryGetValue((pack.Name, normalized), out var unsaved))
        {
            return _files.GetOrAdd(("overlay:" + pack.Name, normalized), k => new Lazy<SourceFile>(() => new SourceFile(k.Path,
                repoPackPath.Length == 0 ? k.Path : repoPackPath + "/" + k.Path,
                unsaved.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n'), ContentHash.Of(Encoding.UTF8.GetBytes(unsaved))))).Value;
        }

        return _files.GetOrAdd((pack.RootPath, normalized), k => new Lazy<SourceFile>(() => Read(k.Root, k.Path, repoPackPath))).Value;
    }

    private static SourceFile Read(string root, string path, string repoPackPath)
    {
        var repoPath = repoPackPath.Length == 0 ? path : repoPackPath + "/" + path;
        if (!PackPaths.TryResolve(root, path, out var full, out var error))
            throw Refused(repoPath, $"The template '{path}' is refused: {error}");
        byte[] bytes;
        try
        {
            bytes = System.IO.File.ReadAllBytes(full);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw Refused(repoPath, $"The template '{path}' cannot be read: {ex.Message}");
        }

        string text;
        try
        {
            text = StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            throw Refused(repoPath, $"The template '{path}' is not valid UTF-8.");
        }

        if (text.Length > 0 && text[0] == '﻿')
            text = text[1..];
        text = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        return new SourceFile(path, repoPath, text, ContentHash.Of(bytes));
    }

    private Template Parse(SourceFile file, string? open, string? close)
    {
        PositionMap? map = null;
        var text = file.Text;
        if (open is not null && close is not null)
        {
            try
            {
                var translated = DelimiterTranslator.Translate(file.Text, open, close);
                text = translated.Text;
                map = translated.Map;
            }
            catch (DelimiterException ex)
            {
                var (line, column) = LineColumn(file.Text, ex.Offset);
                throw new TemplateException([new Diagnostic("MQ6003", DiagnosticSeverity.Error, ex.Message, null, file.RepoPath, null, line, column)]);
            }
        }

        var template = Template.Parse(text, file.RepoPath);
        var info = new TemplateInfo(file.RepoPath, null, map);
        if (template.HasErrors)
        {
            throw new TemplateException([.. template.Messages.Select(m =>
            {
                var (line, column) = info.Position(m.Span.Start.Line, m.Span.Start.Column);
                return new Diagnostic("MQ6003", DiagnosticSeverity.Error, m.Message, null, file.RepoPath, null, line, column);
            })]);
        }

        _info[template] = info;
        return template;
    }

    private static (int Line, int Column) LineColumn(string text, int offset)
    {
        var line = 1;
        var column = 1;
        for (var i = 0; i < offset && i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                line++;
                column = 1;
            }
            else
            {
                column++;
            }
        }

        return (line, column);
    }

    private static TemplateException Refused(string repoPath, string message) =>
        new([new Diagnostic("MQ6003", DiagnosticSeverity.Error, message, null, repoPath, null, null, null)]);

    private sealed record SourceFile(string Path, string RepoPath, string Text, string Hash);
}

/// <summary>Pack-relative template paths: normalization and confinement to the pack folder.</summary>
internal static class PackPaths
{
    /// <summary>
    /// Normalizes a pack-relative path: <c>\</c> becomes <c>/</c>, a leading <c>./</c> is dropped; empty, rooted, drive-qualified
    /// paths and <c>.</c>, <c>..</c> or empty segments are refused.
    /// </summary>
    /// <param name="path">The path as written.</param>
    /// <param name="normalized">The normalized path.</param>
    /// <param name="error">Why it is refused.</param>
    /// <returns>Whether the path is acceptable.</returns>
    public static bool TryNormalize(string? path, [NotNullWhen(true)] out string? normalized, [NotNullWhen(false)] out string? error)
    {
        normalized = null;
        var p = (path ?? "").Trim().Replace('\\', '/');
        while (p.StartsWith("./", StringComparison.Ordinal))
            p = p[2..];
        if (p.Length == 0)
        {
            error = "the path is empty";
            return false;
        }

        if (p[0] == '/' || p.Contains(':', StringComparison.Ordinal))
        {
            error = "the path must be relative to the pack folder";
            return false;
        }

        foreach (var segment in p.Split('/'))
        {
            if (segment.Length == 0 || segment is "." or "..")
            {
                error = "the path must not contain empty, '.' or '..' segments";
                return false;
            }

            if (segment.Any(char.IsControl))
            {
                error = "the path contains control characters";
                return false;
            }
        }

        normalized = p;
        error = null;
        return true;
    }

    /// <summary>
    /// Resolves a normalized pack-relative path to a full path and checks that its real location (after every symbolic link)
    /// stays inside the pack folder's real location.
    /// </summary>
    /// <param name="root">The absolute pack folder.</param>
    /// <param name="normalized">The normalized pack-relative path.</param>
    /// <param name="full">The full path.</param>
    /// <param name="error">Why it is refused.</param>
    /// <returns>Whether the path is inside the pack.</returns>
    public static bool TryResolve(string root, string normalized, [NotNullWhen(true)] out string? full, [NotNullWhen(false)] out string? error)
    {
        var rootFull = Path.GetFullPath(root);
        full = Path.GetFullPath(Path.Combine(rootFull, normalized.Replace('/', Path.DirectorySeparatorChar)));
        var realRoot = RealPath(rootFull, 0);
        var real = RealPath(full, 0);
        if (real is null || realRoot is null)
        {
            error = "a symbolic link could not be resolved";
            full = null;
            return false;
        }

        var prefix = realRoot.EndsWith(Path.DirectorySeparatorChar) ? realRoot : realRoot + Path.DirectorySeparatorChar;
        if (!real.StartsWith(prefix, StringComparison.Ordinal))
        {
            error = "it resolves outside the pack folder";
            full = null;
            return false;
        }

        error = null;
        return true;
    }

    /// <summary>The path with every symbolic link along it resolved (missing trailing parts are kept as they are).</summary>
    private static string? RealPath(string fullPath, int depth)
    {
        if (depth > 32)
            return null;
        var rootPart = Path.GetPathRoot(fullPath) ?? "";
        var parts = fullPath[rootPart.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        var current = rootPart;
        foreach (var part in parts)
        {
            current = Path.Combine(current, part);
            FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (!info.Exists || info.LinkTarget is null)
                continue;
            var target = info.ResolveLinkTarget(returnFinalTarget: true);
            if (target is null)
                return null;
            var resolved = RealPath(Path.GetFullPath(target.FullName), depth + 1);
            if (resolved is null)
                return null;
            current = resolved;
        }

        return current;
    }
}
