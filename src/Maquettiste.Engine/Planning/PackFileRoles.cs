using System.Text;
using System.Text.RegularExpressions;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Hashing;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Rendering;

namespace Maquettiste.Engine.Planning;

/// <summary>
/// The one set of rules for what a pack file is (GU2), shared by <see cref="PackLoader"/> and the editor's pack read: scripts
/// (<c>scripts</c>, or every <c>*.js</c> no copy unit copies when the list is empty), type maps (<c>types/*.json</c> no copy unit
/// copies), the units' templates and companions, literal <c>include</c>s (partials), the files copy units copy (<c>copy-source</c>,
/// never parsed or loaded), and the unit-level parse pass: MQ6003 for a template or partial a unit reaches, MQ6025 for a file no
/// unit reaches that does not parse. The <c>tests/</c> folder is reserved for the pack's tests (next-phase-2-design.md section 5.4):
/// every file under it has the role <c>test</c>, and none is a script, a type map, a template, a partial or a copy source.
/// </summary>
internal static partial class PackFileRoles
{
    /// <summary>The folder of a pack that holds its tests: cases and helper test files (<see cref="IsTest"/>).</summary>
    public const string TestsFolder = "tests";

    private const string ParseError = "MQ6003";
    private const string Unreached = "MQ6025";
    private static readonly UTF8Encoding StrictUtf8 = new(false, throwOnInvalidBytes: true);

    [GeneratedRegex("""include\s+["']([^"'\r\n]+)["']""", RegexOptions.CultureInvariant)]
    private static partial Regex IncludePattern();

    /// <summary>
    /// Whether a pack-relative path lies under the pack's <c>tests/</c> folder, which holds its tests and nothing a run reads
    /// (next-phase-2-design.md section 5.4).
    /// </summary>
    /// <param name="path">The pack-relative path, <c>/</c> separators.</param>
    /// <returns><see langword="true"/> for a path under <c>tests/</c>.</returns>
    public static bool IsTest(string path) => path.StartsWith(TestsFolder + "/", StringComparison.Ordinal);

