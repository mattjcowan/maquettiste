using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Pipeline;
using Maquettiste.Engine.Scripting;
using Maquettiste.Engine.Validation;

namespace Maquettiste.Engine.Resolution;

/// <summary>
/// One seed's scripted rows (next-phase-2-design.md section 3.4): the rows its script returned, converted to seed rows with derived ids,
/// the dependency keys they carry, and what went wrong.
/// </summary>
/// <param name="Rows">The rows, in the script's order, each cell in the seed's column order (trailing nulls dropped).</param>
/// <param name="Keys">
/// The keys the rows depend on besides the seed's own: the script's <c>x:</c> key and every key the run read (<c>e:</c>, <c>k:</c>,
/// <c>ix:</c>, <c>rt:</c>), ordinal.
/// </param>
/// <param name="Diagnostics">MQ7116 (no such script), MQ7127 (the script failed) and MQ7128 (a key the columns do not name, or no row
/// key), on the seed's file at <c>/rowsFrom/script</c>; MQ5005 on the script's file.</param>
/// <param name="Failure">The MQ7116 or MQ7127 that left the seed without rows, or <see langword="null"/>.</param>
internal sealed record ScriptedRows(ImmutableArray<SeedRow> Rows, ImmutableArray<string> Keys, ImmutableArray<Diagnostic> Diagnostics,
    Diagnostic? Failure);

/// <summary>
/// Runs seeds' rows scripts (next-phase-2-design.md section 3.4, decisions 12 to 17). A script runs in a one-engine pool created for the
/// call, under the snapshot's sandbox limits, through <see cref="SeedRowsCache"/>; its function gets the rule-script model view and a
/// frozen copy of the seed document, and returns plain objects keyed by the seed's column names. Each object becomes a
/// <see cref="SeedRow"/>: keys matched ordinally to the column names, numbers as JSON numbers, trailing nulls dropped, and the id derived
/// from the seed id and the row key in seed terms (<see cref="StableIds.RowId"/>), so previews, exports and templates see the same ids
/// on every run. Load, the index and extension writes never call this; the resolver, validation and previews do, for the seeds they read.
/// </summary>
internal static class SeedScripts
{
    /// <summary>The pointer every scripted-rows finding of the seed's file takes.</summary>
    public const string Pointer = "/rowsFrom/script";

    /// <summary>The dependency key of a file under <c>extensions/</c>: <c>x:&lt;path under extensions&gt;</c>.</summary>
    /// <param name="path">The path under <c>extensions/</c>, such as <c>seeds/audited-records.js</c>.</param>
    /// <returns>The key.</returns>
    public static string Key(string path) => "x:" + path;

