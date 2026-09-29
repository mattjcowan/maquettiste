// Project branding (settings `branding`): the accent tokens per theme, the contrast check against the theme's
// surfaces, the tab icon, and Settings › General's draft written back into maquettiste.json.
import { afterEach, describe, expect, it } from "vitest";
import {
  BRANDING_STYLE_ID,
  FAVICON_ID,
  applyBranding,
  brandTokens,
  brandingCss,
  contrastRatio,
  contrastWarning,
  contrastWarningText,
  isHexColor,
  markDataUrl,
  normalizeHexColor,
} from "@/design/branding";
import { colorProblem, fileToBase64, generalOf, iconContentType, withGeneral } from "@/workspaces/settings/general";
import type { SettingsJson } from "@/api/types";

// Color literals live in tokens.css only (scripts/check-hex.mjs); the tests build them.
const hex = (s: string) => `#${s}`;
const WHITE = hex("ffffff");
const BLACK = hex("000000");
const PURPLE = hex("7a1fa2");
const YELLOW = hex("ffeb3b");
const LIGHT_SURFACES = [hex("ffffff"), hex("f4f5f7")];
const DARK_SURFACES = [hex("16191f"), hex("0f1115")];

afterEach(() => {
  document.getElementById(BRANDING_STYLE_ID)?.remove();
  document.getElementById(FAVICON_ID)?.remove();
});

describe("colors", () => {
  it("accepts #rgb and #rrggbb only (MQ8001) and normalizes to lowercase #rrggbb", () => {
    expect(isHexColor(hex("abc"))).toBe(true);
    expect(isHexColor(hex("A1B2C3"))).toBe(true);
    expect(isHexColor("blue")).toBe(false);
    expect(isHexColor(hex("12345"))).toBe(false);
    expect(normalizeHexColor(hex("ABC"))).toBe(hex("aabbcc"));
    expect(normalizeHexColor(" " + hex("7A1FA2") + " ")).toBe(PURPLE);
    expect(normalizeHexColor("rebeccapurple")).toBeNull();
    expect(colorProblem("blue")).toContain("hex color");
    expect(colorProblem(PURPLE)).toBeNull();
    expect(colorProblem(null)).toBeNull();
  });

  it("computes WCAG contrast and warns below 3:1 against the theme's surfaces", () => {
    expect(contrastRatio(WHITE, BLACK)).toBeCloseTo(21, 5);
    expect(contrastRatio(WHITE, WHITE)).toBeCloseTo(1, 5);
    expect(contrastRatio("nope", WHITE)).toBeNull();
    expect(contrastWarning(PURPLE, LIGHT_SURFACES)).toBeNull();
    const yellow = contrastWarning(YELLOW, LIGHT_SURFACES)!;
    expect(yellow.ratio).toBeLessThan(3);
    expect(yellow.surface).toBe(hex("f4f5f7")); // the worst surface is named
    expect(contrastWarning(YELLOW, DARK_SURFACES)).toBeNull();
    expect(contrastWarning(PURPLE, DARK_SURFACES)!.ratio).toBeLessThan(3);
  });

  it("warns below 4.5:1 for accent text and for the text drawn on the accent", () => {
    const blue = contrastWarning(hex("3b8fd0"), [WHITE])!;
    expect(blue.ratio).toBeGreaterThan(3);
    expect(blue.ratio).toBeLessThan(4.5);
    expect(contrastWarningText(blue, "light")).toContain("below 4.5:1 accent text is hard to read");
    const grey = contrastWarning(hex("7a7a7a"), [WHITE])!;
    expect(grey.foreground).toBeLessThan(4.5);
    expect(contrastWarningText(grey, "light")).toMatch(/Button text on the accent reaches 4\.\d:1, below 4\.5:1\./);
    expect(contrastWarning(hex("000000"), [WHITE])).toBeNull();
  });
});

