using System.Collections.Immutable;
using System.Text.Json;
using Maquettiste.Engine.Model;
using ModelDescription = Maquettiste.Engine.Model.Description;

namespace Maquettiste.Testing;

/// <summary>Builds a package.</summary>
public sealed class PackageBuilder : ElementBuilder<PackageBuilder>
{
    private readonly string? _parent;

    internal PackageBuilder(ModelBuilder model, string name, string? parent)
        : base(model, name) => _parent = parent;

    /// <inheritdoc/>
    protected override Element CreateElement() => new Package { Id = Id, Name = Name, Parent = _parent };
}

/// <summary>Builds one attribute.</summary>
public sealed class AttributeBuilder
{
    private ModelAttribute _attribute;

    internal AttributeBuilder(string id, string name, TypeRef type) =>
        _attribute = new ModelAttribute { Id = id, Name = name, Type = type };

    /// <summary>The attribute id.</summary>
    public string Id => _attribute.Id;

    /// <summary>Sets the length facet.</summary>
    /// <param name="length">The length.</param>
    /// <returns>This builder.</returns>
    public AttributeBuilder Length(int length) => Set(_attribute with { Length = length });

    /// <summary>Sets the precision facet.</summary>
    /// <param name="precision">The precision.</param>
    /// <returns>This builder.</returns>
    public AttributeBuilder Precision(int precision) => Set(_attribute with { Precision = precision });

    /// <summary>Sets the scale facet.</summary>
    /// <param name="scale">The scale.</param>
    /// <returns>This builder.</returns>
    public AttributeBuilder Scale(int scale) => Set(_attribute with { Scale = scale });

    /// <summary>Marks the attribute required.</summary>
    /// <param name="required">Whether it is required.</param>
    /// <returns>This builder.</returns>
    public AttributeBuilder Required(bool required = true) => Set(_attribute with { Required = required });

    /// <summary>Marks the attribute unique.</summary>
    /// <returns>This builder.</returns>
    public AttributeBuilder Unique() => Set(_attribute with { Unique = true });

    /// <summary>Marks the attribute indexed.</summary>
    /// <returns>This builder.</returns>
    public AttributeBuilder Indexed() => Set(_attribute with { Indexed = true });

    /// <summary>Marks the attribute read-only.</summary>
    /// <returns>This builder.</returns>
    public AttributeBuilder ReadOnly() => Set(_attribute with { ReadOnly = true });

    /// <summary>Marks the attribute immutable.</summary>
    /// <returns>This builder.</returns>
    public AttributeBuilder Immutable() => Set(_attribute with { Immutable = true });

    /// <summary>Makes the attribute a collection.</summary>
    /// <returns>This builder.</returns>
    public AttributeBuilder Collection() => Set(_attribute with { Collection = true });

    /// <summary>Sets a literal default.</summary>
    /// <param name="value">A JSON-serializable value.</param>
    /// <returns>This builder.</returns>
    public AttributeBuilder Default(object? value) => Set(_attribute with { Default = ModelBuilder.ToJson(value) });

    /// <summary>Sets a named default expression.</summary>
    /// <param name="expression">For example <c>now</c>.</param>
    /// <returns>This builder.</returns>
    public AttributeBuilder DefaultExpression(string expression) => Set(_attribute with { DefaultExpression = expression });

    /// <summary>Marks the attribute sensitive.</summary>
    /// <param name="sensitivity">The sensitivity.</param>
    /// <returns>This builder.</returns>
    public AttributeBuilder Sensitive(Sensitivity sensitivity = Sensitivity.Pii) => Set(_attribute with { Sensitive = sensitivity });

    /// <summary>Sets the order adjustment.</summary>
    /// <param name="order">The order.</param>
    /// <returns>This builder.</returns>
    public AttributeBuilder Order(int order) => Set(_attribute with { Order = order });

