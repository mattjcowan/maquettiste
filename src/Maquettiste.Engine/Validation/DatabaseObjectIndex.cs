using System.Collections.Frozen;
using Maquettiste.Engine.Model;

namespace Maquettiste.Engine.Validation;

/// <summary>
/// What the rules on routines, database types and SQL objects share (added 2026-10-01): the database types of each database by name
/// (plain and schema-qualified, as a column's <c>nativeType</c> may write it), and the elements caught in a dependency cycle
/// (<c>dependsOn</c>, and the database types a composite's fields use), each with the cycle's names for the message.
/// </summary>
internal sealed class DatabaseObjectIndex
{
    private readonly FrozenDictionary<string, DatabaseType> _typesByName;
    private readonly FrozenDictionary<string, string> _cycles;

    private DatabaseObjectIndex(FrozenDictionary<string, DatabaseType> typesByName, FrozenDictionary<string, string> cycles)
    {
        _typesByName = typesByName;
        _cycles = cycles;
    }

    /// <summary>Builds the index over a context's active documents.</summary>
    /// <param name="context">The validation context.</param>
    /// <returns>The index.</returns>
    public static DatabaseObjectIndex Build(ValidationContext context)
    {
        var model = context.Model;
        var byName = new Dictionary<string, DatabaseType>(StringComparer.Ordinal);
        var types = context.Documents.Select(d => d.Element).OfType<DatabaseType>().OrderBy(t => t.Id, StringComparer.Ordinal).ToList();
        foreach (var type in types)
        {
            byName.TryAdd(type.Database + "|" + type.Name, type);
            if (type.Schema is { } schemaId && model.Get<Database>(type.Database)?.Schemas.FirstOrDefault(s => s.Id == schemaId) is { } schema)
                byName.TryAdd(type.Database + "|" + schema.Name + "." + type.Name, type);
            else if (type.Schema is null && model.Get<Database>(type.Database) is { } database
                && (database.DefaultSchema ?? DialectInfo.DefaultSchema(database.Dialect)) is { Length: > 0 } defaultSchema)
                byName.TryAdd(type.Database + "|" + defaultSchema + "." + type.Name, type);
        }

        // The dependency graph: a routine, SQL object or view depends on what its dependsOn names, a database type on the database types
        // its fields use.
        var edges = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var element in context.Documents.Select(d => d.Element))
        {
            IEnumerable<string>? targets = element switch
            {
                Routine r => r.DependsOn,
                SqlObject o => o.DependsOn,
                View v => v.DependsOn,
                DatabaseType t => t.Fields.Select(f => f.Type).OfType<string>().Where(id => model.Get<DatabaseType>(id) is not null),
                _ => null,
            };
            if (targets is null)
                continue;
            edges[element.Id] = [.. targets.Distinct(StringComparer.Ordinal)];
            names[element.Id] = element.Name;
        }

        var cycles = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var component in StronglyConnected(edges))
        {
            if (component.Count == 1 && !edges[component[0]].Contains(component[0], StringComparer.Ordinal))
                continue;
            var ordered = component.OrderBy(id => names[id], StringComparer.Ordinal).ThenBy(id => id, StringComparer.Ordinal).ToList();
            var text = string.Join(", ", ordered.Select(id => "'" + names[id] + "'"));
            foreach (var id in component)
                cycles[id] = text;
        }

        return new DatabaseObjectIndex(byName.ToFrozenDictionary(StringComparer.Ordinal), cycles.ToFrozenDictionary(StringComparer.Ordinal));
    }

    /// <summary>
    /// The database type a column's <c>nativeType</c> names: by id (of any database, so a reference to another database's type can be
    /// reported), else by name or schema-qualified name among the database's own types.
    /// </summary>
    /// <param name="model">The snapshot.</param>
    /// <param name="databaseId">The column's database id.</param>
    /// <param name="nativeType">The native type as written.</param>
    /// <returns>The type, or <see langword="null"/> for a plain native type.</returns>
    public DatabaseType? TypeOfNative(ModelSnapshot model, string databaseId, string nativeType) =>
        model.Get<DatabaseType>(nativeType) ?? _typesByName.GetValueOrDefault(databaseId + "|" + nativeType);

    /// <summary>Whether an element is part of a dependency cycle, and the names of the cycle's elements.</summary>
    /// <param name="id">The element id.</param>
    /// <param name="names">The quoted names of the cycle's elements, comma-separated.</param>
    /// <returns><see langword="true"/> when it is.</returns>
    public bool InCycle(string id, out string names) => _cycles.TryGetValue(id, out names!);

    /// <summary>Tarjan's strongly connected components, iterative, over the graph's nodes in ordinal order (edges to unknown nodes ignored).</summary>
    private static List<List<string>> StronglyConnected(Dictionary<string, List<string>> edges)
    {
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        var low = new Dictionary<string, int>(StringComparer.Ordinal);
        var onStack = new HashSet<string>(StringComparer.Ordinal);
        var stack = new Stack<string>();
        var result = new List<List<string>>();
        var counter = 0;
        foreach (var root in edges.Keys.Order(StringComparer.Ordinal))
        {
            if (index.ContainsKey(root))
                continue;
            var work = new Stack<(string Node, int Next)>();
            work.Push((root, 0));
            index[root] = low[root] = counter++;
            stack.Push(root);
            onStack.Add(root);
            while (work.Count > 0)
            {
                var (node, next) = work.Pop();
                var targets = edges[node];
                if (next < targets.Count)
                {
                    work.Push((node, next + 1));
                    var target = targets[next];
                    if (!edges.ContainsKey(target))
                        continue;
                    if (!index.ContainsKey(target))
                    {
                        index[target] = low[target] = counter++;
                        stack.Push(target);
                        onStack.Add(target);
                        work.Push((target, 0));
                    }
                    else if (onStack.Contains(target))
                    {
                        low[node] = Math.Min(low[node], index[target]);
                    }

                    continue;
                }

                if (work.Count > 0)
                {
                    var parent = work.Peek().Node;
                    low[parent] = Math.Min(low[parent], low[node]);
                }

                if (low[node] == index[node])
                {
                    var component = new List<string>();
                    string member;
                    do
                    {
                        member = stack.Pop();
                        onStack.Remove(member);
                        component.Add(member);
                    }
                    while (member != node);
                    result.Add(component);
                }
            }
        }

        return result;
    }
}
