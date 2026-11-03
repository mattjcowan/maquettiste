using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;

namespace Maquettiste.Engine.Diagnostics;

/// <summary>A built-in rule: its id, default severity and short description.</summary>
/// <param name="Id">The rule id, for example <c>MQ2001</c>.</param>
/// <param name="DefaultSeverity">The severity unless <c>validation.rules</c> overrides it.</param>
/// <param name="Description">A short description, used for SARIF <c>rules[]</c>.</param>
/// <param name="QuickFix">The batch operation that fixes a finding of the rule, applied to the finding's element (phase-3-design.md
/// section 3, "Quick fixes"), or <see langword="null"/>.</param>
public sealed record RuleInfo(string Id, DiagnosticSeverity DefaultSeverity, string Description, string? QuickFix = null)
{
    /// <summary>Whether <c>validation.rules</c> may turn the rule off (every rule except MQ1xxx).</summary>
    public bool CanBeDisabled => !Id.StartsWith("MQ1", StringComparison.Ordinal);
}

/// <summary>A built-in rule as the editor's Validation settings and the MCP tool list it: <see cref="RuleInfo"/> with its family.</summary>
/// <param name="Id">The rule id.</param>
/// <param name="DefaultSeverity">The severity unless <c>validation.rules</c> overrides it.</param>
/// <param name="Description">The short description.</param>
/// <param name="Family">The rule's hundreds group, for example <c>MQ72xx</c>.</param>
/// <param name="FamilyLabel">What the family's rules cover, for example <c>Localization</c>.</param>
/// <param name="CanBeOff">Whether <c>validation.rules</c> may set the rule to <c>off</c> (false for MQ1xxx).</param>
/// <param name="QuickFix">The batch operation that fixes a finding (for example <c>sync-enum</c>); left out of the JSON when none.</param>
public sealed record RuleCatalogEntry(string Id, DiagnosticSeverity DefaultSeverity, string Description, string Family, string FamilyLabel, bool CanBeOff,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? QuickFix = null);

/// <summary>The rule catalog in a few lines (<see cref="RuleCatalog.Summarize"/>): what <c>list_validation_rules</c> answers with no argument.</summary>
/// <param name="Total">The number of built-in rules.</param>
/// <param name="Families">Each family with its rule count and id range, in id order.</param>
public sealed record RuleCatalogSummary(int Total, IReadOnlyList<RuleFamilySummary> Families);

/// <summary>One family of the rule catalog.</summary>
/// <param name="Family">The hundreds group, for example <c>MQ72xx</c>.</param>
/// <param name="FamilyLabel">What the family's rules cover.</param>
/// <param name="Rules">The number of rules in the family.</param>
/// <param name="First">The family's lowest rule id.</param>
/// <param name="Last">The family's highest rule id.</param>
public sealed record RuleFamilySummary(string Family, string FamilyLabel, int Rules, string First, string Last);

/// <summary>One page of full catalog entries (<see cref="RuleCatalog.Page"/>).</summary>
/// <param name="Items">The entries, in id order.</param>
/// <param name="Next">The cursor of the next page, or <see langword="null"/> on the last one.</param>
public sealed record RuleCatalogPage(IReadOnlyList<RuleCatalogEntry> Items, string? Next);

/// <summary>The built-in rule catalog (engine-design.md section 6). JavaScript rules use <c>x/&lt;id&gt;</c> and are not listed here.</summary>
public static class RuleCatalog
{
    private const DiagnosticSeverity E = DiagnosticSeverity.Error;
    private const DiagnosticSeverity W = DiagnosticSeverity.Warning;
    private const DiagnosticSeverity I = DiagnosticSeverity.Info;

