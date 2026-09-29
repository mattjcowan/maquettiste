// The Generate explorer's rows (generation-ui.md 2.1), pure: pack nodes with Units, Templates, Parameters and
// Outputs, flattened in display order for a virtualized ARIA tree. Keys are stable (`p:<pack>`, `p:<pack>/units`,
// `p:<pack>/u:<id>`, ...), so expansion survives a reload of the packs.
import type { components } from "@/api/schema";
import { modeLabel, unitLine, filterWords, type RawUnit } from "./unitsModel";
import { groupLine, groupOutputs, outputRows, type OutputState } from "./outputsModel";

type S = components["schemas"];

export type RowKind = "pack" | "units" | "unit" | "templates" | "file" | "parameters" | "parameter" | "outputs" | "output-group" | "output";

export interface PackRow {
  key: string;
  kind: RowKind;
  level: number;
  label: string;
  detail: string;
  pack: string;
  expandable: boolean;
  expanded: boolean;
  setsize: number;
  posinset: number;
  unit?: string;
  parameter?: string;
  path?: string;
  state?: OutputState;
  filter?: string;
  warnings?: number;
  off?: boolean;
  mono?: boolean;
}

export interface PackDetails {
  document?: S["PackDocument"];
  outputs?: S["PackOutputs"];
}

const plural = (n: number, one: string, many = `${one}s`) => `${n} ${n === 1 ? one : many}`;

/** Distinct output roots the pack's last run wrote. */
export const rootCount = (outputs: S["PackOutputs"] | undefined): number | null => (outputs ? new Set(outputs.outputs.map((o) => o.root ?? "")).size : null);

function valueText(value: unknown): string {
  return typeof value === "string" ? JSON.stringify(value) : (JSON.stringify(value) ?? "null");
}

export function packRows(
  packs: S["PackSummary"][],
  expanded: ReadonlySet<string>,
  details: ReadonlyMap<string, PackDetails>,
  hasElement: (id: string) => boolean = () => true,
): PackRow[] {
  const out: PackRow[] = [];
  const children = (rows: Omit<PackRow, "setsize" | "posinset">[]) => rows.forEach((r, i) => out.push({ ...r, setsize: rows.length, posinset: i + 1 }));
  packs.forEach((pack, pi) => {
    const key = `p:${pack.name}`;
    const open = expanded.has(key);
    const d = details.get(pack.name) ?? {};
    const roots = rootCount(d.outputs);
    out.push({
      key,
      kind: "pack",
      level: 1,
      label: pack.name,
      detail: [plural(pack.units.length, "unit"), roots !== null ? plural(roots, "root") : null].filter(Boolean).join(" · "),
      pack: pack.name,
      expandable: true,
      expanded: open,
      setsize: packs.length,
      posinset: pi + 1,
      warnings: pack.diagnostics.length,
      off: !pack.enabled,
    });
    if (!open) return;
    const section = (name: RowKind, label: string, detail: string) => ({
      key: `${key}/${name}`,
      kind: name,
      level: 2,
      label,
      detail,
      pack: pack.name,
      expandable: true,
      expanded: expanded.has(`${key}/${name}`),
    });
    const doc = d.document;
    const sections = [
      section("units", "Units", plural(pack.units.length, "unit")),
      section("templates", "Templates", plural(doc?.files.filter((f) => f.role !== "manifest").length ?? pack.fileCount, "file")),
      section("parameters", "Parameters", doc ? `${doc.parameters.length} · ${doc.parameters.filter((p) => p.value !== null).length} set` : ""),
      section(
        "outputs",
        "Outputs",
        d.outputs?.lastWritten ? `last run ${d.outputs.lastWritten.slice(0, 16).replace("T", " ")}` : d.outputs ? "no run yet" : "",
      ),
    ];
    sections.forEach((s, si) => {
      out.push({ ...s, setsize: sections.length, posinset: si + 1 });
      if (!s.expanded) return;
      if (s.kind === "units")
        children(
          pack.units.map((u) => {
            const raw = u as unknown as RawUnit;
            return {
              key: `${key}/u:${u.id}`,
              kind: "unit" as const,
              level: 3,
              label: unitLine(raw),
              detail: u.mode !== "overwrite" ? modeLabel(u.mode) : "",
              pack: pack.name,
              unit: u.id,
              filter: filterWords(u.where) || undefined,
              expandable: false,
              expanded: false,
            };
          }),
        );
      if (s.kind === "templates" && doc)
        children(
          doc.files
            .filter((f) => f.role !== "manifest")
            .map((f) => ({
              key: `${key}/f:${f.path}`,
              kind: "file" as const,
              level: 3,
              label: f.path,
              detail: [f.role, ...f.usedBy.filter((u) => u.startsWith("unit:")).map((u) => u.slice(5))].join(" · "),
              pack: pack.name,
              path: f.path,
              expandable: false,
              expanded: false,
            })),
        );
      if (s.kind === "parameters" && doc)
        children(
          doc.parameters.map((p) => ({
            key: `${key}/param:${p.name}`,
            kind: "parameter" as const,
            level: 3,
            label: `${p.name} = ${valueText(p.value ?? p.default)}`,
            detail: p.value !== null ? "set" : "default",
            pack: pack.name,
            parameter: p.name,
            expandable: false,
            expanded: false,
            mono: true,
          })),
        );
      if (s.kind === "outputs" && d.outputs) {
        const groups = groupOutputs(
          outputRows(
            d.outputs.outputs,
            pack.units.map((u) => u.id),
            hasElement,
          ),
          "unit",
        );
        groups.forEach((g, gi) => {
          const gkey = `${key}/o:${g.key}`;
          const gopen = expanded.has(gkey);
          out.push({
            key: gkey,
            kind: "output-group",
            level: 3,
            label: g.key,
            detail: groupLine(g),
            pack: pack.name,
            unit: g.key,
            expandable: true,
            expanded: gopen,
            setsize: groups.length,
            posinset: gi + 1,
          });
          if (gopen)
            children(
              g.rows.map((r) => ({
                key: `${gkey}/${r.path}`,
                kind: "output" as const,
                level: 4,
                label: r.path,
                detail: r.display === "clean" ? "" : r.display,
                pack: pack.name,
                unit: r.unit,
                path: r.path,
                state: r.display,
                expandable: false,
                expanded: false,
                mono: true,
              })),
            );
        });
      }
    });
  });
  return out;
}

/** The explorer header: "4 packs · 61 units". */
export function packTotals(packs: S["PackSummary"][]): string {
  return `${plural(packs.length, "pack")} · ${plural(
    packs.reduce((n, p) => n + p.units.length, 0),
    "unit",
  )}`;
}
