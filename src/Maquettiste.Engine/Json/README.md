# Json

**Owner:** W1 Loader (the scaffold delivered the first version). See docs/engineering/engine-design.md section 18.

JSON Schema registry and key layouts (`ISchemaRegistry`, `SchemaRegistry`, `ObjectLayout`), the canonical writer (`ICanonicalJson`, `CanonicalJson`), the element reader (`ElementReader`), `JsonPositionLocator` and RFC 6901 pointer helpers (`JsonPointer`).

- Implements: `ISchemaRegistry`, `ICanonicalJson` (complete: key order from `x-order`, `x-sort` arrays stable-sorted, defaults and nulls omitted with `x-default-unless` gates, `$schema` computed, LF, trailing newline, no BOM); `JsonPositionLocator` (complete).
- Consumes: the embedded `schemas/v1/*.json` (see `schemas/README.md`), `Model.EngineJson`.

`SchemaFolder` compares `<ModelRoot>/.schema/v1/*.json` with the embedded schemas byte for byte (`Status`: missing, stale and extra names) and refreshes it (`RefreshAsync`: deletes the extra files first, then stages and renames each missing or stale file, every path through `WriteTarget.Model`). `init`, the editor's start and `mcp` refresh; `SchemaFolder.Findings` builds MQ1008 for `validate` and `generate`.

`JsonPositionLocator.Locate(bytes, pointer)` re-reads the bytes with `Utf8JsonReader` only when a diagnostic needs a position and returns the 1-based line and column of the pointer's value (lines by LF, columns in characters, a BOM skipped); `FromException` converts a `JsonException` position the same way. The loader, the store and the batch parser use it for MQ1001, MQ1002, MQ1004, MQ1006 and MQ1007. `CanonicalJson.IsCanonical` returns `false` (never throws) for bytes that are not UTF-8 or that repeat a property name.

Tests: `tests/Maquettiste.Engine.Tests/Json/`.

## Performance notes (WP, round 2)

- **Schema precheck** (`SchemaPrecheck`): `SchemaRegistry.Evaluate` first runs a compiled, allocation-light checker for the
  keyword subset the embedded schemas use (`type`, `enum`/`const`, `$ref`, `properties`, `additionalProperties`, `required`,
  `propertyNames`, `dependentRequired`, `items`/`prefixItems`, `min`/`maxItems`, `uniqueItems`, `min`/`maxLength`, `pattern`,
  integer bounds, `allOf`/`anyOf`/`oneOf`/`not`/`if`-`then`-`else`). It answers valid, invalid or undecided; only **valid** skips
  JsonSchema.Net, so every MQ1002 message and pointer still comes from the full evaluator. Soundness rules: an unknown keyword makes
  its schema undecidable; `pattern` is decided only on printable ASCII; string lengths only when UTF-16 units, code points and text
  elements agree; numbers only as 64-bit integer literals; a repeated property name is undecidable. Invalid is used only where it is
  certain too (`oneOf`, `not`, `if` turn it into validity). On the benchmark model all 26,267 files are decided valid in about 0.1 s
  single-threaded instead of 2.6 s and 3.7 GB of allocation. `SchemaPrecheckTests` compares it with the full evaluator on every
  fixture document, on seeded random mutations of them (thousands of documents) and on dialect edge cases.
- **Element-based canonical check** (`CanonicalCheck`): the loader asks `CanonicalJson.IsCanonical(bytes, root, ...)` with the
  document it already parsed; the canonical form is written from the `JsonElement` tree into a buffer writer that compares each
  chunk with the file bytes (no `JsonNode` tree, no output buffer). Where the node writer's default comparison could differ (numbers
  that are not both integer literals, non-empty object or array defaults) it answers undecided and the node-based check
  (`IsCanonicalByNodes`) decides, so the verdict is always the old one; `CanonicalCheckTests` compares both on fixtures, canonical
  rewrites and random mutations. The public `IsCanonical(bytes, ...)` parses once into a `JsonDocument` and uses the same path.

## Performance notes (WB, one-shot CLI)

- `SchemaRegistry` keeps two lazies: the embedded files (names, ordinal, and bytes), and the built state (JsonSchema.Net schemas,
  layouts, precheck), built from the files on the first `Evaluate`, `GetLayout` or precheck. `FileNames` and `GetFileBytes` (the
  index cache's schema-set hash, `init`) read only the files, so a one-shot process whose model files all come from trusted index-cache
  records no longer builds every schema before loading (about 110 to 120 ms in a fresh process; single instrumented runs, indicative,
  see `Loading/README.md`). Same names, bytes, schemas and layouts.

