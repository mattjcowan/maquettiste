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

## See also

- [Processes, actors and scenarios](../user-guide.md#processes-actors-and-scenarios), including
  [The process editor](../user-guide.md#the-process-editor), [The chart](../user-guide.md#the-chart) and
  [The simulation panel](../user-guide.md#the-simulation-panel).
- [Sync enum and the quick fixes](../user-guide.md#sync-enum-and-the-quick-fixes) and
  [The rules for processes (MQ9xxx)](../user-guide.md#the-rules-for-processes-mq9xxx).
- [Verify scenarios, Export XState and Import XState](../user-guide.md#verify-scenarios-export-xstate-and-import-xstate).
- [Generating code from processes](../user-guide.md#generating-code-from-processes): what the packs write, scenario tests
  included, and the [process-docs](../packs/process-docs.md) pack.
