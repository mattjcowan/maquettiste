# Scripting

**Owner:** W4 Sandbox. See docs/engineering/engine-design.md section 18.

The Jint sandbox, the engine pool and the read-only JavaScript model proxies (engine-design.md section 10).

- Implements: `IScriptSandboxFactory` (`ScriptSandboxFactory`), `IScriptSandboxPool` (`ScriptSandboxPool`), `IScriptSandboxLease`,
  `IScriptSandbox` (`ScriptSandbox`), `JsModelProxy` (plus `JsListProxy` for lists).
- Consumes: R-types, `IReadRecorder`, `SandboxLimits`, `ModelSnapshot` and `ElementDocument` (validation rules).

Tests: `tests/Maquettiste.Engine.Tests/Scripting/`.

## Status: implemented

| File | Holds |
| --- | --- |
| `ScriptingContracts.cs` | The public §10 contract (scaffold; unchanged). |
| `ScriptSandboxFactory.cs` | Prepares every script once per pool (`Engine.PrepareScript`, shared by the pool's engines) and creates the pool. |
| `ScriptSandboxPool.cs` | One engine per worker, rented for a whole unit. The first engine is built eagerly, so load errors surface from `CreatePool`. |
| `ScriptSandbox.cs`, `ScriptSandbox.Calls.cs` | Engine options, the `maquettiste` global, calls, error and limit mapping, the validation-rule model view. |
| `JsModelProxy.cs` | `JsModelProxy : ObjectInstance` and `JsListProxy : ArrayLikeObject`. |
| `ScriptValues.cs` | CLR ↔ JavaScript conversion; `ScriptObjectMap` (plain objects leaving a script). |
| `MemberCatalog.cs` | Per-pool reflection cache: which types cross as proxies, their camelCase members. |
| `SandboxRuntime.cs` | Fixed clock, seeded xorshift128+ `Math.random`, call-token constraint, `ScriptErrorException`. |

## Engine options (Jint 4.16.4)

`Strict()`, `Interop.Enabled = false` (with `AllowGetType`, `AllowSystemReflection` and `AllowWrite` off), `DisableStringCompilation()`
(no `eval`, `Function`, or generator/async function constructors), no modules (`import()` fails), invariant culture, UTC time zone,
a `TimeSystem` fixed at 2000-01-01T00:00:00Z (so `Date.now()`, `new Date()` and `Temporal.Now` are constant), a Temporal time zone
provider whose default zone is UTC (`UtcDefaultTimeZoneProvider`; Jint's default provider takes the host's zone), `LimitRecursion`,
`LimitMemory`, `TimeoutInterval` and `MaxStatements` from `SandboxLimits`, the pool's run token through
`Options.CancellationToken`, and `Constraints.Reset()` before every top-level call. So every limit applies per call
(each helper call, selector, filter, transform or rule check), as SPEC S19 says. `scriptTimeoutMs: 0` sets no
`TimeoutInterval`: the wall clock is off and statements alone bound a call, so results do not depend on the machine's
load (next-phase-1-design.md section 5.7). Statements are clamped to 1 to `ScriptSandbox.StatementCap` (2,000,000,000),
below `int.MaxValue`, which Jint reads as no limit. `LimitMemory` counts the bytes a call allocates on its thread, not
what it keeps, so a long loop that makes new numbers can reach it before the statement limit.

Additions beyond section 10, all to keep the host process safe or the output deterministic:

- A custom constraint checks `ScriptCallContext.CancellationToken` while a script runs (not only before it starts), so either token
  stops a runaway script within the one-second budget (host-contracts 26). Cancellation surfaces as `OperationCanceledException`.
- `Constraints.StackOverflowGuard = true`: deep native recursion inside built-ins (for example `JSON.stringify` of a deeply nested
  array) becomes a catchable `RangeError` instead of ending the process.
- `MaxArraySize = ScriptMemoryBytes / 16`, and `String.prototype.repeat`, `padStart` and `padEnd` refuse results larger than the
  memory limit: those allocate in one step, before the per-statement memory check can run.
- Every regular expression match is bounded by `min(ScriptTimeoutMs, 250 ms)`, or 250 ms when `ScriptTimeoutMs` is 0, so
  a catastrophic pattern cannot outlive the cancellation budget. Jint needs three settings for that:
  `Constraints.RegexTimeout`; the prepared scripts' parsing option `RegexTimeout` (a prepared script ignores the
  constraint and gives its literals 10 s); and a guard on `RegExp.prototype.exec`, `test`, `[Symbol.match]`,
  `[Symbol.matchAll]`, `[Symbol.replace]`, `[Symbol.search]` and `[Symbol.split]` that re-creates the .NET regex of a
  `RegExp` built at run time (`new RegExp(...)`, or the one `'...'.match(string)` builds), which Jint always gives a 5 s
  timeout. The guard also checks the engine's constraints on every call, so a built-in that matches many times (a global
  `replace`, `split`, `matchAll`) observes the time limit and both tokens between matches. A regex timeout while a token
  is cancelled surfaces as cancellation. Because `exec` is no longer the built-in, Jint takes its spec-compliant slow
  path for `replace`, `split` and `matchAll`; results are the same.
