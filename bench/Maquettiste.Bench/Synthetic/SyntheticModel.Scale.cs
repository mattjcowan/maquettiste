using System.Globalization;
using Maquettiste.Engine.Model;

namespace Maquettiste.Bench.Synthetic;

/// <summary>
/// The explorer's scale shape (explorer-redesign.md section 5): nested packages, diagrams as subject areas, database schemas,
/// designed tables, views and sequences, and lookup-style entities. Every option defaults to off, and while it is off nothing here
/// draws an id or a random number, so the benchmark model stays byte-identical. The shape draws from its own random stream.
/// </summary>
internal sealed partial class SyntheticModel
{
    private static readonly string[] SchemaNames = ["app", "ref", "audit", "staging", "archive", "ops", "sales", "people"];

    private int[]? _packageParents;

    /// <summary>Checks the scale options.</summary>
    /// <param name="o">The options.</param>
    /// <exception cref="ArgumentException">An option is out of range.</exception>
    private static void ValidateScaleShape(SyntheticModelOptions o)
    {
        if (o.DomainDepth < 1 || o.DomainWidth < 1)
            throw new ArgumentException("The domain depth and width must be at least 1.");
        if (o.Diagrams < 0 || o.Schemas < 0 || o.DesignedTables < 0 || o.Views < 0 || o.Sequences < 0 || o.Lookups < 0)
            throw new ArgumentException("Diagrams, schemas, designed tables, views, sequences and lookups cannot be negative.");
        if (o.DiagramMinSize < 1 || o.DiagramMaxSize < o.DiagramMinSize)
            throw new ArgumentException("The diagram size range needs 1 <= min <= max.");
        if (o.LookupAttributes < 5)
            throw new ArgumentException("A lookup entity needs at least 5 attributes (id, code, name, sortOrder, isActive).");
    }

    /// <summary>
    /// The parent of package <paramref name="index"/>, or -1. Packages nest breadth first: enough roots to hold them all within
    /// the depth, then each level fills its parents in order with up to <c>DomainWidth</c> children each.
    /// </summary>
    private int ParentPackage(int index)
    {
        if (_options.DomainDepth <= 1)
            return -1;
        _packageParents ??= PackageParents(_options.Packages, _options.DomainDepth, _options.DomainWidth);
        return _packageParents[index];
    }

    private static int[] PackageParents(int count, int depth, int width)
    {
        long capacity = 0, level = 1;
        for (var d = 0; d < depth && capacity < count; d++)
        {
            capacity += level;
            level = Math.Min(level * width, int.MaxValue);
        }

        var roots = (int)Math.Max(1, (count + capacity - 1) / capacity);
        var parents = new int[count];
        var levels = new int[count];
        var children = new int[count];
        var cursor = 0;
        for (var i = 0; i < count; i++)
        {
            if (i < roots)
            {
                parents[i] = -1;
                continue;
            }

            while (levels[cursor] >= depth - 1 || children[cursor] >= width)
                cursor++;
            parents[i] = cursor;
            levels[i] = levels[cursor] + 1;
            children[cursor]++;
        }

        return parents;
    }

    /// <summary>The schemas of a database with the scale shape on, or none.</summary>
    private List<DbSchema> SchemasFor(Dialect dialect)
    {
        var schemas = new List<DbSchema>();
        if (dialect == Dialect.Sqlite)
            return schemas;
        for (var i = 0; i < _options.Schemas; i++)
            schemas.Add(new DbSchema { Id = _ids.Next(), Name = Numbered(SchemaNames, i) });
        return schemas;
    }

    private void BuildScaleShape()
    {
        var random = new SeededRandom(0x5CA1_E000_0000_0000UL ^ (ulong)(uint)_options.Seed);
        BuildLookups(random);
        BuildDesignedObjects(random);
        BuildDiagrams(random);
    }

