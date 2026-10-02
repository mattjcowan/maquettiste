using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using Maquettiste.Engine;
using Maquettiste.Engine.Hashing;
using Maquettiste.Engine.Model;
using ModelDescription = Maquettiste.Engine.Model.Description;

namespace Maquettiste.Testing;

/// <summary>
/// A fluent, in-memory builder for test models: packages, entities, attributes, value objects, enums, scalar types, relations,
/// databases, tables, mappings, stereotypes, tags and categories. <see cref="Build"/> is pure (no I/O): every element is
/// serialized through the canonical writer, so documents carry real paths, hashes and JSON.
/// </summary>
/// <example>
/// <code>
/// var b = new ModelBuilder(seed: 1);
/// var billing = b.Package("Billing");
/// var customer = b.Entity("Customer", billing).Key("id", "uuid", IdentityStrategy.UuidV7).Attr("name", "string", a => a.Length(120).Required());
/// var invoice = b.Entity("Invoice", billing).Stereotype("audited").Attr("number", "string", a => a.Length(32).Unique());
/// b.Relation("places", customer, invoice, fromMax: MaxCardinality.One, toMax: MaxCardinality.Many, fromRole: "customer", toNavigation: "invoices");
/// b.Database("main", Dialect.PostgreSql);
/// ModelSnapshot model = b.Build();
/// </code>
/// </example>
public sealed class ModelBuilder
{
    private readonly List<IElementSource> _sources = [];
    private readonly List<Element> _added = [];
    private readonly List<Category> _categories = [];
    private readonly List<TagDefinition> _tags = [];
    private string? _categoryTreeId;
    private string? _tagVocabularyId;
    private bool _strictTags;
    private ProjectSettings _settings = new() { FormatVersion = EngineVersion.FormatVersion };

    /// <summary>Creates a builder whose ids come from a <see cref="SequentialIdGenerator"/>.</summary>
    /// <param name="seed">The id seed.</param>
    public ModelBuilder(int seed = 1) => Ids = new SequentialIdGenerator(seed);

    /// <summary>The id generator.</summary>
    public IIdGenerator Ids { get; }

    /// <summary>Returns a new id.</summary>
    /// <returns>The id.</returns>
    public string NewId() => Ids.NewId();

    /// <summary>Adds a package.</summary>
    /// <param name="name">The name.</param>
    /// <param name="parent">The parent package.</param>
    /// <returns>The package builder.</returns>
    public PackageBuilder Package(string name, PackageBuilder? parent = null) => Register(new PackageBuilder(this, name, parent?.Id));

    /// <summary>Adds an entity.</summary>
    /// <param name="name">The name.</param>
    /// <param name="package">The package.</param>
    /// <returns>The entity builder.</returns>
    public EntityBuilder Entity(string name, PackageBuilder? package = null) => Register(new EntityBuilder(this, name, package?.Id));

    /// <summary>Adds a value object.</summary>
    /// <param name="name">The name.</param>
    /// <param name="package">The package.</param>
    /// <returns>The value object builder.</returns>
    public ValueObjectBuilder ValueObject(string name, PackageBuilder? package = null) => Register(new ValueObjectBuilder(this, name, package?.Id));

    /// <summary>Adds an enum.</summary>
    /// <param name="name">The name.</param>
    /// <param name="package">The package.</param>
    /// <returns>The enum builder.</returns>
    public EnumBuilder Enum(string name, PackageBuilder? package = null) => Register(new EnumBuilder(this, name, package?.Id));

    /// <summary>Adds a custom scalar type.</summary>
    /// <param name="name">The name.</param>
    /// <param name="baseType">The built-in base keyword.</param>
    /// <param name="package">The package.</param>
    /// <returns>The scalar type builder.</returns>
    public ScalarTypeBuilder ScalarType(string name, string baseType, PackageBuilder? package = null) =>
        Register(new ScalarTypeBuilder(this, name, baseType, package?.Id));

