namespace Maquettiste.Engine.Resolution;

/// <summary>
/// A resolved binding (erratum E43, engine-design.md section 7, "Bindings and materialize"): how an entity reads from and writes to one
/// database, with its source resolved to a table, view or query of that database, its constants, its field map resolved to attributes
/// and columns, every source column accounted for with its status, the write table, the delete plan and the key. Its annotations are
/// the binding entry's own description, tags and properties. <c>BindingSql</c> (the template helper <c>binding_sql</c>) renders its
/// statements per dialect.
/// </summary>
public sealed class REntityBinding : RAnnotated
{
    /// <inheritdoc/>
    public override string Kind => "binding";

    /// <summary>The entity.</summary>
    public REntity Entity { get; internal set; } = null!;

    /// <summary>The database.</summary>
    public RDatabase Database { get; internal set; } = null!;

    /// <summary><c>table</c>, <c>view</c> or <c>query</c>; <see langword="null"/> when the source does not resolve (MQ4044).</summary>
    public string? SourceKind { get; internal set; }

    /// <summary>The source table, when the source is a table.</summary>
    public RTable? SourceTable { get; internal set; }

    /// <summary>The source view, when the source is a view.</summary>
    public RView? SourceView { get; internal set; }

    /// <summary>The source query, when the source is a query.</summary>
    public RQuery? SourceQuery { get; internal set; }

    /// <summary>The source's name (table, view or query name).</summary>
    public string SourceName { get; internal set; } = "";

    /// <summary>The constants, in file order.</summary>
    public IReadOnlyList<RBindingConstant> Constants { get; internal set; } = [];

    /// <summary>The field map, in file order.</summary>
    public IReadOnlyList<RBindingField> Fields { get; internal set; } = [];

    /// <summary>Every column of the source, in its order, with what accounts for it.</summary>
    public IReadOnlyList<RBindingColumn> Columns { get; internal set; } = [];

    /// <summary>Every column of the write table when it is not the source, with what accounts for it; empty otherwise.</summary>
    public IReadOnlyList<RBindingColumn> WriteColumns { get; internal set; } = [];

    /// <summary>Whether the binding writes (inserts and updates).</summary>
    public bool Writes { get; internal set; }

    /// <summary>The table the binding writes, or <see langword="null"/> when it does not write.</summary>
    public RTable? WriteTable { get; internal set; }

    /// <summary><c>key</c>, <c>soft</c> or <c>none</c>.</summary>
    public string Delete { get; internal set; } = "none";

    /// <summary>The column a soft delete sets, with <see cref="Delete"/> <c>soft</c>.</summary>
    public RColumn? SoftDeleteColumn { get; internal set; }

    /// <summary>The value a soft delete sets (a plain value; <see langword="null"/> is SQL NULL).</summary>
    public object? SoftDeleteValue { get; internal set; }

    /// <summary>The fields that map the entity's key attributes, in key order.</summary>
    public IReadOnlyList<RBindingField> Key { get; internal set; } = [];

    /// <summary>The key fields whose value the database generates on insert (identity, a sequence, a default the binding marks).</summary>
    public IReadOnlyList<RBindingField> Generated { get; internal set; } = [];

    /// <summary>The binding entry as the entity's file writes it.</summary>
    internal Model.EntityBinding? Definition { get; set; }

    /// <summary>The index of the binding in the entity's <c>bindings</c> (for diagnostics).</summary>
    internal int Index { get; set; }
}

/// <summary>A constant column of a resolved binding.</summary>
public sealed class RBindingConstant
{
    /// <summary>The column as the file writes it.</summary>
    public string Column { get; internal set; } = "";

    /// <summary>The physical name of the column.</summary>
    public string ColumnName { get; internal set; } = "";

    /// <summary>The source column, when the source is a table.</summary>
    public RColumn? SourceColumn { get; internal set; }

    /// <summary>The source column, when the source is a view.</summary>
    public RViewColumn? ViewColumn { get; internal set; }

    /// <summary>The source column, when the source is a query.</summary>
    public RQueryField? QueryField { get; internal set; }

