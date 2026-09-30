// The Units tab of the pack editor (generation-ui.md 3.1): one row per unit, edited in place (id, scope, filter,
// template, output pattern, write mode, formatter), with the Files choice in words, the path summary, and the
// output path computed for an example element from POST /api/templates/paths (debounced 250 ms, against the
// unsaved row). The side panel explains the focused field and the row's scope in plain language. Save sends the
// whole pack.json document with If-Match; the grid never drops a member it does not show.
import { useEffect, useMemo, useRef, useState, type KeyboardEvent } from "react";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { ArrowDown, ArrowUp, Copy, Filter, Plus, Save, Trash2, Undo2 } from "lucide-react";
import * as endpoints from "@/api/endpoints";
import { keys, useIndex, usePackOutputs } from "@/api/queries";
import { Dialog, DialogContent } from "@/components/ui/dialog";
import { useServices } from "@/app/context";
import { Button } from "@/components/ui/button";
import { Input, Select } from "@/components/ui/input";
import { Badge, Toolbar } from "@/components/ui/misc";
import { Popover, PopoverContent, PopoverTrigger } from "@/components/ui/popover";
import { cn } from "@/lib/cn";
import { markUnsaved, useDraftState } from "./drafts";
import { pathSummary } from "./pathSummary";
import {
  EACH_KINDS,
  FIELD_HELP,
  WRITE_MODES,
  duplicateUnit,
  examplePath,
  filesLabel,
  filterWords,
  insertUnit,
  moveUnit,
  removeUnit,
  sameDocument,
  scopeHelp,
  setUnitField,
  toPackUnit,
  unitIdError,
  unitsOf,
  type PackJson,
  type RawUnit,
  type UnitField,
} from "./unitsModel";

const str = (v: unknown) => (typeof v === "string" ? v : "");
const SCOPES = ["model", ...EACH_KINDS.map((k) => `each ${k}`)];

function useDebounced<T>(value: T, ms: number): T {
  const [out, setOut] = useState(value);
  useEffect(() => {
    const t = setTimeout(() => setOut(value), ms);
    return () => clearTimeout(t);
  }, [value, ms]);
  return out;
}

/** The unit's planned paths, against the unsaved row when it differs from the saved one. */
function useUnitPaths(pack: string, raw: RawUnit, saved: RawUnit | undefined) {
  const text = useDebounced(JSON.stringify(raw), 250);
  const changed = !saved || JSON.stringify(saved) !== text;
  const unit = JSON.parse(text) as RawUnit;
  const id = str(unit.id);
  return useQuery({
    queryKey: [...keys.pack(pack), "paths", id, changed ? text : "saved"],
    queryFn: ({ signal }) => endpoints.unitPaths({ pack, unit: id, limit: 200, unitOverride: changed ? toPackUnit(unit) : null }, signal),
    enabled: /^[a-z][a-z0-9-]*$/.test(id) && !!str(unit.for),
    retry: false,
  });
}

interface Props {
  pack: string;
  document: PackJson;
  hash: string;
  files: string[];
  focusUnit?: string;
  onDirty(dirty: boolean): void;
}

