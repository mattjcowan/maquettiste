# typescript: a custom template pack for a Node service

A small pack that turns a Maquettiste model into TypeScript types and zod schemas and, for a model with processes, into
running TypeScript: state types, chart definitions, commands, handler and service modules, a typed dispatcher, a statechart
interpreter and one test per scenario. It is an example of custom generation templates: copy the folder into a repository
and it generates next to the built-in packs.

```sh
mkdir -p .maquettiste/templates
cp -R <maquettiste>/samples/typescript-pack .maquettiste/templates/typescript
```

Then, in `.maquettiste/maquettiste.json`, allow an output folder and register the pack:

```json
"outputs": { "allow": [ { "path": "db" }, { "path": "src/generated" } ] },
"packs": { "sql-ddl": { "output": "db" }, "typescript": { "output": "src/generated" } }
```

`maquettiste generate` then writes `src/generated/`. Commit the folder and `maquettiste generate --check` guards it in CI;
or keep it out of version control and let every build run `generate` (which outputs to commit is your choice).

## Files

| File | What it does |
| --- | --- |
| `pack.json` | The pack: its units and parameters. `entity` renders once per entity to `<kebab-name>.ts`; `model` renders once for the model; `schema` renders once per non-abstract entity; the process units are listed under Processes. |
| `helpers.js` | JavaScript helpers: `ts_type` and `zod_type` map the model's built-in types (`string`, `uuid`, `decimal`, `datetimeoffset`, ...) to TypeScript and zod; `ts_string` writes a single-quoted literal; `ts_module` writes a relative import. Case conversion uses the engine's own `pascal`, `camel` and `kebab`. |
| `entity.scriban` | One interface per entity. Required attributes are plain, others get `?`. Enum attributes use the enum's type; reference attributes use the reference type's union of codes. Each to-one relation end adds its id (`customerId`) and every navigation adds an optional property (`customer?: Customer`, `lines?: OrderLine[]`). |
| `model.scriban` | File blocks: one module per enum (`order-status.ts`) and per reference type (`product-status.ts`), then the barrel `index.ts` that re-exports every generated module, sorted. |
| `schema.scriban` | File blocks: one zod schema per entity (`order.schema.ts`, `orderSchema` and `OrderInput`), with the ids of to-one relation ends. Enum and reference attributes validate against the exported `...Values` arrays. Written only while the `zod` parameter is true. |
| `_process.scriban` | Shared functions of the process units (module paths, value types, command type names). Prints nothing. |
| `process-*.scriban`, `dispatch*.scriban`, `interpreter*.scriban`, `actors.scriban`, `scenario-tests.scriban` | The process units, below. |

## Parameters

Set them in `maquettiste.json` under `packs.typescript.parameters`; the next `generate` re-renders only what they change.

| Parameter | Default | Effect |
| --- | --- | --- |
| `zod` | `true` | `false` stops the `schema` unit and removes the `*.schema.ts` files it wrote. |
| `enumStyle` | `"union"` | `"union"`: `type OrderStatus = 'pending' \| 'paid'`. `"const"`: a const object `OrderStatus.Paid` plus the same type. Both export `orderStatusValues`. |
| `importExtension` | `".js"` | The extension of relative imports: `.js` for `"moduleResolution": "NodeNext"`, `""` for bundlers, `.ts` to run the sources on node's type stripping (with `allowImportingTsExtensions`). |
| `processFolder` | `"processes"` | Where each process's modules go (`processes/<process>/`), and `actors.ts`. |
| `routePrefix` | `"/processes"` | The first segment of the endpoint routes (`/processes/<process>/:instance/<event>`). |
| `testsFolder` | `"tests"` | Where the scenario tests go (`tests/<process>/<scenario>.test.ts`). |

## Type choices

`decimal` and `int64` are strings (a JS number loses precision), dates and times are ISO strings, `json` is `unknown`,
`binary` is a base64 string, and value objects are `Record<string, unknown>`. A reference attribute holds a row's code:
a reference type becomes `type ProductStatus = 'DRAFT' | 'ACTIVE' | ...` with `productStatusValues`, in row order (plain
`string` while the type has no rows), so a new row changes only that module. Change `helpers.js` to choose otherwise.
The output uses two-space indentation, single quotes and semicolons, and depends only on the model, the settings and
the pack, so a second run changes nothing. It needs zod 3.23 or later (`z.string().date()`).

