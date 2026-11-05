# Requirements as a kind of your own

Teams work from statements of what a system must do: a contract clause, a regulation, a rule in a registry, a line in a
product brief. This guide keeps those statements in the model and links each one to the parts of the model that meet it and
the scenarios that prove it. Agents then see why an element exists before they change it, validation reports the gaps, and
a pack can write the evidence.

Maquettiste has no fixed requirement kind, because teams keep different fields. You declare your own kind, with your own
fields, and everything below is optional: a project that declares none of it sees no change.

The guide uses the Spoke & Chain model from the earlier guides, with its `RentalLifecycle` process and `RideAndPay` scenario.
The file names and the word "requirement" are only examples. Call the kind `rule` or `control` if that is your word.

## 1. Declare the kind

Add `.maquettiste/extensions/kinds/requirement.json`. Its file stem is the type:

```json
{
  "type": "requirement",
  "displayName": "Requirement",
  "properties": {
    "text": { "type": "string", "minLength": 1 },
    "citation": { "type": "string" },
    "importance": { "enum": ["must", "goal"] }
  },
  "required": ["text"]
}
```

Put your own fields here, such as a source system's code, a risk level or a review date. A field can link to other elements
with `x-ref`; [custom kinds](../user-guide.md#custom-kinds-extensionskindsjson) has the details.

## 2. Declare the links

Add `.maquettiste/extensions/traceability.json`. It gives the places you list two properties that hold requirements:

```json
{
  "name": "traceability",
  "description": "Which requirements an element fulfills, and which a scenario verifies.",
  "appliesTo": { "kinds": ["entity", "attribute", "state", "transition", "operation", "scenario"] },
  "properties": {
    "fulfills": { "type": "array", "items": { "type": "string", "x-ref": { "kind": "custom:requirement", "optional": true } } },
    "verifies": { "type": "array", "items": { "type": "string", "x-ref": { "kind": "custom:requirement", "optional": true } } }
  }
}
```

The names are yours too. A team may want two links in place of `fulfills`, one for a control the system runs and one for a
design a rule shaped.

## 3. Write the requirements

A batch writes them, from a terminal with `maquettiste model apply needs.json` or from an agent with `apply_batch`:

```json
{ "operations": [
  { "op": "upsert", "id": "@custom:requirement.Rentals.balance_settled", "element": {
      "kind": "custom", "type": "requirement", "name": "balance_settled", "package": "@package:Rentals", "status": "approved",
      "content": { "text": "A rental closes only when its balance is paid.", "citation": "Rental terms, section 4", "importance": "must" } } },
  { "op": "upsert", "id": "@custom:requirement.Rentals.reminder_before_return", "element": {
      "kind": "custom", "type": "requirement", "name": "reminder_before_return", "package": "@package:Rentals", "status": "approved",
      "content": { "text": "A member gets a reminder one hour before a return is due.", "importance": "must" } } },
  { "op": "upsert", "id": "@custom:requirement.Rentals.same_day_repair", "element": {
      "kind": "custom", "type": "requirement", "name": "same_day_repair", "package": "@package:Rentals", "status": "approved",
      "content": { "text": "Most repairs finish the day they start.", "importance": "goal" } } }
] }
```

`upsert` creates an element when its name is new and replaces it when it exists, so a script can read the statements from
wherever they live and send the same batch on every run. A run that changes nothing writes nothing. Bring in the statements
you link to, not a whole outside catalog: anything with no link reads as a gap.

## 4. Link the model to them

On the **JSON** tab of `RentalLifecycle`, give the `pay` transition that closes a rental the property:

```json
"properties": { "fulfills": ["Rentals.balance_settled"] }
```

On the **JSON** tab of the `RideAndPay` scenario, add:

```json
"properties": { "verifies": ["Rentals.balance_settled"] }
```

These are names, and the model stores ids. A name in a typed property is error MQ2010 until a batch converts it, which also
keeps the links right when a requirement is renamed later:

```json
{ "operations": [ { "op": "names-to-ids" } ] }
```

An agent can skip this step: `patch_element` takes the id, and `get_references` lists what links to a requirement.

The links now behave like any other reference. Deleting a linked requirement is refused unless you ask to remove the links,
and a link to a retired requirement warns MQ2022.

## 5. Report the gaps

Add `.maquettiste/extensions/rules/requirements.json`:

```json
{
  "rules": [
    {
      "id": "requirement-fulfilled",
      "severity": "warning",
      "description": "A requirement someone must meet is fulfilled by something that is not retired.",
      "select": { "kind": "custom:requirement" },
      "require": {
        "any": [
          { "equals": { "path": "content.importance", "value": "goal" } },
          { "referencedBy": { "kind": ["entity", "attribute", "state", "transition", "operation"], "field": "fulfills", "excludeRetired": true } }
        ]
      },
      "message": "Requirement {name} is fulfilled by nothing."
    },
    {
      "id": "requirement-verified",
      "severity": "warning",
      "description": "A requirement someone must meet is verified by a scenario that is not retired.",
      "select": { "kind": "custom:requirement" },
      "require": {
        "any": [
          { "equals": { "path": "content.importance", "value": "goal" } },
          { "referencedBy": { "kind": "scenario", "field": "verifies", "excludeRetired": true } }
        ]
      },
      "message": "Requirement {name} is verified by no scenario."
    }
  ]
}
```

Run `maquettiste validate`. It warns twice about `reminder_before_return`, which has no links yet, and says nothing about
`same_day_repair`, which is only a goal. To fail a build on a gap, set `severity` to `error`.

A verified requirement has a scenario linked to it. The rule does not run the scenario; `verify_scenarios` does that.

## 6. Write the evidence with a pack

A code generation pack can write a page per requirement. Add `.maquettiste/templates/requirement-pages/pack.json`:

```json
{
  "name": "requirement-pages",
  "version": "1.0.0",
  "engine": ">=1.0",
  "units": [
    { "id": "page", "template": "page.scriban", "for": "each custom:requirement", "output": "docs/requirements/{{ custom.name }}.md" }
  ]
}
```

And `page.scriban` beside it:

```
# {{ custom.name }}

{{ custom.content.text }}
{{ if custom.content.citation }}
Source: {{ custom.content.citation }}
{{ end }}
## Fulfilled by
{{ for link in (referenced_by custom "fulfills") }}
- {{ link.kind }}{{ if link.from }} {{ link.from.display_name }}{{ end }}{{ if link.owner }} in {{ link.owner.name }}{{ end }}
{{ end }}
## Verified by
{{ for link in (referenced_by custom "verifies") }}
- {{ link.from.name }}
{{ end }}
```

Add `docs` to `outputs.allow` in `maquettiste.json`, then run `maquettiste generate`. `referenced_by` lists what links to the
requirement, so the page renders again when a link comes or goes, or the element holding it changes.

## What to expect

- **Links from guards, gates, actions, checks and constraints.** These cannot hold custom properties yet, so link from the
  transition, state or entity that holds them.
- **Links across packages.** Each link counts as a dependency. If requirements sit in a package of their own, every package
  that links to it lists it in `dependsOn`, or the link is MQ2020 when layers are checked.
- **Bringing in a spreadsheet.** There is no CSV import for custom kinds. Turn each row into an `upsert` in a small script.
- **Editing.** Custom elements open as JSON in the editor for now.
