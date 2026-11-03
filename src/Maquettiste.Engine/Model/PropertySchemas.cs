using System.Collections.Frozen;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Json.Schema;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Hashing;

namespace Maquettiste.Engine.Model;

/// <summary>
/// One <c>x-ref</c> keyword of a property schema (next-phase-1-design.md section 4.1): the value at that place is the id of an
/// element or sub-element of one of <see cref="Kinds"/>. Kinds are index kind names: element kind names, the sub-element kinds of
/// <see cref="IndexEntry.Kind"/>, or a reserved kind (<c>member</c>, <c>custom</c>, <c>custom:&lt;type&gt;</c>) whose values dangle
/// until the kind exists. <c>operation</c> became an element kind and <c>operation-member</c>, <c>outcome</c> and <c>error</c>
/// sub-element kinds (next-phase-2-design.md section 4.1).
/// </summary>
/// <param name="Kinds">The kinds the value may name, in file order.</param>
/// <param name="Of">For sub-element kinds: <see cref="ElementScope"/> (a member of the host's top-level element) or an element id
/// (a row whose seed targets that element); <see langword="null"/> when absent.</param>
/// <param name="SameDatabase">Whether the target must be in the host's database (physical kinds only).</param>
/// <param name="Optional">Whether <c>remove-references</c> may clear the value.</param>
internal sealed record XRef(IReadOnlyList<string> Kinds, string? Of, bool SameDatabase, bool Optional)
{
    /// <summary>The <c>of</c> value that scopes a sub-element to the host's top-level element.</summary>
    public const string ElementScope = "$element";

    /// <summary>The reserved kinds that name sub-elements: the members of custom elements (next-phase-2-design.md section 4.2).</summary>
    public static readonly FrozenSet<string> ReservedSubElementKinds = new[] { "member" }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>The kinds <c>sameDatabase</c> fits: the physical objects and their parts.</summary>
    public static readonly FrozenSet<string> PhysicalKinds =
        new[] { "table", "view", "sequence", "routine", "database-type", "sql-object", "query", "column", "schema", "key" }.ToFrozenSet(StringComparer.Ordinal);

    private static readonly FrozenSet<string> ElementKinds = KindInfo.All.Select(k => k.Name).ToFrozenSet(StringComparer.Ordinal);

    private static readonly FrozenSet<string> Keys = new[] { "kind", "of", "sameDatabase", "optional" }.ToFrozenSet(StringComparer.Ordinal);

    private static readonly Regex KebabKey = new("^[a-z][a-z0-9]*(-[a-z0-9]+)*$", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, TimeSpan.FromSeconds(1));

    /// <summary>Whether a kind names sub-elements: an index sub-element kind or a reserved one.</summary>
    /// <param name="kind">The kind name.</param>
    /// <returns><see langword="true"/> for a sub-element kind.</returns>
    public static bool IsSubElementKind(string kind) => ModelIndexer.SubElementKinds.Contains(kind) || ReservedSubElementKinds.Contains(kind);

    /// <summary>Whether a kind is reserved (its values report MQ2001 until phase 2 adds it).</summary>
    /// <param name="kind">The kind name.</param>
    /// <returns><see langword="true"/> for a reserved kind.</returns>
    public static bool IsReserved(string kind) =>
        ReservedSubElementKinds.Contains(kind) || kind == "custom" || kind.StartsWith("custom:", StringComparison.Ordinal);

    /// <summary>
    /// Reads one <c>x-ref</c> value, or returns <see langword="null"/> after reporting each problem (MQ2009): a value that is not an
    /// object, an unknown key, a missing or unknown <c>kind</c>, a <c>custom:&lt;type&gt;</c> whose type is not a key, an <c>of</c>
    /// or <c>sameDatabase</c> where it does not fit, and a keyword that is not on a string.
    /// </summary>
    /// <param name="value">The keyword's value.</param>
    /// <param name="pointer">The keyword's pointer in its file.</param>
    /// <param name="onString">Whether the keyword sits on a string schema or an array's items.</param>
    /// <param name="problem">Receives (pointer, message) per problem.</param>
    /// <returns>The keyword, or <see langword="null"/> when it is malformed.</returns>
    public static XRef? Parse(JsonElement value, string pointer, bool onString, Action<string, string> problem)
    {
        ArgumentNullException.ThrowIfNull(problem);
        if (value.ValueKind != JsonValueKind.Object)
        {
            problem(pointer, "x-ref must be an object with kind, and optionally of, sameDatabase and optional.");
            return null;
        }

        var valid = true;
        foreach (var property in value.EnumerateObject())
        {
            if (!Keys.Contains(property.Name))
            {
                problem(pointer + "/" + Escape(property.Name), $"x-ref has no key '{property.Name}': it takes kind, of, sameDatabase and optional.");
                valid = false;
            }
        }

        var kinds = new List<string>();
        if (!value.TryGetProperty("kind", out var kind))
        {
            problem(pointer, "x-ref needs kind: a kind name or a non-empty array of them.");
            valid = false;
        }
        else if (kind.ValueKind == JsonValueKind.String)
        {
            kinds.Add(kind.GetString()!);
        }
        else if (kind.ValueKind == JsonValueKind.Array && kind.GetArrayLength() > 0 && kind.EnumerateArray().All(k => k.ValueKind == JsonValueKind.String))
        {
            foreach (var item in kind.EnumerateArray())
            {
                var name = item.GetString()!;
                if (kinds.Contains(name, StringComparer.Ordinal))
                {
                    problem(pointer + "/kind", $"x-ref kind lists '{name}' twice.");
                    valid = false;
                }
                else
                {
                    kinds.Add(name);
                }
            }
        }
        else
        {
            problem(pointer + "/kind", "x-ref kind must be a kind name or a non-empty array of kind names.");
            valid = false;
        }

        for (var i = 0; i < kinds.Count; i++)
        {
            var name = kinds[i];
            var at = kind.ValueKind == JsonValueKind.Array ? pointer + "/kind/" + i.ToString(CultureInfo.InvariantCulture) : pointer + "/kind";
            if (name.StartsWith("custom:", StringComparison.Ordinal))
            {
                if (!KebabKey.IsMatch(name["custom:".Length..]))
                {
                    problem(at, $"x-ref kind '{name}': the type after custom: must be a kebab-case key.");
                    valid = false;
                }
            }
            else if (!ElementKinds.Contains(name) && !IsSubElementKind(name) && name != "custom")
            {
                problem(at, $"x-ref kind '{name}' is not an element kind, a sub-element kind or a reserved kind.");
                valid = false;
            }
        }

        string? of = null;
        if (value.TryGetProperty("of", out var ofValue))
        {
            if (ofValue.ValueKind != JsonValueKind.String || (ofValue.GetString() != ElementScope && !IdFormat.IsValid(ofValue.GetString())))
            {
                problem(pointer + "/of", $"x-ref of must be \"{ElementScope}\" or an element id.");
                valid = false;
            }
            else
            {
                of = ofValue.GetString();
                foreach (var name in kinds.Where(k => !IsSubElementKind(k)))
                {
                    problem(pointer + "/of", $"x-ref of fits sub-element kinds only, and '{name}' is not one.");
                    valid = false;
                }
            }
        }

        var sameDatabase = false;
        if (value.TryGetProperty("sameDatabase", out var same))
        {
            if (same.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                problem(pointer + "/sameDatabase", "x-ref sameDatabase must be true or false.");
                valid = false;
            }
            else if ((sameDatabase = same.GetBoolean()) && kinds.FirstOrDefault(k => !PhysicalKinds.Contains(k)) is { } notPhysical)
            {
                problem(pointer + "/sameDatabase", $"x-ref sameDatabase fits physical kinds only, and '{notPhysical}' is not one.");
                valid = false;
            }
        }

        var optional = false;
        if (value.TryGetProperty("optional", out var optionalValue))
        {
            if (optionalValue.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                problem(pointer + "/optional", "x-ref optional must be true or false.");
                valid = false;
            }
            else
            {
                optional = optionalValue.GetBoolean();
            }
        }

        if (!onString)
        {
            problem(pointer, "x-ref must sit on a schema of type string or on an array's items: the value it types is an id.");
            valid = false;
        }

        return valid ? new XRef(kinds, of, sameDatabase, optional) : null;
    }

