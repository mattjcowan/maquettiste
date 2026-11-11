// Processes linked to operations, editor part 1 (processes-operations-design.md sections 7 and 12): the Add event picker's
// ordering, the Fired by filter, and the mock's references and deletes for `fires`, a call and an invoke's operation.
import { describe, expect, it } from "vitest";
import { MockBackend } from "@/mocks/backend";
import { firedByIds, groupProcessesForSubject, operationsForSubjects, payloadText } from "@/model/operationLinks";
import { IDS } from "./harness";

type Json = Record<string, unknown>;
const row = (id: string, name: string, extra: Json = {}) =>
  ({ id, kind: "process", name, package: null, tags: [], category: null, stereotypes: [], hash: "h", path: name, use: "lifecycle", ...extra }) as never;

describe("the Fires picker", () => {
  const rows = [
    row("A", "Zeta", { subject: IDS.invoice }),
    row("B", "Alpha"),
    row("C", "Mid", { subjects: [IDS.payment, IDS.invoice] }),
    row("D", "Shared", { use: "library" }),
    row("E", "Beta", { subject: IDS.payment, use: "orchestration" }),
  ];

  it("lists processes governing the subject first, the rest after, each by name, and no library", () => {
    const g = groupProcessesForSubject(rows, IDS.invoice);
    expect(g.on.map((p) => p.name)).toEqual(["Mid", "Zeta"]);
    expect(g.other.map((p) => p.name)).toEqual(["Alpha", "Beta"]);
  });

  it("filters by name and puts everything under Other processes when the operation has no subject", () => {
    expect(groupProcessesForSubject(rows, IDS.invoice, "et").on.map((p) => p.name)).toEqual(["Zeta"]);
    const none = groupProcessesForSubject(rows, undefined);
    expect(none.on).toEqual([]);
    expect(none.other).toHaveLength(4);
  });

  it("orders an invoke's operations on the process's subjects first", () => {
    const op = (id: string, name: string, subject?: string) => ({ ...(row(id, name) as object), kind: "operation", subject }) as never;
    const g = operationsForSubjects([op("1", "b", IDS.payment), op("2", "a"), op("3", "c", IDS.invoice)], [IDS.invoice]);
    expect(g.on.map((o) => o.name)).toEqual(["c"]);
    expect(g.other.map((o) => o.name)).toEqual(["a", "b"]);
  });

  it("prints a payload in small text", () => {
    expect(payloadText({ payload: [{ name: "dueDays", type: "int32", required: true }, { name: "issuedOn", type: "date" }, { name: "x" }] })).toBe(
      "dueDays (int32, required), issuedOn (date), x",
    );
  });
});

describe("Fired by", () => {
  it("keeps the referrers whose field is fires, once each", () => {
    const ref = (fromElementId: string, field: string) => ({ fromElementId, fromId: fromElementId, jsonPointer: "/x", field, toId: "E" });
    expect(firedByIds([ref("OP1", "fires"), ref("P1", "event"), ref("OP1", "fires"), ref("OP2", "fires")])).toEqual(["OP1", "OP2"]);
    expect(firedByIds(undefined)).toEqual([]);
  });
});

describe("the mock's links between processes and operations", () => {
  const OP = "01JR5NE0000000000000000100";
  const MEMBER = "01JR5NE0000000000000000101";
  const OTHER = "01JR5NE0000000000000000102";
  const PROCESS = "01JR5NE0000000000000000110";
  const EVENT = "01JR5NE0000000000000000111";
  const STATE = "01JR5NE0000000000000000112";
  const ACTION = "01JR5NE0000000000000000113";
  const INPUT = "01JR5NE0000000000000000114";
  const INVOKE = "01JR5NE0000000000000000115";
  const operation = (id: string, name: string, extra: Json = {}): Json => ({
    kind: "operation",
    id,
    name,
    subject: IDS.invoice,
    effect: "change",
    outcomes: [{ id: id === OP ? "01JR5NE0000000000000000103" : "01JR5NE0000000000000000105", name: "done" }],
    input: [{ id: id === OP ? MEMBER : "01JR5NE0000000000000000104", name: "dueDays", type: "int32", required: true }],
    ...extra,
  });
  const setup = () => {
    const backend = new MockBackend();
    expect(backend.model.create(operation(OP, "issue")).outcome).toBe("saved");
    expect(backend.model.create(operation(OTHER, "charge")).outcome).toBe("saved");
    const created = backend.model.create({
      kind: "process",
      id: PROCESS,
      name: "InvoiceLifecycle",
      use: "orchestration",
      subjects: [{ entity: IDS.invoice }],
      events: [{ id: EVENT, name: "Issue" }],
      states: [{ id: STATE, name: "Draft", invoke: [{ id: INVOKE, name: "Charge", type: "service", operation: OTHER }] }],
      initial: STATE,
      actions: [
        {
          id: ACTION,
          name: "RequestIssue",
          call: { operation: OP, inputs: [{ id: INPUT, member: MEMBER, value: { value: 30 } }] },
        },
      ],
    });
    expect(created.outcome).toBe("saved");
    const fires = backend.model.save(OP, operation(OP, "issue", { fires: [EVENT] }), backend.model.get(OP)!.hash);
    expect(fires.outcome).toBe("saved");
    return backend;
  };

  it("reports fires, a call's operation and member, and an invoke's operation as references", () => {
    const model = setup().model;
    expect(model.references(EVENT)!.map((r) => [r.fromElementId, r.field, r.jsonPointer])).toEqual([[OP, "fires", "/fires/0"]]);
    expect(model.references(OP)!.map((r) => [r.field, r.jsonPointer])).toEqual([
      ["operation", "/actions/0/call/operation"],
      ["member", "/actions/0/call/inputs/0/member"],
    ]);
    expect(model.references(MEMBER)!.map((r) => [r.field, r.jsonPointer])).toEqual([["member", "/actions/0/call/inputs/0/member"]]);
    expect(model.references(OTHER)!.map((r) => [r.field, r.jsonPointer])).toEqual([["operation", "/states/0/invoke/0/operation"]]);
  });

  it("deleting the called operation removes the whole call, an invoke's operation is cleared", () => {
    const backend = setup();
    const model = backend.model;
    const hash = (id: string) => model.get(id)!.hash;
    expect(model.delete(OTHER, hash(OTHER), "remove-references").outcome).toBe("saved");
    const process = () => model.get(PROCESS)!.json as Json;
    expect(((process().states as Json[])[0].invoke as Json[])[0].operation).toBeUndefined();

    expect(model.delete(OP, hash(OP), "remove-references").outcome).toBe("saved");
    expect((process().actions as Json[])[0].call).toBeUndefined();
    expect((process().actions as Json[])[0].name).toBe("RequestIssue");
  });

  it("deleting the process that holds a fired event clears the operation's entry", () => {
    const model = setup().model;
    const plan = model.deletePlan([PROCESS], "remove-references");
    expect(plan.clears.map((c) => [c.id, c.pointer])).toEqual([[OP, "/fires/0"]]);
  });
});