    private static readonly ImmutableArray<RuleInfo> Rules =
    [
        new("MQ1001", E, "Invalid JSON."),
        new("MQ1002", E, "Schema violation."),
        new("MQ1003", W, "File is not in canonical form: rewrite it from the Problems panel or run maquettiste format."),
        new("MQ1004", E, "Duplicate id."),
        new("MQ1005", W, "File is in the wrong folder or its name does not match the element."),
        new("MQ1006", E, "Invalid ULID."),
        new("MQ1007", E, "Unsupported model format version."),
        new("MQ1008", W, "The .schema folder is out of date."),
        new("MQ1009", E, "A second tag vocabulary or category tree in the same scope (global or one domain); the ordinally first file is used."),
        new("MQ1010", I, "An outputs.allow entry sets commit, which is ignored since 0.5.5; remove it (maquettiste format drops it)."),
        new("MQ1011", E, "A snapshot archive cannot be imported: not a zip, a path outside the snapshot layout, a size limit, or a missing or unreadable snapshot.json."),
        new("MQ1012", E, "A name or placeholder in a batch finds nothing. No element or sub-element of that kind has the name (an update's or delete's target on the model as loaded; any other name on the model as the earlier operations leave it, with the operation's own element as it will be written), or no operation declares the placeholder. A form through an element renamed earlier in the batch keeps the old qualifier: use the id or a second batch."),
        new("MQ1013", E, "A name in a batch finds more than one element or sub-element. Use a longer form (package, database, schema, owner or seed), or the id when every form is shared; the message lists each candidate's longest form and id."),
        new("MQ1014", E, "A write misuses an identity: a placeholder declared twice; as on a delete, a schema, materialize, process or tag operation, or on an object that is not a sub-element; two expectedHashes keys for one element; or an update that drops a sub-element and adds one without an id under the same parent and property with the same name (send the existing id to keep it)."),
        new("MQ1015", E, "A name in a batch is malformed or misplaced: not @kind:path or $name, an unknown kind or one without names, an empty or unclosed quoted segment, a name as a create's id (declare a placeholder with as), or a placeholder where the element must exist before the batch (an update's or delete's id, an expectedHashes key)."),
        new("MQ1016", E, "A JSON Patch cannot be applied: an unknown op, a path or from that does not resolve, an index out of range, a failed test, or a change to id or kind; nothing is written."),
        new("MQ1017", E, "A custom kind file is invalid: its type differs from its file name, its properties do not compile as a JSON Schema, or its required names a property it does not declare."),
        new("MQ1018", E, "An x-member keyword is misplaced: not true, not on an array whose items is an object schema, on an item schema that declares id, or under an applicator the walk does not follow."),
        new("MQ1019", E, "A custom element's type names no custom kind file (extensions/kinds/<type>.json); its content is not checked."),

        new("MQ2001", E, "Dangling reference."),
        new("MQ2002", E, "Reference to an element of the wrong kind."),
        new("MQ2003", E, "Unknown stereotype."),
        new("MQ2004", E, "Stereotype not applicable to this kind."),
        new("MQ2005", E, "Unknown category."),
        new("MQ2006", I, "Undeclared tag (an error when the tag vocabulary is strict)."),
        new("MQ2007", E, "Attribute validation names an unknown rule."),
        new("MQ2008", E, "A tag or category is declared only in a domain vocabulary outside the element's domain chain."),
        new("MQ2009", E, "An x-ref keyword is malformed: an unknown kind or key, a custom:<type> whose type is not a key, of or sameDatabase where they do not fit, an of naming no element (or, under kind member, no custom element), not on a string, under an applicator the link walk does not follow, or beside a patternProperties pattern that needs backtracking (a lookaround, backreference, atomic group or conditional)."),
        new("MQ2010", E, "A value an x-ref types is a name or other text, not an id."),
        new("MQ2011", E, "An x-ref value is out of scope: a member of another element, a member of a custom element other than the one of names, a row of another list, another database, a holder with no single database, or a stereotype default under of \"$element\" or sameDatabase."),
        new("MQ2013", E, "An element carries two stereotypes of one group, own, implied or from a schema or package default."),
        new("MQ2014", E, "An element carries a stereotype that requires another it does not carry."),
        new("MQ2015", E, "An element carries two stereotypes that conflict."),
        new("MQ2016", E, "A stereotype's requires, conflicts or implies names an unknown key or itself, conflicts with one it requires or implies, or implies one of its own group."),
        new("MQ2017", E, "A stereotype implies one that implies others; implication is one level."),
        new("MQ2018", E, "A property value fails the parameters of a stereotype the element carries (required included, on the merged values), or a default fails its own property schema (required not applied)."),
        new("MQ2019", E, "A stereotype's parameters is not a valid schema, or its required names an undeclared parameter."),
        new("MQ2020", E, "A reference from one package to another that its package does not depend on (dependsOn of the package or a parent)."),
        new("MQ2021", E, "dependsOn names the package itself or forms a cycle."),
        new("MQ2022", W, "A reference to a retired element or sub-element from one that is not retired (membership, owning references and diagrams excepted)."),
        new("MQ2024", E, "A stereotype's members do not fit: members on a stereotype whose appliesTo leaves out table, a contributed column with attribute or sequence, or a contributed key, index or check with a name."),
        new("MQ2025", E, "A stereotype's key, index or check names a column the stereotype does not declare."),
        new("MQ2026", E, "A contributed foreign key's target is invalid: both or neither of referencesTable and referencesParameter; a referencesParameter that is not a required string parameter with x-ref kind table and sameDatabase; or referencesColumns beside referencesParameter."),
        new("MQ2027", E, "A redefines entry is invalid: it names a member the element does not inherit, names one twice, states no facet, or states a facet its member's kind does not take."),
        new("MQ2028", E, "An excludes entry names a member the element does not inherit or also redefines, or excludes a column that an inherited key, index or check the element keeps uses."),
        new("MQ2029", E, "excludeStereotypes names a stereotype the element lists in stereotypes, or one a listed stereotype implies; only defaults can be excluded."),
        new("MQ2030", W, "excludeStereotypes names a stereotype no schema or package default gives the element, so it excludes nothing."),
        new("MQ2031", E, "A defaultStereotypes list names two stereotypes of one group, or two that conflict."),

        new("MQ3001", E, "Duplicate name in scope."),
        new("MQ3002", E, "Inheritance cycle."),
        new("MQ3003", E, "Package cycle."),
        new("MQ3004", E, "Category cycle."),
        new("MQ3005", E, "Entity without a key."),
        new("MQ3006", E, "Key names a missing attribute."),
        new("MQ3007", E, "Duplicate attribute name, virtual attributes included."),
        new("MQ3008", E, "Relation end count does not match its kind."),
        new("MQ3009", E, "Navigation collides with a member of the entity."),
        new("MQ3010", E, "Invalid cardinality."),
        new("MQ3011", E, "Set-null on a required end."),
        new("MQ3012", E, "Enum member name or code duplicated, or a flags value that is not a power of two."),
        new("MQ3013", E, "Facet invalid for the type."),
        new("MQ3014", E, "Custom scalar base is not a built-in type."),
        new("MQ3015", E, "Value object containment cycle."),
        new("MQ3016", E, "Composition child with two owners."),
        new("MQ3017", W, "Default value looks like a credential."),
        new("MQ3018", E, "Invalid name for the element kind."),
        new("MQ3019", E, "Literal default does not match its type: an attribute's (use defaultExpression for now, today, new-uuid or new-ulid) or a column's, checked against its resolved length; a json column takes a string of JSON text, and a column with a nativeType is checked for the kind of value only (use defaultSql otherwise)."),
        new("MQ3020", E, "A stereotype's key cannot change."),
        new("MQ3021", E, "A domain vocabulary redeclares a tag key or category name of the global vocabulary or an enclosing domain's."),
        new("MQ3022", E, "A diagram with membership 'package' and no package: it has nothing to follow; set the diagram's package, or set membership to 'explicit' so its members list is the diagram."),
        new("MQ3023", W, "An operation sets level collection without a subject; the level is ignored."),
        new("MQ3024", E, "An operation member is not exactly one of a typed value (type), a record key (keyOf) or a file (file); mediaTypes sits on a member that is not a file; a record key or a file sets default, precision, scale or validation, or a record key sets length; or a member of a returns sets default."),
        new("MQ3025", E, "An outcome's returns is not exactly one of members, entity or file; mediaTypes sits beside no file; list sits beside file; paging is set without list; or paging and maxPageSize are not set together."),
        new("MQ3026", E, "Two errors of an operation, own and inherited counted, share a name or a code (the code, else the name), or two of its stereotypes give it an error of one id."),
        new("MQ3027", E, "An error's input, or a redefinition's input, is not an input member of the operation that holds it; a stereotype's error has none."),
        new("MQ3028", E, "defaultOutcome names an outcome of another operation."),
        new("MQ3029", E, "A stereotype declares errors but does not apply to operations."),
        new("MQ3030", W, "A read operation fires a process event; an event changes state."),
        new("MQ3031", E, "requires names the operation itself, one of its own sub-elements, a diagram or a migration fact."),
        new("MQ3033", E, "A custom element's content fails its custom kind's schema: a required key missing, an undeclared key, a value of the wrong shape, or a member without an id."),
        new("MQ3101", E, "An expression does not parse, or a node is malformed: an unknown operator or function, a wrong number of operands."),
        new("MQ3102", E, "An expression's path names what its site cannot read: an unknown id, a member of another entity or table, a navigation at a check or value site, a to-many navigation, a collection, a member path outside a rule condition, or a payload attribute some using event lacks."),
        new("MQ3103", E, "An expression's types do not fit: operands of different types, ordering or arithmetic on a type without it, a date with a timestamp, + on a date, date_part of hour or minute on a date, a site result that is not bool, a derived value of another type, a type the core does not have (float among them), or an enum member or code against a value of no enum or reference type."),
        new("MQ3104", E, "now() at a check site, a value site or a rule condition, directly or through a constraint; only guards read the clock."),
        new("MQ3105", E, "A regular expression outside the portable subset."),
        new("MQ3106", E, "An expression argument that must be a literal is not: a pattern, a date_part part, round digits, an in list item; or an in list holds null."),
        new("MQ3107", E, "An expression site holds both its raw form and a tree, or neither; a process guard is reported only for both, since a guard with neither is a stub."),
        new("MQ3108", W, "A file holds an expression as text; renames will not follow it. Convert it to a tree.", "text-to-trees"),
        new("MQ3109", E, "A satisfies names no constraint, misses or adds a parameter, binds one to another type, names a constraint whose condition is not a boolean that types, or constraints cite each other in a cycle."),
        new("MQ3110", E, "A binding lists a check that is not its entity's, or one that reads an attribute the binding does not map to its write table or maps to a fixed-length column, or the binding has no write table."),
        new("MQ3111", E, "An operation guard's error is not an error of the operation, or the guard reads the subject of an operation without a record subject."),
        new("MQ3113", E, "An expression has more than 512 nodes or nests deeper than 64."),
        new("MQ3114", W, "A column check does not read its column, or an entity check does not read its attribute."),
        new("MQ3115", W, "An entity check is listed by no binding with a write table, though one of them maps every attribute it reads, so no database enforces it."),

        new("MQ4001", E, "Identifier longer than the dialect's limit."),
        new("MQ4002", E, "Duplicate table name in a schema."),
        new("MQ4003", E, "Duplicate column name."),
        new("MQ4004", E, "Two mappings for one element in one database."),
        new("MQ4005", E, "Foreign key column type does not match the referenced column."),
        new("MQ4006", W, "Native type unknown to the dialect map."),
        new("MQ4007", E, "Overlay references a missing column key."),
        new("MQ4008", E, "Constraint or index names a missing column."),
        new("MQ4009", E, "Mapping option invalid for the element."),
        new("MQ4010", E, "View body missing for the database's dialect."),
        new("MQ4011", E, "Relation between bound tables names no foreign key or junction end binding."),
        new("MQ4012", I, "An entity lands in no database: no database takes its domain by convention and no mapping names it."),
        new("MQ4013", W, "A database lists convention packages that its byConvention setting does not use."),
        new("MQ4014", E, "A convention package entry or an entity mapping names a schema that its database does not declare."),
        new("MQ4015", E, "A schema operation was refused: the schema is unknown or its name is taken, it still holds tables, views, sequences, convention entries or mappings and no target was given, or it is the default and no other schema becomes the default."),
        new("MQ4016", I, "A native type written with quotes or a schema names a type the database defines, which the dialect map cannot check; reported once per type and database with the number of columns that use it."),
        new("MQ4017", W, "A routine or SQL object has no body for its database's dialect (and no \"*\" body), or a database type has neither its structured form nor a definition for the dialect: nothing is created there."),
        new("MQ4018", E, "A routine or query parameter, a routine result, or a composite type's field names a type that is neither a built-in type nor a database type of the same database."),
        new("MQ4019", E, "A column's native type or a dependsOn entry names a database type or object of another database."),
        new("MQ4020", E, "Routines, views, database types and SQL objects depend on each other in a cycle (dependsOn, or a composite type's fields), so no creation order exists."),
        new("MQ4021", E, "A query's source (from or a join) is not a table or view of the query's database, or a call names an id that is not a routine of that database: unknown, of another database, or of another kind."),
        new("MQ4022", E, "A query declares an alias twice, or a column reference names an alias that no source of the query declares."),
        new("MQ4023", E, "A query's column reference names no column of its source (by name, attribute id or column key), or a column name without an alias is ambiguous or unknown."),
        new("MQ4024", E, "A query expression or its paging names a parameter the query does not declare."),
        new("MQ4025", E, "A select field names an attribute that the query's entity (or the collection's entity) does not have, or names an attribute where no entity is named."),
        new("MQ4026", W, "A required attribute of the query's entity is not selected, so the rows leave it at its default."),
        new("MQ4027", E, "A query collection names neither a collection attribute nor a to-many navigation of the query's entity, or its nested query selects nothing."),
        new("MQ4028", E, "A nested query names an outer alias that no enclosing query declares, or a collection's query names its parent's aliases outside an equality at the top of its where."),
        new("MQ4029", E, "A query's sql expression has no text for the database's dialect and no \"*\" text, so the query cannot be rendered there."),
        new("MQ4030", E, "A query's paging names a parameter whose type is not an integer type."),
        new("MQ4031", E, "A query comparison's right side has the wrong shape: between takes two values, isNull and isNotNull none, in and notIn a list or one list parameter, the others one value."),
        new("MQ4032", E, "A collection's correlation key is neither selected nor grouped by its parent, which is distinct: the hidden key column would change which rows DISTINCT removes."),
        new("MQ4033", W, "A select field's value cannot convert to the type of the entity attribute it fills."),
        new("MQ4034", E, "A query calls a function whose name is neither an identifier (optionally schema-qualified, name or schema.name) nor a routine id."),
        new("MQ4035", E, "A join's condition does not fit its kind: a join other than cross needs on, a cross join takes none."),
        new("MQ4036", E, "A query uses a full join on a MySQL database, which has none."),
        new("MQ4037", W, "A query uses a right or full join on a SQLite database, which needs SQLite 3.39 or later."),
        new("MQ4038", E, "A distinct query orders by an expression that is not in its select list, or by nulls first or last where the dialect emulates them (SQL Server, MySQL)."),
        new("MQ4039", E, "A list parameter (collection: true) is used other than as the whole right side of in or notIn."),
        new("MQ4040", E, "Names of a query collide in generated code or are reserved: two collections with one name, fields, collections or parameters whose names are equal ignoring case or once Pascal-cased, a name starting with mq_, or two queries of a database whose class names are equal once Pascal-cased."),
        new("MQ4041", E, "A query's result entity, or a collection's element entity, is abstract."),
        new("MQ4042", E, "An operation has fewer than two operands (only - takes one, as a negation)."),
        new("MQ4043", E, "A literal or a parameter's default does not fit: a number too large to hold, a typed literal whose text is not a decimal number, or a default whose value does not match the parameter's type."),
        new("MQ4044", E, "A binding's source is not a table, view or query of its database, or its write table is not a table of its database."),
        new("MQ4045", E, "A binding names what it cannot resolve: a field attribute that is neither an attribute of the entity, a value object member nor a to-one relation end; a column its source does not have; a listed or soft-delete column of no table it uses; or an attribute or column mapped twice."),
        new("MQ4046", E, "A binding that writes maps no field for one of the entity's key attributes, so updates and deletes cannot find their row."),
        new("MQ4047", W, "A column of a binding's source or write table is accounted for by nothing: no field maps it, no constant sets it, columns does not list it, and it is not an identity, computed or defaulted column."),
        new("MQ4048", E, "A binding's constant names a column its source does not have, so it cannot filter what the binding reads."),
        new("MQ4049", E, "A binding that writes has a constant whose column is not in the table it writes, so an insert cannot set it."),
        new("MQ4050", E, "An entity has two bindings to one database; an entity has one binding per database."),
        new("MQ4051", W, "A binding's constant (or soft-delete value) cannot fit its column: the wrong type, too long, out of range, or NULL in a column that is not nullable."),
        new("MQ4052", E, "A binding writes a table other than its source, and that table has no column for one of the key fields."),
        new("MQ4053", E, "A binding deletes by key from a table without a primary key, or deletes (by key or softly) without a table to write."),
        new("MQ4054", I, "An entity mapping element maps an entity to a database where the entity has a binding: the binding wins and the mapping is ignored."),
        new("MQ4055", E, "A materialize operation is refused: an entity already bound to the database, a table an entity is already bound to, an entity in an inheritance hierarchy, a name already taken, or an id that is not what the operation takes."),
        new("MQ4056", W, "A table, view or sequence asks for a DDL feature its database's dialect does not have, so the DDL leaves it out: a deferrable foreign key (SQL Server, MySQL), ON UPDATE or ON DELETE restrict or set-default (Oracle), set-default (MySQL), index include columns (MySQL, SQLite, Oracle), a partial index (MySQL, Oracle), an index method other than the default outside PostgreSQL (clustered: SQL Server and PostgreSQL), an index expression (SQL Server: the index is left out), a key prefix length outside MySQL, a MySQL index on a text or blob column without one (the index is left out), nullsNotDistinct outside PostgreSQL, a stored computed column (Oracle), identity options (a seed on SQLite, an increment or always on SQLite and MySQL), a clustered primary key outside SQL Server, a routine's volatility or settings on SQL Server, a temporal key (withoutOverlaps, a foreign key's period) an exclusion constraint or partitioning outside PostgreSQL, an index operator class outside PostgreSQL, index storage parameters on MySQL, SQLite and Oracle, a foreign key's onDeleteColumns outside PostgreSQL (the action sets every column of the key), a materialized view outside PostgreSQL and Oracle, WITH CHECK OPTION on SQLite or on a materialized view, or a sequence on SQLite or MySQL; also migration facts outside PostgreSQL (a fact's migration keeps TODO comments where its steps would go, a rekey writes no DDL on any dialect, and no ledger rows are written), index column nulls outside PostgreSQL, a range or multirange column outside PostgreSQL (stored as the dialect's text type), and a run-once SQL object outside PostgreSQL (the scripts leave it out: its guard needs the migration ledger); also a seed cell that names a row whose referenced value the database fills, outside PostgreSQL (written as NULL with a TODO comment); also a check written in the expression language on a database whose dialect is not PostgreSQL (the DDL leaves it out)."),
        new("MQ4057", E, "A column facet does not fit its type: unicode on a column that is not string or text, fixedLength on a column that is not string or binary, a subtype on a column that is not range or multirange, or a range or multirange column without one."),
        new("MQ4058", W, "A stored attribute of a bound entity is mapped by no field of its binding, so the entity reads it from nowhere: an own, inherited or stereotype attribute, or a member of a value object attribute; a collection, a derived attribute that is not stored and a to-one relation end are left out, whatever the binding writes."),
        new("MQ4059", E, "A foreign key references columns of a table that are neither its primary key nor one of its unique keys (a unique constraint, or a unique index without a filter except on Oracle): a database accepts a foreign key only to a key."),
        new("MQ4060", E, "A foreign key's onDeleteColumns does not fit: its onDelete is not set-null or set-default, or it names a column that is not one of the key's columns, or one twice."),
        new("MQ4061", W, "A foreign key's on-delete sets a column that cannot take the value: set-null on a column that is not nullable, or set-default on one that is not nullable and has no default; deleting a referenced row then fails."),
        new("MQ4062", E, "A routine's volatility does not fit: a deterministic function that is not immutable, or a volatility on a procedure."),
        new("MQ4063", W, "A PostgreSQL security-definer routine does not set search_path, so a caller's schema can shadow what it names."),
        new("MQ4064", W, "A storage parameter that reaches nothing or that PostgreSQL would refuse: a PostgreSQL table or index parameter the table or index method does not take (a typo), or a stereotype with storage that does not apply to tables."),
        new("MQ4065", E, "A temporal key does not fit what PostgreSQL takes: a key WITHOUT OVERLAPS or a foreign key PERIOD with one column, a period key with an action other than no-action, a period key to a key that is not temporal, or a key without a period to a temporal key."),
        new("MQ4066", E, "Partitioning that PostgreSQL refuses: partitions without partitionBy, a partition with neither bounds nor default (or both), a primary or unique key without every partition column, more than one default partition, a default partition of a hash-partitioned table, or a partition name used twice."),
        new("MQ4067", W, "The migration ledger's schema (migrations.ledgerSchema) is also a schema of a PostgreSQL database in the model, declared or named by one of its objects: the bookkeeping tables would share it with modeled objects."),
        new("MQ4068", E, "Two index or constraint names collide in a namespace the dialect keeps (index and key names in a PostgreSQL schema with its tables, views and sequences; constraint names in a table; the other dialects' namespaces), compared case-insensitively after cutting each name to the identifier limit."),
        new("MQ4069", E, "A view, routine or SQL object depends on a SQL object that does not run in every environment it runs in: the dependency's environments must cover the dependent's, and empty means every environment."),
        new("MQ4070", W, "A SQL object with environments has phase before: environment scripts run after the schema script and the migrations, so its phase orders it only within its script."),
        new("MQ4076", E, "A table gets two primary keys: an inherited one beside its own, its entity's key, or another stereotype's."),
        new("MQ4077", E, "A fold into a stereotype is refused: the stereotype does not apply to tables or has no members; a target is not a designed or imported table file, excludes the stereotype or is written by another operation of the batch; or a matched column differs in a type facet, in another facet without `redefine`, or carries annotations its member lacks, or the stereotype would add a member the table lacks without `excludeMissing`."),
        new("MQ4078", W, "A redefinition states a facet equal to the value the member inherits, so the table stops following the stereotype for that facet."),
        new("MQ4079", W, "A synthesized table inherits a NOT NULL column with no default, identity or generation, which rows written through its entity or relation cannot fill."),
        new("MQ4082", E, "A column's `typeRef` is misplaced: it names no enum, reference type or scalar type, it sits on an overlay entry of a projected column, or a reference type's binding fills the column with a field."),
        new("MQ4083", E, "A typed column's type or a facet it states cannot hold its type's values: a reference type's codes, an enum's stored values, or a scalar type's base and facets."),
        new("MQ4084", E, "A foreign key that includes a column typed by a reference type does not point where the type's rows are: the type has no binding in the column's database, or the key does not pair the column with the code column of the bound table."),
        new("MQ4085", W, "A column and an attribute it stores, or that a binding maps to it, state different sensitivities."),
        new("MQ4087", W, "A column a binding maps was renamed or retyped since its provenance was recorded, so the attribute may no longer follow it."),
        new("MQ4088", W, "A binding's provenance does not describe its source: the source is not a table file, or none of the columns it records belongs to the source."),
        new("MQ4089", E, "A map-by-name or resync-binding operation is refused: an entity with no binding to the database, a source it cannot read, columns for more than one entity or naming a column the source lacks, or nothing to map or re-sync."),

        new("MQ4101", E, "A migration fact lacks the intent its change needs: a conversion, a data check for a narrowing, a drop's acknowledgement, a backfill's value or data check, a raw step's skipWhen guard; complete it or hold it."),
        new("MQ4102", E, "A change since the baseline needs a migration fact and has none: a rename or moved key, a type change that is not a widening, a dropped table, column or sequence, or a column made not null."),
        new("MQ4103", W, "An open fact the baseline lacks is stale: the model no longer shows its combined effect (a change made and undone, a pending change reverted or widened back), or a value map's codes were not codes of its type in the baseline model and its names did not change; migration fold removes it."),
        new("MQ4104", E, "The after links of a database's open facts form a cycle."),
        new("MQ4105", E, "A fact's after link names a fact of another database."),
        new("MQ4106", E, "A fact's fields do not fit its type: from equal to to, a conversion or data check with both forms or neither, released without held, a key or object listed twice, a values code listed twice or mapped to itself, an object kind the type does not take, or a value map that maps codes in no column or collection and changes no name, or whose to is also a from."),
        new("MQ4107", E, "Two open facts change the same object and neither comes after the other (merged copies changed one object); order them with after or fold them. The object is the fact's object key: a column for a column fact, a table for a table fact; a table drop also changes each of its members."),
        new("MQ4108", W, "A fact's SQL text has no entry for the database's dialect and no \"*\" entry."),
        new("MQ4109", W, "The baseline has errors that leave objects unresolved, so changes to them were not checked."),
        new("MQ4110", I, "A write changed objects whose elements have errors, so no facts were recorded for them; validate --against checks them."),
        new("MQ4111", W, "A fact's after link names a fact that does not exist (folded or removed in another copy); the link is ignored for ordering."),
        new("MQ4112", E, "Since the baseline, a kept row's code changed, a former code was added, or a type's name or plural name changed, and no open value-map fact of a database holding the type records it."),
        new("MQ4113", E, "An open value-map fact maps away a code that is a live code of its reference type."),
        new("MQ4114", W, "A write changed a kept reference row's code, or a reference type's name or plural name, outside the refactor operations, so no value map was recorded."),

        new("MQ5001", E, "Property fails its extension schema."),
        new("MQ5002", E, "Validation rule or calculation script error."),
        new("MQ5003", E, "Validation rule or calculation script exceeded a sandbox limit."),
        new("MQ5004", E, "Invalid extension file."),
        new("MQ5005", W, "A rule, rows or calculation script declares top-level let or var variables; it keeps no state between calls, so its results are never cached."),
        new("MQ5010", E, "A calculated value failed on an object: its function threw, broke a sandbox limit or returned a value that is not JSON."),
        new("MQ5011", E, "A calculated value does not fit its schema."),
        new("MQ5012", E, "A template, script or rule reads a calculated value that is not declared for the object's kind."),
        new("MQ5013", E, "Calculated values read each other in a cycle."),
        new("MQ5014", E, "A rule declaration file does not fit declared-rules.json."),
        new("MQ5015", E, "Two project rules share an id: a declared rule and another declared rule or a rule script."),
        new("MQ5016", E, "A declared rule names what does not exist or does not apply: a kind, a stereotype key, a package id, a path, a calculated value, a link kind, bounds or a message placeholder."),
        new("MQ5017", W, "A declared rule selects no object."),
        new("MQ5018", E, "A suppression names a rule that cannot be suppressed: MQ1xxx, MQ7012 or MQ5018 to MQ5020."),
        new("MQ5019", W, "A suppression names a rule nothing defines or a member the element does not hold, or repeats another entry."),
        new("MQ5020", W, "A suppression matched no finding in a whole-model validate."),

        new("MQ6001", E, "Invalid pack.json."),
        new("MQ6002", E, "Engine version range not satisfied."),
        new("MQ6003", E, "Template parse error."),
        new("MQ6004", E, "Output path refused."),
        new("MQ6005", E, "Duplicate or case-colliding output path."),
        new("MQ6006", E, "Render error."),
        new("MQ6007", E, "Sandbox limit exceeded."),
        new("MQ6008", E, "Formatter failed or its version does not match."),
        new("MQ6009", E, "Hand edit."),
        new("MQ6010", E, "Protected region lost."),
        new("MQ6011", W, "Text outside file blocks in a unit without output."),
        new("MQ6012", E, "Non-deterministic builtin used."),
        new("MQ6013", E, "Helper name collides with a builtin."),
        new("MQ6014", W, "Unit names an unknown formatter."),
        new("MQ6016", E, "Script error."),
        new("MQ6017", E, "Selector returned an unknown id."),
        new("MQ6018", E, "Stale schema snapshot."),
        new("MQ6019", E, "A unit's output path cannot stay under an allowed output root or be an allowed file."),
        new("MQ6020", E, "Two elements of one unit render the same output path."),
        new("MQ6021", E, "A unit's scope (for) is not a known scope."),
        new("MQ6022", E, "A unit names a template or companion template that is not in the pack."),
        new("MQ6023", E, "A project parameter value fails the pack's parameter schema."),
        new("MQ6024", W, "A project parameter value names a parameter the pack does not declare."),
        new("MQ6025", W, "A pack file that no unit reaches does not parse."),
        new("MQ6026", E, "A preview names an element outside its unit's scope."),
        new("MQ6027", E, "A block unit's target file holds its block twice, or a block that is not closed; the file is left alone."),
        new("MQ6028", I, "A block unit's target file does not exist and createFile is false; nothing is written (target-missing)."),
        new("MQ6029", E, "A write against a model snapshot opened read-only; nothing is written (restore the snapshot to change it)."),
        new("MQ6030", E, "A copy unit's source pattern matches no pack file; the unit is skipped and the pack's other units still render."),
        new("MQ6031", E, "A copy unit's token names a parameter the pack does not declare, or one whose value is not a string; the unit is skipped."),
        new("MQ6032", E, "A generation filter names a pack the run lacks, a unit no run pack has, or a bare unit id several run packs have."),
        new("MQ6033", E, "A run limited by unit or element renders a unit that reads the schema diff; schema snapshots advance only in whole-pack runs."),
        new("MQ6034", W, "A generation filter selects no unit and no output; nothing is generated."),
        new("MQ6042", E, "A uses entry in pack.json or maquettiste.json names a pack that does not exist or did not load, or a list names one pack twice."),
        new("MQ6043", E, "A used pack's version does not satisfy the uses range, or the range does not parse."),
        new("MQ6044", E, "A pack's uses names the pack itself or forms a cycle."),
        new("MQ6045", E, "A used pack's helper namespace collides with a builtin, a template keyword or variable, or a root helper of a pack that sees it."),
        new("MQ6046", W, "A pack has no units, and no pack and no project uses lists it."),
        new("MQ6047", E, "A template reads the exports of a pack its pack does not see, or an export template reads its own pack's exports."),
        new("MQ6048", E, "Two units of one pack export one name for one element with different values."),
        new("MQ6049", E, "An export's each names a member that is not a list of items with ids, or two items share an id."),
        new("MQ6050", E, "A unit reads exports that failed to evaluate; it is not rendered."),
        new("MQ6051", E, "A pack test case's output differs from its expected tree: a file added, missing or different."),
        new("MQ6052", E, "A pack helper test fails: an assertion fails, the test throws or it hits a sandbox limit."),
        new("MQ6053", E, "A pack test is malformed: a case without a project or expected/, a case holding templates/, an invalid case.json or one naming a missing case, cases in a pack without units, a test file that does not load or registers anything but tests, or two tests with one name."),

        new("MQ7001", E, "Duplicate code in a reference type, across all its seeds."),
        new("MQ7002", W, "Two codes of one reference type differ only by case."),
        new("MQ7003", E, "A row misses a required field."),
        new("MQ7004", E, "A seed cell does not match its column's type or facets."),
        new("MQ7005", E, "A seed column names neither a field nor an end of the target, names one twice, or a row has more values than columns."),
        new("MQ7006", E, "A collection attribute whose effective reference storage strategy in a database does not support collections."),
        new("MQ7007", E, "A storage choice names a strategy the project has not declared."),
        new("MQ7008", E, "Storage options fail the strategy's option schema."),
        new("MQ7009", E, "A seed cell names a row that does not exist."),
        new("MQ7010", E, "Invalid reference type field: code type, field type or reserved field name."),
        new("MQ7011", E, "An allowedValues entry on a reference-typed attribute names a code that is not in the type's rows."),
        new("MQ7012", E, "The enum lookup-table storage option ('lookup') is retired: convert the enum to a reference type, whose storage strategy then decides the lookup table."),
        new("MQ7013", E, "A uuid code is not a UUID in canonical form: 8-4-4-4-12 lowercase hexadecimal digits with hyphens."),
        new("MQ7014", E, "A value written into a column typed by an enum or a reference type is not one of its stored values or codes: a table seed cell, the column's literal default, or a binding constant."),
        new("MQ7017", E, "A former code is a live code of its type, is listed twice, or does not fit the type's code (type, length, pattern)."),
        new("MQ7018", E, "A former code's replacedBy names a row that is not a row of its type. A missing row is MQ2001."),
        new("MQ7019", E, "A default, allowedValues entry or seed cell names a former code. It replaces MQ3019, MQ7009, MQ7011 and MQ7014 for that value."),
        new("MQ7020", E, "A reference-type refactor cannot apply (an unknown or scripted code, a code taken or former, mismatched types, a binding, a missing field, a second refactor of the type in the batch); the message gives the reason."),

        new("MQ7101", E, "A seed targets an abstract entity."),
        new("MQ7102", E, "A seed key cell is missing, or two rows of one target share a key value."),
        new("MQ7103", W, "Required row references form a cycle, so no insert order exists."),
        new("MQ7104", W, "A seed has more than 10,000 rows or 5 MB."),
        new("MQ7105", E, "A column of an entity seed names a to-many end."),
        new("MQ7106", E, "A relation link stated twice: an entity seed has an end column of a relation that also has seeds."),
        new("MQ7107", E, "A table seed targets a table that is not a table file of a database, or lists a column or foreign key the table does not have."),
        new("MQ7108", E, "A table seed lists an identity, computed, generated or stable id column, whose value the database or the engine gives."),
        new("MQ7109", E, "A table seed's cell does not fit its column (type, length, precision or scale, or NULL in a NOT NULL column without a default), or a foreign-key cell does not have the referenced row key's shape."),
        new("MQ7111", E, "A foreign key that a table seed or a reference type's binding fills, or a foreign-key cell's natural key, names no row of the referenced table's rows, or one they do not hold in every environment the row is loaded in."),
        new("MQ7112", E, "Two rows of one table's seeds share a row key (in environments that overlap), or a table seed's row has the key of a row of a reference type bound to the table."),
        new("MQ7113", W, "A table receives rows from the seeds of an entity bound to it and from its own seeds or a reference type bound to it: a row should have one source."),
        new("MQ7114", E, "A table seed has no row key (the unique constraint it names does not exist, or the table has no primary key), or a row lacks a key cell."),
        new("MQ7116", E, "A seed's rows file or rows script is missing or cannot be read, or the seed has rows of its own beside it."),
        new("MQ7117", E, "A reference type's binding names a table that is not a table file of its database, or the type has two bindings to one database."),
        new("MQ7118", E, "A reference type's binding maps a field or a constant wrongly: no code column, an unknown field or column, a column filled twice or by the database, or a column the field does not fit."),
        new("MQ7119", E, "A reference type's binding has no key: no primary key or unique constraint of its table is made of the code column (and constant columns), or another type bound to the table has the same constants or puts rows of the same key there."),
        new("MQ7120", E, "A reference type's binding fills no value for a NOT NULL column of its table that has no default."),
        new("MQ7121", E, "Two seeds of one table key their rows differently (a different key, or a key beside the primary key)."),
        new("MQ7124", E, "`stableId` is set on a column that is not of type `uuid`, or whose value the database fills (identity, sequence, generated, computed)."),
        new("MQ7125", E, "The row key of a table with a stable id column holds that column, or a value the database fills directly or through a foreign-key entry, so the stable id cannot be derived."),
        new("MQ7126", E, "A table seed's column entry names a foreign key that is not its target's, is listed beside one of that key's own columns, references a table that has no rows in the model, or the seed's row key holds part of the entry's columns."),
        new("MQ7127", E, "A seed's rows script fails: it does not load, registers anything but rows exactly once, throws, exceeds a sandbox limit, or returns something that is not an array of row objects."),
        new("MQ7128", E, "A scripted row has a key its seed's columns do not name, or the seed's target has no row key to derive row ids from."),

        new("MQ7201", E, "Invalid localization settings: a locale that is not BCP 47, a default not in the locales, a fallback naming an undeclared locale, a fallback cycle, or a required node kind that does not exist."),
        new("MQ7202", W, "A locale folder for an undeclared locale or for the default locale; its files are not loaded."),
        new("MQ7203", W, "An orphan translation: its id is not a localizable node of the model, or it has a field the node does not have."),
        new("MQ7204", I, "A locale is incomplete in a shard: the counts of missing and stale texts, for the required kinds."),
        new("MQ7205", I, "A missing translation, one per node and field (off unless validation.rules gives it a severity)."),
        new("MQ7206", I, "Stale translations in a shard: made from a default text that has changed since."),
        new("MQ7207", W, "A translation entry in the wrong shard (its node moved); it still applies."),
        new("MQ7208", E, "A translated description's Markdown sidecar does not exist."),
        new("MQ7209", E, "One id in two shards of one locale; the ordinally first path applies."),
        new("MQ7210", W, "A shard whose locale differs from its folder, or whose scope differs from its path; it applies to its declared locale and scope."),
        new("MQ7211", W, "A plural name on a to-one relation end, or its translation; it is never read."),
        new("MQ7212", E, "x-translatable is not on a top-level string property of an extension file or stereotype parameters, or of a custom kind's content or member items whose name is an identifier: nested, on another type, or not a boolean."),
        new("MQ7213", W, "x-translatable is on a property whose schema applies only to kinds without translations (physical objects, mappings, diagrams, vocabulary containers)."),

        new("MQ8001", E, "A branding color that is not a hex color (#rrggbb or #rgb)."),
        new("MQ8002", E, "The branding icon is not an .svg or .png file under branding/ in the model folder, or the file does not exist."),
        new("MQ8003", E, "The branding icon is not a safe SVG (scripts, event handlers, external references) or a PNG of at most 512 KB."),

        new("MQ9001", E, "A compound state (or the process) whose initial is not one of its direct children, or an initial on a state that is not compound; set initial to a direct child, or remove it to use the first child.", "set-initial"),
        new("MQ9002", E, "A state's type contradicts its children: an atomic, final, history or choice state with children, or a compound or parallel state without; change the type or move the children."),
        new("MQ9003", W, "An unreachable state: no path from the initial state through transitions, history defaults or invoke completions enters it (guards are ignored); add a transition to it or remove it."),
        new("MQ9004", W, "A dead end: a reachable atomic state with no transition on itself or an ancestor and no invoke; add a transition or make it final."),
        new("MQ9005", I, "No final state is reachable, so the process never completes (normal for some lifecycles); add a final state and a transition to it if instances should end."),
        new("MQ9006", W, "Overlapping transitions for one source and trigger: a transition after an unguarded one, or a guard tested twice, never fires; guard the earlier transition or remove the later one."),
        new("MQ9007", E, "Invalid targets: a state of another process, two targets in one region, or several targets not in orthogonal regions of one parallel ancestor; keep one target per region of this process."),
        new("MQ9008", E, "Trigger fields inconsistent: an event trigger without an event, an after trigger without a positive ISO 8601 duration, done on a state that cannot complete, an invoke trigger naming no invoke of the source, or a field of another trigger; fix the trigger or its field."),
        new("MQ9009", E, "A cycle of unguarded eventless transitions: the macrostep would never end; guard one of the transitions or give it an event."),
        new("MQ9010", E, "A choice state whose outgoing transitions are not all always, whose last transition is guarded (no default), or that has none; make them always transitions ending with an unguarded one."),
        new("MQ9011", E, "A history state outside a compound parent, or a default target that is not a descendant of its parent; move the history state into a compound state or pick a default inside it."),
        new("MQ9012", E, "A final state that is the source of transitions or invokes work; remove them, or make the state atomic."),
        new("MQ9013", W, "A declared event, guard, action or invoke that nothing uses (an invoke is used by an invoke-done or invoke-error transition); use it or remove it."),
        new("MQ9014", E, "A reference to a state, event, guard, action or invoke of another process; declare it in this process instead."),
        new("MQ9015", E, "Invokes: a sub-process invoke cycle, a process invoke without a process, or a human task without actors; break the cycle, set the process, or list the actors."),
        new("MQ9016", W, "A process diagram member that is not a state of the diagram's process; remove the member."),
        new("MQ9017", W, "A parallel state with one region; add a region or make the state compound."),
        new("MQ9018", W, "A done transition whose source has no reachable final state (in every region, for a parallel source), so it never fires; add a reachable final child or change the trigger."),
        new("MQ9019", E, "A process operation (sync-enum, set-lifecycle, set-initial, refresh-scenario) was refused: its element is not what the operation needs, the target is not a direct child or a process, the enum sync would remove members still in use, a scenario cannot be replayed to its last step, or another operation of the batch writes the process, enum or scenario it changes; fix the operation or the step, change the uses to a remaining member, or run it in a batch of its own."),

        new("MQ9101", E, "A gate needs more signatures than its signers can give (only person actors, repeat signing off); lower required, add signers, or allow repeat signers."),
        new("MQ9102", E, "A gate's required actors are not all among its signers; add them to signers."),
        new("MQ9103", E, "A gate on a transition whose trigger is not event, or two gates on one source and event; move the gate to an event transition or keep one gate."),
        new("MQ9104", E, "A gate without meanings; add at least one meaning that says what a signature means."),
        new("MQ9105", W, "A gate signer that may not raise the gate's event (the event lists its actors and leaves the signer out); add the signer to the event's actors."),
        new("MQ9106", I, "An actor no process references (event actors, gate signers, human tasks); use it in a process or remove it."),

        new("MQ9201", E, "Lifecycle inconsistent: a lifecycle without a subject, a subject whose lifecycle does not name the process, or an entity whose lifecycle names an orchestration or another entity's lifecycle; set lifecycle on the subject to the process."),
        new("MQ9202", E, "A bound attribute that is not a single-valued enum attribute of the subject (own, inherited or from a stereotype), or one set on an orchestration; bind a single-valued enum attribute of the subject."),
        new("MQ9203", E, "Enum drift: the bound enum's members differ from the lifecycle's root-level states (missing, extra or out of order); sync the enum from the process.", "sync-enum"),
        new("MQ9204", W, "The bound enum is used by other attributes or processes, so syncing it from the process changes them too; give the lifecycle its own enum if they should not follow."),
        new("MQ9205", W, "The bound attribute's default is not the lifecycle's initial root-level state; set the default to that state's name."),
        new("MQ9301", E, "A scenario step is refused while expect.accepted is true (no transition, a guard false, an actor not allowed, a gate refusal), or accepted while it is false; fix the step's input or set accepted to what the process does."),
        new("MQ9302", E, "The active states after a scenario step differ from expect.states; fix the chart or update the expectations from a replay.", "refresh-scenario"),
        new("MQ9303", E, "The context attributes a scenario step changed differ from expect.context; fix the actions or update the expectations from a replay.", "refresh-scenario"),
        new("MQ9304", E, "Whether the process is final after the scenario's last step differs from outcome; fix the steps or update the expectations from a replay.", "refresh-scenario"),
        new("MQ9305", E, "A scenario step's fields are inconsistent: an event the process does not declare, a time step without a positive duration, an invoke that is not pending, or a payload value that is not an attribute of the event or does not fit its type; fix the step."),
        new("MQ9306", W, "A guard without an expression is evaluated during a scenario step that has no assume value for it, so the replay stops there; add the guard to the step's assume, or give the guard an expression."),
        new("MQ9401", W, "XState configuration the model has no place for (custom actor logic, tags, output, systemId, parameterized guards or actions, spawn, sendTo, unknown keys) was kept as opaque data in the process's source extensions and is written back on export; model it with process features where one exists, or leave it for the host."),
        new("MQ9402", W, "An inline function in the XState configuration (a guard, action, delay or invoke source given as function text) became a named stub whose description holds the text, which never runs; give the stub an expression or implement its handler."),
        new("MQ9403", E, "The import input is not a statechart configuration the importer accepts (invalid JSON, no states, a target or initial state that does not resolve, a parallel root, or an unknown state type); fix the configuration and import it again."),
        new("MQ9404", W, "An XState feature was mapped approximately or dropped on import or export (a guard combinator, a named delay without a value, an actor or custom state id the model does not know, a name that is not an identifier, data that no longer has a place); review the imported process."),
        new("MQ9405", E, "An id carried in the configuration names an element of another kind or of another process, or is used twice, so the node got a new id; re-import into the process the ids belong to, or remove the stale ids."),
        new("MQ9406", I, "Export wrote model data with no XState equivalent (ids, display names, types, gates, durations, invoke kinds) into meta.maquettiste, so an import restores it; keep the meta entries when editing the configuration by hand."),
        new("MQ9501", E, "A guard or action expression does not parse as one JavaScript expression; fix its syntax (an action returns an object literal in parentheses)."),
        new("MQ9502", E, "A guard or action expression threw while a scenario replayed or a simulation ran; fix the expression, for example guard against missing values."),
        new("MQ9503", E, "A guard or action expression exceeded the sandbox deadline (50 ms) or statement limit (100,000); simplify it, or leave the expression empty and implement the handler."),
        new("MQ9504", W, "A guard returned a value that is not a boolean, which counts as false; make the expression return true or false."),
        new("MQ9505", W, "An action's result names an attribute the context does not declare, or a value that does not fit the attribute's type, and the entry is ignored; return only context attribute names with values of their types."),
        new("MQ9506", W, "Two guarded transitions of one source and trigger were enabled at once, and the first in priority order was taken; make the guards exclusive or reorder the transitions."),
        new("MQ9507", E, "A macrostep ran more than 1,000 microsteps (an eventless loop whose guards keep holding, or internal events that keep raising each other); break the loop with a guard that turns false."),
    ];