    /// <summary>Adds a binary relation. End 0 is <paramref name="from"/>, end 1 is <paramref name="to"/>.</summary>
    /// <param name="name">The verb phrase.</param>
    /// <param name="from">The entity at end 0.</param>
    /// <param name="to">The entity at end 1.</param>
    /// <param name="fromMax">End 0's upper bound.</param>
    /// <param name="toMax">End 1's upper bound.</param>
    /// <param name="fromMin">End 0's lower bound.</param>
    /// <param name="toMin">End 1's lower bound.</param>
    /// <param name="fromRole">End 0's role; defaults to the camel-cased entity name.</param>
    /// <param name="toRole">End 1's role; defaults to the camel-cased entity name.</param>
    /// <param name="fromNavigation">End 0's navigation (generated on <paramref name="to"/>).</param>
    /// <param name="toNavigation">End 1's navigation (generated on <paramref name="from"/>).</param>
    /// <param name="package">The package.</param>
    /// <returns>The relation builder.</returns>
    public RelationBuilder Relation(
        string name,
        EntityBuilder from,
        EntityBuilder to,
        MaxCardinality fromMax = MaxCardinality.Many,
        MaxCardinality toMax = MaxCardinality.Many,
        int fromMin = 0,
        int toMin = 0,
        string? fromRole = null,
        string? toRole = null,
        string? fromNavigation = null,
        string? toNavigation = null,
        PackageBuilder? package = null)
    {
        var relation = new RelationBuilder(this, name, package?.Id);
        relation.AddEnd(from, fromRole ?? Camel(from.Name), fromMin, fromMax, fromNavigation);
        relation.AddEnd(to, toRole ?? Camel(to.Name), toMin, toMax, toNavigation);
        return Register(relation);
    }

    /// <summary>Adds an n-ary relation with no ends yet; add them with <see cref="RelationBuilder.End"/>.</summary>
    /// <param name="name">The verb phrase.</param>
    /// <param name="package">The package.</param>
    /// <returns>The relation builder.</returns>
    public RelationBuilder NAryRelation(string name, PackageBuilder? package = null) =>
        Register(new RelationBuilder(this, name, package?.Id).Kind(RelationKind.NAry));

    /// <summary>Adds a database.</summary>
    /// <param name="name">The name.</param>
    /// <param name="dialect">The dialect.</param>
    /// <returns>The database builder.</returns>
    public DatabaseBuilder Database(string name, Dialect dialect) => Register(new DatabaseBuilder(this, name, dialect));

    /// <summary>Adds a designed table (or, with <see cref="TableBuilder.OverlayFor(EntityBuilder)"/>, a synthesized table's overlay).</summary>
    /// <param name="name">The name.</param>
    /// <param name="database">The database.</param>
    /// <returns>The table builder.</returns>
    public TableBuilder Table(string name, DatabaseBuilder database) => Register(new TableBuilder(this, name, database));

    /// <summary>Adds a mapping of an entity to a database.</summary>
    /// <param name="database">The database.</param>
    /// <param name="entity">The entity.</param>
    /// <returns>The mapping builder.</returns>
    public MappingBuilder Mapping(DatabaseBuilder database, EntityBuilder entity) =>
        Register(new MappingBuilder(this, $"{entity.Name} in {database.Name}", database.Id, entity.Id, null, entity));

    /// <summary>Adds a mapping of a relation to a database.</summary>
    /// <param name="database">The database.</param>
    /// <param name="relation">The relation.</param>
    /// <returns>The mapping builder.</returns>
    public MappingBuilder Mapping(DatabaseBuilder database, RelationBuilder relation) =>
        Register(new MappingBuilder(this, $"{relation.Name} in {database.Name}", database.Id, null, relation.Id, null));

    /// <summary>Adds a stereotype.</summary>
    /// <param name="key">The stereotype key.</param>
    /// <returns>The stereotype builder.</returns>
    public StereotypeBuilder Stereotype(string key) => Register(new StereotypeBuilder(this, key));

    /// <summary>Declares tags in the tag vocabulary.</summary>
    /// <param name="strict">Whether undeclared tags are errors.</param>
    /// <param name="keys">The tag keys.</param>
    /// <returns>This builder.</returns>
    public ModelBuilder Tags(bool strict, params string[] keys)
    {
        _tagVocabularyId ??= NewId();
        _strictTags = strict;
        foreach (var key in keys)
            _tags.Add(new TagDefinition { Key = key });
        return this;
    }

    /// <summary>Adds a category to the category tree.</summary>
    /// <param name="name">The name.</param>
    /// <param name="parentId">The parent category id.</param>
    /// <returns>The new category's id.</returns>
    public string Category(string name, string? parentId = null)
    {
        _categoryTreeId ??= NewId();
        var id = NewId();
        _categories.Add(new Category { Id = id, Name = name, Parent = parentId });
        return id;
    }

    /// <summary>Adds a ready-made element.</summary>
    /// <param name="element">The element.</param>
    /// <returns>This builder.</returns>
    public ModelBuilder Add(Element element)
    {
        _added.Add(element);
        return this;
    }

