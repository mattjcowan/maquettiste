# Richer sign-off with approval stages: design

**Status (2026-11-08): designed, not built. Not yet checked independently.** It designs the seventh item in the owner's order after 4.1: "richer sign-off: several named signatures, conditions on who must sign, delegation, escalation, separation of duties across steps", with approval stages (4.3). The owner's review of 2026-11-08 answered the eight questions this item raised (B1 to B8). This design takes those answers as decided and does not reopen them. Section 3 tests them against a study of about 20 systems and says where the evidence leans the other way. [Who, what and when](lifecycle-actors-scenarios.md) section 10 step 7 sets the direction. [What lifecycle systems can do](lifecycle-capabilities.md), part E, is the yardstick. [The verbs design](verbs-design.md), [the rights design](rights-design.md), [the actors design](actors-design.md), [the object effects design](object-effects-design.md) and [the trigger points design](trigger-points-design.md) give its parts. It builds on trigger points part 2 (named trigger points, 0.18.0). A file path without a project prefix is under `src/Maquettiste.Engine/`. The examples are invented. Each new name and format is held to the roadmap's test: it stays for good, it must help any team whether they use it or not, and it stays optional.

## 1. In short

Today a move can carry one **gate**. It says who may sign (`signers`, `requiredActors`), how many (`required`), whether one person may count twice (`allowRepeatSigner`), whether a reason is needed, what a signature means, and what is recorded with it. Only `sign` does anything. `reject`, `ignore`, `withdraw`, `delegate` and `route` are built-in verbs that are accepted and do nothing.

| Layer | What changes |
| --- | --- |
| The model | A gate may hold **stages**. A gate with stages is an **approval**. Each stage holds **named signatures**, and each signature has its own signers, count and required actors. A signature may be needed only **when** a condition on the object holds, and may be **not by** the person who did a named verb or move since the object entered the move's source state. One person gives at most one signature of an approval, unless the gate allows it. In part 2, stages run in **rounds**: stages of one round side by side, rounds one after another, and a stage may be skipped by a condition. Part 2 also adds **delegation**, **reminders and escalation**, and **signing again with a password** |
| Responses | A signer answers an approval with a **response**: **sign**, **reject**, **ignore** (abstain) or **withdraw**. A rejection is a veto: it fails that approval, clears its signatures, and moves nothing. A move on rejection is an ordinary transition whose verb is `reject`. Ignore finishes that person and leaves the count as it is. Withdraw takes back one signature. Part 2 adds **delegate** |
| Simulation and scenarios | A step gives a response, and may name the signature it answers. The gate audit records each response, with its signature and stage. A step may expect the signatures counted after it. Trigger points on `reject`, `ignore` and `withdraw` start to run |
| Code generation | Templates read stages and signatures. `csharp-dapper`'s generated interpreter takes the response in its envelope, keeps each approval's progress in the snapshot, and hands each signature's condition to the team. `process-docs` shows stages, signatures and responses |
| The team's system | Nothing changes until the team uses the new output. Who is signed in, sending reminders, calendars, checking a password and adding reviewers at run time stay with the running system |

**A project that uses none of this** keeps its files, output bytes, findings, replays, tool answers, dependency keys and speed. **A gate that keeps today's fields and has no stages means exactly what it means today.** Section 12 lists everything.

## 2. Scope

| | What | Section |
| --- | --- | --- |
| **Part 1 (0.19.0)** | `stages` on a gate and a gate policy, with exactly one stage; named signatures with `signers`, `required`, `requiredActors`, `allowRepeatSigner`, `when` and `notBy`; `allowSignerAcrossSignatures` on the gate; the responses `sign`, `reject`, `ignore` and `withdraw`; trigger points on `reject`, `ignore` and `withdraw`; rights on them; scenario steps, the audit, the simulation's gate state, `expect.signatures`; the resolved model, both packs and the editor | 4 to 11 |
| **Part 2 (0.20.0)** | Several stages, with `round` and a stage's `when`; `delegateTo` and the response `delegate`; `remindAfter`, `escalateAfter` and `escalateTo`; `reauthenticate` | Decisions 5, 6 and 20 to 22 |
| **Later, if teams ask** | "All", "any" or a share of signers as a count; a password or a comment rule per response; a trigger point narrowed to one signature; one person never holding two roles on one object | 13 |
| **Not in either part** | Observers and acknowledgers; weighted votes; giving `route` a meaning; who is signed in, notifying people, calendars, checking a password, adding reviewers at run time | 3, 13 |

**What part 1 leaves fixed, so part 2 adds keys only.** Part 1's file is already an approval with one stage: `stages` holds a stage, and the stage holds `signatures`. Part 2 lets `stages` hold more than one, and adds optional keys to a stage and a signature. Nothing moves. Part 1's responses, refusals, audit records, trace, expectations and generated members stay as they are. The owner fixed this shape in the review (B1, B8), so that names never have to move.

## 3. The review's answers, tested

The owner's answers were tested against the study and the code. All of them hold well enough to build. Where the evidence leans another way, the note says so, and the design still follows the answer.

| Answer | Verdict | Evidence | Decision |
| --- | --- | --- | --- |
| Two releases; part 1's file is one approval with one stage (B1) | **Agree** | Trigger points and rights each shipped in two parts. Names are permanent, so the shape must not move between parts | 1, 31 |
| Stages inside one approval, as rounds; a stage is signatures only; no edges; a round never moves the object (B2) | **Agree** | Several systems keep stages inside one approval: SAP release codes run in parallel when no prerequisite links them [9]; DocuSign gives parallel recipients the same routing order [15]; a "two level" approval runs a second routing after the first [2]. Others use states in series, each with its approvers (Agile [7], Odoo [8]). The code agrees with the answer: one source and event can carry only one gate (MQ9103), so stages as states would need parallel regions for side-by-side approvers | 1, 4, 5 |
| Reject is a veto; it moves nothing; a move on rejection is an ordinary `reject` transition (B3) | **Agree, with a note** | ENOVIA: "The object cannot be promoted with a Reject signature" (checked). Power Automate ends "everyone must approve" at the first rejection [14]. **The note:** most systems also *move* the object on rejection in one action. Agile's "If Rejected, Set Status To" sends it to a named status "automatically" (checked) [16]; Salesforce goes back a step [10]; a decline in a service desk is its own transition [11]. Here one input either vetoes (and stays) or moves. A team that wants both a recorded veto and a move sends two inputs, or only the move, whose leaving discards the signatures as today | 10, 11 |
| Ignore and abstain finish the person and leave the count unchanged (B3) | **Agree, with a note** | **The note:** ENOVIA's Ignore "indicates that this signature requirement is not necessary for promotion" (checked): it waives the requirement. Here it waives only that person's part. A team that needs a waiver uses the signature's `when`, or a lower `required` | 12 |
| Withdraw removes one signature (B3) | **Agree** | Most systems withdraw the whole request (Salesforce's recall, Business Central's cancel [23]). ENOVIA replaces a signature with a new one (checked). Withdrawing one signature is the narrow form, and leaves the request alone | 13 |
| Separation of duties: not the person who did a named verb or move since the source state was entered; a flag for one person on two signatures; `allowRepeatSigner` unchanged (B4) | **Agree** | Business Central warns when a requester is also an approver [13]. No system studied states it more simply than "not whoever did X" | 15, 16 |
| Delegation states whether and to whom; `delegate` records on whose behalf (B5) | **Agree** | Salesforce has `allowDelegate` per step [10]. Agile records the person who signed in place of another as the Signoff User (checked) [16]. Who is away, and from when to when, is the running system's data (ServiceNow's delegation dates [27]) | 20 |
| Reminders and escalation: the model states the delay and the recipient; the host sends; never a transition (B6) | **Agree, with a note** | Agile: a reminder period "begins when the routable object enters each Review or Released status", and "escalation occurs only once per status" (checked) [16]. **The note:** Agile's escalation person "may approve, reject or acknowledge … in the place of the original approver" (checked). Here an escalation is a notice only. A team whose escalation target should sign names it among the signers or in `delegateTo` | 21 |
| Signing again with a password: a flag on a named signature; the host checks (B7) | **Agree, with a note** | 21 CFR 11.200 asks for the full identity check on signings outside one continuous session [32]. **The note:** Agile sets it per response: "Approve Only" or "Reject Only" (checked) [16]. Here the flag covers every response on the signature. A per-response rule can come later as an optional key | 22 |
| `route` stays inert until delegation (B3) | **Agree** | What `route` would do, sending the object to people at run time, is "adding reviewers at run time", which the note leaves to the running system | 23 |
| Observers, acknowledgers and weighted votes stay out | **Agree** | Weights appear only in Aras [6]. Observer and acknowledger roles are common in PLM systems (Agile, Teamcenter, Odoo), so they are listed in section 13 | 24 |
| New names settled by the independent check; the tag waits for the owner (B8) | **Agree** | Section 14 lists every new name | 14 |

**One thing the study suggests that was not asked:** "all", "any" and a share of signers as counts are cheap and common (ServiceNow, Power Automate, Teamcenter [17] [14] [5]). They are left out, since `required` and `requiredActors` already cover the cases the owner named. Section 13 lists them.

## 4. Decisions

Each decision gives its confidence: **high**, **medium-high**, **medium** or **low**. A decision marked **(part 2)** is built in part 2. The others are part 1.

