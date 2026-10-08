// The pack editor's Templates tab (generation-ui.md 3.3): the pack folder's files on the left, the file in Monaco in the
// middle (Scriban coloured; partials, helpers.js and other text files editable too), and the live preview on the right:
// the chosen unit rendered for ONE chosen element with the unsaved text of every open file (debounced 300 ms), its
// diagnostics listed and marked in the editor, the output path above the rendered text. A unit whose one render covers
// the whole model, a database or a locale renders only when asked (Preview), and so does a list of a few elements
// (Preview a list…): nothing renders more than what is on screen (generation-ui.md 5.2, "Bounds"). Save writes the file on disk
// under .maquettiste/templates/<pack>/ with its hash (If-Match); a 409 shows a bar to keep mine or take theirs.
import { useEffect, useMemo, useRef, useState, type KeyboardEvent } from "react";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { FileCode2, FilePlus, Folder, Pencil, Save, Trash2 } from "lucide-react";
import * as endpoints from "@/api/endpoints";
import type { PreviewRequest, PreviewResult } from "@/api/types";
import { keys, useIndex } from "@/api/queries";
import { CodeView } from "@/code";
import type { CodeMarker } from "@/code/CodeEditor";
import { Button } from "@/components/ui/button";
import { Input, Select } from "@/components/ui/input";
import { Badge, EmptyState, Spinner, Toolbar } from "@/components/ui/misc";
import { cn } from "@/lib/cn";
import { markUnsaved, useDraftState } from "./drafts";
import { noFilesText, pickElement, previewUnit, scopeCandidates, scopeMismatch, unitScope, type Candidate, type UnitScope } from "./previewScope";
import {
  lineDiff,
  diagnosticsFor,
  fileTree,
  isDirty,
  languageOf,
  namedByUnits,
  packPathProblem,
  overlayOf,
  PreviewScheduler,
  unitsForFile,
  type Buffer,
  type Diagnostic,
} from "./templatesModel";
import { matchLines, type LineMap } from "./lineMap";
import { useAskedPreview, type AskedPreview } from "./widePreview";
import { useServices } from "@/app/context";
import { EdgeToggle, PanelToggle } from "@/app/panels";
import { Splitter } from "@/components/ui/splitter";
import { LIMITS } from "@/state/layout";
import { useEditor } from "@/state/store";
import { onListArrowKeys } from "@/lib/listKeys";

