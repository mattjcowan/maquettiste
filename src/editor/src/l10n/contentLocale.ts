// The content locale (reference-types-seeds-localization.md 3.10): the language the tree, canvases, the Reference data
// screen and search show. The user's choice is kept per browser; the locale in effect is that choice when the project
// declares it (else the default, null), set by <LocalizationSync> once the settings are known. The index loader reads
// the one in effect to ask for `?locale=`.
import { useSyncExternalStore } from "react";
import { local } from "@/lib/storage";

const KEY = "maquettiste.contentLocale";
let preferred: string | null = local.get(KEY);
let effective: string | null = null;
const listeners = new Set<() => void>();
const emit = () => listeners.forEach((l) => l());

export function preferredContentLocale(): string | null {
  return preferred;
}

/** Chooses the content locale (null: the default locale), remembered in this browser. */
export function setPreferredContentLocale(tag: string | null): void {
  if (tag === preferred) return;
  preferred = tag;
  if (tag) local.set(KEY, tag);
  else local.remove(KEY);
  emit();
}

/** The content locale in effect: null for the default locale. */
export function currentContentLocale(): string | null {
  return effective;
}

/** Sets the locale in effect; true when it changed (the index must then be refetched). */
export function setEffectiveContentLocale(tag: string | null): boolean {
  if (tag === effective) return false;
  effective = tag;
  emit();
  return true;
}

/** Tests: forget the choice. */
export function resetContentLocale(): void {
  preferred = null;
  effective = null;
  local.remove(KEY);
  emit();
}

const subscribe = (l: () => void) => {
  listeners.add(l);
  return () => void listeners.delete(l);
};

export function useContentLocale(): { preferred: string | null; effective: string | null } {
  const p = useSyncExternalStore(subscribe, () => preferred);
  const e = useSyncExternalStore(subscribe, () => effective);
  return { preferred: p, effective: e };
}