    /// <summary>Makes the attribute derived.</summary>
    /// <param name="expression">The expression.</param>
    /// <param name="stored">Whether it is stored.</param>
    /// <returns>This builder.</returns>
    public AttributeBuilder Derived(string expression, bool stored = false) => Set(_attribute with { Derived = new DerivedSpec { Expression = expression, Stored = stored } });

    /// <summary>Sets validation constraints.</summary>
    /// <param name="validation">The constraints.</param>
    /// <returns>This builder.</returns>
    public AttributeBuilder Validation(AttributeValidation validation) => Set(_attribute with { Validation = validation });

    /// <summary>Applies a stereotype.</summary>
    /// <param name="key">The stereotype key.</param>
    /// <returns>This builder.</returns>
    public AttributeBuilder Stereotype(string key) => Set(_attribute with { Stereotypes = [.. _attribute.Stereotypes, key] });

    /// <summary>Adds a tag.</summary>
    /// <param name="key">The tag key.</param>
    /// <returns>This builder.</returns>
    public AttributeBuilder Tag(string key) => Set(_attribute with { Tags = [.. _attribute.Tags, key] });

    /// <summary>Sets a custom property.</summary>
    /// <param name="key">The property name.</param>
    /// <param name="value">A JSON-serializable value.</param>
    /// <returns>This builder.</returns>
    public AttributeBuilder Property(string key, object? value) =>
        Set(_attribute with { Properties = _attribute.Properties.ToImmutableDictionary(StringComparer.Ordinal).SetItem(key, ModelBuilder.ToJson(value)) });

    /// <summary>Sets an inline description.</summary>
    /// <param name="text">The Markdown text.</param>
    /// <returns>This builder.</returns>
    public AttributeBuilder Description(string text) => Set(_attribute with { Description = new ModelDescription { Text = text } });

    internal ModelAttribute Build() => _attribute;

    private AttributeBuilder Set(ModelAttribute attribute)
    {
        _attribute = attribute;
        return this;
    }
}

/// <summary>An ordered attribute list shared by entities, value objects, relations and stereotypes.</summary>
internal sealed class AttributeList(ModelBuilder model)
{
    private readonly List<AttributeBuilder> _attributes = [];

    public AttributeBuilder Add(string name, TypeRef type, Action<AttributeBuilder>? configure)
    {
        var attribute = new AttributeBuilder(model.NewId(), name, type);
        configure?.Invoke(attribute);
        _attributes.Add(attribute);
        return attribute;
    }

    public string IdOf(string name)
    {
        foreach (var a in _attributes)
        {
            if (a.Build().Name == name)
                return a.Id;
        }

        throw new ArgumentException($"No attribute named '{name}'.", nameof(name));
    }

    public IReadOnlyList<ModelAttribute> Build() => [.. _attributes.Select(a => a.Build())];

    public static TypeRef Builtin(string keyword) => new() { Builtin = keyword };

    public static TypeRef Ref(ITypeSource type) => new() { Ref = type.Id };
}

/// <summary>Builds an entity.</summary>
public sealed class EntityBuilder : ElementBuilder<EntityBuilder>
{
    private readonly string? _package;
    private readonly AttributeList _attributes;
    private readonly List<AlternateKey> _alternateKeys = [];
    private EntityKey? _key;
    private bool _abstract;
    private string? _base;

    internal EntityBuilder(ModelBuilder model, string name, string? package)
        : base(model, name)
    {
        _package = package;
        _attributes = new AttributeList(model);
    }

    /// <summary>Adds a required key attribute and makes it the primary key.</summary>
    /// <param name="name">The attribute name.</param>
    /// <param name="type">The built-in type keyword.</param>
    /// <param name="strategy">The identity strategy.</param>
    /// <param name="configure">Further attribute settings.</param>
    /// <returns>This builder.</returns>
    public EntityBuilder Key(string name, string type, IdentityStrategy strategy = IdentityStrategy.Application, Action<AttributeBuilder>? configure = null)
    {
        var attribute = _attributes.Add(name, AttributeList.Builtin(type), a =>
        {
            a.Required();
            configure?.Invoke(a);
        });
        _key = new EntityKey { Attributes = [attribute.Id], Strategy = strategy };
        return this;
    }

