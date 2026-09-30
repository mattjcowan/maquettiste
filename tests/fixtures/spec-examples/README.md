# SPEC examples

The example files of SPEC Section 6 (`invoice.entity.json`) and Section 7 (`membership.relation.json`), adapted to the
phase 1 schemas:

- Ids are replaced by valid ULIDs (the SPEC ids are illustrative and several contain `I`, `L`, `O` or `U`, or have 27
  characters; decision D1). Letters were transliterated (`I`/`L` → `1`, `O` → `0`, `U` → `V`) and the result cut to 26.
- The entity's `lifecycle` field is removed: it belongs to the phase 3 process schema (D7).
  `invoice-with-lifecycle.entity.json` keeps it, and since phase 3 (erratum E3 retired) it is valid.
- The relation ends carry an `id`, which the engine needs for physical column keys (engine-design.md section 7.3).

`dangling-type.entity.json` uses `"type": "Money"`, a name that is neither a built-in keyword nor a `{ "ref": … }`,
and must be rejected by the schema.

`verbatim/` holds both examples exactly as the SPEC prints them. They must fail validation only for the three adaptations
above (`docs/engineering/spec-errata.md` E1 to E3); a test asserts the exact set of failing pointers.
