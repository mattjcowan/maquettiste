using System.Text;
using Maquettiste.Engine;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Pipeline;
using Maquettiste.Engine.Resolution;

namespace Maquettiste.Testing;

/// <summary>What converting a model's projected tables with "Create tables…" changed in its sql-ddl output.</summary>
/// <param name="Materialized">Per database name, the entities stored.</param>
/// <param name="Before">The sql-ddl files before (repo-relative path to text), migrations left out.</param>
/// <param name="After">The same files after.</param>
/// <param name="MigrationsBefore">The migration scripts before.</param>
/// <param name="MigrationsAfter">The migration scripts after.</param>
/// <param name="Errors">The errors validation reports after the conversion.</param>
/// <param name="Projected">The tables the databases still project after the conversion.</param>
public sealed record MaterializeProofResult(IReadOnlyDictionary<string, int> Materialized, IReadOnlyDictionary<string, string> Before,
    IReadOnlyDictionary<string, string> After, IReadOnlyList<string> MigrationsBefore, IReadOnlyList<string> MigrationsAfter, IReadOnlyList<Diagnostic> Errors,
    IReadOnlyList<string> Projected)
{
    /// <summary>The migration facts the conversion recorded other than its rekey facts (a change the database would see).</summary>
    public IReadOnlyList<string> OtherFacts { get; init; } = [];

    /// <summary>
    /// The migrations the run after the conversion wrote (path to text): the rekey fact's ledger rows and the guarded statements that
    /// create what it adopted where it is missing, no other schema statement.
    /// </summary>
    public IReadOnlyDictionary<string, string> RekeyMigrations { get; init; } = new Dictionary<string, string>();

    /// <summary>The migration scripts before the conversion.</summary>
    public IReadOnlyList<string> MigrationsAtStart { get; init; } = [];

    /// <summary>The files whose text differs (or exist on one side only), in path order.</summary>
    public IReadOnlyList<string> Changed =>
        [.. Before.Keys.Union(After.Keys).Order(StringComparer.Ordinal).Where(p => Before.GetValueOrDefault(p) != After.GetValueOrDefault(p))];

    /// <summary>Each changed file with its first differing lines, for an assertion message.</summary>
    public string Describe(int linesPerFile = 6)
    {
        var text = new StringBuilder();
        foreach (var path in Changed)
        {
            text.Append("== ").AppendLine(path);
            var a = (Before.GetValueOrDefault(path) ?? "").Split('\n');
            var b = (After.GetValueOrDefault(path) ?? "").Split('\n');
            var shown = 0;
            for (var i = 0; i < Math.Max(a.Length, b.Length) && shown < linesPerFile; i++)
            {
                var x = i < a.Length ? a[i] : "<none>";
                var y = i < b.Length ? b[i] : "<none>";
                if (x == y)
                    continue;
                text.Append("  - ").AppendLine(x).Append("  + ").AppendLine(y);
                shown++;
            }
        }

        foreach (var error in Errors.Take(20))
            text.Append("error ").Append(error.Rule).Append(' ').AppendLine(error.Message);
        return text.ToString();
    }

    /// <summary>
    /// The lines of a file that differ between before and after, as a set on each side (a line that moved stays equal), for a file
    /// whose only expected change is a few lines.
    /// </summary>
    public (IReadOnlyList<string> Removed, IReadOnlyList<string> Added) LineChanges(string path)
    {
        var a = (Before.GetValueOrDefault(path) ?? "").Split('\n').ToList();
        var b = (After.GetValueOrDefault(path) ?? "").Split('\n').ToList();
        var removed = new List<string>(a);
        var added = new List<string>();
        foreach (var line in b)
        {
            if (!removed.Remove(line))
                added.Add(line);
        }

        return (removed, added);
    }
}