    /// <summary>Makes existing attributes the (composite) primary key.</summary>
    /// <param name="attributeNames">The attribute names, in key order.</param>
    /// <returns>This builder.</returns>
    public EntityBuilder CompositeKey(params string[] attributeNames)
    {
        _key = new EntityKey { Attributes = [.. attributeNames.Select(_attributes.IdOf)] };
        return this;
    }

    /// <summary>Adds an attribute of a built-in type.</summary>
    /// <param name="name">The name.</param>
    /// <param name="type">The built-in keyword.</param>
    /// <param name="configure">Further settings.</param>
    /// <returns>This builder.</returns>
    public EntityBuilder Attr(string name, string type, Action<AttributeBuilder>? configure = null)
    {
        _attributes.Add(name, AttributeList.Builtin(type), configure);
        return this;
    }

    /// <summary>Adds an attribute that references an enum, value object or custom scalar type.</summary>
    /// <param name="name">The name.</param>
    /// <param name="type">The referenced type.</param>
    /// <param name="configure">Further settings.</param>
    /// <returns>This builder.</returns>
    public EntityBuilder Attr(string name, ITypeSource type, Action<AttributeBuilder>? configure = null)
    {
        _attributes.Add(name, AttributeList.Ref(type), configure);
        return this;
    }

    /// <summary>Adds a named alternate key over existing attributes.</summary>
    /// <param name="name">The key name.</param>
    /// <param name="attributeNames">The attribute names.</param>
    /// <returns>This builder.</returns>
    public EntityBuilder AlternateKey(string name, params string[] attributeNames)
    {
        _alternateKeys.Add(new AlternateKey { Id = Model.NewId(), Name = name, Attributes = [.. attributeNames.Select(_attributes.IdOf)] });
        return this;
    }

    /// <summary>Makes the entity abstract.</summary>
    /// <returns>This builder.</returns>
    public EntityBuilder Abstract()
    {
        _abstract = true;
        return this;
    }

    /// <summary>Sets the base entity.</summary>
    /// <param name="baseEntity">The base.</param>
    /// <returns>This builder.</returns>
    public EntityBuilder Base(EntityBuilder baseEntity)
    {
        _base = baseEntity.Id;
        return this;
    }

    /// <summary>Returns the id of an attribute.</summary>
    /// <param name="name">The attribute name.</param>
    /// <returns>The id.</returns>
    public string AttrId(string name) => _attributes.IdOf(name);

    /// <inheritdoc/>
    protected override Element CreateElement() => new Entity
    {
        Id = Id,
        Name = Name,
        Package = _package,
        Abstract = _abstract,
        Base = _base,
        Key = _key,
        AlternateKeys = [.. _alternateKeys],
        Attributes = _attributes.Build(),
    };
}

/// <summary>Builds a value object.</summary>
public sealed class ValueObjectBuilder : ElementBuilder<ValueObjectBuilder>, ITypeSource
{
    private readonly string? _package;
    private readonly AttributeList _attributes;

    internal ValueObjectBuilder(ModelBuilder model, string name, string? package)
        : base(model, name)
    {
        _package = package;
        _attributes = new AttributeList(model);
    }

    /// <summary>Adds an attribute of a built-in type.</summary>
    /// <param name="name">The name.</param>
    /// <param name="type">The built-in keyword.</param>
    /// <param name="configure">Further settings.</param>
    /// <returns>This builder.</returns>
    public ValueObjectBuilder Attr(string name, string type, Action<AttributeBuilder>? configure = null)
    {
        _attributes.Add(name, AttributeList.Builtin(type), configure);
        return this;
    }

