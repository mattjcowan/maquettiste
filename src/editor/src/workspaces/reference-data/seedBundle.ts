// All the model's seed data at once (Export all seed data, Import seed data…): one CSV per seed, named after the seed
// (`<name>.csv`, or `<name>.<id>.csv` when two seeds share a name), bundled in one ZIP; an import takes that ZIP or
// several CSV files, matches each file to a seed by its name, previews every seed's dry run, then applies them in one
// go that one undo reverses. Each seed's file is the same CSV its grid's Export CSV writes.
import type { AppServices } from "@/app/context";
import type { ElementDocument, ElementSummary, ModelJson } from "@/api/types";
import * as endpoints from "@/api/endpoints";
import { indexQuery, keys, loadElement } from "@/api/queries";
import { clone } from "@/lib/json";
import { isZip, readZip, writeZip } from "@/lib/zip";
import { currentSeed } from "./actions";
import { previewSummary, type PreviewSummary } from "./csvPreview";

type SeedRef = Pick<ElementSummary, "id" | "name">;

const byName = (a: SeedRef, b: SeedRef) => a.name.localeCompare(b.name) || a.id.localeCompare(b.id);

/** Each seed's file name in the bundle: `<name>.csv`, or `<name>.<id>.csv` for seeds whose names collide (any case). */
export function seedFileNames(seeds: readonly SeedRef[]): Map<string, string> {
  const count = new Map<string, number>();
  for (const s of seeds) count.set(s.name.toLowerCase(), (count.get(s.name.toLowerCase()) ?? 0) + 1);
  return new Map([...seeds].sort(byName).map((s) => [s.id, (count.get(s.name.toLowerCase()) ?? 0) > 1 ? `${s.name}.${s.id}.csv` : `${s.name}.csv`]));
}

export interface ImportFile {
  name: string;
  text: string;
}

export interface FileMatch {
  seed: SeedRef;
  file: string;
  text: string;
}

/**
 * Matches files to seeds by name: the file's name without folders and `.csv` (any case) is a seed's name, or
 * `<name>.<id>` as the export writes for colliding names. A name several seeds share needs the id form. Unmatched
 * files, and a second file for one seed, come back with the reason.
 */
export function matchSeedFiles(files: readonly ImportFile[], seeds: readonly SeedRef[]): { matched: FileMatch[]; skipped: string[] } {
  const matched: FileMatch[] = [];
  const skipped: string[] = [];
  const taken = new Set<string>();
  for (const file of files) {
    const base = file.name.split(/[\\/]/).pop() ?? file.name;
    if (!/\.csv$/i.test(base)) {
      skipped.push(`${base}: not a CSV file`);
      continue;
    }
    const stem = base.replace(/\.csv$/i, "");
    const byId = seeds.find((s) => stem.toLowerCase() === `${s.name}.${s.id}`.toLowerCase());
    const named = seeds.filter((s) => s.name.toLowerCase() === stem.toLowerCase());
    const seed = byId ?? (named.length === 1 ? named[0] : undefined);
    if (!seed) {
      skipped.push(named.length > 1 ? `${base}: several seeds are named ${stem}; name the file <name>.<id>.csv` : `${base}: no seed is named ${stem}`);
      continue;
    }
    if (taken.has(seed.id)) {
      skipped.push(`${base}: another file already imports into ${seed.name}`);
      continue;
    }
    taken.add(seed.id);
    matched.push({ seed, file: base, text: file.text });
  }
  return { matched: matched.sort((a, b) => byName(a.seed, b.seed)), skipped };
}

/** The CSV files of what the user picked: every `.csv` inside a ZIP, and CSV files as they are. */
export async function readImportFiles(files: readonly File[]): Promise<ImportFile[]> {
  const decoder = new TextDecoder();
  const out: ImportFile[] = [];
  for (const file of files) {
    const bytes = new Uint8Array(await file.arrayBuffer());
    if (isZip(bytes)) for (const entry of await readZip(bytes)) out.push({ name: entry.name, text: decoder.decode(entry.data) });
    else out.push({ name: file.name, text: decoder.decode(bytes) });
  }
  return out;
}

const seedRows = (rows: readonly ElementSummary[]) => rows.filter((r) => r.kind === "seed");

function download(name: string, blob: Blob) {
  const url = URL.createObjectURL(blob);
  const a = document.createElement("a");
  a.href = url;
  a.download = name;
  a.hidden = true;
  document.body.append(a);
  a.click();
  a.remove();
  setTimeout(() => URL.revokeObjectURL(url), 1000);
}

/** The seeds whose target (entity, reference type or relation) sits in the domain or one of its sub-domains. */
export function seedsInDomain(rows: readonly ElementSummary[], domainId: string): ElementSummary[] {
  const byId = new Map(rows.map((r) => [r.id, r]));
  const inside = (packageId: string | null | undefined): boolean => {
    const seen = new Set<string>();
    for (let id = packageId; id && !seen.has(id); id = byId.get(id)?.package) {
      if (id === domainId) return true;
      seen.add(id);
    }
    return false;
  };
  return seedRows(rows).filter((s) => {
    const target = s.target ? byId.get(s.target) : undefined;
    return inside(target ? target.package : s.package);
  });
}

