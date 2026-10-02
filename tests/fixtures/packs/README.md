# packs

**Owner:** W6 Planner and orchestration. See docs/engineering/engine-design.md section 18.

Template packs for the planner, orchestration and job tests (`tests/Maquettiste.Engine.Tests/Planning/`, `Generation/`, `Jobs/`).
Tests copy a pack into a temporary repo's `.maquettiste/templates/<name>/`.

The templates are written for the tests' fake renderer (`Generation/FakeRenderer.cs`), not for Scriban: `{{name}}`, `{{id}}`,
`{{attributes}}`, `{{table}}`, `{{entities}}`, `{{param:<name>}}` and `{{fail}}` are placeholders that the fake renderer
substitutes while recording reads through `IReadRecorder` as the real renderer's tracking context does, and a line
`@@file <path>` starts a file block. Real Scriban packs and their end-to-end tests arrive in the integration stage.

| Pack | Units |
| --- | --- |
| `basic` | `entity` (each entity → `out/entities/<name>.txt`), `index` (model → `out/index.txt`) |
| `scripted` | a JavaScript selector (`select audited`) and a JavaScript `where.script` filter, over `helpers.js` |
| `modes` | `once`, `pair` (with a companion), `regions`, and a unit with file blocks only |