    /// <summary>Adds an attribute that references another type.</summary>
    /// <param name="name">The name.</param>
    /// <param name="type">The referenced type.</param>
    /// <param name="configure">Further settings.</param>
    /// <returns>This builder.</returns>
    public ValueObjectBuilder Attr(string name, ITypeSource type, Action<AttributeBuilder>? configure = null)
    {
        _attributes.Add(name, AttributeList.Ref(type), configure);
        return this;
    }

    /// <summary>Returns the id of an attribute.</summary>
    /// <param name="name">The attribute name.</param>
    /// <returns>The id.</returns>
    public string AttrId(string name) => _attributes.IdOf(name);

    /// <inheritdoc/>
    protected override Element CreateElement() => new ValueObject { Id = Id, Name = Name, Package = _package, Attributes = _attributes.Build() };
}

/// <summary>Builds an enum.</summary>
public sealed class EnumBuilder : ElementBuilder<EnumBuilder>, ITypeSource
{
    private readonly string? _package;
    private readonly List<EnumMember> _members = [];
    private bool _flags;

    internal EnumBuilder(ModelBuilder model, string name, string? package)
        : base(model, name) => _package = package;

    /// <summary>Adds a member.</summary>
    /// <param name="name">The member name.</param>
    /// <param name="value">The integer value.</param>
    /// <param name="code">The string code.</param>
    /// <returns>This builder.</returns>
    public EnumBuilder Member(string name, long? value = null, string? code = null)
    {
        _members.Add(new EnumMember { Id = Model.NewId(), Name = name, Value = value, Code = code });
        return this;
    }

    /// <summary>Makes the enum a flags enum.</summary>
    /// <returns>This builder.</returns>
    public EnumBuilder Flags()
    {
        _flags = true;
        return this;
    }

    /// <summary>Returns the id of a member.</summary>
    /// <param name="name">The member name.</param>
    /// <returns>The id.</returns>
    public string MemberId(string name) => _members.First(m => m.Name == name).Id;

    /// <inheritdoc/>
    protected override Element CreateElement() => new EnumType { Id = Id, Name = Name, Package = _package, Flags = _flags, Members = [.. _members] };
}

/// <summary>Builds a custom scalar type.</summary>
public sealed class ScalarTypeBuilder : ElementBuilder<ScalarTypeBuilder>, ITypeSource
{
    private readonly string? _package;
    private readonly string _base;
    private int? _length;
    private int? _precision;
    private int? _scale;
    private string? _pattern;

    internal ScalarTypeBuilder(ModelBuilder model, string name, string baseType, string? package)
        : base(model, name)
    {
        _base = baseType;
        _package = package;
    }

    /// <summary>Sets the length facet.</summary>
    /// <param name="length">The length.</param>
    /// <returns>This builder.</returns>
    public ScalarTypeBuilder Length(int length)
    {
        _length = length;
        return this;
    }

    /// <summary>Sets precision and scale.</summary>
    /// <param name="precision">The precision.</param>
    /// <param name="scale">The scale.</param>
    /// <returns>This builder.</returns>
    public ScalarTypeBuilder Decimal(int precision, int scale)
    {
        _precision = precision;
        _scale = scale;
        return this;
    }

    /// <summary>Sets a validation pattern.</summary>
    /// <param name="pattern">The regular expression.</param>
    /// <returns>This builder.</returns>
    public ScalarTypeBuilder Pattern(string pattern)
    {
        _pattern = pattern;
        return this;
    }

    /// <inheritdoc/>
    protected override Element CreateElement() => new ScalarType
    {
        Id = Id,
        Name = Name,
        Package = _package,
        Base = _base,
        Length = _length,
        Precision = _precision,
        Scale = _scale,
        Validation = _pattern is null ? null : new AttributeValidation { Pattern = _pattern },
    };
}