- The match bound is wall-clock time, so a thread that a busy machine does not schedule for 250 ms times out even on a trivial
  pattern (CI saw `/\?$/.test(type)` fail once with MQ6007 while nine workflows shared the runner). The guard therefore tries a
  timed-out match once more (`RegexRetry`) when the call cannot have run script code: the receiver is a plain `RegExp` (the
  intrinsic prototype, no own property but a numeric `lastIndex`) and every argument is a primitive (no `toString`, no
  replacer function). It restores `lastIndex` and checks the constraints (time limit, statements, both tokens) before the
  second attempt, so the result is the one an uninterrupted match gives and cancellation is still observed within 250 ms. A
  timeout that an inner match already retried (the `exec` calls inside a global `replace`) is not retried again by the call
  around it. A catastrophic pattern times out twice and fails with MQ6007 after about 500 ms. Raising the 250 ms bound
  instead was rejected: a match cannot be interrupted, so the bound is what keeps cancellation inside its one-second budget.
  The template `regex` functions retry the same way (Rendering README).
- `Intl.DateTimeFormat` is replaced by a constructor that adds `timeZone: 'UTC'` when the options give none (Jint takes the
  default from the host otherwise). Prototype, statics, `instanceof` and subclassing are unchanged; an explicit named zone
  still resolves through the host's time zone data.
- Removed globals: `ArrayBuffer`, `SharedArrayBuffer`, `DataView`, `Atomics`, all typed arrays (single large allocations),
  `WeakRef`, `FinalizationRegistry` (garbage-collector dependent) and `ShadowRealm`.
- After the scripts load, `globalThis` and everything reachable from it (every intrinsic and prototype, the `maquettiste` object,
  script-declared globals) is deep-frozen. Consequence: the strict-mode "override mistake" applies, so `obj.toString = f` on an
  ordinary object throws; use an object literal, a class or `Object.defineProperty`. Closure state (`let` at the top level, captured
  variables) cannot be frozen; helpers must be pure (D12).
- Rule scripts (next-phase-1-design.md section 5.7). `PreparedScript.PrepareAll` wraps each one as `(function () {` +
  code + `\n})();`, the prefix on the code's first line, so line numbers stay; `PreparedScript.Map` takes the prefix off
  first-line columns and puts a position in the suffix at the end of the last line, for syntax errors and in
  `Translate`. A program that is not exactly that one call (a closing brace in the code that ends the function early) is
  MQ5002. When every script of a pool is a rule script, the globals are deep-frozen after the setup and before the
  scripts run, so a rule file cannot publish anything for another: a global or a new prototype member is a `TypeError`
  while it loads (MQ5002), and a function of another file is not in scope. Pack scripts keep one scope per pack and the
  freeze after loading. The first `let` or `var` outside every function and class body of a rule script (a top-level
  block or loop head included) is MQ5005, a warning the factory passes to the pool (`IScriptSandboxPool.Warnings`,
  ordinal by path).
- Rows scripts (next-phase-2-design.md section 3.4): a file directly in `extensions/seeds/` is a model script like a
  rule script (`IsRuleScript` holds for both, `IsRowsScript` for this folder only), with the same scope, freeze, MQ5005
  and MQ5002/MQ5003 codes, which the seed rules and the extension file write report as MQ7127. It calls
  `maquettiste.rows(fn)` exactly once and nothing else (any other registration is a `TypeError` while it loads); a rule
  or pack script that calls `rows` fails to load. `RunRows` calls `fn(model, seed)` with the rule-script model view and
  a frozen copy of the seed's JSON, seeds `Math.random` with the caller's seed (the seed id) and returns the array as
  JSON: plain objects of null, booleans, strings, finite numbers, arrays and objects; anything else is a script error.