### Shape and home

1. **An approval is a gate with `stages`. Each stage holds named `signatures`.** Confidence: high, by the owner's B1 and B2.
   - In part 1, `stages` holds exactly one stage (the schema's `maxItems: 1`). Part 2 lifts the limit (decision 5).
   - **With stages, the counting keys move to the signatures.** `required`, `signers`, `requiredActors` and `allowRepeatSigner` are written on each signature, and never on a gate with stages (MQ8201). So each count lives in one place.
   - **What stays on the gate:** `name`, `displayName`, `reasonRequired`, `meanings`, `auditAttributes`, `description`, `stereotypes` and `properties`. They apply to every signature and every response of the approval. A gate still needs at least one meaning (MQ9104).
   - **A gate policy holds stages the same way.** Signatures still count per transition, as the gate policy's description says today.
   - Each stage is a part of kind **`gate-stage`**, and each signature a part of kind **`gate-signature`**.
   - **Why no wrapper key such as `approval`:** a gate has one approval, so a second level would add nothing. The editor and the docs call a gate with stages an approval.
   - **Why not states, one per stage:** the owner's B2 reasons. One source and event carry only one gate, parallel approvers would need parallel regions, and a gate policy can share a stage list the way it shares one gate today. Modeling each stage as a state stays possible, as before.
2. **A gate with today's fields and no stages keeps today's meaning, and takes only `sign`.** Confidence: high, by the owner's B1.
   - A step that gives such a gate another response is MQ9305. Trigger points and rights on `reject`, `ignore` and `withdraw` don't run there.
   - **Why:** the owner's rule, and it keeps the generated output of every project with gates unchanged (decision 27). A team that wants a rejection on a simple gate writes one stage with one signature. The editor does that conversion in one click (section 11).
3. **A named signature has a `name`, signers and a count, and two optional keys: `when` and `notBy`.** Confidence: high for the keys that move from the gate; medium-high for `when` and `notBy`.
   - `signers`, `required`, `requiredActors` and `allowRepeatSigner` mean what they mean on a gate today, for that signature. A required actor is met through the signature's actor and its memberships only (actors decision 6).
   - **`allowRepeatSigner` keeps its meaning:** one person may count twice on this one signature.
   - A signature is **complete** when it has `required` signatures and each required actor has signed.
   - Its description says why it is needed, and is localizable.
4. **A stage is signatures only.** Confidence: high, by the owner's B2.
   - Rights, trigger points, entry and exit actions, and "What happens when" stay on states and transitions. A team that needs different rights while finance reviews models a state.
   - A stage is **complete** when each of its signatures is complete or not needed.
   - The approval is complete when its last round is complete. The occurrence that completes it makes the move, as the occurrence that completes a gate does today.
5. **(Part 2) Stages run in rounds. A stage names its `round`, a whole number from 1, by default 1.** Confidence: medium-high.
   - Stages of one round run side by side. The next round starts when every stage of the current round is complete or skipped. **Completing a round never moves the object.**
   - **Stages are listed in round order** (MQ8203), so the file reads from top to bottom. Rounds with gaps, such as 1 and 3, just run in that order.
   - **Rounds have no edges.** A real flow belongs in an orchestration.
   - **Why a number, not a `rounds` list holding stages:** part 1's `stages` would have to move under the new list, which the owner ruled out. A number added in part 2 changes nothing in part 1's files. DocuSign's routing order works the same way: equal numbers run in parallel [15].
   - **Why not a flag such as "with the previous stage":** a number shows the round on each row, and the editor can sort and group by it.
6. **(Part 2) A stage may have a `when`. When it isn't true, the stage is skipped.** Confidence: medium-high.
   - It is judged when its round starts, as a signature's `when` is (decision 7). A skipped stage counts as complete for its round.

### Who must sign, and when that is judged

7. **A signature's `when` decides whether it is needed. It is judged when its round starts.** Confidence: medium-high.
   - **When a round starts:** round 1 when the object enters the gated move's source state, and again when a rejection clears the approval (decision 10). A later round when the round before it completes (part 2). The answer holds until the approval completes, is rejected, or the object leaves the source state.
   - **Why at the start, not at each response:** an approval completes on an occurrence. If a signature stopped being needed halfway, nobody would be left to give the occurrence that completes it. Agile picks approvers and starts its reminder clock when the object enters the status (checked) [16]. A team that changes data during a review and wants it judged again rejects, which starts round 1 again.
   - **Only true counts.** False or unknown means the signature isn't needed. The simulation's gate state shows which signatures are needed (decision 26).
   - **What it reads:** a new site kind, **`Approval`**: `subject.`, the process's context, `in_state` and `now()`. Not `current_actor()`, since nobody acts when a round starts, and not an event's payload or `old.` (MQ3102, MQ3104). Unlike a trigger point, it may read the context: a signature only ever happens inside a run.
   - **When nothing is needed,** the approval is complete when it starts. The gated event's next occurrence makes the move as if there were no gate. Its sender is checked as for a move without a gate (the event's actors and rights), and the audit records `completed` with no signature. Confidence: medium.
8. **The step may name the signature it answers. Otherwise the engine picks one, the same way every time.** Confidence: medium-high.
   - The pick: among the current round's signatures that are needed and not complete, in file order (stage, then signature), the first one the person may fill (decisions 14 to 16) where they meet a required actor not yet signed; else the first one they may fill at all.
   - Then actors decision 15 picks the actor they sign as, within that signature.
   - **When the person may fill none,** the step is refused with the first failing check of row 7 (decision 14), on the first signature whose signers they meet, or `gate-signer` when they meet none.
   - Generated code follows the same rule, with the envelope's `Signature` in place of the step's.

### Responses

9. **An occurrence of a gated event carries a response: `sign` (the default), `reject`, `ignore` or `withdraw`. Part 2 adds `delegate`. The occurrence's verb is the response.** Confidence: high for using the built-in verbs; medium-high for answering on the gated event.
   - This extends verbs decision 15, which already makes every counted occurrence a `sign`. No built-in verb is added, and none changes its meaning.
   - **Rights:** rows 3 and 4 check the event's own verb, as today. Row 7 checks the response's verb (decision 14).
   - **Trigger points:** the response's verb runs its points, with the gated transition as the place in `transitions` (decision 17).
   - **A response other than `sign` acts only on an approval.** It takes no transition and never moves the object. When no approval is open on that event, the step is refused with `no-transition`.
   - **The gated move's guard must hold,** as it must for a signature today. A move that isn't offered has no open approval.
   - **Why on the gated event, not on an event of its own:** the response must say which approval it answers. The gated event already does. A separate event would need a new key that points at a transition.
10. **A rejection is a veto.** Confidence: high, by the owner's B3.
    - It fails that approval and clears every signature gathered on it, in every stage and round. It fires no move. Other gates keep their signatures.
    - The audit records `rejected`, with the person, actor, meaning and reason. Round 1 then starts again at once, so its conditions are judged again (decision 7).
    - One rejection is enough, whatever the counts.
    - A person who has signed may still reject. A person who ignored may not (decision 12).
11. **A move on rejection is an ordinary transition from the source, on an event whose verb is `reject`.** Confidence: high, by the owner's B3.
    - It has its own guard, rights and trigger points, as every move does. No key is added.
    - Leaving the source discards every gate's signatures there, audited `discarded`, as today.
    - **So one input is either the veto or the move.** A team that only wants "a rejection sends it back" models the move alone. A team that also wants the veto in the audit sends the response, then the move. Section 3 notes that most systems do both in one action.
12. **Ignore (abstain) finishes that person on that signature. The count is unchanged.** Confidence: high, by the owner's B3.
    - The audit records `ignored`. The person gives no other response on that signature until the approval starts again, and does not meet a required actor.
    - Without `allowSignerAcrossSignatures`, an ignore is the person's one response on the approval, as a signature is (decision 16).
    - **When a required actor can then never be met,** the approval waits. A rejection or a move out of the state ends the wait. The simulation's gate state shows it.
    - **The verb's fixed meaning still reads true:** "waive a signature, so the move no longer waits for it". It waives that person's part.
13. **Withdraw removes that person's signature on one signature of the current round.** Confidence: high for the rule, by the owner's B3; medium-high for "current round only".
    - With `allowRepeatSigner`, it removes their latest one. The audit records `withdrawn`. They may sign again.
    - With no signature of theirs there, the step is refused with **`gate-not-signed`** (new).
    - **Why the current round only (part 2):** a later round started because the earlier one completed. Reopening it would undo that round's start.
14. **The signatures row of the refusal order grows. The response's trigger points follow it.** Confidence: medium-high. Rows 1 to 6 are rights decision 12, unchanged.

    | Order | Check | Refusal |
    | --- | --- | --- |
    | 7a | The person meets the signature's signers (a gate's signers, without stages) | `gate-signer` |
    | 7b | The lifecycle's denies on the response's verb | `right` |
    | 7c | `notBy` (decision 15) | **`gate-excluded`** (new) |
    | 7d | Already signed, ignored or delegated on this approval, where that isn't allowed (decision 16) | `gate-repeat` |
    | 7e | Nothing to withdraw (decision 13) | **`gate-not-signed`** (new) |
    | 7f | A reason is required and missing | `gate-reason` |
    | 8 to 12 | The response's trigger points: before actions, checks, the effect, with the change, after commit (trigger points decision 18) | `check` |

    - Every refused attempt is audited `refused`, as today. `refusedBy` names the gate or gate policy, as today, or the right.
    - `gate-repeat` already means "this person already signed the gate". On an approval it means "already answered this approval", which is the same idea one level up.
    - **`reasonRequired` applies to every response.** A rule per response, as Agile has for comments (checked) [16], can come later as an optional key.

