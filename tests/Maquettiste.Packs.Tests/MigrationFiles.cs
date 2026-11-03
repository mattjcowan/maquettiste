using System.Text.RegularExpressions;
using Maquettiste.Engine;
using Maquettiste.Engine.Pipeline;

namespace Maquettiste.Packs.Tests;

/// <summary>
/// The migrations sql-ddl writes (next-phase-1-design.md section 3.2): <c>&lt;database&gt;/migrations/&lt;NNNN&gt;-&lt;key&gt;.sql</c>
/// from the committed snapshot, where <c>NNNN</c> is the schema revision the migration reaches and <c>key</c> the first 12 hex digits
/// of its content hash, and <c>&lt;key&gt;.sql</c> from a baseline model. Tests find a migration by database and revision, or as the
/// file a run added.
/// </summary>
internal static partial class MigrationFiles
{
    /// <summary>The repo-relative path of the migration a run added for a database (the output root <c>db</c>).</summary>
    /// <param name="result">The run's result.</param>
    /// <param name="database">The database's folder name, such as <c>main</c>.</param>
    /// <returns>The path.</returns>
    public static string Added(GenerationResult result, string database)
    {
        var prefix = $"db/{database}/migrations/";
        var added = result.Changes.Where(c => c.Kind == FileChangeKind.Added && c.Path.StartsWith(prefix, StringComparison.Ordinal)).ToList();
        Assert.True(added.Count == 1, $"expected one migration added for {database}, found: {string.Join(", ", added.Select(c => c.Path))}");
        return added[0].Path;
    }

    /// <summary>The full path of a database's migration at a revision, in an output folder that holds <c>migrations/</c>.</summary>
    /// <param name="folder">The database's output folder (absolute).</param>
    /// <param name="revision">The schema revision the migration reaches.</param>
    /// <returns>The path.</returns>
    public static string In(string folder, int revision)
    {
        var directory = Path.Combine(folder, "migrations");
        var prefix = revision.ToString("0000", System.Globalization.CultureInfo.InvariantCulture) + "-";
        var found = Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory, prefix + "*.sql").Where(f => Named().IsMatch(Path.GetFileName(f))).Order(StringComparer.Ordinal).ToList()
            : [];
        Assert.True(found.Count == 1, $"expected one migration {prefix}<key>.sql in {directory}, found {found.Count}");
        return found[0];
    }

    /// <summary>The repo-relative path of a database's migration at a revision.</summary>
    /// <param name="repo">The repo.</param>
    /// <param name="database">The database's folder name, such as <c>main</c>.</param>
    /// <param name="revision">The schema revision the migration reaches.</param>
    /// <returns>The path.</returns>
    public static string Of(PackRepo repo, string database, int revision) =>
        $"db/{database}/migrations/" + Path.GetFileName(In(repo.PathOf($"db/{database}"), revision));

    /// <summary>The text of a database's migration at a revision.</summary>
    /// <param name="repo">The repo.</param>
    /// <param name="database">The database's folder name, such as <c>main</c>.</param>
    /// <param name="revision">The schema revision the migration reaches.</param>
    /// <returns>The text.</returns>
    public static string Read(PackRepo repo, string database, int revision) => File.ReadAllText(In(repo.PathOf($"db/{database}"), revision));

    /// <summary>Whether a database has a migration at a revision.</summary>
    /// <param name="repo">The repo.</param>
    /// <param name="database">The database's folder name.</param>
    /// <param name="revision">The schema revision.</param>
    /// <returns>Whether one exists.</returns>
    public static bool Exists(PackRepo repo, string database, int revision)
    {
        var directory = repo.PathOf($"db/{database}/migrations");
        var prefix = revision.ToString("0000", System.Globalization.CultureInfo.InvariantCulture) + "-";
        return Directory.Exists(directory) && Directory.EnumerateFiles(directory, prefix + "*.sql").Any(f => Named().IsMatch(Path.GetFileName(f)));
    }

    /// <summary>A migration's name from the committed snapshot: the revision, then the content key.</summary>
    [GeneratedRegex("^[0-9]{4}-[0-9a-f]{12}\\.sql$")]
    public static partial Regex Named();

    /// <summary>A migration's name from a baseline model: the content key alone.</summary>
    [GeneratedRegex("^[0-9a-f]{12}\\.sql$")]
    public static partial Regex Keyed();
}