export function UnitsTab({ pack, document, hash, files, focusUnit, onDirty }: Props) {
  const { store } = useServices();
  const qc = useQueryClient();
  const [draft, setDraft] = useDraftState<PackJson>(`${pack}:units:draft`, document);
  // The document the draft was loaded from (or last saved as) and the disk hash it corresponds to; `base` is the
  // If-Match target of the next save.
  const [loaded, setLoaded] = useDraftState<{ doc: PackJson; hash: string }>(`${pack}:units:loaded`, { doc: document, hash });
  const [base, setBase] = useDraftState(`${pack}:units:base`, hash);
  const [row, setRow] = useState(() =>
    Math.max(
      0,
      unitsOf(document).findIndex((u) => u.id === focusUnit),
    ),
  );
  const [field, setField] = useState<UnitField>("output");
  const [examples, setExamples] = useState<Record<string, string>>({});
  const [conflict, setConflict] = useState(false);
  const [busy, setBusy] = useState(false);
  const dirty = !sameDocument(draft, loaded.doc);
  // A reload from disk (another tab, the CLI, git, or our own save coming back canonical) replaces an unmodified
  // grid; a modified grid keeps its draft and its base, so its next save gets the 409 bar.
  useEffect(() => {
    if (hash !== loaded.hash && !dirty) {
      setDraft(document);
      setLoaded({ doc: document, hash });
      setBase(hash);
    }
  }, [hash, loaded.hash, dirty, document, setDraft, setLoaded, setBase]);
  useEffect(() => {
    markUnsaved(`${pack}:units`, dirty);
    onDirty(dirty);
  }, [pack, dirty, onDirty]);
  useEffect(() => {
    const at = unitsOf(document).findIndex((u) => u.id === focusUnit);
    if (at >= 0) setRow(at);
  }, [focusUnit, document]);

  const units = unitsOf(draft);
  const saved = useMemo(() => new Map(unitsOf(document).map((u) => [str(u.id), u])), [document]);
  const current = units[Math.min(row, units.length - 1)];
  // A row the user focuses in the grid drives the Generate inspector as a pack tree row does (packFocus); the row
  // the grid opens on does not, so opening the Units tab leaves the inspector on the pack.
  const currentId = current ? str(current.id) : null;
  const shownId = useRef(currentId);
  useEffect(() => {
    if (shownId.current === currentId) return;
    shownId.current = currentId;
    if (!currentId || !saved.has(currentId)) return;
    const focus = store.getState().generation.packFocus;
    if (focus?.pack === pack && focus.unit === currentId) return;
    store.getState().setGeneration({ packFocus: { pack, unit: currentId } });
  }, [currentId, saved, pack, store]);

  const save = async (overwrite = false) => {
    if (units.some((u, i) => unitIdError(draft, i, str(u.id)))) {
      store.getState().notify("Fix the unit ids before saving.", "error");
      return;
    }
    setBusy(true);
    try {
      const target = overwrite ? hash : base;
      const result = await endpoints.savePack(pack, draft, target);
      if (result.outcome === "conflict") {
        setConflict(true);
        await qc.invalidateQueries({ queryKey: keys.pack(pack) });
        return;
      }
      if (result.outcome !== "saved") {
        store.getState().notify(result.diagnostics.map((d) => `${d.rule} ${d.message}`).join("; ") || `Not saved (${result.outcome}).`, "error");
        return;
      }
      setConflict(false);
      // The draft is now what is on disk; the refetch replaces it with the server's canonical form.
      setLoaded({ doc: draft, hash });
      if (result.hash) setBase(result.hash);
      await qc.invalidateQueries({ queryKey: keys.packs });
      store.getState().notify(`Saved ${pack}/pack.json`, "info");
    } catch (e) {
      store.getState().notify(`Save failed: ${(e as Error).message}`, "error");
    } finally {
      setBusy(false);
    }
  };

  const apply = (next: { doc: PackJson; index: number }) => {
    setDraft(next.doc);
    setRow(next.index);
  };
  // Removing a unit asks first, naming the files it owns (its manifest entries), which become orphans.
  const [removing, setRemoving] = useState<number | null>(null);
  const outputs = usePackOutputs(pack, removing !== null);
  const owned = removing === null ? [] : (outputs.data?.outputs ?? []).filter((o) => o.unit === str(units[removing]?.id)).map((o) => o.path);
  const remove = () => {
    if (current) setRemoving(row);
  };
  const confirmRemove = () => {
    if (removing === null) return;
    setDraft(removeUnit(draft, removing));
    setRow(Math.max(0, removing - 1));
    setRemoving(null);
  };
  const onKeyDown = (e: KeyboardEvent) => {
    const mod = e.ctrlKey || e.metaKey;
    if (mod && e.key.toLowerCase() === "s") {
      e.preventDefault();
      void save();
    } else if (mod && e.key === "Enter") {
      e.preventDefault();
      apply(insertUnit(draft, row));
    } else if (mod && e.key.toLowerCase() === "d" && current) {
      e.preventDefault();
      apply(duplicateUnit(draft, row));
    } else if (mod && e.key === "Delete") {
      e.preventDefault();
      remove();
    } else if (e.altKey && (e.key === "ArrowUp" || e.key === "ArrowDown")) {
      e.preventDefault();
      apply(moveUnit(draft, row, e.key === "ArrowUp" ? -1 : 1));
    }
  };

  const scope = str(current?.for);
  return (
    <div className="flex h-full min-h-0 flex-col" onKeyDown={onKeyDown}>
      <Toolbar label="Units">
        <Button size="sm" onClick={() => apply(insertUnit(draft, row))} data-testid="unit-add">
          <Plus className="size-3.5" /> Unit
        </Button>
        <Button size="sm" variant="ghost" label="Duplicate unit" shortcut="Ctrl+D" disabled={!current} onClick={() => apply(duplicateUnit(draft, row))}>
          <Copy className="size-3.5" />
        </Button>
        <Button size="sm" variant="ghost" label="Move unit up" shortcut="Alt+Up" disabled={row === 0} onClick={() => apply(moveUnit(draft, row, -1))}>
          <ArrowUp className="size-3.5" />
        </Button>
        <Button
          size="sm"
          variant="ghost"
          label="Move unit down"
          shortcut="Alt+Down"
          disabled={row >= units.length - 1}
          onClick={() => apply(moveUnit(draft, row, 1))}
        >
          <ArrowDown className="size-3.5" />
        </Button>
        <Button size="sm" variant="ghost" label="Remove unit" shortcut="Ctrl+Delete" disabled={!current} onClick={remove}>
          <Trash2 className="size-3.5" />
        </Button>
        <label className="ml-2 flex items-center gap-1 text-12 text-secondary" htmlFor="example-element">
          Example element
        </label>
        <ExamplePicker
          pack={pack}
          unit={current}
          saved={current ? saved.get(str(current.id)) : undefined}
          value={examples[str(current?.id)] ?? ""}
          onChange={(id) => setExamples({ ...examples, [str(current?.id)]: id })}
        />
        <span className="ml-auto" />
        {dirty ? <Badge tone="warning">unsaved</Badge> : null}
        <Button size="sm" variant="ghost" disabled={!dirty} onClick={() => setDraft(loaded.doc)} label="Revert units">
          <Undo2 className="size-3.5" />
        </Button>
        <Button size="sm" variant="primary" disabled={!dirty || busy} onClick={() => void save()} data-testid="units-save">
          <Save className="size-3.5" /> Save
        </Button>
      </Toolbar>
      <Dialog open={removing !== null} onOpenChange={(o) => !o && setRemoving(null)}>
        <DialogContent
          title={`Remove unit ${removing === null ? "" : str(units[removing]?.id)}?`}
          description="Its files' manifest entries become orphans, which the next apply adopts or deletes."
        >
          <div className="flex flex-col gap-2 text-12" data-testid="unit-remove-dialog">
            {outputs.isLoading ? (
              <span className="text-secondary">Reading the files it owns…</span>
            ) : owned.length ? (
              <>
                <span>
                  {owned.length} {owned.length === 1 ? "file" : "files"} it owns:
                </span>
                <ul className="max-h-48 overflow-auto font-mono" data-testid="unit-remove-files">
                  {owned.slice(0, 200).map((f) => (
                    <li key={f}>{f}</li>
                  ))}
                  {owned.length > 200 ? <li className="text-secondary">and {owned.length - 200} more</li> : null}
                </ul>
              </>
            ) : (
              <span className="text-secondary">It owns no files yet.</span>
            )}
            <div className="flex justify-end gap-2">
              <Button size="sm" variant="ghost" onClick={() => setRemoving(null)}>
                Cancel
              </Button>
              <Button size="sm" variant="primary" onClick={confirmRemove} data-testid="unit-remove-confirm">
                Remove
              </Button>
            </div>
          </div>
        </DialogContent>
      </Dialog>
      {conflict ? (
        <div role="alert" className="flex items-center gap-2 border-b border-default bg-surface px-2 py-0.5 text-12 text-warning" data-testid="units-conflict">
          pack.json changed on disk since it was loaded.
          <Button size="sm" onClick={() => void save(true)}>
            Keep mine
          </Button>
          <Button
            size="sm"
            variant="ghost"
            onClick={() => {
              setDraft(document);
              setLoaded({ doc: document, hash });
              setBase(hash);
              setConflict(false);
            }}
          >
            Take theirs
          </Button>
        </div>
      ) : null}
      <div className="flex min-h-0 flex-1">
        <div className="min-h-0 min-w-0 flex-1 overflow-auto">
          <table role="grid" aria-label="Units" aria-rowcount={units.length + 1} className="w-full border-collapse text-12" data-testid="units-grid">
            <thead className="sticky top-0 z-10 bg-surface text-left text-11 text-secondary">
              <tr role="row" aria-rowindex={1} className="h-6 border-b border-default">
                {["Id", "Files", "Scope", "Filter", "Template", "Output path", "Reads as", "Example path", "Files", "Write", "Formatter"].map((h, i) => (
                  <th key={i} role="columnheader" className="whitespace-nowrap px-1 font-medium">
                    {h}
                  </th>
                ))}
              </tr>
            </thead>
            <tbody>
              {units.map((unit, i) => (
                <UnitRow
                  key={i}
                  pack={pack}
                  index={i}
                  unit={unit}
                  saved={saved.get(str(unit.id))}
                  files={files}
                  selected={i === row}
                  idError={unitIdError(draft, i, str(unit.id))}
                  example={examples[str(unit.id)] ?? null}
                  onFocus={(f) => {
                    setRow(i);
                    setField(f);
                  }}
                  onChange={(f, value) => setDraft((d) => setUnitField(d, i, f, value))}
                  onWhere={(where) =>
                    setDraft((d) => {
                      const us = unitsOf(d).slice();
                      const u = { ...us[i] };
                      if (where) u.where = where;
                      else delete u.where;
                      us[i] = u;
                      return { ...d, units: us };
                    })
                  }
                />
              ))}
            </tbody>
          </table>
          {!units.length ? <p className="px-2 py-1 text-12 text-secondary">No units. Add one with + Unit (Ctrl+Enter).</p> : null}
        </div>
        <aside aria-label="Unit help" className="w-64 shrink-0 overflow-auto border-l border-default bg-surface px-2 py-1 text-12" data-testid="unit-help">
          {current ? (
            <>
              <h3 className="text-11 font-semibold uppercase tracking-wide text-secondary">{str(current.id) || "(new unit)"}</h3>
              <p className="mt-1">{FIELD_HELP[field]}</p>
              <h4 className="mt-2 text-11 font-semibold text-secondary">Scope: {scope || "(none)"}</h4>
              <p data-testid="scope-help">{scopeHelp(scope)}</p>
              <h4 className="mt-2 text-11 font-semibold text-secondary">Write: {WRITE_MODES.find((m) => m.value === (current.mode ?? "overwrite"))?.label}</h4>
              <p>{WRITE_MODES.find((m) => m.value === (current.mode ?? "overwrite"))?.help}</p>
              <h4 className="mt-2 text-11 font-semibold text-secondary">Delimiters</h4>
              <p className="font-mono text-11">
                {current.delimiters
                  ? `${str((current.delimiters as { open: string }).open)} … ${str((current.delimiters as { close: string }).close)}`
                  : "{{ … }} (default)"}
              </p>
              <h4 className="mt-2 text-11 font-semibold text-secondary">Transforms</h4>
              <p className="font-mono text-11">
                {Array.isArray(current.transforms) && current.transforms.length ? (current.transforms as string[]).join(" → ") : "none"}
              </p>
              <p className="mt-2 text-11 text-secondary">
                Ctrl+Enter adds a unit, Ctrl+D duplicates, Ctrl+Delete removes, Alt+Up and Alt+Down reorder, Ctrl+S saves.
              </p>
            </>
          ) : null}
        </aside>
      </div>
    </div>
  );
}

