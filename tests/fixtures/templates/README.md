# Template fixtures

**Owner:** W5 Renderer.

- `billing-demo/`: a pack exercising the renderer over `tests/fixtures/models/billing`: every built-in helper (`helpers.scriban`),
  a `pair` entity unit with a partial and a transform, a `where.database` table unit, file blocks (`registrations.scriban`),
  JavaScript helpers (`helpers.js`), a `types/csharp.json` type map, and two custom-delimiter templates (`<% %>` Handlebars-like,
  `[% %]` JSX-like) with raw blocks.
- `golden/billing-demo/`: its expected output (under the pack output folder `out/`). Rewrite with `MAQUETTISTE_UPDATE_GOLDEN=1`.
