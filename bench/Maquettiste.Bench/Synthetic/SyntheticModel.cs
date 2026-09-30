using System.Globalization;
using Maquettiste.Engine.Model;

namespace Maquettiste.Bench.Synthetic;

/// <summary>
/// The synthetic model as element records, built in memory from <see cref="SyntheticModelOptions"/> (engine-design.md section 17):
/// packages, entities (8 to 20 attributes, 10% in two-level hierarchies, <c>audited</c> and <c>soft-delete</c> on 30%), relations
/// (70% one to many, 15% many to many, 10% with attributes, 5% one to one), enums, value objects, scalar types, three databases
/// and mappings. Pure and deterministic: the same options give the same records in the same order.
/// </summary>
internal sealed partial class SyntheticModel
{
    private const double HierarchyShare = 0.10;
    private const double StereotypeShare = 0.30;

    private readonly SyntheticModelOptions _options;
    private readonly SeededRandom _random;
    private readonly SyntheticIds _ids;
    private readonly List<Element> _elements = [];
    private readonly List<Package> _packages = [];
    private readonly List<EnumType> _enums = [];
    private readonly List<ValueObject> _valueObjects = [];
    private readonly List<ScalarType> _scalars = [];
    private readonly List<Entity> _entities = [];
    private readonly List<int> _entityPackage = [];
    private readonly List<int> _hierarchyRoot = [];
    private readonly Dictionary<int, HashSet<string>> _navigationNames = [];
    private readonly HashSet<int> _compositionChildren = [];

    private SyntheticModel(SyntheticModelOptions options)
    {
        _options = options;
        _random = new SeededRandom(0x5EED_0000_0000_0000UL ^ (ulong)(uint)options.Seed);
        _ids = new SyntheticIds(options.Seed);
    }

    /// <summary>Every element, in generation order.</summary>
    public IReadOnlyList<Element> Elements => _elements;

    /// <summary>The entities, in generation order.</summary>
    public IReadOnlyList<Entity> Entities => _entities;

    /// <summary>The project settings.</summary>
    public ProjectSettings Settings { get; private set; } = new() { FormatVersion = 1 };

    /// <summary>The entity the incremental run edits: the middle entity, which is not part of a hierarchy.</summary>
    public Entity EditTarget { get; private set; } = null!;

    /// <summary>Builds the model.</summary>
    /// <param name="options">The options.</param>
    /// <returns>The model.</returns>
    public static SyntheticModel Build(SyntheticModelOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        Validate(options);
        var model = new SyntheticModel(options);
        model.BuildAll();
        return model;
    }

    /// <summary>Returns the edited copy of <see cref="EditTarget"/>: its first non-key attribute gets a description and flips <c>required</c>.</summary>
    /// <returns>The edited entity.</returns>
    public Entity EditedTarget()
    {
        var entity = EditTarget;
        var key = entity.Key?.Attributes ?? [];
        var index = entity.Attributes.Select((a, i) => (a, i)).First(x => !key.Contains(x.a.Id, StringComparer.Ordinal)).i;
        var attributes = entity.Attributes.ToArray();
        attributes[index] = attributes[index] with
        {
            Description = new Description { Text = "Edited by the benchmark's incremental run." },
            Required = !attributes[index].Required,
        };
        return entity with { Attributes = attributes };
    }