/// <summary>Builds a relation.</summary>
public sealed class RelationBuilder : ElementBuilder<RelationBuilder>
{
    private readonly string? _package;
    private readonly List<RelationEnd> _ends = [];
    private readonly AttributeList _attributes;
    private RelationKind _kind = RelationKind.Association;
    private bool _allowDuplicates;
    private string? _inverseName;

    internal RelationBuilder(ModelBuilder model, string name, string? package)
        : base(model, name)
    {
        _package = package;
        _attributes = new AttributeList(model);
    }

    /// <summary>The ids of the ends, in order.</summary>
    public IReadOnlyList<string> EndIds => [.. _ends.Select(e => e.Id)];

    /// <summary>Sets the relation kind.</summary>
    /// <param name="kind">The kind.</param>
    /// <returns>This builder.</returns>
    public RelationBuilder Kind(RelationKind kind)
    {
        _kind = kind;
        return this;
    }

    /// <summary>Sets the inverse verb phrase.</summary>
    /// <param name="inverseName">The phrase.</param>
    /// <returns>This builder.</returns>
    public RelationBuilder InverseName(string inverseName)
    {
        _inverseName = inverseName;
        return this;
    }

    /// <summary>Allows duplicate links.</summary>
    /// <returns>This builder.</returns>
    public RelationBuilder AllowDuplicates()
    {
        _allowDuplicates = true;
        return this;
    }

    /// <summary>Adds an end (for n-ary relations).</summary>
    /// <param name="entity">The entity.</param>
    /// <param name="role">The role.</param>
    /// <param name="min">The lower bound.</param>
    /// <param name="max">The upper bound.</param>
    /// <param name="navigation">The navigation generated on the opposite entity.</param>
    /// <returns>This builder.</returns>
    public RelationBuilder End(EntityBuilder entity, string role, int min = 0, MaxCardinality max = MaxCardinality.Many, string? navigation = null)
    {
        AddEnd(entity, role, min, max, navigation);
        return this;
    }

    /// <summary>Changes one end.</summary>
    /// <param name="index">The end index.</param>
    /// <param name="change">A function from the end to the new end.</param>
    /// <returns>This builder.</returns>
    public RelationBuilder WithEnd(int index, Func<RelationEnd, RelationEnd> change)
    {
        _ends[index] = change(_ends[index]);
        return this;
    }

    /// <summary>Adds a relation attribute of a built-in type.</summary>
    /// <param name="name">The name.</param>
    /// <param name="type">The built-in keyword.</param>
    /// <param name="configure">Further settings.</param>
    /// <returns>This builder.</returns>
    public RelationBuilder Attr(string name, string type, Action<AttributeBuilder>? configure = null)
    {
        _attributes.Add(name, AttributeList.Builtin(type), configure);
        return this;
    }

    internal void AddEnd(EntityBuilder entity, string role, int min, MaxCardinality max, string? navigation) =>
        _ends.Add(new RelationEnd { Id = Model.NewId(), Entity = entity.Id, Role = role, Min = min, Max = max, Navigation = navigation ?? "" });

    /// <inheritdoc/>
    protected override Element CreateElement() => new Relation
    {
        Id = Id,
        Name = Name,
        Package = _package,
        InverseName = _inverseName,
        RelationKind = _kind,
        Ends = [.. _ends],
        Attributes = _attributes.Build(),
        AllowDuplicates = _allowDuplicates,
    };
}

/// <summary>Builds a database.</summary>
public sealed class DatabaseBuilder : ElementBuilder<DatabaseBuilder>
{
    private readonly Dialect _dialect;
    private readonly List<DbSchema> _schemas = [];
    private readonly List<ConventionPackage> _packages = [];
    private ConventionMapping? _byConvention;
    private string? _defaultSchema;
    private Maquettiste.Engine.Model.Quoting _quoting = Maquettiste.Engine.Model.Quoting.Reserved;