function ExamplePicker({ pack, unit, saved, value, onChange }: { pack: string; unit?: RawUnit; saved?: RawUnit; value: string; onChange(id: string): void }) {
  const paths = useUnitPaths(pack, unit ?? {}, saved);
  const index = useIndex();
  const names = useMemo(() => new Map((index.data ?? []).map((e) => [e.id, e.displayName ?? e.name])), [index.data]);
  // The unit's own planned elements: always of its scope; the first until one is picked (kept per unit).
  const ids = [...new Set((paths.data?.paths ?? []).map((p) => p.elementId).filter((x): x is string => !!x))];
  return (
    <Select
      id="example-element"
      className="h-6 w-56 text-12"
      value={value && ids.includes(value) ? value : (ids[0] ?? "")}
      onChange={(e) => onChange(e.target.value)}
      disabled={!ids.length}
      data-testid="example-element"
    >
      {ids.length ? null : <option value="">No element (runs once)</option>}
      {ids.map((id) => (
        <option key={id} value={id}>
          {names.get(id) ?? id}
        </option>
      ))}
    </Select>
  );
}

const cellInput = "h-5 rounded-[4px] border-transparent bg-transparent px-1 text-12 hover:border-input focus-visible:border-input";

function UnitRow(props: {
  pack: string;
  index: number;
  unit: RawUnit;
  saved?: RawUnit;
  files: string[];
  selected: boolean;
  idError: string | null;
  example: string | null;
  onFocus(field: UnitField): void;
  onChange(field: UnitField, value: string): void;
  onWhere(where: Record<string, unknown> | null): void;
}) {
  const { unit, index } = props;
  const paths = useUnitPaths(props.pack, unit, props.saved);
  const summary = pathSummary(typeof unit.output === "string" ? unit.output : null, unit.delimiters as { open: string; close: string } | null);
  const ex = examplePath(paths.data, props.example);
  const templateOptions = [...new Set([...props.files, str(unit.template)].filter(Boolean))].sort();
  const id = str(unit.id);
  const label = (name: string) => `${name} of unit ${id || index + 1}`;
  const cell = "px-1 align-middle";
  return (
    <tr
      role="row"
      aria-rowindex={index + 2}
      aria-selected={props.selected}
      className={cn("h-6 border-b border-default", props.selected && "bg-accent-subtle/50")}
      data-testid={`unit-row-${id}`}
    >
      <td role="gridcell" className={cell}>
        <Input
          aria-label={label("Id")}
          aria-invalid={!!props.idError}
          title={props.idError ?? undefined}
          className={cn(cellInput, "w-28 font-mono", props.idError && "border-danger")}
          value={id}
          onFocus={() => props.onFocus("id")}
          onChange={(e) => props.onChange("id", e.target.value)}
        />
      </td>
      <td role="gridcell" className={cn(cell, "whitespace-nowrap text-secondary")}>
        {filesLabel(str(unit.for))}
      </td>
      <td role="gridcell" className={cell}>
        <Input
          aria-label={label("Scope")}
          list="unit-scopes"
          className={cn(cellInput, "w-36")}
          value={str(unit.for)}
          onFocus={() => props.onFocus("for")}
          onChange={(e) => props.onChange("for", e.target.value)}
        />
        {index === 0 ? (
          <datalist id="unit-scopes">
            {SCOPES.map((s) => (
              <option key={s} value={s} />
            ))}
          </datalist>
        ) : null}
      </td>
      <td role="gridcell" className={cell}>
        <FilterPopover where={unit.where as Record<string, unknown> | undefined} name={label("Filter")} onChange={props.onWhere} />
      </td>
      <td role="gridcell" className={cell}>
        <Select
          aria-label={label("Template")}
          className={cn(cellInput, "w-36")}
          value={str(unit.template)}
          onFocus={() => props.onFocus("template")}
          onChange={(e) => props.onChange("template", e.target.value)}
        >
          {templateOptions.map((f) => (
            <option key={f} value={f}>
              {f}
            </option>
          ))}
        </Select>
      </td>
      <td role="gridcell" className={cell}>
        <Input
          aria-label={label("Output path")}
          className={cn(cellInput, "w-72 font-mono text-11")}
          value={str(unit.output)}
          placeholder="(the template writes its files)"
          onFocus={() => props.onFocus("output")}
          onChange={(e) => props.onChange("output", e.target.value)}
          data-testid="unit-output"
        />
      </td>
      <td role="gridcell" className={cn(cell, "max-w-72 truncate", summary.raw && "font-mono text-11")} title={summary.text} data-testid="unit-summary">
        {summary.text}
      </td>
      <td role="gridcell" className={cn(cell, "max-w-80 truncate font-mono text-11")} data-testid="unit-example" title={ex?.path ?? undefined}>
        {paths.isError ? (
          <span className="text-danger">{(paths.error as Error).message}</span>
        ) : !ex ? (
          <span className="text-secondary">…</span>
        ) : ex.path === null ? (
          <span className="text-secondary">nothing planned</span>
        ) : (
          <>
            {ex.path}
            {ex.rule ? (
              <Badge tone="danger" className="ml-1" title={ex.collisions.length ? `Same path for ${ex.collisions.join(", ")}` : undefined}>
                {ex.rule}
                {ex.collisions.length ? ` · ${ex.collisions.length + 1} elements` : ""}
              </Badge>
            ) : null}
          </>
        )}
      </td>
      <td role="gridcell" className={cn(cell, "text-right text-secondary")} data-testid="unit-count">
        {paths.data ? paths.data.count : ""}
      </td>
      <td role="gridcell" className={cell}>
        <Select
          aria-label={label("Write")}
          className={cn(cellInput, "w-40")}
          value={str(unit.mode) || "overwrite"}
          onFocus={() => props.onFocus("mode")}
          onChange={(e) => props.onChange("mode", e.target.value)}
        >
          {WRITE_MODES.map((m) => (
            <option key={m.value} value={m.value}>
              {m.label}
            </option>
          ))}
        </Select>
      </td>
      <td role="gridcell" className={cell}>
        <Input
          aria-label={label("Formatter")}
          className={cn(cellInput, "w-24")}
          value={str(unit.formatter)}
          placeholder="by extension"
          onFocus={() => props.onFocus("formatter")}
          onChange={(e) => props.onChange("formatter", e.target.value)}
        />
      </td>
    </tr>
  );
}