/**
 * Export seed data: every seed's CSV (pending edits saved first) in one ZIP, the whole model's (`seed-data.zip`) or,
 * with a domain, the seeds of that domain and its sub-domains (`<domain> seed data.zip`); the number of files.
 */
export async function exportAllSeeds(services: AppServices, domain?: { id: string; name: string }): Promise<number> {
  const rows = await services.queryClient.fetchQuery(indexQuery);
  const seeds = domain ? seedsInDomain(rows, domain.id) : seedRows(rows);
  const names = seedFileNames(seeds);
  const encoder = new TextEncoder();
  const entries = [];
  for (const [id, name] of names) {
    await services.drafts.flush(id);
    entries.push({ name, data: encoder.encode(await endpoints.exportSeedCsv(id)) });
  }
  if (!entries.length) return 0;
  const zip = writeZip(entries);
  download(domain ? `${domain.name} seed data.zip` : "seed-data.zip", new Blob([zip as BlobPart], { type: "application/zip" }));
  return entries.length;
}

export interface SeedImportItem extends FileMatch {
  summary: PreviewSummary;
  before: ElementDocument;
}

/** The dry run of every matched file (one request for all), with each seed as it stood when previewed. */
export async function previewSeedImport(
  services: AppServices,
  files: readonly ImportFile[],
  mode: "merge" | "replace",
): Promise<{ items: SeedImportItem[]; skipped: string[] }> {
  const { matched, skipped } = matchSeedFiles(files, seedRows(await services.queryClient.fetchQuery(indexQuery)));
  if (!matched.length) return { items: [], skipped };
  const befores: ElementDocument[] = [];
  for (const m of matched) befores.push(await currentSeed(services, m.seed.id));
  const answer = await endpoints.importSeedsCsv(
    matched.map((m) => ({ seed: m.seed.id, content: m.text })),
    { mode, dryRun: true },
  );
  if (!Array.isArray(answer.items)) throw new Error((answer as unknown as { detail?: string }).detail ?? `The preview failed (HTTP ${answer.status}).`);
  const items = matched.map((m, i) => ({ ...m, before: befores[i], summary: previewSummary(answer.items[i]) }));
  return { items, skipped };
}

/**
 * Applies the previewed imports that change something. The server saves every seed's rows as one change (nothing is
 * written when a seed changed since the preview or any file has an error), then the `@label:<locale>` and
 * `@description:<locale>` translations, one save per seed and locale: a failed translation save leaves the rows saved.
 * The applied seeds are one undo step; undo restores the seed rows, not the translations.
 */
export async function applySeedImport(
  services: AppServices,
  items: readonly SeedImportItem[],
  mode: "merge" | "replace",
): Promise<{ ok: true; applied: number } | { ok: false; reason: string }> {
  const { queryClient, store } = services;
  const blocked = items.find((i) => i.summary.errors.length);
  if (blocked) return { ok: false, reason: `${blocked.file}: ${blocked.summary.errors[0]}` };
  const work = items.filter((i) => i.summary.canApply);
  if (!work.length) return { ok: true, applied: 0 };
  const answer = await endpoints.importSeedsCsv(
    work.map((i) => ({ seed: i.seed.id, content: i.text, hash: i.before.hash })),
    { mode, dryRun: false },
  );
  if (answer.status !== 200 || !Array.isArray(answer.items)) {
    const at = Array.isArray(answer.items) ? answer.items.findIndex((r) => r.diagnostics.length) : -1;
    const reason =
      answer.status === 409
        ? "a seed changed since the preview; preview the files again"
        : at >= 0
          ? `${work[at].file}: ${answer.items[at].diagnostics[0].message}`
          : ((answer as unknown as { detail?: string }).detail ?? `not applied (HTTP ${answer.status})`);
    return { ok: false, reason: `${reason}. Nothing was imported.` };
  }
  const done: { id: string; before: ElementDocument; after: ElementDocument }[] = [];
  for (const item of work) {
    const after = await queryClient.fetchQuery({ queryKey: keys.element(item.seed.id), queryFn: () => loadElement(item.seed.id), staleTime: 0 });
    queryClient.setQueryData(keys.element(item.seed.id), after);
    done.push({ id: item.seed.id, before: item.before, after });
  }
  void queryClient.invalidateQueries({ queryKey: keys.index });
  store.getState().pushUndo({
    label: "Import seed data",
    ids: done.map((d) => d.id),
    before: done.map((d) => clone(d.before.json as ModelJson)),
    after: done.map((d) => clone(d.after.json as ModelJson)),
    afterHashes: done.map((d) => d.after.hash),
  });
  return { ok: true, applied: done.length };
}