    internal DatabaseBuilder(ModelBuilder model, string name, Dialect dialect)
        : base(model, name) => _dialect = dialect;

    /// <summary>Adds a schema.</summary>
    /// <param name="name">The schema name.</param>
    /// <returns>The schema id.</returns>
    public string Schema(string name)
    {
        var id = Model.NewId();
        _schemas.Add(new DbSchema { Id = id, Name = name });
        return id;
    }

    /// <summary>Sets the default schema.</summary>
    /// <param name="name">The schema name.</param>
    /// <returns>This builder.</returns>
    public DatabaseBuilder DefaultSchema(string name)
    {
        _defaultSchema = name;
        return this;
    }

    /// <summary>Sets the quoting policy.</summary>
    /// <param name="quoting">The policy.</param>
    /// <returns>This builder.</returns>
    public DatabaseBuilder Quoting(Maquettiste.Engine.Model.Quoting quoting)
    {
        _quoting = quoting;
        return this;
    }

    /// <summary>Limits the database to entities in packages (and their sub-packages).</summary>
    /// <param name="packages">The packages.</param>
    /// <returns>This builder.</returns>
    public DatabaseBuilder Packages(params PackageBuilder[] packages)
    {
        _packages.AddRange(packages.Select(p => new ConventionPackage { Package = p.Id }));
        return this;
    }

    /// <summary>Takes a package's entities by convention into a schema.</summary>
    /// <param name="package">The package.</param>
    /// <param name="schemaId">The schema id (from <see cref="Schema"/>).</param>
    /// <returns>This builder.</returns>
    public DatabaseBuilder Package(PackageBuilder package, string schemaId)
    {
        _packages.Add(new ConventionPackage { Package = package.Id, Schema = schemaId });
        return this;
    }

    /// <summary>Sets which entities the database takes by convention (<c>byConvention</c>).</summary>
    /// <param name="value">The setting.</param>
    /// <returns>This builder.</returns>
    public DatabaseBuilder ByConvention(ConventionMapping value)
    {
        _byConvention = value;
        return this;
    }

    /// <inheritdoc/>
    protected override Element CreateElement() => new Database
    {
        Id = Id,
        Name = Name,
        Dialect = _dialect,
        DefaultSchema = _defaultSchema,
        Schemas = [.. _schemas],
        Quoting = _quoting,
        ByConvention = _byConvention,
        Packages = [.. _packages],
    };
}

/// <summary>Builds a designed table, or a synthesized table's overlay.</summary>
public sealed class TableBuilder : ElementBuilder<TableBuilder>
{
    private readonly DatabaseBuilder _database;
    private readonly List<Column> _columns = [];
    private readonly List<ForeignKey> _foreignKeys = [];
    private readonly List<TableIndex> _indexes = [];
    private PrimaryKey? _primaryKey;
    private TableOrigin _origin = TableOrigin.Designed;
    private string? _entity;

    internal TableBuilder(ModelBuilder model, string name, DatabaseBuilder database)
        : base(model, name) => _database = database;

    /// <summary>Makes the table the overlay of an entity's synthesized table.</summary>
    /// <param name="entity">The entity.</param>
    /// <returns>This builder.</returns>
    public TableBuilder OverlayFor(EntityBuilder entity)
    {
        _origin = TableOrigin.Synthesized;
        _entity = entity.Id;
        return this;
    }

    /// <summary>Adds a column.</summary>
    /// <param name="name">The name.</param>
    /// <param name="type">The built-in keyword.</param>
    /// <param name="nullable">Nullability.</param>
    /// <param name="length">The length facet.</param>
    /// <param name="nativeType">A native type that replaces the dialect map's.</param>
    /// <returns>This builder.</returns>
    public TableBuilder Column(string name, string type, bool? nullable = null, int? length = null, string? nativeType = null)
    {
        _columns.Add(new Column { Id = Model.NewId(), Name = name, Type = type, Nullable = nullable, Length = length, NativeType = nativeType });
        return this;
    }

