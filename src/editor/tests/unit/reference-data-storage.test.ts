// The storage choice where a reference type is created and used (reference-types-seeds-localization.md 1.4 and 4.5):
// the New reference type dialog's "Stored as" list and preselection, and the effective storage the attribute type
// picker shows beside a reference type's name.
import { describe, expect, it } from "vitest";
import type { ElementSummary } from "@/api/types";
import { pickerGroups } from "@/inspector/typePicker";
import { preselectedStorage, storageFor, storageLabel, storageOptions, TEMPLATE_DEFINED } from "@/workspaces/reference-data/storageChoices";

const declared = { native: {}, check: { description: "CHECK (column IN (codes))" }, "lookup-table": {}, archive: {} };

describe("Stored as", () => {
  it("lists Let the packs decide, then the pack's three strategies by name, then the others, each with its help", () => {
    expect(storageOptions(declared).map((o) => [o.value, o.label])).toEqual([
      [TEMPLATE_DEFINED, "Let the packs decide"],
      ["lookup-table", "lookup-table"],
      ["check", "check"],
      ["native", "native"],
      ["archive", "archive"],
    ]);
    expect(storageOptions(declared)[2].help).toBe("CHECK (column IN (codes))");
    expect(storageOptions(declared)[0].help).toBe("No strategy: the templates decide how the type is stored.");
    expect(storageOptions({}).map((o) => o.label)).toEqual(["Let the packs decide"]);
  });

  it("preselects a check constraint, else the project default, else Let the packs decide", () => {
    expect(preselectedStorage(declared, { strategy: "lookup-table" })).toBe("check");
    expect(preselectedStorage({ "lookup-table": {} }, { strategy: "lookup-table" })).toBe("lookup-table");
    expect(preselectedStorage({ "lookup-table": {} }, null)).toBe(TEMPLATE_DEFINED);
  });

  it("writes the choice for every database, and nothing while the project declares no strategy", () => {
    expect(storageFor("check", declared)).toEqual({ "*": { strategy: "check" } });
    expect(storageFor(TEMPLATE_DEFINED, declared)).toEqual({ "*": {} });
    expect(storageFor(TEMPLATE_DEFINED, {})).toBeUndefined();
  });
});

describe("the type picker's storage beside a reference type", () => {
  it("shows the type's own choice, else the project default, else packs decide, and counts databases set otherwise", () => {
    expect(storageLabel({ "*": { strategy: "check" } }, { strategy: "lookup-table" })).toBe("check");
    expect(storageLabel(undefined, { strategy: "lookup-table" })).toBe("lookup-table");
    expect(storageLabel(undefined, null)).toBe("packs decide");
    expect(storageLabel({ "*": {} }, { strategy: "check" })).toBe("packs decide");
    expect(storageLabel({ "*": { strategy: "check" }, DB1: { strategy: "native" }, DB2: { strategy: "check" } })).toBe("check +1");
  });

  it("puts the storage on the Reference data items only", () => {
    const options = [
      { id: "R1", kind: "reference-type", name: "Country" },
      { id: "E1", kind: "enum", name: "Status" },
    ] as unknown as ElementSummary[];
    const groups = pickerGroups(
      options,
      "",
      [],
      new Map([
        ["R1", "check"],
        ["E1", "lookup-table"],
      ]),
    );
    const reference = groups.find((g) => g.section === "reference-type")!.items[0];
    expect(`${reference.label} · ${reference.storage}`).toBe("Country · check");
    expect(groups.find((g) => g.section === "enum")!.items[0].storage).toBeUndefined();
  });
});
