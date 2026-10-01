# csharp-dapper

A C# model and [Dapper](https://github.com/DapperLib/Dapper) data access layer from the resolved model: entity classes as
generated and hand-written halves of one partial class, enums, value objects, a repository per mapped entity, one registration
file per package and Dapper type handlers; for processes, their states, definitions and contracts, the handlers, services,
machine and store as generated and hand-written pairs, HTTP endpoints with user-code regions, a typed in-process dispatcher with
pipeline behaviours, a generated statechart interpreter and one xunit test per scenario (see Processes).

The pack writes to a **built** output root. `maquettiste init --pack csharp-dapper` sets `packs.csharp-dapper.output` to
`src/Generated` and declares that root without `commit`, so it is regenerated on every build and is yours to ignore or commit (`init --gitignore` writes the entry); every path below is
under `src/Generated/`. The generated code needs C# 12, .NET 9 or later (`Guid.CreateVersion7`) and the `Dapper` package.

## Output

For entity `Invoice` in package `Billing`:

| Unit | Mode | For | Writes |
| --- | --- | --- | --- |
| `entity` | `pair` | `each entity` | `Billing/Invoice.g.cs` every run, and its companion `Billing/Invoice.cs` once |
| `enum` | `overwrite` | `each enum` | `Billing/InvoiceStatus.g.cs`: the enum and an `InvoiceStatusCodes` class (`ToCode`, `Parse`) for text storage |
| `value-object` | `overwrite` | `each value object` | `Billing/Money.g.cs`: a `sealed partial record` with `init` properties |
| `reference-type` | `overwrite` | `each reference type` | `ReferenceData/UnitOfMeasure.g.cs`: a `sealed record` and a static class of rows (see Reference types) |
| `resources` | `overwrite` | `each locale` | `ReferenceData/ReferenceData.resx`, `ReferenceData.fr.resx`, …: display names and row labels per declared locale |
| `repository` | file blocks | `each entity` | `Billing/InvoiceRepository.g.cs`: `IInvoiceRepository` and `InvoiceRepository` |
| `registrations` | file blocks | `model` | `Billing/BillingRepositories.g.cs`: one per package with repositories |
| `type-handlers` | `overwrite` | `model` | `DapperTypeHandlers.g.cs`: the Dapper type handlers and `UlidGenerator` |

Folders follow the package tree (`Billing/Catalog/` for package `Catalog` inside `Billing`), and namespaces follow it too:
`<namespace>.Billing.Catalog`.

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

With the default settings the companions sit next to the generated files, in the built `src/Generated` root, which is regenerated on every build. To commit
them, give them their own committed root: set `packs.csharp-dapper.output` to `src`, `generatedFolder` to `Generated` and
`partialFolder` to `Model`, and declare `src/Generated` (built) and `src/Model` (`commit: true`) in `outputs.allow`.

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

Not covered, by design of an example pack: navigations and junction tables (write queries for them in the companion or in your
own repository class, which is `partial`), collections stored in child tables (their property is left empty), and table-per-type
hierarchies, whose rows span tables (no repository is generated for them).

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

Row fields are the codes in PascalCase (`Code` in front when that does not start with a letter). With locales declared in
`localization`, one `.resx` per locale holds `<Type>_DisplayName`, `<Type>_PluralName`, `<Type>_Description` and `<Type>_<Row>`
(the row label) through the locale's fallback chain; the default locale's file is the neutral `ReferenceData.resx` and the others
are satellites the SDK compiles into `fr/…resources.dll`.

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
| `process-states` | `overwrite` | `each process` | `Processes/PurchaseApproval/PurchaseApprovalStates.cs`: a constant per state path; an enum with one member per path for an orchestration, `ToStatus` (active states to the bound enum) for a lifecycle |
| `process-definition` | `overwrite` | `each process` | `PurchaseApprovalDefinition.cs`: the chart as static data (states with parents, initial children and history; transitions in priority order with triggers, guards, actions, delays and gates; events with their actors; invokes; gates and meanings) |
| `process-contracts` | `overwrite` | `each process` | `PurchaseApprovalContracts.cs`: the context record, one command per event (`PurchaseApproval<Event>Command`: the envelope plus the typed payload, and the gate's audit attributes for a gated event), the built-in `PurchaseApprovalStartControl`, `PurchaseApprovalInvokeResultControl` and `PurchaseApprovalTimersDueControl` (their names end in `Control`, every event command's in `Command`, so no event name collides with them; all implement `IProcessCommand`), `PurchaseApprovalTransitioned` and one audit record per gate (the fields of `gate.audit`) |
| `process-handlers` | `pair` | `each process` | `PurchaseApprovalHandlers.g.cs` (guards and actions as `Guard<Name>` and `Action<Name>`, so a guard and an action may share a name: translated ones implemented, the others declared as partial methods; the command handler that loads, runs the machine, saves over the version it loaded and returns the outbox events) and `PurchaseApprovalHandlers.cs` once, with a stub per untranslated guard or action |
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
| `actors` | `overwrite`, file block | `model` | `Processes/Actors.cs`: a constant per actor, its type, and each event's allowed actors |
| `scenario-tests` | file blocks | `each scenario` | `<testsFolder>/PurchaseApproval/HappyPathTests.cs`: one xunit test per scenario; only when `testsFolder` is set |

