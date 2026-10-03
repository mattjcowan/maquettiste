# fixtures

**Owner:** Shared; one subfolder per owning area. See docs/engineering/engine-design.md section 18.

Fixture repos and files. `spec-examples/` (scaffold) holds the SPEC Section 6 and 7 examples adapted to the phase 1 schemas. Planned: `models/` (W1), `validation/` (W2), `templates/` (W5), `golden/` (W10). `skills/` holds the modeling skill exactly as
an earlier release's `init --skill` wrote it, for the refresh tests of `AgentSetupTests`.

Locate fixtures with `Maquettiste.Testing.Fixtures.Path(...)`.
