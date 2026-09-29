// Project branding (settings `branding`, MQ8001 to MQ8003): the primary color per theme applied through the
// design tokens (accent, accent-subtle, accent-foreground; focus rings and selection use them), the icon in the
// top bar, the browser tab and the sign-in page, with the M mark as the default. Colors come from the project
// or from the viewer's draft (a live preview while Settings › General is edited); no color literal lives here.
import { useSyncExternalStore } from "react";
import { toLongHex } from "@/lib/color";

export type ThemeName = "light" | "dark";
export type BrandColors = { light?: string | null; dark?: string | null };
export type Rgb = [number, number, number];

/** Below this contrast against a theme's surfaces the accent is hard to see (WCAG 2.2 non-text contrast). */
export const MIN_BRAND_CONTRAST = 3;
/** The contrast text needs (WCAG AA): the accent is also a text color and the fill behind button text. */
export const MIN_TEXT_CONTRAST = 4.5;
/** The largest icon the server accepts (MQ8003). */
export const MAX_ICON_BYTES = 512 * 1024;
export const BRANDING_STYLE_ID = "mq-branding";
export const FAVICON_ID = "mq-favicon";

const HEX = /^#(?:[0-9a-fA-F]{3}|[0-9a-fA-F]{6})$/;

/** Whether a value passes MQ8001: #rgb or #rrggbb. */
export function isHexColor(value: string | null | undefined): value is string {
  return !!value && HEX.test(value.trim());
}

/** #rgb or #rrggbb as lowercase #rrggbb, else null. */
export function normalizeHexColor(value: string | null | undefined): string | null {
  if (!isHexColor(value)) return null;
  const v = value.trim().toLowerCase();
  return v.length === 4 ? `#${v[1]}${v[1]}${v[2]}${v[2]}${v[3]}${v[3]}` : v;
}

export function parseHexColor(value: string | null | undefined): Rgb | null {
  const hex = normalizeHexColor(value) ?? normalizeHexColor(toLongHex(value ?? "").slice(0, 7));
  if (!hex) return null;
  return [parseInt(hex.slice(1, 3), 16), parseInt(hex.slice(3, 5), 16), parseInt(hex.slice(5, 7), 16)];
}

/** WCAG 2.2 relative luminance. */
export function relativeLuminance([r, g, b]: Rgb): number {
  const channel = (c: number) => {
    const s = c / 255;
    return s <= 0.04045 ? s / 12.92 : ((s + 0.055) / 1.055) ** 2.4;
  };
  return 0.2126 * channel(r) + 0.7152 * channel(g) + 0.0722 * channel(b);
}

/** The WCAG contrast ratio of two colors (1 to 21), or null when either is not a color. */
export function contrastRatio(a: string, b: string): number | null {
  const x = parseHexColor(a);
  const y = parseHexColor(b);
  if (!x || !y) return null;
  const [hi, lo] = [relativeLuminance(x), relativeLuminance(y)].sort((p, q) => q - p);
  return (hi + 0.05) / (lo + 0.05);
}

export interface ContrastWarning {
  /** The lowest contrast against the theme's surfaces, and that surface. */
  ratio: number;
  surface: string;
  /** The contrast of the text color drawn on the accent (buttons, badges). */
  foreground: number;
}

/**
 * A warning when the color is below 4.5:1 against a theme surface (accent text, and below 3:1 the focus rings and
 * selection too) or when the text drawn on it reaches less than 4.5:1; else null.
 */
export function contrastWarning(color: string, surfaces: readonly string[]): ContrastWarning | null {
  let worst: { ratio: number; surface: string } | null = null;
  for (const surface of surfaces) {
    const ratio = contrastRatio(color, surface);
    if (ratio !== null && (!worst || ratio < worst.ratio)) worst = { ratio, surface };
  }
  const foreground = accentForegroundContrast(color);
  if (!worst || foreground === null) return null;
  return worst.ratio < MIN_TEXT_CONTRAST || foreground < MIN_TEXT_CONTRAST ? { ...worst, foreground } : null;
}

/** The warning's sentence: which use of the accent falls short, with both ratios. */
export function contrastWarningText(w: ContrastWarning, theme: ThemeName): string {
  const parts: string[] = [];
  if (w.ratio < MIN_BRAND_CONTRAST)
    parts.push(`Contrast ${w.ratio.toFixed(1)}:1 against the ${theme} theme's surfaces; below 3:1 the accent, focus rings and selection are hard to see.`);
  else if (w.ratio < MIN_TEXT_CONTRAST)
    parts.push(`Contrast ${w.ratio.toFixed(1)}:1 against the ${theme} theme's surfaces; below 4.5:1 accent text is hard to read.`);
  if (w.foreground < MIN_TEXT_CONTRAST) parts.push(`Button text on the accent reaches ${w.foreground.toFixed(1)}:1, below 4.5:1.`);
  return parts.join(" ");
}

const ON_LIGHT: Rgb = [15, 17, 21];
const ON_DARK: Rgb = [255, 255, 255];
const rgb = ([r, g, b]: Rgb) => `rgb(${r} ${g} ${b})`;

function foregrounds(hex: string): { onDark: number; onLight: number } {
  const lum = relativeLuminance(parseHexColor(hex)!);
  return { onDark: 1.05 / (lum + 0.05), onLight: (lum + 0.05) / (relativeLuminance(ON_LIGHT) + 0.05) };
}