    /// <summary>Evaluates a seed's rows script: the cached run when its inputs are unchanged, else a new run.</summary>
    /// <param name="model">The snapshot.</param>
    /// <param name="seed">The seed (its <c>rowsFrom.script</c> names the script).</param>
    /// <param name="factory">The sandbox factory.</param>
    /// <param name="cache">The rows cache.</param>
    /// <param name="ct">Cancellation of the call.</param>
    /// <returns>The rows.</returns>
    public static ScriptedRows Evaluate(ModelSnapshot model, Seed seed, IScriptSandboxFactory factory, SeedRowsCache cache, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(seed);
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(cache);
        var path = seed.RowsFrom?.Script ?? throw new ArgumentException("The seed has no rows script.", nameof(seed));
        var document = model.GetDocument(seed.Id);
        var seedPath = document?.Path;
        var keys = ImmutableArray.Create(Key(path));
        Diagnostic Fail(string rule, string message) => RuleCatalog.Create(rule, message, seed.Id, seedPath, Pointer);
        if (document is null)
        {
            var missing = Fail("MQ7127", $"Seed '{seed.Name}' is not in the model, so its rows script {path} cannot run.");
            return new ScriptedRows([], keys, [missing], missing);
        }

        if (model.SeedScript(path) is not { } script)
        {
            var missing = Fail("MQ7116", $"The rows script {path} of seed '{seed.Name}' is missing: add extensions/{path}, or name a script that exists.");
            return new ScriptedRows([], keys, [missing], missing);
        }

        var run = cache.Get(model, document, script, factory, ct);
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        diagnostics.AddRange(run.Warnings);
        keys = [.. run.Keys.Append(Key(path)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
        if (run.Failure is { } failure)
        {
            var at = failure.Line is { } line
                ? string.Create(CultureInfo.InvariantCulture, $" ({failure.FilePath ?? script.Path}, line {line}{(failure.Column is { } column ? ", column " + column : "")})")
                : "";
            var diagnostic = Fail("MQ7127", $"The rows script {path} of seed '{seed.Name}' failed: {failure.Message}{at}");
            diagnostics.Add(diagnostic);
            return new ScriptedRows([], keys, diagnostics.ToImmutable(), diagnostic);
        }

        var rows = Convert(model, seed, run.Rows, (rule, message) => diagnostics.Add(Fail(rule, message)));
        return new ScriptedRows(rows, keys, diagnostics.ToImmutable(), null);
    }

    /// <summary>
    /// Turns the objects a script returned into seed rows: keys matched ordinally to the seed's column names (<see cref="ColumnNames"/>),
    /// cells in column order with trailing nulls dropped, ids derived from the seed id and the row key in seed terms. A key the columns do
    /// not name is MQ7128 and the row is left out, as is every row when the target has no row key or a row gives no value for one of its
    /// parts.
    /// </summary>
    private static ImmutableArray<SeedRow> Convert(ModelSnapshot model, Seed seed, JsonElement returned, Action<string, string> report)
    {
        var names = ColumnNames(model, seed);
        var key = KeyPositions(model, seed);
        if (key is null)
        {
            report("MQ7128", $"Seed '{seed.Name}' takes its rows from a script, but its target has no row key to derive row ids from (a relation, " +
                "an entity without a key, a table without a primary key or named key, or a reference type seed without its code column).");
            return [];
        }

        var position = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < names.Count; i++)
            position.TryAdd(names[i], i);
        var rows = ImmutableArray.CreateBuilder<SeedRow>();
        var index = 0;
        foreach (var item in returned.EnumerateArray())
        {
            var at = index++;
            var cells = new JsonElement[names.Count];
            var unknown = item.EnumerateObject().Where(p => !position.ContainsKey(p.Name)).Select(p => p.Name).ToList();
            if (unknown.Count > 0)
            {
                report("MQ7128", $"Scripted row {at.ToString(CultureInfo.InvariantCulture)} of seed '{seed.Name}' has the key '{unknown[0]}', which none of the " +
                    $"seed's columns is named ({string.Join(", ", names)}).");
                continue;
            }

            foreach (var property in item.EnumerateObject())
                cells[position[property.Name]] = property.Value.Clone();
            var parts = key.Select(p => cells[p]).ToList();
            string id;
            try
            {
                if (parts.Any(p => p.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null))
                    throw new ArgumentException("a part of the row key has no value");
                id = StableIds.RowId(seed.Id, [.. parts.Cast<object?>()]);
            }
            catch (ArgumentException ex)
            {
                report("MQ7128", $"Scripted row {at.ToString(CultureInfo.InvariantCulture)} of seed '{seed.Name}' has no row key to derive its id from " +
                    $"({ex.Message.Split(" (Parameter", 2)[0].TrimEnd('.')}): give every key column ({string.Join(", ", key.Select(p => names[p]))}) a value.");
                continue;
            }

            var count = cells.Length;
            while (count > 0 && cells[count - 1].ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
                count--;
            var values = new JsonElement[count];
            for (var i = 0; i < count; i++)
                values[i] = cells[i].ValueKind == JsonValueKind.Undefined ? Null : cells[i];
            rows.Add(new SeedRow { Id = id, Values = values });
        }

        return rows.ToImmutable();
    }

    private static readonly JsonElement Null = JsonDocument.Parse("null").RootElement.Clone();

    /// <summary>
    /// The name of each of a seed's columns, as a rows script keys its objects (decision 12): <c>code</c>, <c>label</c> and
    /// <c>description</c> for a reference type; an attribute's name or an end's role; a table column's name; a foreign key's name for a
    /// foreign-key entry, else its id (as CSV files head it). A column the model does not hold keeps its id.
    /// </summary>
    /// <param name="model">The snapshot.</param>
    /// <param name="seed">The seed.</param>
    /// <returns>The names, in column order.</returns>
    public static IReadOnlyList<string> ColumnNames(ModelSnapshot model, Seed seed)
    {
        var target = model.GetDocument(seed.Target)?.Element;
        var table = target is Table file ? MemberRules.WithMembers(model, file) : null;
        var names = new List<string>(seed.Columns.Count);
        foreach (var column in seed.Columns)
        {
            if (table is not null)
            {
                names.Add(table.Columns.FirstOrDefault(c => string.Equals(c.Id, column, StringComparison.Ordinal))?.Name
                    ?? table.ForeignKeys.FirstOrDefault(f => string.Equals(f.Id, column, StringComparison.Ordinal)) switch
                    {
                        { } fk => fk.Name ?? fk.Id,
                        null => column,
                    });
                continue;
            }

            names.Add(SubElementName(model, column) ?? column);
        }

        return names;
    }

    /// <summary>An attribute's name or a relation end's role, read from the document that holds the id.</summary>
    private static string? SubElementName(ModelSnapshot model, string id)
    {
        if (!model.TryGetEntry(id, out var entry) || entry.JsonPointer.Length == 0 || model.GetDocument(id) is not { } document)
            return null;
        var current = document.Json;
        foreach (var raw in entry.JsonPointer.Split('/').Skip(1))
        {
            var segment = raw.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
            if (current.ValueKind == JsonValueKind.Object && current.TryGetProperty(segment, out var child))
                current = child;
            else if (current.ValueKind == JsonValueKind.Array && int.TryParse(segment, NumberStyles.None, CultureInfo.InvariantCulture, out var i)
                && i < current.GetArrayLength())
                current = current[i];
            else
                return null;
        }

        if (current.ValueKind != JsonValueKind.Object)
            return null;
        return string.Equals(entry.Kind, "end", StringComparison.Ordinal) && current.TryGetProperty("role", out var role) && role.ValueKind == JsonValueKind.String
            ? role.GetString()
            : current.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String ? name.GetString() : null;
    }

    /// <summary>
    /// The positions of a seed's row key among its columns, in key order (decision 14): the code column of a reference type's seed, the
    /// columns of a table seed's key (the unique constraint it names, else the primary key; a key column a foreign-key entry fills is that
    /// entry's position, once), the key attributes of an entity's seed; <see langword="null"/> when the target has no row key (a relation,
    /// an entity without a key, a table without one, a seed that does not list its code) or a key column is not given.
    /// </summary>
    /// <param name="model">The snapshot.</param>
    /// <param name="seed">The seed.</param>
    /// <returns>The positions.</returns>
    public static IReadOnlyList<int>? KeyPositions(ModelSnapshot model, Seed seed)
    {
        IReadOnlyList<string>? columns;
        Table? table = null;
        switch (model.GetDocument(seed.Target)?.Element)
        {
            case ReferenceType:
                columns = ["code"];
                break;
            case Entity { Key.Attributes.Count: > 0 } entity:
                columns = entity.Key.Attributes;
                break;
            case Table file:
                table = MemberRules.WithMembers(model, file);
                columns = seed.Key is { } unique
                    ? table.Uniques.FirstOrDefault(u => string.Equals(u.Id, unique, StringComparison.Ordinal))?.Columns
                    : table.PrimaryKey?.Columns;
                break;
            default:
                return null;
        }

        if (columns is not { Count: > 0 })
            return null;
        var positions = new List<int>(columns.Count);
        foreach (var column in columns)
        {
            var at = IndexOf(seed.Columns, column);
            if (at < 0 && table is not null)
            {
                for (var i = 0; i < seed.Columns.Count && at < 0; i++)
                {
                    if (table.ForeignKeys.FirstOrDefault(f => string.Equals(f.Id, seed.Columns[i], StringComparison.Ordinal)) is { } fk
                        && fk.Columns.Contains(column, StringComparer.Ordinal))
                        at = i;
                }
            }

            if (at < 0)
                return null;
            if (!positions.Contains(at))
                positions.Add(at);
        }

        return positions;
    }

    private static int IndexOf(IReadOnlyList<string> list, string value)
    {
        for (var i = 0; i < list.Count; i++)
        {
            if (string.Equals(list[i], value, StringComparison.Ordinal))
                return i;
        }

        return -1;
    }
}

/// <summary>
/// The engine-wide cache of rows script runs (next-phase-2-design.md section 3.4, decision 15), held by
/// <see cref="Generation.EngineServices"/>, so a long-lived host runs a seed's script again only when its inputs changed. An entry, one per
/// seed, holds the array the script returned and the keys the run read, and is reused while the seed's dependency hash, the script's path
/// and hash, the sandbox limits and the current hash of every key the run read (<see cref="RuleKeyHasher"/>, as rule results are cached)
/// are unchanged. Only clean runs are stored: no failure and no MQ5005. Thread-safe; a run happens outside any lock, so two callers may
/// run one script at once and the last store wins (their results are equal).
/// </summary>
internal sealed class SeedRowsCache
{
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private long _runs;

    /// <summary>The number of script runs (tests: a cached result runs nothing).</summary>
    public long Runs => Interlocked.Read(ref _runs);

    /// <summary>The number of entries.</summary>
    public int Count => _entries.Count;

    /// <summary>One run's outcome.</summary>
    /// <param name="Rows">The returned array (an empty array after a failure).</param>
    /// <param name="Keys">The keys the run read, ordinal.</param>
    /// <param name="Warnings">The script's load warnings (MQ5005).</param>
    /// <param name="Failure">Why the script failed (its MQ5002 or MQ5003 from the sandbox, with the script's position), or null.</param>
    public sealed record Run(JsonElement Rows, ImmutableArray<string> Keys, ImmutableArray<Diagnostic> Warnings, Diagnostic? Failure);

    /// <summary>The cached run of a seed's script when its inputs are unchanged, else a new run (stored when clean).</summary>
    /// <param name="model">The snapshot the script reads.</param>
    /// <param name="seed">The seed's document.</param>
    /// <param name="script">The rows script.</param>
    /// <param name="factory">The sandbox factory.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The run.</returns>
    public Run Get(ModelSnapshot model, ElementDocument seed, ScriptSource script, IScriptSandboxFactory factory, CancellationToken ct)
    {
        var limits = model.Settings.Limits;
        var hasher = RuleKeyHasher.Of(model);
        if (_entries.TryGetValue(seed.Element.Id, out var entry) && entry.Matches(seed, script, limits, hasher))
            return entry.Run;
        var run = Execute(model, seed, script, factory, limits, ct);
        Interlocked.Increment(ref _runs);
        if (run.Failure is null && run.Warnings.IsEmpty)
            _entries[seed.Element.Id] = new Entry(seed.DependencyHash, script.Path, script.Hash, limits, [.. run.Keys.Select(hasher.CurrentHash)], run);
        else
            _entries.TryRemove(seed.Element.Id, out _);
        return run;
    }

    private static Run Execute(ModelSnapshot model, ElementDocument seed, ScriptSource script, IScriptSandboxFactory factory, SandboxLimits limits,
        CancellationToken ct)
    {
        var reads = new Recorder();
        ImmutableArray<Diagnostic> warnings = [];
        try
        {
            using var pool = factory.CreatePool([script], limits, 1, ct);
            warnings = [.. pool.Warnings];
            using var lease = pool.Rent();
            var rows = lease.Sandbox.RunRows(script.Path, seed, model, new ScriptCallContext(reads, seed.Element.Id, NoParameters, ct));
            return new Run(rows, reads.Sorted(), warnings, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (ScriptErrorException ex)
        {
            return new Run(Empty, reads.Sorted(), warnings, ex.Diagnostic);
        }
        catch (ScriptLimitException ex)
        {
            return new Run(Empty, reads.Sorted(), warnings, ex.Diagnostic);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
        {
            return new Run(Empty, reads.Sorted(), warnings, RuleCatalog.Create("MQ5002", ex.Message, seed.Element.Id, script.Path));
        }
    }

    private static readonly IReadOnlyDictionary<string, object?> NoParameters = System.Collections.Frozen.FrozenDictionary<string, object?>.Empty;

    private static readonly JsonElement Empty = JsonDocument.Parse("[]").RootElement.Clone();

    /// <summary>The keys one run read; the sandbox records from one thread.</summary>
    private sealed class Recorder : IReadRecorder
    {
        private readonly HashSet<string> _keys = new(StringComparer.Ordinal);

        public void Record(string dependencyKey) => _keys.Add(dependencyKey);

        public ImmutableArray<string> Sorted() => [.. _keys.Order(StringComparer.Ordinal)];
    }

    private sealed record Entry(string SeedHash, string ScriptPath, string ScriptHash, SandboxLimits Limits, ImmutableArray<string> Hashes, Run Run)
    {
        public bool Matches(ElementDocument seed, ScriptSource script, SandboxLimits limits, RuleKeyHasher hasher)
        {
            if (!string.Equals(SeedHash, seed.DependencyHash, StringComparison.Ordinal) || !string.Equals(ScriptPath, script.Path, StringComparison.Ordinal)
                || !string.Equals(ScriptHash, script.Hash, StringComparison.Ordinal) || !Equals(Limits, limits))
                return false;
            for (var i = 0; i < Hashes.Length; i++)
            {
                if (!string.Equals(Hashes[i], hasher.CurrentHash(Run.Keys[i]), StringComparison.Ordinal))
                    return false;
            }

            return true;
        }
    }
}