    /// <summary>The folder's files, pack-relative, hidden segments left out, ordinal.</summary>
    public static IReadOnlyList<string> EnumerateFiles(string root) =>
        !Directory.Exists(root)
            ? []
            : [.. Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(root, f).Replace(Path.DirectorySeparatorChar, '/'))
                .Where(p => !p.Split('/').Any(s => s.StartsWith('.')))
                .Order(StringComparer.Ordinal)];

    /// <summary>
    /// The files the pack's copy units copy: every path a copy unit's well-formed source patterns match. They are copied as they
    /// are, so they are never default scripts or type maps (next-phase-1-design.md section 5.4).
    /// </summary>
    /// <param name="paths">The pack's files (<see cref="EnumerateFiles"/>).</param>
    /// <param name="manifest">The manifest, when it parses.</param>
    /// <returns>The copied paths.</returns>
    public static IReadOnlySet<string> CopiedFiles(IReadOnlyList<string> paths, PackManifest? manifest)
    {
        var copied = new HashSet<string>(StringComparer.Ordinal);
        foreach (var unit in manifest?.Units ?? [])
        {
            if (unit.Mode == OutputMode.Copy)
                copied.UnionWith(CopySources.Matching(paths, unit.Sources));
        }

        return copied;
    }

    /// <summary>
    /// The pack's scripts: its <c>scripts</c> list, or every <c>*.js</c> file outside <c>tests/</c> no copy unit copies when the list is
    /// empty. A file the list names is a script even when a copy unit also copies it (one under <c>tests/</c> fails the load, MQ6001).
    /// </summary>
    /// <param name="paths">The pack's files (<see cref="EnumerateFiles"/>).</param>
    /// <param name="manifest">The manifest, when it parses.</param>
    /// <param name="copied">The files copy units copy (<see cref="CopiedFiles"/>).</param>
    /// <returns>Pack-relative script paths, in load order.</returns>
    public static IReadOnlyList<string> Scripts(IReadOnlyList<string> paths, PackManifest? manifest, IReadOnlySet<string> copied) =>
        manifest is { Scripts.Count: > 0 }
            ? manifest.Scripts
            : [.. paths.Where(p => p.EndsWith(".js", StringComparison.Ordinal) && !copied.Contains(p) && !IsTest(p))];

    /// <summary>The pack's type maps: the <c>types/&lt;target&gt;.json</c> files (<see cref="IsTypeMap"/>) no copy unit copies.</summary>
    /// <param name="paths">The pack's files (<see cref="EnumerateFiles"/>).</param>
    /// <param name="copied">The files copy units copy (<see cref="CopiedFiles"/>).</param>
    /// <returns>Pack-relative type map paths, ordinal.</returns>
    public static IReadOnlyList<string> TypeMaps(IReadOnlyList<string> paths, IReadOnlySet<string> copied) =>
        [.. paths.Where(p => IsTypeMap(p) && !copied.Contains(p))];

    /// <summary>
    /// Adds the cross-pack users of a pack's files (library packs, next-phase-2-design.md section 5.2): every literal
    /// <c>include "&lt;pack&gt;:&lt;path&gt;"</c> in the files of a pack whose <c>uses</c> names this pack is
    /// <c>include:&lt;user pack&gt;:&lt;including file&gt;</c> among the included file's users, which makes an unclaimed file a partial.
    /// Only the packs that use this pack are read.
    /// </summary>
    /// <param name="root">The pack folder (<c>templates/&lt;pack&gt;</c>).</param>
    /// <param name="files">The pack's files as <see cref="DescribeAsync"/> describes them.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The files with the cross-pack users merged in, ordinal.</returns>
    public static async Task<IReadOnlyList<PackFileInfo>> WithCrossPackUsersAsync(string root, IReadOnlyList<PackFileInfo> files, CancellationToken ct)
    {
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(root));
        var templates = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(root));
        if (templates is null || !Directory.Exists(templates))
            return files;
        var prefix = name + ":";
        var added = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        foreach (var folder in Directory.EnumerateDirectories(templates).Order(StringComparer.Ordinal))
        {
            var user = Path.GetFileName(folder);
            var manifest = Path.Combine(folder, "pack.json");
            if (user.StartsWith('.') || string.Equals(user, name, StringComparison.Ordinal) || !File.Exists(manifest)
                || !PackUses.LightUses(await File.ReadAllBytesAsync(manifest, ct).ConfigureAwait(false)).Contains(name, StringComparer.Ordinal))
                continue;
            foreach (var path in EnumerateFiles(folder).Where(p => !IsTest(p)))
            {
                string text;
                try
                {
                    text = StrictUtf8.GetString(await File.ReadAllBytesAsync(Path.Combine(folder, path.Replace('/', Path.DirectorySeparatorChar)), ct).ConfigureAwait(false));
                }
                catch (DecoderFallbackException)
                {
                    continue;
                }

                foreach (Match match in IncludePattern().Matches(text))
                {
                    var target = match.Groups[1].Value;
                    if (!target.StartsWith(prefix, StringComparison.Ordinal))
                        continue;
                    if (!added.TryGetValue(target[prefix.Length..], out var users))
                        added[target[prefix.Length..]] = users = new SortedSet<string>(StringComparer.Ordinal);
                    users.Add("include:" + user + ":" + path);
                }
            }
        }

        if (added.Count == 0)
            return files;
        return [.. files.Select(f => added.TryGetValue(f.Path, out var users)
            ? f with
            {
                Role = f.Role == "other" ? "partial" : f.Role,
                UsedBy = [.. f.UsedBy.Concat(users).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)],
            }
            : f)];
    }

    /// <summary>Roles and users of every file of a pack (GU2: the loader's rules for scripts and type maps, the units' templates,
    /// literal <c>include</c>s).</summary>
    public static async Task<IReadOnlyList<PackFileInfo>> DescribeAsync(string root, PackManifest? manifest, CancellationToken ct)
    {
        var paths = EnumerateFiles(root);
        var texts = new Dictionary<string, string>(StringComparer.Ordinal);
        var hashes = new Dictionary<string, (long Size, string Hash)>(StringComparer.Ordinal);
        foreach (var path in paths)
        {
            var bytes = await File.ReadAllBytesAsync(Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar)), ct).ConfigureAwait(false);
            hashes[path] = (bytes.Length, ContentHash.Of(bytes));
            try
            {
                texts[path] = StrictUtf8.GetString(bytes);
            }
            catch (DecoderFallbackException)
            {
            }
        }

        var used = paths.ToDictionary(p => p, _ => new SortedSet<string>(StringComparer.Ordinal), StringComparer.Ordinal);
        var named = new HashSet<string>(StringComparer.Ordinal);
        // A copy unit names no template: its sources' matches are copy sources, which no rename or delete check counts as used.
        var copied = CopiedFiles(paths, manifest);
        foreach (var unit in manifest?.Units ?? [])
        {
            if (unit.Template is { } template)
            {
                if (used.TryGetValue(template, out var users))
                    users.Add("unit:" + unit.Id);
                named.Add(template);
            }

            if (unit.Companion is { } companion)
            {
                named.Add(companion.Template);
                if (used.TryGetValue(companion.Template, out var companionUsers))
                    companionUsers.Add("companion:" + unit.Id);
            }
        }

        var scripts = new HashSet<string>(Scripts(paths, manifest, copied), StringComparer.Ordinal);
        var typeMaps = new HashSet<string>(TypeMaps(paths, copied), StringComparer.Ordinal);
        var included = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (path, text) in texts)
        {
            // A copy source is copied as it is, so its text is no template and its includes reach nothing.
            if (scripts.Contains(path) || path == "pack.json" || typeMaps.Contains(path) || (copied.Contains(path) && !named.Contains(path)) || IsTest(path))
                continue;
            foreach (Match match in IncludePattern().Matches(text))
            {
                var target = match.Groups[1].Value;
                if (used.TryGetValue(target, out var users) && !string.Equals(target, path, StringComparison.Ordinal))
                {
                    users.Add("include:" + path);
                    included.Add(target);
                }
            }
        }

        return [.. paths.Select(p => new PackFileInfo(p, hashes[p].Size, hashes[p].Hash,
            p == "pack.json" ? "manifest" : IsTest(p) ? "test" : scripts.Contains(p) ? "script" : typeMaps.Contains(p) ? "type-map" : named.Contains(p) ? "template"
            : included.Contains(p) ? "partial" : copied.Contains(p) ? "copy-source" : "other", [.. used[p]]))];
    }

    /// <summary>A type map's path: <c>types/&lt;target&gt;.json</c> directly in the pack's <c>types</c> folder (one no copy unit copies, <see cref="TypeMaps"/>).</summary>
    public static bool IsTypeMap(string path) => path.StartsWith("types/", StringComparison.Ordinal) && path.Count(c => c == '/') == 1
        && path.EndsWith(".json", StringComparison.Ordinal);

    public static async Task<IReadOnlyList<Diagnostic>> ParseAsync(string root, string relative, PackManifest manifest,
        IReadOnlyList<PackFileInfo> files, CancellationToken ct)
    {
        var result = new List<Diagnostic>();
        foreach (var file in files.Where(f => f.Role is "template" or "partial" or "other"))
        {
            byte[] bytes = await File.ReadAllBytesAsync(Path.Combine(root, file.Path.Replace('/', Path.DirectorySeparatorChar)), ct).ConfigureAwait(false);
            string text;
            try
            {
                text = StrictUtf8.GetString(bytes);
            }
            catch (DecoderFallbackException)
            {
                continue;
            }

            result.AddRange(Parse(relative, file.Path, text, manifest, reached: file.Role != "other"));
        }

        return result;
    }

    /// <summary>
    /// MQ6003 for a template or partial: parsed with the delimiters of each unit that names it (the default ones otherwise), reported at
    /// that unit. A file no unit reaches (<paramref name="reached"/> false) that fails with the default delimiters is MQ6025. A file
    /// under <c>tests/</c> is never a template and answers nothing.
    /// </summary>
    public static List<Diagnostic> Parse(string relative, string path, string text, PackManifest manifest, bool reached)
    {
        var result = new List<Diagnostic>();
        if (IsTest(path) || path.EndsWith(".js", StringComparison.Ordinal) || path.EndsWith(".json", StringComparison.Ordinal) || path.EndsWith(".md", StringComparison.Ordinal))
            return result;
        var repoPath = relative + "/" + path;
        var units = manifest.Units.Where(u => string.Equals(u.Template, path, StringComparison.Ordinal)
            || string.Equals(u.Companion?.Template, path, StringComparison.Ordinal)).ToList();
        var variants = units.Count == 0 ? [(null as PackUnit)] : units.Select(u => (PackUnit?)u).ToList();
        foreach (var unit in variants)
        {
            var source = text;
            try
            {
                if (unit?.Delimiters is { } d && DelimiterTranslator.NeedsTranslation(d.Open, d.Close))
                    source = DelimiterTranslator.Translate(text, d.Open, d.Close).Text;
            }
            catch (DelimiterException ex)
            {
                result.Add(RuleCatalog.Create(reached ? ParseError : Unreached, ex.Message, filePath: repoPath));
                continue;
            }

            var template = Scriban.Template.Parse(source, repoPath);
            if (!template.HasErrors)
                continue;
            foreach (var message in template.Messages.Where(m => m.Type == Scriban.Parsing.ParserMessageType.Error))
            {
                var at = unit is null ? "" : $" (unit '{unit.Id}')";
                result.Add(RuleCatalog.Create(reached ? ParseError : Unreached, message.Message + at, filePath: repoPath) with
                {
                    Line = message.Span.Start.Line + 1,
                    Column = message.Span.Start.Column + 1,
                });
            }
        }

        return result;
    }
}