### Separation of duties

15. **`notBy` keeps a person off a signature: whoever did a named verb or a named move since the object entered this move's source state.** Confidence: high for the rule, by the owner's B4; medium-high for the shape.
    - Each entry is `{ "verb": … }` or `{ "transition": … }`.
    - **The window opens with the move that entered the source state, and includes it.** So "not the submitter" is `notBy: [{ "verb": "promote" }]` on a review entered by a promote, or `{ "transition": <Submit> }`.
    - **A person is an identity:** a step's `signer`, else its participant's identity. In generated code it is the envelope's `Signer`. An automatic move is done by nobody, so it excludes nobody.
    - **A gated move is done by everyone whose signature counted toward the approval that made it.** So "the release approvers are not the review approvers" is `notBy: [{ "transition": <the reviewed move> }]`. Confidence: medium.
    - **Which verbs:** those the lifecycle's own runs see: move verbs, signature verbs, `complete`, and verbs its events name (MQ8202). A verb done outside a run, such as a `modify` from an edit screen through the per-type runner, never reaches the process's snapshot, so generated code couldn't see it. A team that needs it records the person in an attribute and denies with a right's condition, as rights' example `subject.submittedBy = current_actor()` does.
    - **A gate policy may name verbs only.** Its transitions belong to no single lifecycle (MQ8202).
    - **Why not a right:** a right reads the object and the actor, not who acted while the object sat in a state. The rights design left this to sign-off (rights section 12).
16. **One person gives at most one response that counts on an approval, unless the gate says `allowSignerAcrossSignatures`.** Confidence: high, by the owner's B4.
    - It is counted across every stage and round of the approval. A signature, an ignore and (part 2) a delegation each count as the person's response.
    - `allowRepeatSigner` still governs one signature. With both flags, a person may fill two signatures and count twice on each that allows it.
    - **A gate without stages is unchanged.** It has one signature, so the rule never applies.
    - MQ9101 gains a case: an approval that needs more different people than its signers can give, counting only person actors, without the flag.

### Trigger points and rights

17. **Trigger points on `reject`, `ignore` and `withdraw` run at an approval, in the places `sign`'s run.** Confidence: high.
    - Their before actions and checks run during selection, after row 7. When admitted, the response's effect is made, its with-change points run, and its after-commit points are queued. A response never makes a microstep, so this all happens in the admit, as for a signature that doesn't complete a gate (trigger points decision 18).
    - **`targets` and `transitions` are allowed beside `reject`, `ignore` and `withdraw`,** as beside `sign`. So a point can tell the response on the gated move from the ordinary `reject` move of decision 11.
    - **MQ8107 warns on them** only when no gate of the lifecycle has stages and no event names the verb. A named trigger point (0.18.0) may name them too. It runs inside the lifecycle that owns the gate, before that lifecycle's points, for a covered type (the review's answer to A5).
    - `delegate` joins in part 2, where a signature has `delegateTo`. `route` stays warned.
18. **A signature's signers count as an allow for `sign`, `reject`, `ignore` and `withdraw` on that approval, and for the gated event.** Confidence: high, extending rights decision 14.
    - Rights on those verbs narrow them: a right may deny `reject` to some signers.
    - "Who can do what" and the access grid list the new verbs only for lifecycles with an approval.
19. **The gate audit names the signature and stage.** Confidence: high.
    - Records on an approval gain `signature` and `stage`. Outcomes gain `rejected`, `ignored` and `withdrawn`, and in part 2 `delegated`. Records on a gate without stages don't change.

### Part 2: delegation, reminders, password, `route`