interface Props {
  pack: string;
  /** The pack.json hash read (a rename of a file a unit names rewrites pack.json with it). */
  packHash: string;
  files: endpoints.PackDocument["files"];
  units: string[];
  /** Each unit's `for` (its scope), by unit id. */
  scopes: Record<string, string>;
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

export function TemplatesTab({ pack, packHash, files, units, scopes, focusFile, onDirty }: Props) {
  const qc = useQueryClient();
  // The file list and the preview hide and resize like the shell's panels (layout.ts: kept per browser, Reset layout
  // brings them back): a header button, Alt+Shift+F and Alt+Shift+V, the palette, and a slim edge while hidden.
  const { store } = useServices();
  const filesHidden = useEditor(store, (s) => s.packFilesCollapsed);
  const previewHidden = useEditor(store, (s) => s.templatePreviewCollapsed);
  const filesSize = useEditor(store, (s) => s.packFilesSize);
  const previewSize = useEditor(store, (s) => s.templatePreviewSize);
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

  // `latest` is the editor's text at the keystroke (Ctrl+S in the code editor): the state's copy may lag it by a render.
  const save = async (overHash?: string | null, latest?: string) => {
    if (!path || !buffer) return;
    if (busyRef.current) {
      queued.current = true; // runs right after the save in flight instead of being dropped
      return;
    }
    const text = latest ?? buffer.text;
    if (overHash === undefined && !isDirty({ ...buffer, text })) return;
    if (text !== buffer.text) setBuffers((prev) => ({ ...prev, [path]: { ...(prev[path] ?? buffer), text } }));
    busyRef.current = true;
    setBusy(true);
    try {
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
      busyRef.current = false;
      setBusy(false);
    }
  };
  const busyRef = useRef(false);
  const queued = useRef(false);
  useEffect(() => {
    if (busy || !queued.current) return;
    queued.current = false;
    void save();
  });

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
  const { unit, note } = previewUnit(
    files.find((f) => f.path === path),
    feeding,
    units,
    unitChoice,
  );
  const scope = useMemo(() => unitScope(scopes[unit] ?? ""), [scopes, unit]);
  // The element per unit, kept while the pack stays open.
  const [elementChoice, setElementChoice] = useDraftState<Record<string, string>>(`${pack}:templates:elements`, {});
  const markers: CodeMarker[] = [];
  const preview = usePreview(pack, unit, scope, elementChoice[unit], buffers);
  const fileDiagnostics = path ? [...(saveDiagnostics[path] ?? []), ...diagnosticsFor(preview.result?.diagnostics ?? [], pack, path)] : [];
  for (const d of fileDiagnostics)
    if (d.line) markers.push({ line: d.line, column: d.column, message: `${d.rule} ${d.message}`, severity: d.severity === "error" ? "error" : "warning" });

  // New, Rename and Delete in the file tree (generation-ui.md 3.3): through the pack file API, each one change on disk.
  const [fileAction, setFileAction] = useState<{ kind: "new" | "rename" | "delete"; value: string } | null>(null);
  const [fileError, setFileError] = useState<string | null>(null);
  const startAction = (kind: "new" | "rename" | "delete") => {
    setFileError(null);
    const folder = path && path.includes("/") ? path.slice(0, path.lastIndexOf("/") + 1) : "";
    setFileAction({ kind, value: kind === "new" ? `${folder}new.scriban` : (path ?? "") });
  };
  const refusal = (r: endpoints.PackWriteResult, fallback: string) => r.diagnostics.map((d) => d.message).join(" ") || fallback;
  const runFileAction = async () => {
    if (!fileAction || busy) return;
    const target = fileAction.value.trim();
    if (fileAction.kind !== "delete") {
      const problem = packPathProblem(
        target,
        files.map((f) => f.path),
      );
      if (problem && !(fileAction.kind === "rename" && target === path)) return setFileError(problem);
      if (fileAction.kind === "rename" && target === path) return setFileAction(null);
    }
    if (fileAction.kind !== "new" && (!path || !buffer)) return;
    if (fileAction.kind === "rename" && isDirty(buffer)) return setFileError("Save the file before renaming it.");
    setBusy(true);
    try {
      let answer: endpoints.PackWriteResult;
      if (fileAction.kind === "new") answer = await endpoints.savePackFile(pack, target, "", null);
      else if (fileAction.kind === "rename")
        answer = await endpoints.movePackFile(pack, path!, target, buffer!.hash, namedByUnits(files.find((f) => f.path === path)) ? packHash : null);
      else answer = await endpoints.deletePackFile(pack, path!, buffer!.hash);
      if (answer.outcome !== "saved") {
        const what =
          fileAction.kind === "delete" ? `${path} was not deleted` : fileAction.kind === "rename" ? `${path} was not renamed` : `${target} was not created`;
        return setFileError(
          answer.outcome === "referenced"
            ? `${what}: ${refusal(answer, "a unit or a template still uses it")}`
            : answer.outcome === "conflict"
              ? `${what}: it changed on disk; reload and try again.`
              : `${what}: ${refusal(answer, answer.outcome)}`,
        );
      }
      const gone = fileAction.kind !== "new" ? path : null;
      if (gone) {
        setBuffers((prev) => Object.fromEntries(Object.entries(prev).filter(([k]) => k !== gone)));
        setConflictFor(gone, null);
      }
      setSelected(fileAction.kind === "delete" ? null : target);
      // The file that moved away is not read again (it would answer 404).
      const fileKey = [...keys.pack(pack), "file", gone];
      qc.removeQueries({ queryKey: fileKey, exact: true });
      await qc.invalidateQueries({ queryKey: keys.pack(pack), predicate: (q) => JSON.stringify(q.queryKey) !== JSON.stringify(fileKey) });
      await qc.invalidateQueries({ queryKey: keys.packs });
      setFileAction(null);
      setFileError(null);
    } finally {
      setBusy(false);
    }
  };

  // Completion and hover from the preview unit's template context.
  const scriban = !!path && languageOf(path) === "scriban";
  const context = useQuery({
    queryKey: [...keys.pack(pack), "context", unit],
    queryFn: () => endpoints.getTemplateContext(pack, unit),
    enabled: scriban && !!unit,
    staleTime: 60_000,
    retry: false,
  });

  // Which output lines a template line produced, and back: a text match (the renderer reports no positions).
  const [cursorLine, setCursorLine] = useState<number | null>(null);
  const [outputPick, setOutputPick] = useState<{ file: string; line: number } | null>(null);
  const templateText = scriban ? (buffer?.text ?? null) : null;
  const lineMaps = useMemo(() => {
    const maps = new Map<string, LineMap>();
    if (templateText === null) return maps;
    for (const f of preview.result?.files ?? []) maps.set(f.path, matchLines(templateText, f.text));
    return maps;
  }, [templateText, preview.result]);
  const templateHighlight = outputPick ? (lineMaps.get(outputPick.file)?.toTemplate.get(outputPick.line) ?? []) : [];
  const outputHighlight = useMemo(() => {
    const out = new Map<string, Set<number>>();
    if (outputPick) out.set(outputPick.file, new Set([outputPick.line]));
    else if (cursorLine) for (const [file, map] of lineMaps) out.set(file, new Set(map.toOutput.get(cursorLine) ?? []));
    return out;
  }, [lineMaps, cursorLine, outputPick]);

  if (!files.some((f) => f.role !== "manifest"))
    return <EmptyState title="No template files">This pack has only pack.json. Add templates under .maquettiste/templates/{pack}/.</EmptyState>;
  return (
    <div className="flex h-full min-h-0" data-testid="templates-tab" onKeyDown={onKeyDown}>
      {filesHidden ? <EdgeToggle panel="packFiles" side="left" /> : null}
      <nav
        id="mq-pack-files"
        aria-label="Pack files"
        className={cn("flex shrink-0 flex-col overflow-auto text-12", filesHidden && "hidden")}
        style={{ width: filesSize }}
        data-testid="pack-files"
      >
        <div className="flex h-6 shrink-0 items-center gap-0.5 border-b border-default px-1" role="toolbar" aria-label="File actions">
          <Button
            size="icon"
            variant="ghost"
            className="size-5"
            title="New file"
            label="New file"
            onClick={() => startAction("new")}
            data-testid="template-new"
          >
            <FilePlus className="size-3.5" />
          </Button>
          <Button
            size="icon"
            variant="ghost"
            className="size-5"
            title="Rename"
            label="Rename"
            disabled={!path}
            onClick={() => startAction("rename")}
            data-testid="template-rename"
          >
            <Pencil className="size-3.5" />
          </Button>
          <Button
            size="icon"
            variant="ghost"
            className="size-5"
            title="Delete"
            label="Delete"
            disabled={!path}
            onClick={() => startAction("delete")}
            data-testid="template-delete"
          >
            <Trash2 className="size-3.5" />
          </Button>
          <span className="flex-1" />
          {/* A hidden pane stays mounted (its form and scroll survive); its edge, not this button, brings it back. */}
          {filesHidden ? null : <PanelToggle panel="packFiles" />}
        </div>
        {fileAction ? (
          <form
            className="flex shrink-0 flex-col gap-0.5 border-b border-default p-1"
            data-testid="template-file-action"
            onSubmit={(e) => {
              e.preventDefault();
              void runFileAction();
            }}
          >
            {fileAction.kind === "delete" ? (
              <span className="text-11">
                Delete <span className="font-mono">{path}</span>?
              </span>
            ) : (
              <Input
                autoFocus
                className="h-6 font-mono text-11"
                aria-label={fileAction.kind === "new" ? "New file path" : "New path"}
                value={fileAction.value}
                onChange={(e) => setFileAction({ ...fileAction, value: e.target.value })}
                onKeyDown={(e) => {
                  if (e.key === "Escape") setFileAction(null);
                }}
              />
            )}
            {fileError ? (
              <span role="alert" className="text-11 text-danger" data-testid="template-file-error">
                {fileError}
              </span>
            ) : null}
            <span className="flex gap-1">
              <Button size="sm" type="submit" variant={fileAction.kind === "delete" ? "danger" : "primary"} disabled={busy}>
                {fileAction.kind === "new" ? "Create" : fileAction.kind === "rename" ? "Rename" : "Delete"}
              </Button>
              <Button size="sm" variant="ghost" type="button" onClick={() => setFileAction(null)}>
                Cancel
              </Button>
            </span>
          </form>
        ) : null}
        <ul className="py-0.5" onKeyDown={onListArrowKeys}>
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
      {filesHidden ? null : (
        <Splitter
          orientation="vertical"
          value={filesSize}
          {...LIMITS.packFiles}
          direction={1}
          label="Resize the pack files"
          controls="mq-pack-files"
          onChange={(v) => store.setState({ packFilesSize: v })}
        />
      )}
      <section className="flex min-w-60 flex-1 flex-col" aria-label="Template">
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
            {/* Focusable: a scrolling region must be reachable from the keyboard (the editor pane can be narrow). */}
            <pre tabIndex={0} className="h-full overflow-auto font-mono text-11" aria-label={`${path}: on disk (-) and yours (+)`}>
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
              onSave={(text) => void save(undefined, text)}
              markers={markers}
              label={`${pack}/${path}`}
              completion={scriban ? (context.data ?? null) : null}
              onCursorLine={(line) => {
                setCursorLine(line);
                setOutputPick(null);
              }}
              highlightLines={templateHighlight}
            />
          ) : disk.isError ? (
            <EmptyState title={`${path} could not be read`}>{(disk.error as Error).message}</EmptyState>
          ) : (
            <Spinner label={`Loading ${path}`} />
          )}
        </div>
      </section>
      {previewHidden ? (
        <EdgeToggle panel="templatePreview" side="right" />
      ) : (
        <Splitter
          orientation="vertical"
          value={previewSize}
          {...LIMITS.templatePreview}
          direction={-1}
          label="Resize the template preview"
          controls="mq-template-preview"
          onChange={(v) => store.setState({ templatePreviewSize: v })}
        />
      )}
      <PreviewPane
        hidden={previewHidden}
        width={previewSize}
        pack={pack}
        units={units}
        feeding={feeding}
        unit={unit}
        onUnit={setUnitChoice}
        note={note}
        scope={scope}
        onElement={(id) => setElementChoice((prev) => ({ ...prev, [unit]: id }))}
        preview={preview}
        buffers={buffers}
        highlight={outputHighlight}
        onPickLine={scriban ? (file, line) => setOutputPick(outputPick?.file === file && outputPick.line === line ? null : { file, line }) : undefined}
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

/** How many of the unit's elements the scope listing names (the server lists without rendering; at most 2000). */
const LIST_LIMIT = 2000;
/** How many elements "Preview a list…" renders at most (the server's path listing bound too). */
export const LIST_MAX = 20;

interface PreviewState {
  result: PreviewResult | null;
  error: string | null;
  pending: boolean;
  /** The elements of the unit's scope, and the one rendered (null for a model unit or an empty scope). */
  candidates: Candidate[];
  elementId: string | null;
  /** The friendly text when the render ran outside the unit's scope, or the scope has no element. */
  scopeMessage: string | null;
  /**
   * What one render covers when it is more than one element ("the whole model", "a whole database"), else null: such a
   * unit renders only when the user asks (`asked`), never as the template is typed.
   */
  wide: string | null;
  asked: AskedPreview<PreviewResult>;
}

/** Each element of a list preview with what it rendered. */
interface ListedRender {
  id: string;
  label: string;
  result: PreviewResult | null;
  error: string | null;
}

/** What one render of a wide unit covers, in words, from its scope and its elements' kind. */
function wideWords(scope: UnitScope, kind: string | null): string {
  if (scope.once) return "the whole model";
  if (kind === "database" || scope.label === "database") return "a whole database";
  if (kind === "locale" || scope.label === "locale") return "every string of a locale";
  return `a whole ${scope.label}`;
}

/**
 * The preview of the unit with the unsaved text (generation-ui.md 3.3 and 5.2, "Bounds"). An element unit renders ONE
 * element, `PREVIEW_DELAY` ms after the last change; a unit whose one render covers the whole model, a database or a
 * locale renders only when the user asks (`asked.run`), and while the template is typed it renders again by itself
 * only when that render was fast. The picker's elements: an `each` kind's come from the index, narrowed to what the
 * unit renders when the server's scope listing is complete; a table, locale or selector scope's from that listing.
 * The listing renders nothing.
 */
function usePreview(pack: string, unit: string, scope: UnitScope, remembered: string | undefined, buffers: Record<string, Buffer>): PreviewState {
  const index = useIndex();
  const fromListing = !!unit && !scope.once && !scope.indexKind;
  const listing = useQuery({
    queryKey: [...keys.pack(pack), "paths", unit, "saved", LIST_LIMIT],
    queryFn: ({ signal }) => endpoints.unitPaths({ pack, unit, limit: LIST_LIMIT, unitOverride: null }, signal, endpoints.LIVE_CLIENT),
    enabled: !!unit && !scope.once,
    retry: false,
  });
  const elements = useMemo(() => listing.data?.elements ?? [], [listing.data]);
  const planned = useMemo(() => elements.map((e) => e.id), [elements]);
  const inScope = useMemo(() => (listing.data && listing.data.count <= listing.data.elements.length ? new Set(planned) : null), [listing.data, planned]);
  const plannedNames = useMemo(() => new Map(elements.flatMap((e) => (e.name ? [[e.id, e.name] as const] : []))), [elements]);
  const candidates = useMemo(
    () => scopeCandidates(scope, index.data ?? [], fromListing ? planned : [], inScope, plannedNames),
    [scope, index.data, fromListing, planned, inScope, plannedNames],
  );
  const elementId = pickElement(candidates, remembered);
  const overlay = useMemo(() => overlayOf(buffers), [buffers]);
  const wide = scope.once || listing.data?.wide ? wideWords(scope, elements[0]?.kind ?? null) : null;
  const requestKey = JSON.stringify({ pack, unit, elementId, overlay });
  const listed = scope.once || ((!scope.indexKind || !!index.data) && listing.isFetched);
  const empty = listed && !scope.once && !elementId;
  const ready = !!unit && listed && !empty;

  // An element unit: one element, rendered as the template is typed.
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
    if (ready && !wide) scheduler.current?.schedule(requestKey);
  }, [ready, wide, requestKey]);

  // A wide unit: rendered when asked.
  const asked = useAskedPreview<PreviewResult>(ready && wide ? JSON.stringify({ pack, unit, elementId }) : null, JSON.stringify(overlay), async (signal) => {
    const value = await endpoints.previewTemplate({ pack, unit, elementId, overlay }, signal);
    return { value, elapsedMs: value.elapsedMs };
  });

  const shown = wide
    ? { result: asked.result, error: asked.error, current: asked.result !== null || asked.error !== null }
    : { result: answer?.result ?? null, error: answer?.error ?? null, current: answer?.key === requestKey };
  // The last answer stays up while the next one renders (no spinner on every keystroke).
  const mismatch =
    shown.result || shown.error
      ? scopeMismatch(scope, [...(shown.result?.diagnostics ?? []), ...(shown.error ? [{ rule: "MQ6006", message: shown.error }] : [])])
      : null;
  return {
    result: empty ? null : shown.result,
    error: empty ? null : shown.error,
    pending: !empty && (wide ? asked.pending : !ready || answer?.key !== requestKey),
    candidates,
    elementId,
    scopeMessage: empty
      ? `The model has no ${scope.label} to preview this template with.`
      : (mismatch ?? (shown.current ? noFilesText(scope, unit, shown.result) : null)),
    wide,
    asked,
  };
}

