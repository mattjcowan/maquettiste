// The Rights tab's model functions (model/rights.ts) and the delete of a state that a right names (model/process.ts).
import { describe, expect, it } from "vitest";
import { deleteState, type ProcessDoc } from "@/model/process";
import {
  addRight,
  defaultAccess,
  freeRightName,
  rightsLostWithStates,
  unknownActors,
  addRightUse,
  editRight,
  endsBesideNonLinkVerb,
  hasEnds,
  removeRight,
  rightRows,
  rightStateOptions,
  setRightsDefault,
  usesRights,
  ALL_STATES,
  EVERYONE,
} from "@/model/rights";

const A = "01JA0000000000000000000001";
const B = "01JA0000000000000000000002";
const S1 = "01JS0000000000000000000001";
const S2 = "01JS0000000000000000000002";
const H = "01JS0000000000000000000003";
const NAMED = "01JR0000000000000000000001";

const process = (): ProcessDoc =>
  ({
    kind: "process",
    id: "01JP0000000000000000000001",
    name: "Doc",
    use: "lifecycle",
    states: [
      { id: S1, name: "Draft" },
      { id: S2, name: "Released" },
      { id: H, name: "Memory", type: "history" },
    ],
  }) as ProcessDoc;

const names = {
  actor: (id: string) => (id === A ? "Author" : "Admin"),
  state: (id: string) => (id === S1 ? "Draft" : "Released"),
  end: (id: string) => id,
  verb: (key: string) => key.toUpperCase(),
  condition: () => "",
};
const options = {
  actors: [
    { id: A, name: "Author" },
    { id: B, name: "Admin" },
  ],
};

const quick = (p: ProcessDoc, after?: string) => addRight(p, { name: freeRightName(p), access: defaultAccess(p), verbs: ["modify"] }, after)!;