    /// <summary>Adds an overlay column that overrides a synthesized column.</summary>
    /// <param name="columnKey">The synthesized column key.</param>
    /// <param name="name">The new name.</param>
    /// <param name="nativeType">A native type.</param>
    /// <returns>This builder.</returns>
    public TableBuilder Overlay(string columnKey, string? name = null, string? nativeType = null)
    {
        _columns.Add(new Column { Id = Model.NewId(), Attribute = columnKey, Name = name ?? "", NativeType = nativeType });
        return this;
    }

    /// <summary>Returns a column's id.</summary>
    /// <param name="name">The column name.</param>
    /// <returns>The id.</returns>
    public string ColumnId(string name) => _columns.First(c => c.Name == name).Id;

    /// <summary>Sets the primary key.</summary>
    /// <param name="columnNames">Column names, in key order.</param>
    /// <returns>This builder.</returns>
    public TableBuilder PrimaryKey(params string[] columnNames)
    {
        _primaryKey = new PrimaryKey { Columns = [.. columnNames.Select(ColumnId)] };
        return this;
    }

    /// <summary>Adds a foreign key.</summary>
    /// <param name="referencedTable">The referenced table.</param>
    /// <param name="columnNames">The referencing column names.</param>
    /// <returns>This builder.</returns>
    public TableBuilder ForeignKey(TableBuilder referencedTable, params string[] columnNames)
    {
        _foreignKeys.Add(new ForeignKey { Id = Model.NewId(), Columns = [.. columnNames.Select(ColumnId)], ReferencesTable = referencedTable.Id });
        return this;
    }

    /// <summary>Adds an index.</summary>
    /// <param name="unique">Whether it is unique.</param>
    /// <param name="columnNames">The column names.</param>
    /// <returns>This builder.</returns>
    public TableBuilder Index(bool unique, params string[] columnNames)
    {
        _indexes.Add(new TableIndex { Id = Model.NewId(), Unique = unique, Columns = [.. columnNames.Select(n => new IndexColumn { Column = ColumnId(n) })] });
        return this;
    }

    /// <inheritdoc/>
    protected override Element CreateElement() => new Table
    {
        Id = Id,
        Name = Name,
        Database = _database.Id,
        Origin = _origin,
        Entity = _entity,
        Columns = [.. _columns],
        PrimaryKey = _primaryKey,
        ForeignKeys = [.. _foreignKeys],
        Indexes = [.. _indexes],
    };
}

/// <summary>Builds a mapping.</summary>
public sealed class MappingBuilder : ElementBuilder<MappingBuilder>
{
    private readonly string _database;
    private readonly string? _entity;
    private readonly string? _relation;
    private readonly EntityBuilder? _entityBuilder;
    private readonly List<AttributeMapping> _attributes = [];
    private string? _table;
    private string? _schema;
    private bool _ignore;
    private InheritanceStrategy? _inheritance;
    private RelationShape? _shape;
    private JsonElement? _discriminator;

    internal MappingBuilder(ModelBuilder model, string name, string database, string? entity, string? relation, EntityBuilder? entityBuilder)
        : base(model, name)
    {
        _database = database;
        _entity = entity;
        _relation = relation;
        _entityBuilder = entityBuilder;
    }

    /// <summary>Binds the entity to a designed table.</summary>
    /// <param name="table">The table.</param>
    /// <returns>This builder.</returns>
    public MappingBuilder Table(TableBuilder table)
    {
        _table = table.Id;
        return this;
    }

    /// <summary>Places the entity's conventional table in a schema.</summary>
    /// <param name="schemaId">The schema id.</param>
    /// <returns>This builder.</returns>
    public MappingBuilder Schema(string schemaId)
    {
        _schema = schemaId;
        return this;
    }

