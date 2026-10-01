// The rest of pack authoring for the mock backend (generation-ui.md sections 4.3 and 5.1): unit paths, template context,
// explain, pack outputs and the pack settings save. The mock does not run Scriban: paths come from the mock renderer
// (generation.renderUnits), or from a constant unsaved output pattern, which is what MQ6020 needs to show.
import type { components } from "@/api/schema";
import type { MockGeneration } from "./generation";
import type { MockModel } from "./store";

type S = components["schemas"];
type Diagnostic = S["Diagnostic"];

function diagnostic(rule: string, message: string, filePath: string | null): Diagnostic {
  return { rule, severity: "error", message, elementId: null, filePath, jsonPointer: null, line: null, column: null };
}

const HELPERS = [
  "camel",
  "date",
  "include",
  "iso_duration_ms",
  "json",
  "kebab",
  "lower",
  "pascal",
  "plural",
  "quote_ident",
  "singular",
  "snake",
  "state_path",
  "upper",
];
const ELEMENT_MEMBERS: S["TemplateMember"][] = [
  { name: "attributes", type: "list" },
  { name: "description", type: "string" },
  { name: "id", type: "string" },
  { name: "kind", type: "string" },
  { name: "name", type: "string" },
  { name: "tags", type: "list" },
];
const TABLE_MEMBERS: S["TemplateMember"][] = [
  { name: "columns", type: "list" },
  { name: "database", type: "object" },
  { name: "key", type: "string" },
  { name: "name", type: "string" },
  { name: "primary_key", type: "object" },
  { name: "schema", type: "string" },
];
// The members a view and a sequence share with every annotated object, then their own, ordinal as the engine lists them.
const VIEW_MEMBERS: S["TemplateMember"][] = [
  { name: "body", type: "string" },
  { name: "columns", type: "list" },
  { name: "comment", type: "string" },
  { name: "database", type: "object" },
  { name: "description", type: "string" },
  { name: "display_name", type: "string" },
  { name: "id", type: "string" },
  { name: "kind", type: "string" },
  { name: "name", type: "string" },
  { name: "properties", type: "map" },
  { name: "schema", type: "string" },
  { name: "stereotypes", type: "list" },
  { name: "tags", type: "list" },
];
const SEQUENCE_MEMBERS: S["TemplateMember"][] = [
  { name: "cache", type: "number" },
  { name: "cycle", type: "boolean" },
  { name: "database", type: "object" },
  { name: "description", type: "string" },
  { name: "display_name", type: "string" },
  { name: "id", type: "string" },
  { name: "increment", type: "number" },
  { name: "kind", type: "string" },
  { name: "name", type: "string" },
  { name: "native_type", type: "string" },
  { name: "properties", type: "map" },
  { name: "schema", type: "string" },
  { name: "start", type: "number" },
  { name: "stereotypes", type: "list" },
  { name: "tags", type: "list" },
  { name: "type", type: "string" },
];
// The members of the process, actor and scenario records (phase-3-design.md 4.3), ordinal as the engine lists them.
const PROCESS_MEMBERS: S["TemplateMember"][] = [
  { name: "actions", type: "list" },
  { name: "actors", type: "list" },
  { name: "all_states", type: "list" },
  { name: "atomic_states", type: "list" },
  { name: "bound_attribute", type: "object" },
  { name: "bound_enum", type: "object" },
  { name: "bound_states", type: "list" },
  { name: "context", type: "list" },
  { name: "description", type: "string" },
  { name: "display_name", type: "string" },
  { name: "events", type: "list" },
  { name: "gates", type: "list" },
  { name: "guards", type: "list" },
  { name: "id", type: "string" },
  { name: "initial", type: "object" },
  { name: "invokes", type: "list" },
  { name: "kind", type: "string" },
  { name: "name", type: "string" },
  { name: "package", type: "object" },
  { name: "scenarios", type: "list" },
  { name: "states", type: "list" },
  { name: "subject", type: "object" },
  { name: "tags", type: "list" },
  { name: "transitions", type: "list" },
  { name: "use", type: "string" },
];
const ACTOR_MEMBERS: S["TemplateMember"][] = [
  { name: "description", type: "string" },
  { name: "display_name", type: "string" },
  { name: "events", type: "list" },
  { name: "gates", type: "list" },
  { name: "id", type: "string" },
  { name: "kind", type: "string" },
  { name: "name", type: "string" },
  { name: "processes", type: "list" },
  { name: "stereotypes", type: "list" },
  { name: "type", type: "string" },
];
const SCENARIO_MEMBERS: S["TemplateMember"][] = [
  { name: "description", type: "string" },
  { name: "display_name", type: "string" },
  { name: "id", type: "string" },
  { name: "kind", type: "string" },
  { name: "name", type: "string" },
  { name: "outcome", type: "string" },
  { name: "package", type: "object" },
  { name: "process", type: "object" },
  { name: "start", type: "object" },
  { name: "steps", type: "list" },
];
const SCOPE_MEMBERS: Record<string, S["TemplateMember"][]> = {
  "each table": TABLE_MEMBERS,
  "each view": VIEW_MEMBERS,
  "each sequence": SEQUENCE_MEMBERS,
  "each process": PROCESS_MEMBERS,
  "each actor": ACTOR_MEMBERS,
  "each scenario": SCENARIO_MEMBERS,
};
const MODEL_MEMBERS: S["TemplateMember"][] = [
  { name: "actors", type: "list" },
  { name: "databases", type: "list" },
  { name: "entities", type: "list" },
  { name: "enums", type: "list" },
  { name: "packages", type: "list" },
  { name: "processes", type: "list" },
  { name: "relations", type: "list" },
  { name: "scenarios", type: "list" },
  { name: "settings", type: "object" },
  { name: "value_objects", type: "list" },
];