/// <summary>
/// Proves that "Create tables…" (the batch operation <c>materialize-tables</c>) keeps the shape the projection gives: it renders a model's
/// sql-ddl output, stores every projected entity of every database as designed tables with bindings (each hierarchy in its default
/// shape, each list in its default storage), validates, and renders again. The caller compares the outputs.
/// </summary>
public static class MaterializeProof
{
    /// <summary>Runs the proof on a repo whose model has a sql-ddl pack writing under <paramref name="output"/>.</summary>
    /// <param name="options">The repo's engine options.</param>
    /// <param name="output">The sql-ddl pack's output folder, repo-relative (for example <c>db</c>).</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>What changed.</returns>
    public static async Task<MaterializeProofResult> RunAsync(EngineOptions options, string output, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(options);
        var first = await GenerateAsync(options, ct).ConfigureAwait(false);
        var before = Read(options.RepoRoot, output, out var migrationsBefore);

        var materialized = new Dictionary<string, int>(StringComparer.Ordinal);
        var rekeyFacts = new List<string>();
        var otherFacts = new List<string>();
        await using (var store = new ModelStore(options))
        {
            var snapshot = await store.GetSnapshotAsync(ct).ConfigureAwait(false);
            var resolved = await new ModelResolver(options).ResolveAsync(snapshot, null, ct).ConfigureAwait(false);
            var operations = new List<string>();
            foreach (var rdb in resolved.Databases)
            {
                var designedTables = rdb.Tables.Where(t => t.Origin != "synthesized").Select(t => t.Key).ToHashSet(StringComparer.Ordinal);
                var entities = resolved.Entities
                    .Where(e => !e.IsPromoted && e.Mappings.TryGetValue(rdb.Name, out var m) && !designedTables.Contains(m.Table.Key))
                    .Select(e => e.Id)
                    .ToList();
                // A hierarchy is named whole: an abstract base with no table of its own (a table per concrete entity) comes too.
                foreach (var e in resolved.Entities.Where(e => entities.Contains(e.Id)).ToList())
                {
                    for (var b = e.Base; b is not null; b = b.Base)
                    {
                        if (!entities.Contains(b.Id) && !b.Bindings.ContainsKey(rdb.Name))
                            entities.Add(b.Id);
                    }
                }

                if (entities.Count == 0)
                    continue;
                materialized[rdb.Name] = entities.Count;
                operations.Add("{\"op\":\"materialize-tables\",\"database\":\"" + rdb.Id + "\",\"entities\":[" + string.Join(",", entities.Select(e => "\"" + e + "\"")) + "]}");
            }

            // One batch per database: an entity's binding to each database is its own change.
            foreach (var operation in operations)
            {
                var parsed = store.ParseBatch(Encoding.UTF8.GetBytes("{\"operations\":[" + operation + "]}"));
                if (parsed.Batch is null)
                    throw new InvalidOperationException(string.Join("; ", parsed.Diagnostics.Select(d => d.Rule + " " + d.Message)));
                var applied = await store.ApplyBatchAsync(parsed.Batch, ChangeSource.Editor, ct).ConfigureAwait(false);
                if (applied.Outcome != SaveOutcome.Saved)
                {
                    throw new InvalidOperationException("materialize-tables was refused: " + string.Join("; ",
                        applied.Items.SelectMany(i => i.Diagnostics).Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => d.Rule + " " + d.Message).Take(20)));
                }

                // The write records a rekey fact, which adds only ledger rows and guarded adoptions to the scripts; without it, the
                // snapshot's aliases alone must make the next migration empty, and the scripts compare byte for byte.
                foreach (var fact in applied.Facts ?? [])
                {
                    if (fact.Fact == "rekey")
                        rekeyFacts.Add(fact.Path);
                    else
                        otherFacts.Add(fact.Fact + " " + fact.Summary);
                }
            }
        }

