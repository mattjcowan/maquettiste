# schemas

**Owner:** W1 Loader (the scaffold delivered v1). See docs/engineering/engine-design.md section 18.

`v1/*.json`: one JSON Schema (draft 2020-12) per document kind, plus `common.json` (shared `$defs`), `batch.json` and `diagnostics.json`. Each object schema with `properties` has an `x-order` list naming exactly those keys; it is the only place key order and defaults live (engine-design.md section 3).

- Implements: the schemas, embedded into the engine as `Maquettiste.Engine.Schemas.v1.<file>`.
- Consumes: nothing.

No schema has `$id`; `$ref`s are relative (`common.json#/$defs/attribute`); the engine resolves them under `https://maquettiste.invalid/schemas/v1/` (D24). `SchemaConsistencyTests` fail the build when an `x-order` list, a record property or a default drifts.