A limit breach, a cancellation or a host failure marks the engine faulted, and the pool disposes it on return and builds a fresh
one when needed.

## Script API

Scripts run in load order against a global `maquettiste`:

| Call | Invoked as | Returns |
| --- | --- | --- |
| `helper(name, fn[, { plain }])` | `fn(...args)` | any JSON-like value or model object |
| `selector(name, fn)` | `fn(model)` | an array (or array-like) of model objects or id strings → `IReadOnlyList<string>` |
| `filter(name, fn)` | `fn(element, model)` | truthy/falsy → `bool` |
| `transform(name, fn)` | `fn(element, model)` | a plain object (or `null`/`undefined` for none) → `IReadOnlyDictionary<string, object?>` |
| `rows(fn)` (rows scripts only, once) | `fn(model, seed)` | an array of plain objects of JSON values → `JsonElement` |
| `rule({ id, severity, kinds, check })` | `check(element, model, report)` | nothing; `report(message, { pointer, severity })` |
| `rule({ id, severity, scope: "model", check })` | `check(model, report)`, once per whole-model validation | nothing; `report(message, { element, pointer, severity })` |
| `rule({ id, severity, scope: "resolved", kinds, check })` | `check(element, model, report)` per resolved object of its kinds (`RunResolvedRule`) | nothing; `report(message, { pointer, severity })` |
| `calculated({ name, kinds, schema, description, value })` (calculation files only) | `value(element, model)` per resolved object of its kinds (`RunCalculation`) | a JSON value → `null`, `bool`, `long`, `double`, `string`, or a list or map of them |

- Names are unique per kind (a helper and a filter may share a name); registering after loading is a `TypeError`.
- `helper`'s optional third argument is an object whose only key is `plain`, a boolean; any other value, key or `plain`
  value is a `TypeError`, so the script fails to load (MQ6016). `{ plain: true }` makes the helper take plain copies
  (below).
- `maquettiste.params` is a read-only accessor returning the current call's `ScriptCallContext.Parameters` (frozen; empty while
  loading). Section 10 does not say how scripts see parameters; this is the choice made here.
