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
| `reference-data` | MQ2001 and MQ2002 (storage keys), MQ3019 (default codes), MQ7001, MQ7002, MQ7006 to MQ7008, MQ7010, MQ7011 (settings findings land on `maquettiste.json`) |
| `seeds` | MQ7002 to MQ7005, MQ7009, MQ7101 to MQ7103, MQ7105, MQ7106 |

Rules the schemas already block in files (MQ3014 scalar base, MQ3018 names, MQ3010 `min`) and MQ3020 (a save rule) are tested
with `ModelBuilder` in `ModelValidatorTests` and `RuleHelperTests`. The schemas also block a reference type's code type (MQ7010)
and duplicate seed columns (MQ7005); the rules stay as a guard. MQ7104 (bulk seeds) is tested in `ReferenceDataStoreTests`, which
writes a 10,001-row seed. The domain-vocabulary rules MQ2008 (a tag or category declared only outside the element's chain) and
MQ3021 (a domain vocabulary repeats a key or name of the global or an enclosing vocabulary), and MQ1009 per scope, are tested with
`ModelBuilder` in `DomainVocabularyTests`, including the scope a save validates. MQ7012 (the retired enum `lookup` storage) is a
load-time rule: a file that sets it is not schema-valid and cannot be a fixture here, so it is tested in `SchemaValidationTests`.

`ValidationFixtureTests` compares each report with `expected.json`; run the tests with `MAQUETTISTE_UPDATE_GOLDEN=1` to rewrite
them, then review the diff. The tests also pin the set of rule ids per family, so a rewrite cannot silently drop a rule.
