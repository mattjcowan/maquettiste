# Rendering tests

**Owner:** W5 Renderer. Tests of `src/Maquettiste.Engine/Rendering/` (engine-design.md sections 8 and 9).

- `RenderKit.cs`: loads and resolves the billing fixture (`tests/fixtures/models/billing`) once per process through the real
  loader and resolver, loads pack folders into `LoadedPack`s, plans units the simple way (the real planner is W6's), a test
  `IDependencyHasher`, and `Adhoc` for one-template packs in a temp folder.
- `GoldenRenderTests`: the `billing-demo` pack over the billing fixture against `tests/fixtures/templates/golden/billing-demo`
  (every helper, pack helpers and a transform through the real sandbox, a partial, pair mode, file blocks, Handlebars-like and
  JSX-like custom delimiters). Rewrite with `MAQUETTISTE_UPDATE_GOLDEN=1`.
- `ReadTrackingTests`: which element and list-membership keys a template records, including `schema_diff` hits, misses and
  enumeration (`k:database`).
- `DelimiterTranslatorTests`: translation, raw blocks, strings and comments, whitespace control, position mapping.
- `FileBlockAndLimitTests`: file blocks, MQ6011, pair companions, loop and recursion limits (MQ6007).
- `SandboxingTests`: hidden members, read-only model/variables/builtins, MQ6012, include confinement (symbolic links included),
  error locations, LF output.
- `ScriptHelperTests`: JavaScript helpers, transforms, script errors and limits, MQ6013.
- `DeterminismTests`: tr-TR / de-DE / invariant byte identity, 1 versus 8 workers, one cache and one pool shared by 200 units,
  cancellation, `RenderOneAsync`.
- `TemplateCacheAndHelperTests`: the cache, path normalization, and the pure helper functions.
- `RobustnessTests`: cancellation in the middle of a unit (`RenderOneAsync` and `RenderAsync`), UTC date builtins, a catastrophic
  regular expression (MQ6007, fast), bounded `indent`. The date cases pass in any time zone only because of the wrappers; CI runs
  in UTC, so run them once with `TZ=Asia/Tokyo` after changing the date helpers.
