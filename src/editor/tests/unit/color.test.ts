// Token colors handed to Monaco must be #rrggbb[aa]; the minified CSS holds #fff.
import { describe, expect, it } from "vitest";
import { toLongHex } from "@/lib/color";

describe("toLongHex", () => {
  it("expands short hex and lowercases", () => {
    expect(toLongHex("#fff")).toBe("#ffffff");
    expect(toLongHex(" #0B5CD5 ")).toBe("#0b5cd5");
    expect(toLongHex("#abcd")).toBe("#aabbccdd");
  });
  it("converts rgb and rgba", () => {
    expect(toLongHex("rgb(255, 0, 16)")).toBe("#ff0010");
    expect(toLongHex("rgba(0, 0, 0, 0.5)")).toBe("#00000080");
    expect(toLongHex("rgb(0 0 0 / 100%)")).toBe("#000000");
  });
  it("leaves other values alone", () => {
    expect(toLongHex("oklch(0.5 0.1 200)")).toBe("oklch(0.5 0.1 200)");
    expect(toLongHex("")).toBe("");
  });
});
