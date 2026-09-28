# maquettiste
Visual designer for entities, relations, processes and databases with code generation capabilities

## Building

Requires the .NET SDK pinned in `global.json` (10.0.109).

```sh
dotnet build maquettiste.slnx   # warnings are errors
dotnet test maquettiste.slnx
```

`SPEC.md` is the specification; `docs/engineering/engine-design.md` is the phase 1 engine contract and
`docs/engineering/host-contracts.md` what the editor host requires of the engine.