20. **(Part 2) `delegateTo` on a signature says it may be delegated, and to which actors.** Confidence: high for what the model states, by the owner's B5; medium for how a delegated signature counts.
    - The response **`delegate`** names the person it goes to (a step's `delegateTo`, a participant). That person must meet one of the `delegateTo` actors. The audit records `delegated`, and the delegator is finished on that signature. It never moves the object.
    - The person delegated to may then answer on that signature **on behalf of** the delegator (a step's `onBehalfOf`). Their signature counts as the delegator's actor, so required actors still work. The record keeps both people.
    - `notBy` and decision 16 are checked on the person who signs.
    - **Left to the running system:** who is away, from when to when, and standing lists of substitutes.
21. **(Part 2) `remindAfter`, `escalateAfter` and `escalateTo` on a signature state when to remind and whom to tell.** Confidence: high for the split, by the owner's B6; medium-high for "once per round start".
    - Each is an ISO 8601 duration, measured from when the signature's round started. A reminder goes to the people who may still answer. An escalation goes to the `escalateTo` actors.
    - **Each is due once per round start,** as Agile escalates "only once per status" (checked) [16]. A host may repeat reminders on its own schedule.
    - **The host sends them.** A reminder or escalation is never a transition, and never moves the object. A move on a timer stays an ordinary `after` transition.
    - **An escalation is a notice.** Its recipient gets no right to sign unless named among the signers or in `delegateTo` (section 3).
    - Simulation lists due notices when a time step passes them (decision 26).
22. **(Part 2) `reauthenticate: true` on a signature asks for the person's identity to be checked again for every response on it.** Confidence: high for the flag, by the owner's B7; medium for how generated code enforces it.
    - The model never sees a password. Simulation takes the check as done.
    - Generated code refuses a response on such a signature unless the host says it checked, through the envelope's `Reauthenticated`, with `Refusals.GateReauthenticate`. This refusal exists only in generated code, since a scenario can't fail a password.
23. **`route` stays inert in both parts.** Confidence: medium-high. It would send the object to people at run time, which is the running system's job. Its trigger points stay warned (MQ8107).
24. **Out of both parts:** observers and acknowledgers, weighted votes, "all", "any" or a share as counts, a rule per response, and adding reviewers at run time. Confidence: high, by the review.

### Simulation and scenarios

25. **A step answers an approval with `response` and, optionally, `signature`.** Confidence: high.
    - Both are allowed only on an event step whose event is gated by an approval (MQ9305). A `signature` must belong to that gate (MQ9305).
    - Part 2 adds `delegateTo` and `onBehalfOf`, each a participant.
26. **The trace, the audit and the simulation's gate state show the approval.** Confidence: high for the content; medium-high for the shape.
    - **The audit** gains the records of decision 19.
    - **The gate state** (`GateState` in a simulation answer) gains `stages`: for each stage its signatures, each with whether it is needed, the signatures counted, the people finished on it, and whether it is complete. Part 2 adds the current `round` and a stage's `skipped`. All are left out on a gate without stages.
    - **Each enabled trigger** in a simulation answer gains `responses`: the responses its event takes at an approval. It is left out when empty, so today's answers don't change.
    - **(Part 2)** The trace gains `notices`: each reminder or escalation that fell due, with its signature, kind (`remind` or `escalate`) and recipients.
27. **A step may expect the signatures counted after it.** Confidence: medium-high.
    - **`expect.signatures`** maps a signature's id to the number counted after the step. Only the listed signatures are compared. A difference is MQ9311.
    - `expect.refusal` gains `gate-excluded` and `gate-not-signed` (MQ9308).
    - **(Part 2)** `expect.notices` lists the notices due at the step, as `{ signature, kind }` (MQ9311).
    - **Recording** writes `expect.signatures` on a step that answers an approval. A new signature elsewhere doesn't change it, since only the listed ids are compared.
28. **Separation of duties reads what was done since each state was entered.** Confidence: medium-high.
    - The run keeps, per instance, who did which verb and which move, and when each active state was entered. It keeps this only for a lifecycle with a `notBy`, so other runs cost nothing.
    - Generated code keeps the same record in its snapshot, so replays and generated tests agree.

### Code generation

29. **Each output depends on what it needs, so a project pays only for what it uses.** Confidence: high.
    - **A lifecycle uses approvals** when one of its gates, inline or through a gate policy, has stages. Only then are the new members, refusals, outcomes and snapshot fields written.
    - **One naming key, `ap:approvals`,** hashes the ids of those lifecycles, and of the gate policies with stages. **`model.approval_lifecycles`** lists the lifecycles by name, then id, and keys only on `ap:approvals`. The shared generated interpreter asks it, as it asks `model.rights_lifecycles` today. Its hash never changes in a project without approvals.
30. **`csharp-dapper` and `process-docs` follow the model.** Confidence: medium-high. Section 10 lists what each writes. A signature's `when` goes to the team as a static method, as a right's condition does, until the generated runtime (4.6) reads the object.

### Release

31. **Two parts, each a minor release.** Confidence: high, by the owner's B1.
    - Part 1 is 0.19.0. Part 2 is 0.20.0. Both start from `main` once trigger points part 2 (0.18.0) is merged.
    - An older engine refuses a file with `stages`, `allowSignerAcrossSignatures`, a step's `response` or `signature`, or `expect.signatures`. `formatVersion` stays 1.
    - The pull request may be built and made green. The tag waits for the owner (B8).

## 5. Schema and files

**Part 1**

| File | Key | Type | Notes |
| --- | --- | --- | --- |
| `process.json` | `$defs/gate` gains `stages` (after `name` and `displayName`) | list of `$defs/gateStage`, `minItems` 1, `maxItems` 1 | Decision 1. With stages: no `required`, `signers`, `requiredActors` or `allowRepeatSigner` (MQ8201). `signers` stays required on a gate without stages, through the schema's condition, so the finding for a gate with neither doesn't change |
| `process.json` | `$defs/gate` gains `allowSignerAcrossSignatures` | boolean, default false | Decision 16. Only with stages (MQ8201) |
| `process.json` | `$defs/gateStage` (new) | `{ id, name, displayName?, description?, signatures }`; `signatures` a list of `$defs/gateSignature`, `minItems` 1 | Part kind `gate-stage` |
| `process.json` | `$defs/gateSignature` (new) | `{ id, name, displayName?, description?, signers, required?, requiredActors?, allowRepeatSigner?, when?, notBy? }` | Decision 3. `signers` `minItems` 1; `required` default 1; `when` a `$defs/expr`; `notBy` a list of `{ verb }` or `{ transition }`. Part kind `gate-signature` |
| `scenario.json` | step `response` | `sign`, `reject`, `ignore`, `withdraw`; default `sign` | Decision 25 |
| `scenario.json` | step `signature` | id | Decision 25 |
| `scenario.json` | `expect.signatures` | map of signature id to a whole number | Decision 27 |
| `scenario.json` | `expect.refusal` gains `gate-excluded`, `gate-not-signed` | | Decision 14 |

**Part 2**

| File | Key | Type | Notes |
| --- | --- | --- | --- |
| `process.json` | `stages` drops `maxItems` | | Decision 5 |
| `process.json` | `$defs/gateStage` gains `round`, `when` | whole number from 1, default 1; `$defs/expr` | Decisions 5 and 6 |
| `process.json` | `$defs/gateSignature` gains `delegateTo`, `remindAfter`, `escalateAfter`, `escalateTo`, `reauthenticate` | actor ids (`minItems` 1); ISO 8601 durations; actor ids; boolean | Decisions 20 to 22 |
| `scenario.json` | step `response` gains `delegate`; step `delegateTo`, `onBehalfOf` | participant ids | Decision 25 |
| `scenario.json` | `expect.notices` | list of `{ signature, kind }`, kind `remind` or `escalate` | Decision 27 |

Also updated in each part: the sample app's schema copies, `openapi.yaml` by hand (the shapes, the audit record, `GateState`, the enabled trigger's `responses`, the simulate and record requests, the records), and the editor files generated from it.

Part 1, a release approval on a document's `Approve` move:

```json
"gate": {
  "id": "…", "name": "Release", "reasonRequired": false,
  "meanings": [ { "id": "…", "name": "Approved" } ],
  "stages": [
    { "id": "…", "name": "Review", "signatures": [
      { "id": "…", "name": "Engineering", "signers": ["<id of Engineer>"], "required": 2,
        "notBy": [ { "verb": "promote" } ],
        "description": "Two engineers who did not submit it." },
      { "id": "…", "name": "Quality", "signers": ["<id of QualityLead>"] },
      { "id": "…", "name": "Finance", "signers": ["<id of Controller>"],
        "when": { "text": "subject.cost > 10000" },
        "description": "Needed for costs above 10,000." }
    ] }
  ]
}
```

As text, conditions are stored as trees of ids. A scenario, with a document of cost 12,000 in Review, entered by Ana's `Submit`:

```json
"steps": [
  { "id": "…", "event": "<id of Approve>", "participant": "<Ana>",
    "expect": { "accepted": false, "refusal": "gate-excluded", "refusedBy": "<id of Release>" } },
  { "id": "…", "event": "<id of Approve>", "participant": "<Ben>",
    "expect": { "signatures": { "<id of Engineering>": 1 } } },
  { "id": "…", "event": "<id of Approve>", "participant": "<Cleo>", "response": "ignore",
    "expect": { "signatures": { "<id of Engineering>": 1 } } },
  { "id": "…", "event": "<id of Approve>", "participant": "<Ben>", "response": "withdraw",
    "expect": { "signatures": { "<id of Engineering>": 0 } } },
  { "id": "…", "event": "<id of Approve>", "participant": "<Dee>", "signature": "<id of Finance>", "response": "reject",
    "reason": "Cost not approved.",
    "expect": { "states": ["<id of Review>"], "signatures": { "<id of Engineering>": 0, "<id of Quality>": 0 } } },
  { "id": "…", "event": "<id of SendBack>", "participant": "<Dee>",
    "expect": { "states": ["<id of Draft>"] } }
]
```

Ana submitted, so `notBy` refuses her. Cleo's ignore leaves the count at one, and Ben's withdraw takes it to zero. Dee's rejection clears the approval and the document stays in Review. `SendBack` names `reject` and is an ordinary move to Draft (decision 11).

Part 2, finance and legal side by side after the review round, legal only for contracts:

```json
"stages": [
  { "id": "…", "name": "Review", "signatures": [ … ] },
  { "id": "…", "name": "Finance", "round": 2, "signatures": [
      { "id": "…", "name": "Controller", "signers": ["<id of Controller>"],
        "delegateTo": ["<id of DeputyController>"], "remindAfter": "P2D",
        "escalateAfter": "P4D", "escalateTo": ["<id of FinanceDirector>"], "reauthenticate": true } ] },
  { "id": "…", "name": "Legal", "round": 2, "when": { "text": "subject.isContract" }, "signatures": [ … ] }
]
```

## 6. Rules

The process families MQ90xx to MQ99xx are full, and MQ81xx holds trigger points. Approvals take the next free family, **MQ82xx**, labelled "Approvals". It is free on `main` and on the trigger points part 2 branch.

| Id | Severity | Rule | Quick fix |
| --- | --- | --- | --- |
| MQ8201 | E | An approval's shape: a gate with `stages` that also has `required`, `signers`, `requiredActors` or `allowRepeatSigner`; `allowSignerAcrossSignatures` on a gate without stages | Remove the key |
| MQ8202 | E | A `notBy` entry that doesn't fit: a transition that isn't this lifecycle's; a transition in a gate policy; a verb this lifecycle's runs can't see (decision 15) | Remove the entry |
| MQ8203 | E | (Part 2) Stages not listed in round order | Reorder the stages |
| MQ8204 | E | (Part 2) Reminder and escalation shape: `escalateTo` without `escalateAfter`, or the reverse; a delay that isn't a positive ISO 8601 duration | Add or remove the key |
| MQ8205 | W | (Part 2) An escalation due no later than its reminder | Lengthen `escalateAfter` |
| MQ9311 | E | A scenario step whose signatures (part 2: notices) differ from `expect.signatures` (`expect.notices`) | Update the expectations from a replay |

**Existing rules that gain a case.** Each new case needs a stage, a step's `response`, `signature` or `expect.signatures`, or a trigger point's `targets` or `transitions` beside a signature verb.
- **MQ3001:** two stages of one gate with one name, or two signatures of one gate with one name, across its stages.
- **MQ3101, MQ3102, MQ3104, MQ3108 and the typing rules:** a signature's `when` (part 2: a stage's) at the `Approval` site; MQ3102 for `current_actor()` or the payload there.
- **MQ9101, MQ9102, MQ9105:** each signature's signers and required actors, as for a gate today. MQ9101 also: an approval that needs more different people than its signers can give, without `allowSignerAcrossSignatures` (decision 16).
- **MQ9106:** a signature's signers and required actors (part 2: `delegateTo` and `escalateTo`) count as uses.
- **MQ9701, MQ9704:** a verb in `notBy`.
- **MQ9305:** a `response` or `signature` on a step that isn't an event step gated by an approval; a `signature` of another gate. Part 2: `delegateTo` without `response: delegate`, `onBehalfOf` for a person with no delegation, and `delegate` on a signature without `delegateTo`.
- **MQ9308:** the refusals `gate-excluded` and `gate-not-signed`.
- **MQ8102:** `targets` and `transitions` are now allowed beside `reject`, `ignore` and `withdraw` (decision 17). This removes a case, which was an error before; it is the one finding that can disappear.
- **MQ8107:** `reject`, `ignore` and `withdraw` count as doable in a lifecycle with an approval (part 2: `delegate`, where a signature has `delegateTo`).

The mock backend's rule list follows.

## 7. Resolved model and templates

```csharp
public sealed class RGate : RProcessNode                       // existing; new members only
{
    public RList<RGateStage> Stages { get; internal set; }     // empty for a gate without stages
    public bool UsesStages => Stages.Count > 0;
    public RList<RGateSignature> AllSignatures { get; internal set; } // every stage's, in file order; empty without stages
    public bool AllowSignerAcrossSignatures { get; internal set; }
}
public sealed class RGateStage : RProcessNode                  // Kind "gate-stage"
{
    public string Name, DisplayName, Description;
    public RGate Gate { get; internal set; }
    public RList<RGateSignature> Signatures { get; internal set; }
    // Part 2: public int Round; public RExpression? When;
}
public sealed class RGateSignature : RProcessNode              // Kind "gate-signature"
{
    public string Name, DisplayName, Description;
    public RGate Gate; public RGateStage Stage;
    public int Required { get; internal set; } = 1;
    public RList<RActor> Signers, RequiredActors;
    public bool AllowRepeatSigner { get; internal set; }
    public RExpression? When { get; internal set; }           // typed at the Approval site
    public RList<RNotBy> NotBy { get; internal set; }         // each has Verb or Transition
    // Part 2: RList<RActor> DelegateTo, EscalateTo; string? RemindAfter, EscalateAfter; bool Reauthenticate;
}
// ResolvedModel gains ApprovalLifecycles (by name, then id). RActor gains Signatures (the named signatures naming it, by
//   process then file order). RStep gains Response and Signature (part 2: DelegateTo, OnBehalfOf); RExpectation gains
//   Signatures (part 2: Notices). Gate audit records gain Signature and Stage.
```

