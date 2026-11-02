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

    private RList<RRow> _rows = RList<RRow>.Empty;
    private IReadOnlyDictionary<string, RRow> _rowsByCode = FrozenDictionary<string, RRow>.Empty;
    private Lazy<(RList<RRow> Rows, IReadOnlyDictionary<string, RRow> ByCode)>? _scriptedRows;

    /// <summary>
    /// The rows of every seed of the type: seed by seed in merge order (<see cref="RSeed"/>s by package qualified name with none first,
    /// name, id), each in file order. A type with a scripted seed (next-phase-2-design.md section 3.4) builds them on first read, running
    /// the script; a script that fails makes the read fail with its MQ7127.
    /// </summary>
    public RList<RRow> Rows
    {
        get => _scriptedRows?.Value.Rows ?? _rows;
        internal set => _rows = value;
    }

    /// <summary>
    /// The membership keys of <see cref="Rows"/> known before any rows script runs: the keys a reader of the rows depends on that do not
    /// come from a run (a usage's dependencies take them, so resolving never runs a script).
    /// </summary>
    internal IReadOnlyList<string> RowKeys { get; set; } = [];

    /// <summary>Whether the type has a seed with a rows script, whose rows are built on first read.</summary>
    internal bool HasScriptedRows => _scriptedRows is not null;

    /// <summary>Builds the rows on first read (a type with a scripted seed); the factory runs once.</summary>
    /// <param name="factory">The rows and their index by code.</param>
    internal void SetRows(Func<(RList<RRow> Rows, IReadOnlyDictionary<string, RRow> ByCode)> factory) =>
        _scriptedRows = new(factory, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>The seeds whose target is the type, in merge order (package qualified name with none first, name, id).</summary>
    public RList<RSeed> Seeds { get; internal set; } = RList<RSeed>.Empty;

    /// <summary>Every declared attribute whose type is this type (inherited copies excluded), by (owner id, order).</summary>
    public RList<RAttribute> UsedBy { get; internal set; } = RList<RAttribute>.Empty;

    /// <summary>The effective storage choice by database name (a choice with a null strategy is template-defined).</summary>
    public IReadOnlyDictionary<string, RStorageChoice> Storage { get; internal set; } = FrozenDictionary<string, RStorageChoice>.Empty;

    /// <summary>
    /// The bindings by database name (2026-10-11, optional): the table file that holds the rows in that database. A binding wins over
    /// <see cref="Storage"/> there, so a template does not realize the storage choice again for that database.
    /// </summary>
    public IReadOnlyDictionary<string, RReferenceTypeBinding> Bindings { get; internal set; } = FrozenDictionary<string, RReferenceTypeBinding>.Empty;

    private IReadOnlyList<RFormerCode> _formerCodes = [];
    private Lazy<IReadOnlyList<RFormerCode>>? _scriptedFormerCodes;

    /// <summary>
    /// The codes the type's rows used to have (next-phase-2-design.md section 3.5), sorted by code text (ordinal), each with the row that
    /// replaced it. An entry whose <c>replacedBy</c> names no row of the type (MQ7018, MQ2001) is left out. <c>row</c> finds live codes
    /// only. A type with a scripted seed builds them with its rows, on first read.
    /// </summary>
    public IReadOnlyList<RFormerCode> FormerCodes
    {
        get => _scriptedFormerCodes?.Value ?? _formerCodes;
        internal set => _formerCodes = value;
    }

    /// <summary>Builds the former codes on first read (a type with a scripted seed, whose rows are built then); the factory runs once.</summary>
    /// <param name="factory">The former codes.</param>
    internal void SetFormerCodes(Func<IReadOnlyList<RFormerCode>> factory) =>
        _scriptedFormerCodes = new(factory, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Rows by code key (<see cref="RRow.CodeKey"/>), for <c>row</c> and <c>refs</c>.</summary>
    internal IReadOnlyDictionary<string, RRow> RowsByCode
    {
        get => _scriptedRows?.Value.ByCode ?? _rowsByCode;
        set => _rowsByCode = value;
    }

    /// <summary>The row with a code, or <see langword="null"/>.</summary>
    /// <param name="code">The code (a string, or an integer for integer codes).</param>
    /// <returns>The row.</returns>
    internal RRow? RowOf(object? code) => code is null ? null : RowsByCode.GetValueOrDefault(RRow.KeyOf(code));
}

/// <summary>
/// A code a row of a reference type used to have (next-phase-2-design.md section 3.5), and the row that replaced it: a rename keeps the
/// old code with the row itself, a retire or a merge with the row that took over. Generated code keeps it as an alias of that row. Its
/// id is the type id and the code joined by <c>:</c>; it is part of the type's document, so it depends on the type and the row.
/// </summary>
public sealed class RFormerCode : RObject
{
    /// <inheritdoc/>
    public override string Kind => "former-code";

    /// <summary>The former code: a string, or a long for an integer code type (as <see cref="RRow.Code"/>).</summary>
    public object? Code { get; internal set; }

    /// <summary>The row that holds the code's value now.</summary>
    public RRow ReplacedBy { get; internal set; } = null!;
}

/// <summary>
/// A resolved reference type binding (2026-10-11): the table file of one database that holds the type's rows, the column each field
/// fills and the constant columns. The database's seed tables carry the rows through it (source <c>list</c>).
/// </summary>
public sealed class RReferenceTypeBinding : RObject
{
    /// <inheritdoc/>
    public override string Kind => "reference-type-binding";

    /// <summary>The reference type.</summary>
    public RReferenceType Type { get; internal set; } = null!;

    /// <summary>The database.</summary>
    public RDatabase Database { get; internal set; } = null!;

    /// <summary>The table that holds the rows.</summary>
    public RTable Table { get; internal set; } = null!;

    /// <summary>The field map, in file order (entries whose field or column does not resolve are left out; MQ7118 reports them).</summary>
    public IReadOnlyList<RReferenceBindingField> Fields { get; internal set; } = [];

    /// <summary>The constant columns, in file order.</summary>
    public IReadOnlyList<RBindingConstant> Constants { get; internal set; } = [];

    /// <summary>The column the code fills, or <see langword="null"/> when none does (MQ7118).</summary>
    public RColumn? CodeColumn { get; internal set; }

    /// <summary>The binding's description text, when it has one.</summary>
    public string? Description { get; internal set; }
}

/// <summary>One entry of a reference type binding's field map.</summary>
public sealed class RReferenceBindingField
{
    /// <summary>The field: <c>code</c>, <c>label</c>, <c>description</c>, or a user field's name (the row's cell name).</summary>
    public string Name { get; internal set; } = "";

    /// <summary>The user field, or <see langword="null"/> for a built-in one.</summary>
    public RAttribute? Attribute { get; internal set; }

    /// <summary>The column the field fills.</summary>
    public RColumn Column { get; internal set; } = null!;
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

/// <summary>A resolved seed (section 2.5): rows of data for an entity, a relation, a reference type or a table.</summary>
public sealed class RSeed : RElement
{
    /// <inheritdoc/>
    public override string Kind => "seed";

    /// <summary>The longest form 5.1 registers for the seed: its stored package path and name, else the name.</summary>
    public string QualifiedName { get; internal set; } = "";

    /// <summary>The target: an <see cref="REntity"/>, an <see cref="RRelation"/>, an <see cref="RReferenceType"/> or an <see cref="RTable"/>.</summary>
    public IResolvedObject? Target { get; internal set; }

    /// <summary>The environments the rows belong to; empty means every environment.</summary>
    public IReadOnlyList<string> Environments { get; internal set; } = [];

    /// <summary><c>once</c> (insert the rows whose key is missing) or <c>converge</c> (also update changed rows).</summary>
    public string Apply { get; internal set; } = "once";

    /// <summary>With <c>converge</c>: whether a row removed from the seed is deleted from the database.</summary>
    public bool Delete { get; internal set; }

    /// <summary>A table seed's row key: the id of the unique constraint it names, or <see langword="null"/> for the primary key.</summary>
    public string? Key { get; internal set; }

    /// <summary>The CSV file the rows are kept in (<c>rowsFrom.file</c>), or <see langword="null"/> when the seed file holds them.</summary>
    public string? RowsFrom { get; internal set; }

    /// <summary>
    /// The rows script (<c>rowsFrom.script</c>, a path under <c>extensions/</c> such as <c>seeds/audited-records.js</c>;
    /// next-phase-2-design.md section 3.4), or <see langword="null"/> when the rows are written.
    /// </summary>
    public string? RowsScript { get; internal set; }

    /// <summary>The columns, in file order.</summary>
    public IReadOnlyList<RSeedColumn> Columns { get; internal set; } = [];

    private RList<RSeedRow> _rows = RList<RSeedRow>.Empty;
    private Lazy<RList<RSeedRow>>? _scriptedRows;

    /// <summary>
    /// The rows, in file order, or for a seed with a rows script in the script's order. Scripted rows are built on first read, running the
    /// script; their ids derive from the seed id and the row key, and they carry the script's <c>x:</c> key and the keys the run read. A
    /// script that fails makes the read fail with its MQ7127, so the unit that read the rows fails.
    /// </summary>
    public RList<RSeedRow> Rows
    {
        get => _scriptedRows?.Value ?? _rows;
        internal set => _rows = value;
    }

    /// <summary>Builds the rows on first read (a seed with a rows script); the factory runs once.</summary>
    /// <param name="factory">The rows.</param>
    internal void SetRows(Func<RList<RSeedRow>> factory) => _scriptedRows = new(factory, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Whether <see cref="Rows"/> is built (tests: a unit that does not read a scripted seed leaves its script alone).</summary>
    internal bool RowsBuilt => _scriptedRows is null || _scriptedRows.IsValueCreated;

    /// <summary>
    /// The rows, or none when the seed's rows script failed: for readers that list what is there (the resolved model's records) rather
    /// than render from it.
    /// </summary>
    internal RList<RSeedRow> RowsOrNone
    {
        get
        {
            try
            {
                return Rows;
            }
            catch (Scripting.ScriptErrorException)
            {
                return RList<RSeedRow>.Empty;
            }
        }
    }

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

    private FrozenDictionary<string, RSeedRow>? _rowsByKey;

    /// <summary>
    /// The cells that find a row of the seed (next-phase-1-design.md section 5.3, <c>seed_row</c>), in key order: <c>code</c> for a
    /// reference type's seed, the key columns of a table seed (the unique constraint <see cref="Key"/> names, else the primary key),
    /// the key attributes of an entity's seed; <see langword="null"/> for a relation's seed and a target without a key.
    /// </summary>
    internal IReadOnlyList<string>? KeyCells => Target switch
    {
        RReferenceType => ["code"],
        RTable table => (Key is { } id && table.Uniques.FirstOrDefault(u => string.Equals(u.Id, id, StringComparison.Ordinal)) is { } unique
            ? unique.Columns
            : table.PrimaryKey?.Columns) is { Count: > 0 } columns ? [.. columns.Select(c => c.Name)] : null,
        REntity entity => entity.Key is { Attributes.Count: > 0 } key ? [.. key.Attributes.Select(a => a.Name)] : null,
        _ => null,
    };

    /// <summary>
    /// The row whose <see cref="KeyCells"/> hold these values, compared as <see cref="RRow.KeyOf"/> compares codes (an enum member by its
    /// name), or <see langword="null"/>. The index is built on first use; a key repeated in the seed (MQ7001, MQ7102, MQ7112) finds its
    /// first row.
    /// </summary>
    /// <param name="key">One value per key cell, in key order.</param>
    /// <returns>The row.</returns>
    internal RSeedRow? RowOf(IReadOnlyList<object?> key)
    {
        var index = Volatile.Read(ref _rowsByKey) ?? BuildRowIndex();
        return KeyText(key) is { } text ? index.GetValueOrDefault(text) : null;
    }

    private FrozenDictionary<string, RSeedRow> BuildRowIndex()
    {
        var cells = KeyCells ?? [];
        var byKey = new Dictionary<string, RSeedRow>(StringComparer.Ordinal);
        if (cells.Count > 0)
        {
            foreach (var row in Rows)
            {
                if (KeyText([.. cells.Select(c => row.Values.GetValueOrDefault(c))]) is { } text)
                    byKey.TryAdd(text, row);
            }
        }

        var built = byKey.ToFrozenDictionary(StringComparer.Ordinal);
        return Interlocked.CompareExchange(ref _rowsByKey, built, null) ?? built;
    }

    /// <summary>A key's lookup text: each part as <see cref="RRow.KeyOf"/> writes it, length-prefixed; <see langword="null"/> when a part is null.</summary>
    private static string? KeyText(IReadOnlyList<object?> parts)
    {
        var text = new System.Text.StringBuilder();
        foreach (var part in parts)
        {
            if (part is null)
                return null;
            var key = RRow.KeyOf(part is REnumMember member ? member.Name : part);
            text.Append(key.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(':').Append(key);
        }

        return text.ToString();
    }
}

/// <summary>A column of a seed.</summary>
public sealed class RSeedColumn
{
    /// <summary>
    /// The column name: <c>code</c>, <c>label</c>, <c>description</c>, the attribute name, the end role, a table column's name, or a
    /// foreign key's name for a foreign-key entry of a table seed.
    /// </summary>
    public string Name { get; internal set; } = "";

    /// <summary><c>builtin</c>, <c>attribute</c>, <c>end</c>, <c>column</c> or <c>foreign-key</c> (a table seed's).</summary>
    public string Kind { get; internal set; } = "builtin";

    /// <summary>The table column, for a column of a table seed.</summary>
    public RColumn? Column { get; internal set; }

    /// <summary>
    /// The foreign key, for a foreign-key entry of a table seed (next-phase-2-design.md section 3.4): its cell names the referenced row
    /// by that table's row key in seed terms, and the key's own columns take that row's values in database terms.
    /// </summary>
    public RForeignKey? ForeignKey { get; internal set; }

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
    /// codes), end cells as row ids, a foreign-key entry's cell as written under the foreign key's name (the referenced row's key in
    /// seed terms, a list for a composite key); a missing or null cell is null. A row of an entity's or a relation's seed converts its cells on
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

    private RList<RRow> _allowed = RList<RRow>.Empty;
    private RRow? _defaultRow;
    private Lazy<(RList<RRow> Allowed, RRow? DefaultRow)>? _scripted;

    /// <summary>
    /// The rows the attribute allows: <c>validation.allowedValues</c> applied to the type's rows, in row order. Built on first read when
    /// the type has a scripted seed.
    /// </summary>
    public RList<RRow> Allowed
    {
        get => _scripted?.Value.Allowed ?? _allowed;
        internal set => _allowed = value;
    }

    /// <summary>The row of the default code (the first code of a collection default), or <see langword="null"/>.</summary>
    public RRow? DefaultRow
    {
        get => _scripted is { } scripted ? scripted.Value.DefaultRow : _defaultRow;
        internal set => _defaultRow = value;
    }

    /// <summary>Builds <see cref="Allowed"/> and <see cref="DefaultRow"/> on first read (a type with a scripted seed).</summary>
    /// <param name="factory">The allowed rows and the default row.</param>
    internal void SetRows(Func<(RList<RRow> Allowed, RRow? DefaultRow)> factory) => _scripted = new(factory, LazyThreadSafetyMode.ExecutionAndPublication);

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
