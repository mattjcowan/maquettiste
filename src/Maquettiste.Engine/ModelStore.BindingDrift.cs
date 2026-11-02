using System.Collections.Frozen;
using Maquettiste.Engine.Model;

namespace Maquettiste.Engine;

/// <summary>
/// The drift report (next-phase-2-design.md section 3.3, D35): what changed in each binding's table-file source since the binding's
/// provenance was recorded, from files only, as the resolver's MQ4087 and MQ4088 and the <c>resync-binding</c> operation judge it
/// (<see cref="BindingDrift"/>). It never resolves the model.
/// </summary>
public sealed partial class ModelStore
{
    /// <summary>
    /// One page of the bindings and their drift, ordered by entity name, entity id, database name, then database id: per binding the
    /// columns added since its provenance was recorded (without provenance, only the columns nothing accounts for, MQ4047's), the mapped
    /// columns renamed or retyped, the recorded ids the source no longer has, and whether the provenance is stale (MQ4088). Reads files
    /// only, takes no run lock and writes nothing.
    /// </summary>
    /// <param name="query">The database and entities to look at, whether to list bindings in step, the cursor and the page size.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The page; <see cref="BindingDriftPage.Next"/> is the cursor of the next one, or <see langword="null"/> after the last.</returns>
    /// <exception cref="ArgumentException">An unknown database or entity, or a page size out of range.</exception>
    /// <exception cref="FormatException">The cursor is not one a page returned.</exception>
    public async Task<BindingDriftPage> GetBindingDriftAsync(BindingDriftQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        ModelPages.CheckLimit(query.Limit);
        (string, string, string)? after = string.IsNullOrEmpty(query.Cursor) ? null : ModelPages.ReadCursor(query.Cursor);
        var snapshot = await LoadedAsync(ct).ConfigureAwait(false);
        var databases = string.IsNullOrEmpty(query.Database)
            ? null
            : ChosenDatabases(snapshot, query.Database).Select(d => d.Id).ToFrozenSet(StringComparer.Ordinal);
        IEnumerable<Entity> entities = query.Entities is { Count: > 0 } ids
            ? ids.Distinct(StringComparer.Ordinal).Select(id => snapshot.Get<Entity>(id) ?? throw new ArgumentException($"No entity has the id '{id}'.", nameof(query)))
            : snapshot.All<Entity>();

        // The keys order the rows; the drift is computed only for the rows a page reads.
        var rows = new List<(Entity Entity, EntityBinding Binding, string DatabaseName, (string, string, string) Key)>();
        foreach (var entity in entities)
        {
            foreach (var binding in entity.Bindings)
            {
                if (databases is not null && !databases.Contains(binding.Database))
                    continue;
                var databaseName = snapshot.Get<Database>(binding.Database)?.Name ?? binding.Database;
                rows.Add((entity, binding, databaseName, (entity.Name, entity.Id, databaseName + "\u0001" + binding.Database + "\u0001" + binding.Id)));
            }
        }

        rows.Sort((a, b) => Compare(a.Key, b.Key));
        var page = new List<BindingDriftEntry>();
        (string, string, string) last = default;
        string? next = null;
        foreach (var row in rows)
        {
            if (after is { } position && Compare(row.Key, position) <= 0)
                continue;
            ct.ThrowIfCancellationRequested();
            var report = BindingDrift.Of(snapshot, row.Entity, row.Binding);
            if (!query.All && !report.HasDrift)
                continue;
            if (page.Count == query.Limit)
            {
                next = ModelPages.WriteCursor(last);
                break;
            }

            page.Add(Entry(snapshot, row.Entity, row.Binding, row.DatabaseName, report));
            last = row.Key;
        }

        return new BindingDriftPage(page, next);

        static int Compare((string, string, string) a, (string, string, string) b)
        {
            var c = string.CompareOrdinal(a.Item1, b.Item1);
            if (c == 0)
                c = string.CompareOrdinal(a.Item2, b.Item2);
            return c != 0 ? c : string.CompareOrdinal(a.Item3, b.Item3);
        }
    }