- **Templates read** `model.approval_lifecycles`, `gate.stages`, `gate.uses_stages`, `gate.all_signatures`, `gate.allow_signer_across_signatures`, `stage.name`, `stage.signatures`, `stage.gate`, `signature.signers`, `signature.required`, `signature.required_actors`, `signature.allow_repeat_signer`, `signature.when`, `signature.not_by` (each entry's `verb` or `transition`), `signature.stage`, `signature.gate`, `actor.signatures`, `step.response`, `step.signature` and `step.expect.signatures`. Part 2 adds `stage.round`, `stage.when`, `signature.delegate_to`, `signature.remind_after`, `signature.escalate_after`, `signature.escalate_to`, `signature.reauthenticate`, `step.delegate_to`, `step.on_behalf_of` and `step.expect.notices`.
- **A gate without stages reads as today.** `gate.signers`, `gate.required` and the rest keep their values. On a gate with stages they are empty, and templates read the signatures.

**Dependency keys.**
- **`ap:approvals`** hashes the ids of the lifecycles and gate policies with stages. It changes when a lifecycle gets its first approval or loses its last, not when a signature changes. It is worked out once per snapshot from the index rows, as `rn:rights` is: a process row carries a flag only where it has an approval. `Planning/DependencyHasher.cs` gains the `ap` prefix, and the plan explainer words it "The approvals the lifecycles state".
- **`model.approval_lifecycles`** keys only on `ap:approvals`.
- **`gate.*`, `stage.*` and `signature.*`** key on the process file, or on the library's file for a gate policy, as gate members do today.
- **`actor.signatures`** keys on the actor's referrers, as `actor.rights` and the actor's gates do: every signature that names the actor refers to it.
- So a template that reads none of these pays nothing, and in a project without approvals the one new key never changes.

**Records:** gate records gain `stages` and `allowSignerAcrossSignatures`, step records `response` and `signature`, expectation records `signatures`, each only where present. So `get_resolved_model` answers don't change for a project without them.

## 8. Simulation and replay

- **Where the interpreter changes** (`Processes/StatechartInterpreter.cs`):
  - **`Microstep`,** on entering a state: for each gated transition of the entered state whose gate has stages, the approval starts. Round 1's conditions are judged, and the progress is kept on the instance beside `Signatures`, which stays for gates without stages.
  - **`CheckSigner`** becomes the full row 7 of decision 14. It takes the step's response, picks the signature (decision 8), and audits a refused attempt `refused`, as today.
  - **`Sign`** becomes the response's effect: count a signature, record an ignore, remove a withdrawn signature, or fail and clear the approval and start round 1 again. Only a signature can complete the approval. When a signature completes the last needed signature of the round, the round is done. In part 2, the next round starts and its conditions are judged. When the last round is done, the move follows, as when a gate completes today.
  - **Responses other than `sign`** select only gated transitions with an approval, and are consumed in the admit without a microstep, as a signature that doesn't complete a gate is today.
  - **The response's trigger points** run where `sign`'s run today (trigger points section 8), for the response's verb.
  - **`Cancel`** discards the approval's progress on leaving, audited `discarded`, as today.
  - **The acts record** of decision 28 is kept in `HandleEvent`, `HandleInvoke`, `HandleVerb` and the admit, only for a lifecycle with a `notBy`.
  - **(Part 2)** `HandleTime` lists reminders and escalations that fall due, once per round start, in the trace's `notices`.
- **Scenario steps** pass `response` and `signature` (part 2: `delegateTo`, `onBehalfOf`) through `Model/Processes.cs`. A person's identity comes from the step's `signer` or participant, as for signatures today.
- **The trace and audit:** decisions 19 and 26, each new member left out when unused, so today's traces don't change.
- **Replay** compares `expect.signatures` (MQ9311) and the new refusals (MQ9308).
- **Replay keys:** a scenario whose process has an approval keys on its file, as today. What a signature's `when` reads reaches the keys through `TraceInputs`: `ExprSites` in `Validation/ExpressionRules.cs` also walks the `Approval` sites. A gate policy's file is already a key of each process that uses it.
- **Speed:** a gate without stages runs as today. Conditions are judged once per round start, not per response. The acts record is kept only where a `notBy` reads it.

## 9. Tools

- **No new tool.**
- **`simulate_process`, `verify_scenarios`:** the step's `response` and `signature`, the gate state's `stages`, each enabled trigger's `responses`, the new audit outcomes and refusals, `expect.signatures`. Part 2: delegation, `notices`, `expect.notices`.
- **`record_scenario`:** writes the steps' `response` and `signature` as given, and `expect.signatures` on steps that answer an approval.
- **`get_trigger_sequence`:** answers `reject`, `ignore` and `withdraw` at a gated transition, with row 7's checks and the response's effect as built-in rows.
- **`get_rights`** and "who can do what": signers' `reject`, `ignore` and `withdraw` at an approval (decision 18).
- **`preview_expression`:** previews a signature's `when` at its pointer.
- **`save_element`, `apply_batch`:** accept `{ "text": … }` for `when`, parsed at the `Approval` site.
- **`get_references`, `referenced_by`:** a signature's signers and required actors, its `notBy` verbs and transitions, the paths its `when` reads, and a step's `signature`. Part 2: `delegateTo` and `escalateTo`.
- **Deletes:** an actor removed from a signature's signers follows what removing a gate's signer does today. A transition or a project verb named in `notBy` drops its entry; the move or verb can no longer happen, so dropping it widens nothing that could occur. A signature named by a step's `signature`: `remove-references` drops the key, and the engine picks again. Removing a gate's last signature or last stage is refused by `remove-references`; `delete-dependents` removes the gate. `rename-verb` and `remove-verb` walk `notBy`.
- **`get_model_index`:** process rows gain `approvals`, the count of gates with stages, and `signatures`, the count of named signatures, each only when above zero.
- **`export_process`, `import_process`:** stages and signatures travel in `meta.maquettiste`.
- **`validate`:** the rules of section 6.
- **The CLI** follows the same tools. **The rule list, the schemas, the conventions resource, the modeling skill and the tool texts** (`src/Maquettiste.Cli/Mcp/ProcessTools.cs`, `Agent/tools.json`) describe all of this.

## 10. Packs

Output changes only for a model that uses what each output needs (decision 29).

**`csharp-dapper`, part 1**
- **The shared interpreter (`interpreter.scriban`),** only when `model.approval_lifecycles` isn't empty:
  - `SignatureResponse` (`Sign`, `Reject`, `Ignore`, `Withdraw`).
  - `ProcessEnvelope` gains `Response` and `Signature` as init properties. It is a positional record today, so adding them as init properties leaves every existing call compiling.
  - `GateDefinition` gains `Stages` and `AllowSignerAcrossSignatures` as init properties, with new records `StageDefinition` and `SignatureDefinition`.
  - `GateOutcome` gains `Rejected`, `Ignored` and `Withdrawn`. `Refusals` gains `GateExcluded` and `GateNotSigned`. `GateAuditRecord` and `SignatureEntry` gain `Signature` and `Stage` as init properties.
  - The snapshot gains `Approvals`, each gate's progress by transition (`ApprovalProgress`: the round's start, the signatures needed, each person's response), and `Acts` (`ActRecord`), only for a lifecycle with a `notBy`.
  - The interpreter starts approvals on entry, runs row 7 of decision 14, picks the signature by decision 8's rule, and calls the trigger steps of the response's verb through `ITriggerPointSteps`, as it calls `sign`'s.
- **`process-definition.scriban`:** builds a gate's stages and signatures, for lifecycles with an approval.
- **The team's half:** a static partial class `<Lifecycle>ApprovalConditions`, written once, with one synchronous method per signature that has a `when`, named `<Gate><Signature>` and documented with its text. It takes an `ApprovalCall` (instance, lifecycle, gate, stage, signature, states) and returns whether the signature is needed. A method named like its class gets `Value` added, by the pack's existing rule (`member_name` in `_process.scriban`). A new one fails the build until written, as a right's condition does.
- **`process-handlers.scriban`:** passes the envelope through. A refused response saves only what a refused signature saves today: the snapshot and its audit.
- **`process-contracts.scriban`:** each gate's typed audit record gains `Signature` and `Stage` on gates with stages.
- **`scenario-tests.scriban`:** a step's `response` and `signature` set the envelope; `expect.signatures` is asserted from the snapshot; the participant's identity is the envelope's `Signer`, so `notBy` works in generated tests as in replays.
- **Nothing goes in `helpers.js`.**

**`csharp-dapper`, part 2:** rounds and stage conditions (methods per stage with a `when` in `<Lifecycle>ApprovalConditions`); `SignatureResponse.Delegate`, `ProcessEnvelope.DelegateTo` and `.OnBehalfOf`, `GateOutcome.Delegated`; `ApprovalNotice` and `ApprovalNotices.Due(snapshot, now)`, which the host's scheduler calls to list due reminders and escalations, each with a stable id built from the instance, the transition, the round's start, the signature and the kind, so a host can drop a repeat; `SignatureDefinition.Reauthenticate`, `ProcessEnvelope.Reauthenticated` and `Refusals.GateReauthenticate`.

**`process-docs`, part 1**
- **The lifecycle page,** for a gate with stages: an "Approval" heading in place of today's gate lines, with a table per stage (signature, signers, required, required actors, repeat signer, needed when, not by). One paragraph says what each response does. The audit list gains the new outcomes. The transitions table's Gate cell reads "approval, 3 signatures" in place of "required of signers".
- **The actor page:** the signatures an actor gives, beside the gates.
- **The scenario page:** each step's response and signature, and its expected signatures.
- **The verbs page:** where `reject`, `ignore` and `withdraw` apply.

**`process-docs`, part 2:** rounds and skipped stages, delegation, reminders, escalation and "signed again with a password" in the stage tables.

**`sql-ddl`, `seed-data` and the TypeScript sample:** no change.

## 11. Editor

**Process editor, the Gates tab:**
- **A gate without stages** looks as today. A button, **Use named signatures**, turns it into an approval: one stage named after the gate, holding one signature that takes the gate's signers, count, required actors and repeat setting. **Back to one list of signers** reverses it when the approval has one signature with no `when` or `notBy`. Both carry tooltips.
- **An approval** shows its stage's name and a grid of signatures. Columns: Name, **Signers** (the actor picker), **Required**, **Required actors**, **Repeat signer**, **Needed when** (the value box, for `when`), **Not by** (chips of verbs and moves, edited in one popover) and **Why**. **Add signature** at the foot. The gate keeps its meanings, reason and audit attributes above the grid, with **One person may give several signatures** (`allowSignerAcrossSignatures`).
- **(Part 2)** **Add stage**, a **Round** column on stages, rows grouped by round, **Skip unless** (a stage's `when`), and on each signature **May be delegated to**, **Remind after**, **Escalate after**, **Escalate to** and **Sign again with a password**.

**Scenario editor:**
- **Steps:** a **Response** cell (Sign, Reject, Ignore (abstain), Withdraw; part 2: Delegate, with a participant) and a **Signature** cell, which shows "Picked by the engine" when empty. Both are offered only on a step whose event is gated by an approval.
- **Expectations:** a **Signatures** cell. "Update from replay" fills it. The Refusal cell offers the two new refusals.

**Simulation panel:** for each approval, its signatures with needed, counted, required and the people finished; the enabled trigger offers Reject, Ignore and Withdraw beside Sign, from its `responses`, with a signature picker. A rejection shows that the approval was cleared. Part 2: rounds, skipped stages and due notices.

**Elsewhere:** "Who can do what" lists the new verbs for signers. The Verbs tab counts approvals among `reject`, `ignore` and `withdraw`'s uses. Quick fixes: MQ8201 and MQ8202 remove the key or entry, MQ8203 reorders, MQ9311 updates the expectations from a replay.

**Generated and mock files:** the editor's API types come from `openapi.yaml` by the fixed command. The part kinds need no entry in `model/model.ts` or `model/labels.ts`, as `process-right` has none. By hand: `model/labels.ts` gains the texts above, and a new `model/approvals.ts` beside `model/rights.ts` holds the grid's rows and the conversion. In `src/editor/src/mocks/model/`, `stepper.ts` (2,287 lines today) gains approvals at entry, responses, the pick and `notBy`; `validate.ts`, `refs.ts`, `cascade.ts` and `modelIndex.ts` follow.

## 12. A project that uses none of this

**After part 1:**
- **Files:** nothing rewrites a file, so saves, `format`, `format --check` and snapshot import behave as today. Recording a scenario writes what it writes today.
- **Gates:** a gate without stages means exactly what it means today. It takes `sign` only, counts signatures per transition, audits `signed`, `completed`, `refused` and `discarded`, and refuses with the same words. Gate policies without stages are unchanged.
- **Findings:** the same, with one exception that needs a trigger point already in error: MQ8102 no longer flags `targets` or `transitions` beside `reject`, `ignore` or `withdraw`. Every new rule and case needs a stage or a new step key.
- **Output:** the same bytes. Each pack change depends on `model.approval_lifecycles`, `gate.uses_stages` or a new step key.
- **Simulation and replay:** the same outcomes, traces, audits and gate states. No new member appears without an approval.
- **Tool answers about the model:** the same. New record, index and simulation members are left out when unused, and `model.approval_lifecycles` is empty. `get_trigger_sequence`, `get_rights` and `get_references` answer as today.
- **Dependency keys:** the same, but for one key. A template that reads `model.approval_lifecycles` also keys on `ap:approvals`, whose hash never changes in a project without approvals.
- **Speed:** the same. Nothing new runs for a gate without stages.
- **Capabilities:** once built, these rows of `lifecycle-capabilities.md` part E become Yes: approve, reject, ignore with a separate list of who may do each (through rights on each verb); several named signatures on one state; a signature withdrawn; whether a signature is needed decided by a condition. "Separation of duties" becomes Yes for "approve what they submitted", and stays Part for "one person can't hold two roles on one object". After part 2: steps in series or parallel, signing authority handed to someone else, reminders and escalation, and signing again with a password become Yes (the last two as model states, host does).
- **Once, as on any upgrade:** every unit renders again and writes the same bytes, since the engine version is part of each unit's key; the editor refreshes the `.schema` copies, or `validate` warns MQ1008 until refreshed; the index cache is read in full once.
- **A team on mixed engine versions:** an older engine refuses the new keys (decision 31).

**After part 2:**
- **A project that uses part 1 but no rounds, stage conditions, delegation, reminders, escalation or password** keeps its files, output bytes, findings, keys and outcomes.
- **A project that uses neither part** keeps everything listed above.

## 13. What comes next

| Item | What it takes from this |
| --- | --- |
| Counts as "all", "any" or a share of signers | An optional key on a signature beside `required`. The study found these common (section 3). Changes no file |
| A rule per response | A password or a comment required only on approval or only on rejection, as Agile has. An optional key. Changes no file |
| Observers and acknowledgers | A signature that counts nothing, with its own responses. Kept out of both parts by the review |
| One person never holding two roles on one object | A rule over holdings (rights section 12). `notBy` covers "not whoever did X"; this covers standing roles |
| A trigger point narrowed to one signature | A narrowing key on trigger points. Until then, a point on `sign` covers every signature of its gated move |
| `route`, and reviewers added at run time | Left to the running system (decision 23). Revisited only if a team asks for a model of it |
| The generated runtime (4.6) | Generated code reads the object, so signature and stage conditions no longer go to the team |

## 14. The owner's decisions

**All product choices were decided in the review of 2026-11-08 (B1 to B8).** The independent check may settle the new names (B8); the release tag waits for the owner. Two consequences of the decided answers are kept visible here, so they are not a surprise once built: one input either vetoes or moves (decision 11), and an ignore never satisfies a required actor (decision 12).

1. **Names, permanent once released.** Recommended: keep the set below. Each stays clear of a name already in use.
   - On a gate: `stages` and `allowSignerAcrossSignatures` (not a wrapper `approval`; decision 1). On a stage: `signatures`, and in part 2 `round` and `when`. On a signature: the gate's own `signers`, `required`, `requiredActors` and `allowRepeatSigner`, plus `when` and `notBy` (not `excludes`, which operations use for the stereotype errors they don't inherit); in part 2 `delegateTo`, `remindAfter`, `escalateAfter`, `escalateTo` and `reauthenticate`. The part kinds `gate-stage` and `gate-signature`.
   - In scenarios: the step's `response` with `sign`, `reject`, `ignore` and `withdraw` (the built-in verbs' keys), and `signature`; in part 2 `delegate`, `delegateTo` and `onBehalfOf`. `expect.signatures`, and in part 2 `expect.notices` with the kinds `remind` and `escalate`. The refusals `gate-excluded` and `gate-not-signed`. The audit outcomes `rejected`, `ignored` and `withdrawn`, and in part 2 `delegated`; the audit record's `signature` and `stage`, and in part 2 `onBehalfOf`. The gate state's `stages` (part 2: `round`, `skipped`), the enabled trigger's `responses`, and the trace's `notices` (part 2). The rule family label "Approvals".
   - In the engine and its contract: the site kind `Approval`, the naming key `ap:approvals`, the index members `approvals` and `signatures`, and the template members of section 7.
   - In `csharp-dapper`: `SignatureResponse`, `ProcessEnvelope.Response` and `.Signature`, `GateDefinition.Stages` and `.AllowSignerAcrossSignatures`, `StageDefinition`, `SignatureDefinition`, `GateOutcome.Rejected`, `.Ignored` and `.Withdrawn`, `Refusals.GateExcluded` and `.GateNotSigned`, `GateAuditRecord.Signature` and `.Stage`, `SignatureEntry.Signature` and `.Stage`, `ApprovalProgress`, `ActRecord`, `<Lifecycle>ApprovalConditions` and `ApprovalCall`; in part 2 `SignatureResponse.Delegate`, `ProcessEnvelope.DelegateTo`, `.OnBehalfOf` and `.Reauthenticated`, `GateOutcome.Delegated`, `Refusals.GateReauthenticate`, `ApprovalNotice` and `ApprovalNotices.Due`.
   - In the editor: Use named signatures, Back to one list of signers, Needed when, Not by, One person may give several signatures, Response, Signature, Picked by the engine, Ignore (abstain); in part 2 Add stage, Round, Skip unless, May be delegated to, Remind after, Escalate after, Escalate to, Sign again with a password.
   - **Names a builder adds** follow these patterns, and are listed in the pull request and the release notes for the check to settle.

