# A first model in ten minutes

This guide builds the first piece of a model for Spoke & Chain, a community bicycle-sharing and repair cooperative. You
create two domains, two entities with attributes and keys, a relationship with cardinality, an enum and a diagram, and you
learn to read the Problems panel. The later guides continue the same story.

You need the editor running over a repository where `maquettiste init` has run. If you do not have that yet, follow
[Getting started](../getting-started.md) first.

## 1. Create the domains

A domain is a business area. It groups elements and becomes a folder and a namespace in generated code.

1. On the rail, pick **Domain model**.
2. Click **+** in the explorer header and choose **New domain**.
3. Type `Membership` and click **Create**.
4. Do the same for `Rentals`.

The explorer now shows both domains. Each one has a folder per kind (Entities, Relationships, Enums, and so on).

## 2. Create the Member entity

1. Right-click **Membership** and choose **New entity**.
2. Type `Member` and click **Create**.

The entity opens in its editor, as a tab in the centre. It starts with a `uuid` key, so it already has an `id` attribute.

## 3. Add attributes

1. In the entity editor, stay on the **Attributes** tab.
2. Click **Add attribute** (or press Ctrl+Enter in the grid). A new row appears.
3. Fill in the rows below. Use the arrow keys to move, Enter or F2 to edit, and Tab to move right.

| Name | Type | Len | Req | Uniq |
| --- | --- | --- | --- | --- |
| `firstName` | `string` | 80 | yes | |
| `lastName` | `string` | 80 | yes | |
| `email` | `string` | 254 | yes | yes |
| `joinedOn` | `date` | | yes | |

The **Type** cell opens a list with sections (Recent, Built-in, Custom types, Enums, Reference data, Value objects) and one
search box across them. The last two columns, **Display name** and **Description**, say what an attribute means; fill
them in when the name alone is not clear.

Edits save as you go. Each change is one undo step (Ctrl+Z).

## 4. Check the key

The top of the editor holds the entity's controls. If they are folded away, click the chevron at the right of the title
row.

1. Find **Key**: it reads `id`.
2. Click **Edit…** beside it. The **Keys of Member** dialog lists the attributes under **Primary key**, and the **Key
   strategy** (`uuid-v7` for a new entity).
3. Leave it as it is and close the dialog.

The key column of the attribute grid shows a key icon on `id`.

## 5. Create the Rental entity

1. Right-click **Rentals** and choose **New entity**. Name it `Rental`.
2. Add `startedAt` (`datetimeoffset`, required) and `returnedAt` (`datetimeoffset`, not required).

## 6. Draw the relationship on a diagram

A diagram is a saved view of the canvas. It remembers which elements it shows and where.

1. Right-click **Membership** and choose **New diagram**. Name it `Members and rentals`. The diagram opens on the canvas.
2. In the explorer, right-click **Member** and choose **Add to diagram**. Do the same for **Rental**.
3. On the canvas, drag from the small dot on the right edge of the **Member** card onto the **Rental** card. The **New
   relation: Member → Rental** dialog opens.

4. Fill it in:
    - **Name**: `MemberRentals`.
    - **Kind**: `association`.
    - **Member end**: **Min** `1`, **Max** `1`. A rental belongs to exactly one member.
    - **Rental end**: **Min** `0`, **Max** `*`. A member has zero or more rentals.
    - **On delete of the Member**: `restrict` (the default for an association), so a member with rentals cannot be
      deleted.
5. Click **Create relation**.

The edge appears between the two cards, labelled with its cardinality. The toolbar's **Display** menu switches between
**UML multiplicities** and **Crow's feet**, and sets how much each card shows.

You can also select two cards (Shift+click) and click **New relation** in the toolbar.

## 7. Add an enum

An enum is a closed set of named values that belongs to the code.

1. Right-click **Membership** and choose **New enum**. Name it `MembershipPlan`.
2. In the enum editor, on **Members**, click **Add member** three times and name the members `Monthly`, `Annual` and
   `Student`.
3. Open **Member** again and add an attribute `plan`. In its **Type** cell, pick **MembershipPlan** under **Enums**, and
   tick **Req**.

## 8. Arrange the diagram

1. Click **Auto-layout** in the canvas toolbar. It arranges every card and fits the view.
2. Drag a card where you want it. Positions, pan and zoom are saved in the diagram's file, so the diagram opens the same
   way next time.
3. **Export** writes the diagram as SVG or PNG.

![The Domain model screen: the Rentals diagram with Member, Rental, Bike, Tariff, RentalCharge, Station and DamageReport cards joined by named relations, and Rental open in the inspector](../images/editor-light.png#only-light)
![The Domain model screen: the Rentals diagram with Member, Rental, Bike, Tariff, RentalCharge, Station and DamageReport cards joined by named relations, and Rental open in the inspector](../images/editor-dark.png#only-dark)

!!! tip
    The diagram picker also lists an **All of** view per domain (such as **All of Membership**). It shows every entity
    of the domain, and becomes a diagram of its own the first time you arrange it.

## 9. Read the Problems panel

Every save re-validates the whole model. The result is in the **Problems** tab of the bottom panel (Alt+Shift+J shows or
hides the panel).

- The header has one chip per severity: **Errors**, **Warnings** and **Information**, each with its count. Untick a chip to
  hide that severity.
- Each row names a rule (such as `MQ3005`, an entity without a key) and the element it is about. Click a row to go there.
- Some rules have a fix button beside the row. A fix is one change, and Undo takes it back.
- **Validate again** runs every rule over the whole model now.

A finding leaves the panel as soon as its cause is fixed. Information findings are notes, not faults. Under **Settings ›
Validation** you can change the severity of any rule, or turn most rules off.

## 10. Look at the files

Everything you did is plain JSON under `.maquettiste/model/`: one file per element, references between elements by id.
Run `git status` in the repository to see them. The editor, the command line and an agent all read these same files.

From a terminal, the same check the Problems panel runs:

```sh
maquettiste validate
```

It exits 1 when the model has errors.

## Next

- [Start from a database](start-from-a-database.md): design tables first, then make entities from them.
- [The layout](../user-guide.md#the-layout) and [Element editors and General mode](../user-guide.md#element-editors-and-general-mode)
  describe every panel and editor.
- [The explorers and screens](../user-guide.md#the-explorers-and-screens) covers the canvas, the Display menu and diagrams in
  full.
- [Search and filters](../user-guide.md#search-and-filters) shows how to find elements in a large model.
- [Settings › Validation](../user-guide.md#settings-validation) lists every rule and its severity.
