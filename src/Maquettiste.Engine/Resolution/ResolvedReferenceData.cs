using System.Collections.Frozen;

namespace Maquettiste.Engine.Resolution;

/// <summary>
/// A resolved reference type (reference-types-seeds-localization.md section 1.5): a type whose values are data rows kept in seeds.
/// The engine models the intent only; how a project stores the rows is its storage choice, and none means the template decides.
/// </summary>
public sealed class RReferenceType : RElement
{
    /// <inheritdoc/>
    public override string Kind => "reference-type";

    /// <summary>The code field (the stored value of every single-valued attribute of this type).</summary>
    public RReferenceField Code { get; internal set; } = null!;

    /// <summary>The label field.</summary>
    public RReferenceField Label { get; internal set; } = null!;

    /// <summary>The user fields, stereotype virtual attributes included, stably sorted by order.</summary>
    public RList<RAttribute> Attributes { get; internal set; } = RList<RAttribute>.Empty;

    /// <summary>The rows of every seed of the type, in (seed name, seed id, file order).</summary>
    public RList<RRow> Rows { get; internal set; } = RList<RRow>.Empty;

    /// <summary>The seeds whose target is the type, by (name, id).</summary>
    public RList<RSeed> Seeds { get; internal set; } = RList<RSeed>.Empty;

    /// <summary>Every declared attribute whose type is this type (inherited copies excluded), by (owner id, order).</summary>
    public RList<RAttribute> UsedBy { get; internal set; } = RList<RAttribute>.Empty;

    /// <summary>The effective storage choice by database name (a choice with a null strategy is template-defined).</summary>
    public IReadOnlyDictionary<string, RStorageChoice> Storage { get; internal set; } = FrozenDictionary<string, RStorageChoice>.Empty;

    /// <summary>Rows by code key (<see cref="RRow.CodeKey"/>), for <c>row</c> and <c>refs</c>.</summary>
    internal IReadOnlyDictionary<string, RRow> RowsByCode { get; set; } = FrozenDictionary<string, RRow>.Empty;

    /// <summary>The row with a code, or <see langword="null"/>.</summary>
    /// <param name="code">The code (a string, or an integer for integer codes).</param>
    /// <returns>The row.</returns>
    internal RRow? RowOf(object? code) => code is null ? null : RowsByCode.GetValueOrDefault(RRow.KeyOf(code));
}

/// <summary>The code or label field of a reference type.</summary>
public sealed class RReferenceField
{
    /// <summary>The field id (translations key on it).</summary>
    public string Id { get; internal set; } = "";

    /// <summary>The logical type: <c>string</c> for a label; <c>string</c>, <c>int16</c>, <c>int32</c> or <c>int64</c> for a code.</summary>
    public string Type { get; internal set; } = "string";

    /// <summary>The length facet.</summary>
    public int? Length { get; internal set; }

    /// <summary>The pattern a code must match.</summary>
    public string? Pattern { get; internal set; }

    /// <summary>The display name (defaults to <c>Code</c> or <c>Label</c>).</summary>
    public string DisplayName { get; internal set; } = "";

    /// <summary>The description text.</summary>
    public string? Description { get; internal set; }
}

/// <summary>One row of a reference type.</summary>
public sealed class RRow : RObject
{
    private IReadOnlyDictionary<string, object?>? _refs;
    private Func<RRow, IReadOnlyDictionary<string, object?>>? _refsFactory;

    /// <inheritdoc/>
    public override string Kind => "row";

    /// <summary>The code: a string, or a long for an integer code type.</summary>
    public object? Code { get; internal set; }

    /// <summary>The label.</summary>
    public string? Label { get; internal set; }

    /// <summary>The description.</summary>
    public string? Description { get; internal set; }

    /// <summary>
    /// The user fields by field name, as plain values. A reference-typed field holds its code (or a list of codes), never a
    /// nested row, so rows that reference each other stay finite; <see cref="Refs"/> resolves the codes.
    /// </summary>
    public IReadOnlyDictionary<string, object?> Values { get; internal set; } = FrozenDictionary<string, object?>.Empty;

