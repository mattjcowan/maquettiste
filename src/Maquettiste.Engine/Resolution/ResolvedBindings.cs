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

    /// <summary>
    /// The attributes stored in a table of their own (retire-projection-design.md section 5), in file order: each with its child table, the foreign
    /// key that ties a row to its owner, its fields and its order column. <c>BindingSql</c> (the template helper <c>binding_sql</c>, given an entry)
    /// renders each one's select by owner, delete by owner and insert of one row. Empty without child tables.
    /// </summary>
    public IReadOnlyList<RBindingChildTable> ChildTables { get; internal set; } = [];

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

    /// <summary>
    /// The base's binding's child tables (retire-projection-design.md section 5) as the derived entity uses them: the rows of an attribute the
    /// base stores go with the derived entity's row too, and a copy of the derived entity follows its own merged <c>onCopy</c>. Empty when the
    /// base's binding has none.
    /// </summary>
    public IReadOnlyList<RBindingBaseChild> ChildTables { get; internal set; } = [];
}

/// <summary>A child table of a base's binding, as a derived entity (table per type) uses it.</summary>
public sealed class RBindingBaseChild
{
    /// <summary>
    /// The base's binding's entry: its table, owner key, fields and statements. Under a derived binding that writes no table, a copy whose
    /// <see cref="RBindingChildTable.Writes"/> is false, so it renders only its select.
    /// </summary>
    public RBindingChildTable ChildTable { get; internal set; } = null!;

    /// <summary>
    /// What a copy of the derived entity does with the rows: the attribute's <c>onCopy</c> merged through the derived entity's bases (its own
    /// entry wins over the base's), else <c>copy</c>.
    /// </summary>
    public string OnCopy { get; internal set; } = "copy";
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

    /// <summary>The value object member the field fills (the last one of <see cref="Members"/>), or <see langword="null"/>.</summary>
    public RAttribute? Member { get; internal set; }

    /// <summary>
    /// The value object members the field's path walks below <see cref="Attribute"/>, outermost first, ending at <see cref="Member"/>: one for
    /// <c>attributeId.memberId</c>, more for a member of a nested value object (<c>attributeId.memberId.memberId</c>). Empty for an attribute
    /// and a relation end.
    /// </summary>
    public IReadOnlyList<RAttribute> Members { get; internal set; } = [];

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

/// <summary>
/// An attribute a binding stores in a table of its own (retire-projection-design.md section 5): a list, one row per item, or a single value object,
/// at most one row. Each row holds the owner's key (<see cref="Owner"/>), the value or the value object's members (<see cref="Fields"/>), and for a
/// list optionally the item's position (<see cref="OrderColumn"/>). Writes replace the attribute's rows: delete the owner's rows, then insert the
/// items in order, after the owner's insert; a delete removes them before the owner's (unless the foreign key cascades), and a soft delete of the
/// owner leaves them. <c>BindingSql</c> (the template helper <c>binding_sql</c>, given the entry) renders its <c>select</c>, <c>delete</c> and
/// <c>insert</c>.
/// </summary>
public sealed class RBindingChildTable
{
    /// <summary>The entry's id.</summary>
    public string Id { get; internal set; } = "";

    /// <summary>The binding the entry belongs to.</summary>
    public REntityBinding Binding { get; internal set; } = null!;

    /// <summary>The attribute as the file writes it.</summary>
    public string AttributeRef { get; internal set; } = "";

    /// <summary>The attribute stored in the table, as the entity holds it; <see langword="null"/> when it names none (MQ4137).</summary>
    public RAttribute? Attribute { get; internal set; }

    /// <summary>Whether the attribute is a list (one row per item); false for a single value object (at most one row).</summary>
    public bool IsList { get; internal set; }

    /// <summary>The child table, or <see langword="null"/> when it does not resolve (MQ4138).</summary>
    public RTable? Table { get; internal set; }

    /// <summary>The foreign key of the child table that references the owning table, or <see langword="null"/> when it does not fit (MQ4138).</summary>
    public RForeignKey? OwnerForeignKey { get; internal set; }

    /// <summary>The owner foreign key's columns, each with the owning table's column it references and the binding's field for that column.</summary>
    public IReadOnlyList<RBindingOwnerColumn> Owner { get; internal set; } = [];