    private void BuildLookups(SeededRandom random)
    {
        var reserved = new HashSet<string>(["id", "code", "name", "sortOrder", "isActive", "description"], StringComparer.Ordinal);
        var extraFamilies = new[] { AttributeFamily.ShortString, AttributeFamily.Integer, AttributeFamily.Flag, AttributeFamily.Date };
        for (var i = 0; i < _options.Lookups; i++)
        {
            var key = new ModelAttribute { Id = _ids.Next(), Name = "id", Type = Builtin("int32"), Required = true };
            var attributes = new List<ModelAttribute>(_options.LookupAttributes)
            {
                key,
                new() { Id = _ids.Next(), Name = "code", Type = Builtin("string"), Length = 16, Required = true, Unique = true },
                new() { Id = _ids.Next(), Name = "name", Type = Builtin("string"), Length = 120, Required = true },
                new() { Id = _ids.Next(), Name = "sortOrder", Type = Builtin("int32"), Required = true },
                new() { Id = _ids.Next(), Name = "isActive", Type = Builtin("bool"), Required = true },
            };
            foreach (var (word, family) in Vocabulary.Attributes)
            {
                if (attributes.Count >= _options.LookupAttributes)
                    break;
                if (!reserved.Contains(word))
                    attributes.Add(LookupAttribute(random, word, family));
            }

            for (var extra = 1; attributes.Count < _options.LookupAttributes; extra++)
            {
                var name = "extra" + extra.ToString("D2", CultureInfo.InvariantCulture);
                attributes.Add(LookupAttribute(random, name, extraFamilies[extra % extraFamilies.Length]));
            }

            var package = _packages[i % _packages.Count];
            var name2 = Numbered(Vocabulary.Nouns, i, "Lookup");
            _elements.Add(new Entity
            {
                Id = _ids.Next(),
                Name = name2,
                Package = package.Id,
                Key = new EntityKey { Attributes = [key.Id], Strategy = IdentityStrategy.DatabaseIdentity },
                Attributes = attributes,
                Tags = ["lookup"],
                Description = new Description { Text = "Reference data: " + name2 + " values for " + package.Name + "." },
            });
        }
    }

    private ModelAttribute LookupAttribute(SeededRandom random, string name, AttributeFamily family)
    {
        var attribute = new ModelAttribute { Id = _ids.Next(), Name = name, Type = Builtin("string"), Required = random.Chance(0.2) };
        return family switch
        {
            AttributeFamily.ShortString or AttributeFamily.Code => attribute with { Length = 20 + random.Next(10) * 20 },
            AttributeFamily.Text => attribute with { Type = Builtin("text") },
            AttributeFamily.Money => attribute with { Type = Builtin("decimal"), Precision = 18, Scale = 2 },
            AttributeFamily.Integer => attribute with { Type = Builtin("int32") },
            AttributeFamily.Long => attribute with { Type = Builtin("int64") },
            AttributeFamily.Real => attribute with { Type = Builtin("double") },
            AttributeFamily.Flag => attribute with { Type = Builtin("bool") },
            AttributeFamily.Date => attribute with { Type = Builtin("date") },
            _ => attribute with { Length = 64 },
        };
    }

