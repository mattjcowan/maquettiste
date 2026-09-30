# process-docs

Markdown documentation of the model's processes: a page per process with a state diagram, its states, transitions, gates,
events, context, guards, actions, invokes, actors and scenarios; a page per actor; a walk-through page per scenario; and an index
page. Every page is derived from the model and overwritten on each run, so the pages never need editing: change the model and
generate again.

The pages are meant to be read and reviewed next to the model, so the pack writes to a **committed** output root. A project uses
it by copying the pack into `.maquettiste/templates/process-docs/`, registering its output and allowing that root in
`maquettiste.json`:

```json
"outputs": { "allow": [ { "path": "docs", "commit": true } ] },
"packs": { "process-docs": { "output": "docs" } }
```

Every path below is under `docs/`. The gate 3 fixture (`tests/fixtures/models/processes`) is registered this way.

## Output

For process `PurchaseApproval` with scenario `HappyPath` and actor `BudgetHolder`:

| Unit | Mode | For | Writes |
| --- | --- | --- | --- |
| `process-page` | `overwrite` | `each process` | `processes/purchase-approval.md`: the description, a summary table, the lifecycle binding (a lifecycle's bound states and their enum members), the state diagram, then tables of states, transitions (priority order, numbered), gates with their audit record, events with their actors and payloads, context, guards, actions, invokes, the actors involved and links to the scenarios |
| `actor-page` | `overwrite` | `each actor` | `actors/budget-holder.md`: type, stereotypes and tags, the actor's `properties` (a persona's `goals` as a list), the processes that name it, the events it raises, the gates it signs (and whether its signature is required) and the tasks assigned to it |
| `scenario-page` | `overwrite` | `each scenario` | `processes/purchase-approval/happy-path.md`: the start (instant and context), one table row per step with the actor, the input, whether it is accepted or **refused**, the active states after it and notes (signer and meaning, reason, payload, assumed guards, changed context, the step's description), then the outcome |
| `index` | `overwrite` (file block) | `model` | `index.md`: every process and actor with links; written only when the model has a process or an actor |

File names are the kebab-case element names. Links between pages are relative, so the folder can move as a whole.

Descriptions are the author's own Markdown and are written as they are (in table cells, line breaks become spaces). Names and
paths are written as code spans; display names are escaped.

## The diagram

The state diagram is a fenced block of diagram text in the `stateDiagram-v2` syntax of Mermaid, which common Markdown renderers
draw in place. What it shows:

- **Compound states** are nested blocks; `[*] -->` points at the initial child and `--> [*]` leaves each final child. The top
  level does the same with the process's initial state and its final states.
- **Parallel states** are blocks whose regions (each a nested block) are separated by `--` lines.
- **Choice states** are the choice pseudo-state (`state CreditCheck <<choice>>`); their outgoing edges carry the guards, and the
  last, unguarded one is the "else".
- **History states** are states labelled with their name and `H` (shallow) or `H*` (deep), with an edge labelled `default` to
  the state entered when no history is recorded.
- **Transitions** with targets are one edge per target, labelled as the editor's canvas labels them (`event [guard] / actions`,
  `after 5d`, `done`, `always`, `done: invoke`, `error: invoke`); a gated transition adds its count, `(gate 2 of 2)`. An edge is
  declared in the innermost compound state that holds both its ends, so the renderer draws it inside that block.
- **Targetless transitions** (the source stays active, nothing is exited or entered) are a note beside the source, one line
  `internal: <label>` per transition, as the canvas lists them inside the state.

State ids are the state names; a name that two states share, or that the diagram text reserves (`state`, `note`, `end`, …), is
replaced by `s_` and the state's path with `_` between names (`s_Fulfilment_Billing_Shipped`), labelled with the name. Characters
that the diagram text reads as syntax (`#`, `;`, `"`, `<`, `>`) are written as its entity codes.

## Parameters

Set them in `maquettiste.json` under `packs.process-docs.parameters`.

| Parameter | Default | Meaning |
| --- | --- | --- |
| `diagrams` | `true` | Write the state diagram on each process page; `false` leaves the section out (for renderers that do not draw diagram text). |
| `title` | `"Process documentation"` | The heading of `index.md`. |

## Files

| File | Holds |
| --- | --- |
| `pack.json` | Units and parameter defaults. |
| `helpers.js` | Pure string helpers: `md_cell` (escaped text for a table cell), `md_code` (a code span safe in a table), `diagram_text` (entity codes for diagram text) and `diagram_reserved`. |
| `_docs.scriban` | Shared functions: lists of names and links, attribute types, trigger wording, transition numbers and the state diagram. |
| `process-page.scriban`, `actor-page.scriban`, `scenario-page.scriban`, `index.scriban` | The unit templates. |

## Notes

- Transition numbers are the transitions' priority order in the process and are used across the page (gates, events, guards
  and actions refer to them).
- An actor page lists the events that name the actor; an event open to any actor is listed on the process page as "any actor"
  and not on every actor's page.
- A scenario page shows what the scenario expects (its recorded expectations), not a fresh run: `maquettiste process verify`
  checks that the process still behaves that way.
- Two processes with the same name in different domains would write the same page: give them distinct names.
- Pages are deterministic: no dates, no machine data, lists in the model's order.
