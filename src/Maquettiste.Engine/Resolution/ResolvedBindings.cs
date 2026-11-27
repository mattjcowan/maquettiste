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

    /// <summary>
    /// The column a soft delete sets, with <see cref="Delete"/> <c>soft</c>; reads leave out the rows it marks. A binding with
    /// <see cref="Bases"/> under a root that deletes softly has the root's column whatever its own delete, since its reads filter on it.
    /// </summary>
    public RColumn? SoftDeleteColumn { get; internal set; }

    /// <summary>The value a soft delete sets (a plain value; <see langword="null"/> is SQL NULL).</summary>
    public object? SoftDeleteValue { get; internal set; }

    /// <summary>
    /// The entity's type code in the database as the binding sets it (<c>discriminatorValue</c>, several-types-design.md decision 42): a
    /// string or an integer; <see langword="null"/> when the binding sets none.
    /// </summary>
    public object? DiscriminatorValue { get; internal set; }

    /// <summary>
    /// The type column the entity shares with its base or the entities derived from it in one table: the column the binding's own
    /// <c>discriminatorColumn</c> names, else the one it inherits from its nearest base whose binding to the database names one, with the
    /// code its insert writes and the codes its reads, updates and deletes filter on; <see langword="null"/> when neither names one.
    /// </summary>
    public RBindingDiscriminator? DiscriminatorColumn { get; internal set; }

    /// <summary>
    /// The fields that map the entity's key attributes, in key order. With <see cref="BaseForeignKey"/>, the foreign key's columns instead,
    /// one field per key field of the base's binding, with its name and attribute: they are not in <see cref="Fields"/>, since the binding
    /// maps no field for an inherited key attribute, and the insert writes them with the base's key.
    /// </summary>
    public IReadOnlyList<RBindingField> Key { get; internal set; } = [];

    /// <summary>
    /// Table per type (retire-projection-design.md section 4): the foreign key of the binding's write table (else its source) that its
    /// <c>baseForeignKey</c> names, joining it to the table of its nearest bound base; <see langword="null"/> without one, or when it does
    /// not resolve (MQ4128 to MQ4130, MQ4133).
    /// </summary>
    public RForeignKey? BaseForeignKey { get; internal set; }

    /// <summary>
    /// The tables above the binding's own that its entity's rows also live in, nearest first, each with the base's binding that reads and
    /// writes it: the base its <see cref="BaseForeignKey"/> reaches, then that binding's own bases, up the chain. A binding without a base
    /// foreign key in the table of a base that has bases shares them (a subtype in a table per type's table). Reads join them; an insert
    /// writes them first, root first; a delete removes them last. Empty for every other binding.
    /// </summary>
    public IReadOnlyList<RBindingBase> Bases { get; internal set; } = [];

    /// <summary>The key fields whose value the database generates on insert (identity, a sequence, a default the binding marks).</summary>
    public IReadOnlyList<RBindingField> Generated { get; internal set; } = [];

    /// <summary>The binding entry as the entity's file writes it.</summary>
    internal Model.EntityBinding? Definition { get; set; }

    /// <summary>The index of the binding in the entity's <c>bindings</c> (for diagnostics).</summary>
    internal int Index { get; set; }
}

/// <summary>
/// One table above a binding's own (retire-projection-design.md section 4): the base's binding that reads and writes it, the foreign key that
/// joins the table below to it, and its type column as the derived entity's statements use it. <c>BindingSql</c> (the template helper
/// <c>binding_sql</c>, given the level) renders the table's insert, update and delete for the derived entity: the base's fields, constants
/// and key, the derived entity's type code, the base's soft delete.
/// </summary>
public sealed class RBindingBase
{
    /// <summary>The binding whose entity's rows the table holds a part of: the derived entity's.</summary>
    public REntityBinding Derived { get; internal set; } = null!;

    /// <summary>The base entity's binding to the same database, which reads and writes the table: its fields, constants and key.</summary>
    public REntityBinding Binding { get; internal set; } = null!;

    /// <summary>
    /// The foreign key that joins the table below (the derived binding's own for the nearest base, else the level below's) to this one: its
    /// columns are the table below's, its referenced columns this table's key columns.
    /// </summary>
    public RForeignKey ForeignKey { get; internal set; } = null!;

