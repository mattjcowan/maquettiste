// A custom type's native types (the Definition tab's Native types section): one value per dialect, kept in the
// canonical key order, an empty value removing its dialect and an empty map removing the member.
import { describe, expect, it } from "vitest";
import { setNativeType } from "@/inspector/fields";

type Json = Record<string, unknown>;

describe("setNativeType", () => {
  it("adds values in ordinal key order and trims them", () => {
    const json: Json = { kind: "scalar-type", base: "binary" };
    setNativeType(json, "sqlserver", " binary({length}) ");
    setNativeType(json, "postgresql", "pg_lsn");
    expect(json.nativeTypes).toEqual({ postgresql: "pg_lsn", sqlserver: "binary({length})" });
    expect(Object.keys(json.nativeTypes as Json)).toEqual(["postgresql", "sqlserver"]);
  });

  it("removes a dialect on an empty value and the member when none is left", () => {
    const json: Json = { kind: "scalar-type", base: "binary", nativeTypes: { postgresql: "pg_lsn", oracle: "raw(8)" } };
    setNativeType(json, "postgresql", "  ");
    expect(json.nativeTypes).toEqual({ oracle: "raw(8)" });
    setNativeType(json, "oracle", "");
    expect("nativeTypes" in json).toBe(false);
  });
});
