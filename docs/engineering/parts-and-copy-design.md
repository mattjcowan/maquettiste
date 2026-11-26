# The parts of a whole record, and what a copy carries over: design

**Status (2026-11-26): built for 0.27.0, not released.** Two items of [next](next.md), "Bindings, the rest", designed and built together, with
the decisions settled with the owner and the requesting team that day. It also covers "Revisions, the rest: what a copy carries over". A file
path without a project prefix is under `src/Maquettiste.Engine/`. The examples use the billing fixture or invented names.

## 1. In short

Today a composition is checked (one owner per part, MQ3016) and changes nothing else: every template that needs the tree of a record's parts
builds it again from the relations. And the built-in `copy` verb says nothing about what a copy holds; the [revisions](revisions-design.md)
block says what a revise carries over, which is the same record's next revision, not a new record.

| Layer | What changes |
| --- | --- |
| The resolved model | `entity.parts` lists a record's composition parts at every level down, with the owning end, the part end, the parent part, the depth, an order safe for foreign keys, and per database the foreign key that links a part to its owner |
| The model | An entity may carry a `copy` block: per attribute and to-one link what the copy gets (`reset`, `clear`, `original`), and per kind of part which parts it carries over (`all`, `one` chosen by a condition or a state, else the latest by a field, or `none`) |
| Code generation | `entity.copy` gives the block merged through the bases with every reference resolved, so a template writes its own copy statements (`INSERT … SELECT` with an id map). No pack changes |
| The team's system | Nothing changes until it reads the new members |

**A project that uses none of this** keeps its files, findings and output. Its records gain `parts` on each entity that owns a composition; a
template that doesn't read it renders as before.

## 2. The parts (resolved model)

`entity.parts` (`Model/PartTree.cs`, `Resolution/ResolveRun.Parts.cs`):

- **What is a part.** A composition (`relationKind: "composition"`, two ends, one with max 1) whose owner end covers the entity: it names the
  entity or a base of it, or it has several types and covers it. Then the parts of each part, down every level.
- **Order safe for foreign keys.** Owners before their parts. Among the parts, a part after the parts its to-one links point at. For a
  two-ended relation between two parts' entities, both ends of one type, the end with max 1 is the one pointed at; when both have max 1, the
  one with min 1; else the first end. A many-to-many link orders nothing. The order is worked out from the model, the same for every database.
  **Deleting deepest first is the order reversed;** there is no second list.
- **A circle.** When to-one links go round in a circle (a line points at its tax group and the tax group at its main line), the parts of the
  circle go together, in walk order, once nothing outside it holds them back; among the parts and circles free to go, the one holding the
  earliest part in walk order goes first. That never puts a part before its owner, nor a part that only points at the circle (an adjustment
  that points at a tax group) before the circle. The work is linear in the parts and links, even where compositions with two owners multiply
  the parts. Each part on the circle has `in_cycle` true, a
  circle through the record itself included: one of its links has to be written empty and set after the rows are in. No finding: a circle is
  legal when one link is optional. A composition is never a link here, so a tree of parts of one kind is no circle.
- **A limit.** A tree stops at 10,000 parts; only compositions with two owners (MQ3016) multiply the ways down that far. A copy block on such
  an entity gets an MQ3047 warning at `/copy`, and a part entry naming a composition's part end that the cut may hide is not checked.
- **A part of its own kind.** When a part's entity is already on the way down (a folder in a folder), it is listed once with `is_recursive`
  true, and its own parts are not listed again. Its rows point at rows of the same kind; the team orders them by their own links.
- **Several types.** A composition whose **part end** has several types is left out, with what lies below it, and its end can't be named in
  a copy (MQ3047). A part of one kind keeps one meaning for `entity` and for its attribute entries. An **owner end** with several types is
  fine: the part is listed for each entity it covers, with no foreign key (that relation is stored as its mapping describes; read
  `relation.mappings[db].links`).
- **Foreign keys.** `part.foreign_keys[db]` lists the relation's foreign keys in each database where it is stored with them, through
  bindings, a mapping or projection, in table order: usually one, and one per table when each derived type of the part has a table of its
  own. Each has `columns` in the part's table and `referenced_columns`, the owner's key. Its `end` is the part end, except for a
  one-to-one composition stored with the key in the owner's table, where the part is written before its owner. A composition stored through a
  link table has none.