    internal static string Escape(string segment) => segment.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);
}

/// <summary>
/// Where the <c>x-ref</c> keywords of one property schema sit: on the value itself, on its array items, on named members or on any
/// other member of an object value. The walk follows <c>properties</c>, <c>items</c> and <c>additionalProperties</c> only.
/// </summary>
internal sealed class XRefNode
{
    /// <summary>The keyword typing a string value at this place, when there is one.</summary>
    public XRef? Ref { get; set; }

    /// <summary>The nodes of named members of an object value.</summary>
    public Dictionary<string, XRefNode>? Properties { get; set; }

    /// <summary>The node of each item of an array value.</summary>
    public XRefNode? Items { get; set; }

    /// <summary>The node of each member the schema's <c>properties</c> does not declare and no <c>patternProperties</c> pattern
    /// matches.</summary>
    public XRefNode? AdditionalProperties { get; set; }

    /// <summary>With <see cref="AdditionalProperties"/>: the names the schema's <c>properties</c> declares.</summary>
    public FrozenSet<string> Declared { get; set; } = FrozenSet<string>.Empty;

    /// <summary>With <see cref="AdditionalProperties"/>: the schema's <c>patternProperties</c> patterns, compiled for
    /// the non-backtracking engine, so a match takes time linear in the name and its outcome never depends on the
    /// machine's load.</summary>
    public IReadOnlyList<Regex> Patterns { get; set; } = [];

    /// <summary>The node of an object member, or <see langword="null"/> when nothing there is typed.</summary>
    /// <param name="name">The member name.</param>
    /// <returns>The node.</returns>
    public XRefNode? Member(string name)
    {
        if (Properties is not null && Properties.TryGetValue(name, out var node))
            return node;
        if (AdditionalProperties is null || Declared.Contains(name))
            return null;
        foreach (var pattern in Patterns)
        {
            if (pattern.IsMatch(name))
                return null;
        }

        return AdditionalProperties;
    }
}

/// <summary>One place where an <c>x-ref</c> types a value of a model document (next-phase-1-design.md section 4.1).</summary>
/// <param name="Host">The element or sub-element that holds the value.</param>
/// <param name="Pointer">The value's pointer in the document.</param>
/// <param name="Field">The top-level property name.</param>
/// <param name="Value">The value as written: an id, ids joined by <c>.</c>, or text (a name, which validation reports).</param>
/// <param name="Ref">The keyword.</param>
/// <param name="Source">The schema that declares the property.</param>
/// <param name="Default">Whether the value is a stereotype's default (<c>defaultProperties</c>), which serves many hosts.</param>
internal readonly record struct XRefSite(PropertyHost Host, string Pointer, string Field, string Value, XRef Ref, PropertySchemaSource Source, bool Default);

