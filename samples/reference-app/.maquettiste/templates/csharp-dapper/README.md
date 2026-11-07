# csharp-dapper

A C# model and [Dapper](https://github.com/DapperLib/Dapper) data access layer from the resolved model: entity classes
as generated and hand-written halves of one partial class, enums, value objects, a repository per mapped entity, one
registration file per package and Dapper type handlers; for operations, the request, result, error codes and handler
interface, and with `endpointsFolder` one HTTP endpoint each (see Operations); for processes, their states, definitions
and contracts, the handlers, services, machine and store as generated and hand-written pairs, HTTP endpoints with
user-code regions, a typed in-process dispatcher with pipeline behaviours, a generated statechart interpreter and one
xunit test per scenario (see Processes).

The pack writes to one output root. `maquettiste init --pack csharp-dapper` sets `packs.csharp-dapper.output` to
`src/Generated` and allows that folder; `generate` rewrites it whenever the model changes, and whether you commit it or keep it out
of version control is your choice (the `gitignore` unit below can write the ignore line for you). Every path below is under
`src/Generated/`. The generated code needs C# 12, .NET 9 or later (`Guid.CreateVersion7`) and the `Dapper` package.

## Output

For entity `Invoice` in package `Billing`:

| Unit | Mode | For | Writes |
| --- | --- | --- | --- |
| `entity` | `pair` | `each entity` | `Billing/Invoice.g.cs` every run (with `Validate()` when the entity has checks; see Checks), and its companion `Billing/Invoice.cs` once |
| `enum` | `overwrite` | `each enum` | `Billing/InvoiceStatus.g.cs`: the enum and an `InvoiceStatusCodes` class (`ToCode`, `Parse`) for text storage |
| `value-object` | `overwrite` | `each value object` | `Billing/Money.g.cs`: a `sealed partial record` with `init` properties |
| `reference-type` | `overwrite` | `each reference type` | `ReferenceData/UnitOfMeasure.g.cs`: a `sealed record` and a static class of rows (see Reference types) |
| `resources` | `overwrite` | `each locale` | `ReferenceData/ReferenceData.resx`, `ReferenceData.fr.resx`, …: display names and row labels per declared locale |
| `repository` | file blocks | `each entity` | `Billing/InvoiceRepository.g.cs`: `IInvoiceRepository` and `InvoiceRepository` |
| `query` | `overwrite` | `each query` | `Queries/InvoicesByCustomerQuery.g.cs`: `IInvoicesByCustomerQuery` and `InvoicesByCustomerQuery` (see Queries) |
| `operation-contracts` | `overwrite` | `each operation` | `Operations/Billing/SendInvoice.g.cs`: the request, result, error codes and handler interface of operation `send` on Invoice (see Operations) |
| `operation-endpoints` | `overwrite` | `each operation` | Only with `endpointsFolder` set: `<endpointsFolder>/Operations/Billing/SendInvoiceEndpoint.g.cs`, one route that calls the handler (see Operations) |
| `registrations` | file blocks | `model` | `Billing/BillingRepositories.g.cs`: one per package with repositories; `Queries/QueryRegistrations.g.cs` when the model has queries |
| `type-handlers` | `overwrite` | `model` | `DapperTypeHandlers.g.cs`: the Dapper type handlers and `UlidGenerator` |
| `code-points` | `overwrite` (file block) | `model` | When an entity has checks or a process guard is written in the expression language: `CodePoints.g.cs`, `CodePoints.Compare`, the order of strings by code point that rendered checks and guards use (see Checks) |
| `verbs` | `overwrite` (file block) | `model` | When the model uses verbs (a verb vocabulary, or a verb named anywhere): `Verbs.cs` in the base namespace, an enum `Verb` of every item of `model.verbs` (each documented with its meaning) and `Verbs.Key` and `Verbs.FromKey` between members and keys, since the key is what to store. The interpreter's `EventDefinition` and `TransitionDefinition` then take an optional verb, which each process definition writes for an event that names one and an automatic move that names one, and an operation's handler interface gains a `Verb` constant when it names one; output without verbs is unchanged |
| `seed-loader` | `overwrite` (file block) | `model` | With `seedLoader`: `SeedDataLoader.g.cs`, `SeedDataLoader` and `SeedDialect`. `new SeedDataLoader(connection, SeedDialect.PostgreSql, "data/main").LoadAsync("dev")` applies the seed-data pack's files for a database (`manifest.json` and its CSVs, or `seed-data.json`) in their order: a seed's rows when it belongs to every environment or to the one given, inserted when their key is missing (`once`), or also updated (`converge`, deleting the rows the seed does not hold with `delete`), with parameterized statements found by the table's key. `SeedHash` lets an application skip a load it has done. Nothing calls it. |
| `gitignore` | `block`, `createFile` | `model` | Only with `gitignorePath` set: a managed block in that ignore file listing the generated folder (see Keeping the generated folder out of version control) |

Folders follow the package tree (`Billing/Catalog/` for package `Catalog` inside `Billing`), and namespaces follow it too:
`<namespace>.Billing.Catalog`.

## Checks and the expression renderer

An entity's checks, written in the expression language (an entity's `checks`, each with a `condition`, the `attribute` a failure
points at and a `message`), become `Validate()` on its generated half. It returns one error per failed check: the JSON pointer of
the attribute (`/issuedOn`), the check's name as its code (`dueAfterIssue`) and its message, else the condition's text. A check
whose answer is unknown, because a value it reads is null, passes, as the CHECK a binding writes into the table does; whether a
value must be given is the attribute's `required`. A derived entity's `Validate()` overrides its base's and checks both.

```csharp
public virtual IReadOnlyList<(string Pointer, string Code, string Message)> Validate()
{
    var errors = new List<(string Pointer, string Code, string Message)>();
    bool? ok = DueOn switch { { } v1 => (bool?)(IssuedOn <= v1), _ => null };
    if (ok == false)
        errors.Add(("/issuedOn", "dueAfterIssue", "An invoice is due on or after the day it is issued."));
    return errors;
}
```

Each answer goes through the local `ok`, so a check that is constant, such as `number is not null` on a required `number`, is no
unreachable code under warnings as errors.

`expr_cs(condition, spec)` in `helpers.js` writes the C#. It is an **example renderer, a starting point to copy** into your own
pack and adapt, not a contract: the engine ships no C# emitter, and every pack renders the typed tree (`node`, `op`, `call`,
`args`, `value` with its exact text `valueText`, `type` with `kind` and `bits`, `nullable`, `text`) in its own language. It keeps
the language's answers:

