// The property bag's pure part (propertyBag.ts): rows from an element's properties minus the declared keys, the edits a row
// commits (add, rename, type change, remove), and what keeps a key or a value from being saved.
import { describe, expect, it } from "vitest";
import {
  applyPropertyEdit,
  convertPropertyValue,
  declaredKeys,
  editProperties,
  parsePropertyValue,
  propertyKeyProblem,
  propertyRows,
  propertyTypeOf,
} from "@/inspector/propertyBag";

describe("propertyRows", () => {
  it("lists every key no extension declares, in the object's order, with its inferred type and text", () => {
    const properties = { owner: "finance", retentionDays: 30, priority: 3, archived: false, limits: { max: 2 } };
    expect(propertyRows(properties, ["retentionDays"])).toEqual([
      { key: "owner", value: "finance", type: "text", text: "finance" },
      { key: "priority", value: 3, type: "number", text: "3" },
      { key: "archived", value: false, type: "boolean", text: "false" },
      { key: "limits", value: { max: 2 }, type: "json", text: '{"max":2}' },
    ]);
  });

  it("has no rows for a missing or non-object member", () => {
    expect(propertyRows(undefined)).toEqual([]);
    expect(propertyRows(["a"])).toEqual([]);
    expect(propertyRows("x")).toEqual([]);
  });

  it("takes the declared keys from the extension schemas' properties", () => {
    expect(declaredKeys([{ properties: { retentionDays: {}, tier: {} } }, { properties: { tier: {} } }, { properties: null }])).toEqual([
      "retentionDays",
      "tier",
    ]);
  });
});

describe("applyPropertyEdit", () => {
  it("adds a key at the end, its value text by default", () => {
    expect(applyPropertyEdit(undefined, { op: "set", key: "owner", value: "" })).toEqual({ owner: "" });
    expect(Object.keys(applyPropertyEdit({ a: "1", b: "2" }, { op: "set", key: "c", value: "3" })!)).toEqual(["a", "b", "c"]);
  });

  it("sets a value in place, and a rename keeps the value and the key's place", () => {
    const properties = { a: "1", owner: "finance", z: "9" };
    expect(applyPropertyEdit(properties, { op: "set", key: "owner", value: "ops" })).toEqual({ a: "1", owner: "ops", z: "9" });
    const renamed = applyPropertyEdit(properties, { op: "set", key: "team", value: "finance", from: "owner" })!;
    expect(renamed).toEqual({ a: "1", team: "finance", z: "9" });
    expect(Object.keys(renamed)).toEqual(["a", "team", "z"]);
    // The object passed in is left alone.
    expect(properties).toEqual({ a: "1", owner: "finance", z: "9" });
  });

  it("changes a value's type: a number or a boolean is stored as JSON", () => {
    expect(applyPropertyEdit({ priority: "3" }, { op: "set", key: "priority", value: 3 })).toEqual({ priority: 3 });
    expect(applyPropertyEdit({ active: "true" }, { op: "set", key: "active", value: true })).toEqual({ active: true });
  });

  it("removes a key, and the member when it was the last", () => {
    expect(applyPropertyEdit({ a: "1", b: "2" }, { op: "remove", key: "a" })).toEqual({ b: "2" });
    expect(applyPropertyEdit({ a: "1" }, { op: "remove", key: "a" })).toBeUndefined();
    const doc: Record<string, unknown> = { kind: "entity", properties: { a: "1" } };
    editProperties(doc, { op: "remove", key: "a" });
    expect(doc).toEqual({ kind: "entity" });
    editProperties(doc, { op: "set", key: "owner", value: "finance" });
    expect(doc).toEqual({ kind: "entity", properties: { owner: "finance" } });
  });

  it("leaves the declared keys alone", () => {
    expect(applyPropertyEdit({ retentionDays: 30, owner: "x" }, { op: "remove", key: "owner" })).toEqual({ retentionDays: 30 });
  });
});

describe("validation", () => {
  it("requires a key, unique in the bag, and not a declared one", () => {
    expect(propertyKeyProblem("", ["a"])).toBe("A key is required.");
    expect(propertyKeyProblem("  ", ["a"])).toBe("A key is required.");
    expect(propertyKeyProblem("a", ["a", "b"])).toBe("Another property is named a.");
    expect(propertyKeyProblem("retentionDays", ["a"], ["retentionDays"])).toBe("retentionDays is a declared custom property; set it above.");
    expect(propertyKeyProblem(" owner ", ["a"], ["retentionDays"])).toBeNull();
  });

  it("reads a value under its type, and says what is wrong", () => {
    expect(parsePropertyValue("finance", "text")).toEqual({ value: "finance" });
    expect(parsePropertyValue(" 12.5 ", "number")).toEqual({ value: 12.5 });
    expect(parsePropertyValue("twelve", "number")).toEqual({ error: "Not a number." });
    expect(parsePropertyValue("", "number")).toEqual({ error: "Not a number." });
    expect(parsePropertyValue("TRUE", "boolean")).toEqual({ value: true });
    expect(parsePropertyValue("maybe", "boolean")).toEqual({ error: "Not true or false." });
    expect(parsePropertyValue('{"a":1}', "json")).toEqual({ value: { a: 1 } });
    expect(parsePropertyValue("{", "json")).toEqual({ error: "Not valid JSON." });
  });

  it("converts a value to another type: a number must read as one, true/false takes true", () => {
    expect(convertPropertyValue("3", "number")).toEqual({ value: 3 });
    expect(convertPropertyValue("finance", "number")).toEqual({ error: "Not a number." });
    expect(convertPropertyValue(3, "text")).toEqual({ value: "3" });
    expect(convertPropertyValue("true", "boolean")).toEqual({ value: true });
    expect(convertPropertyValue("yes", "boolean")).toEqual({ value: false });
    expect(convertPropertyValue(true, "text")).toEqual({ value: "true" });
  });

  it("infers a stored value's type", () => {
    expect([propertyTypeOf("x"), propertyTypeOf(1), propertyTypeOf(false), propertyTypeOf(null), propertyTypeOf([1])]).toEqual([
      "text",
      "number",
      "boolean",
      "json",
      "json",
    ]);
  });
});
