// The pack editor (generation-ui.md 3): a centre tab per pack with a header (name and version, description, engine
// range, Enabled, output base, the project's hand-edit policy read-only, diagnostics, Rename pack… and Remove pack…) and the tabs Units,
// Parameters, Templates and Outputs (Alt+1 to Alt+4). Enabled, the output base and parameter values save through
// PUT /api/project/settings/packs/{pack} (maintainer), which writes only packs.<pack> in maquettiste.json; the
// units save pack.json itself. Everything edits the pack's own files: the CLI and git see the same thing.
import { useCallback, useEffect, useMemo, useState } from "react";
import { useQueryClient } from "@tanstack/react-query";
import { FileDiff, RotateCcw, Save, TriangleAlert } from "lucide-react";
import * as endpoints from "@/api/endpoints";
import { keys, useIndex, usePack, usePackOutputs, usePacks, usePlan, useSettings } from "@/api/queries";
import { useServices } from "@/app/context";
import { useEditorNavigation } from "@/app/navigation";
import { Button } from "@/components/ui/button";
import { Checkbox } from "@/components/ui/checkbox";
import { Input, Select, Textarea } from "@/components/ui/input";
import { Badge, EmptyState, Spinner, Toolbar } from "@/components/ui/misc";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs";
import { cn } from "@/lib/cn";
import { useEditor, type PackPane } from "@/state/store";
import { groupOutputs, outputRows, type OutputState } from "./outputsModel";
import { displayValue, packSection, parameterRows, parseParameter, undeclared, withParameter, type ParameterRow } from "./parametersModel";
import { RemovePackButton } from "./RemovePackDialog";
import { RenamePackButton } from "./RenamePackDialog";
import { TemplatesTab } from "./TemplatesTab";
import { UnitsTab } from "./UnitsTab";
import type { PackJson } from "./unitsModel";

const PANES: { value: PackPane; label: string }[] = [
  { value: "units", label: "Units" },
  { value: "parameters", label: "Parameters" },
  { value: "templates", label: "Templates" },
  { value: "outputs", label: "Outputs" },
];

type Section = Record<string, unknown>;

/** Saves `packs.<pack>` with the settings hash; answers true when saved. */
function useSavePackSection(pack: string) {
  const { store } = useServices();
  const qc = useQueryClient();
  const settings = useSettings();
  return useCallback(
    async (section: Section): Promise<boolean> => {
      const hash = settings.data?.hash;
      if (!hash) return false;
      try {
        const result = await endpoints.savePackSettings(pack, section, hash);
        if (result.outcome !== "saved") {
          store.getState().notify(result.diagnostics.map((d) => `${d.rule} ${d.message}`).join("; ") || `Not saved (${result.outcome}).`, "error");
          await qc.invalidateQueries({ queryKey: keys.settings });
          return false;
        }
        await Promise.all([
          qc.invalidateQueries({ queryKey: keys.settings }),
          qc.invalidateQueries({ queryKey: keys.project }),
          qc.invalidateQueries({ queryKey: keys.packs }),
        ]);
        return true;
      } catch (e) {
        store.getState().notify(`Save failed: ${(e as Error).message}`, "error");
        return false;
      }
    },
    [pack, settings.data?.hash, qc, store],
  );
}

function loadedSection(json: unknown, pack: string): Section {
  const packs = (json as { packs?: Record<string, Section> } | undefined)?.packs;
  return { ...(packs?.[pack] ?? {}) };
}

