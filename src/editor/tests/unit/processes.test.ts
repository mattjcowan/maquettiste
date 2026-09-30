// The Processes explorer's rows (phase-3-design.md 6.1), the process dialogs' models and the mock's process operations.
import { describe, expect, it } from "vitest";
import { MockBackend } from "@/mocks/backend";
import { buildForest, childKeys, documentChildren, nodeOf, type Forest } from "@/explorer/tree";
import { menuFor } from "@/explorer/menus";
import {
  NEW_STATUS,
  buildActor,
  buildProcessBatch,
  buildScenario,
  deleteProcessOps,
  enumAttributes,
  goalsStereotypes,
  importPreview,
  parseConfig,
  processProblem,
} from "@/explorer/processCreate";
import { exportConfig, importDocument, initialLeaves } from "@/mocks/processHandlers";
import { BUDGET_REJECTED, INVOICE, INVOICE_LIFECYCLE, INVOICE_STATUS, PAYMENT_LIFECYCLE, PURCHASE_APPROVAL, QUICK_APPROVAL } from "@/mocks/model/processSeed";
import { useMockApi } from "./harness";

const labels = (f: Forest, key: string) => childKeys(f, key).map((k) => nodeOf(f, k)!.label);
const child = (f: Forest, key: string, label: string) => childKeys(f, key).find((k) => nodeOf(f, k)!.label === label)!;

function forest(scenarioStatus?: Map<string, { passed: true } | { passed: false; step: number }>) {
  const backend = new MockBackend();
  return { backend, forest: buildForest({ rows: backend.model.index(), scenarioStatus }) };
}

