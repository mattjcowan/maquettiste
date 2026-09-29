// The pack editor's Templates tab (generation-ui.md 3.3): the pack folder's files on the left, the file in Monaco in the
// middle (Scriban coloured; partials, helpers.js and other text files editable too), and the live preview on the right:
// the chosen unit rendered for the chosen element with the unsaved text of every open file (debounced 300 ms), its
// diagnostics listed and marked in the editor, the output path above the rendered text. Save writes the file on disk
// under .maquettiste/templates/<pack>/ with its hash (If-Match); a 409 shows a bar to keep mine or take theirs.
import { useEffect, useMemo, useRef, useState, type KeyboardEvent } from "react";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { FileCode2, Folder, Save } from "lucide-react";
import * as endpoints from "@/api/endpoints";
import type { PreviewRequest, PreviewResult } from "@/api/types";
import { keys, useIndex } from "@/api/queries";
import { CodeView } from "@/code";
import type { CodeMarker } from "@/code/CodeEditor";
import { Button } from "@/components/ui/button";
import { Select } from "@/components/ui/input";
import { Badge, EmptyState, Spinner, Toolbar } from "@/components/ui/misc";
import { cn } from "@/lib/cn";
import { markUnsaved, useDraftState } from "./drafts";
import {
  lineDiff,
  diagnosticsFor,
  fileTree,
  isDirty,
  languageOf,
  overlayOf,
  PreviewScheduler,
  unitsForFile,
  type Buffer,
  type Diagnostic,
} from "./templatesModel";

interface Props {
  pack: string;
  files: endpoints.PackDocument["files"];
  units: string[];
  /** A file to show (the explorer's file rows). */
  focusFile?: string;
  onDirty(dirty: boolean): void;
}

interface Conflict {
  path: string;
  hash: string | null;
  current: string | null;
}

const PREVIEW_DELAY = 300;

