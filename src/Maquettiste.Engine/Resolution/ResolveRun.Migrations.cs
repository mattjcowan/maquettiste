using Maquettiste.Engine.Migrations;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Validation;

namespace Maquettiste.Engine.Resolution;

/// <summary>
/// Migration facts (next-phase-1-design.md section 3.2, "Applied order and projection"): each database's open facts in applied
/// order (<see cref="FactKeys.Order"/>), their object keys moved through the <c>rekey</c> facts after them
/// (<see cref="FactKeys.Moves"/>), and their projection onto the tables, columns, views and sequences they name by current key:
/// former names, a column's conversion and the id of the first fact. Facts never change what a capture records; a projected
/// object adds the <c>e:</c> key of each fact projected onto it (a table, of its columns' facts too) to its dependencies.
/// </summary>
internal sealed partial class ResolveRun
{
    /// <summary>The membership key of the migration lists.</summary>
    private const string MigrationKind = "k:migration";

    /// <summary>Every database's facts in turn, in database order then applied order: <see cref="ResolvedModel.Migrations"/>.</summary>
    private readonly List<RMigration> _migrationOrder = [];

    /// <summary>The <c>e:</c> keys of every fact, for the membership of <see cref="ResolvedModel.Migrations"/>.</summary>
    private readonly List<string> _migrationKeys = [];

    /// <summary>Resolves one database's facts and projects them onto its objects; runs after the database itself is resolved.</summary>
    /// <param name="rdb">The resolved database.</param>
    /// <param name="database">Its file.</param>
    internal void ResolveMigrations(RDatabase rdb, Database database)
    {
        var facts = Model.All<Migration>().Where(f => string.Equals(f.Database, database.Id, StringComparison.Ordinal)).ToList();
        if (facts.Count == 0)
        {
            rdb.Migrations = new RList<RMigration>([], [MigrationKind]);
            return;
        }

        var dialect = DialectTypeMaps.Name(database.Dialect);
        var ordered = FactKeys.Order(facts);
        var moves = FactKeys.Moves(ordered);
        var resolved = new List<RMigration>(ordered.Count);
        var byId = new Dictionary<string, RMigration>(ordered.Count, StringComparer.Ordinal);
        for (var i = 0; i < ordered.Count; i++)
        {
            var r = MakeMigration(ordered[i], i, rdb, database, dialect, moves, ordered);
            resolved.Add(r);
            byId[r.Id] = r;
            Register(r);
        }

        foreach (var r in resolved)
        {
            var after = new List<RMigration>();
            foreach (var id in r.Source.After)
            {
                if (byId.TryGetValue(id, out var other) && !ReferenceEquals(other, r) && !after.Contains(other))
                    after.Add(other);
            }

            r.After = after;
        }

        var membership = new DependencySet(Keys).Add(MigrationKind);
        foreach (var r in resolved)
            membership.Element(r.Id);
        rdb.Migrations = new RList<RMigration>(resolved, membership.ToList());
        _migrationOrder.AddRange(resolved);
        _migrationKeys.AddRange(resolved.Select(r => Keys.Element(r.Id)));
        Project(rdb, resolved);
    }

    /// <summary>The membership keys of <see cref="ResolvedModel.Migrations"/>: <c>k:migration</c>, <c>k:database</c> and every fact.</summary>
    private IReadOnlyList<string> MigrationMembership() =>
        new DependencySet(Keys).Add(MigrationKind).Add("k:database").AddRange(_migrationKeys).ToList();