export class MockPackAuthoring {
  constructor(
    private readonly model: MockModel,
    private readonly generation: MockGeneration,
    private readonly registrations: (pack: string) => S["ScriptRegistration"][] = () => [],
  ) {}

  private unit(pack: string, unit: string): S["PackUnit"] | null {
    return this.model.packs.find((p) => p.name === pack)?.units.find((u) => u.id === unit) ?? null;
  }

  paths(request: S["PathsRequest"]): S["UnitPathsResult"] | { problem: string } {
    const { pack, unit, elementIds, limit, unitOverride } = request;
    if (unitOverride && unitOverride.id !== unit) return { problem: "unitOverride.id must equal unit." };
    const saved = this.unit(pack, unit);
    if (!saved && !unitOverride) return { problem: `Pack '${pack}' has no unit '${unit}'.` };
    let units = this.generation.renderUnits([pack]).filter((u) => u.unit === unit);
    if (elementIds?.length) units = units.filter((u) => u.elementId !== null && elementIds.includes(u.elementId));
    const take = Math.min(Math.max(limit ?? 200, 1), 2000);
    const output = unitOverride?.output ?? null;
    const constant = output !== null && output !== undefined && !output.includes("{{");
    const base = this.model.projectSettings().packs[pack]?.output || (pack === "sql-ddl" ? "db" : "src/Generated");
    const paths: S["UnitPath"][] = units.slice(0, take).map((u) => ({
      elementId: u.elementId,
      path: constant ? `${base}/${output}` : u.files[0].path,
      role: "main",
      root: base.split("/")[0],
      allowed: true,
      rule: null,
      ...this.generation.elementLabel(u.elementId),
    }));
    const diagnostics: Diagnostic[] = [];
    if (constant && paths.length > 1)
      diagnostics.push(
        diagnostic(
          "MQ6020",
          `Unit '${pack}/${unit}' renders ${paths[0].path} for '${paths[0].elementId}' and '${paths[1].elementId}': its output pattern is not unique per element.`,
          paths[0].path,
        ),
      );
    paths.sort((a, b) => (a.elementId ?? "").localeCompare(b.elementId ?? "") || a.path.localeCompare(b.path));
    return { count: units.length, rendered: paths.length, paths, diagnostics, elapsedMs: 4 };
  }

  context(pack: string, unitId: string): S["TemplateContextResult"] | null {
    const unit = this.unit(pack, unitId);
    if (!unit) return null;
    const manifest = this.model.packs.find((p) => p.name === pack)!;
    const variables: S["TemplateVariable"][] = [
      { name: "data", detail: "The pack's transforms' results." },
      { name: "hints", detail: "The element's generation hints for this pack." },
      { name: "mapping", detail: "The element's mapping to a database, when it has one." },
      { name: "mappings", detail: "Every mapping of the element." },
      { name: "model", detail: "The resolved model." },
      { name: "pack", detail: "The pack: name, version and params." },
      { name: "schema_diff", detail: "The schema diffs by database name." },
      { name: "unit", detail: "The unit: id and key." },
      ...Object.keys((manifest.parameters as Record<string, unknown> | undefined) ?? {}).map((name) => ({
        name: `pack.params.${name}`,
        detail: `Parameter ${name}.`,
      })),
    ];
    if (unit.for !== "model") {
      variables.push({ name: "element", detail: "The element this unit renders for." });
      if (unit.for.startsWith("each ")) variables.push({ name: unit.for.slice(5).replace(/ /g, "_"), detail: "The element, by its kind." });
    }
    variables.sort((a, b) => (a.name < b.name ? -1 : a.name > b.name ? 1 : 0));
    const members: Record<string, S["TemplateMember"][]> = { model: MODEL_MEMBERS };
    if (unit.for.startsWith("each ")) members.element = SCOPE_MEMBERS[unit.for] ?? ELEMENT_MEMBERS;
    const registrations = this.registrations(pack);
    const helpers = [...new Set([...HELPERS, ...registrations.filter((r) => r.kind === "helper").map((r) => r.name)])].sort();
    return { pack, unit: unitId, scope: unit.for, variables, members, helpers, registrations };
  }

