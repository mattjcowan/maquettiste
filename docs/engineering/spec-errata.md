# SPEC errata

Places where `SPEC.md` and the phase 1 contract (`engine-design.md`, the schemas under `schemas/v1/`) disagree on purpose.
Each entry is a proposed change to the SPEC text, so the authority matches what the engine accepts. Until the SPEC is
amended, `engine-design.md` decides, and the decision named in each entry records why.

`tests/Maquettiste.Engine.Tests/Json/SchemaValidationTests.cs` loads the Section 6 and 7 examples verbatim
(`tests/fixtures/spec-examples/verbatim/`) and asserts that they fail only for E1 to E3, so no other drift can hide
behind them.

| Id | SPEC | Proposed change | Decision |
| --- | --- | --- | --- |
| E1 | Sections 6 and 7, example files | Replace the illustrative ids with valid ULIDs: several have 27 characters or contain `I`, `L`, `O` or `U` (for example `01JAX4R0MEMBERSHIPRELATION1`, `01JAX3N0RECEIVABLESCAT0001`) | D1 |
| E2 | Section 7, end table and example | Add an `id` row to the end table ("stable id of the end; used by physical column keys and mappings") and an `id` to each example end | D33 |
| E3 | Section 6, example entity | Remove `"lifecycle"` from the example, or mark it as a phase 3 field; phase 1 schemas reject it | D7 |
| E4 | Section 5 (`id`) and Section 11 (identity and references) | "Every reference between elements uses the id" gains one exception: `stereotypes` lists hold the stereotype's immutable `key` (a field separate from its renamable `name`), as the Section 6 example already does | D2 |
| E5 | Section 6, attribute table | Split "`default`: literal or named expression such as `now`" into `default` (a literal of the attribute's type) and `defaultExpression` (`now`, `today`, `new-uuid`, `new-ulid` or a pack-defined name) | D4 |