    /// <summary>Checks the options: at least 1 package, 2 entities, 1 enum, 1 value object and 1 scalar type, a fanout of at least 1,
    /// and at most one relation per distinct entity pair.</summary>
    /// <param name="o">The options.</param>
    /// <exception cref="ArgumentException">The options describe no valid model.</exception>
    public static void Validate(SyntheticModelOptions o)
    {
        ArgumentNullException.ThrowIfNull(o);
        if (o.Packages < 1 || o.Entities < 2 || o.Relations < 0 || o.Enums < 1 || o.ValueObjects < 1 || o.ScalarTypes < 1)
            throw new ArgumentException("The synthetic model needs at least 1 package, 2 entities, 1 enum, 1 value object and 1 scalar type.");
        if (o.Fanout is < 1)
            throw new ArgumentException("The fanout must be at least 1 file per entity.");
        if (o.Locales is < 0 or > LocaleShards.MaxLocales)
            throw new ArgumentException(string.Create(CultureInfo.InvariantCulture, $"--locales takes 0 to {LocaleShards.MaxLocales} translated locales."));
        ValidateScaleShape(o);
        var pairs = Pairs(o.Entities);
        if (o.Relations > pairs)
            throw new ArgumentException(string.Create(CultureInfo.InvariantCulture,
                $"{o.Relations} relations are too many for {o.Entities} entities (at most {pairs}, one per entity pair)."));
    }

    private static long Pairs(int entities) => (long)entities * (entities - 1) / 2;

    private void BuildAll()
    {
        BuildStereotypes();
        BuildPackages();
        BuildScalars();
        BuildEnums();
        BuildValueObjects();
        BuildEntities();
        BuildRelations();
        BuildDatabasesAndMappings();
        BuildScaleShape();
        EditTarget = _entities[MiddleStandalone()];
        Settings = BuildSettings();
    }

    private int MiddleStandalone()
    {
        for (var i = _entities.Count / 2; i < _entities.Count; i++)
        {
            if (_hierarchyRoot[i] < 0)
                return i;
        }

        return 0;
    }

    private void BuildStereotypes()
    {
        _elements.Add(new Stereotype
        {
            Id = _ids.Next(),
            Key = "audited",
            Name = "Audited",
            AppliesTo = ["entity"],
            Attributes =
            [
                new ModelAttribute { Id = _ids.Next(), Name = "createdAt", Type = Builtin("datetimeoffset"), Required = true, ReadOnly = true },
                new ModelAttribute { Id = _ids.Next(), Name = "updatedAt", Type = Builtin("datetimeoffset"), ReadOnly = true },
            ],
        });
        _elements.Add(new Stereotype
        {
            Id = _ids.Next(),
            Key = "soft-delete",
            Name = "Soft delete",
            AppliesTo = ["entity"],
            Attributes =
            [
                new ModelAttribute { Id = _ids.Next(), Name = "deletedAt", Type = Builtin("datetimeoffset") },
                new ModelAttribute { Id = _ids.Next(), Name = "isDeleted", Type = Builtin("bool"), Required = true },
            ],
        });
    }

    private void BuildPackages()
    {
        for (var i = 0; i < _options.Packages; i++)
        {
            var words = Vocabulary.Packages;
            var name = i < words.Length ? words[i] : words[i % words.Length] + (i / words.Length + 1).ToString(CultureInfo.InvariantCulture);
            var parent = ParentPackage(i);
            var package = new Package
            {
                Id = _ids.Next(),
                Name = name,
                Parent = parent < 0 ? null : _packages[parent].Id,
                Description = new Description { Text = "Synthetic package " + name + "." },
            };
            _packages.Add(package);
            _elements.Add(package);
        }
    }

    private void BuildScalars()
    {
        for (var i = 0; i < _options.ScalarTypes; i++)
        {
            var name = Numbered(Vocabulary.Nouns, i, "Code");
            var scalar = new ScalarType
            {
                Id = _ids.Next(),
                Name = name,
                Package = _packages[i % _packages.Count].Id,
                Base = "string",
                Length = 16 + (i % 5) * 16,
            };
            _scalars.Add(scalar);
            _elements.Add(scalar);
        }
    }

    private void BuildEnums()
    {
        for (var i = 0; i < _options.Enums; i++)
        {
            var name = Combined(i, "Kind");
            var count = _random.Between(3, 8);
            var start = _random.Next(Vocabulary.EnumMembers.Length - count + 1);
            var members = new List<EnumMember>(count);
            for (var m = 0; m < count; m++)
            {
                var word = Vocabulary.EnumMembers[start + m];
                members.Add(new EnumMember { Id = _ids.Next(), Name = word, Value = m, Code = word[..3].ToUpperInvariant() });
            }

            var enumType = new EnumType { Id = _ids.Next(), Name = name, Package = _packages[i % _packages.Count].Id, Members = members };
            _enums.Add(enumType);
            _elements.Add(enumType);
        }
    }