    /// <summary>The column of the write table the value is inserted into, or <see langword="null"/>.</summary>
    public RColumn? WriteColumn { get; internal set; }

    /// <summary>The value: a string, a number or a bool; <see langword="null"/> is SQL NULL.</summary>
    public object? Value { get; internal set; }
}

/// <summary>One entry of a resolved binding's field map.</summary>
public sealed class RBindingField
{
    /// <summary>
    /// The field's name: the attribute's, a value object member's as <c>attributeMember</c>, or a to-one navigation key's as its column
    /// name camel-cased. The select statement aliases the column to it and the statements' parameters are named after it.
    /// </summary>
    public string Name { get; internal set; } = "";

    /// <summary>The attribute as the file writes it.</summary>
    public string AttributeRef { get; internal set; } = "";

    /// <summary>The attribute the field fills (for a member, the value object attribute), or <see langword="null"/>.</summary>
    public RAttribute? Attribute { get; internal set; }

    /// <summary>The value object member the field fills, or <see langword="null"/>.</summary>
    public RAttribute? Member { get; internal set; }

    /// <summary>The relation end the field's key leads to (a to-one end of a relation of the entity), or <see langword="null"/>.</summary>
    public REnd? End { get; internal set; }

    /// <summary>The to-one navigation towards <see cref="End"/>, when the end is navigable, or <see langword="null"/>.</summary>
    public RNavigation? Navigation { get; internal set; }

    /// <summary>The column as the file writes it.</summary>
    public string Column { get; internal set; } = "";

    /// <summary>The physical name of the source column.</summary>
    public string ColumnName { get; internal set; } = "";

    /// <summary>The source column, when the source is a table.</summary>
    public RColumn? SourceColumn { get; internal set; }

    /// <summary>The source column, when the source is a view.</summary>
    public RViewColumn? ViewColumn { get; internal set; }

    /// <summary>The source column, when the source is a query.</summary>
    public RQueryField? QueryField { get; internal set; }

    /// <summary>The column of the write table the field writes, or <see langword="null"/> (the field is not written).</summary>
    public RColumn? WriteColumn { get; internal set; }

    /// <summary>Whether the field maps a key attribute.</summary>
    public bool IsKey { get; internal set; }

    /// <summary>Whether the database fills the column (identity, a key sequence, computed, or marked <c>database</c> or <c>computed</c>).</summary>
    public bool IsGenerated { get; internal set; }

    /// <summary>Whether the insert statement writes the field.</summary>
    public bool InInsert { get; internal set; }

    /// <summary>Whether the update statement sets the field (written, not a key, not read-only or immutable).</summary>
    public bool InUpdate { get; internal set; }

    /// <summary>The built-in type keyword of the column, or <see langword="null"/> when unknown.</summary>
    public string? Type { get; internal set; }

    /// <summary>The native type of the column, or <see langword="null"/> when unknown.</summary>
    public string? NativeType { get; internal set; }

    /// <summary>Whether the column is nullable.</summary>
    public bool Nullable { get; internal set; } = true;
}

/// <summary>A source (or write table) column of a resolved binding and what accounts for it.</summary>
public sealed class RBindingColumn
{
    /// <summary>The column's physical name.</summary>
    public string Name { get; internal set; } = "";

    /// <summary>The column, for a table.</summary>
    public RColumn? Column { get; internal set; }

    /// <summary>
    /// <c>field</c>, <c>constant</c>, <c>ignored</c>, <c>database</c>, <c>computed</c>, <c>identity</c> (identity or a key
    /// sequence), <c>default</c> (a column default), <c>soft-delete</c> or <c>unaccounted</c> (MQ4047).
    /// </summary>
    public string Status { get; internal set; } = "unaccounted";

    /// <summary>The field that maps the column, with <see cref="Status"/> <c>field</c>.</summary>
    public RBindingField? Field { get; internal set; }

    /// <summary>The constant, with <see cref="Status"/> <c>constant</c>.</summary>
    public RBindingConstant? Constant { get; internal set; }
}
