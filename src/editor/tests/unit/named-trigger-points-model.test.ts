// Named trigger points, the editor's pure model (trigger-points-design.md decisions 33 to 35, section 11): what a new one holds, its
// subjects and except list, the types it runs for, the noBypass cell, its index row, and What happens when's rows for a named step.
import { describe, expect, it } from "vitest";
import type { ElementDocument } from "@/api/types";
import { summaryFromDocument } from "@/model/model";
import {
  buildNamedTriggerPoint,
  coveredEntities,
  editPoint,
  editTriggerPoint,
  exceptChoices,
  fitPointType,
  namedVerbOption,
  namedWhereKeys,
  parseSubjectValue,
  sequenceRows,
  setNamedExcept,
  setNamedSubjects,
  subjectValue,
  triggerPointRows,
  type CoverRow,
  type NamedTriggerPointDoc,
  type TriggerNames,
} from "@/model/triggerPoints";
import type { ProcessDoc } from "@/model/process";

const rows: CoverRow[] = [
  { id: "doc", name: "Document", stereotypes: ["approvable"] },
  { id: "memo", name: "Memo", base: "doc" },
  { id: "invoice", name: "Invoice", base: "doc" },
  { id: "customer", name: "Customer" },
  { id: "order", name: "Order", stereotypes: ["approvable"] },
];

const point = (extra: Partial<NamedTriggerPointDoc> = {}): NamedTriggerPointDoc => ({
  kind: "trigger-point",
  id: "tp",
  name: "CustomerNumber",
  type: "action",
  timing: "before",
  verbs: ["create"],
  subjects: [{ entity: "customer" }],
  ...extra,
});

describe("a new named trigger point", () => {
  it("is a check on one verb of one entity, in the entity's package", () => {
    expect(buildNamedTriggerPoint({ name: " Num ", verb: "create", entity: "customer", package: "pkg" }, () => "id1")).toEqual({
      kind: "trigger-point",
      id: "id1",
      name: "Num",
      package: "pkg",
      subjects: [{ entity: "customer" }],
      type: "check",
      verbs: ["create"],
    });
  });
  it("offers no move verb and no sign, and narrows only by type, attribute and link", () => {
    expect(namedVerbOption("create")).toBe(true);
    expect(namedVerbOption("promote")).toBe(false);
    expect(namedVerbOption("sign")).toBe(false);
    expect(namedWhereKeys(["create"])).toEqual(["entities"]);
    expect(namedWhereKeys(["read"])).toContain("attributes");
    expect(namedWhereKeys(["connect"])).toEqual(["entities", "ends"]);
  });
});

describe("subjects and except", () => {
  it("round-trips a chip value, entity or stereotype", () => {
    expect(parseSubjectValue(subjectValue({ entity: "e1" }))).toEqual({ entity: "e1" });
    expect(parseSubjectValue(subjectValue({ stereotype: "approvable" }))).toEqual({ stereotype: "approvable" });
    expect(parseSubjectValue("nonsense")).toBeNull();
  });
  it("refuses to take away the last subject", () => {
    const p = point();
    expect(setNamedSubjects(p, [])).toBe(false);
    expect(p.subjects).toEqual([{ entity: "customer" }]);
    expect(setNamedSubjects(p, ["entity:customer", "stereotype:approvable"])).toBe(true);
    expect(p.subjects).toEqual([{ entity: "customer" }, { stereotype: "approvable" }]);
  });
  it("removes the except key when it is empty", () => {
    const p = point({ exceptEntities: ["memo"] });
    setNamedExcept(p, []);
    expect("exceptEntities" in p).toBe(false);
    setNamedExcept(p, ["memo"]);
    expect(p.exceptEntities).toEqual(["memo"]);
  });
});