    /// <summary>
    /// The field map, in file order: for a list of a scalar, an enum or a reference type, one field on the attribute itself (named <c>value</c>);
    /// else one per value object member path, named after the path below the attribute (<c>line1</c>, <c>geoLatitude</c>). The insert writes the
    /// fields not generated; nothing updates them.
    /// </summary>
    public IReadOnlyList<RBindingField> Fields { get; internal set; } = [];

    /// <summary>The column holding a list item's position, from 0; reads are ordered by it. <see langword="null"/> without one.</summary>
    public RColumn? OrderColumn { get; internal set; }

    /// <summary>The parameter the insert writes the position with (<c>position</c>), with <see cref="OrderColumn"/>; else <see langword="null"/>.</summary>
    public string? OrderParameter { get; internal set; }

    /// <summary>The constants, in file order: a filter on every read and a value on every insert.</summary>
    public IReadOnlyList<RBindingConstant> Constants { get; internal set; } = [];

    /// <summary>Every column of the child table, in its order, with what accounts for it (<c>owner</c> and <c>order</c> besides a binding's statuses).</summary>
    public IReadOnlyList<RBindingColumn> Columns { get; internal set; } = [];

    /// <summary>
    /// Whether the entry writes its rows: the binding writes, and the entry resolves. A base's entry as a derived binding that writes no table
    /// uses it (<see cref="RBindingBaseChild.ChildTable"/>) writes nothing.
    /// </summary>
    public bool Writes { get; internal set; }

    /// <summary>
    /// What a copy of the owner does with the rows (parts-and-copy-design.md): <c>copy</c> copies them with the copied owner; <c>clear</c> and
    /// <c>reset</c>, the attribute's <c>onCopy</c> merged through the bases, leave none.
    /// </summary>
    public string OnCopy
    {
        get => _original?.OnCopy ?? _onCopy;
        internal set => _onCopy = value;
    }

    private string _onCopy = "copy";

    /// <summary>The entry a read-only copy was made from (its <see cref="OnCopy"/> is set after the copy is made); else <see langword="null"/>.</summary>
    private RBindingChildTable? _original;

    /// <summary>The entry as a derived binding that writes no table uses it: the same, writing nothing.</summary>
    internal RBindingChildTable ReadOnly() => new()
    {
        Id = Id, Binding = Binding, AttributeRef = AttributeRef, Attribute = Attribute, IsList = IsList, Table = Table, OwnerForeignKey = OwnerForeignKey,
        Owner = Owner, Fields = Fields, OrderColumn = OrderColumn, OrderParameter = OrderParameter, Constants = Constants, Columns = Columns, Writes = false,
        _original = this,
    };
}

/// <summary>One column of a child table's owner foreign key, paired with the owning table's column it references.</summary>
public sealed class RBindingOwnerColumn
{
    /// <summary>The child table's column.</summary>
    public RColumn Column { get; internal set; } = null!;

    /// <summary>The owning table's column it references (a key or unique key column).</summary>
    public RColumn ReferencedColumn { get; internal set; } = null!;

    /// <summary>The binding's field (or key field) on the referenced column, whose value fills the column; <see langword="null"/> when none maps it (MQ4139).</summary>
    public RBindingField? Field { get; internal set; }

    /// <summary>
    /// The statements' parameter for the owner's value: <c>owner</c> and the field's name in Pascal case (<c>ownerId</c>), else <c>owner</c> and the
    /// referenced column's name in Pascal case.
    /// </summary>
    public string Parameter { get; internal set; } = "";
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
    /// <c>computed</c>, <c>identity</c> (identity or a key sequence), <c>default</c> (a column default), <c>soft-delete</c>, <c>relation</c> (a relation attribute the relation's mapping stores on its foreign key),
    /// in a child table <c>owner</c> (a column of the owner foreign key) and <c>order</c> (the order column), or <c>unaccounted</c> (MQ4047).
    /// </summary>
    public string Status { get; internal set; } = "unaccounted";

    /// <summary>The field that maps the column, with <see cref="Status"/> <c>field</c>.</summary>
    public RBindingField? Field { get; internal set; }

    /// <summary>The constant, with <see cref="Status"/> <c>constant</c>.</summary>
    public RBindingConstant? Constant { get; internal set; }
}