export function PackEditor({ pack }: { pack: string }) {
  const { store } = useServices();
  const { openWorkspace } = useEditorNavigation();
  const doc = usePack(pack);
  const packs = usePacks();
  const settings = useSettings();
  const pane = useEditor(store, (s) => s.generation.packPane[pack] ?? "units");
  const focus = useEditor(store, (s) => (s.generation.packFocus?.pack === pack ? s.generation.packFocus : null));
  const saveSection = useSavePackSection(pack);
  const [unitsDirty, setUnitsDirty] = useState(false);
  const [templatesDirty, setTemplatesDirty] = useState(false);
  const summary = packs.data?.find((p) => p.name === pack);
  const section = loadedSection(settings.data?.json, pack);
  const [output, setOutput] = useState<string | null>(null);
  const setPane = useCallback(
    (value: PackPane) => store.getState().setGeneration({ packPane: { ...store.getState().generation.packPane, [pack]: value } }),
    [store, pack],
  );
  // Alt+1 to Alt+4 switch tabs wherever the focus is while this pack's editor shows (one editor shows at a time).
  useEffect(() => {
    const onKey = (e: globalThis.KeyboardEvent) => {
      const n = Number(e.key.replace("Digit", "")) || Number(e.code.replace("Digit", ""));
      if (!e.altKey || e.ctrlKey || e.metaKey || !(n >= 1 && n <= 4)) return;
      e.preventDefault();
      setPane(PANES[n - 1].value);
    };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [setPane]);

  if (doc.isError) return <EmptyState title={`Pack ${pack} could not be read`}>{(doc.error as Error).message}</EmptyState>;
  if (!doc.data) return <Spinner label={`Loading ${pack}`} />;
  const d = doc.data;
  const json = (d.document ?? {}) as PackJson;
  const diagnostics = d.diagnostics.length + (summary?.diagnostics.length ?? 0);
  const handEdits = settings.data?.settings.handEdits;
  return (
    <div className="flex h-full min-h-0 flex-col" data-testid="pack-editor">
      <header className="flex shrink-0 flex-wrap items-center gap-x-2 gap-y-0.5 border-b border-default bg-surface px-2 py-1 text-12">
        <h2 className="text-13 font-semibold" data-testid="pack-editor-title">
          {pack} <span className="font-normal text-secondary">{typeof json.version === "string" ? json.version : ""}</span>
        </h2>
        <span className="text-secondary">engine {typeof json.engine === "string" ? json.engine : "?"}</span>
        <label className="flex items-center gap-1">
          <Checkbox
            checked={d.enabled}
            aria-label="Enabled"
            onCheckedChange={(v) => void saveSection({ ...section, enabled: v === true })}
            data-testid="pack-enabled"
          />
          Enabled
        </label>
        <label className="flex items-center gap-1">
          Output base
          <Input
            className="h-6 w-40 font-mono text-11"
            value={output ?? d.output}
            onChange={(e) => setOutput(e.target.value)}
            onBlur={() => {
              if (output === null || output === d.output) return;
              void saveSection({ ...section, output }).then(() => setOutput(null));
            }}
            onKeyDown={(e) => e.key === "Enter" && (e.target as HTMLInputElement).blur()}
            aria-label="Output base"
          />
        </label>
        <span className="text-secondary">
          Hand edits: {handEdits ?? "refuse"} (project-wide,{" "}
          <button type="button" className="text-accent underline-offset-2 hover:underline" onClick={() => openWorkspace("settings")}>
            Settings
          </button>
          )
        </span>
        {diagnostics ? (
          <Badge tone="warning" title={[...d.diagnostics, ...(summary?.diagnostics ?? [])].map((x) => `${x.rule} ${x.message}`).join("\n")}>
            <TriangleAlert className="size-3" aria-hidden /> {diagnostics}
          </Badge>
        ) : null}
        <span className="ml-auto flex items-center gap-1">
          <RenamePackButton pack={pack} hash={d.hash} />
          <RemovePackButton pack={pack} hash={d.hash} fileCount={d.files.length} outputBase={d.output} />
        </span>
        {typeof json.description === "string" ? (
          <p className="w-full truncate text-secondary" title={json.description}>
            {json.description}
          </p>
        ) : null}
      </header>
      <Tabs value={pane} onValueChange={(v) => setPane(v as PackPane)} className="flex min-h-0 flex-1 flex-col">
        <TabsList aria-label={`${pack} editor`}>
          {PANES.map((p, i) => (
            <TabsTrigger key={p.value} value={p.value} title={`Alt+${i + 1}`}>
              {p.label}
              {(p.value === "units" && unitsDirty) || (p.value === "templates" && templatesDirty) ? <span aria-label="unsaved">•</span> : null}
            </TabsTrigger>
          ))}
        </TabsList>
        <TabsContent value="units" className="min-h-0 flex-1" forceMount hidden={pane !== "units"}>
          <UnitsTab
            key={pack}
            pack={pack}
            document={json}
            hash={d.hash}
            files={d.files.filter((f) => f.role !== "manifest").map((f) => f.path)}
            focusUnit={focus?.unit}
            onDirty={setUnitsDirty}
          />
        </TabsContent>
        <TabsContent value="parameters" className="min-h-0 flex-1 overflow-auto">
          <ParametersTab pack={pack} parameters={d.parameters} section={section} focus={focus?.parameter} save={saveSection} />
        </TabsContent>
        <TabsContent value="templates" className="min-h-0 flex-1" forceMount hidden={pane !== "templates"}>
          <TemplatesTab
            key={pack}
            pack={pack}
            packHash={d.hash}
            files={d.files}
            units={((json.units as { id?: string }[] | undefined) ?? []).map((u) => u.id ?? "").filter(Boolean)}
            scopes={Object.fromEntries(
              ((json.units as { id?: string; for?: string }[] | undefined) ?? []).filter((u) => u.id).map((u) => [u.id!, u.for ?? ""]),
            )}
            focusFile={focus?.file}
            onDirty={setTemplatesDirty}
          />
        </TabsContent>
        <TabsContent value="outputs" className="min-h-0 flex-1">
          <OutputsTab pack={pack} unitIds={((json.units as { id?: string }[] | undefined) ?? []).map((u) => u.id ?? "")} />
        </TabsContent>
      </Tabs>
    </div>
  );
}

function ParametersTab({
  pack,
  parameters,
  section,
  focus,
  save,
}: {
  pack: string;
  parameters: Parameters<typeof parameterRows>[0];
  section: Section;
  focus?: string;
  save(section: Section): Promise<boolean>;
}) {
  const loaded = useMemo(() => (section.parameters as Record<string, unknown> | undefined) ?? {}, [section.parameters]);
  const [values, setValues] = useState<Record<string, unknown>>(loaded);
  const [errors, setErrors] = useState<Record<string, string>>({});
  const loadedText = JSON.stringify(loaded);
  useEffect(() => setValues(JSON.parse(loadedText) as Record<string, unknown>), [loadedText]);
  const rows = parameterRows(parameters, values);
  const extra = undeclared(parameters, values);
  const dirty = JSON.stringify(values) !== loadedText;
  useEffect(() => {
    if (focus) document.getElementById(`param-${pack}-${focus}`)?.focus();
  }, [focus, pack]);
  const edit = (row: ParameterRow, text: string | boolean) => {
    const parsed = parseParameter(row, text);
    if (!parsed.ok) {
      setErrors({ ...errors, [row.name]: parsed.error });
      return;
    }
    const { [row.name]: _gone, ...rest } = errors;
    void _gone;
    setErrors(rest);
    setValues(withParameter(values, row.name, parsed.value));
  };
  return (
    <div className="flex flex-col" data-testid="parameters-tab">
      <Toolbar label="Parameters">
        <span className="text-12 text-secondary">
          Values for this project (packs.{pack}.parameters in maquettiste.json); the templates read them as pack.params.
        </span>
        <span className="ml-auto" />
        {dirty ? <Badge tone="warning">unsaved</Badge> : null}
        <Button
          size="sm"
          variant="primary"
          disabled={!dirty || Object.keys(errors).length > 0}
          onClick={() => void save(packSection(section, values))}
          data-testid="parameters-save"
        >
          <Save className="size-3.5" /> Save
        </Button>
      </Toolbar>
      {!rows.length && !extra.length ? <EmptyState title="No parameters">This pack declares no parameters.</EmptyState> : null}
      <table className="w-full border-collapse text-12" aria-label="Parameters">
        <thead className="text-left text-11 text-secondary">
          <tr className="h-6 border-b border-default">
            <th className="px-1 font-medium">Parameter</th>
            <th className="px-1 font-medium">Value</th>
            <th className="px-1 font-medium">Default</th>
            <th className="px-1 font-medium" />
          </tr>
        </thead>
        <tbody>
          {rows.map((row) => (
            <tr key={row.name} className="border-b border-default align-top" data-testid={`param-row-${row.name}`}>
              <td className="px-1 py-0.5">
                <div className="font-mono">{row.title !== row.name ? `${row.title} (${row.name})` : row.name}</div>
                {row.description ? <div className="text-11 text-secondary">{row.description}</div> : null}
              </td>
              <td className="px-1 py-0.5">
                <ParameterControl pack={pack} row={row} onEdit={(t) => edit(row, t)} />
                {errors[row.name] ? (
                  <div role="alert" className="text-11 text-danger">
                    {errors[row.name]}
                  </div>
                ) : null}
              </td>
              <td className="max-w-60 truncate px-1 py-0.5 font-mono text-11 text-secondary" title={JSON.stringify(row.defaultValue)}>
                {JSON.stringify(row.defaultValue)}
              </td>
              <td className="whitespace-nowrap px-1 py-0.5">
                {row.set ? (
                  <Button
                    size="sm"
                    variant="ghost"
                    onClick={() => setValues(withParameter(values, row.name, undefined))}
                    aria-label={`Reset ${row.name} to default`}
                  >
                    <RotateCcw className="size-3.5" /> Reset to default
                  </Button>
                ) : (
                  <span className="text-11 text-secondary">default</span>
                )}
              </td>
            </tr>
          ))}
          {extra.map((name) => (
            <tr key={name} className="border-b border-default" data-testid={`param-row-${name}`}>
              <td className="px-1 py-0.5 font-mono">{name}</td>
              <td className="px-1 py-0.5 text-warning" colSpan={2}>
                MQ6024 The pack does not declare this parameter.
              </td>
              <td className="px-1 py-0.5">
                <Button size="sm" variant="ghost" onClick={() => setValues(withParameter(values, name, undefined))}>
                  Remove
                </Button>
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

function ParameterControl({ pack, row, onEdit }: { pack: string; row: ParameterRow; onEdit(text: string | boolean): void }) {
  const id = `param-${pack}-${row.name}`;
  const shown = row.set ? row.value : row.defaultValue;
  const [text, setText] = useState(displayValue(row.control, shown));
  const shownText = displayValue(row.control, shown);
  useEffect(() => setText(shownText), [shownText]);
  if (row.control === "switch") return <Checkbox id={id} aria-label={row.name} checked={shown === true} onCheckedChange={(v) => onEdit(v === true)} />;
  if (row.control === "select")
    return (
      <Select id={id} aria-label={row.name} className="h-6 w-56 text-12" value={String(shown ?? "")} onChange={(e) => onEdit(e.target.value)}>
        {row.options.map((o) => (
          <option key={o} value={o}>
            {o}
          </option>
        ))}
      </Select>
    );
  if (row.control === "json")
    return (
      <Textarea
        id={id}
        aria-label={row.name}
        className="min-h-12 w-80 font-mono text-11"
        rows={Math.min(8, text.split("\n").length)}
        value={text}
        onChange={(e) => setText(e.target.value)}
        onBlur={() => onEdit(text)}
      />
    );
  return (
    <Input
      id={id}
      aria-label={row.name}
      type={row.control === "number" ? "number" : "text"}
      className="h-6 w-56 text-12"
      value={text}
      onChange={(e) => setText(e.target.value)}
      onBlur={() => text !== shownText && onEdit(text)}
    />
  );
}

const STATES: OutputState[] = ["clean", "hand-edited", "missing", "orphan"];

function OutputsTab({ pack, unitIds }: { pack: string; unitIds: string[] }) {
  const { store } = useServices();
  const outputs = usePackOutputs(pack);
  const index = useIndex();
  const planId = useEditor(store, (s) => s.generation.planId);
  const plan = usePlan(planId);
  const [by, setBy] = useState<"unit" | "root">("unit");
  const [state, setState] = useState<"" | OutputState>("");
  const [root, setRoot] = useState("");
  const [filter, setFilter] = useState("");
  const ids = useMemo(() => new Set((index.data ?? []).map((e) => e.id)), [index.data]);
  const inPlan = useMemo(() => new Set((plan.data?.changes ?? []).map((c) => c.path)), [plan.data]);
  const rows = useMemo(() => outputRows(outputs.data?.outputs ?? [], unitIds, (id) => !index.data || ids.has(id)), [outputs.data, unitIds, ids, index.data]);
  const roots = [...new Set(rows.map((r) => r.root ?? ""))].sort();
  const shown = rows.filter(
    (r) => (!state || r.display === state) && (!root || r.root === root) && (!filter || r.path.toLowerCase().includes(filter.toLowerCase())),
  );
  const groups = groupOutputs(shown, by);
  if (outputs.isPending) return <Spinner label="Reading the manifest" />;
  return (
    <div className="flex h-full min-h-0 flex-col" data-testid="outputs-tab">
      <Toolbar label="Outputs">
        <span className="text-12 text-secondary">
          {rows.length} files · last run {outputs.data?.lastWritten ? outputs.data.lastWritten.slice(0, 16).replace("T", " ") : "never"}
        </span>
        <Select aria-label="Group by" className="h-6 w-28 text-12" value={by} onChange={(e) => setBy(e.target.value as "unit" | "root")}>
          <option value="unit">By unit</option>
          <option value="root">By root</option>
        </Select>
        <Select aria-label="State" className="h-6 w-32 text-12" value={state} onChange={(e) => setState(e.target.value as OutputState | "")}>
          <option value="">Every state</option>
          {STATES.map((s) => (
            <option key={s} value={s}>
              {s}
            </option>
          ))}
        </Select>
        <Select aria-label="Root" className="h-6 w-32 text-12" value={root} onChange={(e) => setRoot(e.target.value)}>
          <option value="">Every root</option>
          {roots.map((r) => (
            <option key={r} value={r}>
              {r}
            </option>
          ))}
        </Select>
        <Input aria-label="Path filter" placeholder="Filter paths" className="h-6 w-48 text-12" value={filter} onChange={(e) => setFilter(e.target.value)} />
      </Toolbar>
      {!rows.length ? (
        <EmptyState title="No files yet">This pack has not written anything. Plan and apply on the Plan tab; the manifest records every file.</EmptyState>
      ) : (
        <div className="min-h-0 flex-1 overflow-auto">
          <table className="w-full border-collapse text-12" aria-label="Outputs">
            <tbody>
              {groups.map((g) => (
                <GroupRows key={g.key} group={g} inPlan={inPlan} onDiff={(path) => planId && store.getState().showDiff({ planId, path })} />
              ))}
            </tbody>
          </table>
        </div>
      )}
    </div>
  );
}

function GroupRows({ group, inPlan, onDiff }: { group: ReturnType<typeof groupOutputs>[number]; inPlan: Set<string>; onDiff(path: string): void }) {
  return (
    <>
      <tr className="h-6 border-b border-default bg-app">
        <th colSpan={4} className="px-1 text-left text-12 font-semibold">
          {group.key} <span className="font-normal text-secondary">· {group.rows.length} files</span>
        </th>
      </tr>
      {group.rows.map((r) => (
        <tr key={r.path} className="h-6 border-b border-default" data-testid={`output-row-${r.path}`}>
          <td className="px-1 pl-4 font-mono text-11">{r.path}</td>
          <td className="px-1">
            <span className={cn("text-11", r.display === "clean" ? "text-secondary" : "text-warning")}>{r.display}</span>
          </td>
          <td className="px-1 text-11 text-secondary">
            {r.mode === "once" ? "Create only if missing" : r.mode === "regions" ? "Protected regions" : "Overwrite"}
          </td>
          <td className="px-1 text-right">
            <Button
              size="sm"
              variant="ghost"
              disabled={!inPlan.has(r.path)}
              title={inPlan.has(r.path) ? "Open this file's diff in the current plan" : "No current plan includes this file; plan first to see its diff."}
              onClick={() => onDiff(r.path)}
            >
              <FileDiff className="size-3.5" /> Diff
            </Button>
          </td>
        </tr>
      ))}
    </>
  );
}