    private void BuildDesignedObjects(SeededRandom random)
    {
        // SQLite has no schemas or sequences, so designed objects go to the server databases.
        var databases = _elements.OfType<Database>().Where(d => d.Dialect != Dialect.Sqlite).ToList();
        if (databases.Count == 0)
            return;
        var tables = databases.ToDictionary(d => d.Id, _ => new List<Table>(), StringComparer.Ordinal);
        for (var i = 0; i < _options.DesignedTables; i++)
        {
            var database = databases[i % databases.Count];
            var own = tables[database.Id];
            var table = DesignedTable(random, database, i, own.Count > 0 && i % 2 == 1 ? own[random.Next(own.Count)] : null);
            own.Add(table);
            _elements.Add(table);
        }

        for (var i = 0; i < _options.Views; i++)
        {
            var database = databases[i % databases.Count];
            var own = tables[database.Id];
            var source = own.Count > 0 ? own[i / databases.Count % own.Count] : null;
            var schema = SchemaOf(database, i);
            var from = source is null ? "(select 1 as id, 'none' as name) t" : Qualified(database, source.Schema, source.Name);
            _elements.Add(new View
            {
                Id = _ids.Next(),
                Name = "v_" + Snake(Numbered(Vocabulary.Nouns, i, "")) + "_summary",
                Database = database.Id,
                Schema = schema?.Id,
                Body = new SortedDictionary<string, string>(StringComparer.Ordinal) { ["*"] = "select id, name from " + from },
                Columns =
                [
                    new ViewColumn { Name = "id", Type = "int64", Nullable = false },
                    new ViewColumn { Name = "name", Type = "string" },
                ],
            });
        }

        for (var i = 0; i < _options.Sequences; i++)
        {
            var database = databases[i % databases.Count];
            _elements.Add(new Sequence
            {
                Id = _ids.Next(),
                Name = Snake(Numbered(Vocabulary.Nouns, i, "")) + "_number_seq",
                Database = database.Id,
                Schema = SchemaOf(database, i)?.Id,
                Start = 1_000 * (1 + random.Next(9)),
            });
        }
    }

    private Table DesignedTable(SeededRandom random, Database database, int index, Table? parent)
    {
        var id = new Column { Id = _ids.Next(), Name = "id", Type = "int64", Nullable = false };
        var columns = new List<Column>
        {
            id,
            new() { Id = _ids.Next(), Name = "code", Type = "string", Length = 32, Nullable = false },
            new() { Id = _ids.Next(), Name = "name", Type = "string", Length = 200, Nullable = false },
        };
        var extra = random.Between(2, 8);
        for (var c = 0; c < extra; c++)
        {
            var (word, family) = Vocabulary.Attributes[(index + c * 7) % Vocabulary.Attributes.Length];
            var column = Snake(word) + "_" + c.ToString(CultureInfo.InvariantCulture);
            columns.Add(family switch
            {
                AttributeFamily.Money => new Column { Id = _ids.Next(), Name = column, Type = "decimal", Precision = 18, Scale = 2 },
                AttributeFamily.Integer or AttributeFamily.Long => new Column { Id = _ids.Next(), Name = column, Type = "int64" },
                AttributeFamily.Flag => new Column { Id = _ids.Next(), Name = column, Type = "bool", Nullable = false },
                AttributeFamily.Date => new Column { Id = _ids.Next(), Name = column, Type = "date" },
                _ => new Column { Id = _ids.Next(), Name = column, Type = "string", Length = 100 },
            });
        }

        var foreignKeys = new List<ForeignKey>();
        if (parent is not null)
        {
            var parentId = new Column { Id = _ids.Next(), Name = "parent_id", Type = "int64" };
            columns.Add(parentId);
            foreignKeys.Add(new ForeignKey
            {
                Id = _ids.Next(),
                Columns = [parentId.Id],
                ReferencesTable = parent.Id,
                ReferencesColumns = [parent.Columns[0].Id],
            });
        }

        return new Table
        {
            Id = _ids.Next(),
            Name = "ref_" + Snake(Numbered(Vocabulary.Nouns, index, "")),
            Database = database.Id,
            Schema = SchemaOf(database, index)?.Id,
            Columns = columns,
            PrimaryKey = new PrimaryKey { Columns = [id.Id] },
            Uniques = [new UniqueConstraint { Id = _ids.Next(), Columns = [columns[1].Id] }],
            ForeignKeys = foreignKeys,
            Comment = "Designed table " + index.ToString(CultureInfo.InvariantCulture) + ", owned by no entity.",
        };
    }

    private static DbSchema? SchemaOf(Database database, int index) =>
        database.Schemas.Count == 0 ? null : database.Schemas[index % database.Schemas.Count];

    private static string Qualified(Database database, string? schemaId, string table) =>
        database.Schemas.FirstOrDefault(s => s.Id == schemaId) is { } schema ? schema.Name + "." + table : table;