  explain(request: S["ExplainRequest"]): S["ExplainResult"] | null {
    const { pack, unit, elementId = null, planId = null } = request;
    const manifest = this.model.packs.find((p) => p.name === pack);
    if (!manifest) return null;
    const key = elementId ? `${pack}/${unit}:${elementId}` : `${pack}/${unit}`;
    const answer = (reason: S["ExplainResult"]["reason"], detail: string): S["ExplainResult"] => ({
      pack,
      unit,
      elementId,
      planned: false,
      reason,
      detail,
      key,
      planId: null,
      planUnit: null,
      diagnostics: [],
    });
    const found = manifest.units.find((u) => u.id === unit);
    if (!found) return answer("unknown-unit", `Pack '${pack}' has no unit '${unit}'.`);
    if (!this.generation.enabledPacks().includes(pack))
      return answer("pack-disabled", `Pack '${pack}' is disabled (packs.${pack}.enabled is false in the project settings), so none of its units run.`);
    if (request.packs?.length && !request.packs.includes(pack))
      return answer("not-selected", `Pack '${pack}' is not in this run's selection (${[...request.packs].sort().join(", ")}).`);
    const planned = this.generation.renderUnits([pack]).find((u) => u.unit === unit && u.elementId === elementId);
    if (planned) {
      const stored = planId ? this.generation.getPlan(planId, true)?.units.find((u) => u.key === key) : undefined;
      return {
        ...answer(
          stored?.reason ?? "new",
          stored?.skipped
            ? `Skipped: its ${stored.readKeys.length} recorded inputs are unchanged since its last render, and its ${stored.outputs.length} outputs are intact.`
            : (stored?.causes[0]?.detail ?? "No recorded state to compare with; the unit renders."),
        ),
        planned: true,
        planId: stored ? planId : null,
        planUnit: stored ?? null,
      };
    }
    const tableKey = elementId?.includes("@");
    if (elementId && !tableKey && !this.model.docs().has(elementId))
      return answer("unknown-element", `'${elementId}' is not in the resolved model (deleted, renamed or never defined).`);
    return answer(
      "scope",
      elementId ? `Unit '${unit}' is '${found.for}', which does not cover '${elementId}'.` : `Unit '${unit}' is '${found.for}': name an element.`,
    );
  }

  outputs(pack: string): S["PackOutputs"] | null {
    if (!this.model.packs.some((p) => p.name === pack)) return null;
    const units = new Map(this.generation.renderUnits([pack]).flatMap((u) => u.files.map((f) => [f.path, u] as const)));
    const outputs: S["PackOutput"][] = [...this.generation.manifest]
      .filter(([, entry]) => entry.pack === pack)
      .map(([path]): S["PackOutput"] => {
        const unit = units.get(path);
        return {
          path,
          unit: unit?.unit ?? "unknown",
          elementId: unit?.elementId ?? null,
          companion: false,
          root: path.split("/")[0],
          commit: true,
          mode: "overwrite",
          state: "intact",
        };
      })
      .sort((a, b) => (a.path < b.path ? -1 : 1));
    return { pack, outputs, lastWritten: outputs.length ? "2026-09-29T12:00:00Z" : null };
  }

  /** Replaces packs.<pack> of maquettiste.json through the settings save (same ETag, same 409). */
  saveSettings(pack: string, section: Record<string, unknown>, expectedHash: string) {
    const json = structuredClone(this.model.settingsDocument().json) as Record<string, unknown>;
    const packs = { ...((json.packs as Record<string, unknown> | undefined) ?? {}) };
    if (Object.keys(section).length) packs[pack] = section;
    else delete packs[pack];
    json.packs = packs;
    return this.model.saveSettings(json, expectedHash);
  }
}