## Processes

For every process the pack mirrors the C# example pack's process units (phase-3-design.md section 7.2), with the same unit
ids. A model without processes gets none of these files: the model-wide units write file blocks only when the model has
processes (or actors), so the entity, model and schema output is unchanged.

| Unit | Scope | Output | Mode | What it holds |
| --- | --- | --- | --- | --- |
| `process-states` | each process | `processes/<p>/<p>.states.ts` | overwrite | The state union type (every dotted path), the path constants, the atomic states; for a lifecycle, the bound enum's value of each bound state and `<p>StatusOf(states)` |
| `process-definition` | each process | `<p>.definition.ts` | overwrite | The chart as a `const` object checked with `satisfies ChartDefinition`: states by path in document order, transitions in priority order, events, guards, actions, invokes, gates and actors, each with its model id |
| `process-contracts` | each process | `<p>.contracts.ts` | overwrite | The context type, one command per event (`<P><Event>Command`: typed payload, with a gate's audit attributes, plus the envelope: instance, actor, signer, meaning, reason), the built-in `<P>StartControl` (`:start`), `<P>TickControl` (`:tick`), `<P><Invoke>DoneControl` and `<P><Invoke>ErrorControl` (`<invoke>:done`, `<invoke>:error`): their names end in `Control` and every event command's in `Command`, so no event name collides with them; the command union, `<P>Transitioned` and one audit record type per gate |
| `process-handlers` | each process | `<p>.handlers.gen.ts` + `<p>.handlers.ts` | pair | Every guard and action; the stubs (no expression) are declared in `<P>Handlers` as `guard<Name>` and `action<Name>` (so a guard and an action may share a name) and implemented by the companion |
| `process-services` | each process | `<p>.services.gen.ts` + `<p>.services.ts` | pair | One interface per service task (`start`, `cancel`) and per human task (`assigned`, `withdrawn`), and `<p>RunTasks`, which the machine calls after a step |
| `process-machine` | each process | `<p>.machine.gen.ts` + `<p>.machine.ts` | pair | The typed facade: `start`, `send<Event>` per event, `complete<Invoke>`/`fail<Invoke>` per task (the prefixes keep event and invoke names apart from the fixed members), `tick`, `apply`, `states`, `matches`, and `handle` (load through the store, run, save, append history and audit records, tell the host and the services); the companion adds queries |
| `process-store` | each process | `<p>.store.gen.ts` + `<p>.store.ts` | pair | `<P>Store` (load and save the snapshot, append history, append audit records); the companion starts in memory |
| `process-endpoints` | each process | `endpoints/<p>.endpoints.ts` | regions | One endpoint per event, `POST /processes/<p>/:instance/<event>`, mapping the request to the command and calling the dispatcher, with a region per endpoint between the two, keyed by the event's id (keep the folder in version control: region bodies live only in the file) |
| `dispatch`, `dispatch-companion` | model | `dispatch/dispatch.gen.ts` + `dispatch/pipeline.ts` | overwrite + once | The command union of every process, `Handler`, `Behaviour`, `Registry`, `Dispatcher`, `createDispatcher`, the endpoint shapes; the companion orders the behaviours and holds the policy hooks |
| `dispatch-registry` | model | `dispatch/registry.ts` | overwrite | The typed map from command type to handler, `satisfies Registry` (a mapped type over every command type, so a missing handler fails the build; no reflection), each process's program by id (sub-process invokes run from it) and the stores |
| `dispatch-behaviours` | model | `dispatch/behaviours.ts` | overwrite | Composed functions in their default order: validation (payload required, length, range, allowed values), authorization (the caller may act as the command's actor), logging (sensitive attributes left out), transaction, outbox hand-off |
| `interpreter`, `interpreter-companion` | model | `runtime/statechart.gen.ts` + `runtime/process-host.ts` | overwrite + once | The interpreter (below) and the host's clock, timer scheduler and invoke host, with in-memory defaults (`ManualClock`, `InMemoryTimers`, `InMemoryInvokes`) and `InMemoryProcessStore` |
| `actors` | model | `processes/actors.ts` | overwrite | One constant per actor with its id and type, and each event's allowed actors by command type |
| `scenario-tests` | each scenario | `tests/<p>/<scenario>.test.ts` | overwrite | One `node:test` test per scenario |

`<p>` is the process name in kebab case, `<P>` in PascalCase.

**Pairs.** A pair is a generated `*.gen.ts` module plus a companion the generator writes once and never touches again. The
generated module declares what the companion must provide and imports it through that declaration:
`import { handlers } from './order.handlers.js'`, and the companion ends with `satisfies OrderHandlers`, so a stub the model
adds fails the build until the companion implements it. The dispatcher and the interpreter's host are model-wide pairs
written as two units (`dispatch` and `dispatch-companion`, `interpreter` and `interpreter-companion`), an overwrite unit and a
`once` unit of file blocks, because a model-scope `pair` unit always writes its files, and a model without processes must get
none.

**Regions.** Each endpoint builds its command and its response in generated code; between the two, a region
(`maquettiste:keep id=<event id>` to `maquettiste:end-keep`) may change either (`command = { ...command, ... }`,
`respond = (outcome) => ...`) and survives regeneration. The id is the event's model id, so renaming the event keeps the
region's body; removing the event leaves its region without a counterpart (MQ6010), so move the code before deleting it.

**Expressions.** A guard or action expression is emitted as written, on its own line, inside a typed function:
`(context: Readonly<OrderContext>, event: EventView): boolean => ( context.total > context.creditLimit )`. The context is typed
from the process's context attributes (every number is a `number`, as the interpreter holds JSON values), the event's payload
loosely (an expression may serve several events), and an action's result as `Partial<OrderContext>`, so the compiler catches an
expression that names an attribute the model no longer has. A guard or action without an expression is a stub: the companion
implements it; its starting body throws "not implemented", so every scenario test that reaches it fails until the rule is
written. A stub also gets the paths of the active states (all levels, document order) as a third argument, as the C# pack's
`StatechartCall.States`; the model's expressions read only the context and the event, as in the engine.

**The interpreter** (`runtime/statechart.gen.ts`) has the semantics of the model's own interpreter (phase-3-design.md section
4.1): legal configurations, macrosteps of microsteps with eventless transitions and raised events, selection by active leaf in
document order with conflict removal, exit and entry sets with shallow and deep history, `done` for compound and parallel
states, a clock that moves only on ticks with timers fired in due then document order, sub-process instances, pending service
and human tasks, gates with their audit records, and actors. Each call takes an instance snapshot (plain JSON the store keeps)
and returns the next, with the step's result: accepted or the refusal reason, the active atomic states, the context and what
changed, the guards evaluated, the audit records, the tasks started and cancelled.

**Commands and the clock.** Every input goes through the dispatcher as a command: `Order:start` creates an instance,
`Order.submit` sends an event, `Order.checkStock:done` completes a task, `Order:tick` moves the instance's clock to the host
clock's time and fires the timers due by then. The instance's clock moves only on ticks, as the model's interpreter's does; a
service dispatches a tick when `InMemoryTimers.due(now)` (or its own scheduler) names the instance. Actor and gate rules are
the interpreter's, so their refusals are audited; the authorization behaviour only asks the `mayActAs` hook whether the caller
may act as the command's actor.

**Scenario tests.** Each test starts a dispatcher on a `ManualClock` at the scenario's start, sends the start command and then
each step (a time step advances the clock and sends a tick), and after each step with an expectation asserts accepted or
refused (with the refusal reason of the engine's replay), the gate audit outcomes the replay wrote, the active states (when
the scenario lists them) and the changed context, that every stub guard the step assumes answered as assumed, that no guard
or action failed, and the outcome. A failing test is fixed in the model or in a handler, never in the test: the scenario
records what the handler must answer, so a companion that answers otherwise fails. Each step is also reported as a test
diagnostic line (`maquettiste-step` and JSON), which `TypeScriptPackTests` compares with the engine's replay.

To run them, with node 22.18 or later (which runs TypeScript by stripping types):

```sh
# package.json: { "type": "module" }; pack parameter "importExtension": ".ts"
npx tsc --noEmit        # tsconfig: strict, "allowImportingTsExtensions": true, "verbatimModuleSyntax": true, "types": ["node"]
node --test "src/generated/tests/**/*.test.ts"
```

With the default `.js` imports, compile with `tsc` to an output folder and run `node --test` on the compiled `*.test.js`. The
tests need `@types/node` to type-check; the generated code has no runtime dependency beyond node (and zod for the schemas).
