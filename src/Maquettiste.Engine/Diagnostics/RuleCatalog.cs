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

        new("MQ2001", E, "Dangling reference."),
        new("MQ2002", E, "Reference to an element of the wrong kind."),
        new("MQ2003", E, "Unknown stereotype."),
        new("MQ2004", E, "Stereotype not applicable to this kind."),
        new("MQ2005", E, "Unknown category."),
        new("MQ2006", I, "Undeclared tag (an error when the tag vocabulary is strict)."),
        new("MQ2007", E, "Attribute validation names an unknown rule."),
        new("MQ2008", E, "A tag or category is declared only in a domain vocabulary outside the element's domain chain."),

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
        new("MQ3019", E, "Literal default does not match the attribute's type (use defaultExpression for now, today, new-uuid or new-ulid)."),
        new("MQ3020", E, "A stereotype's key cannot change."),
        new("MQ3021", E, "A domain vocabulary redeclares a tag key or category name of the global vocabulary or an enclosing domain's."),
        new("MQ3022", E, "A diagram with membership 'package' and no package: it has nothing to follow; set the diagram's package, or set membership to 'explicit' so its members list is the diagram."),

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
        new("MQ4056", W, "A table, view or sequence asks for a DDL feature its database's dialect does not have, so the DDL leaves it out: a deferrable foreign key (SQL Server, MySQL), ON UPDATE or ON DELETE restrict or set-default (Oracle), set-default (MySQL), index include columns (MySQL, SQLite, Oracle), a partial index (MySQL, Oracle), an index method other than the default outside PostgreSQL (clustered: SQL Server and PostgreSQL), an index expression (SQL Server: the index is left out), a key prefix length outside MySQL, a MySQL index on a text or blob column without one (the index is left out), nullsNotDistinct outside PostgreSQL, a stored computed column (Oracle), identity options (a seed on SQLite, an increment or always on SQLite and MySQL), a clustered primary key outside SQL Server, a materialized view outside PostgreSQL and Oracle, WITH CHECK OPTION on SQLite or on a materialized view, or a sequence on SQLite or MySQL."),
        new("MQ4057", E, "A column facet does not fit its type: unicode on a column that is not string or text, or fixedLength on a column that is not string or binary."),
        new("MQ4059", E, "A foreign key references columns of a table that are neither its primary key nor one of its unique keys (a unique constraint, or a unique index without a filter except on Oracle): a database accepts a foreign key only to a key."),

        new("MQ5001", E, "Property fails its extension schema."),
        new("MQ5002", E, "Validation rule script error."),
        new("MQ5003", E, "Validation rule exceeded a sandbox limit."),
        new("MQ5004", E, "Invalid extension file."),

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

        new("MQ7101", E, "A seed targets an abstract entity."),
        new("MQ7102", E, "A seed key cell is missing, or two rows of one target share a key value."),
        new("MQ7103", W, "Required row references form a cycle, so no insert order exists."),
        new("MQ7104", W, "A seed has more than 10,000 rows or 5 MB."),
        new("MQ7105", E, "A column of an entity seed names a to-many end."),
        new("MQ7106", E, "A relation link stated twice: an entity seed has an end column of a relation that also has seeds."),

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
        ["MQ40xx"] = "Databases and mappings",
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
    public static IReadOnlyList<RuleCatalogEntry> Describe() =>
        [.. Rules.Select(r => new RuleCatalogEntry(r.Id, r.DefaultSeverity, r.Description, FamilyOf(r.Id), FamilyLabel(FamilyOf(r.Id)), r.CanBeDisabled, r.QuickFix))];

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
