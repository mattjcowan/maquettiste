using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Text;
using System.Text.RegularExpressions;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Pipeline;

namespace Maquettiste.Engine.Writing;

/// <summary>
/// The output path allowlist and the guard for every engine write (W7; engine-design.md section 12.1; SPEC sections 4 and 19).
/// Construction does no I/O; <see cref="Check"/> and <see cref="CheckEngineWrite"/> read the file system to resolve symbolic links.
/// </summary>
internal sealed class OutputPathPolicy : IOutputPathPolicy
{
    /// <summary>The rule of every refusal made here.</summary>
    public const string RefusedRule = "MQ6004";

    private static readonly FrozenSet<string> ReservedNames = new[]
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private readonly string _repoRoot;
    private readonly string _modelRoot;
    private readonly string _cacheDirectory;
    private readonly string _journalDirectory;
    private readonly ImmutableArray<string> _setupPaths;
    private readonly bool _hasSettings;
    private readonly ImmutableArray<(string Path, OutputRootInfo Info)> _roots;
    private readonly ImmutableArray<(string Pattern, Regex Regex)> _deny;
    private readonly ConcurrentDictionary<string, string> _realFolders = new(StringComparer.Ordinal);

    /// <summary>Creates the policy.</summary>
    /// <param name="options">The engine options.</param>
    /// <param name="settings">The project settings; <see langword="null"/> allows engine-write checks only.</param>
    public OutputPathPolicy(EngineOptions options, ProjectSettings? settings)
    {
        ArgumentNullException.ThrowIfNull(options);
        _repoRoot = Full(options.RepoRoot);
        _modelRoot = Full(options.EffectiveModelRoot);
        _cacheDirectory = Full(options.CacheDirectory);
        _journalDirectory = Full(options.EffectiveJournalDirectory);
        _setupPaths =
        [
            Path.Combine(_repoRoot, ".gitignore"),
            Path.Combine(_repoRoot, ".git", "hooks", "post-checkout"),
            Path.Combine(_repoRoot, ".git", "hooks", "post-merge"),
            Path.Combine(_repoRoot, ".mcp.json"),
            Path.Combine(_repoRoot, "mcp.sh"),
            Path.Combine(_repoRoot, ".claude", "skills", "maquettiste-modeling", "SKILL.md"),
        ];
        _hasSettings = settings is not null;
        var roots = new List<(string, OutputRootInfo)>();
        var deny = new List<(string, Regex)>();
        if (settings is not null)
        {
            foreach (var root in settings.Outputs.Allow)
            {
                // An allow path that fails the lexical rules is ignored: nothing can be written under it.
                if (NormalizeRoot(root.Path) is { } normalized)
                    roots.Add((normalized, new OutputRootInfo(normalized, root.Commit)));
            }

            foreach (var pattern in settings.Outputs.Deny)
            {
                if (!string.IsNullOrWhiteSpace(pattern))
                    deny.Add((pattern, GlobToRegex(pattern)));
            }
        }

        // Longest root first, so the first match is the longest one (D17); ties keep the settings order.
        _roots = [.. roots.Select((r, i) => (r, i)).OrderByDescending(x => x.r.Item1.Length).ThenBy(x => x.i).Select(x => x.r)];
        _deny = [.. deny];
    }

    /// <inheritdoc/>
    public PathCheck Check(string repoRelativePath)
    {
        if (Classify(repoRelativePath, out var root) is { } refused)
            return refused;
        var path = repoRelativePath;
        if (SymlinkEscape(root!, path) is { } escape)
            return Refuse(path, root, escape);
        return new PathCheck(true, path, root, null, null);
    }

    /// <summary>
    /// The output root of a repo-relative path by the rules of <see cref="Check"/> that need no file system access (lexical rules,
    /// <c>outputs.allow</c>, <c>outputs.deny</c>), or <see langword="null"/> when one of them refuses the path. A path with a root
    /// here can still be refused by <see cref="Check"/>, whose symbolic link check reads the disk.
    /// </summary>
    /// <param name="repoRelativePath">The path.</param>
    /// <returns>The root, or <see langword="null"/>.</returns>
    internal OutputRootInfo? LexicalRoot(string repoRelativePath) => Classify(repoRelativePath, out var root) is null ? root : null;

