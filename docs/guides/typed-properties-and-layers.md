# Typed properties and layers

As a model grows, the loose parts of it start to matter: a custom property that holds an attribute's name breaks when
the attribute is renamed, two stereotypes that should never meet end up on one entity, a tag carries a value nobody
checks, and a domain that should stand alone starts to lean on another. This guide tightens the Spoke & Chain model
with the tools for each: typed links (`x-ref`), stereotype rules and parameters, tags turned into a property, package
layers (`dependsOn`), translatable property texts, and the rationale and status every element can carry.

You need the Spoke & Chain model from the earlier guides, with its Fleet, Membership, Rentals and Workshop domains.
Until the editor's forms show these keys, edit them on the inspector's **JSON** tab, or in the files under
`.maquettiste/`; the custom property files are under the **Extensions** node of the Generate explorer. The batches
below run from a terminal with `maquettiste model apply`, or from an agent with `apply_batch`.

## 1. Type a link with `x-ref`

The generated list screens should sort each entity by an attribute of its own. Add the custom property file
`.maquettiste/extensions/listing.json`:

```json
{
  "name": "listing",
  "description": "How the generated screens list an entity.",
  "appliesTo": { "kinds": ["entity"] },
  "properties": {
    "sortBy": { "type": "string", "x-ref": { "kind": "attribute", "of": "$element", "optional": true } },
    "emptyText": { "type": "string", "x-translatable": true }
  }
}
```

1. `"x-ref": { "kind": "attribute" }` says that `sortBy` holds the id of an attribute, never its name.
2. `"of": "$element"` keeps the choice to the attributes of the entity that holds the value (its base entities and
   stereotypes included), so `Station` cannot sort by an attribute of `Bike`.
3. `"optional": true` lets a delete with `remove-references` clear the value when the attribute goes.

Because the value is an id, renaming the attribute changes nothing in the property, and the bottom panel's
**References** tab (`get_references` for an agent) lists the value with the schema that types it. A value that names
something of another kind, or out of scope, is reported (MQ2002, MQ2011), as is a malformed `x-ref` in the file itself
(MQ2009). Step 6 comes back to `emptyText`.

## 2. Turn names into ids

Someone opens **Station** and types the attribute's name, as people do:

```json
"properties": { "sortBy": "name" }
```

Validation reports MQ2010 at the value: `sortBy` holds `name`, which is a name, not an id. Rather than looking up the
id, let the engine convert every such value at once. Save this batch as `names-to-ids.json`:

```json
{ "operations": [ { "op": "names-to-ids" } ] }
```

1. Run `maquettiste model apply names-to-ids.json --dry-run`. Nothing is written: the output shows the diff of
   `station.json`, where `"name"` becomes the id of Station's `name` attribute, and the diagnostics the change removes
   (the MQ2010).
2. Run it again without `--dry-run` to write it.

The operation looks at every document with typed values, stereotype defaults included; `"elementIds": [...]` limits it
to some elements. A name that finds nothing, or more than one target, stays MQ2010 and the whole batch writes nothing.
A value that is already an id is left alone, so running it twice writes nothing the second time.

## 3. Give a stereotype rules and parameters