describe("tokens", () => {
  it("derives the accent, a subtle tint over the surface and a readable foreground", () => {
    expect(brandTokens(PURPLE, "light")).toEqual({
      "--mq-accent": PURPLE,
      "--mq-accent-subtle": `color-mix(in srgb, ${PURPLE} 12%, var(--mq-bg-surface))`,
      "--mq-accent-foreground": "rgb(255 255 255)",
    });
    expect(brandTokens(YELLOW, "dark")!["--mq-accent-foreground"]).toBe("rgb(15 17 21)");
    expect(brandTokens("blue", "light")).toBeNull();
  });

  it("writes one rule per theme that has a color, keyed so the other theme keeps its accent", () => {
    const css = brandingCss({ light: PURPLE, dark: null });
    expect(css).toContain(`:root:not([data-theme="dark"]) { --mq-accent: ${PURPLE};`);
    expect(css).not.toContain('[data-theme="dark"] {');
    expect(brandingCss({ light: null, dark: YELLOW })).toMatch(/^:root\[data-theme="dark"\] \{ --mq-accent: #ffeb3b;/);
    expect(brandingCss({ light: "not-a-color" })).toBe("");
    expect(brandingCss(null)).toBe("");
  });

  it("applies the style sheet and the tab icon to the document, replacing them in place", () => {
    applyBranding(document, { colors: { light: PURPLE, dark: YELLOW }, iconUrl: null });
    const style = document.getElementById(BRANDING_STYLE_ID)!;
    const link = document.getElementById(FAVICON_ID) as HTMLLinkElement;
    expect(style.textContent).toBe(brandingCss({ light: PURPLE, dark: YELLOW }));
    expect(link.rel).toBe("icon");
    expect(link.getAttribute("href")).toMatch(/^data:image\/svg\+xml,/);
    expect(decodeURIComponent(link.getAttribute("href")!)).toContain(">M</text>");

    applyBranding(document, { colors: null, iconUrl: "/api/project/branding/icon?v=abc" });
    expect(document.querySelectorAll(`#${BRANDING_STYLE_ID}`)).toHaveLength(1);
    expect(document.getElementById(BRANDING_STYLE_ID)!.textContent).toBe("");
    expect(document.querySelectorAll(`#${FAVICON_ID}`)).toHaveLength(1);
    expect(document.getElementById(FAVICON_ID)!.getAttribute("href")).toBe("/api/project/branding/icon?v=abc");
  });

  it("draws the M mark in the given colors", () => {
    const url = decodeURIComponent(markDataUrl("var-a", "var-b"));
    expect(url).toContain('fill="var-a"');
    expect(url).toContain('fill="var-b">M<');
  });
});

describe("Settings › General", () => {
  const base = { formatVersion: 1, name: "billing", outputs: { allow: [] } } as unknown as SettingsJson;

  it("reads and writes the name and branding, leaving empty members out", () => {
    expect(generalOf(base)).toEqual({ name: "billing", icon: null, light: null, dark: null });
    const next = withGeneral(base, { name: "  Partner app ", icon: "branding/icon.svg", light: hex("7A1FA2"), dark: null });
    expect(next.name).toBe("Partner app");
    expect((next as { branding?: unknown }).branding).toEqual({ icon: "branding/icon.svg", colors: { light: PURPLE } });
    expect(generalOf(next)).toEqual({ name: "Partner app", icon: "branding/icon.svg", light: PURPLE, dark: null });
    const cleared = withGeneral(next, { name: "", icon: null, light: null, dark: null });
    expect("name" in cleared).toBe(false);
    expect("branding" in cleared).toBe(false);
    expect(base.name).toBe("billing"); // not mutated
  });

  it("accepts SVG and PNG icons by type or extension and encodes them as base64", async () => {
    expect(iconContentType({ name: "logo.SVG", type: "" })).toBe("image/svg+xml");
    expect(iconContentType({ name: "x", type: "image/png" })).toBe("image/png");
    expect(iconContentType({ name: "logo.gif", type: "image/gif" })).toBeNull();
    expect(await fileToBase64(new Blob(["<svg/>"]))).toBe(btoa("<svg/>"));
  });
});
