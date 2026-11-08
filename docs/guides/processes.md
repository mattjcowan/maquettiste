# Processes: a lifecycle and an orchestration

A **process** is a statechart. A **lifecycle** describes the states one entity goes through, and can be bound to an enum
attribute of that entity so the two never drift. An **orchestration** coordinates work across people, roles, systems and
other processes. **Actors** are the people, roles and systems that raise events. **Scenarios** are recorded runs that the
engine replays as tests.

This guide models two processes for Spoke & Chain: the `Rental` lifecycle, and a `Repair` orchestration where ordering
parts and the mechanic's assessment run in parallel. Then it simulates the repair, records the run as a scenario and
verifies it.

## 1. Create the actors

1. On the rail, pick **Processes**.
2. Click **+** in the explorer header and choose **New actor…**.
3. Create three actors, each with its **Name** and **Type**:
    - `Member`, a person.
    - `Mechanic`, a role.
    - `PartsSupplier`, an external system.

The actors are listed in the **Actors** folder.

## 2. Create the Rental lifecycle

1. Click **+** and choose **New process…**.
2. **Name**: `RentalLifecycle`. **Domain**: `Rentals`. **Use**: **Lifecycle**.
3. **Subject entity**: `Rental`. **Bound attribute**: **New status attribute and enum**.
4. Click **Create**.

The dialog creates, in the same change, a `status` attribute on `Rental` and an enum `RentalStatus`. The process starts
with one state, `Initial`, and opens on its **Chart** tab.

## 3. Draw the lifecycle

On the chart, select a state and use the keys, or right-click it for the same actions as a menu.

1. Select `Initial`, press F2 and rename it `Reserved`.
2. Press N to add a sibling state, then F2 to name it `Active`. Add one more, `Returned`.
3. On the **States** tab, set the **Type** of `Returned` to `final`.
4. Back on **Chart**, drag the dot on the right edge of `Reserved` onto `Active`. A transition is created on a new event.
   Do the same from `Active` to `Returned`.
5. On the **Events** tab, rename the two events `PickUp` and `BikeReturned`, and allow the actor `Member` to raise each.
   An event with no actors listed can be raised by any actor.

The chart now reads `Reserved → Active → Returned`, with the edges labelled by their events.

## 4. Keep the enum in step

The bound enum still has one member, `Initial`, while the lifecycle has three root states. The Problems panel shows
MQ9203 (enum drift), and the **Bound attribute** control shows a drift badge.

1. Click **Sync enum** beside **Bound attribute** (or the fix button on the problem row).
2. Read the plan: members added, removed and reordered.
3. Click **Apply**. `RentalStatus` now has `Reserved`, `Active` and `Returned`.