Two of the showcase's stereotypes should never meet: a `lookup` list maintained by staff is not the entry point of an
aggregate. And `audited` entities need a retention period that validation checks. Edit the two stereotypes under
`.maquettiste/model/vocabularies/stereotypes/` and add the keys below; every key already in the files (`id`,
`description`, `audited`'s `attributes` and the rest) stays as it is. Add to `lookup.json`:

```json
"conflicts": ["aggregate-root"]
```

And add to `audited.json`:

```json
"parameters": {
  "properties": { "retentionDays": { "type": "integer", "minimum": 1 } },
  "required": ["retentionDays"]
},
"defaultProperties": { "retentionDays": 2555 }
```

1. Add `aggregate-root` to **Tariff**, which carries `lookup`: validation reports MQ2015 at the later stereotype's
   entry. Take it off again.
2. Every audited entity now has `retentionDays`: its own value in `properties`, or the stereotype's default of 2555
   days. A value below 1 is MQ2018 at `/properties/retentionDays`, and templates read the merged value as
   `entity.properties.retentionDays`.

`group` keeps alternatives apart (one stereotype per group on an element), `requires` asks for another stereotype, and
`implies` applies others along with this one, one level deep. MCP `get_schema` lists the parameters of the stereotypes
that may apply to a kind, so an agent builds valid properties.

## 4. Turn tags into a property

Before `retentionDays` existed, the team marked **Payment** with the tag `retention:3650` (declared in the tag
vocabulary) to say its rows are kept ten years. Now that the property exists, the value belongs there, where it is
checked. Save this batch as `retention.json`:

```json
{ "operations": [ { "op": "tags-to-property", "prefix": "retention:", "name": "retentionDays" } ] }
```

1. Run `maquettiste model apply retention.json --dry-run`. The diff takes `retention:3650` off **Payment**, writes
   `"retentionDays": 3650` into its `properties` (an integer, because the audited parameter says so), and removes the
   tag's declaration from the vocabulary.
2. Run it without `--dry-run`.

Every element and sub-element that holds a tag starting with the prefix gets the property, converted by the schema that
declares it: an extension file, or the parameters of a stereotype the holder carries. When one holder cannot take its
value (no schema declares the property, the suffix does not fit, or the holder already holds another value), nothing is
written and that holder is MQ5001.

## 5. Declare layers

The four domains have a direction: Fleet and Membership stand alone, Rentals rents bikes from Fleet to members, and the
Workshop repairs Fleet's bikes. Say so by adding `dependsOn` to each package file under
`.maquettiste/model/packages/`, as a list of package ids; the other keys stay as they are:

| Package | `dependsOn` |
| --- | --- |
| Fleet | `[]` |
| Membership | `[]` |
| Rentals | Fleet, Membership |
| Workshop | Fleet |

For Workshop, add to `workshop.json`:

```json
"dependsOn": ["<id of Fleet>"]
```

1. Validate. The relation **follows up** in Workshop joins a **DamageReport** of Rentals to a **RepairJob**, and
   Workshop does not depend on Rentals: MQ2020, an error at the relation's end.
2. Decide which way is right. Here the workshop does follow up damage reports, so add Rentals to Workshop's
   `dependsOn`; validation passes.

A package is checked once it or a parent declares `dependsOn`, even as `[]`, so a model without any behaves as before.
Every reference counts (attribute types, relation ends, `base`, stereotype keys, `x-ref` values, a seed's target)
except membership, categories, diagrams and elements in no package. A `dependsOn` that names its own package or forms a
cycle is MQ2021. In the editor and over MCP a save that would break a layer is refused, so the layers hold once they
pass.

## 6. Translate a property's text

`emptyText` from step 1 is marked `"x-translatable": true`, so each locale can translate it like a display name. Give
**Station** the text `"No stations yet."` in its `properties`, then:

1. Run `maquettiste l10n status`. Fleet's French shard now counts one more text to translate.
2. Translate it in Station's **Translations** section, in the translation queue, or with the `l10n` exports, where its
   field is `properties/emptyText`.

Templates read a locale's text with `translate(entity, "properties/emptyText")`, which falls back to the element's own
value. Only a top-level string property can be translatable (MQ7212 elsewhere).

## 7. Say why, and retire

Every element, and every attribute, enum member, column, database schema and category, can carry three optional
fields after its `description`: `rationale` (Markdown), `decision` (a URL or a reference such as `ADR-0042`) and
`status` (`proposed`, `approved`, `deprecated` or `retired`).

The cooperative now records a bike's condition from its latest inspection, so the **BikeCondition** enum goes out of
use. Open it and add:

```json
"rationale": "A bike's condition now comes from its latest inspection.",
"decision": "ADR-0007",
"status": "retired"
```

1. Validate. **Bike**'s `condition` attribute still uses the enum: MQ2022, a warning at the attribute's type. Retiring
   something lists its last users.
2. Repoint `condition`, or retire the attribute too; a retired element's own references do not warn.

`deprecated` means still in use but on its way out, and does not warn. An agent with the modeling skill reads
`rationale` and `status` before it edits an element. None of the three fields changes a database, so editing them
records no migration fact.

## See also

- [Custom properties](../user-guide.md#custom-properties-extensionsjson) in the user guide, with
  [typed links](../user-guide.md#typed-links-x-ref) and
  [translatable properties](../user-guide.md#translatable-properties-x-translatable).
- [Stereotype rules and parameters](../user-guide.md#stereotype-rules-and-parameters).
- [Packages and layering](../user-guide.md#packages-and-layering).
- [Rationale, decision and status](../user-guide.md#rationale-decision-and-status).
- [Translating the model in the editor](../user-guide.md#translating-the-model-in-the-editor).
- [Batches by name](../mcp.md#batches-by-name), for `model apply` and `apply_batch`.
- [Work with an agent](work-with-an-agent.md).
