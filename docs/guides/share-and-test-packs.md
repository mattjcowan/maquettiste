# Share and test packs

Two packs that open their files with the same header, or name their classes the same way, should not each keep a copy.
This guide moves that shared part of the billing example into a **library pack**, uses it from two packs and from a
project rule, watches what renders again when the library changes, and gives the packs tests of their own.

## A few words first

| Word | Meaning |
| --- | --- |
| Library pack | A pack without units. It holds partials and helpers that other packs use. |
| `uses` | The list in `pack.json` (or `maquettiste.json`) of the packs a pack uses, each with a version range. |
| Namespace | How a template reaches a used pack's helpers: the pack name, with `-` written `_` (`naming.class_name`). |
| Prefixed include | `{{ include "naming:partials/header.scriban" }}`: a partial of a used pack. |
| Case | A folder under a pack's `tests/`: a small project and `expected/`, the files the pack must write for it. |
| Helper test | A `maquettiste.test(...)` in a pack's `tests/*.test.js`, calling the pack's helpers with plain values. |

## 1. Make the library

```bash
maquettiste pack new naming --library
```

This writes `.maquettiste/templates/naming/` with a `pack.json` that has no units, `partials/header.scriban`, a
`helpers.js` with one helper and `tests/helpers.test.js`, a test of that helper (section 7 replaces it). Give the
library what the two packs share. In `helpers.js`:

```js
function pascal(text) {
  return String(text).replace(/(^|[-_ ])([a-zA-Z])/g, (_, __, c) => c.toUpperCase());
}

maquettiste.helper('class_name', (element) => pascal(element.name) + maquettiste.params.suffix);
```

In `pack.json`, set `"version": "1.4.0"` and a default for the parameter: `"parameters": { "suffix": "Record" }`. In
`partials/header.scriban`:

```scriban
// {{ naming.class_name element }}
// Generated from the billing model; do not edit.
```

Until a pack uses it, validation warns with MQ6046: a pack with no units that nothing uses.

## 2. Use it from two packs

The `records` pack writes one class per entity. Its `pack.json` lists the library with a range and names its files
with the library's helper:

```json
{
  "name": "records",
  "version": "1.0.0",
  "engine": ">=1.0 <2.0",
  "uses": [ { "pack": "naming", "version": "^1.2.0" } ],
  "units": [
    {
      "id": "record",
      "template": "record.scriban",
      "for": "each entity",
      "output": "records/{{ entity.package.name }}/{{ naming.class_name entity }}.cs"
    }
  ]
}
```

`record.scriban` opens with the library's header:

```scriban
{{ include "naming:partials/header.scriban" }}
public sealed class {{ naming.class_name entity }}
{
}
```

A `services` pack does the same with `{{ naming.class_name entity }}Service`. Give both an output base
(`packs.records.output` and `packs.services.output`, say `src/Generated`), then plan and apply. Invoice gets
`records/Billing/InvoiceRecord.cs` and `services/Billing/InvoiceRecordService.cs`, both opening with the same two
header lines; Product lands under `Catalog/`.

The partial renders with the including unit's variables, so `element` is the entity being rendered. The helper runs
with the library's own parameters: `packs.naming.parameters.suffix` in `maquettiste.json` changes it for every pack
that uses the library.

## Read another pack's names: exports

The records pack states each class name once, per entity, and the services pack reads it. Put `exports` on the
records unit, and name the file with it:

```json
{ "id": "record", "template": "record.scriban", "for": "each entity",
  "output": "{{ entity.package.name }}/{{ exports.class_name }}.cs",
  "exports": {
    "class_name": "{{ pascal entity.name }}Record",
    "namespace": "{{ project.properties.baseNamespace }}.{{ entity.package.name }}",
    "members": { "each": "attributes", "value": "{{ pascal item.name }}" } } }
```

List `records` in the services pack's `uses`, then read the record with `exported`:

```scriban
{{ r = entity | exported "records" }}
public sealed class {{ pascal entity.name }}Service
{
    public {{ r.namespace }}.{{ r.class_name }} Load(Guid id) => ...;
{{ for a in entity.attributes }}    // {{ r.members[a.id] }}
{{ end }}}
```

Change `class_name` to `{{ pascal entity.name }}Row` and one plan renames every record file and updates every service
that reads the name; the units that read no exports are skipped. `generate --pack services` evaluates the records
pack's exports without writing its files. To check the names before a run, use
`maquettiste pack exports records --element <id>` or the MCP tool `get_pack_exports`.

## 3. Reach the library from a project rule

List the library at the top of `maquettiste.json`, with a range of its own:

```json
"uses": [ { "pack": "naming", "version": "^1.0.0" } ]
```

A rule in `extensions/rules/class-names.js` can now call the same helper:

```js
const naming = maquettiste.use('naming');

maquettiste.rule({
  id: 'class-name-length',
  severity: 'error',
  kinds: ['entity'],
  check(element, model, report) {
    const name = naming.class_name(element);
    if (name.length > 20)
      report(`The class name ${name} has ${name.length} characters; keep it to 20.`, { pointer: '/name' });
  },
});
```

Validation is clean: the longest names, `CustomerNoteRecord` and `RevenueMonthRecord`, have 18 characters.

## 4. Change the library and see what renders again

Set `packs.naming.parameters.suffix` to `RecordEntry` and plan. Every entity unit of `records` and `services` renders
again, since the library's parameters are part of each unit's inputs, and nothing else does. Validation runs the rule
again too and reports `x/class-name-length` on InvoiceLine, InvoiceNote, CustomerNote and RevenueMonth (22 and 23
characters), not on Invoice (`InvoiceRecordEntry`, 18).

## 5. When a range does not match

Change `records` to use `naming` `^2.0.0`. The library is version 1.4.0, so generation stops before writing anything,
with MQ6043 at `/uses/0/version` of `records/pack.json`. Widen the range or update the library.

| Rule | When |
| --- | --- |
| MQ6042 | A `uses` entry names a pack that does not exist or did not load, or a list names a pack twice. |
| MQ6043 | The used pack's version does not satisfy the range, or the range does not parse. |
| MQ6044 | A pack uses itself, or packs use each other in a cycle. |
| MQ6045 | A used pack's namespace collides with a builtin, a template keyword or variable, or a root helper. |
| MQ6046 | Warning: a pack has no units, and no pack and no project `uses` lists it. |
| MQ6047 | Error: a template reads the exports of a pack its pack does not use, or an export template reads its own pack's exports. |
| MQ6048 | Error: two units of one pack export one name for one element with different values. |
| MQ6049 | Error: an export's `each` names a member that is not a list of items with ids, or two items share an id. |
| MQ6050 | Error: a unit reads exports that failed to evaluate; it is not rendered. |

## 6. Rename or remove a library

Renaming `naming` rewrites the `uses` entries that name it, in packs and in `maquettiste.json`, and lists the templates
and scripts whose text still names it (the prefixed includes, `naming.` calls and `maquettiste.use("naming")`); edit
those yourself. Removing it, or deleting or moving a partial another pack includes, is refused while something uses
it.

## 7. Test the packs

`records` and `naming` now hold logic that other people rely on. Tests that live in the packs catch a change before it
reaches anyone: `maquettiste pack test` runs them, from a terminal, in CI or through the MCP tool `test_pack`.

**A case for `records`.** A case is a small project of its own, in a folder whose name is lowercase letters, digits and
hyphens, starting with a letter or digit; a folder named otherwise does not run and reports MQ6053. Make
`.maquettiste/templates/records/tests/billing-small/` with a `maquettiste.json` that sends the pack's output to `out`:

```json
{ "formatVersion": 1, "name": "billing-small", "packs": { "records": { "output": "out" } } }
```