describe("the types it runs for", () => {
  const names = (p: NamedTriggerPointDoc) => coveredEntities(p, rows).map((e) => e.name);
  it("covers an entity and the entities derived from it", () => {
    expect(names(point({ subjects: [{ entity: "doc" }] }))).toEqual(["Document", "Invoice", "Memo"]);
  });
  it("covers the carriers of a stereotype and what derives from them", () => {
    expect(names(point({ subjects: [{ stereotype: "approvable" }] }))).toEqual(["Document", "Invoice", "Memo", "Order"]);
  });
  it("leaves out the except entities with their derived ones", () => {
    expect(names(point({ subjects: [{ stereotype: "approvable" }], exceptEntities: ["doc"] }))).toEqual(["Order"]);
    expect(names(point({ subjects: [{ entity: "doc" }], exceptEntities: ["memo"] }))).toEqual(["Document", "Invoice"]);
  });
  it("narrows to the entities it names, with theirs", () => {
    expect(names(point({ subjects: [{ stereotype: "approvable" }], entities: ["memo"] }))).toEqual(["Memo"]);
  });
  it("offers as except the entities a subject covers", () => {
    expect(exceptChoices(point({ subjects: [{ entity: "doc" }] }), rows).map((e) => e.id)).toEqual(["doc", "invoice", "memo"]);
  });
});

describe("the cells of a named or inline point", () => {
  it("sets and clears noBypass", () => {
    const p = point();
    expect(editPoint(p, "noBypass", true)).toBe(true);
    expect(p.noBypass).toBe(true);
    editPoint(p, "noBypass", false);
    expect("noBypass" in p).toBe(false);
  });
  it("refuses a replace beside a verb that does not allow it", () => {
    const p = point();
    expect(editPoint(p, "type", "replace")).toBe(false);
    p.verbs = ["delete"];
    expect(editPoint(p, "type", "replace")).toBe(true);
    expect(p.type).toBe("replace");
    expect(p.timing).toBeUndefined();
    expect(editPoint(p, "verbs", "create")).toBe(false);
  });
  it("takes out the keys a new type does not have", () => {
    const p = point({ sets: [{ id: "s", attribute: "a", value: { text: "1" } }] });
    fitPointType(p, "check");
    expect(p.sets).toBeUndefined();
    expect(p.timing).toBeUndefined();
  });
  it("shows noBypass on an inline row, and edits it", () => {
    const process = {
      kind: "process",
      id: "p",
      name: "P",
      states: [],
      triggerPoints: [{ id: "t", name: "A", type: "check", verbs: ["modify"] }],
    } as unknown as ProcessDoc;
    const n: TriggerNames = { state: String, transition: String, entity: String, field: String, verb: String, expression: () => "" };
    expect(triggerPointRows(process, n)[0].noBypass).toBe(false);
    editTriggerPoint(process, "t", "noBypass", true);
    expect(triggerPointRows(process, n)[0].noBypass).toBe(true);
  });
});

describe("its index row", () => {
  const doc = (json: Record<string, unknown>) => ({ json, hash: "h", path: "p" }) as unknown as ElementDocument;
  it("carries type, verbs, entity subjects and the stereotype subjects by id", () => {
    const row = summaryFromDocument(
      doc({ ...point({ verbs: ["modify", "create"], subjects: [{ entity: "customer" }, { stereotype: "approvable" }] }) }),
      undefined,
      new Map([["approvable", "stereo-id"]]),
    );
    expect(row).toMatchObject({
      kind: "trigger-point",
      type: "action",
      verbs: ["create", "modify"],
      subjects: ["customer"],
      subjectStereotypes: ["stereo-id"],
    });
  });
});

describe("What happens when, for a named step", () => {
  it("has no lifecycle, and is marked as a named point", () => {
    const out = sequenceRows(
      {
        steps: [
          { order: 1, step: "before", triggerPoint: "tp", type: "action", timing: "before", noBypass: true },
          { order: 2, step: "effect" },
        ],
      } as never,
      { point: () => "CustomerNumber", lifecycle: () => "L", where: () => "" },
    );
    expect(out[0]).toMatchObject({ named: true, other: null, point: { id: "tp", name: "CustomerNumber", lifecycle: null } });
    expect(out[1]).toMatchObject({ named: false, point: null });
  });
});