    private RMigration MakeMigration(Migration fact, int position, RDatabase rdb, Database database, string dialect, FactKeys.KeyMoves moves,
        IReadOnlyList<Migration> ordered)
    {
        var deps = new DependencySet(Keys).Element(fact.Id).Element(database.Id);
        var state = MigrationRules.State(fact, database.Dialect);
        var r = new RMigration
        {
            Id = fact.Id,
            Name = fact.Name,
            Database = rdb,
            Fact = ResolutionValues.Kebab(fact.Fact),
            Order = position + 1,
            State = state,
            Missing = state == "pending" ? MigrationRules.Missing(fact, database.Dialect) : [],
            Object = fact.Object is { } o ? MakeObject(o, position, moves) : null,
            Objects = [.. fact.Objects.Select(o => MakeObject(o, position, moves))],
            Collections = [.. fact.Collections.Select(c => MakeCollection(c, position, moves))],
            From = MakeState(fact.From),
            To = MakeState(fact.To),
            Via = [.. fact.Via],
            Keys = [.. fact.Keys.Select(k => new RMigrationRekey { Kind = k.Kind, Table = k.Table, From = k.From, To = k.To, Name = k.Name })],
            Conversion = MakeConversion(fact, dialect),
            DataCheck = fact.DataCheck is { } check ? new RMigrationDataCheck { Query = ForDialect(check.Query, dialect), Accept = check.Accept } : null,
            Acknowledge = fact.Acknowledge,
            Value = ForDialect(fact.Value, dialect),
            Where = ForDialect(fact.Where, dialect),
            Sql = ForDialect(fact.Sql, dialect),
            SkipWhen = ForDialect(fact.SkipWhen, dialect),
            Phase = ResolutionValues.Kebab(fact.Phase),
            Environment = fact.Environment,
            ReferenceType = fact.ReferenceType,
            FromReferenceType = fact.FromReferenceType,
            Process = fact.Process,
            Values = [.. fact.Values.Select(v => new RMigrationValue { From = v.From, To = v.To })],
            Held = fact.Held,
            Released = fact.Released,
            Source = fact,
        };
        var document = Model.GetDocument(fact.Id);
        r.HashFactory = document is null ? () => FactHash.Of(fact) : () => FactHash.Of(document.Json);
        FillPhysicalAnnotations(r, fact, deps);
        // The keys today depend on the rekey facts after this one.
        if (moves.Any)
        {
            for (var i = position + 1; i < ordered.Count; i++)
            {
                if (ordered[i].Fact == MigrationFact.Rekey)
                    deps.Element(ordered[i].Id);
            }
        }

        r.Dependencies = deps.ToList();
        return r;
    }

    private static RMigrationObject MakeObject(MigrationObject o, int position, FactKeys.KeyMoves moves)
    {
        var (table, key) = moves.Current(position, o.Kind, o.Table, o.Key);
        return new RMigrationObject
        {
            Kind = o.Kind,
            Table = o.Table,
            Key = o.Key,
            Schema = o.Schema,
            TableName = o.TableName,
            Name = o.Name,
            FormerNames = [.. o.FormerNames],
            CurrentKey = key,
            CurrentTable = table,
        };
    }

    /// <summary>A value map's collection, its owner table's key moved as a table key through the <c>rekey</c> facts after the fact.</summary>
    private static RMigrationCollection MakeCollection(MigrationCollection c, int position, FactKeys.KeyMoves moves) => new()
    {
        Table = c.Table,
        Schema = c.Schema,
        TableName = c.TableName,
        Attribute = c.Attribute,
        Name = c.Name,
        CurrentTable = moves.Current(position, "table", null, c.Table).Key,
    };

    private static RMigrationState? MakeState(MigrationState? s) => s is null ? null : new RMigrationState
    {
        Name = s.Name,
        PluralName = s.PluralName,
        Schema = s.Schema,
        Type = s.Type,
        Length = s.Length,
        Precision = s.Precision,
        Scale = s.Scale,
        NativeType = s.NativeType,
        Strategy = s.Strategy,
        ReferenceTable = s.ReferenceTable,
        Nullable = s.Nullable,
    };

    private static RConversion? MakeConversion(Migration fact, string dialect) => fact.Conversion is { } c
        ? new RConversion { FactId = fact.Id, Cast = c.Cast, Expression = ForDialect(c.Expression, dialect) }
        : null;

    /// <summary>A dialect map's entry for the dialect, else its <c>"*"</c> entry, else <see langword="null"/>.</summary>
    private static string? ForDialect(IReadOnlyDictionary<string, string> map, string dialect) =>
        map.Count == 0 ? null : map.TryGetValue(dialect, out var own) ? own : map.GetValueOrDefault("*");