- `maquettiste.use(name)` (library packs, next-phase-2-design.md section 5.2) returns the frozen object of a used pack's
  helpers: one host function per name of the `ScriptLibrary` the pool was created with (`IScriptSandboxFactory.CreatePool`'s
  `libraries` overload), the same object for every call in one engine. It may run while scripts load, so
  `const naming = maquettiste.use("naming");` at the top of a file works; a name outside the libraries is a `TypeError`
  (MQ6016, or MQ5002 in a rule script). A member call converts its arguments out as a script's results are (`FromJs`; a
  model proxy leaves as its object), calls `ScriptCallContext.Libraries.Call(pack, helper, args, caller)` and converts the
  result in as a helper's arguments are (`ToJs`). Without a call context (while loading) or without `Libraries` (selectors
  and filters, which run while planning) the call is a `TypeError` that says so. The used pack's helper runs in a sandbox
  of its own pool, so its scripts load exactly as they do for the pack itself; a failure there is its own
  `ScriptErrorException` or `ScriptLimitException` at its own script and line, which passes through the calling engine
  (`Run` only resets that engine's call stack). The calling engine's wall clock keeps running during the nested call;
  each engine counts its own statements. The pool and the sandbox add nothing else, so a pool without libraries behaves
  as before.
- `rule`: `id` has no whitespace (the diagnostic rule is `x/<id>`); `severity` is `error` (default), `warning` or
  `info`; `kinds` lists kind names from `KindInfo` (empty or missing = all kinds; `RunRule` returns no diagnostics for
  other kinds). `element` is a deep-frozen copy of the element's canonical JSON; `model` offers `get(id)` (an element,
  or a sub-element found through its index pointer), `all(kind)` (the snapshot's sorted kind index, ordinal by name,
  then id), `referencesTo(id)` (`{ fromElementId, fromId, jsonPointer, field, toId }`, the id's own file and diagrams
  included), and (next-phase-1-design.md section 5.7) `withStereotype(key)`, `withTag(tag)` and `withProperty(key)` (the
  documents whose own file lists the key, ordinal by name, then id, from `Validation/RuleModelIndex`, built once per
  snapshot) and `byQualifiedName(name)` (what `get` returns for each id a 5.1 name such as `@entity:Billing.Invoice`
  finds in the snapshot's `NameIndex`, ordinal by id; a malformed name or a kind without names is a `TypeError`), all
  frozen. Report diagnostics carry the element id, the element file path and the pointer; W2 fills line and column.
- `scope` is `"element"` (the default) or `"model"`; anything else, or `kinds` with `"model"`, is a `TypeError`.
  `ScriptRegistration.WholeModel` marks a model rule. `RunModelRule` runs it once under one statement budget; its
  `report` needs an `element` option naming a document or sub-element, whose file the diagnostic takes (the entry's
  pointer before the given one, the given id as the element id); a missing or unknown id is a `TypeError`, so MQ5002 on
  the rule file. `RunRule` returns nothing for a model rule.
- `scope: "resolved"` (next-phase-2-design.md section 5.5) needs `kinds` from `ResolvedKinds.All` (resolved kind names
  such as `entity`, `attribute`, `table`, `column`); none, or an element-only kind, is a `TypeError` (MQ5002).
  `ScriptRegistration.Resolved` marks it, `RunRule` and `RunModelRule` return nothing for it, and `RunResolvedRule` runs
  it on one `IResolvedObject` with the resolved model: `element` and `model` are the proxies of the model view (below),
  and the diagnostics carry the object's id and the given pointer; `Validation/ResolvedChecks` locates them
  (`ResolvedLocations`). The extension file API's `ExtensionRuleInfo.Resolved` reads `ScriptSandbox.Rules`.
- Calculation files (`extensions/calculated/*.js`, `IsCalculationScript`) are project scripts as rule and rows scripts
  are: one function scope per file, the early freeze, MQ5002 and MQ5003 while loading, MQ5005 for top-level `let` or
  `var`, and no script library. They may call `maquettiste.calculated` and nothing else; `calculated` from any other
  script is a `TypeError`. A declaration checks its `name` (`^[a-z][A-Za-z0-9]*$`, at most 64), its `kinds` (from
  `ResolvedKinds.All`), its `schema` (compiled with `CompileCalculationSchema`, draft 2020-12), an optional
  `description`, and refuses any other key and a (name, kind) pair already declared (the later file by path). The
  registration carries a `CalculatedValueDeclaration`. `RunCalculation(name, element, model, ctx)` calls `value` once,
  with its own statement budget, and converts the result out as a transform's (`CalculationJson` gives the JSON the
  schema check reads); a non-JSON result is a script error, which `Resolution/CalculationHost` reports as MQ5010.
- Read keys: `get` records `e:<id>`; `all` records `k:<kind>` and `e:` per document; the index functions record
  `ix:<index>:<value>` (`ix:stereotype:`, `ix:tag:`, `ix:property:`, and `ix:qn:<canonical name>` for `byQualifiedName`)
  and `e:` per document; `referencesTo` records `rt:<id>`. `Validation/RuleKeyHasher` hashes them per snapshot for the
  validator's result cache (`Validation/README.md`).

## Model view

`JsModelProxy` exposes the public properties of resolved-model types (`Maquettiste.Engine.Resolution`), their helper types and
`GenerationHints` in camelCase; `Dependencies` is hidden, and the `ResolvedModel` shows only its lists plus `find(id)`.
Conceptual objects add `hasStereotype(key)` and `hasTag(key)`. Proxies are cached per engine, so identity holds
(`model.find(e.id) === e`). `Set`, `DefineOwnProperty`, `Delete` and `setPrototypeOf` refuse (a `TypeError` in strict mode);
`Object.isFrozen(proxy)` is true.

Dependency recording matches the Scriban accessor: every member read, `in` test or key enumeration of an `IResolvedObject`
records its `Dependencies`; reading the length or an item of an `RList<T>` records its `MembershipKeys`; `find` of a missing id
records `e:<id>`. Keys go to the recorder of the call that reads them, even through proxies cached by an earlier call.

