// The Templates tab's pure parts (generation-ui.md 3.3): the file tree over the pack folder, the editor language of a
// file, the units a file feeds (directly, as a companion, through includes, or every unit for a script), the overlay of
// unsaved text sent to the preview, the diagnostics that belong to a file, and the preview scheduler (debounced, one
// request in flight, stale answers dropped).
import type { components } from "@/api/schema";

type S = components["schemas"];
export type PackFileInfo = S["PackFileInfo"];
export type Diagnostic = S["Diagnostic"];

export interface TreeRow {
  /** Folder rows end with `/`. */
  path: string;
  name: string;
  depth: number;
  folder: boolean;
  file?: PackFileInfo;
}

/** Folders first at each level, then files, both ordinal; `pack.json` is left out (the Units tab edits it). */
export function fileTree(files: PackFileInfo[]): TreeRow[] {
  interface Node {
    folders: Map<string, Node>;
    files: PackFileInfo[];
  }
  const root: Node = { folders: new Map(), files: [] };
  for (const f of files) {
    if (f.role === "manifest") continue;
    const parts = f.path.split("/");
    let node = root;
    for (const part of parts.slice(0, -1)) {
      if (!node.folders.has(part)) node.folders.set(part, { folders: new Map(), files: [] });
      node = node.folders.get(part)!;
    }
    node.files.push(f);
  }
  const rows: TreeRow[] = [];
  const walk = (node: Node, prefix: string, depth: number) => {
    for (const name of [...node.folders.keys()].sort()) {
      rows.push({ path: `${prefix}${name}/`, name, depth, folder: true });
      walk(node.folders.get(name)!, `${prefix}${name}/`, depth + 1);
    }
    for (const f of [...node.files].sort((a, b) => (a.path < b.path ? -1 : a.path > b.path ? 1 : 0)))
      rows.push({ path: f.path, name: f.path.slice(prefix.length), depth, folder: false, file: f });
  };
  walk(root, "", 0);
  return rows;
}

export type TemplateLanguage = "scriban" | "javascript" | "json" | "markdown" | "sql" | "csharp" | "plaintext";

export function languageOf(path: string): TemplateLanguage {
  const ext = path.slice(path.lastIndexOf(".") + 1).toLowerCase();
  if (ext === "scriban" || ext === "sbn" || ext === "sbn-txt") return "scriban";
  if (ext === "js" || ext === "mjs") return "javascript";
  if (ext === "json") return "json";
  if (ext === "md") return "markdown";
  if (ext === "sql") return "sql";
  if (ext === "cs") return "csharp";
  return "plaintext";
}

/**
 * The unit ids a file feeds, ordinal: the units naming it (as template or companion) and, through `include:` edges, the
 * units of every template that includes it. A script feeds every unit.
 */
export function unitsForFile(files: PackFileInfo[], allUnits: string[], path: string): string[] {
  const byPath = new Map(files.map((f) => [f.path, f]));
  const file = byPath.get(path);
  if (!file) return [];
  if (file.role === "script") return [...allUnits].sort();
  const found = new Set<string>();
  const seen = new Set<string>();
  const visit = (p: string) => {
    if (seen.has(p)) return;
    seen.add(p);
    for (const use of byPath.get(p)?.usedBy ?? []) {
      const [kind, ...rest] = use.split(":");
      const target = rest.join(":");
      if (kind === "unit" || kind === "companion") found.add(target);
      else if (kind === "include") visit(target);
    }
  };
  visit(path);
  return [...found].sort();
}

export interface Buffer {
  /** The text in the editor. */
  text: string;
  /** The text last read or saved, and its hash (the If-Match of the next save). */
  base: string;
  hash: string;
}

export const isDirty = (b: Buffer | undefined): boolean => !!b && b.text !== b.base;

/** Unsaved text by pack-relative path (never pack.json), or null when nothing is unsaved. */
export function overlayOf(buffers: Record<string, Buffer>): Record<string, string> | null {
  const out: Record<string, string> = {};
  for (const path of Object.keys(buffers).sort()) if (path !== "pack.json" && isDirty(buffers[path])) out[path] = buffers[path].text;
  return Object.keys(out).length ? out : null;
}

/** The diagnostics located in this pack file (repo path `.maquettiste/templates/<pack>/<path>`) with a line. */
export function diagnosticsFor(diagnostics: Diagnostic[], pack: string, path: string): Diagnostic[] {
  const suffix = `templates/${pack}/${path}`;
  return diagnostics.filter((d) => !!d.filePath && (d.filePath === path || d.filePath.endsWith(`/${suffix}`) || d.filePath === suffix));
}

/**
 * Runs the latest scheduled request `delay` ms after the last schedule call; a newer request aborts the one in
 * flight, and an answer that is no longer the latest is dropped.
 */
export class PreviewScheduler<Req, Res> {
  private timer: ReturnType<typeof setTimeout> | null = null;
  private controller: AbortController | null = null;
  private seq = 0;

  constructor(
    private readonly run: (request: Req, signal: AbortSignal) => Promise<Res>,
    private readonly onResult: (result: { ok: true; value: Res } | { ok: false; error: unknown }, request: Req) => void,
    private readonly delay = 300,
  ) {}

  schedule(request: Req): void {
    if (this.timer) clearTimeout(this.timer);
    const seq = ++this.seq;
    this.timer = setTimeout(() => {
      this.timer = null;
      this.controller?.abort();
      const controller = new AbortController();
      this.controller = controller;
      this.run(request, controller.signal).then(
        (value) => {
          if (seq === this.seq) this.onResult({ ok: true, value }, request);
        },
        (error: unknown) => {
          if (seq === this.seq && !controller.signal.aborted) this.onResult({ ok: false, error }, request);
        },
      );
    }, this.delay);
  }

  dispose(): void {
    if (this.timer) clearTimeout(this.timer);
    this.controller?.abort();
    this.seq++;
  }
}

/** A line diff of the text on disk (`-`) against the buffer (`+`), unchanged lines as ` `; for the 409 bar's Compare. */
export function lineDiff(theirs: string, mine: string): { op: " " | "-" | "+"; text: string }[] {
  const a = theirs.split("\n");
  const b = mine.split("\n");
  // Too large for the quadratic table: show both sides whole.
  if (a.length * b.length > 4_000_000) return [...a.map((text) => ({ op: "-" as const, text })), ...b.map((text) => ({ op: "+" as const, text }))];
  const lcs: number[][] = Array.from({ length: a.length + 1 }, () => new Array<number>(b.length + 1).fill(0));
  for (let i = a.length - 1; i >= 0; i--)
    for (let j = b.length - 1; j >= 0; j--) lcs[i][j] = a[i] === b[j] ? lcs[i + 1][j + 1] + 1 : Math.max(lcs[i + 1][j], lcs[i][j + 1]);
  const out: { op: " " | "-" | "+"; text: string }[] = [];
  let i = 0;
  let j = 0;
  while (i < a.length && j < b.length) {
    if (a[i] === b[j]) {
      out.push({ op: " ", text: a[i++] });
      j++;
    } else if (lcs[i + 1][j] >= lcs[i][j + 1]) out.push({ op: "-", text: a[i++] });
    else out.push({ op: "+", text: b[j++] });
  }
  while (i < a.length) out.push({ op: "-", text: a[i++] });
  while (j < b.length) out.push({ op: "+", text: b[j++] });
  return out;
}