describe("Processes explorer", () => {
  it("groups processes by domain, hides domains without processes, then lists Actors", () => {
    const { forest: f } = forest();
    expect(f.headers.processes).toBe("2 processes · 3 actors · 3 scenarios");
    expect(labels(f, f.roots.processes)).toEqual(["Billing", "Actors"]);
    const billing = child(f, f.roots.processes, "Billing");
    expect(nodeOf(f, billing)!.secondary).toBe("2 processes");
    expect(labels(f, billing)).toEqual(["InvoiceLifecycle", "Purchase approval"]);
    const lifecycle = nodeOf(f, child(f, billing, "InvoiceLifecycle"))!;
    expect(lifecycle.secondary).toBe("lifecycle · Invoice · 4 states");
    expect(nodeOf(f, child(f, billing, "Purchase approval"))!.secondary).toBe("orchestration · 13 states");
    const actors = child(f, f.roots.processes, "Actors");
    expect(labels(f, actors)).toEqual(["Approver", "BudgetHolder", "ProcurementSystem"]);
    expect(childKeys(f, actors).map((k) => nodeOf(f, k)!.secondary)).toEqual(["role", "person · persona", "external system"]);
  });

  it("lists a process's scenarios with steps and status, and its States and Events once the document loads", () => {
    const status = new Map([
      [BUDGET_REJECTED, { passed: true as const }],
      [QUICK_APPROVAL, { passed: false as const, step: 2 }],
    ]);
    const { backend, forest: f } = forest(status);
    const key = `@processes/p:${PURCHASE_APPROVAL}`;
    const scenarios = child(f, key, "Scenarios");
    expect(nodeOf(f, scenarios)!.secondary).toBe("✓ 1 passed");
    expect(childKeys(f, scenarios).map((k) => `${nodeOf(f, k)!.label}: ${nodeOf(f, k)!.secondary}`)).toEqual([
      "Budget rejected: 3 steps · passed",
      "Quick approval: 2 steps · failed at step 2",
    ]);
    documentChildren(f, key, backend.model.get(PURCHASE_APPROVAL)!);
    expect(labels(f, key)).toEqual(["States", "Events", "Scenarios"]);
    const states = child(f, key, "States");
    expect(nodeOf(f, states)!.secondary).toBe("13");
    expect(labels(f, states)).toEqual(["Drafting", "Review", "Approval", "Ordering", "Ordered", "Rejected"]);
    const review = child(f, states, "Review");
    expect(nodeOf(f, review)!.secondary).toBe("parallel");
    expect(labels(f, review)).toEqual(["Budget", "Compliance"]);
    expect(nodeOf(f, review)!.kind).toBe("state");
    expect(labels(f, child(f, key, "Events"))).toEqual(["approve", "reject", "requestChanges", "submit"]);
    // A lifecycle names its bound attribute once the subject is read.
    const lifecycle = `@processes/p:${INVOICE_LIFECYCLE}`;
    documentChildren(f, lifecycle, backend.model.get(INVOICE_LIFECYCLE)!, "status");
    expect(nodeOf(f, lifecycle)!.secondary).toBe("lifecycle · Invoice.status · 4 states");
    expect(labels(f, child(f, lifecycle, "Scenarios"))).toEqual(["Pay in full"]);
    expect(nodeOf(f, childKeys(f, child(f, lifecycle, "Scenarios"))[0])!.secondary).toBe("2 steps · not run");
  });

  it("keeps processes in the Domain model's Processes folder and actors in People and access", () => {
    const { forest: f } = forest();
    const billing = child(f, f.roots["domain-model"], "Billing");
    expect(labels(f, child(f, billing, "Processes"))).toEqual(["InvoiceLifecycle", "Purchase approval"]);
    const people = child(f, f.roots["domain-model"], "People and access");
    expect(labels(f, child(f, people, "Actors"))).toEqual(["Approver", "BudgetHolder", "ProcurementSystem"]);
  });

  it("offers the section 1.8 actions on process rows and New process and Import on a domain", () => {
    const process = menuFor([{ type: "element", kind: "process", element: true }]);
    expect(process.map((i) => i.label)).toEqual([
      "Open",
      "Open in new tab",
      "Simulate",
      "Verify scenarios",
      "Export XState",
      "Where used",
      "Move to domain…",
      "Rename",
      "Add to favorites",
      "Delete",
    ]);
    expect(process.find((i) => i.id === "simulate")!.disabledNote).toBe("Arrives with the simulation panel");
    expect(menuFor([{ type: "group", kind: "process", element: false, domainGroup: true, explorer: "processes" }]).map((i) => i.label)).toEqual([
      "New process…",
      "Import XState…",
      "Expand all",
    ]);
    expect(menuFor([{ type: "folder", kind: "scenario", element: false }])[0].label).toBe("New scenario…");
    expect(menuFor([{ type: "folder", kind: "actor", element: false }])[0].label).toBe("New actor…");
  });
});