Every annotated object has `calculated` (next-phase-2-design.md section 5.5): a frozen object whose keys are the names
its kind declares, ordinal. Reading a key evaluates the value through `RCalculated.Read` with the call's recorder, so
the reader records the value's keys and its calculation file's `x:` key; enumerating the keys or an `in` test records
`cv:<kind>` and evaluates nothing. A name the kind does not declare is a script error carrying MQ5012, and a value that
failed rethrows its own diagnostic. A plain helper's copies carry every value of an argument's object at hop 0 only.

Lists cross as frozen `JsListProxy` array-likes whose prototype is `Array.prototype`: indexing, `length`, `for…of`, spread,
`Array.from`, `map`, `filter` and `find` work; `Array.isArray` is `false` and mutators throw.

Values crossing the boundary:

- Into scripts: `null`, strings, booleans, numbers, enums (their JSON names), `JsonElement` (deep-frozen), resolved objects
  (proxies), string-keyed maps (frozen objects with keys in ordinal order, since CLR maps have no dependable order), other
  enumerables (array-likes). Anything else is a script error.
- Out of scripts: `undefined`/`null` → `null`; integral numbers within ±2^53−1 → `long`, others → `double`; strings; booleans;
  arrays → `IReadOnlyList<object?>`; proxies → the original CLR object; plain objects (prototype `Object.prototype` or `null`)
  → `ScriptObjectMap` (an `IReadOnlyDictionary<string, object?>` that keeps JavaScript property order). A function, symbol,
  BigInt, promise (so any async function), `Date`, `Map`, `Set`, `RegExp`, `Error`, class instance or any other non-plain
  object, or a value nested deeper than `ScriptRecursion` (circular data) is a script error naming what it is.
