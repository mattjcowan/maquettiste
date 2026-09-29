using System.Text;
using Maquettiste.Engine.Model;

namespace Maquettiste.Engine.Loading;

/// <summary>What a file under the model root is to the loader.</summary>
internal enum ModelFileKind
{
    /// <summary><c>maquettiste.json</c>.</summary>
    Settings,

    /// <summary><c>model/**/*.json</c>.</summary>
    Element,

    /// <summary><c>extensions/*.json</c>.</summary>
    Extension,

    /// <summary><c>extensions/rules/*.js</c>.</summary>
    RuleScript,

    /// <summary><c>model/locales/**/*.json</c>: a locale shard, which is not an element.</summary>
    LocaleShard,

    /// <summary>A description sidecar referenced by an element (<c>"description": { "file": … }</c>).</summary>
    Sidecar,
}

/// <summary>
/// Path conventions of the model folder (engine-design.md section 2.2): model-relative paths (<c>model/entities/invoice.json</c>)
/// are the loader's keys; repo-relative paths (<c>.maquettiste/model/entities/invoice.json</c>) appear in documents and diagnostics.
/// Also the file-name policy: kebab-case name, <c>-&lt;last 6 of the id&gt;</c> only on a collision.
/// </summary>
internal sealed class ModelPaths
{
    /// <summary>The name of the settings file.</summary>
    public const string SettingsFile = "maquettiste.json";

