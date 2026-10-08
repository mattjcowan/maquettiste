// The project's branding in the shell: the name and icon of the top bar, the tab icon and the accent tokens
// (src/design/branding.ts), from GET /api/project or, while Settings › General has unsaved changes, its preview.
import { useEffect } from "react";
import { useProject } from "@/api/queries";
import { BRANDING_ICON_URL } from "@/api/endpoints";
import { applyBranding, useBrandingPreview, type BrandColors } from "@/design/branding";
import { useEditor } from "@/state/store";
import { useServices } from "./context";

const PRODUCT_NAME = "Maquettiste";

export type BrandingView = { name: string; iconUrl: string | null; colors: BrandColors | null };

/** The project icon's URL, refreshed by its hash; null without a usable icon. */
export function projectIconUrl(icon: string | null | undefined, iconHash: string | null | undefined): string | null {
  return icon && iconHash ? `${BRANDING_ICON_URL}?v=${iconHash.slice(0, 12)}` : null;
}

export function useBrandingView(): BrandingView {
  const project = useProject();
  const preview = useBrandingPreview();
  const branding = project.data?.settings.branding;
  const name = project.data?.name ?? PRODUCT_NAME;
  return {
    name: preview?.name ? preview.name : name,
    iconUrl: preview && preview.iconUrl !== undefined ? preview.iconUrl : projectIconUrl(branding?.icon, project.data?.iconHash),
    colors: preview?.colors ?? branding?.colors ?? null,
  };
}

/** Keeps the token style sheet, the tab icon and the tab title in step with the branding and the theme. */
export function BrandingSync() {
  const { store } = useServices();
  const theme = useEditor(store, (s) => s.theme);
  const { colors, iconUrl, name } = useBrandingView();
  // The browser tab names the project first, so editors of several projects (or one on several ports) tell apart.
  useEffect(() => {
    document.title = name === PRODUCT_NAME ? PRODUCT_NAME : `${name} — ${PRODUCT_NAME}`;
  }, [name]);
  const light = colors?.light ?? null;
  const dark = colors?.dark ?? null;
  useEffect(() => {
    applyBranding(document, { colors: { light, dark }, iconUrl });
    // The theme attribute may change in the same commit; the mark's colors are read once it has.
    const frame = requestAnimationFrame(() => applyBranding(document, { colors: { light, dark }, iconUrl }));
    return () => cancelAnimationFrame(frame);
  }, [light, dark, iconUrl, theme]);
  return null;
}