    /// <summary>
    /// Projects a database's facts, in applied order, onto the objects they name by current key: a table, view, sequence or column
    /// takes the <c>from</c> and <c>via</c> names of its renames as former names (oldest first, distinct, without its current name)
    /// and the id of the first fact that names it (as <c>object</c> or in <c>objects</c>) as <c>since</c>; a column takes the
    /// conversion of its last <c>change-type</c>. A dropped object is not in the database, so nothing takes its facts.
    /// </summary>
    private void Project(RDatabase rdb, List<RMigration> facts)
    {
        var tables = rdb.Tables.ToDictionary(t => t.Key, StringComparer.Ordinal);
        var views = rdb.Views.ToDictionary(v => v.Id, StringComparer.Ordinal);
        var sequences = rdb.Sequences.ToDictionary(s => s.Id, StringComparer.Ordinal);
        var projections = new Dictionary<object, Projection>(ReferenceEqualityComparer.Instance);
        var tableFacts = new Dictionary<RTable, List<string>>(ReferenceEqualityComparer.Instance);

        foreach (var fact in facts)
        {
            var named = fact.Object is { } single ? (IEnumerable<RMigrationObject>)[single, .. fact.Objects] : fact.Objects;
            foreach (var o in named)
            {
                object? target = null;
                RTable? owner = null;
                switch (o.Kind)
                {
                    case "table" when tables.TryGetValue(o.CurrentKey, out var table):
                        target = owner = table;
                        break;
                    case "column" when o.CurrentTable is { } tableKey && tables.TryGetValue(tableKey, out var host):
                        owner = host;
                        target = host.Columns.FirstOrDefault(c => string.Equals(c.Key, o.CurrentKey, StringComparison.Ordinal));
                        break;
                    case "view" when views.TryGetValue(o.CurrentKey, out var view):
                        target = view;
                        break;
                    case "sequence" when sequences.TryGetValue(o.CurrentKey, out var sequence):
                        target = sequence;
                        break;
                }

                if (owner is not null)
                {
                    if (!tableFacts.TryGetValue(owner, out var ids))
                        tableFacts[owner] = ids = [];
                    if (!ids.Contains(fact.Id, StringComparer.Ordinal))
                        ids.Add(fact.Id);
                }

                if (target is null)
                    continue;
                if (!projections.TryGetValue(target, out var projection))
                    projections[target] = projection = new Projection();
                projection.Facts.Add(fact.Id);
                projection.Since ??= fact.Id;
                if (!ReferenceEquals(o, fact.Object))
                    continue;
                if (fact.Fact == "rename")
                {
                    if (fact.From?.Name is { } from)
                        projection.Names.Add(from);
                    projection.Names.AddRange(fact.Via);
                }
                else if (fact.Fact == "change-type" && target is RColumn)
                {
                    projection.Conversion = fact.Conversion;
                    projection.HasChangeType = true;
                }
            }
        }

        foreach (var (target, projection) in projections)
        {
            switch (target)
            {
                case RTable t:
                    t.FormerNames = Former(projection.Names, t.Name);
                    t.Since = projection.Since;
                    break;
                case RColumn c:
                    c.FormerNames = Former(projection.Names, c.Name);
                    c.Since = projection.Since;
                    if (projection.HasChangeType)
                        c.Conversion = projection.Conversion;
                    break;
                case RView v:
                    v.FormerNames = Former(projection.Names, v.Name);
                    v.Since = projection.Since;
                    v.Dependencies = new DependencySet(Keys).AddRange(v.Dependencies).AddRange(projection.Facts.Select(Keys.Element)).ToList();
                    break;
                case RSequence s:
                    s.FormerNames = Former(projection.Names, s.Name);
                    s.Since = projection.Since;
                    s.Dependencies = new DependencySet(Keys).AddRange(s.Dependencies).AddRange(projection.Facts.Select(Keys.Element)).ToList();
                    break;
            }
        }

        // A table and its columns share one dependency list (and the column list's membership): the facts of the table and of its
        // columns join it.
        foreach (var (table, ids) in tableFacts)
        {
            var deps = new DependencySet(Keys).AddRange(table.Dependencies).AddRange(ids.Select(Keys.Element)).ToList();
            table.Dependencies = deps;
            foreach (var column in table.Columns)
                column.Dependencies = deps;
            table.Columns = new RList<RColumn>(table.Columns, deps);
        }
    }

    /// <summary>Names oldest first, each once, without the current name.</summary>
    private static List<string> Former(List<string> names, string current)
    {
        var result = new List<string>(names.Count);
        foreach (var name in names)
        {
            if (!string.Equals(name, current, StringComparison.Ordinal) && !result.Contains(name, StringComparer.Ordinal))
                result.Add(name);
        }

        return result;
    }

    /// <summary>What the facts of one object project onto it.</summary>
    private sealed class Projection
    {
        public List<string> Names { get; } = [];

        public List<string> Facts { get; } = [];

        public string? Since { get; set; }

        public RConversion? Conversion { get; set; }

        public bool HasChangeType { get; set; }
    }
}