    private void BuildDiagrams(SeededRandom random)
    {
        if (_options.Diagrams == 0)
            return;
        var entityIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < _entities.Count; i++)
            entityIndex[_entities[i].Id] = i;
        var relations = _elements.OfType<Relation>().ToList();
        var adjacency = new List<int>[_entities.Count];
        for (var i = 0; i < adjacency.Length; i++)
            adjacency[i] = [];
        foreach (var relation in relations)
        {
            var a = entityIndex[relation.Ends[0].Entity];
            var b = entityIndex[relation.Ends[1].Entity];
            adjacency[a].Add(b);
            adjacency[b].Add(a);
        }

        var byPackage = new List<int>[_packages.Count];
        for (var p = 0; p < byPackage.Length; p++)
            byPackage[p] = [];
        for (var i = 0; i < _entities.Count; i++)
            byPackage[_entityPackage[i]].Add(i);

        var max = Math.Min(_options.DiagramMaxSize, _entities.Count);
        var min = Math.Min(_options.DiagramMinSize, max);
        for (var d = 0; d < _options.Diagrams; d++)
        {
            var package = d % _packages.Count;
            var round = d / _packages.Count;
            var chosen = ChooseEntities(byPackage, adjacency, package, random.Between(min, max));
            var set = new HashSet<string>(chosen.Select(i => _entities[i].Id), StringComparer.Ordinal);
            var columns = (int)Math.Ceiling(Math.Sqrt(chosen.Count));
            var members = new List<DiagramMember>(chosen.Count * 2);
            for (var k = 0; k < chosen.Count; k++)
                members.Add(new DiagramMember { Element = _entities[chosen[k]].Id, X = 40 + k % columns * 320, Y = 40 + k / columns * 240 });
            foreach (var relation in relations)
            {
                if (set.Contains(relation.Ends[0].Entity) && set.Contains(relation.Ends[1].Entity))
                    members.Add(new DiagramMember { Element = relation.Id });
            }

            var name = _packages[package].Name + (round == 0 ? " overview" : " area " + (round + 1).ToString(CultureInfo.InvariantCulture));
            _elements.Add(new Diagram { Id = _ids.Next(), Name = name, Package = _packages[package].Id, Members = members });
        }
    }

    /// <summary>The package's own entities first, then their neighbours breadth first, then the next packages' entities.</summary>
    private List<int> ChooseEntities(List<int>[] byPackage, List<int>[] adjacency, int package, int size)
    {
        var chosen = new List<int>(size);
        var seen = new HashSet<int>();
        foreach (var i in byPackage[package])
        {
            if (chosen.Count == size)
                return chosen;
            if (seen.Add(i))
                chosen.Add(i);
        }

        for (var head = 0; head < chosen.Count && chosen.Count < size; head++)
        {
            foreach (var next in adjacency[chosen[head]])
            {
                if (chosen.Count == size)
                    break;
                if (seen.Add(next))
                    chosen.Add(next);
            }
        }

        for (var p = 1; p < byPackage.Length && chosen.Count < size; p++)
        {
            foreach (var i in byPackage[(package + p) % byPackage.Length])
            {
                if (chosen.Count == size)
                    break;
                if (seen.Add(i))
                    chosen.Add(i);
            }
        }

        return chosen;
    }

    private static string Numbered(string[] words, int index) =>
        words[index % words.Length] + (index / words.Length > 0 ? (index / words.Length + 1).ToString(CultureInfo.InvariantCulture) : "");

    private static string Snake(string pascal)
    {
        var builder = new System.Text.StringBuilder(pascal.Length + 4);
        for (var i = 0; i < pascal.Length; i++)
        {
            var c = pascal[i];
            if (char.IsUpper(c))
            {
                if (i > 0)
                    builder.Append('_');
                builder.Append(char.ToLowerInvariant(c));
            }
            else
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }
}