    /// <summary>
    /// The reference-typed fields by name, resolved lazily to their <see cref="RRow"/> (or a list of rows for a collection field;
    /// null for an empty cell or an unknown code). <c>json</c> never follows it.
    /// </summary>
    public IReadOnlyDictionary<string, object?> Refs
    {
        get
        {
            if (Volatile.Read(ref _refs) is { } refs)
                return refs;
            var factory = _refsFactory;
            var built = factory is null ? FrozenDictionary<string, object?>.Empty : factory(this);
            return Interlocked.CompareExchange(ref _refs, built, null) ?? built;
        }
    }

    /// <summary>The seed that holds the row.</summary>
    public RSeed Seed { get; internal set; } = null!;

    /// <summary>The reference type.</summary>
    public RReferenceType Type { get; internal set; } = null!;

    /// <summary>The row's position among the type's rows (0-based).</summary>
    public int Order { get; internal set; }

    /// <summary>The key of <see cref="Code"/> in <see cref="RReferenceType.RowsByCode"/>.</summary>
    internal string CodeKey => KeyOf(Code);

    internal void SetRefs(Func<RRow, IReadOnlyDictionary<string, object?>> factory) => _refsFactory = factory;

    /// <summary>The lookup key of a code: strings as themselves, integers in invariant digits with a <c>#</c> prefix.</summary>
    internal static string KeyOf(object? code) => code switch
    {
        null => "",
        string s => s,
        long or int or short or byte => "#" + Convert.ToInt64(code, System.Globalization.CultureInfo.InvariantCulture).ToString(System.Globalization.CultureInfo.InvariantCulture),
        decimal m when decimal.Truncate(m) == m => "#" + ((long)m).ToString(System.Globalization.CultureInfo.InvariantCulture),
        double d when Math.Truncate(d) == d => "#" + ((long)d).ToString(System.Globalization.CultureInfo.InvariantCulture),
        _ => Convert.ToString(code, System.Globalization.CultureInfo.InvariantCulture) ?? "",
    };
}

/// <summary>A resolved seed (section 2.5): rows of data for an entity, a relation or a reference type.</summary>
public sealed class RSeed : RElement
{
    /// <inheritdoc/>
    public override string Kind => "seed";

    /// <summary>The target: an <see cref="REntity"/>, an <see cref="RRelation"/> or an <see cref="RReferenceType"/>.</summary>
    public IResolvedObject? Target { get; internal set; }

    /// <summary>The columns, in file order.</summary>
    public IReadOnlyList<RSeedColumn> Columns { get; internal set; } = [];

    /// <summary>The rows, in file order.</summary>
    public RList<RSeedRow> Rows { get; internal set; } = RList<RSeedRow>.Empty;

    private RList<RSeedRow>? _orderedRows;
    private Func<RList<RSeedRow>>? _orderedRowsFactory;

    /// <summary>
    /// The rows in insert-dependency order: a row that another row of this seed names comes first (Kahn, ties by file order). Ordered on
    /// first read, so a large seed that nothing renders is never ordered (and its cells never converted).
    /// </summary>
    public RList<RSeedRow> OrderedRows
    {
        get
        {
            if (Volatile.Read(ref _orderedRows) is { } rows)
                return rows;
            var built = _orderedRowsFactory?.Invoke() ?? RList<RSeedRow>.Empty;
            return Interlocked.CompareExchange(ref _orderedRows, built, null) ?? built;
        }
        internal set => _orderedRows = value;
    }

    internal void SetOrderedRows(Func<RList<RSeedRow>> factory) => _orderedRowsFactory = factory;
}

/// <summary>A column of a seed.</summary>
public sealed class RSeedColumn
{
    /// <summary>The column name: <c>code</c>, <c>label</c>, <c>description</c>, the attribute name or the end role.</summary>
    public string Name { get; internal set; } = "";

    /// <summary><c>builtin</c>, <c>attribute</c> or <c>end</c>.</summary>
    public string Kind { get; internal set; } = "builtin";

    /// <summary>The attribute, for an attribute column.</summary>
    public RAttribute? Attribute { get; internal set; }

    /// <summary>The relation end, for an end column.</summary>
    public REnd? End { get; internal set; }
}

/// <summary>One row of a seed.</summary>
public sealed class RSeedRow : RObject
{
    private IReadOnlyDictionary<string, object?>? _refs;
    private Func<RSeedRow, IReadOnlyDictionary<string, object?>>? _refsFactory;

