using System.Text;
using System.Text.RegularExpressions;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Hashing;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Rendering;

namespace Maquettiste.Engine.Planning;

/// <summary>
/// The one set of rules for what a pack file is (GU2), shared by <see cref="PackLoader"/> and the editor's pack read: scripts
/// (<c>scripts</c>, or every <c>*.js</c> when the list is empty), type maps (<c>types/*.json</c>), the units' templates and
/// companions, literal <c>include</c>s (partials), and the unit-level parse pass: MQ6003 for a template or partial a unit reaches,
/// MQ6025 for a file no unit reaches that does not parse.
/// </summary>
internal static partial class PackFileRoles
{
    private const string ParseError = "MQ6003";
    private const string Unreached = "MQ6025";
    private static readonly UTF8Encoding StrictUtf8 = new(false, throwOnInvalidBytes: true);

    [GeneratedRegex("""include\s+["']([^"'\r\n]+)["']""", RegexOptions.CultureInvariant)]
    private static partial Regex IncludePattern();

    /// <summary>The folder's files, pack-relative, hidden segments left out, ordinal.</summary>
    public static IReadOnlyList<string> EnumerateFiles(string root) =>
        !Directory.Exists(root)
            ? []
            : [.. Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(root, f).Replace(Path.DirectorySeparatorChar, '/'))
                .Where(p => !p.Split('/').Any(s => s.StartsWith('.')))
                .Order(StringComparer.Ordinal)];

    /// <summary>The pack's scripts: its <c>scripts</c> list, or every <c>*.js</c> file when the list is empty.</summary>
    /// <param name="paths">The pack's files (<see cref="EnumerateFiles"/>).</param>
    /// <param name="manifest">The manifest, when it parses.</param>
    /// <returns>Pack-relative script paths, in load order.</returns>
    public static IReadOnlyList<string> Scripts(IReadOnlyList<string> paths, PackManifest? manifest) =>
        manifest is { Scripts.Count: > 0 } ? manifest.Scripts : [.. paths.Where(p => p.EndsWith(".js", StringComparison.Ordinal))];

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
        foreach (var unit in manifest?.Units ?? [])
        {
            if (used.TryGetValue(unit.Template, out var users))
                users.Add("unit:" + unit.Id);
            named.Add(unit.Template);
            if (unit.Companion is { } companion)
            {
                named.Add(companion.Template);
                if (used.TryGetValue(companion.Template, out var companionUsers))
                    companionUsers.Add("companion:" + unit.Id);
            }
        }

        var scripts = new HashSet<string>(Scripts(paths, manifest), StringComparer.Ordinal);
        var included = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (path, text) in texts)
        {
            if (scripts.Contains(path) || path == "pack.json" || IsTypeMap(path))
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
            p == "pack.json" ? "manifest" : scripts.Contains(p) ? "script" : IsTypeMap(p) ? "type-map" : named.Contains(p) ? "template"
            : included.Contains(p) ? "partial" : "other", [.. used[p]]))];
    }

    /// <summary>A type map: <c>types/&lt;target&gt;.json</c> directly in the pack's <c>types</c> folder.</summary>
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
    /// that unit. A file no unit reaches (<paramref name="reached"/> false) that fails with the default delimiters is MQ6025.
    /// </summary>
    public static List<Diagnostic> Parse(string relative, string path, string text, PackManifest manifest, bool reached)
    {
        var result = new List<Diagnostic>();
        if (path.EndsWith(".js", StringComparison.Ordinal) || path.EndsWith(".json", StringComparison.Ordinal) || path.EndsWith(".md", StringComparison.Ordinal))
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