export function TemplatesTab({ pack, files, units, focusFile, onDirty }: Props) {
  const qc = useQueryClient();
  const tree = useMemo(() => fileTree(files), [files]);
  const firstFile = tree.find((r) => r.file?.role === "template")?.path ?? tree.find((r) => !r.folder)?.path ?? null;
  const [selected, setSelected] = useState<string | null>(focusFile ?? firstFile);
  const [seenFocus, setSeenFocus] = useState(focusFile);
  if (focusFile !== seenFocus) {
    setSeenFocus(focusFile);
    if (focusFile) setSelected(focusFile);
  }
  const path = selected && files.some((f) => f.path === selected) ? selected : firstFile;
  const [buffers, setBuffers] = useDraftState<Record<string, Buffer>>(`${pack}:templates:buffers`, {});
  const [saveDiagnostics, setSaveDiagnostics] = useState<Record<string, Diagnostic[]>>({});
  // Conflicts by path: a save of one file never clears another file's bar.
  const [conflicts, setConflicts] = useDraftState<Record<string, Conflict>>(`${pack}:templates:conflicts`, {});
  const conflict = path ? (conflicts[path] ?? null) : null;
  const setConflictFor = (p: string, c: Conflict | null) =>
    setConflicts((prev) => {
      const next = { ...prev };
      if (c) next[p] = c;
      else delete next[p];
      return next;
    });
  // Hashes this tab wrote itself, so the refetch after a save is not taken for a change on disk.
  const ours = useRef(new Map<string, Set<string>>());
  // Bumped when the text is replaced from outside the editor (Take theirs).
  const [revisions, setRevisions] = useDraftState<Record<string, number>>(`${pack}:templates:revisions`, {});
  const [busy, setBusy] = useState(false);
  const [comparing, setComparing] = useState(false);
  const [notice, setNotice] = useState<string | null>(null);

  const disk = useQuery({
    queryKey: [...keys.pack(pack), "file", path],
    queryFn: () => endpoints.getPackFile(pack, path!),
    enabled: !!path,
    retry: false,
  });
  const buffer: Buffer | undefined =
    (path && buffers[path]) || (disk.data && disk.data.path === path ? { text: disk.data.text, base: disk.data.text, hash: disk.data.hash } : undefined);
  const dirtyPaths = useMemo(() => new Set(Object.keys(buffers).filter((p) => isDirty(buffers[p]))), [buffers]);
  useEffect(() => {
    markUnsaved(`${pack}:templates`, dirtyPaths.size > 0);
    onDirty(dirtyPaths.size > 0);
  }, [pack, dirtyPaths, onDirty]);
  // A change on disk (templates.changed, the CLI, git): an unedited buffer takes the new text; an edited one shows
  // the conflict bar now rather than on its next save.
  const diskHash = disk.data && disk.data.path === path ? disk.data.hash : null;
  useEffect(() => {
    if (!path || busy || !disk.data || disk.data.path !== path) return;
    const held = buffers[path];
    if (!held || held.hash === disk.data.hash || ours.current.get(path)?.has(disk.data.hash)) return;
    if (!isDirty(held)) {
      const { text, hash } = disk.data;
      setBuffers((prev) => ({ ...prev, [path]: { text, base: text, hash } }));
      setRevisions((prev) => ({ ...prev, [path]: (prev[path] ?? 0) + 1 }));
    } else if (conflicts[path]?.hash !== disk.data.hash) {
      setConflictFor(path, { path, hash: disk.data.hash, current: disk.data.text });
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [path, diskHash, busy]);

  const edit = (text: string) => {
    if (!path || !buffer) return;
    setBuffers((prev) => ({ ...prev, [path]: { ...(prev[path] ?? buffer), text } }));
  };

  const save = async (overHash?: string | null) => {
    if (!path || !buffer || busy) return;
    if (overHash === undefined && !isDirty(buffer)) return;
    setBusy(true);
    try {
      const text = buffer.text;
      const result = await endpoints.savePackFile(pack, path, text, overHash === undefined ? buffer.hash : overHash);
      if (result.outcome === "conflict") {
        setConflictFor(path, { path, hash: result.hash, current: result.current });
        return;
      }
      if (result.outcome !== "saved" || !result.hash) {
        setNotice(result.diagnostics.map((d) => `${d.rule} ${d.message}`).join(" ") || `Not saved (${result.outcome}).`);
        return;
      }
      const hash = result.hash;
      const mine = ours.current.get(path) ?? new Set<string>();
      mine.add(hash);
      ours.current.set(path, mine);
      setConflictFor(path, null);
      setNotice(null);
      setBuffers((prev) => ({ ...prev, [path]: { text: prev[path]?.text ?? text, base: text, hash } }));
      setSaveDiagnostics((prev) => ({ ...prev, [path]: result.diagnostics }));
      await qc.invalidateQueries({ queryKey: keys.pack(pack) });
      await qc.invalidateQueries({ queryKey: keys.packs });
    } finally {
      setBusy(false);
    }
  };

  const takeTheirs = () => {
    if (!conflict) return;
    const { path: p, hash, current } = conflict;
    setConflictFor(p, null);
    setRevisions((prev) => ({ ...prev, [p]: (prev[p] ?? 0) + 1 }));
    if (current === null || hash === null) {
      setBuffers((prev) => Object.fromEntries(Object.entries(prev).filter(([k]) => k !== p)));
      return;
    }
    setBuffers((prev) => ({ ...prev, [p]: { text: current, base: current, hash } }));
  };

  const onKeyDown = (e: KeyboardEvent) => {
    if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === "s") {
      e.preventDefault();
      void save();
    }
  };

  // The preview's unit: one that uses this file (directly, as a companion, through includes; any for a script).
  const feeding = useMemo(() => (path ? unitsForFile(files, units, path) : []), [files, units, path]);
  const [unitChoice, setUnitChoice] = useState<string>("");
  const unit = unitChoice && units.includes(unitChoice) ? unitChoice : (feeding[0] ?? units[0] ?? "");
  const [elementChoice, setElementChoice] = useState<Record<string, string>>({});
  const markers: CodeMarker[] = [];
  const preview = usePreview(pack, unit, elementChoice[unit] ?? "", buffers);
  const fileDiagnostics = path ? [...(saveDiagnostics[path] ?? []), ...diagnosticsFor(preview.result?.diagnostics ?? [], pack, path)] : [];
  for (const d of fileDiagnostics)
    if (d.line) markers.push({ line: d.line, column: d.column, message: `${d.rule} ${d.message}`, severity: d.severity === "error" ? "error" : "warning" });

  if (!files.some((f) => f.role !== "manifest"))
    return <EmptyState title="No template files">This pack has only pack.json. Add templates under .maquettiste/templates/{pack}/.</EmptyState>;
  return (
    <div className="flex h-full min-h-0" data-testid="templates-tab" onKeyDown={onKeyDown}>
      <nav aria-label="Pack files" className="w-48 shrink-0 overflow-auto border-r border-default py-0.5 text-12">
        <ul>
          {tree.map((row) =>
            row.folder ? (
              <li key={row.path} className="flex h-6 items-center gap-1 text-secondary" style={{ paddingLeft: 4 + row.depth * 12 }}>
                <Folder className="size-3.5 shrink-0" aria-hidden /> {row.name}
              </li>
            ) : (
              <li key={row.path}>
                <button
                  type="button"
                  className={cn(
                    "flex h-6 w-full items-center gap-1 pr-1 text-left hover:bg-accent-subtle",
                    row.path === path && "bg-accent-subtle font-medium",
                  )}
                  style={{ paddingLeft: 4 + row.depth * 12 }}
                  aria-current={row.path === path ? "true" : undefined}
                  title={[row.path, row.file?.role, row.file?.usedBy.join(", ")].filter(Boolean).join("\n")}
                  onClick={() => setSelected(row.path)}
                  data-testid="template-file"
                >
                  <FileCode2 className="size-3.5 shrink-0 text-secondary" aria-hidden />
                  <span className="min-w-0 flex-1 truncate font-mono text-11">{row.name}</span>
                  {dirtyPaths.has(row.path) ? (
                    <span aria-label="unsaved" className="text-accent">
                      •
                    </span>
                  ) : null}
                  {row.file && row.file.role !== "template" ? <span className="text-11 text-secondary">{row.file.role}</span> : null}
                </button>
              </li>
            ),
          )}
        </ul>
      </nav>
      <section className="flex min-w-0 flex-1 flex-col" aria-label="Template">
        <Toolbar label="Template file" className="gap-1 px-1 text-12">
          <span className="min-w-0 truncate font-mono text-11" data-testid="template-path">
            {path}
            {isDirty(buffer) ? <span aria-label="unsaved"> •</span> : null}
          </span>
          <span className="truncate text-11 text-secondary">{path ? feedsLine(files, path, feeding) : ""}</span>
          <span className="flex-1" />
          <Button size="sm" variant="primary" disabled={!isDirty(buffer) || busy} onClick={() => void save()} title="Ctrl+S" data-testid="template-save">
            <Save className="size-3.5" /> Save
          </Button>
        </Toolbar>
        {conflict && conflict.path === path ? (
          <div
            role="alert"
            className="flex min-w-0 flex-wrap items-center gap-x-2 gap-y-0.5 border-b border-default bg-surface px-2 py-0.5 text-12 text-warning"
            data-testid="template-conflict"
          >
            {conflict.current === null ? `${path} was deleted on disk.` : `${path} changed on disk since you opened it.`}
            <Button size="sm" onClick={() => void save(conflict.hash)}>
              Keep mine
            </Button>
            <Button size="sm" variant="ghost" onClick={takeTheirs}>
              Take theirs
            </Button>
            {conflict.current !== null ? (
              <Button size="sm" variant="ghost" aria-pressed={comparing} onClick={() => setComparing(!comparing)} data-testid="template-compare">
                Compare
              </Button>
            ) : null}
          </div>
        ) : null}
        {conflict && conflict.current !== null && comparing && buffer ? (
          <div className="h-48 shrink-0 border-b border-default" data-testid="template-compare-view">
            <pre className="h-full overflow-auto font-mono text-11" aria-label={`${path}: on disk (-) and yours (+)`}>
              {lineDiff(conflict.current, buffer.text).map((l, n) => (
                <div key={n} className={cn(l.op === "-" && "text-danger", l.op === "+" && "text-success")}>
                  {l.op} {l.text}
                </div>
              ))}
            </pre>
          </div>
        ) : null}
        {notice ? (
          <div role="alert" className="border-b border-default px-2 py-0.5 text-12 text-danger">
            {notice}
          </div>
        ) : null}
        <div className="min-h-0 flex-1">
          {!path ? null : buffer ? (
            <CodeView
              language={languageOf(path)}
              path={`templates/${pack}/${path}`}
              value={buffer.text}
              revision={revisions[path] ?? 0}
              onChange={edit}
              onSave={() => void save()}
              markers={markers}
              label={`${pack}/${path}`}
            />
          ) : disk.isError ? (
            <EmptyState title={`${path} could not be read`}>{(disk.error as Error).message}</EmptyState>
          ) : (
            <Spinner label={`Loading ${path}`} />
          )}
        </div>
      </section>
      <PreviewPane
        pack={pack}
        units={units}
        feeding={feeding}
        unit={unit}
        onUnit={setUnitChoice}
        element={elementChoice[unit] ?? ""}
        onElement={(id) => setElementChoice((prev) => ({ ...prev, [unit]: id }))}
        preview={preview}
      />
    </div>
  );
}

function feedsLine(files: endpoints.PackDocument["files"], path: string, feeding: string[]): string {
  const role = files.find((f) => f.path === path)?.role;
  if (role === "script") return "script: every unit's templates can call it";
  if (!feeding.length) return "no unit uses it yet";
  return `${role === "partial" ? "included by" : "used by"} ${feeding.join(", ")}`;
}

interface PreviewState {
  result: PreviewResult | null;
  error: string | null;
  pending: boolean;
  elementIds: string[];
}

/** Renders the unit with the unsaved text, `PREVIEW_DELAY` ms after the last change; the element ids come from the unit's planned paths. */
function usePreview(pack: string, unit: string, element: string, buffers: Record<string, Buffer>): PreviewState {
  const paths = useQuery({
    queryKey: [...keys.pack(pack), "paths", unit, "saved"],
    queryFn: ({ signal }) => endpoints.unitPaths({ pack, unit, limit: 200, unitOverride: null }, signal, endpoints.LIVE_CLIENT),
    enabled: !!unit,
    retry: false,
  });
  const elementIds = useMemo(() => [...new Set((paths.data?.paths ?? []).map((p) => p.elementId).filter((x): x is string => !!x))], [paths.data]);
  const elementId = element && elementIds.includes(element) ? element : (elementIds[0] ?? null);
  const overlay = useMemo(() => overlayOf(buffers), [buffers]);
  const requestKey = JSON.stringify({ pack, unit, elementId, overlay });
  const ready = !!unit && paths.isFetched;
  const [answer, setAnswer] = useState<{ key: string; result: PreviewResult | null; error: string | null } | null>(null);
  const scheduler = useRef<PreviewScheduler<string, PreviewResult> | null>(null);
  useEffect(() => {
    const s = new PreviewScheduler<string, PreviewResult>(
      (key, signal) => endpoints.previewTemplate(JSON.parse(key) as PreviewRequest, signal, endpoints.LIVE_CLIENT),
      (r, key) => setAnswer(r.ok ? { key, result: r.value, error: null } : { key, result: null, error: (r.error as Error).message ?? String(r.error) }),
      PREVIEW_DELAY,
    );
    scheduler.current = s;
    return () => s.dispose();
  }, []);
  useEffect(() => {
    if (ready) scheduler.current?.schedule(requestKey);
  }, [ready, requestKey]);
  return {
    result: answer?.result ?? null,
    error: answer?.error ?? null,
    pending: !ready || answer?.key !== requestKey,
    elementIds,
  };
}

function PreviewPane(props: {
  pack: string;
  units: string[];
  feeding: string[];
  unit: string;
  onUnit(unit: string): void;
  element: string;
  onElement(id: string): void;
  preview: PreviewState;
}) {
  const { units, feeding, unit, preview } = props;
  const index = useIndex();
  const names = useMemo(() => new Map((index.data ?? []).map((e) => [e.id, e.displayName ?? e.name])), [index.data]);
  const others = units.filter((u) => !feeding.includes(u));
  const result = preview.result;
  return (
    <section className="flex w-[42%] min-w-0 shrink-0 flex-col border-l border-default" aria-label="Preview" data-testid="template-preview">
      <Toolbar label="Preview" className="gap-1 px-1 text-12">
        <label className="flex items-center gap-1 text-secondary">
          Unit
          <Select className="h-6 w-32 text-12" value={unit} onChange={(e) => props.onUnit(e.target.value)} data-testid="preview-unit">
            {feeding.map((u) => (
              <option key={u} value={u}>
                {u}
              </option>
            ))}
            {others.length ? (
              <optgroup label="Other units">
                {others.map((u) => (
                  <option key={u} value={u}>
                    {u}
                  </option>
                ))}
              </optgroup>
            ) : null}
          </Select>
        </label>
        <label className="flex min-w-0 items-center gap-1 text-secondary">
          Element
          <Select
            className="h-6 min-w-0 flex-1 text-12"
            value={props.element}
            onChange={(e) => props.onElement(e.target.value)}
            disabled={!preview.elementIds.length}
            data-testid="preview-element"
          >
            <option value="">{preview.elementIds.length ? "First in scope" : "None (runs once)"}</option>
            {preview.elementIds.map((id) => (
              <option key={id} value={id}>
                {names.get(id) ?? id}
              </option>
            ))}
          </Select>
        </label>
        {preview.pending ? (
          <span className="text-11 text-secondary">rendering…</span>
        ) : result ? (
          <span className="text-11 text-secondary">{result.elapsedMs} ms</span>
        ) : null}
      </Toolbar>
      {result?.diagnostics.length ? (
        <ul className="max-h-24 shrink-0 overflow-auto border-b border-default text-11" aria-label="Preview diagnostics" data-testid="preview-diagnostics">
          {result.diagnostics.map((d, i) => (
            <li key={i} className={cn("px-2 py-px", d.severity === "error" ? "text-danger" : "text-warning")}>
              <Badge tone={d.severity === "error" ? "danger" : "warning"}>{d.rule}</Badge> {d.message}
              {d.filePath ? (
                <span className="text-secondary">
                  {" "}
                  {d.filePath.split("/").at(-1)}
                  {d.line ? `:${d.line}${d.column ? `:${d.column}` : ""}` : ""}
                </span>
              ) : null}
            </li>
          ))}
        </ul>
      ) : null}
      <div className="min-h-0 flex-1 overflow-auto">
        {!unit ? (
          <EmptyState title="No unit to preview">Add a unit on the Units tab; it names the template it runs.</EmptyState>
        ) : preview.error ? (
          <p role="alert" className="px-2 py-1 text-12 text-danger">
            {preview.error}
          </p>
        ) : !result ? (
          <Spinner label="Rendering" />
        ) : !result.files.length ? (
          <p className="px-2 py-1 text-12 text-secondary">The render wrote no file.</p>
        ) : (
          result.files.map((f) => (
            <div key={f.path}>
              <div className="sticky top-0 flex h-6 items-center gap-1 border-b border-default bg-surface px-2 text-11">
                <span className="min-w-0 truncate font-mono" data-testid="preview-path" title={f.path}>
                  {f.path}
                </span>
                {f.role !== "main" ? <span className="text-secondary">{f.role}</span> : null}
              </div>
              <pre className="px-2 py-1 font-mono text-11 leading-[16px] whitespace-pre" data-testid="preview-text">
                {f.text}
              </pre>
            </div>
          ))
        )}
      </div>
    </section>
  );
}