/// <summary>
/// One holder of custom properties in a model document (next-phase-1-design.md section 4.1): the element, its attributes, enum
/// members and table columns, a process's states, transitions and events, and an operation's members (input, then each outcome's
/// returns), outcomes and errors (next-phase-2-design.md section 4.1). Every walk over <c>properties</c> (extension
/// validation, the <c>x-ref</c> walk of the index and the validator, localization and the batches) visits this set.
/// </summary>
/// <param name="Pointer">The holder's pointer in the document (<c>""</c> for the element).</param>
/// <param name="Id">The holder's id.</param>
/// <param name="KindName">The kind name extension files and stereotypes apply by (<c>attribute</c>, <c>column</c>, <c>state</c>...).</param>
/// <param name="Stereotypes">The holder's stereotype keys, as the file lists them.</param>
/// <param name="Properties">The holder's own <c>properties</c>.</param>
/// <param name="EffectiveKeys">
/// For the element, when the model declares default stereotypes: the keys schemas apply by, its effective set's with defaults
/// (<see cref="PropertySchemaSet.HostKeys"/>, next-phase-2-design.md section 3.1); <see langword="null"/> otherwise, and the
/// schemas apply by <paramref name="Stereotypes"/> with the keys they imply.
/// </param>
internal readonly record struct PropertyHost(string Pointer, string Id, string KindName, IReadOnlyList<string> Stereotypes,
    IReadOnlyDictionary<string, JsonElement> Properties, IReadOnlyList<string>? EffectiveKeys = null)
{
    /// <summary>The holders of a document, in walk order: the element, attributes, enum members, columns, states (depth first),
    /// transitions, events; for an operation its input members, then each outcome followed by its returns' members, then its
    /// errors.</summary>
    /// <param name="element">The top-level element.</param>
    /// <param name="effectiveKeys">The element's keys with defaults (<see cref="ModelSnapshot.PropertyKeys"/>), or <see langword="null"/>
    /// when the model declares no default stereotype.</param>
    /// <returns>The holders.</returns>
    public static IEnumerable<PropertyHost> Of(Element element, IReadOnlyList<string>? effectiveKeys)
    {
        ArgumentNullException.ThrowIfNull(element);
        yield return new PropertyHost("", element.Id, element.KindName, element.Stereotypes, element.Properties, effectiveKeys);
        IReadOnlyList<ModelAttribute> attributes = element switch
        {
            Entity e => e.Attributes,
            ValueObject v => v.Attributes,
            Relation r => r.Attributes,
            Stereotype s => s.Attributes,
            _ => [],
        };
        for (var i = 0; i < attributes.Count; i++)
            yield return Item(attributes[i], At("/attributes", i), "attribute");
        if (element is EnumType enumType)
        {
            for (var i = 0; i < enumType.Members.Count; i++)
                yield return Item(enumType.Members[i], At("/members", i), "enum-member");
        }

        if (element is Table table)
        {
            for (var i = 0; i < table.Columns.Count; i++)
                yield return Item(table.Columns[i], At("/columns", i), "column");
        }

        if (element is Operation operation)
        {
            for (var i = 0; i < operation.Input.Count; i++)
                yield return Item(operation.Input[i], At("/input", i), "operation-member");
            for (var i = 0; i < operation.Outcomes.Count; i++)
            {
                var outcome = operation.Outcomes[i];
                var at = At("/outcomes", i);
                yield return Item(outcome, at, "outcome");
                var members = outcome.Returns?.Members ?? [];
                for (var j = 0; j < members.Count; j++)
                    yield return Item(members[j], At(at + "/returns/members", j), "operation-member");
            }

            for (var i = 0; i < operation.Errors.Count; i++)
                yield return Item(operation.Errors[i], At("/errors", i), "error");
            yield break;
        }

        if (element is not Process process)
            yield break;
        foreach (var state in States(process.States, ""))
            yield return state;
        for (var i = 0; i < process.Transitions.Count; i++)
        {
            var t = process.Transitions[i];
            yield return new PropertyHost(At("/transitions", i), t.Id, "transition", t.Stereotypes, t.Properties);
        }

        for (var i = 0; i < process.Events.Count; i++)
        {
            var e = process.Events[i];
            yield return new PropertyHost(At("/events", i), e.Id, "event", e.Stereotypes, e.Properties);
        }
    }

    private static PropertyHost Item(ElementBase item, string pointer, string kindName) => new(pointer, item.Id, kindName, item.Stereotypes, item.Properties);

    private static IEnumerable<PropertyHost> States(IReadOnlyList<ProcessState> states, string pointer)
    {
        for (var i = 0; i < states.Count; i++)
        {
            var at = At(pointer + "/states", i);
            yield return new PropertyHost(at, states[i].Id, "state", states[i].Stereotypes, states[i].Properties);
            foreach (var nested in States(states[i].States, at))
                yield return nested;
        }
    }

    private static string At(string pointer, int index) => pointer + "/" + index.ToString(CultureInfo.InvariantCulture);
}

/// <summary>
/// One schema of custom properties: an extension file or a stereotype's <c>parameters</c> (next-phase-1-design.md section 4.2). It
/// holds the <c>x-ref</c> tree of each top-level property, the names marked <c>x-translatable</c>, and the schema compiled once on
/// first use (with <c>required</c>, and for a stereotype's defaults without it).
/// </summary>
internal sealed class PropertySchemaSource
{
    private const string BaseUri = "https://maquettiste.invalid/extensions/";
    private static readonly EvaluationOptions CompileCheck = new() { OutputFormat = OutputFormat.List, Culture = CultureInfo.InvariantCulture };
    private readonly Lazy<(JsonSchema? Schema, string? Error)> _compiled;
    private readonly Lazy<(JsonSchema? Schema, string? Error)> _lenient;

