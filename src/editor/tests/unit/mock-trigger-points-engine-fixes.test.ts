// The mock follows the engine's fixes from the review of trigger points, part 1 (engine commit 55649d4f): the MQ2002 wording of a
// trigger point, get_trigger_sequence's 400 for an unknown attribute or end, and checksUnknown on a gated move.
import { describe, expect, it } from "vitest";
import { MockBackend } from "@/mocks/backend";
import { INVOICE_LIFECYCLE } from "@/mocks/model/processSeed";
import { simulate } from "@/mocks/model/stepper";
import { useMockApi } from "./harness";

type Json = Record<string, unknown>;

const INVOICE_TOTAL = "01J92P0V0T6EA0XM025XQX8GBW";

describe("MQ2002 on a scenario's assume keys", () => {
  it("calls an entry of a lifecycle's trigger points 'a trigger point', and tells it from a guard", () => {
    const backend = new MockBackend();
    const lifecycle = structuredClone(backend.model.get(INVOICE_LIFECYCLE)!.json) as Json;
    const point = (lifecycle.triggerPoints as Json[])[0].id as string;
    const guard = "01JQR000000000000000000070";
    lifecycle.guards = [{ id: guard, name: "isBig", condition: { text: "true" } }];
    expect(backend.model.save(INVOICE_LIFECYCLE, lifecycle, backend.model.get(INVOICE_LIFECYCLE)!.hash).outcome).toBe("saved");
    const step = (id: string, assume: Json) => ({ id, input: "verb", verb: "read", expect: { accepted: true, states: [] }, assume });
    const created = backend.model.create({
      kind: "scenario",
      name: "Mixed",
      process: INVOICE_LIFECYCLE,
      steps: [step("01JQR000000000000000000071", { [point]: true }), step("01JQR000000000000000000072", { [guard]: "pass" })],
    });
    // An error refuses the save, so the findings come with the refusal.
    expect(created.outcome).not.toBe("saved");
    const found = (created as { diagnostics: { rule: string; message: string }[] }).diagnostics.filter((d) => d.rule === "MQ2002");
    expect(found.map((d) => d.message)).toEqual([
      `'assume' references '${point}', a trigger point; expected a guard.`,
      `'assume' references '${guard}', a guard; expected a trigger point.`,
    ]);
    expect(found.some((d) => d.message.includes("process-trigger-point"))).toBe(false);
  });
});

describe("get_trigger_sequence with an attribute or end the model does not have", () => {
  const mock = useMockApi();
  const get = (query: string) => fetch(mock.url(`/api/processes/${INVOICE_LIFECYCLE}/trigger-sequence?verb=modify${query}`));

  it("answers 400 for an unknown attribute or end, and 200 for a known attribute", async () => {
    expect((await get("&attributes=NOPE")).status).toBe(400);
    expect((await get(`&attributes=${INVOICE_TOTAL},NOPE`)).status).toBe(400);
    expect((await get("&end=NOPE")).status).toBe(400);
    expect((await get(`&attributes=${INVOICE_TOTAL}`)).status).toBe(200);
  });
});

describe("checksUnknown on a gated move", () => {
  const st = (id: string): Json => ({ id, name: id });
  const doc = (required: number): Json => ({
    kind: "lifecycle-test",
    id: "P",
    name: "P",
    use: "lifecycle",
    subjects: [{ id: "s", entity: "E" }],
    states: [st("A"), st("B")],
    events: [{ id: "approve", name: "approve", actors: [] }],
    transitions: [
      {
        id: "t",
        source: "A",
        event: "approve",
        targets: ["B"],
        verb: "promote",
        gate: { id: "g", name: "g", required, signers: ["Finance"], meanings: [{ id: "m", name: "approved" }] },
      },
    ],
    triggerPoints: [
      { id: "move", name: "move", type: "check", verbs: ["promote"] },
      { id: "signs", name: "signs", type: "check", verbs: ["sign"] },
    ],
  });
  const options = { attributesOf: () => [], subjectEntity: "E" };

  it("lists the move's own checks only when the next signature brings the gate to its count", () => {
    expect(simulate(doc(2), null, [], options).enabled[0].checksUnknown).toEqual(["signs"]);
    const afterOne = simulate(doc(2), null, [{ event: "approve", actor: "Finance", signer: "ann" }], options);
    expect(afterOne.trace[1].accepted).toBe(true);
    expect(afterOne.enabled[0].checksUnknown).toEqual(["signs", "move"]);
    expect(simulate(doc(1), null, [], options).enabled[0].checksUnknown).toEqual(["signs", "move"]);
  });
});