### The pair and regions shapes

Guards, actions, services and storage are hand-owned logic, so their units are pairs, as entities are: the `.g.cs` half is
regenerated on every run, the `.cs` companion is written once and never touched again. The generated half declares what the
companion must supply (a partial method per untranslated guard or action, the service interfaces the class implements, the
store interface), so a guard, action or service added to the model fails the build until its companion code is written. Keep
the companions in a committed root (`partialFolder`, see The pair pattern). Endpoints are one file per process whose set of
endpoints follows the events: each has a region between `// maquettiste:keep id=<event id>` (the event's model id, so renaming
the event keeps the region's body) and the end marker, just before the
command is sent, where the request can be mapped further (`command = command with { ... }`) and the response reshaped
(`respond = result => ...`); the rest of the file is regenerated and region bodies carry over. Regions need a committed root,
hence `endpointsFolder`.

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
`gate-reason`). Audit records hold the actor's and the meaning's ids, as the engine's do (the envelope carries names). A transition
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

`helpers.js` translates the documented subset of the model's JavaScript expressions to C#; anything else becomes a stub in the
handlers companion, with the expression as a comment:

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

`types/csharp.json` maps the model's built-in types to C# for `type_of attribute "csharp"`, with `nullable` (`{type}?`) and
`collection` (`IReadOnlyList<{type}>`) patterns. Enums, value objects and scalar types map to their own names (a scalar type to
its base type); add an entry named after one of them to map it elsewhere, for example `"EmailAddress": "MailAddress"`. `ulid`
maps to `string`, so the pack needs no ULID library.

## Parameters

Set them in `maquettiste.json` under `packs.csharp-dapper.parameters`.

| Parameter | Default | Meaning |
| --- | --- | --- |
| `namespace` | `"App.Model"` | Root namespace; each package adds its qualified name. |
| `database` | `""` | The database the repositories and foreign-key properties use; empty picks, per entity, the first database by name that maps it. |
| `generatedFolder` | `""` | Folder (under the pack output) for generated files. |
| `partialFolder` | `""` | Folder (under the pack output) for the once-written companions. |
| `endpointsFolder` | `""` | Folder (under the pack output) for the process endpoint files; empty writes none. It must be in a committed root: the files have user-code regions. |
| `testsFolder` | `""` | Folder (under the pack output) for the generated scenario tests (a test project's); empty writes none. |

## Files

| File | Holds |
| --- | --- |
| `pack.json` | Units and parameter defaults. |
| `helpers.js` | `join_path`, `package_folder`, `cs_ident` (escapes C# keywords), `cs_value_type`, `cs_expression` (the expression translator), `cs_line` (one comment line: every line break becomes a space, for every comment context of the process units) and `ulid_uuid`. |
| `_csharp.scriban` | Shared functions: namespaces, property names and types, XML documentation, file header. |
| `_dapper.scriban` | Mapping choice, the row model and the entity/row conversions. |
| `entity.scriban`, `entity.partial.scriban` | The generated and hand-written halves of an entity. |
| `enum.scriban`, `value-object.scriban`, `repository.scriban`, `registrations.scriban`, `type-handlers.scriban` | The other units. |
| `_process.scriban` | Shared functions of the process units: names, folders, attribute types, C# literals, the translator's input. |
| `process-*.scriban`, `*.partial.scriban` | The process units and their companions. |
| `dispatch*.scriban`, `interpreter*.scriban`, `actors.scriban`, `scenario-tests.scriban` | The model-level process units and the scenario tests. |
| `types/csharp.json` | The type map. |