/** The contrast of the text color brandTokens picks for the accent (the better of the two), or null for a bad color. */
export function accentForegroundContrast(color: string): number | null {
  const hex = normalizeHexColor(color);
  if (!hex) return null;
  const { onDark, onLight } = foregrounds(hex);
  return Math.max(onDark, onLight);
}

/** The accent tokens for a primary color: the color, a subtle tint over the theme's surface, and a readable text color on it. */
export function brandTokens(color: string, theme: ThemeName): Record<string, string> | null {
  const hex = normalizeHexColor(color);
  if (!hex) return null;
  const { onDark, onLight } = foregrounds(hex);
  return {
    "--mq-accent": hex,
    "--mq-accent-subtle": `color-mix(in srgb, ${hex} ${theme === "light" ? 12 : 22}%, var(--mq-bg-surface))`,
    "--mq-accent-foreground": rgb(onDark >= onLight ? ON_DARK : ON_LIGHT),
  };
}

/** The style sheet that overrides the accent tokens per theme; empty when the project keeps the built-in accent. */
export function brandingCss(colors: BrandColors | null | undefined): string {
  const rules: string[] = [];
  const selectors: Record<ThemeName, string> = { light: ':root:not([data-theme="dark"])', dark: ':root[data-theme="dark"]' };
  for (const theme of ["light", "dark"] as const) {
    const tokens = colors?.[theme] ? brandTokens(colors[theme]!, theme) : null;
    if (!tokens) continue;
    rules.push(
      `${selectors[theme]} { ${Object.entries(tokens)
        .map(([k, v]) => `${k}: ${v};`)
        .join(" ")} }`,
    );
  }
  return rules.join("\n");
}

/** The M mark as an SVG data URL, drawn in the given accent and foreground (the tab icon without a project icon). */
export function markDataUrl(accent: string, foreground: string): string {
  const svg =
    `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 32 32"><rect width="32" height="32" rx="6" fill="${accent}"/>` +
    `<text x="16" y="23" font-family="system-ui,sans-serif" font-size="20" font-weight="600" text-anchor="middle" fill="${foreground}">M</text></svg>`;
  return `data:image/svg+xml,${encodeURIComponent(svg)}`;
}

/**
 * Applies the branding to a document: the token style sheet (<style id="mq-branding">) and the tab icon
 * (<link id="mq-favicon" rel="icon">): the project icon, else the M mark in the resolved accent.
 */
export function applyBranding(doc: Document, input: { colors: BrandColors | null | undefined; iconUrl: string | null }): void {
  let style = doc.getElementById(BRANDING_STYLE_ID) as HTMLStyleElement | null;
  if (!style) {
    style = doc.createElement("style");
    style.id = BRANDING_STYLE_ID;
    doc.head.appendChild(style);
  }
  const css = brandingCss(input.colors);
  if (style.textContent !== css) style.textContent = css;

  let link = doc.getElementById(FAVICON_ID) as HTMLLinkElement | null;
  if (!link) {
    link = doc.createElement("link");
    link.id = FAVICON_ID;
    link.rel = "icon";
    doc.head.appendChild(link);
  }
  let href = input.iconUrl;
  if (!href) {
    const computed = doc.defaultView?.getComputedStyle(doc.documentElement);
    const accent = computed?.getPropertyValue("--mq-accent").trim() || "currentColor";
    const foreground = computed?.getPropertyValue("--mq-accent-foreground").trim() || "Canvas";
    href = markDataUrl(accent, foreground);
  }
  const type = href.startsWith("data:image/svg") || /\.svg(\?|$)/.test(href) ? "image/svg+xml" : "";
  if (link.getAttribute("href") !== href) link.setAttribute("href", href);
  if (type) link.type = type;
  else link.removeAttribute("type");
}

/** The surfaces the accent sits on in a theme (app background and panels), read from tokens.css through a probe. */
export function themeSurfaces(doc: Document, theme: ThemeName): string[] {
  const probe = doc.createElement("div");
  probe.dataset.theme = theme;
  probe.hidden = true;
  doc.body.appendChild(probe);
  try {
    const style = doc.defaultView?.getComputedStyle(probe);
    return ["--mq-bg-surface", "--mq-bg-app"].map((token) => toLongHex(style?.getPropertyValue(token).trim() ?? "")).filter((v) => isHexColor(v));
  } finally {
    probe.remove();
  }
}

/** The built-in accent of a theme (the color input's value before the project picks one). */
export function builtInAccent(doc: Document, theme: ThemeName): string | null {
  const probe = doc.createElement("div");
  probe.dataset.theme = theme;
  probe.hidden = true;
  doc.body.appendChild(probe);
  try {
    return normalizeHexColor(toLongHex(doc.defaultView?.getComputedStyle(probe).getPropertyValue("--mq-accent").trim() ?? ""));
  } finally {
    probe.remove();
  }
}

// ------------------------------------------------------------------ the draft preview

/** What Settings › General shows before it is saved: undefined members follow the project. */
export type BrandingPreview = { name?: string; colors?: BrandColors; iconUrl?: string | null };

let preview: BrandingPreview | null = null;
const listeners = new Set<() => void>();

export function setBrandingPreview(next: BrandingPreview | null): void {
  preview = next;
  for (const listener of listeners) listener();
}

export function getBrandingPreview(): BrandingPreview | null {
  return preview;
}

export function useBrandingPreview(): BrandingPreview | null {
  return useSyncExternalStore(
    (listener) => {
      listeners.add(listener);
      return () => listeners.delete(listener);
    },
    getBrandingPreview,
    getBrandingPreview,
  );
}
