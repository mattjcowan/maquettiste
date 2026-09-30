// A small process for the statechart canvas's unit tests: a parallel state with two regions, a gated transition, a
// targetless timer, a transition with two targets, a deep history state and pseudo-states.
import type { ProcessDoc } from "@/model/process";

export const purchase: ProcessDoc = {
  kind: "process",
  id: "P",
  name: "PurchaseApproval",
  events: [
    { id: "submit", name: "submit" },
    { id: "approve", name: "approve" },
  ],
  guards: [{ id: "large", name: "isLarge" }],
  actions: [
    { id: "remind", name: "sendReminder" },
    { id: "log", name: "log" },
  ],
  states: [
    { id: "draft", name: "Drafting" },
    {
      id: "review",
      name: "Review",
      type: "parallel",
      states: [
        {
          id: "budget",
          name: "Budget",
          type: "compound",
          states: [
            { id: "checking", name: "Checking", invoke: [{ id: "check", name: "checkBudget", type: "service" }] },
            { id: "ok", name: "BudgetOk", type: "final" },
          ],
        },
        {
          id: "compliance",
          name: "Compliance",
          type: "compound",
          initial: "cleared",
          states: [
            { id: "pending", name: "Pending" },
            { id: "cleared", name: "Cleared", type: "final" },
          ],
        },
      ],
    },
    { id: "approval", name: "Approval", displayName: "Awaiting approval" },
    { id: "hist", name: "Resume", type: "history", history: "deep" },
    { id: "done", name: "Ordered", type: "final" },
  ],
  transitions: [
    { id: "t1", source: "draft", event: "submit", guard: "large", actions: ["log"], targets: ["review"] },
    { id: "t2", source: "checking", trigger: "invoke-done", invoke: "check", targets: ["ok"] },
    { id: "t3", source: "review", trigger: "done", targets: ["approval"] },
    {
      id: "t4",
      source: "approval",
      event: "approve",
      targets: ["done"],
      gate: { id: "g", name: "purchaseApproval", displayName: "Purchase approval", required: 2, signers: ["a1", "a2", "a3"] },
    },
    { id: "t5", source: "approval", trigger: "after", after: "P5D", actions: ["remind"] },
    { id: "t6", source: "pending", trigger: "always", targets: ["cleared", "approval"] },
  ],
};