    /// <summary>Excludes the element from the database.</summary>
    /// <returns>This builder.</returns>
    public MappingBuilder Ignore()
    {
        _ignore = true;
        return this;
    }

    /// <summary>Sets the inheritance strategy (on a hierarchy root).</summary>
    /// <param name="strategy">The strategy.</param>
    /// <returns>This builder.</returns>
    public MappingBuilder Inheritance(InheritanceStrategy strategy)
    {
        _inheritance = strategy;
        return this;
    }

    /// <summary>Sets the discriminator value.</summary>
    /// <param name="value">A string or integer.</param>
    /// <returns>This builder.</returns>
    public MappingBuilder Discriminator(object value)
    {
        _discriminator = ModelBuilder.ToJson(value);
        return this;
    }

    /// <summary>Overrides a relation's shape.</summary>
    /// <param name="shape">The shape.</param>
    /// <returns>This builder.</returns>
    public MappingBuilder Shape(RelationShape shape)
    {
        _shape = shape;
        return this;
    }

    /// <summary>Sets an entity attribute's storage.</summary>
    /// <param name="attributeName">The attribute name.</param>
    /// <param name="storage">The storage.</param>
    /// <returns>This builder.</returns>
    public MappingBuilder Storage(string attributeName, StorageKind storage)
    {
        var id = _entityBuilder?.AttrId(attributeName) ?? throw new InvalidOperationException("Storage applies to entity mappings.");
        _attributes.Add(new AttributeMapping { Attribute = id, Storage = storage });
        return this;
    }

    /// <inheritdoc/>
    protected override Element CreateElement() => new Mapping
    {
        Id = Id,
        Name = Name,
        Database = _database,
        Entity = _entity,
        Relation = _relation,
        Table = _table,
        Schema = _schema,
        Ignore = _ignore,
        Inheritance = _inheritance,
        DiscriminatorValue = _discriminator,
        Shape = _shape,
        Attributes = [.. _attributes],
    };
}

/// <summary>Builds a stereotype.</summary>
public sealed class StereotypeBuilder : ElementBuilder<StereotypeBuilder>
{
    private readonly AttributeList _attributes;
    private readonly List<string> _appliesTo = [];
    private readonly Dictionary<string, JsonElement> _defaults = new(StringComparer.Ordinal);

    internal StereotypeBuilder(ModelBuilder model, string key)
        : base(model, key) => _attributes = new AttributeList(model);

    /// <summary>Restricts the stereotype to kinds.</summary>
    /// <param name="kinds">Kind names.</param>
    /// <returns>This builder.</returns>
    public StereotypeBuilder AppliesTo(params string[] kinds)
    {
        _appliesTo.AddRange(kinds);
        return this;
    }

    /// <summary>Adds a virtual attribute.</summary>
    /// <param name="name">The name.</param>
    /// <param name="type">The built-in keyword.</param>
    /// <param name="configure">Further settings.</param>
    /// <returns>This builder.</returns>
    public StereotypeBuilder Attr(string name, string type, Action<AttributeBuilder>? configure = null)
    {
        _attributes.Add(name, AttributeList.Builtin(type), configure);
        return this;
    }

    /// <summary>Sets a default property value.</summary>
    /// <param name="key">The property name.</param>
    /// <param name="value">A JSON-serializable value.</param>
    /// <returns>This builder.</returns>
    public StereotypeBuilder DefaultProperty(string key, object? value)
    {
        _defaults[key] = ModelBuilder.ToJson(value);
        return this;
    }

    /// <inheritdoc/>
    protected override Element CreateElement() => new Stereotype
    {
        Id = Id,
        Key = Name,
        Name = Name,
        AppliesTo = [.. _appliesTo],
        Attributes = _attributes.Build(),
        DefaultProperties = _defaults.ToImmutableDictionary(StringComparer.Ordinal),
    };
}