    /// <inheritdoc/>
    public override string Kind => "seed-row";

    /// <summary>The seed.</summary>
    public RSeed Seed { get; internal set; } = null!;

    /// <summary>The row's position in the file (0-based).</summary>
    public int Order { get; internal set; }

    private IReadOnlyDictionary<string, object?>? _values;
    private Func<IReadOnlyDictionary<string, object?>>? _valuesFactory;

    /// <summary>
    /// The cells by column name, as plain values: enum cells as <see cref="REnumMember"/>, reference cells as codes (or lists of
    /// codes), end cells as row ids; a missing or null cell is null. A row of an entity's or a relation's seed converts its cells on
    /// first read, so the rows of a large seed cost nothing until a template (or the resolved model's reader) reads them.
    /// </summary>
    public IReadOnlyDictionary<string, object?> Values
    {
        get
        {
            if (Volatile.Read(ref _values) is { } values)
                return values;
            var built = _valuesFactory?.Invoke() ?? FrozenDictionary<string, object?>.Empty;
            return Interlocked.CompareExchange(ref _values, built, null) ?? built;
        }
        internal set => _values = value;
    }

    /// <summary>Whether <see cref="Values"/> has been converted (tests: a preview that does not read a seed leaves its rows alone).</summary>
    internal bool ValuesBuilt => Volatile.Read(ref _values) is not null;

    internal void SetValues(Func<IReadOnlyDictionary<string, object?>> factory) => _valuesFactory = factory;

    /// <summary>
    /// Reference and end cells by column name, resolved lazily to an <see cref="RRow"/> or an <see cref="RSeedRow"/> (a list for a
    /// collection). <c>json</c> never follows it, so rows that reference each other serialize finitely.
    /// </summary>
    public IReadOnlyDictionary<string, object?> Refs
    {
        get
        {
            if (Volatile.Read(ref _refs) is { } refs)
                return refs;
            var factory = _refsFactory;
            var built = factory is null ? FrozenDictionary<string, object?>.Empty : factory(this);
            return Interlocked.CompareExchange(ref _refs, built, null) ?? built;
        }
    }

    internal void SetRefs(Func<RSeedRow, IReadOnlyDictionary<string, object?>> factory) => _refsFactory = factory;
}

/// <summary>How an attribute uses a reference type (section 1.5).</summary>
public sealed class RReferenceUsage : RObject
{
    /// <inheritdoc/>
    public override string Kind => "reference-usage";

    /// <summary>The reference type.</summary>
    public RReferenceType Type { get; internal set; } = null!;

    /// <summary>Whether the attribute holds many codes.</summary>
    public bool IsCollection { get; internal set; }

    /// <summary>Whether a value is required.</summary>
    public bool Required { get; internal set; }

    /// <summary>The rows the attribute allows: <c>validation.allowedValues</c> applied to the type's rows, in row order.</summary>
    public RList<RRow> Allowed { get; internal set; } = RList<RRow>.Empty;

    /// <summary>The row of the default code (the first code of a collection default), or <see langword="null"/>.</summary>
    public RRow? DefaultRow { get; internal set; }

    /// <summary>The effective storage choice by database name.</summary>
    public IReadOnlyDictionary<string, RStorageChoice> Storage { get; internal set; } = FrozenDictionary<string, RStorageChoice>.Empty;
}

/// <summary>The effective storage choice of a reference type in one database.</summary>
public sealed class RStorageChoice : RObject
{
    /// <inheritdoc/>
    public override string Kind => "storage-choice";

    /// <summary>The project's strategy key, or <see langword="null"/> for template-defined.</summary>
    public string? Strategy { get; internal set; }

    /// <summary>The choice's options, as plain values.</summary>
    public IReadOnlyDictionary<string, object?> Options { get; internal set; } = FrozenDictionary<string, object?>.Empty;

    /// <summary>Where the choice came from: <c>type</c>, <c>database</c>, <c>project</c>, or <see langword="null"/> for none.</summary>
    public string? Source { get; internal set; }

    /// <summary>The strategy's declaration in <c>referenceData.strategies</c>: its description.</summary>
    public string? Description { get; internal set; }

    /// <summary>Whether the declared strategy supports collections in this database's dialect (false when template-defined or undeclared).</summary>
    public bool Collections { get; internal set; }
}