    private void BuildValueObjects()
    {
        for (var i = 0; i < _options.ValueObjects; i++)
        {
            var name = Numbered(Vocabulary.Nouns, i, "Spec");
            var count = _random.Between(2, 4);
            var start = _random.Next(Vocabulary.Members.Length - count + 1);
            var attributes = new List<ModelAttribute>(count);
            for (var m = 0; m < count; m++)
            {
                var (word, family) = Vocabulary.Members[start + m];
                attributes.Add(Attribute(word, family, required: m == 0));
            }

            var valueObject = new ValueObject { Id = _ids.Next(), Name = name, Package = _packages[i % _packages.Count].Id, Attributes = attributes };
            _valueObjects.Add(valueObject);
            _elements.Add(valueObject);
        }
    }

    private void BuildEntities()
    {
        var total = _options.Entities;
        var packages = _packages.Count;
        for (var i = 0; i < total; i++)
        {
            // Entities are spread evenly: package p holds the entities whose index falls in its share.
            var package = (int)((long)i * packages / total);
            var first = (int)(((long)package * total + packages - 1) / packages);
            var last = (int)(((long)(package + 1) * total + packages - 1) / packages);
            var local = i - first;
            var hierarchyCount = (int)Math.Round((last - first) * HierarchyShare, MidpointRounding.AwayFromZero);
            var root = local < hierarchyCount ? first + local - local % 3 : -1;
            _entityPackage.Add(package);
            _hierarchyRoot.Add(root);
            _entities.Add(BuildEntity(i, package, root));
            _elements.Add(_entities[^1]);
        }
    }

    private Entity BuildEntity(int index, int package, int root)
    {
        var derived = root >= 0 && root != index;
        var inherited = derived ? new HashSet<string>(_entities[root].Attributes.Select(a => a.Name), StringComparer.Ordinal) : [];
        var count = _random.Between(8, 20);
        var pool = Vocabulary.Attributes.Where(a => !inherited.Contains(a.Name)).ToList();
        var attributes = new List<ModelAttribute>(count + 1);
        EntityKey? key = null;
        if (!derived)
        {
            var identity = _random.Chance(0.3);
            var id = new ModelAttribute { Id = _ids.Next(), Name = "id", Type = Builtin(identity ? "int64" : "uuid"), Required = true };
            attributes.Add(id);
            key = new EntityKey { Attributes = [id.Id], Strategy = identity ? IdentityStrategy.DatabaseIdentity : IdentityStrategy.UuidV7 };
            count--;
        }

        for (var a = 0; a < count && pool.Count > 0; a++)
        {
            var pick = _random.Next(pool.Count);
            var (word, family) = pool[pick];
            pool.RemoveAt(pick);
            attributes.Add(Attribute(word, family, required: _random.Chance(0.4)));
        }

        var stereotypes = new List<string>();
        if (root < 0 && _random.Chance(StereotypeShare))
        {
            stereotypes.Add("audited");
            if (_random.Chance(0.5))
                stereotypes.Add("soft-delete");
        }

        return new Entity
        {
            Id = _ids.Next(),
            Name = Combined(index, ""),
            Package = _packages[package].Id,
            Base = derived ? _entities[root].Id : null,
            Key = key,
            Attributes = attributes,
            Stereotypes = stereotypes,
            Tags = _random.Chance(0.2) ? ["team:" + _packages[package].Name.ToLowerInvariant()] : [],
            Description = _random.Chance(0.5) ? new Description { Text = "Synthetic entity number " + index.ToString(CultureInfo.InvariantCulture) + "." } : null,
        };
    }