    internal PropertySchemaSource(string schema, string path, string hash, IReadOnlyList<string> kinds, IReadOnlyList<string> stereotypes,
        JsonElement properties, IReadOnlyList<string> required, IReadOnlyDictionary<string, XRefNode> refs,
        IReadOnlyList<(string Pointer, XRef Ref)> keywords, IReadOnlySet<string> translatable, string? stereotypeKey = null,
        IReadOnlyList<Diagnostic>? diagnostics = null)
    {
        Schema = schema;
        Path = path;
        Hash = hash;
        Kinds = kinds;
        Stereotypes = stereotypes;
        Properties = properties;
        Required = required;
        Refs = refs;
        Keywords = keywords;
        Translatable = translatable;
        StereotypeKey = stereotypeKey;
        Diagnostics = diagnostics ?? [];
        _compiled = new Lazy<(JsonSchema?, string?)>(() => Compile(withRequired: true), LazyThreadSafetyMode.ExecutionAndPublication);
        _lenient = new Lazy<(JsonSchema?, string?)>(() => Compile(withRequired: false), LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>
    /// The source of a stereotype's <c>parameters</c>: named <c>stereotype:&lt;key&gt;</c>, applying to the kinds the stereotype applies
    /// to on a holder that carries it; its keyword pointers are file pointers under <c>/parameters/properties</c>. Malformed keywords
    /// are left to the stereotype's own validation (<c>BuiltinRules.CheckStereotype</c>, MQ2009).
    /// </summary>
    /// <param name="stereotype">The stereotype, with parameters.</param>
    /// <param name="parameters">Its parameters.</param>
    /// <param name="path">Its file's repo-relative path.</param>
    /// <param name="hash">Its file's hash.</param>
    /// <param name="problem">Receives (pointer, message) per malformed keyword, or <see langword="null"/>.</param>
    /// <returns>The source.</returns>
    internal static PropertySchemaSource OfStereotype(Stereotype stereotype, StereotypeParameters parameters, string path, string hash,
        Action<string, string>? problem = null)
    {
        var (refs, keywords, translatable) = PropertySchemaSet.Read(parameters.Properties, "/parameters/properties", problem ?? ((_, _) => { }));
        return new PropertySchemaSource("stereotype:" + stereotype.Key, path, hash, stereotype.AppliesTo, [stereotype.Key], parameters.Properties,
            parameters.Required, refs, keywords, translatable, stereotype.Key);
    }

    /// <summary>The name references carry (<see cref="ReferenceInfo.Schema"/>): <c>extension:&lt;name&gt;</c> or
    /// <c>stereotype:&lt;key&gt;</c>.</summary>
    public string Schema { get; }

    /// <summary>The repo-relative path of the file that declares it.</summary>
    public string Path { get; }

    /// <summary>The file's hash.</summary>
    public string Hash { get; }

    /// <summary>The kind names it applies to; empty means every kind.</summary>
    public IReadOnlyList<string> Kinds { get; }

    /// <summary>The stereotype keys it applies to (a holder carrying one of them); empty means every holder.</summary>
    public IReadOnlyList<string> Stereotypes { get; }

    /// <summary>The <c>properties</c> object: one schema per property name.</summary>
    public JsonElement Properties { get; }

    /// <summary>The required property names.</summary>
    public IReadOnlyList<string> Required { get; }

    /// <summary>The <c>x-ref</c> tree of each top-level property that has a well-formed keyword.</summary>
    public IReadOnlyDictionary<string, XRefNode> Refs { get; }

    /// <summary>Every well-formed keyword with its pointer in the file, in file order.</summary>
    public IReadOnlyList<(string Pointer, XRef Ref)> Keywords { get; }

    /// <summary>The top-level property names marked <c>x-translatable</c>.</summary>
    public IReadOnlySet<string> Translatable { get; }

    /// <summary>The key of the stereotype whose <c>parameters</c> this is, or <see langword="null"/> for an extension file.</summary>
    public string? StereotypeKey { get; }

    /// <summary>MQ2009 for each malformed or misplaced <c>x-ref</c> of an extension file, in file order (none for a stereotype).</summary>
    public IReadOnlyList<Diagnostic> Diagnostics { get; }

    /// <summary>The compiled schema <c>{ type: object, properties, required }</c>, or <see langword="null"/> with the reason when
    /// it does not compile (MQ5004 for an extension file, MQ2019 for a stereotype's parameters).</summary>
    public (JsonSchema? Schema, string? Error) Compiled => _compiled.Value;

    /// <summary>The compiled schema without <c>required</c>, which a stereotype's defaults are checked against: a stereotype may leave
    /// a required parameter to its hosts.</summary>
    public (JsonSchema? Schema, string? Error) CompiledWithoutRequired => _lenient.Value;

    /// <summary>Whether the schema applies to a holder of this kind carrying these stereotypes.</summary>
    /// <param name="kindName">The holder's kind name.</param>
    /// <param name="stereotypes">The holder's stereotype keys.</param>
    /// <returns><see langword="true"/> when it applies.</returns>
    public bool AppliesTo(string kindName, IReadOnlyList<string> stereotypes) =>
        (Kinds.Count == 0 || Kinds.Contains(kindName, StringComparer.Ordinal))
        && (Stereotypes.Count == 0 || Stereotypes.Any(s => stereotypes.Contains(s, StringComparer.Ordinal)));

    /// <summary>
    /// The dialect custom property schemas compile with: the default one, which refuses unknown keywords (a typo is MQ5004), plus
    /// <c>x-ref</c> and <c>x-translatable</c> as annotations, so a schema that types a link or marks a text compiles.
    /// </summary>
    private static readonly global::Json.Schema.Dialect PropertyDialect = global::Json.Schema.Dialect.Default.With([new AnnotationOnly("x-ref"), new AnnotationOnly("x-translatable")]);

    private (JsonSchema?, string?) Compile(bool withRequired)
    {
        if (Properties.ValueKind != JsonValueKind.Object)
            return (null, "'properties' must be an object of property schemas.");
        var root = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = JsonNode.Parse(Properties.GetRawText()),
        };
        if (withRequired && Required.Count > 0)
            root["required"] = new JsonArray([.. Required.Select(r => (JsonNode?)JsonValue.Create(r))]);
        try
        {
            var element = JsonSerializer.SerializeToElement(root);
            var fileName = Path[(Path.LastIndexOf('/') + 1)..];
            var options = new BuildOptions { SchemaRegistry = new global::Json.Schema.SchemaRegistry(), Dialect = PropertyDialect };
            var schema = JsonSchema.Build(element, options, new Uri(BaseUri + Uri.EscapeDataString(fileName)));
            using var empty = JsonDocument.Parse("{}");
            _ = schema.Evaluate(empty.RootElement, CompileCheck); // surfaces unresolvable references now, once
            return (schema, null);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return (null, e.Message);
        }
    }
}

/// <summary>A keyword the evaluation ignores (the engine reads it itself): any value builds, and it always passes.</summary>
/// <param name="name">The keyword.</param>
internal sealed class AnnotationOnly(string name) : IKeywordHandler
{
    /// <inheritdoc/>
    public string Name => name;

    /// <inheritdoc/>
    public object? ValidateKeywordValue(JsonElement value) => null;

    /// <inheritdoc/>
    public void BuildSubschemas(KeywordData keyword, BuildContext context)
    {
    }

    /// <inheritdoc/>
    public KeywordEvaluation Evaluate(KeywordData keyword, EvaluationContext context) => KeywordEvaluation.Ignore;
}

/// <summary>
/// The custom property schemas of a snapshot (next-phase-1-design.md sections 4.1 and 4.2): the extension files by path, then each
/// stereotype's <c>parameters</c> (for each key, the ordinally first file). The index, the validator, localization and the batches
/// read <c>x-ref</c> sites and translatable names from it. A holder's stereotypes count with the ones they imply (one level, as
/// <see cref="ModelSnapshot.EffectiveStereotypes(IReadOnlyList{string})"/>), so a schema that applies to an implied stereotype applies to its carriers;
/// an element of a model with default stereotypes counts its effective set's keys (<see cref="HostKeys"/>), defaults included, which
/// its callers pass.
/// <see cref="Hash"/> covers the ordinal (path, hash) pairs of the extension files and the stereotype files, so a snapshot whose files
/// did not change reuses the previous set, and a source whose file did not change is reused with its compiled schema. Immutable and
/// thread-safe.
/// </summary>
internal sealed class PropertySchemaSet
{
    private static readonly FrozenSet<string> Unfollowed = new[]
    {
        "allOf", "anyOf", "oneOf", "prefixItems", "not", "if", "then", "else", "contains", "propertyNames", "unevaluatedItems",
        "unevaluatedProperties", "additionalItems", "patternProperties", "dependentSchemas", "$defs", "definitions", "dependencies",
    }.ToFrozenSet(StringComparer.Ordinal);

    private PropertySchemaSet(string hash, IReadOnlyList<PropertySchemaSource> sources, IReadOnlyList<Diagnostic> diagnostics,
        FrozenDictionary<string, IReadOnlyList<string>> implies)
    {
        Hash = hash;
        Sources = sources;
        Diagnostics = diagnostics;
        _implies = implies;
        HasRefs = sources.Any(s => s.Refs.Count > 0);
        HasTranslatable = sources.Any(s => s.Translatable.Count > 0);
        ByPath = sources.GroupBy(s => s.Path, StringComparer.Ordinal).ToFrozenDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
    }