    /// <summary>Creates the conventions for a set of options.</summary>
    /// <param name="options">The options.</param>
    public ModelPaths(EngineOptions options)
    {
        RepoRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.RepoRoot));
        ModelRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.EffectiveModelRoot));
        var relative = Path.GetRelativePath(RepoRoot, ModelRoot).Replace('\\', '/');
        Prefix = relative == "." ? "" : relative.StartsWith("../", StringComparison.Ordinal) || relative == ".." || Path.IsPathRooted(relative) ? ".maquettiste" : relative;
    }

    /// <summary>The absolute repo root.</summary>
    public string RepoRoot { get; }

    /// <summary>The absolute model root.</summary>
    public string ModelRoot { get; }

    /// <summary>The repo-relative path of the model root (<c>.maquettiste</c>); empty when the model root is the repo root.</summary>
    public string Prefix { get; }

    /// <summary>Converts a model-relative path to a repo-relative one.</summary>
    /// <param name="modelPath">The model-relative path.</param>
    /// <returns>The repo-relative path.</returns>
    public string ToRepoPath(string modelPath) => Prefix.Length == 0 ? modelPath : Prefix + "/" + modelPath;

    /// <summary>Converts a repo-relative document path to a model-relative one.</summary>
    /// <param name="repoPath">The repo-relative path.</param>
    /// <returns>The model-relative path.</returns>
    public string FromRepoPath(string repoPath) =>
        Prefix.Length > 0 && repoPath.StartsWith(Prefix + "/", StringComparison.Ordinal) ? repoPath[(Prefix.Length + 1)..] : repoPath;

    /// <summary>Returns the absolute path of a model-relative path.</summary>
    /// <param name="modelPath">The model-relative path.</param>
    /// <returns>The absolute path.</returns>
    public string FullPath(string modelPath) =>
        modelPath.Length == 0 ? ModelRoot : Path.Combine(ModelRoot, modelPath.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>
    /// Normalizes a path reported by a watcher or a caller: absolute (under the model root or the repo root), repo-relative
    /// (<c>.maquettiste/…</c>) or model-relative (<c>model/…</c>). Returns <see langword="null"/> for a path outside the model root
    /// or one with <c>.</c> or <c>..</c> segments; <c>""</c> is the model root itself.
    /// </summary>
    /// <param name="path">The path.</param>
    /// <returns>The model-relative path.</returns>
    public string? ToModelPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;
        string relative;
        if (Path.IsPathRooted(path))
        {
            var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            if (IsUnder(full, ModelRoot, out var underModel))
                return underModel;
            if (!IsUnder(full, RepoRoot, out var underRepo))
                return null;
            relative = underRepo;
        }
        else
        {
            relative = path.Replace('\\', '/').TrimEnd('/');
            while (relative.StartsWith("./", StringComparison.Ordinal))
                relative = relative[2..];
        }

        if (Prefix.Length > 0)
        {
            if (relative == Prefix)
                return "";
            if (relative.StartsWith(Prefix + "/", StringComparison.Ordinal))
                relative = relative[(Prefix.Length + 1)..];
        }

        return relative.Split('/').Any(s => s.Length == 0 || s == "." || s == "..") ? null : relative;
    }

    /// <summary>Classifies a model-relative path; sidecars are classified by reference, not by path.</summary>
    /// <param name="modelPath">The model-relative path.</param>
    /// <returns>The kind, or <see langword="null"/> when the loader ignores the file.</returns>
    public static ModelFileKind? Classify(string modelPath)
    {
        if (modelPath == SettingsFile)
            return ModelFileKind.Settings;
        var segments = modelPath.Split('/');
        if (segments.Any(s => s.Length == 0 || s[0] == '.'))
            return null; // hidden files and folders, including the engine's own temp files
        if (segments.Length >= 4 && segments[0] == "model" && segments[1] == "locales" && modelPath.EndsWith(".json", StringComparison.Ordinal))
            return ModelFileKind.LocaleShard;
        if (segments.Length >= 2 && segments[0] == "model" && modelPath.EndsWith(".json", StringComparison.Ordinal))
            return ModelFileKind.Element;
        if (segments.Length == 2 && segments[0] == "extensions" && modelPath.EndsWith(".json", StringComparison.Ordinal))
            return ModelFileKind.Extension;
        if (segments.Length == 3 && segments[0] == "extensions" && segments[1] == "rules" && modelPath.EndsWith(".js", StringComparison.Ordinal))
            return ModelFileKind.RuleScript;
        return null;
    }

    /// <summary>The folder part of a model-relative path (<c>""</c> for a top-level file).</summary>
    /// <param name="modelPath">The path.</param>
    /// <returns>The folder.</returns>
    public static string FolderOf(string modelPath)
    {
        var slash = modelPath.LastIndexOf('/');
        return slash < 0 ? "" : modelPath[..slash];
    }

    /// <summary>The file name part of a model-relative path.</summary>
    /// <param name="modelPath">The path.</param>
    /// <returns>The file name.</returns>
    public static string FileNameOf(string modelPath) => modelPath[(modelPath.LastIndexOf('/') + 1)..];

    /// <summary>Joins a folder and a name.</summary>
    /// <param name="folder">The folder, possibly empty.</param>
    /// <param name="name">The name.</param>
    /// <returns>The path.</returns>
    public static string Join(string folder, string name) => folder.Length == 0 ? name : folder + "/" + name;

    /// <summary>
    /// Resolves a sidecar reference (<c>"description": { "file": "invoice.md" }</c>) relative to the element's folder. Returns
    /// <see langword="null"/> for an absolute reference or one that leaves the model root.
    /// </summary>
    /// <param name="elementModelPath">The element file's model-relative path.</param>
    /// <param name="file">The reference.</param>
    /// <returns>The sidecar's model-relative path.</returns>
    public static string? ResolveSidecar(string elementModelPath, string file)
    {
        if (file.Length == 0 || Path.IsPathRooted(file) || file.StartsWith('/') || file.Contains('\\', StringComparison.Ordinal))
            return null;
        var parts = new List<string>(FolderOf(elementModelPath).Split('/', StringSplitOptions.RemoveEmptyEntries));
        foreach (var segment in file.Split('/'))
        {
            switch (segment)
            {
                case "" or ".":
                    continue;
                case "..":
                    if (parts.Count == 0)
                        return null;
                    parts.RemoveAt(parts.Count - 1);
                    continue;
                default:
                    parts.Add(segment);
                    break;
            }
        }

        return parts.Count == 0 ? null : string.Join('/', parts);
    }

    /// <summary>The file stem of an element: its kebab-case name, or its lowercase id when the name has no letters or digits.</summary>
    /// <param name="element">The element.</param>
    /// <returns>The stem.</returns>
    public static string Stem(Element element)
    {
        var kebab = Kebab(element.Name);
        return kebab.Length > 0 ? kebab : element.Id.ToLowerInvariant();
    }

    /// <summary>The collision suffix of an id: <c>-</c> and its last six characters, lowercase.</summary>
    /// <param name="id">The id.</param>
    /// <returns>The suffix.</returns>
    public static string Suffix(string id) => "-" + (id.Length >= 6 ? id[^6..] : id).ToLowerInvariant();

    /// <summary>
    /// The file name the element should have: the kind's fixed name (a domain's tag vocabulary or category tree prefixes it with its
    /// own stem and a hyphen), else <c>&lt;stem&gt;.json</c>; with <paramref name="suffixed"/>,
    /// the collision form <c>&lt;stem&gt;-&lt;id6&gt;.json</c>.
    /// </summary>
    /// <param name="element">The element.</param>
    /// <param name="suffixed">Whether to add the collision suffix.</param>
    /// <returns>The file name.</returns>
    public static string FileName(Element element, bool suffixed)
    {
        if (element.Kind == ElementKind.Database)
            return "database.json"; // a database collides at the folder level (DatabaseFolderName)
        var stem = KindInfo.Get(element.Kind).FixedFileName is { } fixedName ? fixedName[..^".json".Length] : Stem(element);
        if (element is TagVocabulary { Package: not null } or CategoryTree { Package: not null })
            stem = Stem(element) + "-" + stem; // a domain's vocabulary (explorer-redesign.md section 1.11): <name>-tags.json, <name>-categories.json
        return stem + (suffixed ? Suffix(element.Id) : "") + ".json";
    }

    /// <summary>The folder that holds every database folder.</summary>
    public const string DatabasesFolder = "model/databases";

    /// <summary>
    /// The conventional folder of an element (engine-design.md section 2.2). A database's folder is <c>model/databases/&lt;stem&gt;</c>
    /// (a collision adds the id suffix, see <see cref="DatabaseFolder"/>); tables, views and sequences live in their database's
    /// actual folder, found through <paramref name="databaseFolder"/>; a seed lives in <c>model/seeds/&lt;target stem&gt;</c>.
    /// </summary>
    /// <param name="element">The element.</param>
    /// <param name="databaseFolder">Returns the model-relative folder of a database id, or <see langword="null"/> when unknown.</param>
    /// <param name="targetStem">Returns the file stem of a seed's target, or <see langword="null"/> when unknown (the lowercase id is used).</param>
    /// <returns>The model-relative folder.</returns>
    public static string ConventionalFolder(Element element, Func<string, string?> databaseFolder, Func<string, string?>? targetStem = null)
    {
        string Under(string databaseId, string child) =>
            (databaseFolder(databaseId) ?? DatabaseFolder(databaseId.ToLowerInvariant(), null)) + "/" + child;

        return element switch
        {
            Database d => DatabaseFolder(Stem(d), null),
            Table t => Under(t.Database, "tables"),
            View v => Under(v.Database, "views"),
            Sequence s => Under(s.Database, "sequences"),
            Seed seed => KindInfo.SeedsFolder + "/" + (targetStem?.Invoke(seed.Target) ?? seed.Target.ToLowerInvariant()),
            _ => KindInfo.Get(element.Kind).Folder,
        };
    }

    /// <summary>A database folder: <c>model/databases/&lt;stem&gt;</c>, with the collision suffix of <paramref name="suffixId"/> when given.</summary>
    /// <param name="stem">The database's stem.</param>
    /// <param name="suffixId">The id whose suffix to add, or <see langword="null"/>.</param>
    /// <returns>The model-relative folder.</returns>
    public static string DatabaseFolder(string stem, string? suffixId) => DatabasesFolder + "/" + stem + (suffixId is null ? "" : Suffix(suffixId));

    /// <summary>
    /// Whether a model-relative path is one the file-name policy could have produced for the element in <paramref name="folder"/>:
    /// the plain name or the collision form. A database's folder name is checked the same way.
    /// </summary>
    /// <param name="element">The element.</param>
    /// <param name="modelPath">The file's path.</param>
    /// <param name="folder">The conventional folder.</param>
    /// <returns><see langword="true"/> when the folder and the name match.</returns>
    public static bool MatchesConvention(Element element, string modelPath, string folder)
    {
        if (element.Kind == ElementKind.Database)
        {
            return FileNameOf(modelPath) == "database.json"
                && (FolderOf(modelPath) == folder || FolderOf(modelPath) == DatabaseFolder(Stem(element), element.Id));
        }

        var name = FileNameOf(modelPath);
        var inFolder = FolderOf(modelPath) == folder
            || (element is Seed seed && FolderOf(modelPath) == folder + Suffix(seed.Target)); // a target folder suffixed on a collision
        return inFolder && (name == FileName(element, false) || name == FileName(element, true));
    }

    /// <summary>
    /// Kebab-cases a name for file names: <c>InvoiceLine</c> → <c>invoice-line</c>, <c>HTTPServer</c> → <c>http-server</c>,
    /// <c>is member of</c> → <c>is-member-of</c>. Only ASCII letters and digits survive; everything else separates words.
    /// </summary>
    /// <param name="name">The name.</param>
    /// <returns>The kebab-case name.</returns>
    public static string Kebab(string name)
    {
        var sb = new StringBuilder(name.Length + 4);
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (!char.IsAsciiLetterOrDigit(c))
            {
                if (sb.Length > 0 && sb[^1] != '-')
                    sb.Append('-');
                continue;
            }

            var boundary = i > 0 && char.IsAsciiLetterUpper(c)
                && (char.IsAsciiLetterLower(name[i - 1]) || char.IsAsciiDigit(name[i - 1])
                    || (i + 1 < name.Length && char.IsAsciiLetterUpper(name[i - 1]) && char.IsAsciiLetterLower(name[i + 1])));
            if (boundary && sb.Length > 0 && sb[^1] != '-')
                sb.Append('-');
            sb.Append(char.ToLowerInvariant(c));
        }

        return sb.ToString().Trim('-');
    }

    private static bool IsUnder(string full, string root, out string relative)
    {
        relative = "";
        if (string.Equals(full, root, StringComparison.Ordinal))
            return true;
        var withSeparator = root + Path.DirectorySeparatorChar;
        if (!full.StartsWith(withSeparator, StringComparison.Ordinal))
            return false;
        relative = full[withSeparator.Length..].Replace('\\', '/');
        return true;
    }
}