    private ModelAttribute Attribute(string name, AttributeFamily family, bool required)
    {
        var attribute = new ModelAttribute { Id = _ids.Next(), Name = name, Type = Builtin("string"), Required = required };
        return family switch
        {
            AttributeFamily.ShortString => attribute with { Length = 40 + _random.Next(5) * 40 },
            AttributeFamily.Code => attribute with { Length = 3 + _random.Next(14), Unique = name == "code" },
            AttributeFamily.Text => attribute with { Type = Builtin("text") },
            AttributeFamily.Money => attribute with { Type = Builtin("decimal"), Precision = 18, Scale = 2 },
            AttributeFamily.Integer => attribute with { Type = Builtin("int32") },
            AttributeFamily.Long => attribute with { Type = Builtin("int64") },
            AttributeFamily.Real => attribute with { Type = Builtin("double") },
            AttributeFamily.Flag => attribute with { Type = Builtin("bool") },
            AttributeFamily.Date => attribute with { Type = Builtin("date"), Indexed = _random.Chance(0.3) },
            AttributeFamily.Instant => attribute with { Type = Builtin("datetimeoffset") },
            AttributeFamily.Uuid => attribute with { Type = Builtin("uuid"), Indexed = true },
            AttributeFamily.Enum => attribute with { Type = new TypeRef { Ref = _enums[_random.Next(_enums.Count)].Id } },
            AttributeFamily.ValueObject => attribute with { Type = new TypeRef { Ref = _valueObjects[_random.Next(_valueObjects.Count)].Id } },
            AttributeFamily.Scalar => attribute with { Type = new TypeRef { Ref = _scalars[_random.Next(_scalars.Count)].Id } },
            _ => attribute,
        };
    }

    private void BuildRelations()
    {
        var pairs = new HashSet<(int, int)>();
        var packages = _packages.Count;

        // Rejection sampling slows down as the pairs run out, so a dense model (more than half the pairs related) draws a seeded
        // shuffle of every pair instead. The default and test models are sparse and keep the sampled, package-local shape.
        var dense = _options.Relations > Pairs(_entities.Count) / 2 ? DenseShuffle() : null;
        for (var r = 0; r < _options.Relations; r++)
        {
            var (a, b) = dense is null ? PickPair(pairs, packages) : dense[r];
            var draw = _random.NextDouble();
            var shape = draw < 0.70 ? Shape.OneToMany : draw < 0.85 ? Shape.ManyToMany : draw < 0.95 ? Shape.WithAttributes : Shape.OneToOne;
            _elements.Add(BuildRelation(a, b, shape));
        }
    }

    /// <summary>Every distinct entity pair in index order, with its first <see cref="SyntheticModelOptions.Relations"/> entries
    /// replaced by a seeded partial Fisher-Yates shuffle and each drawn pair oriented at random.</summary>
    private List<(int A, int B)> DenseShuffle()
    {
        var total = _entities.Count;
        var all = new List<(int A, int B)>((int)Pairs(total));
        for (var a = 0; a < total; a++)
        {
            for (var b = a + 1; b < total; b++)
                all.Add((a, b));
        }

        for (var r = 0; r < _options.Relations; r++)
        {
            var j = r + _random.Next(all.Count - r);
            (all[r], all[j]) = (all[j], all[r]);
            if (_random.Chance(0.5))
                all[r] = (all[r].B, all[r].A);
        }

        return all;
    }

    private (int A, int B) PickPair(HashSet<(int, int)> pairs, int packages)
    {
        var total = _entities.Count;
        while (true)
        {
            var a = _random.Next(total);
            int b;
            if (packages > 1 && _random.Chance(0.4))
            {
                b = _random.Next(total);
            }
            else
            {
                // Most relations stay inside a package, as in real models.
                var package = _entityPackage[a];
                var first = (int)(((long)package * total + packages - 1) / packages);
                var last = (int)(((long)(package + 1) * total + packages - 1) / packages);
                b = first + _random.Next(last - first);
            }

            if (a == b)
                continue;
            var pair = a < b ? (a, b) : (b, a);
            if (pairs.Add(pair))
                return (a, b);
        }
    }

