// "Stored in a table" (2026-10-11): the fields a reference type binding can map, the field map a newly chosen table starts
// with (each field to the column named like it), and a constant's value as its column stores it.
import { describe, expect, it } from "vitest";
import type { ReferenceTypeDoc } from "@/api/types";
import { bindableFields, constantValue, guessFields } from "@/workspaces/reference-data/TableBindings";

const type = {
  kind: "reference-type",
  id: "01K7RTB0000000000000000020",
  name: "PaymentTerm",
  code: { id: "01K7RTB0000000000000000021" },
  label: { id: "01K7RTB0000000000000000022" },
  attributes: [
    { id: "01K7RTB0000000000000000023", name: "dueDays", type: "int32" },
    { id: "01K7RTB0000000000000000024", name: "regions", type: "string", collection: true },
  ],
} as unknown as ReferenceTypeDoc;

describe("Stored in a table", () => {
  it("offers code, label, description and the single-valued user fields", () => {
    expect(bindableFields(type).map((f) => f.key)).toEqual(["code", "label", "description", "01K7RTB0000000000000000023"]);
  });

  it("maps each field to the first column named like it, the label to a name column, a user field to its snake-case column", () => {
    const columns = [
      { id: "c1", name: "code" },
      { id: "c2", name: "name" },
      { id: "c3", name: "due_days" },
      { id: "c4", name: "note" },
    ];
    expect(guessFields(type, columns)).toEqual([
      { attribute: "code", column: "c1" },
      { attribute: "label", column: "c2" },
      { attribute: "01K7RTB0000000000000000023", column: "c3" },
    ]);
  });

  it("keeps a constant's text, a number in a numeric column and true or false in a boolean one", () => {
    expect(constantValue("payment-term", "string")).toBe("payment-term");
    expect(constantValue("7", "int32")).toBe(7);
    expect(constantValue("seven", "int32")).toBe("seven");
    expect(constantValue("true", "bool")).toBe(true);
    expect(constantValue("7", undefined)).toBe("7");
  });
});