    /// <summary>Changes the project settings.</summary>
    /// <param name="change">A function from the current settings to the new ones.</param>
    /// <returns>This builder.</returns>
    public ModelBuilder Settings(Func<ProjectSettings, ProjectSettings> change)
    {
        _settings = change(_settings);
        return this;
    }

    /// <summary>Returns the elements in build order.</summary>
    /// <returns>The elements.</returns>
    public IReadOnlyList<Element> BuildElements()
    {
        var elements = _sources.Select(s => s.Build()).Concat(_added).ToList();
        if (_tagVocabularyId is not null)
            elements.Add(new TagVocabulary { Id = _tagVocabularyId, Name = "tags", Strict = _strictTags, Definitions = [.. _tags] });
        if (_categoryTreeId is not null)
            elements.Add(new CategoryTree { Id = _categoryTreeId, Name = "categories", Categories = [.. _categories] });
        return elements;
    }

    /// <summary>Returns the element documents: canonical JSON, conventional paths, hashes. No I/O.</summary>
    /// <returns>The documents, ordinal by path.</returns>
    public IReadOnlyList<ElementDocument> BuildDocuments()
    {
        var elements = BuildElements();
        var databaseFolders = elements.OfType<Database>().ToDictionary(d => d.Id, d => Kebab(d.Name), StringComparer.Ordinal);
        var used = new HashSet<string>(StringComparer.Ordinal);
        var documents = new List<ElementDocument>();
        foreach (var element in elements)
        {
            var path = PathOf(element, databaseFolders, used);
            var info = KindInfo.Get(element.Kind);
            var bytes = TestServices.Json.Serialize(element, info.SchemaFile, path);
            var hash = ContentHash.Of(bytes);
            using var json = JsonDocument.Parse(bytes);
            documents.Add(new ElementDocument(element, path, hash, HashBuilder.Of(hash, null), json.RootElement.Clone(), null));
        }

        return [.. documents.OrderBy(d => d.Path, StringComparer.Ordinal)];
    }

    /// <summary>Returns the canonical bytes of <c>maquettiste.json</c>.</summary>
    /// <returns>The bytes.</returns>
    public byte[] BuildSettingsBytes() => TestServices.Json.Serialize(_settings, "maquettiste.json", ".maquettiste/maquettiste.json");

    /// <summary>Builds a snapshot. Pure: no I/O.</summary>
    /// <param name="version">The snapshot version.</param>
    /// <returns>The snapshot.</returns>
    public ModelSnapshot Build(long version = 1) =>
        ModelSnapshot.Create(BuildDocuments(), _settings, ContentHash.Of(BuildSettingsBytes()), [], [], version);