## 15. Tests

**Part 1**
- **`ApprovalRuleTests` (new):** failing and passing cases for MQ8201, MQ8202 and MQ9311, and the new cases of MQ3001, MQ3101, MQ3102, MQ3104, MQ3108, MQ9101, MQ9102, MQ9105, MQ9106, MQ9701, MQ9704, MQ9305, MQ9308, MQ8102 and MQ8107. A gate policy with a `notBy` transition. A gate without signers or stages keeps today's finding. Today's models give the same findings.
- **`ApprovalReplayTests` (new):**
  - Counting: two signatures complete independently; the move follows only when all needed signatures are complete; a required actor met through membership; `allowRepeatSigner` on one signature only.
  - Conditions: a signature not needed when `when` is false or unknown; judged on entry and not again after a `modify`; judged again after a rejection; nothing needed lets the next occurrence move, checked as a move without a gate.
  - Responses: a rejection clears every signature of that approval, keeps another gate's, stays in the state and audits `rejected`; an ordinary `reject` move leaves, audits `discarded`, and runs its own guard, rights and trigger points; ignore leaves the count, finishes the person, and never meets a required actor; withdraw removes the latest signature and allows signing again; withdraw with nothing refused `gate-not-signed`; a non-sign response with no open approval refused `no-transition`; a false guard refuses a response as it refuses a signature.
  - Refusal order: each row of decision 14 in turn, each audited `refused`.
  - Separation: the submitter excluded by verb and by move; the window includes the entering move; a gated move's signers excluded by `{ transition }`; an automatic move excludes nobody; one person on two signatures refused `gate-repeat`, and allowed with the flag.
  - The pick: by required actor first, then file order; a step's `signature` overrides it.
  - Trigger points on `reject`, `ignore` and `withdraw`: run with `transitions` at the gate; a refusing check leaves the approval as it was and audits `refused`; a point on `reject` with `transitions` naming the ordinary move runs only there; a named trigger point on `reject` runs before the lifecycle's.
  - Rights: a deny on `reject` refuses a signer's rejection; signers allowed to reject under a deny default.
  - Expectations, MQ9311; recording writes `expect.signatures`.
  - Unchanged traces, audits, gate states and replays for models without stages, including gates, gate policies, rights and trigger points on `sign`.