        IReadOnlyList<Diagnostic> errors;
        IReadOnlyList<string> projected;
        await using (var store = new ModelStore(options))
        {
            var validation = await store.ValidateAsync(ValidationScope.All, ct).ConfigureAwait(false);
            errors = [.. validation.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)];
            var snapshot = await store.GetSnapshotAsync(ct).ConfigureAwait(false);
            var resolved = await new ModelResolver(options).ResolveAsync(snapshot, null, ct).ConfigureAwait(false);
            projected = [.. resolved.Databases.SelectMany(d => d.Tables.Where(t => t.Origin == "synthesized").Select(t => d.Name + "." + t.Name))];
        }

        _ = first;
        // The run after the write renders its rekey fact: a migration with the fact's ledger rows and guarded adoptions only, and the
        // snapshot it saves holds what the fact adopted. With the fact rendered and removed, a third run must find nothing to migrate,
        // and its scripts are what is compared.
        var second = await GenerateAsync(options, ct, throwOnFailure: false).ConfigureAwait(false);
        var rekeyMigrations = new Dictionary<string, string>(StringComparer.Ordinal);
        Read(options.RepoRoot, output, out var migrationsSecond);
        foreach (var added in migrationsSecond.Except(migrationsBefore))
            rekeyMigrations[added] = File.ReadAllText(Path.Combine(options.RepoRoot, added));
        foreach (var path in rekeyFacts)
            File.Delete(Path.Combine(options.RepoRoot, path));
        var third = second.Outcome == RunOutcome.Succeeded ? await GenerateAsync(options, ct, throwOnFailure: false).ConfigureAwait(false) : second;
        var after = Read(options.RepoRoot, output, out var migrationsAfter);
        foreach (var added in migrationsAfter.Except(migrationsSecond))
            after[added] = File.ReadAllText(Path.Combine(options.RepoRoot, added));
        foreach (var run in new[] { second, third }.Where(r => r.Outcome != RunOutcome.Succeeded))
            after["(generation)"] = string.Join("\n", run.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => d.Rule + " " + d.Message));
        if (Environment.GetEnvironmentVariable("MAQUETTISTE_PROOF_DUMP") is { Length: > 0 } dump)
        {
            foreach (var (side, files) in new[] { ("before", before), ("after", after) })
            {
                foreach (var (path, text) in files)
                {
                    var target = Path.Combine(dump, side, path.Replace('(', '_').Replace(')', '_'));
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.WriteAllText(target, text);
                }
            }
        }

        return new MaterializeProofResult(materialized, before, after, migrationsSecond, migrationsAfter, errors, projected)
        {
            OtherFacts = otherFacts, RekeyMigrations = rekeyMigrations, MigrationsAtStart = migrationsBefore,
        };
    }

    private static async Task<GenerationResult> GenerateAsync(EngineOptions options, CancellationToken ct, bool throwOnFailure = true)
    {
        await using var store = new ModelStore(options);
        var service = new GenerationService(store, options);
        var result = await service.RunAsync(new GenerationRequest { Mode = GenerationMode.Apply, Packs = ["sql-ddl"] }, progress: null, ct).ConfigureAwait(false);
        if (throwOnFailure && result.Outcome != RunOutcome.Succeeded)
        {
            throw new InvalidOperationException("sql-ddl did not run: " + string.Join("; ",
                result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => d.Rule + " " + d.Message).Take(20)));
        }

        return result;
    }

    private static Dictionary<string, string> Read(string repoRoot, string output, out IReadOnlyList<string> migrations)
    {
        var root = Path.Combine(repoRoot, output);
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        var scripts = new List<string>();
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var path = Path.GetRelativePath(repoRoot, file).Replace(Path.DirectorySeparatorChar, '/');
            if (path.Contains("/migrations/", StringComparison.Ordinal))
            {
                scripts.Add(path);
                continue;
            }

            files[path] = File.ReadAllText(file);
        }

        scripts.Sort(StringComparer.Ordinal);
        migrations = scripts;
        return files;
    }
}