describe("the Rights tab's model", () => {
  it("adds a right that denies, or allows under a denying default, with one verb and a unique name", () => {
    const p = process();
    const first = quick(p);
    const second = quick(p, first);
    expect(p.rights!.map((r) => r.name)).toEqual(["right", "right2"]);
    expect(p.rights![0]).toMatchObject({ id: first, access: "deny", verbs: ["modify"] });
    setRightsDefault(p, "deny");
    const third = quick(p);
    expect(p.rights!.find((r) => r.id === third)!.access).toBe("allow");
    expect(second).not.toBe(first);
    expect(usesRights(p)).toBe(true);
  });

  it("writes only deny as the default; allow is the absence", () => {
    const p = process();
    setRightsDefault(p, "deny");
    expect(p.rightsDefault).toBe("deny");
    setRightsDefault(p, "allow");
    expect(p.rightsDefault).toBeUndefined();
    expect(usesRights(p)).toBe(false);
  });

  it("shows everyone and all states for empty lists, and a use's fields from the named right", () => {
    const p = process();
    const id = quick(p);
    const use = addRightUse(p, NAMED);
    p.rights!.find((r) => r.id === use)!.states = [S2];
    const rows = rightRows(p, new Map([[NAMED, { name: "Frozen", access: "allow", actors: [A], verbs: ["read"], ends: ["e"] }]]), names);
    expect(rows[0]).toMatchObject({ id, use: false, who: EVERYONE, states: ALL_STATES, verbs: "MODIFY", pointer: "/rights/0" });
    expect(rows[1]).toMatchObject({ id: use, use: true, named: NAMED, name: "Frozen", access: "allow", who: "Author", states: "Released", verbs: "READ" });
    expect(hasEnds(rows)).toBe(true);
    expect(hasEnds(rows.slice(0, 1))).toBe(false);
  });

  it("edits the cells: who and except by names, verbs, states and ends by lists, empty lists leave the key out", () => {
    const p = process();
    const id = quick(p);
    expect(editRight(p, id, "who", "author, Admin", options)).toBe(true);
    expect(p.rights![0].actors).toEqual([A, B]);
    editRight(p, id, "except", "Admin", options);
    expect(p.rights![0].except).toEqual([B]);
    editRight(p, id, "who", "", options);
    expect(p.rights![0].actors).toBeUndefined();
    expect(editRight(p, id, "verbs", "", options)).toBe(false);
    editRight(p, id, "verbs", "read,connect", options);
    expect(p.rights![0].verbs).toEqual(["read", "connect"]);
    editRight(p, id, "states", `${S1},${S2}`, options);
    expect(p.rights![0].states).toEqual([S1, S2]);
    editRight(p, id, "states", "", options);
    expect(p.rights![0].states).toBeUndefined();
    editRight(p, id, "when", " subject.x = 1 ", options);
    expect(p.rights![0].condition).toEqual({ text: "subject.x = 1" });
    editRight(p, id, "when", "", options);
    expect(p.rights![0].condition).toBeUndefined();
    editRight(p, id, "access", "allow", options);
    editRight(p, id, "name", "  ", options);
    expect(p.rights![0]).toMatchObject({ access: "allow", name: "right" });
  });

  it("lets a use change only its states", () => {
    const p = process();
    const use = addRightUse(p, NAMED);
    expect(editRight(p, use, "access", "allow", options)).toBe(false);
    expect(editRight(p, use, "who", "Author", options)).toBe(false);
    expect(editRight(p, use, "states", S1, options)).toBe(true);
    expect(p.rights![0]).toEqual({ id: use, right: NAMED, states: [S1] });
  });

  it("removes a right and the key with the last one", () => {
    const p = process();
    const id = quick(p);
    removeRight(p, id);
    expect(p.rights).toBeUndefined();
  });

  it("offers the states of the lifecycle but not a history or a choice state", () => {
    expect(rightStateOptions(process()).map((o) => o.label)).toEqual(["Draft", "Released"]);
  });

  it("deleting a state takes it from the rights that name it, and a right with no state left goes", () => {
    const p = process();
    const both = quick(p);
    const only = quick(p);
    editRight(p, both, "states", `${S1},${S2}`, options);
    editRight(p, only, "states", S2, options);
    expect(deleteState(p, S2)).toEqual({ ok: true });
    expect(p.rights!.map((r) => [r.id, r.states])).toEqual([[both, [S1]]]);
  });

  it("knows ends beside a verb that cannot act on a link", () => {
    expect(endsBesideNonLinkVerb(["connect", "read"], ["e"])).toBe(false);
    expect(endsBesideNonLinkVerb(["connect", "delete"], ["e"])).toBe(true);
    expect(endsBesideNonLinkVerb(["delete"], [])).toBe(false);
  });

  it("refuses an actor name no actor has, never reading it as everyone", () => {
    const p = process();
    const id = quick(p);
    editRight(p, id, "who", "Author", options);
    expect(unknownActors("Nobody", options.actors)).toEqual(["Nobody"]);
    expect(unknownActors("admin, Nobody, ", options.actors)).toEqual(["Nobody"]);
    expect(unknownActors("Everyone", options.actors)).toEqual([]);
    expect(unknownActors("", options.actors)).toEqual([]);
    expect(editRight(p, id, "who", "Nobody", options)).toBe(false);
    expect(editRight(p, id, "except", "Admin, Nobody", options)).toBe(false);
    expect(p.rights![0].actors).toEqual([A]);
    expect(p.rights![0].except).toBeUndefined();
  });

  it("adds a right as the dialog asked, and nothing without a verb", () => {
    const p = process();
    expect(addRight(p, { name: "Locked", access: "deny", verbs: [] })).toBeNull();
    expect(p.rights).toBeUndefined();
    const id = addRight(p, { name: " Locked ", access: "deny", verbs: ["modify", "delete"], actors: [A], states: [S1] })!;
    expect(p.rights![0]).toEqual({ id, name: "Locked", access: "deny", verbs: ["modify", "delete"], actors: [A], states: [S1] });
    const plain = addRight(p, { name: "", access: "allow", verbs: ["read"] })!;
    expect(p.rights!.find((r) => r.id === plain)).toEqual({ id: plain, name: "right", access: "allow", verbs: ["read"] });
  });

  it("names the rights a state delete takes along: those that name only the deleted states", () => {
    const p = process();
    const only = quick(p);
    const both = quick(p, only);
    const use = addRightUse(p, NAMED);
    editRight(p, only, "states", S1, options);
    editRight(p, both, "states", `${S1},${S2}`, options);
    editRight(p, use, "states", S1, options);
    expect(rightsLostWithStates(p, new Set([S1]), () => "Frozen")).toEqual(["right", "Frozen"]);
    expect(rightsLostWithStates(p, new Set([S1, S2]))).toEqual(["right", "right2", "a named right"]);
    expect(rightsLostWithStates(p, new Set(["other"]))).toEqual([]);
  });
});