- **`ResolvedApprovalTests` (new):** `model.approval_lifecycles`, the gate, stage and signature members, `actor.signatures`, records; `ap:approvals` changes on the first approval and not on a signature edit; a gate policy with stages renders each user again.
- **No change:** a model without approvals gives the same output, files, `format --check`, findings, replay, traces and tool answers. The pack golden tests pass untouched.
- **Pack tests:** a new case per pack. `csharp-dapper`'s approval build and run with warnings as errors: a rejection clears the snapshot's progress and makes no move; a refused response saves only its audit; `notBy` refuses the submitter from the envelope's `Signer`; `<Lifecycle>ApprovalConditions` methods named after their signatures, with one named like the class; generated scenario tests agree with the engine's replay. `process-docs`' approval tables, actor signatures and scenario lines.
- **Editor:** unit tests for the conversion both ways, the signatures grid, the Not by popover and the Response cell. Browser tests (in the browser pass) for converting a gate, adding signatures, responding in the simulation panel, and updating expectations from a replay.
- **Acceptance,** on an invented model: the document approval and scenario of section 5, under a deny default with rights.

**Part 2**
- Rounds: side by side in one round, the next after all complete or skipped; a round never moves the object; MQ8203. A stage skipped by `when`, judged at its round's start. Withdraw refused in an earlier round. A rejection in round 2 clears round 1 too.
- Delegation: `delegate` finishes the delegator; the delegate signs on behalf, counts as the delegator's actor for required actors, and is checked by `notBy`; MQ9305 cases.
- Notices: due once per round start on a time step, reset by a rejection; MQ8204, MQ8205; `expect.notices`.
- Packs: `ApprovalNotices.Due` with stable ids; `Reauthenticated` refused when missing; delegation in generated tests.
- No change for a project that uses part 1 only.

## 16. Tasks and the build plan

### Tasks

The sizes are days for a person who knows the code, with tests and a review.

**Part 1**

| Wave | Group | What | Size |
| --- | --- | --- | --- |
| 1 | Contract and model | The schemas, `openapi.yaml`, rule texts; the part kinds; the `Approval` site; the audit, gate state and refusal members; MQ8201, MQ8202 and the changed rules | A day and a half |
| 1 | References, deletes and the sequence | References, deletes, `rename-verb` and `remove-verb`, index rows, localization; `TriggerSequence` for the response verbs | A day |
| 1 | Editor, part one | The Gates tab's approval grid and conversion, quick fixes, the mock's validation and references | A day and a half |
| 2 | Simulation and scenarios | Approvals on entry, responses, the pick, row 7, `notBy` and the acts record, trigger points on the response verbs, the trace, audit and gate state, `expect.signatures`, MQ9311, recording, replay keys | Two days |
| 2 | Resolved model, keys, tools and docs output | `RGateStage`, `RGateSignature`, `model.approval_lifecycles`, `ap:approvals`, records, tool texts and routes; `process-docs` | A day |
| 2 | `csharp-dapper` | The interpreter's approvals, envelope, snapshot, refusals and outcomes; `<Lifecycle>ApprovalConditions`; handlers, contracts and scenario tests | Two days |
| 2 | Editor, part two | Scenario cells, the simulation panel, the mock's stepper | A day and a half |
| 3 | Docs | User guide (processes, scenarios, approvals), releases, modeling skill, capabilities rows, `next.md` | Half a day |

That comes to about eleven days for a person.

**Part 2**

| Wave | Group | What | Size |
| --- | --- | --- | --- |
| 1 | Contract and engine | `round`, a stage's `when`, delegation, reminder, escalation and password keys; rules; references and deletes | A day |
| 1 | Editor | Stages and rounds, the part 2 columns, the mock | A day |
| 2 | Simulation and output | Rounds and skips, delegation, notices, replay; resolved members; both packs, with `ApprovalNotices` and the password refusal | Two days |
| 3 | Docs | User guide, releases, `next.md` | A quarter day |

That comes to about four and a quarter days for a person.