and a `model/` folder laid out like `.maquettiste/model/`, with the package `Billing` and the entities `Invoice` and
`Customer`. Copied from the project, they need what they reference copied too (their types and enums, stereotypes and
categories); small files with a key and a few attributes are often simpler, since a case needs only what the pack reads.
A case whose model does not validate fails with the run's diagnostics. Keep it small: people read a case when they
review a change. Then write its expected output and read it:

```bash
maquettiste pack test records --update
```

`tests/billing-small/expected/records/Billing/InvoiceRecord.cs` and `CustomerRecord.cs` appear, each opening with the
library's two header lines. An update records whatever the templates write, so check them before you keep them. From now
on:

```
$ maquettiste pack test records
records
  PASS case billing-small
1 passed, 0 failed
```

The case runs as a fresh project in a temporary folder, with `records` and the `naming` library it uses, and the output
must equal `expected/` byte for byte. Nothing in your project changes: not its outputs, manifests or unit states. The
case reads its own `maquettiste.json`, not the project's, so the `RecordEntry` suffix of section 4 does not reach it;
the library's default suffix does.

**Variants.** For a pack with parameters, a case can run on another case's project and change only them: a `case.json`
such as `{ "model": "billing-small", "parameters": { "suffix": "Row" } }` beside its own `expected/`, and no model. The
parameters are merged over those of the pack under test. `records` has none, and the library's come from the case's
`maquettiste.json` (`packs.naming.parameters`), so another suffix for `naming` takes a case with a project of its own.

**Helper tests for `naming`.** A library has no cases: its partials are tested through the cases of the packs that use
it, and its helpers by helper tests. Replace `naming/tests/helpers.test.js` with tests of `class_name`:

```js
maquettiste.test("class_name adds the suffix", (assert, helpers) => {
  assert.equal(helpers.class_name({ name: "invoice" }), "InvoiceRecord");
});
maquettiste.test("class_name joins words", (assert, helpers) => {
  assert.equal(helpers.class_name({ name: "invoice-line" }), "InvoiceLineRecord");
});
```

A test gets the library's default parameters (`maquettiste.params`) and no model: it passes the plain values the helper
reads. `assert` has `equal`, `deepEqual` (objects compared by key in any order), `ok`, `throws` (returns the error's
message) and `fail`.

**When something changes.** Change `class_name` to drop the suffix and run `maquettiste pack test`. Both helper tests
fail with MQ6052 at their `assert` line, and the `records` case fails with MQ6051: `InvoiceRecord.cs` and
`CustomerRecord.cs` are `missing` and `Invoice.cs` and `Customer.cs` are `added`, each with a diff. The command exits 1,
so a CI job that runs it stops there. If the change is intended, `--update` rewrites the expected files; review the diff
before you keep it.

**Line endings.** The comparison is byte for byte, so keep `expected/` away from anything that converts line endings.
Put a `.gitattributes` holding `* -text` in each pack's `tests/` folder so that version control leaves the files as they
are; a new pack starts with one.

**No links.** A symbolic link, junction or other link anywhere under a pack's `tests/` folder is refused with MQ6053
before anything there is read or changed, and none of that pack's tests run: a link could show a file outside the pack
in a diff, or let `--update` write or delete through it. Replace the link with the files it points at. An update checks
every path first, so a refused path leaves `expected/` as it was.

| Rule | When |
| --- | --- |
| MQ6051 | A case's output differs from its `expected/` tree: a file added, missing or different. |
| MQ6052 | A helper test fails: an assertion fails, the test throws or it hits a sandbox limit. |
| MQ6053 | A test is malformed: a case without a project or `expected/`, a case holding `templates/`, a case folder name that is not lowercase letters, digits and hyphens, an invalid `case.json`, cases in a pack without units, a link under `tests/`, a test file that registers anything but tests, or two tests with one name. |