| The language | C# |
| --- | --- |
| Unknown (null) and Kleene's `and`, `or`, `not`, `implies` | `bool?` with `&`, `\|` and `!`; `&&` and `\|\|` when nothing may be null |
| A comparison, `\|\|` or a function with a null operand is null | Each operand that may be null is bound once by a pattern, `(A, B) switch { ({ } v1, { } v2) => (bool?)(v1 <= v2), _ => null }`, where C#'s lifted operators would answer `false` (ordering) or skip the null (concatenation): no operand is written or evaluated twice, however deep the nesting, and C# knows the bound values are not null. Arithmetic uses C#'s lifted operators, which give null |
| `decimal` exact; integer arithmetic never overflows, as in PostgreSQL's `numeric` | `decimal`; integer arithmetic in `System.Numerics.BigInteger`, since `long` would wrap to a wrong answer and `decimal` would throw past 28 digits; literals from their exact text (`valueText`), and a decimal literal of more than 28 digits is not rendered, since C# would round it. Decimal arithmetic itself keeps about 28 significant digits, where PostgreSQL keeps every digit, so a result longer than that can round |
| Strings compare equal ordinally and order by code point; `trim` removes spaces; `length` counts code points | `string.Equals(…, StringComparison.Ordinal)`; `CodePoints.Compare` (from the `code-points` unit) for `<`, `<=`, `>`, `>=` and `between`, since `string.CompareOrdinal` orders UTF-16 code units, which puts U+E000 to U+FFFF above the characters beyond the basic plane; `Trim(' ')`; `EnumerateRunes()` |
| `add_months` clamps to the month's last day; offsets compute in UTC | `AddMonths`; `UtcDateTime`, `ToUniversalTime()` |
| `matches` in the portable subset | `Regex.IsMatch` with `RegexOptions.NonBacktracking` (linear time, it never backtracks) and `Singleline` (`.` matches a newline); `$` becomes `\z`, since .NET's `$` also matches before a final newline; case counts. .NET counts UTF-16 code units where the language counts code points, so a pattern that counts characters beyond the basic plane can differ |