If MQ9205 follows (the attribute's default is not the initial state), its fix button **Set default to** sets it.

## 4a. Give the states codes

A state may carry a stable stored value, its **code**. Codes are optional.

1. On the **States** tab, type `R` in the **Code** column of `Reserved`, `A` for `Active` and `X` for `Returned`.
   Codes are strings or whole numbers, unique in the lifecycle.
2. The codes form a map here, since no coded state sits inside another coded state. That means any moment has at most one code.
3. If the codes must fit an enum, reference type or plain status attribute, bind it in **Bound attribute**. A code that does
   not fit is MQ9207. States without a code under a reference-type or plain binding give one MQ9210 warning per binding, naming them, and **Sync codes** proposes their codes. Under an enum binding a code is optional, since the member already is the stored value.

The new lifecycle is written as `subjects`, a list with one entry per entity it governs. One lifecycle may list several entities,
and an entity may follow several lifecycles. An old file may still use `subject` and `boundAttribute` on the process. That form
works but warns (MQ9209), and **Use subjects** converts it. A new lifecycle can't be read by an engine older than 0.13.0.

### One lifecycle for each object of a detail

When a parent has many things that each have their own states, make the thing a detail entity and make it the subject. In
Spoke & Chain, a `Rental` could have many `Accessory` lines, such as a helmet or a lock. Each line goes from `Reserved` to
`Handed out` to `Returned` on its own. Create `AccessoryLifecycle` with the subject `RentalAccessory`, a detail of `Rental`.
A rental with three accessory lines then has three instances of that lifecycle, and one of the rental's own. The model needs
nothing else.

### Guards that read the object

A guard may read the object the lifecycle follows. On `Rental`, a guard `subject.customer.since < date_of(now())` reads the rental's
customer. Use `subject.<attribute>` for the subject's own attributes and for to-one navigations. The bound attribute is out of scope.
With several subjects, only what they all hold under one id is in scope. A value that is missing reads as the default, then null.
Only true passes. `csharp-dapper` leaves such a guard as a partial method for a person to write.

### Scenario objects

A scenario can name its objects, so a guard like that has something to read. Add a `customer` object and a `rental` object, link
them by the rental's relation end, and set `subject` to the rental. In JSON:

```json
"objects": [
  { "id": "…c", "name": "customer", "entity": "…Customer", "values": { "…Customer.since": "2024-03-01" } },
  { "id": "…r", "name": "rental", "entity": "…Rental",
    "links": [ { "end": "…Rental.customer", "object": "…c" } ] }
],
"subject": "…r"
```

A value you leave out reads as the default, then null. `simulate_process` and `record_scenario` take `objects` and `subject`,
and the simulation panel shows a loaded scenario's cast, read-only. Recording keeps it.

## 5. Create the Repair orchestration

1. In the Domain model explorer, create a domain `Workshop` (**+**, then **New domain**).
2. Back in Processes, click **+**, **New process…**. **Name**: `Repair`. **Domain**: `Workshop`. **Use**:
   **Orchestration**. A subject entity is optional; leave **None**.
3. Rename the first state `Received`, and add a sibling state `InProgress`.
4. Select `InProgress` and press Shift+N twice to add two children; name them `Parts` and `Assessment`. A state that
   gets a child becomes compound.
5. On the **States** tab, set the **Type** of `InProgress` to `parallel`. Its two children are now regions that run side
   by side, drawn separated by a dashed line.
6. Inside `Parts`, add `Ordering`, then its sibling `PartsReady` (type `final`). Inside `Assessment`, add `Assessing`,
   then `Assessed` (type `final`).
7. After `InProgress`, add `Repairing`, then `Completed` (type `final`).

A compound state starts in its first child unless you set **Initial** on the States tab. A parallel state with one
region is warned about (MQ9017).

## 6. Wire the transitions

Draw each transition by dragging a state's dot onto its target, then name the event on the **Events** tab:

| From | To | Event | Raised by |
| --- | --- | --- | --- |
| `Received` | `InProgress` | `StartRepair` | `Mechanic` |
| `Ordering` | `PartsReady` | `PartsArrived` | `PartsSupplier` |
| `Assessing` | `Assessed` | `AssessmentDone` | `Mechanic` |
| `InProgress` | `Repairing` | (none) | |
| `Repairing` | `Completed` | `RepairFinished` | `Mechanic` |

For `InProgress → Repairing`, open the **Transitions** tab and set the row's **Trigger** to `done`: it fires once every
region has reached its final state, whatever order the parts and the assessment finish in. If the drag left an event
that nothing uses, MQ9013 offers **Remove** in the Problems panel.

![The RepairOrchestration chart one step into a simulation: the In progress state's Assessment and Parts regions both active, and the simulation panel listing the enabled events, the configuration and the trace](../images/process-chart-light.png#only-light)
![The RepairOrchestration chart one step into a simulation: the In progress state's Assessment and Parts regions both active, and the simulation panel listing the enabled events, the configuration and the trace](../images/process-chart-dark.png#only-dark)

**Layout** (Ctrl+L) arranges the chart; after that, states stay where you put them.

## 7. Simulate the repair

The simulation panel under the chart runs the process through the engine.

1. Expand the panel with the chevron on its title row.
2. **Enabled** lists what the process can take now. Pick `StartRepair` with the actor `Mechanic` and click **Raise**
   (or press 1).
3. **Configuration** now shows two active states, `Ordering` and `Assessing`, and the chart highlights both.
4. Raise `AssessmentDone`, then `PartsArrived`. The `done` transition fires, and the process is in `Repairing`.
5. Raise `RepairFinished`. The process is final.

**Last step** shows the guards evaluated, the actions run and what changed. **Trace** lists the inputs: select one to see
the state after it. A refused input stays in the trace with its reason.

## 8. Record the run as a scenario

1. On the panel's title row, click **Record to scenario…**.
2. Name it `PartsAfterAssessment`. The outcome is prefilled: final, because the process ended.
3. Save. The scenario stores every step with its expected states and context, filled from the run.

The scenario appears under the process's **Scenarios** in the explorer, and on the editor's **Scenarios** tab.

## 9. Verify

Scenarios are tests of the model.

- Right-click **Repair** in the explorer and choose **Verify scenarios**. One line per scenario goes to the Output panel,
  and the explorer shows each scenario's status.
- On the **Scenarios** tab, **Replay** runs one scenario and shows its first failure.
- From a terminal, `maquettiste process verify Repair` prints `pass` or `FAIL` per scenario and exits 1 when one fails.
  `maquettiste validate` replays every scenario of every process, so CI catches a chart change that breaks one.

When you change the chart on purpose, a scenario may fail with MQ9302 (the active states differ). Its fix, **Update
expectations from replay**, rewrites the expectations from a fresh replay.

## 10. Who may act: roles, members and the object

Say a `Review` lifecycle governs an `Item`, and each item belongs to a project and has an account manager.

1. Add an actor `Reviewer` of type **role**. Add an actor `Clerk` of type **person**, and in its editor set **Member of**
   to `Reviewer`. Or open `Reviewer` and use **Add members…**. An event that lists `Reviewer` now accepts `Clerk`.
2. Add an actor `AccountManager`. In **Taken from the object**, set **Worked out as** to **A field of the object** and pick
   the item's `accountManager` field. Name it in a gate's signers like any other actor.
3. On the lifecycle's **Subject**, set **Owner** to the item's `ownerLogin` field if the model holds it there. This is optional.
4. In a scenario, open **Participants**. Add `ann`, who holds `Reviewer in Apollo` (a project in the scenario's objects), and
   `bea`, who stands for `Clerk`. On each step, pick the participant. The engine shows the actor it acts as.

If Ann tries a step that only a reviewer on another project may take, the replay refuses it and says which participant
failed and which list it did not satisfy. See [Actors](../user-guide.md#actors-types-membership-and-the-object) and
[Scenario participants](../user-guide.md#scenario-participants).

## 11. Share a guard between processes

When two processes need the same check, hold it once. Create a process with `"use": "library"` and move the guard into its `guards`. It has
no states of its own. A transition of any process then names the guard by its id in `guard`, as it did before. The expression runs in the
process that uses it, so it reads that process's `context`. [Libraries](../user-guide.md#libraries-shared-guards-actions-and-gate-policies)
has the rest, with actions and gate policies.

## 12. One lifecycle for every type that carries a stereotype

Spoke & Chain wants every approvable record, such as a `PartsOrder` and a `FleetPurchase`, to follow one `Approval` lifecycle. Listing
each type is easy to forget when a new one arrives. Name the stereotype instead.

1. Create a stereotype `approvable` that applies to entities. Give it two attributes: an enum `approvalStatus` and a reference
   `requestedBy`. Apply it to `PartsOrder` and `FleetPurchase`.
2. Create a lifecycle `Approval`. On its **Subject**, pick **Approvable** from the **Stereotypes** group.
3. Set **Bound attribute** to `approvalStatus` and **Owner** to `requestedBy`. Both are the stereotype's own attributes, so every
   type that carries it reads the same fields.

Both types now follow `Approval`. Mark a third type `approvable` later, and it follows it too. Open `PartsOrder` in the explorer:
its **Lifecycles** folder lists `Approval` "through Approvable".

Say `FleetPurchase` keeps its state in its own `status` attribute. Add a second subject that names `FleetPurchase` and binds
`status`. The entry that names the entity wins over the stereotype, so no error appears. In a guard, `subject.requestedBy` now reads
for every approvable type. See [A stereotype as a subject](../user-guide.md#lifecycle-subjects-and-state-codes).

## 13. Say what an event does: verbs

An event name is free text. A verb says what the event does, so a later rule or a pack knows. Verbs are optional.

1. Open **Rental** lifecycle's **Events** tab. In the **Verb** cell of `PickUp`, pick **Promote**. For a `Cancel` event that moves a
   rental back, pick **Demote**.
2. Open **Settings › Verbs**. Under **Your verbs**, click **Add**, then pick the suggestion `cancel`. Add a description such as
   "Stop the rental before pick-up." The verb `cancel` is now yours.
3. Back on the `Cancel` event, change its verb to `cancel`.
4. On an operation `AssignMechanic`, open its **Verb** and pick **Connect**.
5. In **Settings › Verbs**, tick **Hide** on `lock` and `unlock`. The shop never uses them, so the pickers stop offering them.

Typing a verb nobody declared, such as `cancle`, is MQ9701. Its quick fix adds the verb or clears it. To rename `cancel` to
`call-off`, use **Rename…**. It shows every event that will change, and runs as one step. See [Verbs](../user-guide.md#verbs).

## 14. Record who approved, and check another object's state

Lakeside Hardware keeps a `StockTransfer` that moves stock between two shops. A manager approves it. The shop wants to know who
approved and when, the approver must not be the person who submitted it, and the transfer may only leave a shop that is open.

Say `StockTransfer` has a lifecycle `Transfer` with states `Draft`, `Review`, `Approved` and `Rejected`, and the attributes
`submittedBy`, `approvedBy` and `approvedOn`. It also has a to-one end `fromShop` to a `Shop`, which has its own lifecycle
`ShopStatus` with the states `Open` and `Closed`.

1. On `Transfer`'s **Transitions** tab, add an action `RecordSubmission`. In its **Sets** cell, add one set: attribute
   `submittedBy`, value `current_actor()`. Put the action on the `Submit` move.
2. Add an action `RecordApproval` with two sets: `approvedBy` is `current_actor()`, and `approvedOn` is `date_of(now())`. Put it on the
   `Approve` move.
3. Add a guard `NotTheSubmitter` with the condition `subject.submittedBy <> current_actor()`. Put it on `Approve`. The submitter is read
   from the object, so the earlier set is what the guard sees.
4. Add a guard `ShopIsOpen` with the condition `in_state(subject.fromShop, ShopStatus.Open)`. Put it on `Submit`.

A set's value is checked as you type, so a wrong type shows at once (MQ3103).

Now a scenario. Add two objects. `north` is a `Shop` whose **States** cell holds `ShopStatus.Open`. `t1` is a `StockTransfer`
linked to `north` by `fromShop`, and `t1` is the subject. Add two participants, `bea` and `carl`. Then add the steps:

1. `Submit` by `bea`. The guard `ShopIsOpen` reads the open shop. The step expects the state `Review` and the value
   `submittedBy` of `bea`.
2. `Approve` by `bea`. The guard `NotTheSubmitter` fails, so the step is refused. Mark the step as expecting a refusal.
3. `Approve` by `carl`. The step is accepted. It expects `Approved` and the values `approvedBy` of `carl` and `approvedOn` of
   the simulation's date.

Run **Replay**, or **Record from simulation** and then **Update expectations from replay**. The values appear in each step's
**Expected values**. If a step changes a value you did not expect, MQ9307 names it.

An object's state that the scenario does not give reads from the bound attribute, and otherwise as the lifecycle's initial
state. The trace notes the guess. Close `north` by listing `ShopStatus.Closed` in its **States**, and step 1 is refused.

`csharp-dapper` adds a `RecordSubmission` hook and a `RecordApproval` hook for the team to fill in, and leaves the two new guards
to a person. See [Actions that set the object's data](../user-guide.md#actions-that-set-the-objects-data) and
[Who acts, and another lifecycle's state](../user-guide.md#who-acts-and-another-lifecycles-state).

## 15. Close a paid invoice, except to the finance team

Lakeside Hardware keeps an `Invoice` with a lifecycle `InvoiceFlow` and the states `Draft`, `Sent`, `Paid` and `Void`. The shop's rule is
"nobody may change a paid invoice, except the finance team". Say there are two actors: `Clerk`, a role, and `Finance`, a role. An `Invoice`
has no other rule.

1. Open `InvoiceFlow` and its **Rights** tab. **When nothing is said** stays **Allowed**, so clerks can still work on drafts and sent invoices.
2. Add a right. Name it `PaidIsClosed` and set **Allows or denies** to **Deny**. In **Does**, pick **Modify** and **Delete**. In **In states**,
   pick `Paid`. Leave **Who** empty, which means everyone. In **Except**, pick `Finance`. In **Why**, write "A paid invoice is closed to
   everyone but finance."
3. Switch to **Who can do what** and pick the state `Paid`. The row for `Clerk` shows **Denied** under Modify and Delete. The row for
   `Finance` shows **Allowed**, greyed, because only the default says so. `Except` did not grant anything: it only took finance out of the deny.
   Click the Denied cell: it names `PaidIsClosed`.

Now a scenario. Add an object `inv1`, an `Invoice` that is the subject, and its **States** are left alone. Add two participants: `cal`, who stands
for a `Clerk`, and `fay`, who stands for a `Finance`. Then add the steps:

1. `Send` by `cal`. The invoice is in `Sent`.
2. `Pay` by `cal`. The invoice is in `Paid`.
3. A verb step: **Verb** `modify`, **Set values** `total=90`, by `cal`. The step is refused. Its **Refusal** is `right` and **Refused by** is
   `PaidIsClosed`.
4. The same verb step by `fay`. The step is accepted, and expects the value `total` of 90.

Run **Replay**, or run the steps in the simulation panel and use **Record to scenario…**, then **Update expectations from replay**. The refused
step shows "a right denies it, refused by PaidIsClosed" and lists the right under **Rights that answered**. If someone later removes the
right, step 3 is accepted and the replay fails with MQ9308 for the refusal it no longer finds, and with MQ9301 for the step that is now accepted.

A lifecycle that denies by default works the other way: set **When nothing is said** to **Denied**, then add allow rights. An allow in a lifecycle
that allows by default can change nothing, so it is MQ9904. See [Rights](../user-guide.md#rights).

## 16. Freeze a released document, and keep salary private

Harbor Labs keeps a `Document` with a lifecycle `DocumentFlow` and the states `Draft`, `Released` and `Obsolete`. Documents are released
and then must not change. A new revision is how a released document changes. Say there are two actors: `Author`, a role, and `Hr`, a role.

**A frozen document.** Freezing refuses changes before any right, so no right is needed for the rule itself.

1. In a scenario, add an object `doc1`, a `Document` that is the subject. Turn its **Frozen** switch on in the objects grid.
2. Add a participant `ann`, who stands for an `Author`. Add a verb step: **Verb** `modify`, **Set values** `title=Plan B`, by `ann`.
   The step is refused. Its **Refusal** is `frozen`, and there is nothing under **Refused by**.
3. Add a verb step: **Verb** `revise`, by `ann`. The step is accepted. Freezing does not stop `revise`.
4. Add an event step `Obsolete` by `ann`. The move is accepted: freezing does not stop moves.
5. Add a verb step `thaw` by `ann`, then the `modify` step again. It is accepted.

Open **Who can do what**, pick `Released` and turn on **Frozen**. Modify and Delete read **Denied**, with "Freezing refuses it before any
right." Revise stays **Allowed**. To stop more while frozen, add a right with **While frozen** on, such as a deny of `change-owner`.

If a scenario sends an event that names `freeze` and later a step that modifies the document, the later step is refused as frozen and
`verify_scenarios` warns with MQ9309. Thaw in between, or **Update expectations from replay** so the step expects the refusal.

**Nobody but HR reads salary.** Say `Employee` has the attribute `salary`, and its lifecycle is `EmployeeFlow`.

1. Open `EmployeeFlow` and its **Rights** tab. Add a right named `SalaryIsHr`, set **Allows or denies** to **Deny**, and pick `Read` in **Does**.
2. In **Attributes**, pick `salary`. Pick `Hr` in **Except**. In **Why**, write "Only HR reads salary."
3. In **Who can do what**, the Read cell of an ordinary actor says **Depends**, naming `SalaryIsHr`: it denies only for the attribute.
4. A scenario step with **Verb** `read` and **Attributes** `salary`, by a participant who is not in HR, is refused with refusal `right`,
   refused by `SalaryIsHr`. The same step reading `name` is accepted, since `SalaryIsHr` says nothing about `name`.

An event that names `modify` cannot say which attributes it changes. A deny that names attributes still holds for it. See
[Rights](../user-guide.md#rights).

## See also

- [Processes, actors and scenarios](../user-guide.md#processes-actors-and-scenarios), including
  [The process editor](../user-guide.md#the-process-editor), [The chart](../user-guide.md#the-chart) and
  [The simulation panel](../user-guide.md#the-simulation-panel).
- [Lifecycle subjects and state codes](../user-guide.md#lifecycle-subjects-and-state-codes), including a stereotype as a subject.
- [Verbs](../user-guide.md#verbs).
- [Rights](../user-guide.md#rights): who may do which verbs in which states, named rights, and refusals in scenarios.
- [Trigger points](../user-guide.md#trigger-points): checks, actions and replaces on any verb, in a fixed order, and what happens when.
- [Actions that set the object's data](../user-guide.md#actions-that-set-the-objects-data).
- [Actors: types, membership and the object](../user-guide.md#actors-types-membership-and-the-object).
- [Sync enum and the quick fixes](../user-guide.md#sync-enum-and-the-quick-fixes) and
  [The rules for processes (MQ9xxx)](../user-guide.md#the-rules-for-processes-mq9xxx).
- [Verify scenarios, Export XState and Import XState](../user-guide.md#verify-scenarios-export-xstate-and-import-xstate).
- [Generating code from processes](../user-guide.md#generating-code-from-processes): what the packs write, scenario tests
  included, and the [process-docs](../packs/process-docs.md) pack.
