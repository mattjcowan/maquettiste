// The Extensions tab of the Generate screen: the model's custom property schemas (extensions/<name>.json) and script rules
// (extensions/rules/<name>.js) on the left, the file in the code editor in the middle, and below it the file's Problems: what
// the engine says about the file alone (an invalid schema, a rule that does not load, with line and column), what the last
// save answered, the editor's own live checks (a schema is validated against extension.json as it is typed) and, for a
// rule, the findings it reports on the model. Save writes the file through the engine's write guard with its hash
// (If-Match); a 409 shows a bar to keep mine or take theirs. File saves are not undo steps, as on the Templates tab.
import { useEffect, useMemo, useRef, useState, type KeyboardEvent } from "react";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { CircleAlert, FileJson, FilePlus, Info, Pencil, Save, ScrollText, Trash2, TriangleAlert } from "lucide-react";
import * as endpoints from "@/api/endpoints";
import { keys, useExtensionFiles, useIndex, useValidation } from "@/api/queries";
import type { Diagnostic } from "@/api/types";
import { CodeView } from "@/code";
import type { CodeMarker } from "@/code/CodeEditor";
import extensionSchema from "@/code/extensionSchema.json";
import { useServices } from "@/app/context";
import { useEditorNavigation } from "@/app/navigation";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { EmptyState, Spinner, Toolbar } from "@/components/ui/misc";
import { cn } from "@/lib/cn";
import { indexLookup } from "@/model/index";
import { displayName } from "@/model/model";
import { useEditor } from "@/state/store";
import { markUnsaved, useDraftState } from "./drafts";
import {
  extensionPathProblem,
  findingsFor,
  freePath,
  KIND_LABEL,
  kindOfPath,
  ruleFindings,
  ruleTemplate,
  schemaTemplate,
  type ExtensionFile,
  type ExtensionKind,
} from "./extensionsModel";
import { EXTENSIONS_TAB } from "./packTabs";
import { isDirty, lineDiff, type Buffer } from "./templatesModel";

interface Conflict {
  path: string;
  hash: string | null;
  current: string | null;
}

type FileAction = { kind: "new"; of: ExtensionKind; value: string } | { kind: "rename"; value: string } | { kind: "delete"; value: string };

const DRAFTS = `${EXTENSIONS_TAB}:files`;
/** The `$schema` a canonical extension file carries, relative to extensions/. */
const SCHEMA_REF = "../.schema/v1/extension.json";
const JSON_SCHEMA = { ref: SCHEMA_REF, schema: extensionSchema };
const ICON = { error: CircleAlert, warning: TriangleAlert, info: Info } as const;
const TONE = { error: "text-danger", warning: "text-warning", info: "text-accent" } as const;