/**
 * "Preview a list…": the elements the user ticked (at most `LIST_MAX`), each rendered once when asked, one request after
 * another; the unsaved text changing afterwards re-renders the list by itself only when it rendered fast.
 */
const listTarget = (pack: string, unit: string, ids: string[]) => (ids.length ? JSON.stringify({ pack, unit, ids }) : null);

function useListPreview(pack: string, unit: string, ids: string[], candidates: Candidate[], buffers: Record<string, Buffer>) {
  const overlay = useMemo(() => overlayOf(buffers), [buffers]);
  const labels = useMemo(() => new Map(candidates.map((c) => [c.id, c.label])), [candidates]);
  return useAskedPreview<ListedRender[]>(listTarget(pack, unit, ids), JSON.stringify(overlay), async (signal) => {
    const started = performance.now();
    const out: ListedRender[] = [];
    for (const id of ids) {
      if (signal.aborted) throw new DOMException("Aborted", "AbortError");
      try {
        const result = await endpoints.previewTemplate({ pack, unit, elementId: id, overlay }, signal);
        out.push({ id, label: labels.get(id) ?? id, result, error: null });
      } catch (e) {
        if (signal.aborted) throw e;
        out.push({ id, label: labels.get(id) ?? id, result: null, error: (e as Error).message ?? String(e) });
      }
    }
    return { value: out, elapsedMs: performance.now() - started };
  });
}

