// The inspector's tabs: Properties (the scalar fields), Attributes (only for kinds with attributes), JSON and Used.
// Value objects, stereotypes and relationships edit their attribute grid here; an entity lists its attributes read-only, its
// grid is the entity editor's. The selected tab is remembered per kind; a remembered tab the kind lacks falls back
// to Properties.
import type { ElementKind } from "@/api/types";
import { USED_LABEL } from "@/model/labels";

export type InspectorTab = "properties" | "attributes" | "json" | "references";

export const INSPECTOR_TAB_LABELS: Record<InspectorTab, string> = {
  properties: "Properties",
  attributes: "Attributes",
  json: "JSON",
  references: USED_LABEL,
};

/** How the Attributes tab shows a kind's attributes: the editable grid, a read-only list, or no tab. */
export function attributesView(kind: ElementKind | string): "grid" | "list" | null {
  if (kind === "value-object" || kind === "stereotype" || kind === "relation") return "grid";
  if (kind === "entity") return "list";
  return null;
}

/** The tabs a kind shows, in order. */
export function inspectorTabs(kind: ElementKind | string): InspectorTab[] {
  return attributesView(kind) ? ["properties", "attributes", "json", "references"] : ["properties", "json", "references"];
}

/** The tab to show for `kind` given the one remembered for it; anything it does not have is Properties. */
export function resolveInspectorTab(kind: ElementKind | string, remembered: string | undefined): InspectorTab {
  return inspectorTabs(kind).find((t) => t === remembered) ?? "properties";
}
