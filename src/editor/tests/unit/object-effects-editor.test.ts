// Object effects in the editor and the mock (object-effects-design.md section 10): what the mock's references and
// cascade follow, the values text, the states picker's ids, the Problems count and the pick cell.
import { describe, expect, it } from "vitest";
import { planDelete, type CascadeDoc } from "@/mocks/model/cascade";
import { referencesOf, subElementIds } from "@/mocks/model/refs";
import { parseValuesText, statesOfIds, valuesText } from "@/model/effects";
import { groupProblems } from "@/problems/group";
import type { Diagnostic } from "@/api/types";

type Json = Record<string, unknown>;

const process: Json = {
  kind: "process",
  id: "P",
  name: "Order",
  subjects: [{ id: "SUBJ", entity: "E" }],
  actions: [
    {
      id: "A1",
      name: "Record",
      sets: [
        { id: "S1", attribute: "TOTAL", value: { op: "add", args: [{ path: ["PRICE"] }, { value: 1 }] } },
        { id: "S2", attribute: "BY", value: { actor: true } },
        { id: "S3", attribute: "OK", value: { inState: ["ACTIVE"], of: ["END"] } },
      ],
    },
  ],
  states: [{ id: "ACTIVE", name: "Active" }],
};
const scenario: Json = {
  kind: "scenario",
  id: "SC",
  process: "P",
  subject: "O1",
  objects: [{ id: "O1", name: "ann", entity: "E", values: { TOTAL: 1 }, states: ["ACTIVE", "OTHER"] }],
  steps: [{ id: "ST", event: "EV", expect: { values: { TOTAL: 2, PRICE: 3 } } }],
};
const lifecycle: Json = { kind: "process", id: "LC", name: "Employment", states: [{ id: "OTHER", name: "Left" }] };
const entity: Json = {
  kind: "entity",
  id: "AT",
  name: "Attrs",
  attributes: ["TOTAL", "PRICE", "BY", "OK"].map((id) => ({ id, name: id.toLowerCase(), type: "text" })),
};
const docs = (...list: Json[]) =>
  new Map<string, CascadeDoc>(list.map((j) => [String(j.id), { id: String(j.id), path: `${j.id}.json`, json: structuredClone(j) }]));

describe("the mock's references for object effects", () => {
  it("reads sets, the ids in their values, states and expected values", () => {
    const refs = referencesOf(process).map((r) => [r.pointer, r.toId, r.removeItem, r.drops]);
    expect(refs).toContainEqual(["/actions/0/sets/0/attribute", "TOTAL", "/actions/0/sets/0", true]);
    expect(refs).toContainEqual(["/actions/0/sets/0/value/args/0/path/0", "PRICE", "/actions/0/sets/0", true]);
    expect(refs).toContainEqual(["/actions/0/sets/2/value/inState/0", "ACTIVE", "/actions/0/sets/2", true]);
    expect(refs).toContainEqual(["/actions/0/sets/2/value/of/0", "END", "/actions/0/sets/2", true]);
    const mine = referencesOf(scenario).map((r) => [r.pointer, r.toId]);
    expect(mine).toContainEqual(["/objects/0/states/1", "OTHER"]);
    expect(mine).toContainEqual(["/steps/0/expect/values/PRICE", "PRICE"]);
  });

  it("holds each set as a sub-element", () => {
    expect(subElementIds(process)).toEqual(expect.arrayContaining(["S1", "S2", "S3"]));
  });
});

describe("the mock's cascade for object effects", () => {
  it("drops a set whose attribute goes and keeps the action's other sets", () => {
    const plan = planDelete(docs(process, scenario, entity, lifecycle), ["AT"], "remove-references");
    expect(plan.edited.get("P")!.actions).toEqual([{ id: "A1", name: "Record" }]);
    expect(plan.plan.removes.map((r) => r.subKind)).toContain("set");
    expect((plan.edited.get("SC")!.objects as Json[])[0].values).toEqual({});
  });

  it("keeps the other sets of an action when one set goes", () => {
    const one = structuredClone(process);
    const sets = (one.actions as Json[])[0].sets as Json[];
    sets[0].attribute = "TOTAL";
    sets[1].attribute = "KEEP";
    sets[2].attribute = "KEEP2";
    const plan = planDelete(docs(one, { kind: "entity", id: "AT", attributes: [{ id: "TOTAL" }] }), ["AT"], "remove-references");
    expect(((plan.edited.get("P")!.actions as Json[])[0].sets as Json[]).map((x) => x.id)).toEqual(["S2", "S3"]);
  });

  it("drops an object's state when the state or its lifecycle goes, and an expected value when its attribute goes", () => {
    const byState = planDelete(docs(process, scenario, lifecycle, entity), ["LC"], "remove-references");
    expect((byState.edited.get("SC")!.objects as Json[])[0].states).toEqual(["ACTIVE"]);
    const byLifecycle = planDelete(docs(process, scenario, lifecycle, entity), ["LC"], "remove-references");
    expect((byLifecycle.edited.get("SC")!.objects as Json[])[0].states).toEqual(["ACTIVE"]);
    const byAttribute = planDelete(docs(process, scenario, entity), ["AT"], "remove-references");
    expect(((byAttribute.edited.get("SC")!.steps as Json[])[0].expect as Json).values).toEqual({});
  });
});

describe("values by attribute name", () => {
  const names = new Map([
    ["T", "total"],
    ["B", "by"],
  ]);
  it("shows names and files ids, and says a name nothing has instead of dropping it", () => {
    expect(valuesText({ T: 200, B: "ann", X: 1 }, names)).toBe("total=200, by=ann, ?=1");
    expect(parseValuesText("Total=200, by=ann", names)).toEqual({ values: { T: 200, B: "ann" }, unknown: [] });
    expect(parseValuesText("total=1, nope=2, loose", names)).toEqual({ values: { T: 1 }, unknown: ["nope", "loose"] });
  });
});

describe("the states picker", () => {
  const groups = [{ lifecycle: "Employment", states: [{ id: "A", label: "Employment.Active" }] }];
  it("keeps the ids the picker offers", () => {
    expect(statesOfIds("A,zzz,A", groups)).toEqual(["A"]);
    expect(statesOfIds("", groups)).toEqual([]);
  });
});

describe("the Problems count", () => {
  const d = (rule: string, pointer: string): Diagnostic =>
    ({ rule, severity: "warning", message: `${rule} says`, elementId: "P", filePath: "p.json", jsonPointer: pointer, line: null, column: null }) as Diagnostic;
  it("counts a finding the draft and the saved version both report once", () => {
    const draft = { id: "P", status: "invalid", diagnostics: [d("MQ9013", "/actions/0"), d("MQ9801", "/actions/1/sets/0")], json: { name: "Order" } };
    const groups = groupProblems([d("MQ9013", "/actions/0"), d("MQ1", "/name")], [draft as never], new Map());
    expect(groups.flatMap((g) => g.diagnostics).map((x) => x.rule)).toEqual(["MQ9013", "MQ9801", "MQ1"]);
  });
});