    /// <summary>Runs the checks of <see cref="Check"/> that need no file system access; returns the refusal, or <see langword="null"/>.</summary>
    private PathCheck? Classify(string repoRelativePath, out OutputRootInfo? root)
    {
        root = null;
        if (LexicalError(repoRelativePath) is { } lexical)
            return Refuse(repoRelativePath ?? "", null, lexical);
        var path = repoRelativePath;
        if (!_hasSettings)
            return Refuse(path, null, "no output roots are configured for this check (engine writes only)");

        foreach (var (rootPath, info) in _roots)
        {
            if (rootPath.Length == 0 || (path.Length > rootPath.Length && path[rootPath.Length] == '/' && path.StartsWith(rootPath, StringComparison.Ordinal)))
            {
                root = info;
                break;
            }
        }

        if (root is null)
            return Refuse(path, null, "the path is not under an outputs.allow root");

        foreach (var (pattern, regex) in _deny)
        {
            if (MatchesPathOrAncestor(regex, path))
                return Refuse(path, root, $"the path matches the outputs.deny rule '{pattern}'");
        }

        return null;
    }

    /// <inheritdoc/>
    public PathCheck CheckEngineWrite(WriteTarget target, string fullPath)
    {
        if (string.IsNullOrEmpty(fullPath) || !Path.IsPathFullyQualified(fullPath))
            return Refuse(fullPath ?? "", null, "an engine write needs an absolute path");
        if (fullPath.Contains('\0', StringComparison.Ordinal))
            return Refuse(fullPath, null, "the path contains a NUL character");
        var full = Full(fullPath);
        switch (target)
        {
            case WriteTarget.Output:
            {
                if (!FileSystemPaths.IsUnder(full, _repoRoot, allowEqual: false))
                    return Refuse(full, null, "an output must lie under the repo root");
                var relative = Path.GetRelativePath(_repoRoot, full).Replace(Path.DirectorySeparatorChar, '/');
                return Check(relative);
            }

            case WriteTarget.Model:
                return CheckUnder(full, [_modelRoot], "a model write must lie under the model root");
            case WriteTarget.Cache:
                return CheckUnder(full, [_cacheDirectory, _journalDirectory], "a cache write must lie under the cache or journal folder");
            case WriteTarget.Setup:
            {
                if (!_setupPaths.Any(p => string.Equals(p, full, FileSystemPaths.Comparison)))
                    return Refuse(full, null, "setup writes are limited to .gitignore, the post-checkout and post-merge hooks, .mcp.json, mcp.sh and .claude/skills/maquettiste-modeling/SKILL.md");
                try
                {
                    var realRepo = FileSystemPaths.RealPath(_repoRoot);
                    return FileSystemPaths.IsUnder(FileSystemPaths.RealPath(full), realRepo, allowEqual: false)
                        ? new PathCheck(true, full, null, null, null)
                        : Refuse(full, null, "a symbolic link takes the setup write outside the repo");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // A link cycle or an unreadable link is refused, never thrown.
                    return Refuse(full, null, ex.Message);
                }
            }

            default:
                return Refuse(full, null, $"unknown write target {target}");
        }
    }

    /// <summary>Returns why a repo-relative output path is lexically invalid, or <see langword="null"/>.</summary>
    /// <param name="path">The path.</param>
    /// <returns>The reason.</returns>
    internal static string? LexicalError(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return "the path is empty";
        if (path.Contains('\\', StringComparison.Ordinal))
            return "the path must use '/' separators";
        if (path[0] == '/')
            return "the path must be relative";
        if (path.Length >= 2 && char.IsAsciiLetter(path[0]) && path[1] == ':')
            return "the path must not name a drive";
        foreach (var c in path)
        {
            if (c < 0x20 || c == 0x7F)
                return "the path contains a control character";
            if (c is '<' or '>' or ':' or '"' or '|' or '?' or '*')
                return $"the path contains the character '{c}', which is invalid on Windows";
        }

        // Segment by segment without allocating (every output path and manifest entry comes through here).
        var reserved = ReservedNames.GetAlternateLookup<ReadOnlySpan<char>>();
        var rest = path.AsSpan();
        while (true)
        {
            var slash = rest.IndexOf('/');
            var segment = slash < 0 ? rest : rest[..slash];
            if (segment.Length == 0)
                return "the path has an empty segment";
            if (segment is "." or "..")
                return "the path must not contain '.' or '..' segments";
            if (segment[^1] is '.' or ' ')
                return $"the segment '{segment}' ends with '.' or a space";
            var dot = segment.IndexOf('.');
            var stem = (dot < 0 ? segment : segment[..dot]).TrimEnd(' ');
            if (reserved.Contains(stem))
                return $"the segment '{segment}' is a reserved device name";
            if (segment.Equals(".git", StringComparison.OrdinalIgnoreCase) || segment.Equals(".maquettiste", StringComparison.OrdinalIgnoreCase))
                return $"'{segment}' is always denied";
            if (slash < 0)
                return null;
            rest = rest[(slash + 1)..];
        }
    }