const WHERE_LISTS: [string, string][] = [
  ["tags", "Tags"],
  ["notTags", "Not tags"],
  ["stereotypes", "Stereotypes"],
  ["notStereotypes", "Not stereotypes"],
  ["categories", "Categories"],
  ["packages", "Packages"],
  ["notPackages", "Not packages"],
];

function FilterPopover({ where, name, onChange }: { where?: Record<string, unknown>; name: string; onChange(where: Record<string, unknown> | null): void }) {
  const words = filterWords(where);
  const set = (key: string, value: unknown) => {
    const next: Record<string, unknown> = { ...(where ?? {}) };
    if (value === undefined || value === "" || (Array.isArray(value) && !value.length)) delete next[key];
    else next[key] = value;
    onChange(Object.keys(next).length ? next : null);
  };
  const list = (key: string) => (Array.isArray(where?.[key]) ? (where![key] as string[]).join(", ") : "");
  return (
    <Popover>
      <PopoverTrigger asChild>
        <button
          type="button"
          aria-label={name}
          className="flex h-5 max-w-40 items-center gap-1 truncate rounded-[4px] px-1 text-12 hover:bg-accent-subtle"
          title={words || "No filter"}
        >
          <Filter className={cn("size-3 shrink-0", words ? "text-accent" : "text-secondary")} aria-hidden />
          <span className="truncate">{words || "all"}</span>
        </button>
      </PopoverTrigger>
      <PopoverContent className="flex w-72 flex-col gap-1" align="start">
        <p className="text-11 text-secondary">
          Only elements that match every set filter; a list matches any of its values; a not-list excludes any of its values.
        </p>
        {WHERE_LISTS.map(([key, text]) => (
          <label key={key} className="grid grid-cols-[7rem_1fr] items-center gap-1 text-12">
            {text}
            <Input
              className="h-6 text-12"
              defaultValue={list(key)}
              placeholder="comma separated"
              onBlur={(e) =>
                set(
                  key,
                  e.target.value
                    .split(",")
                    .map((s) => s.trim())
                    .filter(Boolean),
                )
              }
            />
          </label>
        ))}
        <label className="grid grid-cols-[7rem_1fr] items-center gap-1 text-12">
          Database
          <Input className="h-6 text-12" defaultValue={str(where?.database)} onBlur={(e) => set("database", e.target.value.trim())} />
        </label>
        <label className="grid grid-cols-[7rem_1fr] items-center gap-1 text-12">
          Abstract
          <Select
            className="h-6 text-12"
            value={where?.abstract === true ? "yes" : where?.abstract === false ? "no" : ""}
            onChange={(e) => set("abstract", e.target.value === "" ? undefined : e.target.value === "yes")}
          >
            <option value="">either</option>
            <option value="yes">abstract only</option>
            <option value="no">not abstract</option>
          </Select>
        </label>
        <label className="grid grid-cols-[7rem_1fr] items-center gap-1 text-12">
          Script filter
          <Input className="h-6 text-12" defaultValue={str(where?.script)} onBlur={(e) => set("script", e.target.value.trim())} />
        </label>
      </PopoverContent>
    </Popover>
  );
}
