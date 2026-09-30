// The mock model's processes, actors and scenarios (phase-3-design.md 6.1, round P3), added to the billing fixture
// by seed.ts: the billing fixture is shared with the engine's tests, so these documents live with the mocks. The
// orchestration keeps the ids of the processes fixture's PurchaseApproval (tests/fixtures/models/processes), so the
// engine recordings of simulate and export (src/mocks/recorded/) answer for it; its actors are reduced to three.
// The `drift` scenario adds a Payment status enum and a second lifecycle whose states differ from it (MQ9203), so
// the Sync enum quick fix can be walked; the default model validates clean.
import type { Seed, SeedFile } from "./store";

type Json = Record<string, unknown>;

export const BILLING = "01J92P0V01KDRN8GX5PGYCNKSX";
export const INVOICE = "01J92P0V0FJ23CGSNKM7P1W5V7";
export const INVOICE_STATUS = "01J92P0V0VB1Z49SWERMAGR4TV";
const PAYMENT = "01J92P0V0HEGSC6MW92CST5KA6";

export const PURCHASE_APPROVAL = "01JQPRC0000000000000000002";
export const INVOICE_LIFECYCLE = "01JQMCK0000000000000000001";
export const PAYMENT_LIFECYCLE = "01JQMCK0000000000000000002";
export const BUDGET_HOLDER = "01JQACT0000000000000000008";
export const APPROVER = "01JQACT0000000000000000004";
export const PROCUREMENT = "01JQACT0000000000000000007";
export const BUDGET_REJECTED = "01JQSCN0000000000000000010";
export const QUICK_APPROVAL = "01JQMCK0000000000000000101";
export const PAY_IN_FULL = "01JQMCK0000000000000000102";

/** Scenarios the mock's verify reports as failing, with the failure the engine would give (the mock has no interpreter). */
export const MOCK_FAILURES: Record<string, { step: number; rule: string; message: string; expected: unknown; actual: unknown }> = {
  [QUICK_APPROVAL]: {
    step: 1,
    rule: "MQ9302",
    message: "Step 2 expected the states Approval but the process is in BudgetOk, Pending.",
    expected: ["01JQSTA0000000000000000110"],
    actual: ["01JQSTA0000000000000000105", "01JQSTA0000000000000000108"],
  },
};

const S = (n: number) => `01JQSTA0000000000000000${String(n).padStart(3, "0")}`;
const X = (n: number) => `01JQPRX00000000000000000${String(n).padStart(2, "0")}`;
const T = (n: number) => `01JQTRN00000000000000000${String(n).padStart(2, "0")}`;
const M = (n: number) => `01JQMCK0000000000000000${String(n).padStart(3, "0")}`;

const actors: Json[] = [
  {
    kind: "actor",
    id: BUDGET_HOLDER,
    name: "BudgetHolder",
    type: "person",
    stereotypes: ["persona"],
    properties: { goals: ["Keep spending inside the budget", "Approve urgent purchases quickly"] },
  },
  { kind: "actor", id: APPROVER, name: "Approver", type: "role" },
  { kind: "actor", id: PROCUREMENT, name: "ProcurementSystem", type: "external-system" },
];