    /// <summary>
    /// The table's type column, when the base's binding has one: the derived entity's type code is the value an insert writes, and the codes
    /// of the derived entity and the entities derived from it that keep rows in the table are those its reads, updates and deletes keep;
    /// <see langword="null"/> without a type column.
    /// </summary>
    public RBindingDiscriminator? DiscriminatorColumn { get; internal set; }
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

/// <summary>
/// The type column of a resolved binding: a column a base entity and the entities derived from it share in one table, holding each
/// one's type code. It is resolved against the binding's own source and write table, like a constant.
/// </summary>
public sealed class RBindingDiscriminator
{
    /// <summary>The column as the file of <see cref="Base"/> writes it.</summary>
    public string Column { get; internal set; } = "";

    /// <summary>The physical name of the column in the source.</summary>
    public string ColumnName { get; internal set; } = "";

    /// <summary>The source column, when the source is a table.</summary>
    public RColumn? SourceColumn { get; internal set; }

    /// <summary>The source column, when the source is a view.</summary>
    public RViewColumn? ViewColumn { get; internal set; }

    /// <summary>The source column, when the source is a query.</summary>
    public RQueryField? QueryField { get; internal set; }

    /// <summary>The column of the write table the insert writes the code into, or <see langword="null"/>.</summary>
    public RColumn? WriteColumn { get; internal set; }

    /// <summary>The entity whose binding names the column: the binding's own entity, or its nearest base that names one.</summary>
    public REntity Base { get; internal set; } = null!;

    /// <summary>The entity's type code, a string or an integer: the value the insert writes.</summary>
    public object Value { get; internal set; } = "";

    /// <summary>
    /// The codes reads, updates and deletes filter on: the entity's own, then those of the entities derived from it whose bindings to the
    /// database share the column and keep their rows in the same table, so reading an entity returns its subtypes' rows too. For the
    /// entity whose binding names the column, the codes of its whole hierarchy in that table: it never reaches another entity's rows.
    /// </summary>
    public IReadOnlyList<object> Codes { get; internal set; } = [];
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

    /// <summary>
    /// Whether the insert statement writes the field (never when a constant or the type column writes its column: that value goes in).
    /// </summary>
    public bool InInsert { get; internal set; }

    /// <summary>
    /// Whether the update statement sets the field (written, not a key, not read-only or immutable, and neither a constant nor the type
    /// column writes its column).
    /// </summary>
    public bool InUpdate { get; internal set; }

    /// <summary>The built-in type keyword of the column, or <see langword="null"/> when unknown.</summary>
    public string? Type { get; internal set; }

    /// <summary>The native type of the column, or <see langword="null"/> when unknown.</summary>
    public string? NativeType { get; internal set; }

    /// <summary>Whether the column is nullable.</summary>
    public bool Nullable { get; internal set; } = true;

    /// <summary>
    /// On a relation end with several types (several-types-design.md decision 46), what the field's column holds: <c>id</c> (the field
    /// names <c>&lt;endId&gt;</c>), <c>type</c> (<c>&lt;endId&gt;.type</c>) or <c>key</c> (<c>&lt;endId&gt;.&lt;entityId&gt;</c>);
    /// <see langword="null"/> on every other field, a one-type end's key included.
    /// </summary>
    public string? Holds { get; internal set; }

    /// <summary>The entity of an <c>&lt;endId&gt;.&lt;entityId&gt;</c> field (<see cref="Holds"/> is <c>key</c>); <see langword="null"/> otherwise.</summary>
    public REntity? KeyOf { get; internal set; }
}

/// <summary>A source (or write table) column of a resolved binding and what accounts for it.</summary>
public sealed class RBindingColumn
{
    /// <summary>The column's physical name.</summary>
    public string Name { get; internal set; } = "";

    /// <summary>The column, for a table.</summary>
    public RColumn? Column { get; internal set; }

    /// <summary>
    /// <c>field</c>, <c>constant</c>, <c>discriminator</c> (the binding's type column), <c>ignored</c>, <c>database</c>,
    /// <c>computed</c>, <c>identity</c> (identity or a key sequence), <c>default</c> (a column default), <c>soft-delete</c>, <c>relation</c> (a relation attribute the relation's mapping stores on its foreign key) or
    /// <c>unaccounted</c> (MQ4047).
    /// </summary>
    public string Status { get; internal set; } = "unaccounted";

    /// <summary>The field that maps the column, with <see cref="Status"/> <c>field</c>.</summary>
    public RBindingField? Field { get; internal set; }

    /// <summary>The constant, with <see cref="Status"/> <c>constant</c>.</summary>
    public RBindingConstant? Constant { get; internal set; }
}