    /// <summary>Converts a deny glob to an anchored regular expression: <c>**</c> spans folders, <c>*</c> and <c>?</c> stay in one segment.</summary>
    /// <param name="pattern">The glob; a leading <c>/</c> is ignored and a trailing <c>/</c> means the folder and everything under it.</param>
    /// <returns>The expression.</returns>
    internal static Regex GlobToRegex(string pattern)
    {
        var glob = pattern.Replace('\\', '/').TrimStart('/');
        var folder = glob.EndsWith('/');
        glob = glob.TrimEnd('/');
        var sb = new StringBuilder("^");
        for (var i = 0; i < glob.Length; i++)
        {
            var c = glob[i];
            if (c == '*' && i + 1 < glob.Length && glob[i + 1] == '*')
            {
                var atStart = i == 0 || glob[i - 1] == '/';
                var slashAfter = i + 2 < glob.Length && glob[i + 2] == '/';
                if (atStart && slashAfter)
                {
                    sb.Append("(?:.*/)?");
                    i += 2;
                }
                else
                {
                    sb.Append(".*");
                    i += 1;
                }
            }
            else if (c == '*')
            {
                sb.Append("[^/]*");
            }
            else if (c == '?')
            {
                sb.Append("[^/]");
            }
            else
            {
                sb.Append(Regex.Escape(c.ToString()));
            }
        }

        sb.Append(folder ? "(?:/.*)?$" : "$");
        return new Regex(sb.ToString(), RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Singleline, TimeSpan.FromSeconds(1));
    }

    private static bool MatchesPathOrAncestor(Regex regex, string path)
    {
        if (regex.IsMatch(path))
            return true;
        for (var i = path.IndexOf('/', StringComparison.Ordinal); i > 0; i = path.IndexOf('/', i + 1))
        {
            if (regex.IsMatch(path[..i]))
                return true;
        }

        return false;
    }

    private static string? NormalizeRoot(string? rootPath)
    {
        if (rootPath is null)
            return null;
        var trimmed = rootPath.Trim().TrimEnd('/');
        if (trimmed is "" or ".")
            return "";
        if (trimmed.StartsWith("./", StringComparison.Ordinal))
            trimmed = trimmed[2..];
        return LexicalError(trimmed) is null ? trimmed : null;
    }

    private static string Full(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static PathCheck Refuse(string path, OutputRootInfo? root, string reason) => new(false, path, root, RefusedRule, reason);

    private static PathCheck CheckUnder(string full, IReadOnlyList<string> bases, string reason)
    {
        foreach (var folder in bases)
        {
            if (!FileSystemPaths.IsUnder(full, folder, allowEqual: false))
                continue;
            try
            {
                var realBase = FileSystemPaths.RealPath(folder);
                if (FileSystemPaths.IsUnder(FileSystemPaths.RealPath(full), realBase, allowEqual: false))
                    return new PathCheck(true, full, null, null, null);
                return Refuse(full, null, "a symbolic link takes the write outside its folder");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A link cycle or an unreadable link is refused, never thrown.
                return Refuse(full, null, ex.Message);
            }
        }

        return Refuse(full, null, reason);
    }

    private string? SymlinkEscape(OutputRootInfo root, string path)
    {
        try
        {
            var realRepo = FileSystemPaths.RealPath(_repoRoot, _realFolders);
            var rootFull = root.Path.Length == 0 ? _repoRoot : Path.Combine(_repoRoot, root.Path.Replace('/', Path.DirectorySeparatorChar));
            var realRoot = FileSystemPaths.RealPath(rootFull, _realFolders);
            if (!FileSystemPaths.IsUnder(realRoot, realRepo, allowEqual: true))
                return $"a symbolic link takes the output root '{root.Path}' outside the repo";
            var target = Path.Combine(_repoRoot, path.Replace('/', Path.DirectorySeparatorChar));
            var realTarget = FileSystemPaths.RealPath(target, _realFolders);
            return FileSystemPaths.IsUnder(realTarget, realRoot, allowEqual: false)
                ? null
                : "a symbolic link takes the path outside its output root";
        }
        catch (IOException ex)
        {
            return ex.Message;
        }
        catch (UnauthorizedAccessException ex)
        {
            return ex.Message;
        }
    }
}