function PreviewPane(props: {
  hidden: boolean;
  width: number;
  pack: string;
  units: string[];
  feeding: string[];
  unit: string;
  onUnit(unit: string): void;
  note: string | null;
  scope: UnitScope;
  onElement(id: string): void;
  preview: PreviewState;
  buffers: Record<string, Buffer>;
  /** Output lines matched to the template's cursor line (or the picked line), by file path. */
  highlight: Map<string, Set<number>>;
  onPickLine?: (file: string, line: number) => void;
}) {
  const { units, feeding, unit, preview, scope, highlight } = props;
  const matched = [...highlight.values()].some((s) => s.size > 0);
  const others = units.filter((u) => !feeding.includes(u));
  // "Preview a list…": the picker's open state, the ticked elements, and the list asked for (kept per unit).
  const [picking, setPicking] = useState(false);
  const [ticked, setTicked] = useState<string[]>([]);
  const [listFor, setListFor] = useState<{ unit: string; ids: string[] } | null>(null);
  const listIds = listFor && listFor.unit === unit ? listFor.ids : [];
  const list = useListPreview(props.pack, unit, listIds, preview.candidates, props.buffers);
  const showingList = listIds.length > 0;
  const asked = preview.wide ? preview.asked : null;
  const result = preview.result;
  const canList = !preview.wide && !scope.once && preview.candidates.length > 1;
  return (
    <section
      id="mq-template-preview"
      className={cn("flex min-w-0 flex-col", props.hidden && "hidden")}
      style={{ width: props.width, maxWidth: "60%" }}
      aria-label="Preview"
      data-testid="template-preview"
    >
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
          {scope.once ? "Element" : scope.label.charAt(0).toUpperCase() + scope.label.slice(1)}
          <Select
            className="h-6 min-w-0 flex-1 text-12"
            value={preview.elementId ?? ""}
            onChange={(e) => props.onElement(e.target.value)}
            disabled={!preview.candidates.length || showingList}
            data-testid="preview-element"
          >
            {preview.candidates.length ? null : <option value="">{scope.once ? "None (runs once)" : `No ${scope.label}`}</option>}
            {preview.candidates.map((c) => (
              <option key={c.id} value={c.id}>
                {c.label}
              </option>
            ))}
          </Select>
        </label>
        {canList ? (
          <Button
            size="sm"
            variant="ghost"
            aria-expanded={picking}
            title={`Render several ${scope.label} elements at once (at most ${LIST_MAX}), when you ask`}
            onClick={() => {
              setPicking(!picking);
              if (!picking) setTicked(listIds.length ? listIds : preview.elementId ? [preview.elementId] : []);
            }}
            data-testid="preview-list-open"
          >
            Preview a list…
          </Button>
        ) : null}
        {(showingList ? list.pending : preview.pending) ? (
          <span className="text-11 text-secondary">rendering…</span>
        ) : showingList && list.elapsedMs !== null ? (
          <span className="text-11 text-secondary">{Math.round(list.elapsedMs)} ms</span>
        ) : result ? (
          <span className="text-11 text-secondary">{result.elapsedMs} ms</span>
        ) : null}
        {matched ? (
          <span
            className="text-11 text-secondary"
            title="The template engine reports no output positions: lines are matched by the template line's literal text, so a line of code only matches nothing."
            data-testid="preview-match-note"
          >
            lines matched by text (approximate)
          </span>
        ) : null}
        {props.hidden ? null : <PanelToggle panel="templatePreview" className="ml-auto" />}
      </Toolbar>
      {picking && canList ? (
        <ListPicker
          candidates={preview.candidates}
          label={scope.label}
          ticked={ticked}
          onTicked={setTicked}
          onCancel={() => setPicking(false)}
          onRun={() => {
            setPicking(false);
            const ids = ticked.slice(0, LIST_MAX);
            setListFor({ unit, ids });
            // Rendered once the list is the hook's target (the next render), and again when the same list is asked again.
            list.runWhenReady(listTarget(props.pack, unit, ids)!);
          }}
        />
      ) : null}
      {props.note ? (
        <p className="border-b border-default px-2 py-0.5 text-11 text-secondary" data-testid="preview-note">
          {props.note}
        </p>
      ) : null}
      {preview.scopeMessage && !showingList ? (
        <p role="status" className="border-b border-default px-2 py-0.5 text-12 text-warning" data-testid="preview-scope">
          {preview.scopeMessage}
        </p>
      ) : null}
      {asked?.stale || (showingList && list.stale) ? (
        <p role="status" className="flex items-center gap-2 border-b border-default px-2 py-0.5 text-12 text-warning" data-testid="preview-stale">
          <span className="min-w-0 flex-1">
            Out of date: the text changed after this render, which took {Math.round((showingList ? list.elapsedMs : asked?.elapsedMs) ?? 0)} ms, so it does not
            render again as you type.
          </span>
          <Button size="sm" onClick={() => (showingList ? list.run() : asked?.run())} data-testid="preview-again">
            Preview again
          </Button>
        </p>
      ) : null}
      {showingList ? (
        <div className="flex h-6 shrink-0 items-center gap-2 border-b border-default px-2 text-11 text-secondary" data-testid="preview-list-header">
          <span className="min-w-0 flex-1 truncate">
            {listIds.length} {scope.label} elements, rendered when you asked
          </span>
          <Button size="sm" variant="ghost" onClick={() => setListFor(null)} data-testid="preview-list-close">
            Back to one {scope.label}
          </Button>
        </div>
      ) : null}
      {result?.diagnostics.length && !preview.scopeMessage && !showingList ? (
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
      {/* Focusable: the rendered text scrolls both ways in a narrow pane, and a scrolling region needs the keyboard. */}
      <div className="min-h-0 flex-1 overflow-auto" role="region" aria-label="Rendered output" tabIndex={0}>
        {!unit ? (
          <EmptyState title="No unit to preview">Add a unit on the Units tab; it names the template it runs.</EmptyState>
        ) : showingList ? (
          !list.result ? (
            <Spinner label="Rendering the list" />
          ) : (
            list.result.map((item) => (
              <div key={item.id} data-testid="preview-list-item">
                <div className="sticky top-0 flex h-6 items-center gap-1 border-b border-default bg-accent-subtle px-2 text-11 font-medium" title={item.id}>
                  {item.label}
                </div>
                {item.error ? (
                  <p role="alert" className="px-2 py-1 text-12 text-danger">
                    {item.error}
                  </p>
                ) : item.result?.diagnostics.some((d) => d.severity === "error") ? (
                  <ul className="px-2 py-1 text-11 text-danger">
                    {item.result.diagnostics
                      .filter((d) => d.severity === "error")
                      .map((d, i) => (
                        <li key={i}>
                          {d.rule} {d.message}
                        </li>
                      ))}
                  </ul>
                ) : null}
                {(item.result?.files ?? []).map((f) => (
                  <RenderedFile key={f.path} file={f} />
                ))}
              </div>
            ))
          )
        ) : preview.wide && !preview.asked.asked && !preview.scopeMessage ? (
          <div className="flex flex-col items-start gap-2 px-2 py-2 text-12" data-testid="preview-wide-note">
            <p className="text-secondary">
              Unit {unit} renders {preview.wide} in one go, so it is not rendered as you type. Preview it when you want to see it.
            </p>
            <Button size="sm" variant="primary" onClick={() => preview.asked.run()} data-testid="preview-wide">
              Preview (renders {preview.wide})
            </Button>
          </div>
        ) : preview.scopeMessage ? null : preview.error ? (
          <p role="alert" className="px-2 py-1 text-12 text-danger">
            {preview.error}
          </p>
        ) : !result ? (
          <Spinner label="Rendering" />
        ) : !result.files.length ? (
          <p className="px-2 py-1 text-12 text-secondary">The render wrote no file.</p>
        ) : (
          result.files.map((f) => <RenderedFile key={f.path} file={f} highlight={highlight.get(f.path)} onPickLine={props.onPickLine} />)
        )}
      </div>
    </section>
  );
}

function RenderedFile({
  file,
  highlight,
  onPickLine,
}: {
  file: PreviewResult["files"][number];
  highlight?: Set<number>;
  onPickLine?: (file: string, line: number) => void;
}) {
  return (
    <div>
      <div className="sticky top-0 flex h-6 items-center gap-1 border-b border-default bg-surface px-2 text-11">
        <span className="min-w-0 truncate font-mono" data-testid="preview-path" title={file.path}>
          {file.path}
        </span>
        {file.role !== "main" ? <span className="text-secondary">{file.role}</span> : null}
      </div>
      <pre className="py-1 font-mono text-11 leading-[16px] whitespace-pre" data-testid="preview-text">
        {file.text.split("\n").map((line, i) => {
          const on = highlight?.has(i + 1) ?? false;
          return (
            <div
              key={i}
              className={cn("px-2", on && "bg-accent-subtle", onPickLine && "cursor-pointer hover:bg-accent-subtle")}
              data-line={i + 1}
              data-match={on ? "true" : undefined}
              onClick={onPickLine ? () => onPickLine(file.path, i + 1) : undefined}
            >
              {line || "​"}
            </div>
          );
        })}
      </pre>
    </div>
  );
}

/** The "Preview a list…" picker: a filter box over the unit's elements, at most `LIST_MAX` ticked, and Preview. */
function ListPicker(props: { candidates: Candidate[]; label: string; ticked: string[]; onTicked(ids: string[]): void; onRun(): void; onCancel(): void }) {
  const [text, setText] = useState("");
  const words = text.toLowerCase().split(/\s+/).filter(Boolean);
  const matches = props.candidates.filter((c) => words.every((w) => `${c.label} ${c.id}`.toLowerCase().includes(w)));
  const shown = matches.slice(0, 200);
  const full = props.ticked.length >= LIST_MAX;
  const toggle = (id: string, on: boolean) => props.onTicked(on ? [...props.ticked, id].slice(0, LIST_MAX) : props.ticked.filter((x) => x !== id));
  return (
    <div className="flex max-h-64 shrink-0 flex-col gap-1 border-b border-default p-1 text-12" data-testid="preview-list-picker">
      <div className="flex items-center gap-1">
        <Input
          autoFocus
          className="h-6 flex-1 text-12"
          aria-label={`Filter the ${props.label} elements`}
          placeholder={`Filter ${props.candidates.length} elements`}
          value={text}
          onChange={(e) => setText(e.target.value)}
          onKeyDown={(e) => {
            if (e.key === "Escape") props.onCancel();
          }}
        />
        <span className="text-11 text-secondary" data-testid="preview-list-count">
          {props.ticked.length} of at most {LIST_MAX}
        </span>
      </div>
      <ul className="min-h-0 flex-1 overflow-auto" aria-label={`${props.label} elements to preview`}>
        {shown.map((c) => {
          const on = props.ticked.includes(c.id);
          return (
            <li key={c.id}>
              <label className="flex h-6 items-center gap-1 px-1 hover:bg-accent-subtle" title={c.id}>
                <input type="checkbox" checked={on} disabled={!on && full} onChange={(e) => toggle(c.id, e.target.checked)} data-testid="preview-list-option" />
                <span className="min-w-0 truncate">{c.label}</span>
              </label>
            </li>
          );
        })}
        {matches.length > shown.length ? <li className="px-1 text-11 text-secondary">{matches.length - shown.length} more: type to narrow</li> : null}
      </ul>
      <div className="flex gap-1">
        <Button size="sm" variant="primary" disabled={!props.ticked.length} onClick={props.onRun} data-testid="preview-list-run">
          Preview {props.ticked.length} {props.ticked.length === 1 ? "element" : "elements"}
        </Button>
        <Button size="sm" variant="ghost" onClick={props.onCancel}>
          Cancel
        </Button>
      </div>
    </div>
  );
}
