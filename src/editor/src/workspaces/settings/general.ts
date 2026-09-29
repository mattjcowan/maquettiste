// Settings › General as data: the project name (maquettiste.json `name`, the field `maquettiste init --name`
// writes) and `branding` (icon, primary color per theme). Pure, so the form and the tests share it.
import type { SettingsJson } from "@/api/types";
import { isHexColor, normalizeHexColor } from "@/design/branding";
import { clone } from "@/lib/json";

export type GeneralDraft = { name: string; icon: string | null; light: string | null; dark: string | null };

type BrandingJson = { icon?: string | null; colors?: { light?: string | null; dark?: string | null } };

export function generalOf(json: SettingsJson): GeneralDraft {
  const branding = (json as { branding?: BrandingJson }).branding;
  return {
    name: json.name ?? "",
    icon: branding?.icon ?? null,
    light: branding?.colors?.light ?? null,
    dark: branding?.colors?.dark ?? null,
  };
}

export function sameGeneral(a: GeneralDraft, b: GeneralDraft): boolean {
  return a.name === b.name && a.icon === b.icon && a.light === b.light && a.dark === b.dark;
}

/** The settings with the draft applied: empty members are left out, as the canonical writer would. */
export function withGeneral(json: SettingsJson, draft: GeneralDraft): SettingsJson {
  const next = clone(json) as SettingsJson & { branding?: BrandingJson };
  const name = draft.name.trim();
  if (name) next.name = name;
  else delete next.name;
  const colors: NonNullable<BrandingJson["colors"]> = {};
  if (draft.light) colors.light = normalizeHexColor(draft.light) ?? draft.light;
  if (draft.dark) colors.dark = normalizeHexColor(draft.dark) ?? draft.dark;
  const branding: BrandingJson = {};
  if (draft.icon) branding.icon = draft.icon;
  if (Object.keys(colors).length) branding.colors = colors;
  if (Object.keys(branding).length) next.branding = branding;
  else delete next.branding;
  return next;
}

/** Why a typed color would fail MQ8001, or null. */
export function colorProblem(value: string | null): string | null {
  if (!value) return null;
  return isHexColor(value) ? null : "Use a hex color: # and six (or three) hex digits.";
}

/** A file as base64, for POST /api/project/branding/icon. */
export function fileToBase64(file: Blob): Promise<string> {
  return new Promise((resolve, reject) => {
    const reader = new FileReader();
    reader.onload = () => resolve(String(reader.result).replace(/^data:[^,]*,/, ""));
    reader.onerror = () => reject(reader.error ?? new Error("The file cannot be read."));
    reader.readAsDataURL(file);
  });
}

/** The media type of an icon file by type or extension; null for anything but SVG and PNG. */
export function iconContentType(file: { name: string; type: string }): "image/svg+xml" | "image/png" | null {
  if (file.type === "image/svg+xml" || /\.svg$/i.test(file.name)) return "image/svg+xml";
  if (file.type === "image/png" || /\.png$/i.test(file.name)) return "image/png";
  return null;
}
