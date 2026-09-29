/// <reference types="node" />
// One density (generation-ui.md 6, E23): ROW_H in src/design/density.ts and --mq-row-h in tokens.css agree, and
// the comfortable variant is gone.
import { readFileSync } from "node:fs";
import path from "node:path";
import { describe, expect, it } from "vitest";
import { ROW_H, TOUCH_ROW_H, rowHeight } from "@/design/density";

const css = readFileSync(path.resolve(import.meta.dirname, "../../src/design/tokens.css"), "utf8");

describe("density", () => {
  it("shares the row height between CSS and JavaScript", () => {
    const root = /:root,\s*\[data-theme="light"\]\s*\{([^}]*)\}/.exec(css)![1];
    expect(root).toContain(`--mq-row-h: ${ROW_H}px;`);
    expect(ROW_H).toBe(24);
  });

  it("has no comfortable density and keeps 44 px rows for a coarse pointer only", () => {
    expect(css).not.toContain("data-density");
    expect(css).toMatch(new RegExp(`@media \\(pointer: coarse\\)\\s*\\{\\s*:root\\s*\\{\\s*--mq-row-h: ${TOUCH_ROW_H}px;`));
  });

  it("reads the pointer for the virtualizers", () => {
    const original = window.matchMedia;
    window.matchMedia = ((q: string) => ({ matches: q === "(pointer: coarse)" })) as unknown as typeof window.matchMedia;
    expect(rowHeight()).toBe(TOUCH_ROW_H);
    window.matchMedia = (() => ({ matches: false })) as unknown as typeof window.matchMedia;
    expect(rowHeight()).toBe(ROW_H);
    window.matchMedia = original;
  });
});