describe("process dialog models", () => {
  let n = 0;
  const id = () => `ID${++n}`;
  const invoice = {
    json: { kind: "entity", id: INVOICE, name: "Invoice", package: "P1", attributes: [{ id: INVOICE_STATUS, name: "status", type: { ref: "E1" } }] },
    hash: "h1",
  };

  it("builds a lifecycle from an enum's members, binding the entity in the same batch", () => {
    expect(enumAttributes(invoice.json, (x) => x === "E1")).toEqual([{ id: INVOICE_STATUS, name: "status", enumId: "E1" }]);
    const input = {
      name: "InvoiceFlow",
      domain: "P1",
      use: "lifecycle" as const,
      subject: INVOICE,
      subjectDoc: invoice,
      attribute: INVOICE_STATUS,
      members: ["Draft", "Paid"],
    };
    expect(processProblem(input)).toBeNull();
    const { ops, processId } = buildProcessBatch(input, id);
    expect(ops.map((o) => o.op)).toEqual(["create", "update"]);
    const process = (ops[0] as { element: Record<string, unknown> }).element;
    expect(process).toMatchObject({ kind: "process", id: processId, use: "lifecycle", subject: INVOICE, boundAttribute: INVOICE_STATUS, package: "P1" });
    expect((process.states as { name: string }[]).map((s) => s.name)).toEqual(["Draft", "Paid"]);
    expect(ops[1]).toMatchObject({ op: "update", id: INVOICE, expectedHash: "h1", element: { lifecycle: processId } });
  });

  it("turns the subject's previous lifecycle into an orchestration in the same batch", () => {
    const bound = { json: { ...invoice.json, lifecycle: "OLD" }, hash: "h1" };
    const input = {
      name: "InvoiceFlow3",
      domain: null,
      use: "lifecycle" as const,
      subject: INVOICE,
      subjectDoc: bound,
      attribute: INVOICE_STATUS,
      members: ["Draft"],
    };
    expect(processProblem(input)).toBe("Loading the entity's lifecycle…");
    const previousDoc = {
      json: { kind: "process", id: "OLD", name: "InvoiceLifecycle", use: "lifecycle", subject: INVOICE, boundAttribute: INVOICE_STATUS },
      hash: "h2",
    };
    const { ops, processId } = buildProcessBatch({ ...input, previousDoc }, id);
    expect(ops.map((o) => o.op)).toEqual(["create", "update", "update"]);
    expect(ops[1]).toMatchObject({ id: INVOICE, element: { lifecycle: processId } });
    const unbound = (ops[2] as { id: string; expectedHash: string; element: Record<string, unknown> }).element;
    expect(ops[2]).toMatchObject({ id: "OLD", expectedHash: "h2" });
    expect(unbound.use).toBeUndefined();
    expect(unbound.boundAttribute).toBeUndefined();
  });

  it("creates a new status attribute and enum when asked, and an orchestration starts with Initial", () => {
    const { ops } = buildProcessBatch(
      {
        name: "InvoiceFlow",
        domain: null,
        use: "lifecycle",
        subject: INVOICE,
        subjectDoc: invoice,
        attribute: NEW_STATUS,
        takenTypeNames: new Set(["invoicestatus"]),
      },
      id,
    );
    expect(ops.map((o) => o.op)).toEqual(["create", "create", "update"]);
    const created = (ops[1] as { element: Record<string, unknown> }).element;
    expect(created).toMatchObject({ kind: "enum", name: "InvoiceStatus2", package: "P1", members: [{ name: "Initial" }] });
    const entity = (ops[2] as unknown as { element: { attributes: { name: string; type: unknown }[] } }).element;
    expect(entity.attributes.at(-1)).toMatchObject({ name: "status2", type: { ref: created.id }, default: "Initial" });
    const orchestration = buildProcessBatch({ name: "Flow", domain: null, use: "orchestration", subject: null }, id);
    expect(orchestration.ops).toHaveLength(1);
    expect((orchestration.ops[0] as unknown as { element: { states: { name: string }[]; use?: string } }).element.states.map((s) => s.name)).toEqual([
      "Initial",
    ]);
    expect(processProblem({ name: "Flow", domain: null, use: "lifecycle", subject: null })).toBe("Choose the entity whose lifecycle this is.");
    expect(processProblem({ name: "1x", domain: null, use: "orchestration", subject: null })).toMatch(/letters/);
  });

  it("builds actors with goals, empty scenarios, the process delete and the import preview", () => {
    expect(buildActor({ name: " Clerk ", type: "person", stereotypes: ["persona"], goals: "Close the month\n\n Pay on time " }, id)).toMatchObject({
      kind: "actor",
      name: "Clerk",
      type: "person",
      stereotypes: ["persona"],
      properties: { goals: ["Close the month", "Pay on time"] },
    });
    expect(buildActor({ name: "Bank", type: "external-system", stereotypes: [] }, id)).not.toHaveProperty("properties");
    expect(
      goalsStereotypes([
        { appliesTo: { kinds: ["actor"], stereotypes: ["persona"] }, properties: { goals: {} } },
        { appliesTo: { kinds: ["entity"] }, properties: { goals: {} } },
      ]),
    ).toEqual(new Set(["persona"]));
    const scenario = buildScenario("Quick", { id: "PR", events: [{ id: "EV" }] }, id);
    expect(scenario).toMatchObject({ kind: "scenario", process: "PR", steps: [{ event: "EV" }] });
    expect(deleteProcessOps({ id: "PR", hash: "a" }, [{ id: "S1", hash: "b" }]).map((o) => `${o.op} ${(o as { id: string }).id}`)).toEqual([
      "delete S1",
      "delete PR",
    ]);
    expect(parseConfig("")).toHaveProperty("problem");
    expect(parseConfig("[1]")).toEqual({ problem: "The config must be a JSON object." });
    const { document, created } = importDocument(
      { id: "Door", initial: "Closed", states: { Closed: { on: { open: "Opened" } }, Opened: { on: { close: "Closed" } } } },
      "Door",
      "P1",
      "PR",
    );
    expect(importPreview({ document, created, removed: [], diagnostics: [{ severity: "warning" }] })).toEqual({
      created: 6,
      removed: 0,
      states: ["Closed", "Opened"],
      events: ["open", "close"],
      errors: 0,
      warnings: 1,
    });
  });
});

