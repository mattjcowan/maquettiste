# Share and test packs

Two packs that open their files with the same header, or name their classes the same way, should not each keep a copy.
This guide moves that shared part of the billing example into a **library pack**, uses it from two packs and from a
project rule, and watches what renders again when the library changes.

## A few words first

| Word | Meaning |
| --- | --- |
| Library pack | A pack without units. It holds partials and helpers that other packs use. |
| `uses` | The list in `pack.json` (or `maquettiste.json`) of the packs a pack uses, each with a version range. |
| Namespace | How a template reaches a used pack's helpers: the pack name, with `-` written `_` (`naming.class_name`). |
| Prefixed include | `{{ include "naming:partials/header.scriban" }}`: a partial of a used pack. |

## 1. Make the library

```bash
maquettiste pack new naming --library
```

This writes `.maquettiste/templates/naming/` with a `pack.json` that has no units, `partials/header.scriban` and a
`helpers.js` with one helper. Give the library what the two packs share. In `helpers.js`:

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