| Member of `RPart` | Value |
| --- | --- |
| `id` | The part end ids on the way down, joined by `/`; unique among the record's parts |
| `relation`, `owner_end`, `end` | The composition, its owner end (at the owner) and its part end (at the part) |
| `entity`, `owner` | The part's entity, and the entity that owns it here (the record's, or the parent part's) |
| `parent`, `depth` | The part above (null at depth 1), and 1, 2, … |
| `is_recursive`, `in_cycle` | As above |
| `foreign_keys` | By database name, a list of `RForeignKey`, in table order |

**Keys.** `entity.parts` keys on the files of the entity, each part's entity and their bases, their referrers, and the relations at them;
reading a part records its relation's keys, which cover its storage.

## 3. The copy (model)

```json
"copy": {
  "attributes": [ { "attribute": "<id of status>", "onCopy": "reset" }, { "attribute": "<id of copiedFrom>", "onCopy": "original" } ],
  "links": [ { "end": "<id of source>", "onCopy": "original" } ],
  "parts": [
    { "end": "<id of schedules>", "onCopy": "one", "choose": { "state": "<id of Active>", "latestBy": "<id of version>" } },
    { "end": "<id of discounts>", "onCopy": "none" },
    { "end": "<id of lines>", "onCopy": "all", "attributes": [ { "attribute": "<id of description>", "onCopy": "clear" } ] }
  ]
}
```

- **Fields.** `attributes`: `reset` (the attribute's default, else empty; on a column the database fills, the insert leaves it to the
  column's default, so the copy gets a fresh value), `clear` (empty), `original` (the id of the record it was copied from), `copy` (the
  original's value; undoes a base's entry). `links`, for a to-one end at the other side of a relation of the entity (it needs no navigation): `original` (the copy links to the record it was
  made from) or `copy`. Anything not listed is copied as it is.
- **Parts.** One entry per kind of part, by its part end at any level: `all`, `one` with `choose`, or `none`. A part the copy leaves out leaves
  its own parts out too. A part kind's `attributes` take the same words, judged on the part's entity; `original` there is the original part's id.
