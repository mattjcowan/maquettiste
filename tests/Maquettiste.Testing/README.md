# Maquettiste.Testing

**Owner:** Scaffold. See docs/engineering/engine-design.md section 18.

Shared test helpers, with no test-framework dependency: `ModelBuilder` (fluent, in-memory; `Build`, `BuildDocuments`, `WriteToAsync`), `SequentialIdGenerator`, `TempRepo`, `Golden`, `Fixtures`, `TestServices`, `TestLimits` (sandbox limits that keep a test's outcome off the wall clock).

- Implements: complete.
- Consumes: the engine internals (`InternalsVisibleTo`).

Every test project references it.
