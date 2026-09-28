# Generation tests

W6's orchestration tests run the real loader, validator, resolver, pack loader, planner, change detector, post-processor, writer,
manifest, journal and run lock over temporary repos, with `FakeRenderer` in place of the Scriban renderer (being built
concurrently). `FakeRenderer` honors the `IRenderer` and `RenderContext` contracts: it streams lazily in input order, records
reads through an `IReadRecorder` as the tracking context does (resolved objects' `Dependencies`, `RList` membership keys,
`t:` template keys) and computes input hashes with `RenderContext.Hasher`. Fixture packs live in `tests/fixtures/packs/`.

The list of end-to-end tests the integration stage should add with the real renderer is in
`src/Maquettiste.Engine/Generation/README.md` ("Integration stage").