export function ExtensionsScreen() {
  const { store } = useServices();
  const qc = useQueryClient();
  const list = useExtensionFiles();
  const validation = useValidation();
  const files = useMemo(() => list.data?.files ?? [], [list.data]);
  const folder = list.data?.folder ?? ".maquettiste/extensions";
  const focus = useEditor(store, (s) => s.generation.extensionFile);
  const asked = useEditor(store, (s) => s.generation.extensionNew);
  const [selected, setSelected] = useState<string | null>(focus);
  const [seenFocus, setSeenFocus] = useState(focus);
  if (focus !== seenFocus) {
    setSeenFocus(focus);
    if (focus) setSelected(focus);
  }
  const path = selected && files.some((f) => f.path === selected) ? selected : (files[0]?.path ?? null);
  const info = files.find((f) => f.path === path);
  const kind = path ? kindOfPath(path) : null;

  const [buffers, setBuffers] = useDraftState<Record<string, Buffer>>(`${DRAFTS}:buffers`, {});
  const [conflicts, setConflicts] = useDraftState<Record<string, Conflict>>(`${DRAFTS}:conflicts`, {});
  const [revisions, setRevisions] = useDraftState<Record<string, number>>(`${DRAFTS}:revisions`, {});
  const [saved, setSaved] = useState<Record<string, Diagnostic[]>>({});
  const [live, setLive] = useState<CodeMarker[]>([]);
  const [busy, setBusy] = useState(false);
  const [comparing, setComparing] = useState(false);
  const [notice, setNotice] = useState<string | null>(null);
  const ours = useRef(new Map<string, Set<string>>());
  const conflict = path ? (conflicts[path] ?? null) : null;
  const setConflictFor = (p: string, c: Conflict | null) =>
    setConflicts((prev) => {
      const next = { ...prev };
      if (c) next[p] = c;
      else delete next[p];
      return next;
    });

  const disk = useQuery({ queryKey: keys.extensionFile(path ?? ""), queryFn: () => endpoints.getExtensionFile(path!), enabled: !!path, retry: false });
  const buffer: Buffer | undefined =
    (path && buffers[path]) || (disk.data && disk.data.path === path ? { text: disk.data.text, base: disk.data.text, hash: disk.data.hash } : undefined);
  const dirtyPaths = useMemo(() => new Set(Object.keys(buffers).filter((p) => isDirty(buffers[p]))), [buffers]);
  useEffect(() => markUnsaved(DRAFTS, dirtyPaths.size > 0), [dirtyPaths]);

  // A change on disk (an agent, the command line, git): an unedited buffer takes the new text; an edited one shows the
  // conflict bar now rather than on its next save.
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

  const refresh = async () => {
    await Promise.all([
      qc.invalidateQueries({ queryKey: keys.extensions }),
      // The project carries the extension schemas the inspector renders; validation runs the rules again.
      qc.invalidateQueries({ queryKey: keys.project }),
      qc.invalidateQueries({ queryKey: keys.validation, exact: true }),
    ]);
  };

  const edit = (text: string) => {
    if (!path || !buffer) return;
    setBuffers((prev) => ({ ...prev, [path]: { ...(prev[path] ?? buffer), text } }));
  };

  // `latest` is the editor's text at the keystroke (Ctrl+S in the code editor): the state's copy may lag it by a render.
  const save = async (overHash?: string | null, latest?: string) => {
    if (!path || !buffer) return;
    if (busyRef.current) {
      // A save asked while one is in flight (Ctrl+S twice in a row) runs right after it instead of being dropped.
      queued.current = true;
      return;
    }
    const text = latest ?? buffer.text;
    if (overHash === undefined && !isDirty({ ...buffer, text })) return;
    if (text !== buffer.text) setBuffers((prev) => ({ ...prev, [path]: { ...(prev[path] ?? buffer), text } }));
    busyRef.current = true;
    setBusy(true);
    try {
      const result = await endpoints.saveExtensionFile(path, text, overHash === undefined ? buffer.hash : overHash);
      setSaved((prev) => ({ ...prev, [path]: result.diagnostics }));
      if (result.outcome === "conflict") {
        setConflictFor(path, { path, hash: result.hash, current: result.current });
        return;
      }
      if (result.outcome !== "saved" || !result.hash) {
        setNotice(result.outcome === "invalid" ? "Not saved: the file is not valid. Its problems are listed below." : `Not saved (${result.outcome}).`);
        return;
      }
      const hash = result.hash;
      ours.current.set(path, new Set([...(ours.current.get(path) ?? []), hash]));
      setConflictFor(path, null);
      setNotice(null);
      // A schema comes back in canonical form: the editor shows what was written.
      const written = result.text ?? text;
      setBuffers((prev) => ({ ...prev, [path]: { text: written === text ? (prev[path]?.text ?? text) : written, base: written, hash } }));
      if (written !== text) setRevisions((prev) => ({ ...prev, [path]: (prev[path] ?? 0) + 1 }));
      await refresh();
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

  // New, Rename and Delete: each one change on disk through the extension file API.
  const [action, setAction] = useState<FileAction | null>(null);
  const [actionError, setActionError] = useState<string | null>(null);
  const taken = files.map((f) => f.path);
  const startNew = (of: ExtensionKind) => {
    setActionError(null);
    setAction({ kind: "new", of, value: freePath(of, taken) });
  };
  useEffect(() => {
    if (!asked) return;
    store.getState().setGeneration({ extensionNew: null });
    startNew(asked);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [asked]);
  const runAction = async () => {
    if (!action || busy) return;
    const target = action.value.trim();
    if (action.kind === "new") {
      const problem = extensionPathProblem(target, action.of, taken);
      if (problem) return setActionError(problem);
    } else if (action.kind === "rename") {
      if (!path || !buffer) return;
      if (target === path) return setAction(null);
      const problem = extensionPathProblem(target, kindOfPath(path), taken);
      if (problem) return setActionError(problem);
      if (isDirty(buffer)) return setActionError("Save the file before renaming it.");
    }
    setBusy(true);
    try {
      const answer =
        action.kind === "new"
          ? await endpoints.saveExtensionFile(target, action.of === "rule" ? ruleTemplate(target) : schemaTemplate(target), null)
          : action.kind === "rename"
            ? await endpoints.moveExtensionFile(path!, target, buffer!.hash)
            : await endpoints.deleteExtensionFile(path!, buffer!.hash);
      if (answer.outcome !== "saved") {
        const what = action.kind === "delete" ? `${path} was not deleted` : action.kind === "rename" ? `${path} was not renamed` : `${target} was not created`;
        return setActionError(
          answer.outcome === "conflict"
            ? `${what}: it changed on disk; reload and try again.`
            : `${what}: ${answer.diagnostics.map((d) => d.message).join(" ") || answer.outcome}`,
        );
      }
      const gone = action.kind !== "new" ? path : null;
      if (gone) {
        setBuffers((prev) => Object.fromEntries(Object.entries(prev).filter(([k]) => k !== gone)));
        setConflictFor(gone, null);
        qc.removeQueries({ queryKey: keys.extensionFile(gone), exact: true });
      }
      if (action.kind === "new") setSaved((prev) => ({ ...prev, [target]: answer.diagnostics }));
      setSelected(action.kind === "delete" ? null : target);
      store.getState().setGeneration({ extensionFile: action.kind === "delete" ? null : target });
      setAction(null);
      setActionError(null);
      await refresh();
    } finally {
      setBusy(false);
    }
  };

  const onKeyDown = (e: KeyboardEvent) => {
    if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === "s") {
      e.preventDefault();
      void save();
    }
  };

  const report = useMemo(() => validation.data?.diagnostics ?? [], [validation.data]);
  const savedFindings = path ? saved[path] : undefined;
  const findings = useMemo(
    () => (path ? findingsFor(path, folder, { listed: info?.diagnostics, saved: savedFindings, report }) : []),
    [path, folder, info, savedFindings, report],
  );
  const markers = useMemo<CodeMarker[]>(
    () =>
      findings.flatMap((d) =>
        d.line ? [{ line: d.line, column: d.column, message: `${d.rule} ${d.message}`, severity: d.severity === "info" ? "info" : d.severity }] : [],
      ),
    [findings],
  );
  const reported = useMemo(() => (info && kind === "rule" ? ruleFindings(info, report) : []), [info, kind, report]);

  if (list.isPending) return <Spinner label="Loading the extension files" />;
  return (
    <div className="flex h-full min-h-0" data-testid="extensions-screen" onKeyDown={onKeyDown}>
      <nav aria-label="Extension files" className="flex w-56 shrink-0 flex-col overflow-auto border-r border-default text-12" data-testid="extension-files">
        <div className="flex h-6 shrink-0 items-center gap-0.5 border-b border-default px-1" role="toolbar" aria-label="Extension file actions">
          <Button size="icon-row" variant="ghost" label="New property schema…" onClick={() => startNew("schema")} data-testid="extension-new-schema">
            <FilePlus />
          </Button>
          <Button size="icon-row" variant="ghost" label="New script rule…" onClick={() => startNew("rule")} data-testid="extension-new-rule">
            <ScrollText />
          </Button>
          <Button
            size="icon-row"
            variant="ghost"
            label="Rename…"
            disabled={!path}
            onClick={() => {
              setActionError(null);
              setAction({ kind: "rename", value: path ?? "" });
            }}
            data-testid="extension-rename"
          >
            <Pencil />
          </Button>
          <Button
            size="icon-row"
            variant="ghost"
            label="Delete…"
            disabled={!path}
            onClick={() => {
              setActionError(null);
              setAction({ kind: "delete", value: path ?? "" });
            }}
            data-testid="extension-delete"
          >
            <Trash2 />
          </Button>
        </div>
        {action ? (
          <form
            className="flex shrink-0 flex-col gap-0.5 border-b border-default p-1"
            data-testid="extension-file-action"
            onSubmit={(e) => {
              e.preventDefault();
              void runAction();
            }}
          >
            {action.kind === "delete" ? (
              <span className="text-11">
                Delete <span className="font-mono">{path}</span>? This cannot be undone from the editor.
              </span>
            ) : (
              <Input
                autoFocus
                className="h-6 font-mono text-11"
                aria-label={action.kind === "new" ? (action.of === "rule" ? "New script rule path" : "New property schema path") : "New path"}
                value={action.value}
                onChange={(e) => setAction({ ...action, value: e.target.value })}
                onKeyDown={(e) => {
                  if (e.key === "Escape") setAction(null);
                }}
              />
            )}
            {actionError ? (
              <span role="alert" className="text-11 text-danger" data-testid="extension-file-error">
                {actionError}
              </span>
            ) : null}
            <span className="flex gap-1">
              <Button size="sm" type="submit" variant={action.kind === "delete" ? "danger" : "primary"} disabled={busy}>
                {action.kind === "new" ? "Create" : action.kind === "rename" ? "Rename" : "Delete"}
              </Button>
              <Button size="sm" variant="ghost" type="button" onClick={() => setAction(null)}>
                Cancel
              </Button>
            </span>
          </form>
        ) : null}
        <FileGroup title="Custom properties" files={files.filter((f) => f.kind === "schema")} path={path} dirty={dirtyPaths} onPick={setSelected} />
        <FileGroup title="Script rules" files={files.filter((f) => f.kind === "rule")} path={path} dirty={dirtyPaths} onPick={setSelected} />
      </nav>
      <section className="flex min-w-60 flex-1 flex-col" aria-label="Extension file">
        {!path ? (
          <EmptyState title="No extension files">
            Custom property schemas add fields to the inspector; script rules add your own checks to validation. Create one with New property schema… or New
            script rule….
          </EmptyState>
        ) : (
          <>
            <Toolbar label="Extension file" className="gap-2 px-1 text-12">
              <span className="min-w-0 truncate font-mono text-11" data-testid="extension-path">
                {path}
                {isDirty(buffer) ? <span aria-label="unsaved"> •</span> : null}
              </span>
              <span className="truncate text-11 text-secondary">
                {kind ? KIND_LABEL[kind] : ""}
                {info?.rules.length ? `: ${info.rules.map((r) => `${r.id} (${r.severity})`).join(", ")}` : ""}
              </span>
              <span className="flex-1" />
              <span
                className="truncate text-11 text-secondary"
                title="Saving writes the file under .maquettiste/extensions/ at once. Undo and Redo cover model edits, not file saves."
              >
                Saves write the file; they are not undo steps.
              </span>
              <Button
                size="sm"
                variant="primary"
                disabled={!isDirty(buffer) || busy}
                onClick={() => void save()}
                title="Save (Ctrl+S)"
                data-testid="extension-save"
              >
                <Save className="size-3.5" /> Save
              </Button>
            </Toolbar>
            {conflict && conflict.path === path ? (
              <div
                role="alert"
                className="flex min-w-0 flex-wrap items-center gap-x-2 gap-y-0.5 border-b border-default bg-surface px-2 py-0.5 text-12 text-warning"
                data-testid="extension-conflict"
              >
                {conflict.current === null ? `${path} was deleted on disk.` : `${path} changed on disk since you opened it.`}
                <Button size="sm" onClick={() => void save(conflict.hash)}>
                  Keep mine
                </Button>
                <Button size="sm" variant="ghost" onClick={takeTheirs}>
                  Take theirs
                </Button>
                {conflict.current !== null ? (
                  <Button size="sm" variant="ghost" aria-pressed={comparing} onClick={() => setComparing(!comparing)}>
                    Compare
                  </Button>
                ) : null}
              </div>
            ) : null}
            {conflict && conflict.current !== null && comparing && buffer ? (
              <div className="h-40 shrink-0 border-b border-default">
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
              <div role="alert" className="border-b border-default px-2 py-0.5 text-12 text-danger" data-testid="extension-notice">
                {notice}
              </div>
            ) : null}
            <div className="min-h-0 flex-1">
              {buffer ? (
                <CodeView
                  language={kind === "rule" ? "javascript" : "json"}
                  path={`extensions/${path}`}
                  value={buffer.text}
                  revision={revisions[path] ?? 0}
                  onChange={edit}
                  onSave={(text) => void save(undefined, text)}
                  markers={markers}
                  label={`extensions/${path}`}
                  jsonSchema={kind === "schema" ? JSON_SCHEMA : null}
                  onMarkers={setLive}
                />
              ) : disk.isError ? (
                <EmptyState title={`${path} could not be read`}>{(disk.error as Error).message}</EmptyState>
              ) : (
                <Spinner label={`Loading ${path}`} />
              )}
            </div>
            <ProblemsStrip findings={findings} live={kind === "schema" ? live : []} reported={reported} />
          </>
        )}
      </section>
    </div>
  );
}

function FileGroup({
  title,
  files,
  path,
  dirty,
  onPick,
}: {
  title: string;
  files: ExtensionFile[];
  path: string | null;
  dirty: Set<string>;
  onPick: (path: string) => void;
}) {
  return (
    <div className="py-0.5">
      <h3 className="flex h-6 items-center px-2 text-11 font-semibold uppercase tracking-wide text-secondary">{title}</h3>
      {files.length ? (
        <ul>
          {files.map((f) => (
            <li key={f.path}>
              <button
                type="button"
                className={cn(
                  "flex h-6 w-full items-center gap-1 pr-1 pl-2 text-left hover:bg-accent-subtle",
                  f.path === path && "bg-accent-subtle font-medium",
                )}
                aria-current={f.path === path ? "true" : undefined}
                title={[f.path, ...f.rules.map((r) => r.id)].join("\n")}
                onClick={() => onPick(f.path)}
                data-testid="extension-file"
              >
                {f.kind === "rule" ? (
                  <ScrollText className="size-3.5 shrink-0 text-secondary" aria-hidden />
                ) : (
                  <FileJson className="size-3.5 shrink-0 text-secondary" aria-hidden />
                )}
                <span className="min-w-0 flex-1 truncate font-mono text-11">{f.path.replace(/^rules\//, "")}</span>
                {dirty.has(f.path) ? (
                  <span aria-label="unsaved" className="text-accent">
                    •
                  </span>
                ) : null}
                {f.diagnostics.length ? (
                  <span className="flex items-center gap-0.5 text-11 text-warning" aria-label={`${f.diagnostics.length} problems`}>
                    <TriangleAlert className="size-3" aria-hidden />
                    {f.diagnostics.length}
                  </span>
                ) : null}
              </button>
            </li>
          ))}
        </ul>
      ) : (
        <p className="px-2 text-11 text-secondary">None yet.</p>
      )}
    </div>
  );
}

/**
 * The file's Problems: the engine's findings about the file (with line and column), the editor's live checks of a schema,
 * and, for a rule, what it reports on the model (each opens the element).
 */
function ProblemsStrip({ findings, live, reported }: { findings: Diagnostic[]; live: CodeMarker[]; reported: Diagnostic[] }) {
  const index = useIndex();
  const lookup = indexLookup(index.data);
  const { reveal } = useEditorNavigation();
  const total = findings.length + live.length;
  return (
    <section aria-label="Problems in this file" className="flex max-h-48 shrink-0 flex-col border-t border-default" data-testid="extension-problems">
      <h3 className="flex h-6 shrink-0 items-center gap-2 px-2 text-12 font-semibold">
        Problems <span className="font-normal text-secondary">{total ? total : "none"}</span>
        {reported.length ? (
          <span className="font-normal text-secondary" data-testid="extension-rule-count">
            · this file&apos;s rules report {reported.length} {reported.length === 1 ? "finding" : "findings"}
          </span>
        ) : null}
      </h3>
      <ul className="min-h-0 overflow-auto text-12">
        {findings.map((d, i) => {
          const Icon = ICON[d.severity];
          return (
            <li key={`f${i}`} className="flex items-start gap-2 px-2 py-0.5" data-testid="extension-problem">
              <Icon className={`mt-0.5 size-3.5 shrink-0 ${TONE[d.severity]}`} aria-label={d.severity} />
              <span className="font-mono text-secondary">{d.rule}</span>
              <span className="flex-1">{d.message}</span>
              {d.line ? <span className="font-mono text-11 text-secondary">{`${d.line}:${d.column ?? 1}`}</span> : null}
            </li>
          );
        })}
        {live.map((m, i) => {
          const Icon = ICON[m.severity];
          return (
            <li key={`l${i}`} className="flex items-start gap-2 px-2 py-0.5" data-testid="extension-live-problem">
              <Icon className={`mt-0.5 size-3.5 shrink-0 ${TONE[m.severity]}`} aria-label={m.severity} />
              <span className="font-mono text-secondary">schema</span>
              <span className="flex-1">{m.message}</span>
              <span className="font-mono text-11 text-secondary">{`${m.line}:${m.column ?? 1}`}</span>
            </li>
          );
        })}
        {reported.slice(0, 50).map((d, i) => {
          const Icon = ICON[d.severity];
          const summary = d.elementId ? lookup.byId.get(d.elementId) : undefined;
          return (
            <li key={`r${i}`}>
              <button
                type="button"
                className="flex w-full items-start gap-2 px-2 py-0.5 text-left hover:bg-accent-subtle"
                onClick={() => (summary ? reveal(summary, d.jsonPointer) : undefined)}
                data-testid="extension-rule-finding"
              >
                <Icon className={`mt-0.5 size-3.5 shrink-0 ${TONE[d.severity]}`} aria-label={d.severity} />
                <span className="font-mono text-secondary">{d.rule}</span>
                <span className="shrink-0 font-medium">{summary ? displayName(summary) : (d.elementId ?? "")}</span>
                <span className="flex-1">{d.message}</span>
              </button>
            </li>
          );
        })}
      </ul>
    </section>
  );
}