- An array (or a selector's array-like) longer than `MaxArraySize` is a memory limit error (MQ6007) before the host allocates
  anything, and conversion loops check the engine's constraints every 1,024 items or keys, so converting a result counts
  toward the call's time and memory limits and observes both tokens.
- Caching: resolved-model objects, `RList<T>`, frozen collections and `System.Collections.Immutable` collections cross as the
  same script object for the engine's lifetime (so identity holds). Any other CLR map or list (a `List<T>`, a `Dictionary`, a
  template engine's arrays and objects) and `maquettiste.params` are converted afresh on every call, so a caller that mutates
  them between calls is seen correctly and the engine does not keep them alive.

### Plain helpers

A helper registered with `{ plain: true }` (next-phase-1-design.md section 5.5) takes frozen plain copies of its
arguments (`ScriptValues.ToPlainJs`) instead of proxies, which are cheaper to read in a loop. The flag lives in the
sandbox's registry (`Registered.Plain`); no public type changes.

- A copy is bounded by member hops, since a copy of everything reachable would be the whole model (`database.tables`). A
  resolved object given as an argument, or as an item of an argument list or map, is at hop 0, and its copy is a frozen
  object of every camelCase member `MemberCatalog` lists. A resolved object a member holds, directly or inside a list or
  map, is copied at the next hop; lists, maps and the model's helper objects (`RType`, `RStereotype`, `RCategory`, ...)
  add no hop of their own. From hop 2 a resolved object is the stub `{ id, kind }`. Scalars, strings, enums (their JSON
  names), `JsonElement` and null copy as above; lists become frozen arrays (`Array.isArray` is true) and maps frozen
  objects. A map read through `MemberCatalog.TryReadMap` has its keys in ordinal order; a `ScriptObjectMap` (a value a
  script built) keeps its insertion order, which is just as deterministic.
- One memo keyed by (object, hop) serves the whole call, so a record shared by two arguments is one object at a hop, and
  a cycle of helper objects (a category's parent and children) closes on its copy.
- Copies carry no functions: `hasStereotype`, `hasTag` and `find` exist on proxies only; a plain helper reads
  `stereotypes` and `tags`. The `ResolvedModel` itself, as an argument or inside one, is a `TypeError` (MQ6016): pass
  the list the helper needs.
- Recording: every resolved object copied at hop 0 or 1 records its `Dependencies` when it is copied, whether the helper
  reads it or not, and every copied `RList<T>` its `MembershipKeys`. A stub records nothing: its id and kind change only
  with the member or list that holds it, which its holder records.
- Limits, seeded random and cancellation are unchanged; the copy checks the engine's constraints every 1,024 objects.

## Errors

| Failure | Pack scripts | Rule scripts (a file directly in `extensions/rules/` or `.maquettiste/extensions/rules/`) |
| --- | --- | --- |
| Syntax error or throw while loading (from `CreatePool`) | `ScriptErrorException`, MQ6016 | `ScriptErrorException`, MQ5002 |
| A limit while loading | `ScriptLimitException`, MQ6007 | `ScriptLimitException`, MQ5003 |
| A call throws or returns an unsupported value | `ScriptErrorException`, MQ6016 | a MQ5002 diagnostic in `RunRule`'s result, after the rule's earlier reports |
| A call exceeds a limit | `ScriptLimitException`, MQ6007 | `ScriptLimitException`, MQ5003 |
| Unknown helper/selector/filter/transform/rule name | `ScriptErrorException`, MQ6016 | `ScriptErrorException`, MQ5002 |
| Cancellation | `OperationCanceledException` | `OperationCanceledException` |

Diagnostics carry the script path and the 1-based line and column of the failing expression (from the Jint exception location,
else the innermost stack frame, else the registering script without a line) and, for filters, transforms and rules, the element
id. `ScriptErrorException` is `internal` (its consumers, W2, W5 and W6, are in this assembly); section 10 names only
`ScriptLimitException`.

## Known limits

- The memory limit is Jint's: bytes allocated on the calling thread since the call started, checked between statements. The
  guards above cover the built-ins that allocate most in one step, but other single built-in calls (a huge `join` separator,
  `JSON.stringify` of a large graph) can overshoot before the next check.
- A regular expression running at the moment of cancellation can hold the thread for up to 250 ms (a .NET regex cannot be
  interrupted; its match timeout is the bound). The retry does not extend this: cancellation is checked before it.
- A stall longer than 250 ms that hits both attempts of one match still fails it with MQ6007; the retry makes that rare, not
  impossible.
- Because a converted result now counts toward the memory limit, a helper whose result is close to `ScriptMemoryBytes` can hit
  the limit during conversion where it did not before.
- Rule scripts are recognised by path (see the table), since `CreatePool` does not say which kind of pool it builds. If W1
  gives `ModelSnapshot.RuleScripts` paths in another form (for example absolute, or under a model root not named
  `.maquettiste`), `IsRuleScript` must learn that form.

## Performance notes (WP, round 2)

- A call records each dependency key list once (`CallState.Record`): every item and length read of a list records the list's
  membership keys, and every member read records the object's dependencies, so a script walking a database's tables recorded the
  table list's keys (one per model file) once per item. Recorded keys form a set, so recording a list again in the same call adds
  nothing; the per-call memo is cleared at the start and end of every call. `LargeModelWalkTests` walks 5,000 items whose list has
  20,000 membership keys in well under the 2 s limit and checks the keys recorded.

## Helper tests (next-phase-2-design.md section 5.4)

`ScriptSandboxFactory.CreateTestPool` builds a one-engine pool for a pack's `tests/*.test.js` files: the pack's scripts
load as usual, the assert object (`ScriptTests.Prelude`, run under the internal source name `maquettiste:assert`, so a
failure points at the test's call) and a frozen object of the pack's helpers (the registered functions themselves,
ordinal) are built, the globals freeze, and then each test file loads in its own function scope
(`PreparedScript.PrepareAll(..., scoped: true)`, the rule-file form). Only a test pool has `maquettiste.test`, and it
works only while a test file loads; a test file that calls any other registration, repeats a test name of its own file,
throws, hits a limit or has a syntax error is a `ScriptTestFailure` and registers no test, while the other files still
load. Test names are unique per file. `RunTest` is one `Run` call with the context's parameters (the pack's
`pack.json` defaults) and libraries (each used pack in its own pool with its own defaults); an `AssertionError` reads
its own message, any other throw or limit the usual sandbox message. `AssertionError.prototype.name` is defined, not
assigned, since assigning over a frozen `Error.prototype.name` throws in strict mode. A pack's top-level `const` values
are not frozen (they live in the global lexical scope, not on the global object), so a test can mutate a top-level array
a pack keeps.