**Checklist for every group.** Anything that reads or writes a gate, a lifecycle's parts, an expression site, a verb, a scenario step or the audit must be found and handled:
- **New part kinds:** `Model/ModelIndexer.cs` (the part kind map and `SubElementKinds`), `Model/PropertySchemas.cs` (`TranslatedHolderKinds`), `Loading/NameIndex.cs`, the list-to-kind map and `PartNoun` in `Loading/ChangePlanner.Cascade.cs`.
- **Expressions:** the `Approval` site in `Expressions/ExprType.cs` and every site switch the `Trigger` kind needed (`Expressions/ExprJs.cs`, `ExprTyper.cs`, `ExprScope.cs`, `ExprSql.cs`, `Rendering/BuiltinHelpers.cs`, `ModelStore.ExpressionPreview.cs`), and `Validation/ExpressionRules.cs` (`ExprSites.Of`).
- **Processes:** `Model/Processes.cs` (the gate, stage and signature; the step's new keys), `Processes/StatechartInterpreter.cs` (`Microstep`, `CheckSigner`, `Sign`, `Cancel`, selection of responses, the admit), `Processes/StepTrace.cs` (`Refusals`, `GateOutcome`, `GateAuditRecord`), `Processes/ProcessApi.cs` (`GateState`, `EnabledTrigger.Responses`), `Processes/ScenarioReplayer.cs`, `Processes/RunRights.cs` (the response verbs at row 7), `Processes/ProcessLibraries.cs` (gate policies with stages), `Processes/TriggerSequence.cs`, and the exporter and importer.
- **Resolver, keys and records:** `Resolution/ResolvedProcesses.cs`, `Resolution/ResolveRun.Processes.cs`, `Editor/ResolvedRecords.cs`, `Planning/DependencyHasher.cs` (`ap`), `Generation/PlanExplainer.cs`.
- **Tools:** `src/Maquettiste.Cli/Mcp/ProcessTools.cs`, `Agent/tools.json` and `src/Maquettiste.Functions/_functions/ProcessEndpoints.cs`, each owned by one agent per wave.
- **Packs:** in `packs/csharp-dapper/`, `interpreter.scriban`, `process-definition.scriban`, `process-handlers.scriban`, `process-contracts.scriban`, `process-store.scriban` and `scenario-tests.scriban`; in `packs/process-docs/`, `process-page.scriban`, `actor-page.scriban` and `_docs.scriban`.
- **Contracts:** the schema copies, `openapi.yaml` and its generated files. `validation-rules.json` is written only by the fixed command.

### How it gets built

**What earlier items cost.** Rights part 1, about 7,500 to 8,500 lines, cost about 1.46M output tokens, 5.17M new input and 372M cached re-reads over about 3.1 hours. Trigger points part 1, about 15,900 lines, cost about 1.49M output, 4.51M new input and 297M cached over about 3 hours, against an estimate of 1.9M, 6.5M and 465M. So the cost of a part follows its agents and waves more than its lines: both parts used about the same, though one was twice the size. Trigger points' estimate ran high by about a third on output and cached. The figures below use the two actual costs, scaled down a little for a smaller part. In every item, the review and the browser pass each found real bugs.

**This item's size,** with tests, without pack expected files.
- **Part 1, about 9,000 to 10,000 lines:** contract, rules and references about 1,300 and their tests about 900; simulation about 1,200 and its tests about 1,000; resolved model, keys, tools and `process-docs` about 900 and tests about 400; `csharp-dapper` about 1,100 and tests about 300; editor and mock about 1,800; docs about 350. The main risks are the interpreter's admit, where responses join signatures, and the generated interpreter's snapshot.
- **Part 2, about 4,500 lines:** contract and engine about 500 and tests about 400; simulation about 700 and tests about 600; resolved model and packs about 900 and tests about 250; editor and mock about 1,000; docs about 200.

**Agents.** Each is capped at about 100 steps or 200k context. Opus 5.5 builds the engine and pack output and runs every review. Sonnet 5.5 builds the editor, the mock and the docs. **At most three agents run at once.**

**Part 1 agents**

| Wave | Agent | Model | Steps | Starts from |
| --- | --- | --- | --- | --- |
| 1 | Contract and model; **contract first**: the schemas, all `openapi.yaml` changes (audit, gate state, enabled trigger, steps, expectations, records), the members in `Processes/StepTrace.cs`, rule texts. Keeps every change to `Expressions/` and `Validation/ExpressionRules.cs` | Opus | About 100 | `main` with 0.18.0 merged |
| 1 | References, deletes, index rows and `TriggerSequence`, through the engine's planner, not the tool files | Opus | About 80 | The contract commit |
| 1 | Editor and mock, part one | Sonnet | About 90 | The contract commit |
| 2 | Simulation and scenarios, through `Processes/ProcessApi.cs` | Opus | About 100, with a fresh agent for the rest if it runs over | Wave 1's engine agents |
| 2 | Resolved model, keys, records, tool texts and routes, replay keys and `process-docs`. **Owns `ProcessTools.cs`, `tools.json` and `ProcessEndpoints.cs`** | Opus | About 80 | Wave 1's engine agents, beside the simulation agent |
| 2 | `csharp-dapper` output | Opus | About 100 | The resolved model agent's first commit (`RGateStage`, `RGateSignature`, `model.approval_lifecycles`), in the first free slot |
| 2 | Editor and mock, part two | Sonnet | About 90 | Wave 1's editor agent, in the next free slot |
| 3 | Review of the pull request | Opus | About 60 | All builders' finish |
| 3 | Docs | Sonnet | About 50 | The builders' finish, beside the review |
| 3 | Browser pass | Sonnet | About 50 | The builders' finish, beside the review |
| 4 | Fixers, split by area (engine and output; editor and mock; docs) | Opus or Sonnet | About 40 each | The review's and the browser pass's findings |
| 4 | A short second browser pass on the fixes | Sonnet | About 25 | The editor fixer's finish |

**Part 2 agents**

| Wave | Agent | Model | Steps | Starts from |
| --- | --- | --- | --- | --- |
| 1 | Contract and engine; contract first | Opus | About 80 | `main` once part 1 is merged |
| 1 | Editor and mock | Sonnet | About 70 | The contract commit |
| 2 | Simulation, resolved model and both packs. Owns the tool files | Opus | About 100 | The contract and engine agent's finish |
| 3 | Review, docs, browser pass | Opus, Sonnet, Sonnet | About 50, 30, 30 | All builders' finish |
| 4 | Fixers, split by area | Opus or Sonnet | About 30 each | The findings |

- **One pull request per part, into `main`,** with the contract, engine, editor, output, docs and fix commits. Each is its own release. The owner merges part 1 first, and tags each.
- **Generated files** (the editor's API types, the recorded rule list, the sample app's copies) are written only by the fixed commands, after each merge into the branch. Pack expected files stay with the builder, so their changes are reviewed.
- **Fixers** are fresh agents with the exact findings. **A design gap found while building** stops the work, and the owner is asked.

**Estimate.**

**Part 1**

| | Wall time | Output | New input | Cached re-reads |
| --- | --- | --- | --- | --- |
| Wave 1 (contract, references, editor part one) | About 1 hour | 0.35M | 1.2M | 80M |
| Wave 2 (simulation, resolved model and docs output, `csharp-dapper`, editor part two) | About 1.2 hours | 0.55M | 1.8M | 125M |
| Wave 3 (review, docs, browser pass) | About 0.4 hours | 0.2M | 0.7M | 50M |
| Wave 4 (fixes, second browser pass, CI) | About 0.4 hours | 0.2M | 0.7M | 45M |
| In all | **About 3 hours** | **About 1.3M** | **About 4.4M** | **About 300M** |

**Part 1 stays under the 400M cap without a further split.** Its likely range is 260M to 360M: trigger points part 1 came in at 297M, and rights part 1, the heaviest so far, at 372M. If the simulation agent needs a fresh agent to finish, add about 30M. If wave 2 ends above 330M, the rest of part 1 is tracked step by step, and the owner is told before wave 4 starts.

**Part 2**

| | Wall time | Output | New input | Cached re-reads |
| --- | --- | --- | --- | --- |
| Wave 1 (contract and engine, editor) | About 0.7 hours | 0.2M | 0.7M | 45M |
| Wave 2 (simulation and output) | About 0.9 hours | 0.3M | 1.0M | 65M |
| Wave 3 (review, docs, browser pass) | About 0.5 hours | 0.15M | 0.5M | 35M |
| Wave 4 (fixes, CI) | About 0.4 hours | 0.1M | 0.4M | 25M |
| In all | **About 2.5 hours** | **About 0.75M** | **About 2.6M** | **About 170M** |

**Both parts:** about 13,500 to 14,500 lines, about 5.5 hours, about 2.05M output, about 7M new input and about 470M cached re-reads, each part well under the cap.

## Sources

From the study of about 20 systems. Those marked "checked" were opened again for this design and say what is quoted. Vendor names appear only here, as prior art.

- [2] PTC Windchill, route types (two level approval) — https://support.ptc.com/help/wnc/r12.1.0.0/en/Windchill_Help_Center/projectmgmt/ProjMgmtRouteAbout.html
- [5] Siemens Teamcenter ITK, signoff functions (quorum, decisions) — https://docs.plm.automation.siemens.com/data_services/resources/tc/12.1/tcrefs/custom/en_US/ITKFunction/group___s_i_g_n_o_f_f.html
- [6] Aras community, voting weight — https://www.aras.com/community/f/archive/2182/developers-forum---workflow-activity-assignment-defining-voting-weight
- [7] Oracle Agile PLM, Product Collaboration guide, routing — https://docs.oracle.com/cd/E50306_29/otn/pdf/user/html_agabd/output/chapter_7.htm
- [8] Odoo PLM approvals — https://www.odoo.com/documentation/18.0/applications/inventory_and_mrp/plm/management/approvals.html
- [9] SAP release strategy — https://help.sap.com/saphelp_46C/helpdata/EN/a8/2d547bb435d1118b3f0060b03ca329/content.htm
- [10] Salesforce Metadata API, ApprovalProcess (value names unverified) — https://developer.salesforce.com/docs/atlas.en-us.264.0.api_meta.meta/api_meta/meta_approvalprocess.htm
- [11] Jira Service Management approvals — https://confluence.atlassian.com/adminjiraserver112/configuring-jira-service-management-approvals-1688897276.html
- [13] Business Central, approval users (requester and approver) — https://learn.microsoft.com/dynamics365/business-central/across-how-to-set-up-approval-users
- [14] Power Automate approvals — https://learn.microsoft.com/en-us/power-automate/get-started-approvals
- [15] DocuSign routing order (community tip) — https://community.docusign.com/tips-from-docusign-155/docusign-trainer-tips-mastering-signing-order-22771
- [16] Oracle Agile PLM 9.3.5 Administrator guide, chapter 11 (checked: "If Rejected, Set Status To", Password Required, reminder and escalation periods, the escalation person, comments per response) — https://docs.oracle.com/cd/E69230_15/otn/pdf/administration/html_agaah/output/chapter_11.htm
- [17] ServiceNow, Ask for Approval — https://www.servicenow.com/docs/r/build-workflows/workflow-studio/ask-approval-flow-designer.html
- [23] Business Central, approve, reject, delegate — https://learn.microsoft.com/dynamics365/business-central/across-how-use-approval-workflows
- [27] ServiceNow delegation — https://www.servicenow.com/docs/r/servicenow-platform/self-service/t_DelegateApprovalsTasks.html
- [32] 21 CFR 11.200 (read in the study; the page refused a second fetch) — https://www.ecfr.gov/current/title-21/chapter-I/subchapter-A/part-11/subpart-C/section-11.200
- ENOVIA eMatrix lifecycle help (checked: Reject blocks promotion, Ignore marks the requirement "not necessary for promotion", a new signature replaces the old) — https://hsn.halliburton.com/ematrix/doc/common/en/lifecycle4.html