    private Relation BuildRelation(int a, int b, Shape shape)
    {
        var ea = _entities[a];
        var eb = _entities[b];
        var (maxA, minA, maxB, minB) = shape switch
        {
            Shape.OneToMany => (MaxCardinality.One, _random.Chance(0.5) ? 1 : 0, MaxCardinality.Many, 0),
            Shape.OneToOne => (MaxCardinality.One, 1, MaxCardinality.One, 0),
            _ => (MaxCardinality.Many, 0, MaxCardinality.Many, 0),
        };

        // End a's navigation is a property of b (and the reverse); names are unique per hierarchy.
        var navigationOnB = Navigation(b, Camel(ea.Name) + (maxA == MaxCardinality.Many ? "List" : ""));
        var navigationOnA = _random.Chance(0.7) ? Navigation(a, Camel(eb.Name) + (maxB == MaxCardinality.Many ? "List" : "")) : "";
        var attributes = shape == Shape.WithAttributes
            ? new List<ModelAttribute>
            {
                new() { Id = _ids.Next(), Name = "since", Type = Builtin("date"), Required = true },
                new() { Id = _ids.Next(), Name = "weight", Type = Builtin("int32") },
            }
            : [];

        return new Relation
        {
            Id = _ids.Next(),
            Name = ea.Name + " " + Vocabulary.Verbs[_random.Next(Vocabulary.Verbs.Length)] + " " + eb.Name,
            Package = ea.Package,
            RelationKind = Composition(a, b, shape) ? RelationKind.Composition : RelationKind.Association,
            Ends =
            [
                new RelationEnd { Id = _ids.Next(), Entity = ea.Id, Role = Camel(ea.Name), Navigation = navigationOnB, Min = minA, Max = maxA },
                new RelationEnd { Id = _ids.Next(), Entity = eb.Id, Role = Camel(eb.Name), Navigation = navigationOnA, Min = minB, Max = maxB },
            ],
            Attributes = attributes,
        };
    }

    /// <summary>One one-to-many relation in five is a composition, when the child has no owner yet (MQ3016) and the owner comes
    /// first in generation order, so ownership never forms a cycle.</summary>
    private bool Composition(int owner, int child, Shape shape) =>
        shape == Shape.OneToMany && _random.Chance(0.2) && owner < child && _compositionChildren.Add(child);

    private string Navigation(int entity, string name)
    {
        var root = _hierarchyRoot[entity] >= 0 ? _hierarchyRoot[entity] : entity;
        if (!_navigationNames.TryGetValue(root, out var used))
            _navigationNames[root] = used = new HashSet<string>(StringComparer.Ordinal);
        var candidate = name;
        for (var n = 2; !used.Add(candidate); n++)
            candidate = name + n.ToString(CultureInfo.InvariantCulture);
        return candidate;
    }