describe("mock process operations", () => {
  const mock = useMockApi();
  const post = (path: string, body: unknown = {}) =>
    fetch(mock.url(path), { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(body) });

  it("replays the recorded simulation, verifies with one failure, and exports and imports", async () => {
    const sim = await (await post(`/api/processes/${PURCHASE_APPROVAL}/simulate`, { scenario: BUDGET_REJECTED })).json();
    expect(sim.configuration).toEqual(["01JQSTA0000000000000000106", "01JQSTA0000000000000000109"]);
    const fresh = await (await post(`/api/processes/${INVOICE_LIFECYCLE}/simulate`)).json();
    expect(fresh.configuration).toEqual([initialLeaves((mock.backend.model.get(INVOICE_LIFECYCLE)!.json as unknown as { states: never }).states)[0]]);
    const verify = await (await post(`/api/processes/${PURCHASE_APPROVAL}/verify`)).json();
    expect(verify.passed).toBe(false);
    expect(verify.results.map((r: { name: string; passed: boolean }) => `${r.name} ${r.passed}`)).toEqual(["BudgetRejected true", "QuickApproval false"]);
    const exported = await fetch(mock.url(`/api/processes/${INVOICE_LIFECYCLE}/export?format=xstate`));
    expect(JSON.parse(await exported.text())).toEqual(exportConfig(mock.backend.model.get(INVOICE_LIFECYCLE)!.json as never));
    const config = { id: "Door", states: { Closed: { on: { open: "Opened" } }, Opened: {} } };
    const dry = await (await post("/api/processes/import?format=xstate", { config, package: "Billing" })).json();
    expect(dry.applied).toBe(false);
    expect(mock.backend.model.index().some((r) => r.name === "Door")).toBe(false);
    const applied = await (await post("/api/processes/import?format=xstate&dryRun=false", { config, package: "Billing" })).json();
    expect(applied.applied).toBe(true);
    expect(mock.backend.model.index().find((r) => r.name === "Door")).toMatchObject({ kind: "process", stateCount: 2, use: "orchestration" });
  });
});

describe("mock enum drift (the drift scenario)", () => {
  const mock = useMockApi({ scenarios: ["drift"] });

  it("reports MQ9203 and syncs the enum from the process", async () => {
    expect(
      mock.backend.model
        .validate()
        .diagnostics.filter((d) => d.rule === "MQ9203")
        .map((d) => d.elementId),
    ).toEqual([PAYMENT_LIFECYCLE]);
    const post = (body: unknown) =>
      fetch(mock.url(`/api/processes/${PAYMENT_LIFECYCLE}/sync-enum`), {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(body),
      });
    const dry = await (await post({ dryRun: true })).json();
    expect(dry).toMatchObject({ added: ["Disputed"], removed: [], applied: false });
    const applied = await (await post({})).json();
    expect(applied.applied).toBe(true);
    expect(mock.backend.model.validate().diagnostics.some((d) => d.rule === "MQ9203")).toBe(false);
    expect(
      await (
        await fetch(mock.url(`/api/processes/${PURCHASE_APPROVAL}/sync-enum`), { method: "POST", headers: { "Content-Type": "application/json" }, body: "{}" })
      ).status,
    ).toBe(422);
  });
});