    private readonly FrozenDictionary<string, IReadOnlyList<string>> _implies;

    /// <summary>The set of a model without extension files.</summary>
    public static PropertySchemaSet Empty { get; } = Create([], null);

    /// <summary>The hash of the files the set reads: ordinal (path, hash) pairs.</summary>
    public string Hash { get; }

    /// <summary>The schemas: extension files by path, then stereotype parameters by key.</summary>
    public IReadOnlyList<PropertySchemaSource> Sources { get; }

    /// <summary>The extension files' MQ2009 for each malformed or misplaced <c>x-ref</c>, and MQ7212 and MQ7213 for each misplaced
    /// <c>x-translatable</c>, in file order.</summary>
    public IReadOnlyList<Diagnostic> Diagnostics { get; }

    /// <summary>Whether any schema has an <c>x-ref</c> site (otherwise no document needs the walk).</summary>
    public bool HasRefs { get; }

    /// <summary>Whether any schema marks a property <c>x-translatable</c> (otherwise localization reads no property texts).</summary>
    public bool HasTranslatable { get; }

    private FrozenDictionary<string, PropertySchemaSource> ByPath { get; }

    /// <summary>The schema a file declares.</summary>
    /// <param name="path">The file's repo-relative path.</param>
    /// <returns>The source, or <see langword="null"/>.</returns>
    public PropertySchemaSource? SourceOf(string path) => ByPath.GetValueOrDefault(path);

    /// <summary>Builds the set, or returns <paramref name="previous"/> when its files are the same.</summary>
    /// <param name="extensions">The extension files.</param>
    /// <param name="previous">The previous snapshot's set, or <see langword="null"/>.</param>
    /// <param name="stereotypes">The stereotype documents (their <c>parameters</c> and <c>implies</c>), or <see langword="null"/> for
    /// none.</param>
    /// <returns>The set.</returns>
    public static PropertySchemaSet Create(IReadOnlyList<ExtensionDocument> extensions, PropertySchemaSet? previous,
        IReadOnlyList<ElementDocument>? stereotypes = null)
    {
        ArgumentNullException.ThrowIfNull(extensions);
        var ordered = extensions.OrderBy(e => e.Path, StringComparer.Ordinal).ToList();
        var stereotypeDocs = (stereotypes ?? []).Where(d => d.Element is Stereotype).OrderBy(d => d.Path, StringComparer.Ordinal).ToList();
        var fields = new List<string?>(ordered.Count * 2 + stereotypeDocs.Count * 2 + 2) { "mq-property-schemas-1" };
        foreach (var extension in ordered)
        {
            fields.Add(extension.Path);
            fields.Add(extension.Hash);
        }

        if (stereotypeDocs.Count > 0)
        {
            fields.Add("stereotypes");
            foreach (var document in stereotypeDocs)
            {
                fields.Add(document.Path);
                fields.Add(document.Hash);
            }
        }

        var hash = HashBuilder.Of([.. fields]);
        if (previous is not null && string.Equals(previous.Hash, hash, StringComparison.Ordinal))
            return previous;
        // A source whose file did not change is reused, with its compiled schema.
        var reusable = previous?.Sources.ToDictionary(s => (s.Schema, s.Path, s.Hash)) ?? [];
        var diagnostics = new List<Diagnostic>();
        var sources = new List<PropertySchemaSource>(ordered.Count);
        foreach (var extension in ordered)
        {
            var schema = extension.Schema;
            if (!reusable.TryGetValue(("extension:" + schema.Name, extension.Path, extension.Hash), out var source))
            {
                var problems = new List<Diagnostic>();
                void Problem(string pointer, string message) => problems.Add(RuleCatalog.Create("MQ2009", message, null, extension.Path, pointer));
                var (refs, keywords, translatable) = Read(schema.Properties, "/properties", Problem);
                foreach (var (rule, pointer, message) in TranslatableFindings(schema.Properties, "/properties", schema.AppliesTo.Kinds))
                    problems.Add(RuleCatalog.Create(rule, message, null, extension.Path, pointer));
                source = new PropertySchemaSource("extension:" + schema.Name, extension.Path, extension.Hash, schema.AppliesTo.Kinds,
                    schema.AppliesTo.Stereotypes, schema.Properties, schema.Required, refs, keywords, translatable, null, problems);
            }

            diagnostics.AddRange(source.Diagnostics);
            sources.Add(source);
        }

        // Stereotypes: for each key the ordinally first file, as the snapshot's stereotype lookup takes it.
        var implies = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var document in stereotypeDocs)
        {
            var stereotype = (Stereotype)document.Element;
            if (!keys.Add(stereotype.Key))
                continue;
            if (stereotype.Implies.Count > 0)
                implies[stereotype.Key] = stereotype.Implies;
            if (stereotype.Parameters is not { } parameters)
                continue;
            sources.Add(reusable.TryGetValue(("stereotype:" + stereotype.Key, document.Path, document.Hash), out var source)
                ? source
                : PropertySchemaSource.OfStereotype(stereotype, parameters, document.Path, document.Hash));
        }

        return new PropertySchemaSet(hash, sources, diagnostics, implies.ToFrozenDictionary(StringComparer.Ordinal));
    }

    /// <summary>
    /// A holder's stereotype keys with the keys they imply (one level), as the schemas apply by them; the list itself when nothing
    /// is implied. The order is the holder's, then the implied keys: applicability does not depend on it.
    /// </summary>
    /// <param name="stereotypes">The holder's stereotype keys, as its file lists them.</param>
    /// <returns>The keys.</returns>
    public IReadOnlyList<string> EffectiveKeys(IReadOnlyList<string> stereotypes)
    {
        ArgumentNullException.ThrowIfNull(stereotypes);
        if (_implies.Count == 0 || stereotypes.Count == 0)
            return stereotypes;
        List<string>? keys = null;
        foreach (var key in stereotypes)
        {
            if (!_implies.TryGetValue(key, out var implied))
                continue;
            foreach (var item in implied)
            {
                if (!(keys ?? stereotypes).Contains(item, StringComparer.Ordinal))
                    (keys ??= [.. stereotypes]).Add(item);
            }
        }

        return keys ?? stereotypes;
    }