    private static readonly FrozenDictionary<string, RuleInfo> ById = Rules.ToFrozenDictionary(r => r.Id, StringComparer.Ordinal);

    private static readonly FrozenDictionary<string, string> FamilyLabels = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["MQ10xx"] = "Model files",
        ["MQ20xx"] = "References and vocabularies",
        ["MQ30xx"] = "Model structure",
        ["MQ31xx"] = "Expressions",
        ["MQ40xx"] = "Databases and mappings",
        ["MQ41xx"] = "Migrations",
        ["MQ50xx"] = "Extensions and script rules",
        ["MQ60xx"] = "Packs and generation",
        ["MQ70xx"] = "Reference types",
        ["MQ71xx"] = "Seeds",
        ["MQ72xx"] = "Localization",
        ["MQ80xx"] = "Branding",
        ["MQ90xx"] = "Processes",
        ["MQ91xx"] = "Actors and gates",
        ["MQ92xx"] = "Lifecycles",
        ["MQ93xx"] = "Scenarios",
        ["MQ94xx"] = "Process import and export",
        ["MQ95xx"] = "Process expressions and simulation",
    }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>The hundreds group of a rule id: <c>MQ7204</c> is in <c>MQ72xx</c>.</summary>
    /// <param name="id">A built-in rule id.</param>
    /// <returns>The family key.</returns>
    public static string FamilyOf(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        return id.Length >= 4 ? string.Concat(id.AsSpan(0, 4), "xx") : id;
    }

    /// <summary>What a family's rules cover.</summary>
    /// <param name="family">A family key from <see cref="FamilyOf"/>.</param>
    /// <returns>The label, or the key itself for an unknown family.</returns>
    public static string FamilyLabel(string family) => FamilyLabels.TryGetValue(family, out var label) ? label : family;

    /// <summary>Every built-in rule with its family and whether it can be turned off, ordered by id.</summary>
    /// <returns>The catalog entries.</returns>
    public static IReadOnlyList<RuleCatalogEntry> Describe() => [.. Rules.Select(Entry)];

    /// <summary>
    /// The catalog by family (next-phase-1-design.md section 5.2): the total, then each family with its label, rule count and first and
    /// last ids, in id order, so an agent sees what there is without every description.
    /// </summary>
    /// <returns>The summary.</returns>
    public static RuleCatalogSummary Summarize()
    {
        var families = new List<RuleFamilySummary>();
        foreach (var rule in Rules)
        {
            var family = FamilyOf(rule.Id);
            if (families.Count > 0 && families[^1].Family == family)
                families[^1] = families[^1] with { Rules = families[^1].Rules + 1, Last = rule.Id };
            else
                families.Add(new RuleFamilySummary(family, FamilyLabel(family), 1, rule.Id, rule.Id));
        }

        return new RuleCatalogSummary(Rules.Length, families);
    }

    /// <summary>
    /// One page of full entries that match every filter given (AND), in id order: <paramref name="family"/> equals the rule's
    /// <see cref="FamilyOf"/>, <paramref name="ids"/> holds the rule id exactly, and <paramref name="query"/> occurs in the id or the
    /// description, ignoring case (ordinal). Pages use the keyset cursor of the model's paged reads, keyed by rule id.
    /// </summary>
    /// <param name="family">A family key such as <c>MQ72xx</c>, or <see langword="null"/>.</param>
    /// <param name="ids">Rule ids, or <see langword="null"/>; an empty collection matches nothing.</param>
    /// <param name="query">Text to find, or <see langword="null"/>.</param>
    /// <param name="cursor">The <see cref="RuleCatalogPage.Next"/> of the previous page, or <see langword="null"/> for the first.</param>
    /// <param name="limit">The page size, 1 to 1000.</param>
    /// <returns>The page.</returns>
    /// <exception cref="FormatException"><paramref name="cursor"/> is not a cursor a paged read returned.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="limit"/> is out of range.</exception>
    public static RuleCatalogPage Page(string? family, IReadOnlyCollection<string>? ids, string? query, string? cursor, int limit)
    {
        var only = ids is null ? null : new HashSet<string>(ids, StringComparer.Ordinal);
        var rows = Rules.Where(r => (string.IsNullOrEmpty(family) || string.Equals(FamilyOf(r.Id), family, StringComparison.Ordinal))
            && (only is null || only.Contains(r.Id))
            && (string.IsNullOrEmpty(query) || r.Id.Contains(query, StringComparison.OrdinalIgnoreCase) || r.Description.Contains(query, StringComparison.OrdinalIgnoreCase)));
        var (items, next) = ModelPages.Page(rows, r => ("rule", r.Id, ""), cursor, limit);
        return new RuleCatalogPage([.. items.Select(Entry)], next);
    }

    private static RuleCatalogEntry Entry(RuleInfo r) =>
        new(r.Id, r.DefaultSeverity, r.Description, FamilyOf(r.Id), FamilyLabel(FamilyOf(r.Id)), r.CanBeDisabled, r.QuickFix);

    /// <summary>Every built-in rule, ordered by id.</summary>
    public static IReadOnlyList<RuleInfo> All => Rules;

    /// <summary>Looks a rule up by id.</summary>
    /// <param name="id">The rule id.</param>
    /// <param name="rule">The rule when found.</param>
    /// <returns><see langword="true"/> for a built-in rule.</returns>
    public static bool TryGet(string id, [MaybeNullWhen(false)] out RuleInfo rule) => ById.TryGetValue(id, out rule);

    /// <summary>Returns a built-in rule.</summary>
    /// <param name="id">The rule id.</param>
    /// <returns>The rule.</returns>
    /// <exception cref="KeyNotFoundException">The id is not a built-in rule.</exception>
    public static RuleInfo Get(string id) => ById[id];

    /// <summary>
    /// Whether a rule reports what a scenario replay found (MQ93xx, MQ9502 to MQ9507): a verification result, like a failing test, that is
    /// reported on a save but does not refuse it, so a chart edit can be saved and its scenarios then refreshed (phase-3-design.md 3).
    /// </summary>
    /// <param name="ruleId">The rule id.</param>
    /// <returns><see langword="true"/> for a replay finding.</returns>
    public static bool IsReplayFinding(string ruleId) =>
        ruleId.StartsWith("MQ93", StringComparison.Ordinal) || ruleId is "MQ9502" or "MQ9503" or "MQ9504" or "MQ9505" or "MQ9506" or "MQ9507";

    /// <summary>Creates a diagnostic with the rule's default severity.</summary>
    /// <param name="ruleId">The built-in rule id.</param>
    /// <param name="message">The message.</param>
    /// <param name="elementId">The element id, when known.</param>
    /// <param name="filePath">The repo-relative file path, when known.</param>
    /// <param name="jsonPointer">The JSON pointer, when known.</param>
    /// <returns>The diagnostic, without line and column.</returns>
    public static Diagnostic Create(string ruleId, string message, string? elementId = null, string? filePath = null, string? jsonPointer = null) =>
        new(ruleId, Get(ruleId).DefaultSeverity, message, elementId, filePath, jsonPointer, null, null);
}