    private void BuildDatabasesAndMappings()
    {
        var packages = _packages.Count;
        var main = new Database { Id = _ids.Next(), Name = "main", Dialect = Dialect.PostgreSql, Version = "16" };
        main = main with { Schemas = SchemasFor(main.Dialect), DefaultSchema = _options.Schemas > 0 ? SchemaNames[0] : null };
        var reporting = new Database
        {
            Id = _ids.Next(),
            Name = "reporting",
            Dialect = Dialect.SqlServer,
            Version = "2022",
            Packages = [.. _packages.Take(Math.Max(1, packages / 5)).Select(p => p.Id)],
        };
        reporting = reporting with { Schemas = SchemasFor(reporting.Dialect), DefaultSchema = _options.Schemas > 0 ? SchemaNames[0] : null };
        var edge = new Database
        {
            Id = _ids.Next(),
            Name = "edge",
            Dialect = Dialect.Sqlite,
            Packages = [.. _packages.Take(Math.Max(1, packages / 25)).Select(p => p.Id)],
        };
        _elements.Add(main);
        _elements.Add(reporting);
        _elements.Add(edge);

        var reportingPackages = new HashSet<string>(reporting.Packages.Select(p => p.Package), StringComparer.Ordinal);
        var enumIds = new HashSet<string>(_enums.Select(e => e.Id), StringComparer.Ordinal);
        var valueObjectIds = new HashSet<string>(_valueObjects.Select(v => v.Id), StringComparer.Ordinal);
        for (var i = 0; i < _entities.Count; i++)
        {
            var entity = _entities[i];
            if (i % 10 == 3 && entity.Attributes.FirstOrDefault(a => a.Type.Ref is { } r && enumIds.Contains(r)) is { } enumAttribute)
            {
                // Enums stored by their code in PostgreSQL for one entity in ten.
                _elements.Add(new Mapping
                {
                    Id = _ids.Next(),
                    Name = entity.Name + " in main",
                    Database = main.Id,
                    Entity = entity.Id,
                    Attributes = [new AttributeMapping { Attribute = enumAttribute.Id, Storage = StorageKind.String }],
                });
            }

            if (i % 5 == 1 && reportingPackages.Contains(entity.Package!)
                && entity.Attributes.FirstOrDefault(a => a.Type.Ref is { } r && valueObjectIds.Contains(r)) is { } voAttribute)
            {
                // Value objects stored as JSON in SQL Server for some reporting entities.
                _elements.Add(new Mapping
                {
                    Id = _ids.Next(),
                    Name = entity.Name + " in reporting",
                    Database = reporting.Id,
                    Entity = entity.Id,
                    Attributes = [new AttributeMapping { Attribute = voAttribute.Id, Storage = StorageKind.Json }],
                });
            }
        }
    }

    private ProjectSettings BuildSettings() => new()
    {
        FormatVersion = 1,
        Name = "synthetic-bench",
        Outputs = new OutputSettings
        {
            Allow =
            [
                new OutputRoot { Path = "db", Commit = true },
                new OutputRoot { Path = "gen/built" },
                new OutputRoot { Path = "gen/committed", Commit = true },
                new OutputRoot { Path = "src/Generated" },
            ],
        },
        Conventions = new Conventions { Inheritance = InheritanceStrategy.Tpt },
        Packs = new SortedDictionary<string, PackSettings>(StringComparer.Ordinal)
        {
            ["csharp-dapper"] = new PackSettings { Output = "src/Generated" },
            ["fanout"] = new PackSettings { Output = "gen" },
            ["sql-ddl"] = new PackSettings { Output = "db" },
        },
        Localization = _options.Locales > 0
            ? new LocalizationSettings { DefaultLocale = LocaleShards.DefaultLocale, Locales = [LocaleShards.DefaultLocale, .. LocaleShards.Translated(_options.Locales)] }
            : null,
    };

    private static TypeRef Builtin(string keyword) => new() { Builtin = keyword };

    private static string Camel(string pascal) => char.ToLowerInvariant(pascal[0]) + pascal[1..];

    private static string Combined(int index, string suffix)
    {
        var q = Vocabulary.Qualifiers;
        var n = Vocabulary.Nouns;
        var combos = q.Length * n.Length;
        var slot = index % combos;
        var round = index / combos;
        return q[slot % q.Length] + n[slot / q.Length % n.Length] + suffix + (round > 0 ? (round + 1).ToString(CultureInfo.InvariantCulture) : "");
    }

    private static string Numbered(System.Collections.Immutable.ImmutableArray<string> words, int index, string suffix)
    {
        var round = index / words.Length;
        return words[index % words.Length] + suffix + (round > 0 ? (round + 1).ToString(CultureInfo.InvariantCulture) : "");
    }

    private enum Shape
    {
        OneToMany,
        ManyToMany,
        WithAttributes,
        OneToOne,
    }
}
