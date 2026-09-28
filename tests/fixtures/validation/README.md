# validation fixtures

**Owner:** W2 Validator.

One model repo per rule family. Each folder holds `.maquettiste/` (a `maquettiste.json`, `model/**`, and for some families
`extensions/*.json` and `extensions/rules/*.js`) and `expected.json`, the golden list of diagnostics the validator reports
(serialized with web JSON defaults). Every file is schema-valid, so only semantic rules fire.

| Folder | Rules |
| --- | --- |
| `clean` | none: a model using every element kind; the report must be empty |
| `references` | MQ2001 to MQ2007 |
| `cycles` | MQ3002, MQ3003, MQ3004, MQ3015 |
| `names-keys` | MQ3001, MQ3005, MQ3006, MQ3007 |
| `relations` | MQ3001, MQ3008 to MQ3011, MQ3016 |
| `values` | MQ3012, MQ3013, MQ3017, MQ3019 |
| `physical` | MQ2001, MQ4001 to MQ4008, MQ4010 |
| `mappings` | MQ4004, MQ4009, MQ4011 |
| `extensions` | MQ5001, MQ5004 |
| `scripts` | MQ2007, MQ5002, MQ5003, `x/no-draft` (with the test's fake sandbox) |

Rules the schemas already block in files (MQ3014 scalar base, MQ3018 names, MQ3010 `min`) and MQ3020 (a save rule) are tested
with `ModelBuilder` in `ModelValidatorTests` and `RuleHelperTests`.

`ValidationFixtureTests` compares each report with `expected.json`; run the tests with `MAQUETTISTE_UPDATE_GOLDEN=1` to rewrite
them, then review the diff. The tests also pin the set of rule ids per family, so a rewrite cannot silently drop a rule.