    /// <summary>Writes <c>maquettiste.json</c> and every element file under a model root.</summary>
    /// <param name="modelRoot">The model root (a repo's <c>.maquettiste</c> folder).</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>A task.</returns>
    public async Task WriteToAsync(string modelRoot, CancellationToken ct)
    {
        Directory.CreateDirectory(modelRoot);
        await File.WriteAllBytesAsync(Path.Combine(modelRoot, "maquettiste.json"), BuildSettingsBytes(), ct).ConfigureAwait(false);
        foreach (var document in BuildDocuments())
        {
            var relative = document.Path.StartsWith(".maquettiste/", StringComparison.Ordinal) ? document.Path[".maquettiste/".Length..] : document.Path;
            var target = Path.Combine(modelRoot, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            var bytes = TestServices.Json.Serialize(document.Element, KindInfo.Get(document.Element.Kind).SchemaFile, document.Path);
            await File.WriteAllBytesAsync(target, bytes, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Kebab-cases a name for file names: <c>InvoiceLine</c> → <c>invoice-line</c>, <c>is member of</c> → <c>is-member-of</c>.</summary>
    /// <param name="name">The name.</param>
    /// <returns>The kebab-case name.</returns>
    public static string Kebab(string name)
    {
        var sb = new StringBuilder();
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

    internal static string Camel(string name) => name.Length == 0 ? name : char.ToLowerInvariant(name[0]) + name[1..];

    internal static JsonElement ToJson(object? value) => JsonSerializer.SerializeToElement(value, EngineJson.Options);

    private T Register<T>(T source) where T : IElementSource
    {
        _sources.Add(source);
        return source;
    }

    private static string PathOf(Element element, Dictionary<string, string> databaseFolders, HashSet<string> used)
    {
        var info = KindInfo.Get(element.Kind);
        var database = element switch
        {
            Database d => d.Id,
            Table t => t.Database,
            View v => v.Database,
            Sequence s => s.Database,
            Query q => q.Database,
            _ => null,
        };
        var folder = info.Folder.Replace("{db}", database is not null && databaseFolders.TryGetValue(database, out var db) ? db : "unknown", StringComparison.Ordinal);
        var fileName = info.FixedFileName ?? Kebab(element.Name.Length > 0 ? element.Name : element.Id.ToLowerInvariant()) + ".json";
        var path = ".maquettiste/" + folder + "/" + fileName;
        if (!used.Add(path))
        {
            path = path[..^".json".Length] + "-" + element.Id[^6..].ToLowerInvariant() + ".json";
            used.Add(path);
        }

        return path;
    }
}

/// <summary>Something that builds one element.</summary>
public interface IElementSource
{
    /// <summary>Builds the element.</summary>
    /// <returns>The element.</returns>
    Element Build();
}

/// <summary>A type an attribute can reference: an enum, a value object or a custom scalar type.</summary>
public interface ITypeSource
{
    /// <summary>The referenced element's id.</summary>
    string Id { get; }
}

/// <summary>The common fluent members of element builders.</summary>
/// <typeparam name="TSelf">The concrete builder.</typeparam>
public abstract class ElementBuilder<TSelf> : IElementSource where TSelf : ElementBuilder<TSelf>
{
    private readonly List<string> _stereotypes = [];
    private readonly List<string> _tags = [];
    private readonly Dictionary<string, JsonElement> _properties = new(StringComparer.Ordinal);

    internal ElementBuilder(ModelBuilder model, string name)
    {
        Model = model;
        Id = model.NewId();
        Name = name;
    }

    /// <summary>The element id.</summary>
    public string Id { get; }

    /// <summary>The element name.</summary>
    public string Name { get; private set; }

    /// <summary>The owning model builder.</summary>
    protected ModelBuilder Model { get; }

    private string? DisplayNameValue { get; set; }

    private string? DescriptionValue { get; set; }

    private string? CategoryValue { get; set; }

    /// <summary>Renames the element.</summary>
    /// <param name="name">The new name.</param>
    /// <returns>This builder.</returns>
    public TSelf Rename(string name)
    {
        Name = name;
        return (TSelf)this;
    }

    /// <summary>Sets the display name.</summary>
    /// <param name="displayName">The display name.</param>
    /// <returns>This builder.</returns>
    public TSelf DisplayName(string displayName)
    {
        DisplayNameValue = displayName;
        return (TSelf)this;
    }

    /// <summary>Sets an inline description.</summary>
    /// <param name="text">The Markdown text.</param>
    /// <returns>This builder.</returns>
    public TSelf Description(string text)
    {
        DescriptionValue = text;
        return (TSelf)this;
    }

    /// <summary>Applies a stereotype.</summary>
    /// <param name="key">The stereotype key.</param>
    /// <returns>This builder.</returns>
    public TSelf Stereotype(string key)
    {
        _stereotypes.Add(key);
        return (TSelf)this;
    }

    /// <summary>Adds a tag.</summary>
    /// <param name="key">The tag key.</param>
    /// <returns>This builder.</returns>
    public TSelf Tag(string key)
    {
        _tags.Add(key);
        return (TSelf)this;
    }

    /// <summary>Sets the category.</summary>
    /// <param name="categoryId">The category id.</param>
    /// <returns>This builder.</returns>
    public TSelf Category(string categoryId)
    {
        CategoryValue = categoryId;
        return (TSelf)this;
    }

    /// <summary>Sets a custom property.</summary>
    /// <param name="key">The property name.</param>
    /// <param name="value">A JSON-serializable value.</param>
    /// <returns>This builder.</returns>
    public TSelf Property(string key, object? value)
    {
        _properties[key] = ModelBuilder.ToJson(value);
        return (TSelf)this;
    }

    /// <inheritdoc/>
    public Element Build() => Apply(CreateElement());

    /// <summary>Creates the kind-specific element; the common fields are applied afterwards.</summary>
    /// <returns>The element.</returns>
    protected abstract Element CreateElement();

    private Element Apply(Element element) => element with
    {
        DisplayName = DisplayNameValue,
        Description = DescriptionValue is null ? null : new ModelDescription { Text = DescriptionValue },
        Stereotypes = [.. _stereotypes],
        Tags = [.. _tags],
        Category = CategoryValue,
        Properties = _properties.ToImmutableDictionary(StringComparer.Ordinal),
    };
}