- **Choosing one.** `choose` holds `condition` (the expression language, on the part's own attributes) or `state` (a state of a lifecycle that
  governs the part's entity), and `latestBy` (an attribute with an order). The part that matches, the latest by `latestBy` among several that
  do; when none does, the latest of all by `latestBy`; with neither condition nor state, the latest of all. Without `latestBy`, no part is copied
  when none matches.
- **Links.** Links inside the copy point at the copies; links out of the copy stay as they were. This is the meaning, not a setting.
- **What stays as decided before.** The copy is a new record: its key is always new, made the team's way, and every lifecycle starts at its
  initial state (revisions decision 15). A `copy` trigger point that replaces the verb's built-in effect replaces this description.
- **Inheritance.** A derived entity inherits its bases' block; a nearer entry replaces a farther one for the same attribute or end.

`entity.copy` (`RCopy`) is null when neither the entity nor a base has a block. Otherwise: `attributes` and `links` (each with `on_copy` and
`from`, `copy` entries dropped), and `parts`: one `RCopyPart` per part the copy carries, in the order of `entity.parts`, with `part`, `on_copy`
(`all` or `one`), `choose` (`condition` an `RExpression`, `state` an `RState`, `latest_by` an `RAttribute`), `attributes` and `from` (null
for a kind with no entry, filled in with `all`). A kind left out is not listed, nor are its parts.

## 4. Rules

| Rule | Sev | What |
| --- | --- | --- |
| MQ3045 | E | An attribute or link entry can't stand: an attribute the record doesn't hold, a derived one or a key attribute; two entries for one attribute or end; `clear`, or `reset` with no default, on a required attribute; a link end that is not a to-one end at the other side of a two-ended relation of the entity that is not a composition (no navigation needed) |
| MQ3046 | E | `original` can't hold the original's id: the key is not one attribute, the attribute holds a list or is not typed as the key is, or a link end's type doesn't cover the entity |
| MQ3047 | E | A part entry can't stand: an end that is not a part end among the entity's parts (several types never is); two entries for one end; `one` without `choose`, `choose` beside `all` or `none`, both `condition` and `state`; `attributes` beside `none`. A warning when the parts pass 10,000, with entries beyond them unchecked |
| MQ3048 | E | A choice can't stand: a state not of a lifecycle that governs the part's entity, or a history or choice state; `latestBy` not an attribute of the part's entity, or one with no order |

The condition is an expression site of the entity (site kind "part choice", only true chooses), so MQ3101 to MQ3113 type it against the part's
attributes. It reads the part's own attributes only: no navigation, `now()`, `current_actor()`, `in_state` or `old`.

Each rule points at its key in the entity's file (`/copy/parts/0/choose/latestBy`). Each block is checked on the entity that writes it; a
derived entity holds what its bases hold. The parts depend on compositions and bases the block doesn't name, so a save or delete of a
relation or entity checks again every block whose parts reach it, before or after the change; a save of a scalar type, process, stereotype or
package checks every block (which lifecycle governs a part, the type of a key). What else an entry names (an attribute, an end, a state, an
enum member in a condition) is a reference, so its holder's save checks the block as for any reference.

## 5. Decisions

1. **The family is MQ30xx, "Model structure", from MQ3045.** Copy entries are about entities and compositions, beside MQ3016 and MQ3041; the
   ids after MQ4121 stay free for binding work.
2. **A `copy` block of its own, not `onCopy` beside `onRevise`.** A type with no revisions can be copied, and a revisions block needs levels.
   The entries reuse revisions' shape (`attribute`/`end` with one word) and its words (`copy`, `reset`, `clear`).
3. **`onCopy` on part entries too,** with `all`, `one`, `none`, so every entry reads the same way.
4. **`none` is in.** It reads naturally ("copy no discounts") and is the only way to leave out a whole kind and its parts.
5. **A flat `choose` (`condition` or `state`, plus `latestBy`), not a nested `else`.** One fallback is all the team described; a chain adds
   vocabulary nobody asked for.
6. **Part entries are one flat list keyed by the part end, at any depth.** An end names a kind of part; nesting would repeat the tree.
7. **The root's block is the one place to read.** A part's own `copy` block applies when the part is copied on its own, not as part of a whole.
8. **`copy` on an entity that is itself a part is allowed.** Copying one line keeps its owner, as every link out of a copy does.
9. **Key attributes are never listed.** A copy is a new record, and ids are made in too many ways for the model to say how.
10. **`links` holds only `original` and `copy`.** Clearing a link out of the copy is not asked for; adding `clear` later is additive. The end
    needs no navigation: the record holds the link whether or not code walks it.
11. **A circle of links is marked, not reported.** It is legal when one link is optional; `in_cycle` tells the template where to break it.
12. **No helper renders copy SQL and no pack writes copy code** (decided before this design).
13. **`reset` on a required attribute with no default is refused,** as `clear` is: both leave it empty. A column the database fills needs its
    attribute to say a default (or a default expression) to be reset.

## 6. Deferred

- Scenarios and simulation of `copy` (a copy step making new objects in a run), with "Scenarios across objects".
- The editor: no screen yet; the JSON tab edits the block.
- Parts of a part end with several types; parts that only a derived entity of a part has.
- `clear` on a link; a "copied from" link on a part kind (`links` inside a part entry).

## 7. New public names, to confirm before release

- **File keys:** `copy` on an entity, with `attributes` (`attribute`, `onCopy`: `copy`, `reset`, `clear`, `original`), `links` (`end`,
  `onCopy`: `copy`, `original`) and `parts` (`end`, `onCopy`: `all`, `one`, `none`, `choose` with `condition`, `state`, `latestBy`, and
  `attributes`).
- **Rules:** MQ3045, MQ3046, MQ3047, MQ3048.
- **Template members:** `entity.parts` (`id`, `relation`, `owner_end`, `end`, `entity`, `owner`, `parent`, `depth`, `is_recursive`,
  `in_cycle`, `foreign_keys`); `entity.copy` (`attributes` with `attribute`, `on_copy`, `from`; `links` with `end`, `on_copy`, `from`; `parts`
  with `part`, `on_copy`, `choose` (`condition`, `state`, `latest_by`), `attributes`, `from`).
- **Records:** `parts` (`partRecord`, `partForeignKeyRecord`) and `copy` (`copyRecord`, `copyAttributeRecord`, `copyLinkRecord`,
  `copyPartRecord`, `copyChoiceRecord`) on the entity record, each left out when empty.
- **In the engine:** the expression site kind `PartChoice`.
