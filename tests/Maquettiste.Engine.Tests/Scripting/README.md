# Scripting tests (W4)

Tests for the Jint sandbox in `src/Maquettiste.Engine/Scripting/` (engine-design.md section 10). They build resolved objects by
hand (`ResolvedFixture`) and element documents with `ModelBuilder`, so they need neither the resolver nor the loader.

- `SandboxRestrictionTests`: no CLR, host, module or file access; no string compilation; statement, time, recursion and memory
  limits (MQ6007) that `try`/`catch` cannot swallow; a catastrophic regular expression (literal, `new RegExp`, and through
  `match`, `search`, `replace`, `split`, `matchAll`, the `v` and `u` flags) stops within one second under the default limits
  and within one second of cancelling the call; frozen globals; a faulted engine is replaced.
- `SandboxRuntimeTests`: a runaway script stops within one second of cancelling the call token or the pool's run token;
  determinism (fixed clock, seeded `Math.random`, host-culture independence, UTC as the default zone of `Intl.DateTimeFormat`
  and `Temporal.Now`, same output twice); pooling under parallel use. The time zone test can only fail on a host whose zone
  is not UTC (run it with `TZ=Asia/Tokyo` to check it); CI runners use UTC. The parallel pooling test holds its first lease
  until a second one is taken, so it checks distinct engines however a busy machine schedules the iterations.
- `JsModelProxyTests`: read-only proxies, dependency recording (object keys, list membership keys, `find` of a missing id),
  write refusal, and how values cross the boundary: mutable CLR arguments and parameters are read afresh on every call,
  resolved lists keep their identity, a huge sparse array result is a limit error before the host allocates it, and a `Map`,
  `Set`, `RegExp`, `Error` or class instance result is a script error.
- `LargeModelWalkTests`: a script walking every item of a 5,000-item list whose membership has 20,000 keys (the packs' walk of
  a database's tables) stays well inside the time limit and records exactly the keys read, again in a second call.
- `RegistrationTests`: the `maquettiste` registration API, validation rules and their diagnostics (MQ5002, MQ5003, `x/<id>`),
  script errors mapped to diagnostics with file, line and column, and which paths count as rule scripts (only files directly
  in the model's `extensions/rules/`).