    /// <summary>The schemas that apply to a holder, in set order: by its kind and its stereotypes, implied ones included.</summary>
    /// <param name="kindName">The holder's kind name.</param>
    /// <param name="stereotypes">The holder's stereotype keys, as its file lists them.</param>
    /// <returns>The sources.</returns>
    public IEnumerable<PropertySchemaSource> For(string kindName, IReadOnlyList<string> stereotypes)
    {
        var keys = EffectiveKeys(stereotypes);
        return Sources.Where(s => s.AppliesTo(kindName, keys));
    }

    /// <summary>
    /// The schemas that apply to a holder, in set order: by its kind and its effective keys when it carries them (an element of a model
    /// with default stereotypes, next-phase-2-design.md section 3.1), else by its own keys with the keys they imply.
    /// </summary>
    /// <param name="host">The holder.</param>
    /// <returns>The sources.</returns>
    public IEnumerable<PropertySchemaSource> For(PropertyHost host)
    {
        if (host.EffectiveKeys is not { } keys)
            return For(host.KindName, host.Stereotypes);
        return Sources.Where(s => s.AppliesTo(host.KindName, keys));
    }

    /// <summary>
    /// The keys schemas apply by for an element of a model with default stereotypes (next-phase-2-design.md section 3.1): its effective
    /// set's keys (implied stereotypes and defaults included, excluded defaults left out), then its own keys that name no stereotype, as
    /// <see cref="EffectiveKeys"/> keeps them. Without defaults the set is the one <see cref="EffectiveKeys"/> gives the own list.
    /// </summary>
    /// <param name="effective">The element's effective set (<see cref="ModelSnapshot.EffectiveStereotypes(Element)"/>).</param>
    /// <param name="own">The element's own keys, as its file lists them.</param>
    /// <returns>The keys.</returns>
    public static IReadOnlyList<string> HostKeys(IReadOnlyList<AppliedStereotype> effective, IReadOnlyList<string> own)
    {
        ArgumentNullException.ThrowIfNull(effective);
        ArgumentNullException.ThrowIfNull(own);
        var keys = new List<string>(effective.Count + own.Count);
        foreach (var applied in effective)
            keys.Add(applied.Stereotype.Key);
        foreach (var key in own)
        {
            if (!keys.Contains(key, StringComparer.Ordinal))
                keys.Add(key);
        }

        return keys;
    }

    /// <summary>
    /// The schemas a stereotype's <c>defaultProperties</c> are walked against: its own <c>parameters</c> and the extension files that
    /// name its key.
    /// </summary>
    /// <param name="key">The stereotype key.</param>
    /// <returns>The sources.</returns>
    public IEnumerable<PropertySchemaSource> ForDefaults(string key) =>
        Sources.Where(s => s.Stereotypes.Contains(key, StringComparer.Ordinal));

    /// <summary>The source of a stereotype's <c>parameters</c> in the set, or <see langword="null"/>.</summary>
    /// <param name="key">The stereotype key.</param>
    /// <returns>The source.</returns>
    public PropertySchemaSource? ParametersOf(string key) => Sources.FirstOrDefault(s => s.StereotypeKey == key);

    /// <summary>
    /// Every <c>x-ref</c> site of a document holding a string: each holder's own <c>properties</c> against the schemas that apply to
    /// it, and a stereotype's <c>defaultProperties</c> against the schemas that name its key. In holder order, then property name
    /// (ordinal), then schema order, then the value's own order. One value two schemas type is two sites.
    /// </summary>
    /// <param name="element">The top-level element.</param>
    /// <param name="effectiveKeys">
    /// The element's keys with defaults (<see cref="ModelSnapshot.PropertyKeys"/>, or <see cref="HostKeys"/> of its effective set), or
    /// <see langword="null"/> when the model declares no default stereotype (next-phase-2-design.md section 3.1).
    /// </param>
    /// <returns>The sites.</returns>
    public IEnumerable<XRefSite> Sites(Element element, IReadOnlyList<string>? effectiveKeys)
    {
        ArgumentNullException.ThrowIfNull(element);
        if (!HasRefs)
            yield break;
        foreach (var host in PropertyHost.Of(element, effectiveKeys))
        {
            if (host.Properties.Count == 0)
                continue;
            foreach (var site in SitesOf(host, host.Pointer + "/properties", host.Properties, For(host), false))
                yield return site;
        }

        if (element is Stereotype stereotype && stereotype.DefaultProperties.Count > 0)
        {
            var host = new PropertyHost("", stereotype.Id, stereotype.KindName, stereotype.Stereotypes, stereotype.Properties);
            foreach (var site in SitesOf(host, "/defaultProperties", stereotype.DefaultProperties, ForDefaults(stereotype.Key), true))
                yield return site;
        }
    }

    private static IEnumerable<XRefSite> SitesOf(PropertyHost host, string pointer, IReadOnlyDictionary<string, JsonElement> properties,
        IEnumerable<PropertySchemaSource> sources, bool isDefault)
    {
        var applicable = sources.Where(s => s.Refs.Count > 0).ToList();
        if (applicable.Count == 0)
            yield break;
        var found = new List<(string Pointer, string Value, XRef Ref)>();
        foreach (var name in properties.Keys.Order(StringComparer.Ordinal))
        {
            foreach (var source in applicable)
            {
                if (!source.Refs.TryGetValue(name, out var node))
                    continue;
                found.Clear();
                Walk(node, properties[name], pointer + "/" + XRef.Escape(name), found);
                foreach (var (at, value, xref) in found)
                    yield return new XRefSite(host, at, name, value, xref, source, isDefault);
            }
        }
    }

    /// <summary>Collects the strings an <c>x-ref</c> tree types in a value (pack parameter values share it with the documents).</summary>
    /// <param name="node">The tree of the value's schema.</param>
    /// <param name="value">The value.</param>
    /// <param name="pointer">The value's pointer.</param>
    /// <param name="found">Receives (pointer, string, keyword) per typed string, in the value's order (object members ordinal).</param>
    internal static void Walk(XRefNode node, JsonElement value, string pointer, List<(string Pointer, string Value, XRef Ref)> found)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.String when node.Ref is { } xref:
                found.Add((pointer, value.GetString()!, xref));
                break;
            case JsonValueKind.Array when node.Items is { } items:
                var i = 0;
                foreach (var item in value.EnumerateArray())
                    Walk(items, item, pointer + "/" + (i++).ToString(CultureInfo.InvariantCulture), found);
                break;
            case JsonValueKind.Object when node.Properties is not null || node.AdditionalProperties is not null:
                foreach (var member in value.EnumerateObject().OrderBy(m => m.Name, StringComparer.Ordinal))
                {
                    if (node.Member(member.Name) is { } child)
                        Walk(child, member.Value, pointer + "/" + XRef.Escape(member.Name), found);
                }