const purchaseApproval: Json = {
  kind: "process",
  id: PURCHASE_APPROVAL,
  name: "PurchaseApproval",
  displayName: "Purchase approval",
  package: BILLING,
  description: "A purchase request checked for budget and compliance in parallel, approved by two signers and ordered.",
  context: [
    { id: "01JQATT0000000000000000201", name: "amount", type: "decimal", default: 0 },
    { id: "01JQATT0000000000000000202", name: "reminders", type: "int32", default: 0 },
  ],
  events: [
    { id: X(14), name: "submit", actors: [BUDGET_HOLDER] },
    { id: X(15), name: "requestChanges", actors: [BUDGET_HOLDER] },
    { id: X(16), name: "approve", actors: [APPROVER] },
    { id: X(17), name: "reject", payload: [{ id: "01JQATT0000000000000000203", name: "reason", type: "text", required: true }], actors: [APPROVER] },
  ],
  actions: [{ id: X(18), name: "sendReminder", expression: "({ reminders: context.reminders + 1 })" }],
  states: [
    { id: S(101), name: "Drafting" },
    {
      id: S(102),
      name: "Review",
      type: "parallel",
      states: [
        {
          id: S(103),
          name: "Budget",
          type: "compound",
          states: [
            { id: S(104), name: "Checking", invoke: [{ id: X(19), name: "checkBudget", type: "service" }] },
            { id: S(105), name: "BudgetOk", type: "final" },
            { id: S(106), name: "BudgetRejected" },
          ],
        },
        {
          id: S(107),
          name: "Compliance",
          type: "compound",
          states: [
            { id: S(108), name: "Pending", invoke: [{ id: X(20), name: "complianceReview", type: "human-task", actors: [APPROVER] }] },
            { id: S(109), name: "Cleared", type: "final" },
          ],
        },
      ],
    },
    { id: S(110), name: "Approval" },
    { id: S(111), name: "Ordering", invoke: [{ id: X(21), name: "createPurchaseOrder", type: "service", actors: [PROCUREMENT] }] },
    { id: S(112), name: "Ordered", type: "final" },
    { id: S(113), name: "Rejected", type: "final" },
  ],
  transitions: [
    { id: T(15), source: S(101), event: X(14), targets: [S(102)] },
    { id: T(16), source: S(104), trigger: "invoke-done", invoke: X(19), targets: [S(105)] },
    { id: T(17), source: S(104), trigger: "invoke-error", invoke: X(19), targets: [S(106)] },
    { id: T(18), source: S(106), event: X(15), targets: [S(101)] },
    { id: T(19), source: S(108), trigger: "invoke-done", invoke: X(20), targets: [S(109)] },
    { id: T(20), source: S(102), trigger: "done", targets: [S(110)] },
    {
      id: T(21),
      source: S(110),
      event: X(16),
      targets: [S(111)],
      gate: {
        id: X(22),
        name: "purchaseApproval",
        displayName: "Purchase approval",
        required: 2,
        signers: [APPROVER],
        meanings: [{ id: X(23), name: "approved", displayName: "Approved for purchase" }],
        auditAttributes: [{ id: "01JQATT0000000000000000204", name: "costCentre", type: "string" }],
      },
    },
    { id: T(22), source: S(110), event: X(17), targets: [S(113)] },
    { id: T(23), source: S(110), trigger: "after", after: "P5D", actions: [X(18)] },
    { id: T(24), source: S(111), trigger: "invoke-done", invoke: X(21), targets: [S(112)] },
  ],
};

/** A lifecycle over an enum's members, one root state per member, with events moving along `moves`. */
function lifecycle(
  id: string,
  name: string,
  subject: string,
  attribute: string,
  states: [string, string][],
  moves: [string, string, string][],
  n: number,
): Json {
  const ids = new Map(states.map(([sid, s]) => [s, sid]));
  const events = [...new Set(moves.map(([e]) => e))].map((e, i) => ({ id: M(n + i), name: e }));
  const eventId = new Map(events.map((e) => [e.name, e.id]));
  return {
    kind: "process",
    id,
    name,
    package: BILLING,
    use: "lifecycle",
    subject,
    boundAttribute: attribute,
    events,
    states: states.map(([sid, s]) => ({ id: sid, name: s })),
    transitions: moves.map(([e, from, to], i) => ({ id: M(n + 50 + i), source: ids.get(from), event: eventId.get(e), targets: [ids.get(to)] })),
  };
}

const invoiceLifecycle = lifecycle(
  INVOICE_LIFECYCLE,
  "InvoiceLifecycle",
  INVOICE,
  INVOICE_STATUS,
  [
    [M(201), "Draft"],
    [M(202), "Issued"],
    [M(203), "Paid"],
    [M(204), "Void"],
  ],
  [
    ["issue", "Draft", "Issued"],
    ["pay", "Issued", "Paid"],
    ["void", "Issued", "Void"],
  ],
  301,
);

const scenarios: Json[] = [
  {
    kind: "scenario",
    id: BUDGET_REJECTED,
    name: "BudgetRejected",
    displayName: "Budget rejected",
    process: PURCHASE_APPROVAL,
    start: { context: { "01JQATT0000000000000000201": 2500 } },
    steps: [
      { id: "01JQSTP0000000000000000035", event: X(14), actor: BUDGET_HOLDER, expect: { states: [S(104), S(108)] } },
      { id: "01JQSTP0000000000000000036", input: "invoke-error", invoke: X(19), expect: { states: [S(106), S(108)] } },
      {
        id: "01JQSTP0000000000000000037",
        input: "invoke-done",
        invoke: X(20),
        actor: APPROVER,
        expect: { states: [S(106), S(109)] },
        description: "Compliance clears it, but the budget region never completes.",
      },
    ],
  },
  {
    kind: "scenario",
    id: QUICK_APPROVAL,
    name: "QuickApproval",
    displayName: "Quick approval",
    process: PURCHASE_APPROVAL,
    steps: [
      { id: M(111), event: X(14), actor: BUDGET_HOLDER, expect: { states: [S(104), S(108)] } },
      { id: M(112), input: "invoke-done", invoke: X(19), expect: { states: [S(110)] }, description: "Expects approval before compliance has cleared it." },
    ],
  },
  {
    kind: "scenario",
    id: PAY_IN_FULL,
    name: "PayInFull",
    displayName: "Pay in full",
    process: INVOICE_LIFECYCLE,
    steps: [
      { id: M(121), event: M(301), expect: { states: [M(202)] } },
      { id: M(122), event: M(302), expect: { states: [M(203)] } },
    ],
  },
];

