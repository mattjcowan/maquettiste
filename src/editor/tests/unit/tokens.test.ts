/// <reference types="node" />
// WCAG 2.2 contrast for every pairing phase2-design.md 4.7 names, in both themes, read straight
// from src/design/tokens.css so the test and the stylesheet cannot disagree.
import { readFileSync } from "node:fs";
import path from "node:path";
import { describe, expect, it } from "vitest";

const css = readFileSync(path.resolve(import.meta.dirname, "../../src/design/tokens.css"), "utf8");

function block(selector: RegExp): Record<string, string> {
  const match = selector.exec(css);
  if (!match) throw new Error(`No block for ${selector}`);
  const body = css.slice(match.index + match[0].length, css.indexOf("}", match.index));
  const out: Record<string, string> = {};
  for (const m of body.matchAll(/--mq-([a-z0-9-]+):\s*(#[0-9a-fA-F]{6})\b/g)) out[m[1]] = m[2];
  return out;
}

const THEMES = {
  light: block(/:root,\s*\[data-theme="light"\]\s*\{/),
  dark: block(/\[data-theme="dark"\]\s*\{/),
};

function luminance(hex: string): number {
  const channel = (i: number) => {
    const c = parseInt(hex.slice(i, i + 2), 16) / 255;
    return c <= 0.04045 ? c / 12.92 : ((c + 0.055) / 1.055) ** 2.4;
  };
  return 0.2126 * channel(1) + 0.7152 * channel(3) + 0.0722 * channel(5);
}

export function contrast(a: string, b: string): number {
  const [hi, lo] = [luminance(a), luminance(b)].sort((x, y) => y - x);
  return (hi + 0.05) / (lo + 0.05);
}

const BACKGROUNDS = ["bg-app", "bg-surface", "bg-raised", "bg-canvas", "accent-subtle"];
const TEXT = ["text-primary", "text-secondary", "status-success", "status-warning", "status-danger"];
const CATEGORIES = Array.from({ length: 8 }, (_, i) => `cat-${i + 1}`);

describe.each(Object.entries(THEMES))("%s theme tokens", (_theme, tokens) => {
  const failures = (fg: string[], bg: string[], min: number) =>
    fg.flatMap((f) => bg.map((b) => ({ pair: `${f} on ${b}`, ratio: contrast(tokens[f], tokens[b]) }))).filter((p) => p.ratio < min);

  it("defines every token the design names", () => {
    for (const name of [...BACKGROUNDS, ...TEXT, ...CATEGORIES, "accent", "accent-foreground", "border-default", "border-strong", "border-input"])
      expect(tokens[name], name).toMatch(/^#[0-9a-fA-F]{6}$/);
  });

  it("text and status colors reach 4.5:1 on every background", () => {
    expect(failures(TEXT, BACKGROUNDS, 4.5)).toEqual([]);
  });

  it("accent-foreground reaches 4.5:1 on the accent", () => {
    expect(contrast(tokens["accent-foreground"], tokens.accent)).toBeGreaterThanOrEqual(4.5);
  });

  it("control outlines and the accent (focus ring) reach 3:1", () => {
    expect(failures(["border-input", "accent"], BACKGROUNDS, 3)).toEqual([]);
  });

  it("categorical colors reach 3:1 on surfaces and the canvas", () => {
    expect(failures(CATEGORIES, ["bg-surface", "bg-canvas"], 3)).toEqual([]);
  });
});

describe("adjusted values (phase2-design.md 4.7)", () => {
  it("uses the phase 2 values where S15 failed", () => {
    expect(THEMES.light["status-warning"].toLowerCase()).toBe("#9e5400");
    expect(THEMES.light["status-success"].toLowerCase()).toBe("#1b7348");
    expect(THEMES.light["accent-foreground"].toLowerCase()).toBe("#ffffff");
    expect(THEMES.dark["accent-foreground"].toLowerCase()).toBe("#0f1115");
    expect(THEMES.light["border-input"].toLowerCase()).toBe("#7d8694");
    expect(THEMES.dark["border-input"].toLowerCase()).toBe("#6b7482");
  });
});