                break;
        }
    }

    /// <summary>Reads the <c>x-ref</c> trees and translatable names of a <c>properties</c> object at <paramref name="pointer"/>.</summary>
    internal static (IReadOnlyDictionary<string, XRefNode> Refs, IReadOnlyList<(string Pointer, XRef Ref)> Keywords, IReadOnlySet<string> Translatable) Read(
        JsonElement properties, string pointer, Action<string, string> problem)
    {
        var refs = new Dictionary<string, XRefNode>(StringComparer.Ordinal);
        var keywords = new List<(string, XRef)>();
        var translatable = new HashSet<string>(StringComparer.Ordinal);
        if (properties.ValueKind != JsonValueKind.Object)
            return (refs, keywords, translatable);
        foreach (var property in properties.EnumerateObject())
        {
            if (Scan(property.Value, pointer + "/" + XRef.Escape(property.Name), null, false, problem, keywords) is { } node)
                refs[property.Name] = node;
            if (property.Value.ValueKind == JsonValueKind.Object && property.Value.TryGetProperty("x-translatable", out var mark)
                && mark.ValueKind == JsonValueKind.True && OnString(property.Value, false))
            {
                translatable.Add(property.Name); // a misplaced mark is MQ7212 and marks nothing
            }
        }

        return (refs.ToFrozenDictionary(StringComparer.Ordinal), keywords, translatable.ToFrozenSet(StringComparer.Ordinal));
    }

    /// <summary>
    /// The node of one schema, or <see langword="null"/> when nothing under it is typed. <paramref name="via"/> names the applicator
    /// the walk does not follow that the schema sits under (a keyword there is MQ2009), or is <see langword="null"/>.
    /// </summary>
    private static XRefNode? Scan(JsonElement schema, string pointer, string? via, bool isItems, Action<string, string> problem,
        List<(string, XRef)> keywords)
    {
        if (schema.ValueKind != JsonValueKind.Object)
            return null;
        XRefNode? node = null;
        if (schema.TryGetProperty("x-ref", out var keyword))
        {
            if (via is not null)
            {
                problem(pointer + "/x-ref", $"x-ref sits under {via}, which the link walk does not follow: put it under properties, items or additionalProperties.");
            }
            else if (XRef.Parse(keyword, pointer + "/x-ref", OnString(schema, isItems), problem) is { } xref)
            {
                node = new XRefNode { Ref = xref };
                keywords.Add((pointer + "/x-ref", xref));
            }
        }

        foreach (var member in schema.EnumerateObject())
        {
            var at = pointer + "/" + XRef.Escape(member.Name);
            switch (member.Name)
            {
                case "properties" when member.Value.ValueKind == JsonValueKind.Object:
                    foreach (var child in member.Value.EnumerateObject())
                    {
                        if (Scan(child.Value, at + "/" + XRef.Escape(child.Name), via, false, problem, keywords) is { } c && via is null)
                        {
                            node ??= new XRefNode();
                            node.Properties ??= new Dictionary<string, XRefNode>(StringComparer.Ordinal);
                            node.Properties[child.Name] = c;
                        }
                    }

                    break;
                case "items" when member.Value.ValueKind == JsonValueKind.Object:
                    if (Scan(member.Value, at, via, true, problem, keywords) is { } items && via is null)
                        (node ??= new XRefNode()).Items = items;
                    break;
                case "additionalProperties" when member.Value.ValueKind == JsonValueKind.Object:
                    if (Scan(member.Value, at, via, false, problem, keywords) is { } additional && via is null
                        && Patterns(schema, pointer, problem) is { } patterns)
                    {
                        node ??= new XRefNode();
                        node.AdditionalProperties = additional;
                        node.Declared = schema.TryGetProperty("properties", out var declared) && declared.ValueKind == JsonValueKind.Object
                            ? declared.EnumerateObject().Select(p => p.Name).ToFrozenSet(StringComparer.Ordinal)
                            : FrozenSet<string>.Empty;
                        node.Patterns = patterns;
                    }

                    break;
                case var name when Unfollowed.Contains(name) || name == "items":
                    ScanUnfollowed(member.Value, name, at, via ?? name, problem, keywords);
                    break;
            }
        }

        return node;
    }

    /// <summary>Scans what an applicator the walk does not follow holds, reporting each keyword under it.</summary>
    /// <param name="value">The applicator's value.</param>
    /// <param name="keyword">The applicator, which decides the shape: an array of schemas, a map of schemas or one schema.</param>
    /// <param name="pointer">The value's pointer.</param>
    /// <param name="via">The outermost applicator not followed, named in the message.</param>
    /// <param name="problem">Receives (pointer, message) per problem.</param>
    /// <param name="keywords">Unused here: a keyword under an applicator not followed is never added.</param>
    private static void ScanUnfollowed(JsonElement value, string keyword, string pointer, string via, Action<string, string> problem, List<(string, XRef)> keywords)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Array:
                var i = 0;
                foreach (var item in value.EnumerateArray())
                    _ = Scan(item, pointer + "/" + (i++).ToString(CultureInfo.InvariantCulture), via, false, problem, keywords);
                break;
            case JsonValueKind.Object when keyword is "patternProperties" or "dependentSchemas" or "$defs" or "definitions" or "dependencies":
                foreach (var child in value.EnumerateObject())
                    _ = Scan(child.Value, pointer + "/" + XRef.Escape(child.Name), via, false, problem, keywords);
                break;
            case JsonValueKind.Object:
                _ = Scan(value, pointer, via, false, problem, keywords);
                break;
        }
    }

    /// <summary>
    /// The <c>patternProperties</c> patterns of a schema whose <c>additionalProperties</c> holds an <c>x-ref</c>,
    /// compiled for the non-backtracking engine with no timeout: a match takes time linear in the name, so which
    /// members the keyword types never depends on the machine's load. A pattern that engine cannot run (a
    /// backreference, a lookaround, an atomic group or a conditional) is MQ2009, and the keyword then types no member
    /// until the pattern changes (<see langword="null"/>).
    /// </summary>
    /// <param name="schema">The schema that holds <c>additionalProperties</c>.</param>
    /// <param name="pointer">The schema's pointer.</param>
    /// <param name="problem">Receives (pointer, message) per problem.</param>
    /// <returns>The patterns in file order, or <see langword="null"/> after a problem.</returns>
    private static IReadOnlyList<Regex>? Patterns(JsonElement schema, string pointer, Action<string, string> problem)
    {
        if (!schema.TryGetProperty("patternProperties", out var patterns) || patterns.ValueKind != JsonValueKind.Object)
            return [];
        var compiled = new List<Regex>();
        var refused = false;
        foreach (var pattern in patterns.EnumerateObject())
        {
            try
            {
                compiled.Add(new Regex(pattern.Name, RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, Regex.InfiniteMatchTimeout));
            }
            catch (NotSupportedException)
            {
                problem(pointer + "/patternProperties/" + XRef.Escape(pattern.Name),
                    $"x-ref sits under additionalProperties beside the pattern {pattern.Name}, which uses a backreference, a lookaround, an atomic "
                    + "group or a conditional: the link walk matches member names without backtracking, so rewrite the pattern without one.");
                refused = true;
            }
            catch (ArgumentException)
            {
                // the schema compile reports an invalid pattern (MQ5004)
            }
        }

        return refused ? null : compiled;
    }

    /// <summary>
    /// The holder kinds whose nodes localization translates (next-phase-1-design.md section 4.4): the localizable element kinds and
    /// the sub-elements <see cref="PropertyHost.Of"/> reaches on them (attributes, enum members, states, transitions, events, and an
    /// operation's members, outcomes and errors). Physical
    /// objects and their columns, mappings, diagrams and the vocabulary containers have no translations.
    /// </summary>
    internal static readonly FrozenSet<string> TranslatedHolderKinds = global::Maquettiste.Engine.Localization.LocaleChains.ElementKinds.Select(k => KindInfo.Get(k).Name)
        .Concat(["attribute", "enum-member", "state", "transition", "event", "operation-member", "outcome", "error"]).ToFrozenSet(StringComparer.Ordinal);

    /// <summary>The subschema applicators a nested <c>x-translatable</c> can sit under: those holding one schema, a map of schemas or an
    /// array of schemas.</summary>
    private static readonly FrozenSet<string> SchemaApplicators = new[]
    {
        "items", "additionalProperties", "not", "if", "then", "else", "contains", "propertyNames", "unevaluatedItems", "unevaluatedProperties",
        "additionalItems", "properties", "patternProperties", "dependentSchemas", "$defs", "definitions", "dependencies", "allOf", "anyOf",
        "oneOf", "prefixItems",
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>
    /// The misplaced <c>x-translatable</c> keywords of a <c>properties</c> object (next-phase-1-design.md section 4.4), as (rule,
    /// pointer, message) in file order: MQ7212 for one that is not a boolean, not on a string property, or nested below a top-level
    /// property; MQ7213 for a mark on a string property of a schema whose <paramref name="kinds"/> are all kinds without translations.
    /// </summary>
    /// <param name="properties">The <c>properties</c> object.</param>
    /// <param name="pointer">Its pointer in the file.</param>
    /// <param name="kinds">The kind names the schema applies to; empty means every kind.</param>
    /// <returns>The findings.</returns>
    internal static IEnumerable<(string Rule, string Pointer, string Message)> TranslatableFindings(JsonElement properties, string pointer, IReadOnlyList<string> kinds)
    {
        ArgumentNullException.ThrowIfNull(kinds);
        if (properties.ValueKind != JsonValueKind.Object)
            yield break;
        var untranslated = kinds.Count > 0 && !kinds.Any(TranslatedHolderKinds.Contains);
        foreach (var property in properties.EnumerateObject())
        {
            var at = pointer + "/" + XRef.Escape(property.Name);
            if (property.Value.ValueKind != JsonValueKind.Object)
                continue;
            if (property.Value.TryGetProperty("x-translatable", out var mark))
            {
                if (mark.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    yield return ("MQ7212", at + "/x-translatable", $"x-translatable on '{property.Name}' must be true or false.");
                else if (!OnString(property.Value, false))
                    yield return ("MQ7212", at + "/x-translatable", $"x-translatable fits a string property, and '{property.Name}' is not one: only texts are translated.");
                else if (mark.ValueKind == JsonValueKind.True && untranslated)
                    yield return ("MQ7213", at + "/x-translatable",
                        $"x-translatable on '{property.Name}' does nothing: the schema applies only to {string.Join(", ", kinds)}, which have no translations.");
            }

            foreach (var nested in NestedMarks(property.Value, at))
                yield return ("MQ7212", nested, "x-translatable sits below a top-level property: it fits top-level string properties only.");
        }
    }

    /// <summary>The pointers of the <c>x-translatable</c> keywords in the subschemas of a schema (not on the schema itself).</summary>
    private static IEnumerable<string> NestedMarks(JsonElement schema, string pointer)
    {
        foreach (var member in schema.EnumerateObject())
        {
            if (!SchemaApplicators.Contains(member.Name))
                continue;
            var at = pointer + "/" + XRef.Escape(member.Name);
            IEnumerable<(JsonElement Schema, string Pointer)> children = member.Name switch
            {
                "properties" or "patternProperties" or "dependentSchemas" or "$defs" or "definitions" or "dependencies"
                    when member.Value.ValueKind == JsonValueKind.Object =>
                    [.. member.Value.EnumerateObject().Select(c => (c.Value, at + "/" + XRef.Escape(c.Name)))],
                _ when member.Value.ValueKind == JsonValueKind.Array =>
                    [.. member.Value.EnumerateArray().Select((c, i) => (c, at + "/" + i.ToString(CultureInfo.InvariantCulture)))],
                _ => [(member.Value, at)],
            };
            foreach (var (child, childPointer) in children)
            {
                if (child.ValueKind != JsonValueKind.Object)
                    continue;
                if (child.TryGetProperty("x-translatable", out _))
                    yield return childPointer + "/x-translatable";
                foreach (var deeper in NestedMarks(child, childPointer))
                    yield return deeper;
            }
        }
    }

    /// <summary>Whether a schema types strings: <c>type</c> is <c>string</c> (or <c>[string, null]</c>), or it is an array's items
    /// with no <c>type</c>.</summary>
    private static bool OnString(JsonElement schema, bool isItems)
    {
        if (!schema.TryGetProperty("type", out var type))
            return isItems;
        return type.ValueKind switch
        {
            JsonValueKind.String => type.GetString() == "string",
            JsonValueKind.Array => type.EnumerateArray().Any(t => t.ValueKind == JsonValueKind.String && t.GetString() == "string")
                && type.EnumerateArray().All(t => t.ValueKind == JsonValueKind.String && t.GetString() is "string" or "null"),
            _ => false,
        };
    }
}