const persona: Json = { kind: "stereotype", id: M(401), key: "persona", name: "Persona", appliesTo: ["actor"] };
const personaExtension: Json = {
  name: "persona",
  appliesTo: { kinds: ["actor"], stereotypes: ["persona"] },
  properties: { goals: { type: "array", items: { type: "string" } } },
};

const file = (path: string, json: Json): SeedFile => ({ path, text: JSON.stringify(json, null, 2) + "\n" });

/** The documents the billing seed gains. */
export function processFiles(): SeedFile[] {
  return [
    file("extensions/persona.json", personaExtension),
    file("model/vocabularies/stereotypes/persona.json", persona),
    ...actors.map((a) => file(`model/actors/${kebab(String(a.name))}.json`, a)),
    file("model/processes/purchase-approval.json", purchaseApproval),
    file("model/processes/invoice-lifecycle.json", invoiceLifecycle),
    ...scenarios.map((s) => file(`model/scenarios/${kebab(String(s.name))}.json`, s)),
  ];
}

/** Rewrites one seed file's document. */
function patch(files: SeedFile[], suffix: string, update: (json: Json) => void): SeedFile[] {
  return files.map((f) => {
    if (!f.path.endsWith(suffix)) return f;
    const json = JSON.parse(f.text) as Json;
    update(json);
    return { ...f, text: JSON.stringify(json, null, 2) + "\n" };
  });
}

/** The shared billing fixture's Invoice names its lifecycle (mock-only: the fixture holds no processes). */
export function bindLifecycles(files: SeedFile[]): SeedFile[] {
  return patch(files, "model/entities/invoice.json", (json) => void (json.lifecycle = INVOICE_LIFECYCLE));
}

/**
 * The `lifecycle` scenario: one finding per quick-fix kind. Invoice no longer names its lifecycle (MQ9201, the
 * set-lifecycle operation), its status defaults to Issued (MQ9205, a plain update), and Purchase approval's initial is
 * a nested state (MQ9001, the set-initial operation with a picker).
 */
export function withLifecycleProblems(seed: Seed): Seed {
  let files = patch(seed.files, "model/entities/invoice.json", (json) => {
    delete json.lifecycle;
    for (const a of (json.attributes as Json[]) ?? []) if (a.id === INVOICE_STATUS) a.default = "Issued";
  });
  files = patch(files, "model/processes/purchase-approval.json", (json) => {
    const compound = ((json.states as Json[]) ?? []).find((st) => Array.isArray(st.states) && st.states.length);
    json.initial = ((compound?.states as Json[]) ?? [])[0]?.id;
  });
  return { ...seed, files };
}

/** The `drift` scenario: a Payment status enum and attribute, and a lifecycle with a state the enum lacks (MQ9203). */
export function withDrift(seed: Seed): Seed {
  const statusEnum = M(501);
  const statusAttribute = M(502);
  const files = seed.files.map((f) => {
    if (!f.path.endsWith("model/entities/payment.json")) return f;
    const json = JSON.parse(f.text) as Json;
    json.attributes = [...((json.attributes as Json[]) ?? []), { id: statusAttribute, name: "status", type: { ref: statusEnum }, default: "Pending" }];
    json.lifecycle = PAYMENT_LIFECYCLE;
    return { ...f, text: JSON.stringify(json, null, 2) + "\n" };
  });
  files.push(
    file("model/enums/payment-status.json", {
      kind: "enum",
      id: statusEnum,
      name: "PaymentStatus",
      package: BILLING,
      members: [
        { id: M(511), name: "Pending" },
        { id: M(512), name: "Received" },
        { id: M(513), name: "Refunded" },
      ],
    }),
    file(
      "model/processes/payment-lifecycle.json",
      lifecycle(
        PAYMENT_LIFECYCLE,
        "PaymentLifecycle",
        PAYMENT,
        statusAttribute,
        [
          [M(521), "Pending"],
          [M(522), "Received"],
          [M(523), "Disputed"],
          [M(524), "Refunded"],
        ],
        [
          ["receive", "Pending", "Received"],
          ["dispute", "Received", "Disputed"],
          ["refund", "Disputed", "Refunded"],
        ],
        331,
      ),
    ),
  );
  return { ...seed, files };
}

function kebab(name: string): string {
  return name.replace(/([a-z0-9])([A-Z])/g, "$1-$2").toLowerCase();
}
