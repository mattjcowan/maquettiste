// Which fields can name a person: uuids are id fields and may name one, like a guid or a whole number.
import { describe, expect, it } from "vitest";
import { canBeOwner, canNamePerson, type ObjectField } from "@/model/objectFields";

const field = (scalar: string): ObjectField => ({ id: "a", name: "a", kind: "attribute", many: false, valueObject: false, scalar });

describe("canNamePerson", () => {
  it.each(["uuid", "UUID", "guid", "string", "int64"])("offers a %s attribute", (scalar) => {
    expect(canNamePerson(field(scalar))).toBe(true);
    expect(canBeOwner(field(scalar))).toBe(true);
  });
  it("leaves out a boolean", () => expect(canNamePerson(field("bool"))).toBe(false));
});