What it does not render returns `""`, and the unit falls back: a check becomes a comment in `Validate()` ("validate it in
Invoice.cs"), a guard a stub in the handlers companion. That is division (its rounding is PostgreSQL's), the `json_` functions,
reference-type codes, a `datetime` against a `datetimeoffset`, `now()` (a guard's call carries no clock), value object members
and a `case` over enum members. The spec names what each path reads: `"path|C# expression|C# type|kind|member=C# member,..."`
joined with `;` (`_csharp.scriban` `expr_entry` writes one entry; `entity.scriban` maps attributes to properties), and an entry
named `compare()` the function that orders strings (`expr_functions`, `global::<namespace>.CodePoints.Compare`); without it a
string order is not rendered.

## The pair pattern

A `pair` unit renders two templates for one entity. `Invoice.g.cs` is generated on every run and must not be edited; it declares
`public partial class Invoice` with a property per attribute (inherited attributes stay on the base class) and, for the table the
repositories use, a property per column that no attribute maps, such as the foreign key `CustomerId`. `Invoice.cs` is written
only when it is missing and is never touched again (the manifest records it as owned), so it is where hand-written members,
interfaces and attributes go:

```csharp
// Invoice.cs, yours
public partial class Invoice
{
    public bool IsOverdue(DateOnly today) => Status == InvoiceStatus.Issued && IssuedOn.AddDays(30) < today;
}
```

The C# compiler merges both halves. The generated half never needs editing, so regeneration never loses work, and deleting a
companion brings back the empty stub.

With the default settings the companions sit next to the generated files, in `src/Generated`. To commit them while leaving the
generated files out of version control, give them their own folder: set `packs.csharp-dapper.output` to `src`, `generatedFolder`
to `Generated` and `partialFolder` to `Model`, declare `src/Generated` and `src/Model` in `outputs.allow`, and commit `src/Model`
(with `gitignorePath` set to `.gitignore` and `src/.gitignore` allowed, the pack ignores `src/Generated/` for you).

## Keeping the generated folder out of version control

The `gitignore` unit (mode `block`, `createFile: true`) writes nothing until the `gitignorePath` parameter names an ignore file,
relative to the pack output like the other folders. It then keeps one managed block in that file, between
`# maquettiste: begin csharp-dapper/gitignore` and `# maquettiste: end csharp-dapper/gitignore`, and never touches the file's other
lines; the file is created (holding just the block) when it does not exist. The block holds the generated folder relative to the
file's folder: `/Generated/` for `generatedFolder` `Generated` and `gitignorePath` `.gitignore`. When the generated folder is the
file's own folder, it holds `/*` and `!/<file name>` (everything but the file itself); when the generated folder is not at or
under the file's folder, the unit writes nothing. The file's path must be allowed: add it to `outputs.allow` as a file entry, so
only that file becomes writable. The reference application does this:

```json
"outputs": { "allow": [ { "path": "db" }, { "path": "src/ReferenceApp.Data/Generated" }, { "path": "src/ReferenceApp.Data/.gitignore" } ] },
"packs": { "csharp-dapper": { "output": "src/ReferenceApp.Data",
  "parameters": { "generatedFolder": "Generated", "partialFolder": "Generated", "gitignorePath": ".gitignore", "namespace": "ReferenceApp.Data" } } }
```

Clearing `gitignorePath`, or removing the unit, removes the block again (and the file, when the engine created it and nothing
else is left). Nothing here needs git: the file is plain text for whatever tool reads it.

## Repositories

A repository is generated for every concrete entity with a mapping in the chosen database (the `database` parameter, else the
first database by name that maps the entity) whose columns live in the entity's own table. It takes an `IDbConnection` (and an
optional `IDbTransaction`) and offers:

| Method | SQL |
| --- | --- |
| `GetAsync(key…)` | `SELECT … WHERE <primary key>` |
| `ListAsync(skip, take)` | `SELECT … ORDER BY <primary key>` with `LIMIT/OFFSET` or `OFFSET … FETCH` |
| `InsertAsync(entity)` | `INSERT`, returning a database-generated key (`RETURNING` or `OUTPUT INSERTED`) into the entity; `uuid-v7` keys are filled with `Guid.CreateVersion7()` and `ulid` keys with `UlidGenerator.NewUlid()` when empty |
| `UpdateAsync(entity)` | `UPDATE … SET <every column but the key, generated and immutable ones> WHERE <primary key>` |
| `DeleteAsync(key…)` | `DELETE … WHERE <primary key>` |

Table, column and key names come from the resolved model and are quoted for the database's dialect. Dapper maps a private `Row`
class with one property per column (aliased in the `SELECT`), and `Row` converts to and from the entity: embedded value objects
are rebuilt from their prefixed columns, enums stored as text go through `ToCode`/`Parse`, enums stored as integers or lookup
keys are cast, and JSON columns are (de)serialized with `System.Text.Json`. A table-per-hierarchy discriminator is written on
insert and filtered on read.

Range and multirange columns (a column-only type: an overlay's extra column, so an entity property of its own) are held
as their text form, a `string` such as `[1,10)` or `{[1,3),[5,8)}`, as JSON is. On PostgreSQL the repository reads
`<column>::text` and writes `CAST(@<property> AS <native type>)`, such as `CAST(@Seats AS int4range)`; elsewhere the
column is text already. A bound repository and a query class take their SQL from the engine (`binding_sql`,
`query_sql`), which adds no casts: a binding or query over a range column needs them written by hand for now, until the
engine's statements cast range columns (a follow-up, as typed C# ranges are).

Not covered, by design of an example pack: navigations and junction tables (write queries for them in the companion or in your
own repository class, which is `partial`), collections stored in child tables (their property is left empty), and table-per-type
hierarchies, whose rows span tables (no repository is generated for them).

### Repositories from bindings

An entity with a binding (its `bindings` in the model, one per database) for the chosen database gets its repository from the
binding instead: the chosen database is the `database` parameter, else the first database by name that binds or maps the entity.
Its statements come from the engine (`binding_sql`), so the binding decides everything: the source it reads (a table, a view, or
a query wrapped as a derived table), the constants (a filter on every read, a value on every insert, which is how several entities
share one table through an `entity_type` column), the columns the database fills (never written), and the delete.

| Method | SQL |
| --- | --- |
| `GetAsync(query parameters…, key…)` | the binding's `select-by-key` |
| `ListAsync(query parameters…, mq_skip, mq_take)` | the binding's `select`, ordered by the key fields, with `LIMIT/OFFSET` or `OFFSET … FETCH` |
| `InsertAsync(entity)` | the binding's `insert`: fields and constants, one database-generated key read back into the entity; `uuid-v7` and `ulid` keys filled as above |
| `UpdateAsync(entity)` | the binding's `update`, by key with the constants in the where clause |
| `DeleteAsync(key…)` | the binding's `delete`: by key, or the soft-delete update |

A binding that does not write (a view or query source, or `write: "none"`) gets a read-only repository: no `InsertAsync`,
`UpdateAsync` or `DeleteAsync`. The private `Row` class has one property per field, named exactly as the field, because the
select's aliases and the statements' parameters carry the field names. A field that holds a relation's key becomes a property of
the entity class. Entities without bindings generate exactly as before.

## Queries

A query element (a query over a database's tables and views, written as data) becomes one class in `Queries/`, in the
namespace `<namespace>.Queries`: an interface `I<Name>Query` and a class `<Name>Query` over an `IDbConnection` (and an optional
`IDbTransaction`) with one method. `<Name>` is the query's name Pascal-cased, with `Q` in front when it would start with a
digit (the `cs_class` helper; the engine reports two queries of a database that end up with one class name, MQ4040).

```csharp
Task<IReadOnlyList<InvoicesByCustomerResult>> ExecuteAsync(Guid customerId, IEnumerable<string> statuses, int offset = 0, int limit = 50,
    CancellationToken mq_cancellationToken = default);
```

Its parameters are the query's, typed through `types/csharp.json` (a list parameter is an `IEnumerable<T>`), the ones with a
constant default last (a string, bool, integer, double, `decimal` with `m` or `float` with `f`). The class's own names, its
constructor's `mq_connection` and `mq_transaction`, the method's `mq_cancellationToken` and its locals, start with `mq_`,
which the engine reserves (MQ4040), so a parameter, a field or a collection may take any other name. Entity, enum and value
object types are written `global::`-qualified, so no type of the `Queries` namespace hides them, and `System.Text.Json` is
imported when a field is `json` (a `JsonElement`). The SQL is what the engine renders for the query's database (`query_sql`), with `@name` placeholders and
`IN @name` for a list, which Dapper expands. A private `Row` class has one property per select field, named as the field (Dapper
matches column aliases without regard to case), and converts to the result:

- With an `entity`, each row becomes that entity: a field that names an attribute fills its property (an enum stored as text goes
  through `<Enum>Codes.Parse`, an enum stored as a number is cast, a number of another number type than the attribute's is
  cast to it); a field that names a value object member (`<attribute id>.<member id>`) fills it, the members of one attribute
  building the value together (an optional one is null when all of its fields are); a field that names the relation end of a
  to-one navigation fills the entity's foreign key property (`CustomerId`); a field without an attribute is read but not
  copied. The rows are read-only projections, as the class's remarks say: an entity holds what the query selects and leaves
  its other properties at their defaults, so it is not a row to save back through the entity's repository.
- Without one, the method returns `<Name>Row`, a `sealed partial record` with one property per field (`pascal` of its
  name), typed by the field's type and nullability. A field that reads a column of a table file typed by an enum (the
  column's `typeRef`) is that enum, nullable as the field, converted as an entity's enum attribute is (a stored code
  through `<Enum>Codes.Parse`, a stored number cast). A field that reads a column typed by a reference type keeps the
  code's type, and the record's summary says so with a cref to the type's record ("Status holds a code of
  `<see cref="global::Billing.InvoiceState"/>`."). The same goes for a generated `<Name><Collection>Item` record.
- With collections, the method returns `<Name>Result(Item, <Collection>…)`: after the parent rows, each collection's statement
  (`query_collection_sql`) runs with the parent rows' distinct key values (`mq_keys0`), at most `MaxKeysPerStatement` (1000)
  of them per run, so a large parent set stays under the databases' parameter limits; the runs go in the parents' order and
  their rows are merged, grouped by their `mq_key0` and attached to the parent whose key matches. With several keys each run
  keeps only the rows whose whole key is one of its own (the `IN` lists over-fetch). An element is the collection's `entity`,
  or a generated `<Name><Collection>Item` record. The collections are on the result record, never on the entity.

A template that binds parameters by position (`$1`, `$2`...) gets their names in placeholder order from
`query_sql_parameters query` (or a collection), with the same dialect and options arguments as `query_sql`.

`QueryRegistrations.All` lists the `(Service, Implementation)` pairs of every query class, as the repositories' registration
files do.

## Registration

`BillingRepositories.All` lists `(Service, Implementation)` pairs and `Register` walks them, so any container works:

```csharp
DapperTypeHandlers.Register();
services.AddScoped<IDbConnection>(_ => new NpgsqlConnection(connectionString));
BillingRepositories.Register((service, implementation) => services.AddScoped(service, implementation));
```

`DapperTypeHandlers.Register()` teaches Dapper to read `DateOnly`, `TimeOnly`, and (for SQLite, which stores them as text)
`Guid` and `DateTimeOffset`. Call it once at startup.

## Reference types

An attribute typed by a reference type holds the row's code: `string` (or the code's integer type, or `Guid` for a uuid code), `IReadOnlyList<string>` for a
collection. For reading the rows in code, each type gets a record and a static class of its rows, in seed order:

```csharp
public sealed record UnitOfMeasure(string Code, string Label, decimal Factor, string? Symbol);

public static partial class UnitOfMeasures
{
    public static readonly UnitOfMeasure Kg = new("kg", "Kilogram", 1000m, "kg");
    // ...
    public static IReadOnlyList<UnitOfMeasure> All { get; } = [Kg, G, Pinch];
    public static UnitOfMeasure? Find(string code) => code switch { "kg" => Kg, /* ... */ _ => null };
}
```

Row fields are the codes in PascalCase (`Code` in front when that does not start with a letter). Each former code of the
type (0.12.0: `formerCodes`, which the reference-type refactors write) gets an `[Obsolete]` field named as its row was,
holding the row that replaced it, so code that names the old constant still compiles and reads the new row:

```csharp
    /// <summary>The former code kg, now the row kilogram.</summary>
    [Obsolete("kg is a former code of UnitOfMeasure; use Kilogram.")]
    public static readonly UnitOfMeasure Kg = Kilogram;
```

A former code whose field name a live row's field already has gets none: `net30` and `net-30` both give `Net30`. `Find`
and `All` know the live codes only. With locales declared in
`localization`, one `.resx` per locale holds `<Type>_DisplayName`, `<Type>_PluralName`, `<Type>_Description` and `<Type>_<Row>`
(the row label) through the locale's fallback chain; the default locale's file is the neutral `ReferenceData.resx` and the others
are satellites the SDK compiles into `fr/…resources.dll`.

## Operations

An operation is one command over the model (see the user guide's Operations section). For each operation, the
`operation-contracts` unit writes `Operations/<package folder>/<Op>.g.cs`, where `<Op>` is the operation's Pascal name
followed by its subject's (`SendInvoice` for `send` on Invoice), or the Pascal name alone without a subject. The file
holds plain types in the package's namespace, with no dependency on a web framework:

- `<Op>Request`, a positional record of the input members (absent without input). A member's C# type comes from the type
  map, nullable when not required and `IReadOnlyList<T>` for a collection; a member that holds a record key takes its
  key attribute's type.
- `<Op><Outcome>`, a record of the members an outcome returns inline. An outcome that returns records of an entity uses
  the entity's class instead, and a file a `System.IO.Stream` (the type map's `file` entry, which a file input member
  takes too); a list is `IReadOnlyList<T>`.
- `<Op><Outcome>Page`, for a paged list: `Items` and `Next` (a cursor, null on the last page) or `Page` (from 1), with
  the constant `MaxPageSize`.
- `<Op>Errors`, one constant per error, own and inherited from the operation's stereotypes: the Pascal name of the
  error, holding its code (the name when it has none). Absent without errors.
- `<Op>Result`, an abstract record with one nested record per outcome (`Value` holds what the outcome returns; an
  outcome that returns nothing has no `Value`) and `Failed(string Code, string? Input)`, where `Input` names the input
  member the error points at.
- `I<Op>Handler`, whose `HandleAsync` takes the subject's key at record level (one parameter per key attribute), the
  request, the paging inputs a paged list implies (`cursor` or `page`, then `pageSize`), and a cancellation token. Its
  summary names the process events the operation fires.

For `send` on Invoice:

```csharp
public sealed record SendInvoiceRequest(string Recipient);
public sealed record SendInvoiceWaiting(string? TrackingId);
public static class SendInvoiceErrors { public const string AlreadySent = "already_sent"; /* ... */ }
public abstract record SendInvoiceResult
{
    public sealed record Applied(Invoice Value) : SendInvoiceResult;
    public sealed record Waiting(SendInvoiceWaiting Value) : SendInvoiceResult;
    public sealed record Failed(string Code, string? Input) : SendInvoiceResult;
}
/// <summary>send on Invoice; fires InvoiceLifecycle.sent.</summary>
public interface ISendInvoiceHandler
{
    Task<SendInvoiceResult> HandleAsync(Guid id, SendInvoiceRequest request, CancellationToken cancellationToken);
}
```

The pack writes no implementation: you implement the handler. When `endpointsFolder` names a folder, the
`operation-endpoints` unit writes `<endpointsFolder>/Operations/<package folder>/<Op>Endpoint.g.cs`, a static class
with `Map<Op>Endpoint` (ASP.NET Core minimal APIs, so the project references the `Microsoft.AspNetCore.App` framework)
and one route that resolves `I<Op>Handler` from the service container. The file is overwritten on every run; it has no
regions. The HTTP mapping is this pack's own convention:

- The route is `/<kebab plural subject>`, then one segment per key attribute at record level (`/{id}`), then
  `/<kebab name>`; without a subject, `/operations/<kebab name>`. `send` on Invoice is `POST /invoices/{id}/send`.
- GET for a read without input, else POST. A JSON body binds the request; with a file input the request is a multipart
  form: each file is a part named after its member, and the other members are one JSON part named `request`.
- An outcome that completes now answers 200 with its value (a file is streamed with its first media type) or 204
  without one; an outcome that completes later answers 202.
- An error answers with problem details (RFC 9457) whose title and `code` are the error code and whose `input` names the
  input member: status 422 when the error points at an input, 409 otherwise, unless the error has an integer property
  `httpStatus` (stereotype parameters can give one to every error a stereotype contributes).
- Paging inputs come from the query string (`cursor` or `page`, and `pageSize`); `pageSize` is capped at the smallest
  `maxPageSize`.

## Processes

Every process of the model gets the units below (phase-3-design.md section 7.2), under `Processes/<Process>/` in the
`generatedFolder` and, for companions, the `partialFolder`; the namespace is the process's package namespace
(`<namespace>.<Package>`), so companions reach the entities and repositories of the package without a `using`. The model-level
units write the dispatcher (`<namespace>.Dispatch`), the interpreter (`<namespace>.Runtime`) and the actors
(`<namespace>.Processes`); they are `model` units whose templates write their files through file blocks only when the model has
a process, so a model without processes gets none of them and skipping one process (`generation.skip`) leaves the others'
runtime in place. The two model-wide companions (`Pipeline.cs`, `ProcessHost.cs`) are `once` units of their own
(`dispatch-companion`, `interpreter-companion`): a model-scope `pair` would always write its files, while a `once` file block is
written only when missing and never overwritten or deleted, as a pair's companion is. Nothing is created for storage: the store
is an interface until the companion says where instances live.

For `PurchaseApproval` in package `Purchasing`:

| Unit | Mode | For | Writes |
| --- | --- | --- | --- |
| `process-states` | `overwrite` | `each process` | `Processes/PurchaseApproval/PurchaseApprovalStates.cs`: a constant per state path; an enum with one member per path for an orchestration, `ToStatus` (active states to the bound enum) for a lifecycle with one enum binding, and `To<Entity>Status` per enum binding for a lifecycle with several subjects (qualified by package where two subjects share a name); with state codes, also the `<Process>StateCodes` class (see Lifecycle state codes) |
| `process-definition` | `overwrite` | `each process` | `PurchaseApprovalDefinition.cs`: the chart as static data (states with parents, initial children and history; transitions in priority order with triggers, guards, actions, delays and gates; events with their actors; invokes; gates and meanings) |
| `process-contracts` | `overwrite` | `each process` | `PurchaseApprovalContracts.cs`: the context record, one command per event (`PurchaseApproval<Event>Command`: the envelope plus the typed payload, and the gate's audit attributes for a gated event), the built-in `PurchaseApprovalStartControl`, `PurchaseApprovalInvokeResultControl` and `PurchaseApprovalTimersDueControl` (their names end in `Control`, every event command's in `Command`, so no event name collides with them; all implement `IProcessCommand`), `PurchaseApprovalTransitioned` and one audit record per gate (the fields of `gate.audit`) |
| `process-handlers` | `pair` | `each process` | `PurchaseApprovalHandlers.g.cs` (guards and actions as `Guard<Name>` and `Action<Name>`, so a guard and an action may share a name: translated ones implemented, the others declared as partial methods; the command handler that loads, runs the machine, saves over the version it loaded and returns the outbox events) and `PurchaseApprovalHandlers.cs` once, with a stub per untranslated guard or action. A process that names actors taken from the object also implements `IActorResolver`: `Is<Actor>` per such actor, declared as a partial method whose stub refuses until the team writes it |
| `process-services` | `pair` | `each process` | `PurchaseApprovalServices.g.cs` (an interface per service task, an optional hook per human task and sub-process, `RunAsync` routing a started invoke) and `PurchaseApprovalServices.cs` once |
| `process-machine` | `pair` | `each process` | `PurchaseApprovalMachine.g.cs` (`Start`, `Restore`, one method per event, `Complete`, `Tick`, `Publish`) and `PurchaseApprovalMachine.cs` once |
| `process-store` | `pair` | `each process` | `PurchaseApprovalStore.g.cs` (`IPurchaseApprovalStore`: load the snapshot, save it over the version the command loaded, append history and audit records) and `PurchaseApprovalStore.cs` once, an in-memory store to adapt |
| `process-endpoints` | `regions` | `each process` | `<endpointsFolder>/Processes/PurchaseApproval/Endpoints/PurchaseApprovalEndpoints.cs`: `POST /processes/purchase-approval/{instance}/<event>` per event (ASP.NET Core minimal APIs), with a user-code region per endpoint keyed by the event's id; only when `endpointsFolder` is set |
| `dispatch` | `overwrite`, file block | `model` | `Dispatch/Dispatch.g.cs`: handler and behaviour contracts, `CommandResult`, the registration type, the outbox and transaction interfaces, `Dispatcher` |
| `dispatch-companion` | `once`, file block | `model` | `Dispatch/Pipeline.cs` (in the `partialFolder`): the behaviour order and the policy hooks |
| `dispatch-registry` | `overwrite`, file block | `model` | `Dispatch/HandlerRegistry.g.cs`: a typed list of registrations and the command descriptors, and `Register(IHandlerRegistrar)` handing a container one typed factory per handler, without reflection |
| `dispatch-behaviours` | `overwrite`, file block | `model` | `Dispatch/Behaviours.g.cs`: validation, authorization, logging, transaction and outbox behaviours and their hooks |
| `interpreter` | `overwrite`, file block | `model` | `Runtime/Statechart.g.cs`: the interpreter |
| `interpreter-companion` | `once`, file block | `model` | `Runtime/ProcessHost.cs` (in the `partialFolder`): in-memory clock, timers and invokes |
| `actors` | `overwrite`, file block | `model` | `Processes/Actors.cs`: a constant per actor, its type, and each event's allowed actors. `ActorType` gains `Group`, `Organization` and `System` only for the types the model uses; when an actor is a member of another, `MemberOf`, `Accepts` and `Allows` are written and the interpreter's and dispatcher's actor checks follow membership. An actor taken from the object says how it is worked out ("the object's owner") |
| `rights` | `overwrite`, file block | `model` | `Processes/Rights.cs`, only when a lifecycle states rights or a default: the rights check (see Rights) |
| `right-conditions` | `overwrite`, file block | `model` | `<Package>/RightConditions.g.cs`: one static partial method per named right with a condition and a use, however many lifecycles use it |
| `right-conditions-companion` | `once`, file block | `model` | `<Package>/RightConditions.cs` (in the `partialFolder`): a stub per condition, which throws until written |
| `freezing` | `overwrite`, file block | `model` | `Processes/Freezing.cs`, only when the project uses freezing: the frozen check and its host interface, `IFrozenState` (see Freezing and attribute rights) |
| `frozen-state` | `once`, file block | `model` | `Dispatch/FrozenState.cs` (in the `partialFolder`), only when the project uses freezing: the team's `IFrozenState`, which answers "not frozen" until written (see Freezing and attribute rights) |
| `caller-holdings` | `once`, file block | `model` | `Dispatch/CallerHoldings.cs` (in the `partialFolder`), only when the project uses rights: the team's `ICallerHoldings`, which returns no held actors until written (see Rights) |
| `scenario-holdings` | `overwrite`, file block | `model` | `<testsFolder>/ScenarioHoldings.cs`, only when the project uses rights and `testsFolder` is set: the held actors of each step's participant |
| `scenario-frozen` | `overwrite`, file block | `model` | `<testsFolder>/ScenarioFrozen.cs`, only when the project uses freezing and `testsFolder` is set: whether each step's subject, and a link of it, are frozen before the step |
| `scenario-tests` | file blocks | `each scenario` | `<testsFolder>/PurchaseApproval/HappyPathTests.cs`: one xunit test per scenario; only when `testsFolder` is set. Each step acts as the actor the engine worked out (`step.acts_as`), and an event step by a participant signs with the participant's identity unless it names a signer |

### Lifecycle state codes

A state with a `code` gets a constant in `<Process>StateCodes`, in the same file as the state paths. It is named by the state, or
by its path where two coded states share a name. Its type is `string` or `long`, by the kind of the codes. A model with no codes
gets none of this, and the files are the same as before.

When the codes form a map (no two coded states on one path, none inside a parallel region), the class also has:

- `ToCode(IReadOnlyList<string> states)`: the code of a configuration, or `null` for a state with no code.
- `FromCode(code)`: the configuration the code restores, as an `IReadOnlyList<string>` of leaf state paths in document order, ready
  for a snapshot's `States`. A compound state enters its initial state and a parallel state each of its regions, as the chart
  does, so a code on a simple state gives one path and a code whose default enters a parallel state gives one per region. A coded
  state whose default enters a choice state throws, since the chart picks its states when it runs.
- `Moves`: every transition as (from code, to code, trigger, event).
  - From a state with no code on its path, there is one move per coded state inside it, plus one with a `null` "from" code where a
    state without a code can rest inside it.
  - A move into a history state takes the code of the nearest coded state that holds it, since everything it restores sits inside
    that state; with no coded state around it, and for a move into a choice state, the "to" code is `null`, since it is decided
    when the move runs.
  - A move without a target, or inside one coded state, has the same code at both ends. A timer, a completion or an eventless
    move has no event.

A lifecycle with several subjects keeps the per-state enum and `Parse`, and gets `To<Entity>Status` for each enum binding. Each
names its enum in full (`global::<namespace>.<Enum>`), so two bound enums with one name, or an enum named like a type of the
lifecycle's own namespace, return the right type. A lifecycle with one subject, in either form, writes the same `ToStatus` as
before.

### The pair and regions shapes

Guards, actions, services and storage are hand-owned logic, so their units are pairs, as entities are: the `.g.cs` half is
regenerated on every run, the `.cs` companion is written once and never touched again. The generated half declares what the
companion must supply (a partial method per untranslated guard or action, the service interfaces the class implements, the
store interface), so a guard, action or service added to the model fails the build until its companion code is written. Keep
the companions in a folder you commit (`partialFolder`, see The pair pattern). Endpoints are one file per process whose set of
endpoints follows the events: each has a region between `// maquettiste:keep id=<event id>` (the event's model id, so renaming
the event keeps the region's body) and the end marker, just before the
command is sent, where the request can be mapped further (`command = command with { ... }`) and the response reshaped
(`respond = result => ...`); the rest of the file is regenerated and region bodies carry over. Region bodies live only in
the file, so keep it in a folder you commit, hence `endpointsFolder`.

### Dispatch

Commands go through `Dispatcher.SendAsync(command)`: the dispatcher finds the command's typed registration in
`HandlerRegistry.All` (pattern matching on `HandlerRegistration<TCommand>`, no reflection), runs the pipeline's behaviours
outermost first, then the process's command handler, which loads the instance through `I<P>Store`, runs the machine, saves, and
returns a `CommandResult` (accepted or refused with the reason, the active states, the changed context attributes, the context,
whether the instance is final, and the `<P>Transitioned` and gate audit records). The default pipeline, ordered in `Pipeline.cs`:

| Behaviour | Does | Hook in `Pipeline.cs` |
| --- | --- | --- |
| `ValidationBehaviour` | refuses (`validation`, with `Errors`) a payload that breaks its attributes' rules: required, length, range, allowed values | `OnValidate` (optional) adds the project's own rules |
| `AuthorizationBehaviour` | refuses (`actor`) an envelope actor the event does not allow and a caller the hook rejects; gates are the interpreter's, so a gated event without a signer is refused there (`gate-signer`) with its audit record, as in the engine | `Authorize` (required): maps the host's principal to the envelope's actor; the stub throws until implemented |
| `LoggingBehaviour` | logs the command, its payload without the attributes marked `sensitive`, and its outcome | `OnLog` (optional): nothing is logged without it |
| `TransactionBehaviour` | runs the rest in a unit of work and commits it | `BeginTransactionAsync` (required): none by default |
| `OutboxBehaviour` | writes the result's records to the `IProcessOutbox` given to `Pipeline`, inside the transaction | the outbox (`InMemoryProcessOutbox` in memory) |

A refusal from a behaviour reports the instance's current states like a refusal from the interpreter does. Cross-process
delivery (queues, change capture) stays outside: the contracts are plain records, and the outbox is where generated code stops.
`HandlerRegistry.Register(registrar)` hands a container (through `IHandlerRegistrar.Add<THandler>(Func<IServiceProvider, THandler>)`)
one factory per command handler, which builds it from the machine and the store the container holds; the registry's only type
token is the `typeof(T)` an `IServiceProvider` lookup needs. `CommandResult` also carries the gate audit records (`Audit`) and a
`Failure` when the input stopped short (below).

### The interpreter

`Runtime/Statechart.g.cs` is the interpreter of phase-3-design.md section 4.1 in C#: configuration, macrosteps with eventless
transitions and raised events, selection by active leaf in document order with conflict removal, exit and entry sets, shallow
and deep history, `done` for compound and parallel states, timers, service and human tasks, gates with their audit records, and
actor checks, with the same refusal reasons as the engine (`no-transition`, `guard`, `actor`, `gate-signer`, `gate-repeat`,
`gate-reason`). When the model has actors taken from the object (the order's owner, an approver on its project), the generated code
can't read the object: the interpreter accepts such an actor only when the process's Handlers, as `IActorResolver<TContext>`,
says the caller is that actor for the instance, and refuses it otherwise (actors-design.md decision 27). Until the team
implements those `Is<Actor>` methods, the generated scenario tests whose steps act as an actor taken from the object fail: the
stub refuses, so the step is refused by actor where the engine's replay accepts it. A human task's completion or failure carries
no caller identity in its envelope, so for a task whose actors are taken from the object the resolver must judge the host's own
principal (the user the host runs the completion for). Audit records hold the actor's and the meaning's ids, as the engine's do (the envelope carries names). A transition
naming a guard the process does not declare never fires, as in the engine. A macrostep over the microstep bound (1,000 by
default) does not throw: the step result's `Failure` says so and the instance keeps the state it reached, as the engine reports
MQ9507. It keeps nothing between calls: `ProcessSnapshot<TContext>` (active leaf paths, history, timers, pending invokes,
signatures, sequences, context, the instance's clock and the stored version) is what the store persists. The instance's clock is
set by the start and moves only on time inputs (to the host clock's instant at a timers-due command); events are stamped with it,
as in the engine, and delays count in ticks, so a duration below a millisecond is kept. `SaveAsync` takes the version the command
loaded and the store refuses (`ProcessConcurrencyException`) a save over another version, so two commands on one instance cannot
both win (the in-memory companion checks it; a project's store keeps the check, for example over a version held in the stored
snapshot). Its extension points are interfaces that `ProcessHost.cs`
implements in memory: `IProcessClock` (the clock; `ManualClock` moves only when told, for tests), `ITimerScheduler` (told when a
timer is scheduled or cancelled; a due timer comes back as a `<P>TimersDueControl`, which fires every timer due at the clock's
instant in due order) and `IInvokeHost` (told when an invoke starts or is cancelled; the result comes back as a
`<P>InvokeResultControl`, and `<P>Services.RunAsync` runs a service task and returns that command). A sub-process invoke is
started through the invoke host and reports back like a service task (the engine runs it in the same interpreter).

### Guard and action expressions

A guard written in the expression language (its `condition`) is rendered by `expr_cs_holds`, `expr_cs` compared with `true` (see
Checks and the expression renderer): it reads `total` as `call.Context.Total` and `event.amount` as
`call.Event.Get<decimal?>("amount")`, a missing payload member reading as null, and holds only when the condition is true, as in
the engine; a condition that is always null is `false`. Anything `expr_cs` does not render becomes a stub.

For JavaScript guards and actions, `helpers.js` translates the documented subset of the model's JavaScript expressions to C#
(`cs_expression`); anything else becomes a stub in the handlers companion, with the expression as a comment:

| JavaScript | C# |
| --- | --- |
| numbers, `'text'` or `"text"`, `true`, `false`, `null` | the same literal; a fraction takes the other operand's suffix (`2.5m` next to a `decimal`) |
| `context.x` (a context attribute) | `call.Context.X` |
| `event.payload.x` (a payload attribute of an event of the process) | `call.Event.Get<T>("x")` |
| `event.name`, `event.actor` | `call.Event.Name`, `call.Event.Actor` |
| `==`, `===`, `!=`, `!==` | `==`, `!=`, between operands of one kind (numbers, strings, booleans, one enum); an enum attribute compared with a member name compares with the member; a comparison with `null` only of an operand that may be null |
| `<`, `<=`, `>`, `>=` | the same, between numbers that cannot be null |
| `&&`, `\|\|`, `!` | the same (a nullable boolean reads as `== true`) |
| `+`, `-`, `*`, `%`, unary `-`, `? :`, parentheses | the same, on numbers that cannot be null (`+` also joins two strings) |
| `/` | the same, when both operands are `decimal`, or both `double`, or one of them is and the other a number literal |
| an action `({ x: expression, ... })` | `call.Context with { X = expression, ... }`, when the value fits the attribute (a member name for an enum, no fraction for an integer, no null for an attribute that cannot be null) |

Everything else becomes a stub, because C# would compute it otherwise than the engine's JavaScript: a division of integers (C#
drops the fraction), ordering or arithmetic with an operand that may be null (JavaScript reads `null` as 0: `null >= 0` holds),
ordering strings or mixing kinds (`context.name < "b"`, `"1" == 1`), `decimal` mixed with `double` or `float`, and a `null` test of
an attribute that cannot be null (always false in JavaScript, a warning in C#). Function calls, `Math`, arrays, template strings,
`?.`, `??`, assignments and names other than the above are outside the subset too. A guard receives `StatechartCall<TContext>` (`Context`, `Event`, `States`, `IsActive(path)`, `Instance`) and returns a
boolean; an action returns the new context.

### Rights

A project uses rights when a lifecycle states `rights` or `rightsDefault` (rights-design.md, part 1). Only then does the pack write
anything below; a project without rights gets the same bytes as before. A scenario that only expects a refusal such as `guard`
writes nothing new.

- **`Processes/Rights.cs`**: `RightAccess`, `RightDefinition` (id, name, access, actors, except, verbs, ends, condition and description),
  `RightUse` (a right and the states where it holds), `ProcessRights` (what a process's definition states: whether it is a lifecycle, its
  default, its rights, the entities it governs, its one subject, and the ids a refusal names), `RightRequest`, `RightDecision` and
  `Rights.Check`, with a `Can<Verb>` method for each verb a right names. Each answering lifecycle answers on its own: its denies, then its
  allows (a closed list the caller passed counts as one), then its default; any "no" wins. The first check that fails names the refusal:
  denies (the acting lifecycle's first, in file order, then the others by name), then defaults. For an object, link or own verb, every
  other lifecycle that governs `Subject` answers too, in the states `States` gives (a missing lifecycle reads as its initial state, and
  every lifecycle does for `create`); with no `Subject`, the lifecycles `States` names answer.
- **`RightRequest`** has init properties, so later versions add members without breaking a call: `Verb`, `Lifecycle` (the acting
  lifecycle, none outside a move), `Subject` (the entity's type), `Actor` (the actor the caller acts as), `Envelope`, `Held`, `States`,
  `End` (a relation end, for a link) and `Allowed` (the closed lists the caller passed), and `Resolves` when the model has actors taken
  from the object. A team's own call, such as from an edit endpoint or an operation's handler, fills it itself; generated operation code
  doesn't check rights.
- **Process definitions** pass their `ProcessRights` to `ChartDefinition` through an optional parameter.
- **Conditions** are static partial methods that take the request, which the team writes: a named right's in `RightConditions` in its
  package's namespace, once however many lifecycles use it; an inline right's in `<Lifecycle>RightConditions`, beside its lifecycle's
  handlers. A new one fails the build until written. Actors taken from the object, in a right's `actors` or `except`, are answered by the
  acting process's resolver (`IActorResolver`), which also answers for those another answering lifecycle's rights name.
- **What the caller holds** comes from `ICallerHoldings` (in `Dispatch.g.cs`), never from the request body or the command. Each command
  handler asks it once per command and passes the list to the interpreter. The team's stub, `Dispatch/CallerHoldings.cs`, is written once
  and returns none: the caller is then checked only as the actor it acts as, so a deny on a role it holds but isn't acting as doesn't reach
  it, and neither does an allow. The registry passes the container's `ICallerHoldings` to each handler, else the stub.
- **Other lifecycles' states:** when another lifecycle could say no to a verb an event names (`process.may_refuse`), the process's
  handlers implement `ILifecycleStates` and declare `StatesIn<Lifecycle>(call)` in the team's half: the object's active states there, none
  for its initial state, and null when that lifecycle doesn't govern the object (a process with several subjects), so it isn't asked. It
  throws until written. A process whose events reach no such lifecycle gets none.
- **The interpreter** checks events, human tasks (as `complete`) and signatures the way the engine does: the closed list (`actor`), then
  rights and defaults on the event's verb before any guard (`right`, `default`; a gate's signers count as an allow for the gated event),
  then the guards, then at a gate the signers, the lifecycle's denies on `sign`, a repeat signer and a reason. `Refusals` gains `Right` and
  `Default`, and `StepResult` and `CommandResult` gain `RefusedBy`: the event or invoke, the right, the lifecycle whose default denies, the
  guard or the gate.
- **Scenario tests:** `ScenarioHoldings` answers each command with the held actors of the step's participant (its actor and its holdings
  with no context). Each generated test sets `ScenarioHoldings.Current` before a send and clears it after. `ScenarioHost` is your file, so
  register it there once, in place of the stub: pass `new ScenarioHoldings()` as each command handler's last argument (or register it as
  the container's `ICallerHoldings`). Until then, a step that depends on a held actor (its participant holds one besides the actor the
  step acts as) fails with a message that says so; other steps don't ask. A refused step
  asserts its expected `refusal` and `refusedBy`. A verb step calls `Rights.Check` with the subject object's entity and states, and the
  actors from the object its participant satisfies in the engine's replay (`step.trace.object_actors`) as the resolver, and asserts its answer; without rights it is written as a comment, since nothing in the model can refuse it.

### Freezing and attribute rights

A project uses freezing when an event, a transition or a right names `freeze` or `thaw`, or a right has `whileFrozen` (rights-design.md,
part 2). A project uses attribute rights when a right names `attributes`. Only then does the pack write what follows; a project that uses
rights without them gets the same bytes as before.

- **`Processes/Freezing.cs`**, only when the project uses freezing: `FrozenQuery` (the process that asks, the subject's entity, the
  envelope, and whether it asks about a link and through which end), `IFrozenState` and `FrozenFlags`. A frozen object can't be modified or
  deleted, and its links can't be connected or disconnected; a frozen link can't be disconnected or modified. This is refused before any
  right, with the refusal `frozen` and no `RefusedBy`, and no allow lifts it. Freezing stops nothing else.
- **The frozen state.** Where "frozen" is held is your system's business, so generated code asks one host interface, `IFrozenState`
  (`bool IsFrozen(FrozenQuery query)`), as it asks `ICallerHoldings` for held actors. The pack writes your stub of it once,
  `Dispatch/FrozenState.cs` in the `partialFolder`, and never touches it again. As written it answers "not frozen": the project builds,
  and nothing is refused for freezing until you write it. So a project whose events already name `freeze` keeps building, and starts
  refusing once your `FrozenState` answers. Each command handler takes an `IFrozenState` (the dispatch registry passes the container's),
  with your `FrozenState` when none is given. It may be asked more than once for one command.
- **The interpreter** asks it for an event whose verb is `modify`, `connect`, `disconnect` or `delete`, before the actor check, and refuses
  with `Refusals.Frozen`. An accepted `freeze` or `thaw` event changes nothing in generated code: your handler or your system records it.
- **`Rights.Check`** answers freezing first. `RightRequest` gains `Frozen`, `LinkFrozen` and `FrozenState`: a team's own call that knows
  sets the flags, and a flag left null asks `FrozenState` (your `FrozenState` when that is null too). A right with `whileFrozen` holds only while the object (with `ends`, the link) is frozen, and
  `RightDefinition` gains `WhileFrozen` when a right has it.
- **Attribute rights:** `RightDefinition` gains `Attributes` and `RightRequest` gains `Attributes`, the names of the attributes the verb
  reads or modifies. Each is checked on its own, against the rights that name it and those that name none, and one refused attribute
  refuses the verb. When the request gives none, as for an event that names `modify`, it fails closed: a deny that names attributes holds,
  and an allow that names attributes grants nothing. When several denies hold, the first in file order that holds for any attribute
  names the refusal, as the engine does.
- **Scenario tests** work out whether the subject, and each of its links, is frozen before each step from the scenario: its `frozen`
  flags, then each accepted `freeze` or `thaw`, from a verb step or an event that names the verb. A verb step passes them to
  `Rights.Check` as `Frozen` and `LinkFrozen`, with the attributes it reads or the values it sets as `Attributes`. Without rights, a verb
  step checks `FrozenFlags.Refuses`. In a scenario that freezes, each send sets `ScenarioFrozen.Current` from those flags first, so an
  event the replay refused as `frozen` is refused by the dispatcher. `Tests/ScenarioFrozen.cs` (unit `scenario-frozen`, only when the
  project uses freezing and `testsFolder` names a folder) is the tests' `IFrozenState`: register it once in your `ScenarioHost`, in place of
  the `FrozenState` stub, as you do `ScenarioHoldings`. A step refused as frozen fails until then, and its message says so.

### Scenario tests

With `testsFolder` set, each scenario becomes one xunit test: it starts the instance with the scenario's start context on a
`ManualClock` at `start.at`, sends every step through the dispatcher (an event command with its envelope and payload, an
invoke-result command, or the clock advanced and a timers-due command), and asserts after each step that it was accepted or
refused (a refused step with the refusal reason of the engine's replay, `step.trace.refusal`), the outcomes of the gate audit
records the step wrote when the replay wrote any (`step.trace.audit`), the active leaf states, the names and values of the
context attributes it changed, and the outcome after the last. The instance identity is the scenario's id as a UUID. The tests call the real guards (a scenario's `assume` values are not used), so
a handler that disagrees with the model's scenario fails its test. The test project supplies `ScenarioHost` in the root
namespace: `static Task<ScenarioHost> StartAsync(ManualClock clock, CancellationToken cancellationToken)`, a `Dispatcher`
property and `DisposeAsync`, wiring the stores the project chose (the gate 3 fixture's opens an in-memory SQLite database from
the sql-ddl schema). A failing test is fixed in the model or in a handler, never in the test.

## Type map

`types/csharp.json` maps the model's built-in types to C# for `type_of attribute "csharp"`, with `nullable` (`{type}?`)
and `collection` (`IReadOnlyList<{type}>`) patterns. Enums, value objects and scalar types map to their own names (a
scalar type to its base type); add an entry named after one of them to map it elsewhere, for example `"EmailAddress":
"MailAddress"`. `ulid` maps to `string`, so the pack needs no ULID library, and the column-only `range` and `multirange`
map to `string`, their text form.

## Parameters

Set them in `maquettiste.json` under `packs.csharp-dapper.parameters`.

| Parameter | Default | Meaning |
| --- | --- | --- |
| `namespace` | `""` | Root namespace; each package adds its qualified name. Unset, the pack takes the project's `baseNamespace` property (Settings › General, `project.properties.baseNamespace`), else `App.Model`. |
| `database` | `""` | The database the repositories and foreign-key properties use; empty picks, per entity, the first database by name that maps it. |
| `generatedFolder` | `""` | Folder (under the pack output) for generated files. |
| `partialFolder` | `""` | Folder (under the pack output) for the once-written companions. |
| `seedLoader` | `false` | Also write `SeedDataLoader.g.cs` (unit `seed-loader`): a class that loads the seed-data pack's output for one database with Dapper. Off, nothing changes. |
| `endpointsFolder` | `""` | Folder (under the pack output) for the process and operation endpoint files; empty writes none. Keep it in a folder you commit: the process files have user-code regions. |
| `testsFolder` | `""` | Folder (under the pack output) for the generated scenario tests (a test project's); empty writes none. |
| `gitignorePath` | `""` | Path (under the pack output) of an ignore file that gets a managed block listing `generatedFolder`; empty writes none. Allow the file in `outputs.allow`. |

## Tests

`tests/billing-small/` is the pack's case: a package `Billing` with the entities `Customer` and `Invoice` (with four checks)
and the enum `InvoiceStatus`, and under `expected/` the files the pack writes for it (both halves of each entity, the enum, the
type handlers and the code point order). `tests/helpers.test.js` tests each helper of `helpers.js`. Copy the pack folder, `tests/` included, into
a project's `.maquettiste/templates/` and run `maquettiste pack test csharp-dapper`: the case runs as a project of its
own and its output is compared byte for byte with `expected/`. `--update` rewrites `expected/` from the output; review
the diff before keeping it. In this repository `ShippedPackTests` in `tests/Maquettiste.Cli.Tests` runs it, and
`MAQUETTISTE_UPDATE_GOLDEN=1` updates it. `ExprCsTests` in `tests/Maquettiste.Packs.Tests` compiles the `Validate()` of a check
of each shape that is easy to get wrong in C# with warnings as errors, and compares its answer on several rows with the engine's.

## Files

| File | Holds |
| --- | --- |
| `pack.json` | Units and parameter defaults. |
| `helpers.js` | `join_path`, `package_folder`, `cs_ident` (escapes C# keywords), `cs_value_type`, `cs_expression` (the JavaScript expression translator), `expr_cs` (the example renderer of the expression language) and `expr_cs_holds` (a guard's form), `cs_unwrap` (an expression without its outer parentheses), `cs_line` (one comment line: every line break becomes a space, for every comment context of the process units) and `ulid_uuid`. |
| `tests/` | The `billing-small` case and `helpers.test.js` (see Tests); the starter the CLI embeds leaves them out. |
| `_csharp.scriban` | Shared functions: namespaces, property names and types, XML documentation, file header, and the spec entries `expr_cs` reads (`expr_entry`, `expr_functions`). |
| `_dapper.scriban` | Mapping choice, the row model and the entity/row conversions. |
| `entity.scriban`, `entity.partial.scriban` | The generated and hand-written halves of an entity. |
| `enum.scriban`, `value-object.scriban`, `repository.scriban`, `registrations.scriban`, `type-handlers.scriban`, `code-points.scriban` | The other units. |
| `_binding.scriban` | The repository of a bound entity, from its binding's statements (included by `repository.scriban`). |
| `_operation.scriban` | Shared functions of the operation units: the type name, member and return types, paging, key parameters, fired events and the HTTP mapping. |
| `operation-contracts.scriban` | The contracts of an operation. |
| `operation-endpoints.scriban` | The HTTP endpoint of an operation, with `endpointsFolder`. |
| `_process.scriban` | Shared functions of the process units: names, folders, attribute types, C# literals, the translator's input. |
| `process-*.scriban`, `*.partial.scriban` | The process units and their companions. |
| `dispatch*.scriban`, `interpreter*.scriban`, `actors.scriban`, `scenario-tests.scriban` | The model-level process units and the scenario tests. |
| `gitignore.scriban` | The managed block of the ignore file named by `gitignorePath`. |
| `types/csharp.json` | The type map. |
