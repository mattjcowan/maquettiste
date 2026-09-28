// Theme and density: the OS preference by default, with a manual override remembered per browser
// (localStorage mq.theme, mq.density). The resolved theme is written to <html data-theme>, which
// tokens.css keys on.
import type { Density, ThemeChoice } from "@/state/store";

export function systemPrefersDark(): boolean {
  return typeof window !== "undefined" && !!window.matchMedia?.("(prefers-color-scheme: dark)").matches;
}

export function resolveTheme(choice: ThemeChoice): "light" | "dark" {
  return choice === "system" ? (systemPrefersDark() ? "dark" : "light") : choice;
}

export function applyTheme(choice: ThemeChoice): void {
  document.documentElement.dataset.theme = resolveTheme(choice);
}

export function applyDensity(density: Density): void {
  document.documentElement.dataset.density = density;
}

/** Follows OS changes while the choice is "system"; returns the unsubscribe function. */
export function followSystemTheme(getChoice: () => ThemeChoice): () => void {
  const query = window.matchMedia?.("(prefers-color-scheme: dark)");
  if (!query) return () => undefined;
  const listener = () => {
    if (getChoice() === "system") applyTheme("system");
  };
  query.addEventListener("change", listener);
  return () => query.removeEventListener("change", listener);
}

/** The categorical token for a 1-based palette index (category colors, diagram groups). */
export function categoryVar(index: number | null): string {
  return index === null ? "var(--mq-border-strong)" : `var(--mq-cat-${index})`;
}