    private static BindingDriftEntry Entry(ModelSnapshot snapshot, Entity entity, EntityBinding binding, string databaseName, BindingDriftReport report)
    {
        var sourceName = snapshot.Get<Element>(binding.Source) switch
        {
            Table { Name.Length: > 0 } table => table.Name,
            View view => view.Name,
            Query query => query.Name,
            _ => binding.Source,
        };
        return new BindingDriftEntry(entity.Id, entity.Name, binding.Id, binding.Database, databaseName, binding.Source, sourceName, report.HasProvenance, report.Stale,
            [.. report.Added.Select(a => new DriftColumn(a.Column, a.Name, a.Status))],
            [.. report.Renamed.Select(Field)],
            [.. report.Retyped.Select(Field)],
            report.Removed);

        static DriftField Field(DriftMappedColumn c) => new(c.Column, c.Name, c.Attribute, c.AttributeName);
    }
}

/// <summary>What <see cref="ModelStore.GetBindingDriftAsync"/> reads (next-phase-2-design.md section 3.3).</summary>
/// <param name="Database">Only the bindings to this database, by id or name; <see langword="null"/> for every database.</param>
/// <param name="Entities">Only the bindings of these entities, by id; <see langword="null"/> or empty for every entity.</param>
/// <param name="All">List every binding, also those with nothing to report; by default only those with drift or stale provenance.</param>
/// <param name="Cursor">The <see cref="BindingDriftPage.Next"/> of the previous page.</param>
/// <param name="Limit">The page size, 1 to <see cref="ModelPages.MaxLimit"/>.</param>
public sealed record BindingDriftQuery(string? Database = null, IReadOnlyList<string>? Entities = null, bool All = false,
    string? Cursor = null, int Limit = ModelPages.DefaultLimit);

/// <summary>One page of the drift report.</summary>
/// <param name="Bindings">The bindings, by entity name, entity id, database name, then database id.</param>
/// <param name="Next">The cursor of the next page, or <see langword="null"/> after the last.</param>
public sealed record BindingDriftPage(IReadOnlyList<BindingDriftEntry> Bindings, string? Next);

/// <summary>One binding's drift.</summary>
/// <param name="Entity">The entity id.</param>
/// <param name="EntityName">The entity name.</param>
/// <param name="Binding">The binding id.</param>
/// <param name="Database">The database id.</param>
/// <param name="DatabaseName">The database name (the id when no database has it).</param>
/// <param name="Source">The binding's source as written: a table, view or query id, or a synthesized table key.</param>
/// <param name="SourceName">The source's name (the source as written when it has none).</param>
/// <param name="HasProvenance">Whether the binding records provenance.</param>
/// <param name="Stale">
/// Whether the provenance does not describe the source (MQ4088): the source is not a designed or imported table file, or none of the
/// columns it records is a column of it. A stale provenance is read as none.
/// </param>
/// <param name="Added">
/// The source's columns not recorded that nothing in the binding names, in column order, whatever the database fills; without
/// provenance (or with a stale one), only the columns nothing accounts for.
/// </param>
/// <param name="Renamed">The mapped columns whose name changed since they were recorded, in column order.</param>
/// <param name="Retyped">The mapped columns whose type changed since they were recorded, in column order.</param>
/// <param name="Removed">The recorded column ids the source no longer has, in ordinal order.</param>
public sealed record BindingDriftEntry(string Entity, string EntityName, string Binding, string Database, string DatabaseName,
    string Source, string SourceName, bool HasProvenance, bool Stale, IReadOnlyList<DriftColumn> Added,
    IReadOnlyList<DriftField> Renamed, IReadOnlyList<DriftField> Retyped, IReadOnlyList<string> Removed);

/// <summary>A column added to a binding's source.</summary>
/// <param name="Column">The column id (an inherited column's keyed id).</param>
/// <param name="Name">The column's name.</param>
/// <param name="Status">What fills it: <c>unaccounted</c>, <c>identity</c>, <c>default</c> or <c>computed</c>.</param>
public sealed record DriftColumn(string Column, string Name, string Status);

/// <summary>A mapped column renamed or retyped since the binding's provenance was recorded.</summary>
/// <param name="Column">The column id (an inherited column's keyed id).</param>
/// <param name="Name">The column's name now.</param>
/// <param name="Attribute">The field's attribute reference: an attribute id, <c>attributeId.memberId</c> or a relation end id.</param>
/// <param name="AttributeName">The name the field goes by (an attribute's name, <c>attributeMember</c>, or a navigation's name plus <c>Id</c>).</param>
public sealed record DriftField(string Column, string Name, string Attribute, string AttributeName);
