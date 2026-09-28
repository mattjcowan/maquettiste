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
`LimitMemory`, `TimeoutInterval` and `MaxStatements` from `SandboxLimits` (statements clamped to `int.MaxValue`), the pool's run
token through `Options.CancellationToken`, and `Constraints.Reset()` before every top-level call. So every limit applies per call
(each helper call, selector, filter, transform or rule check), as SPEC S19 says.

Additions beyond section 10, all to keep the host process safe or the output deterministic:

- A custom constraint checks `ScriptCallContext.CancellationToken` while a script runs (not only before it starts), so either token
  stops a runaway script within the one-second budget (host-contracts 26). Cancellation surfaces as `OperationCanceledException`.
- `Constraints.StackOverflowGuard = true`: deep native recursion inside built-ins (for example `JSON.stringify` of a deeply nested
  array) becomes a catchable `RangeError` instead of ending the process.
- `MaxArraySize = ScriptMemoryBytes / 16`, and `String.prototype.repeat`, `padStart` and `padEnd` refuse results larger than the
  memory limit: those allocate in one step, before the per-statement memory check can run.
- Every regular expression match is bounded by `min(ScriptTimeoutMs, 250 ms)`, so a catastrophic pattern cannot outlive the
  cancellation budget. Jint needs three settings for that: `Constraints.RegexTimeout`; the prepared scripts' parsing option
  `RegexTimeout` (a prepared script ignores the constraint and gives its literals 10 s); and a guard on `RegExp.prototype.exec`,
  `test`, `[Symbol.match]`, `[Symbol.matchAll]`, `[Symbol.replace]`, `[Symbol.search]` and `[Symbol.split]` that re-creates the
  .NET regex of a `RegExp` built at run time (`new RegExp(...)`, or the one `'...'.match(string)` builds), which Jint always
  gives a 5 s timeout. The guard also checks the engine's constraints on every call, so a built-in that matches many times
  (a global `replace`, `split`, `matchAll`) observes the time limit and both tokens between matches. A regex timeout while a
  token is cancelled surfaces as cancellation. Because `exec` is no longer the built-in, Jint takes its spec-compliant slow
  path for `replace`, `split` and `matchAll`; results are the same.
- `Intl.DateTimeFormat` is replaced by a constructor that adds `timeZone: 'UTC'` when the options give none (Jint takes the
  default from the host otherwise). Prototype, statics, `instanceof` and subclassing are unchanged; an explicit named zone
  still resolves through the host's time zone data.
- Removed globals: `ArrayBuffer`, `SharedArrayBuffer`, `DataView`, `Atomics`, all typed arrays (single large allocations),
  `WeakRef`, `FinalizationRegistry` (garbage-collector dependent) and `ShadowRealm`.
- After the scripts load, `globalThis` and everything reachable from it (every intrinsic and prototype, the `maquettiste` object,
  script-declared globals) is deep-frozen. Consequence: the strict-mode "override mistake" applies, so `obj.toString = f` on an
  ordinary object throws; use an object literal, a class or `Object.defineProperty`. Closure state (`let` at the top level, captured
  variables) cannot be frozen; helpers must be pure (D12).

A limit breach, a cancellation or a host failure marks the engine faulted, and the pool disposes it on return and builds a fresh
one when needed.

## Script API

Scripts run in load order against a global `maquettiste`:

| Call | Invoked as | Returns |
| --- | --- | --- |
| `helper(name, fn)` | `fn(...args)` | any JSON-like value or model object |
| `selector(name, fn)` | `fn(model)` | an array (or array-like) of model objects or id strings → `IReadOnlyList<string>` |
| `filter(name, fn)` | `fn(element, model)` | truthy/falsy → `bool` |
| `transform(name, fn)` | `fn(element, model)` | a plain object (or `null`/`undefined` for none) → `IReadOnlyDictionary<string, object?>` |
| `rule({ id, severity, kinds, check })` | `check(element, model, report)` | nothing; `report(message, { pointer, severity })` |

- Names are unique per kind (a helper and a filter may share a name); registering after loading is a `TypeError`.
- `maquettiste.params` is a read-only accessor returning the current call's `ScriptCallContext.Parameters` (frozen; empty while
  loading). Section 10 does not say how scripts see parameters; this is the choice made here.
- `rule`: `id` has no whitespace (the diagnostic rule is `x/<id>`); `severity` is `error` (default), `warning` or `info`; `kinds`
  lists kind names from `KindInfo` (empty or missing = all kinds; `RunRule` returns no diagnostics for other kinds). `element` is
  a deep-frozen copy of the element's canonical JSON; `model` offers `get(id)` (an element, or a sub-element found through its
  index pointer), `all(kind)` (ordinal by name, then id) and `referencesTo(id)` (`{ fromElementId, fromId, jsonPointer, field, toId }`),
  all frozen. Report diagnostics carry the element id, the element file path and the pointer; W2 fills line and column.

## Model view

`JsModelProxy` exposes the public properties of resolved-model types (`Maquettiste.Engine.Resolution`), their helper types and
`GenerationHints` in camelCase; `Dependencies` is hidden, and the `ResolvedModel` shows only its lists plus `find(id)`.
Conceptual objects add `hasStereotype(key)` and `hasTag(key)`. Proxies are cached per engine, so identity holds
(`model.find(e.id) === e`). `Set`, `DefineOwnProperty`, `Delete` and `setPrototypeOf` refuse (a `TypeError` in strict mode);
`Object.isFrozen(proxy)` is true.

Dependency recording matches the Scriban accessor: every member read, `in` test or key enumeration of an `IResolvedObject`
records its `Dependencies`; reading the length or an item of an `RList<T>` records its `MembershipKeys`; `find` of a missing id
records `e:<id>`. Keys go to the recorder of the call that reads them, even through proxies cached by an earlier call.

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
  interrupted; its match timeout is the bound).
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
