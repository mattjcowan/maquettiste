// The mock backend's translations, seed CSV and reference type usage (reference-types-seeds-localization.md
// sections 2.3 and 3.9), mirroring the functions' shapes. Off by default (the billing fixture declares no
// localization); the `locales` scenario declares en (default), fr and fr-CA (falling back to fr) in maquettiste.json
// and seeds a French display name for Invoice. The settings are read live, and translations are kept per locale and
// placed in per-domain shards (RT 3.1 and 3.3) with their source fingerprints, so stale entries show.
import { sha256Hex } from "@/lib/sha256";
import type { MockModel } from "./store";

type Json = Record<string, unknown>;
type State = "translated" | "missing" | "stale" | "fallback";
const FIELDS = ["displayName", "pluralName", "label", "description"] as const;
export type Field = (typeof FIELDS)[number];

export interface TranslationEntryRow {
  id: string;
  owner: string;
  field: Field;
  source: string | null;
  translation: string | null;
  effective: string | null;
  state: State;
  shard: string;
  shardHash: string | null;
}

export interface ImportPreviewBody {
  added: number;
  changed: Json[];
  removed: number;
  diagnostics: Json[];
  applied: boolean;
  ignoredHeaders?: string[];
  blocked?: Json[];
  hash?: string;
  shardHashes?: Record<string, string>;
}

/** RFC 4180 CSV, as the functions write it: quoted only when needed, LF (or BOM and CRLF for spreadsheets). */
export function writeCsv(rows: readonly (readonly (string | null | undefined)[])[], bom = false): string {
  const nl = bom ? "\r\n" : "\n";
  const cell = (v: string | null | undefined) => {
    const s = v ?? "";
    return /[",\r\n]/.test(s) ? `"${s.replace(/"/g, '""')}"` : s;
  };
  return (bom ? "﻿" : "") + rows.map((r) => r.map(cell).join(",") + nl).join("");
}

export function readCsv(text: string): string[][] {
  const rows: string[][] = [];
  let row: string[] = [];
  let cell = "";
  let quoted = false;
  let i = text.startsWith("﻿") ? 1 : 0;
  const endRow = () => {
    row.push(cell);
    if (!(row.length === 1 && row[0] === "")) rows.push(row);
    row = [];
    cell = "";
  };
  for (; i < text.length; i++) {
    const c = text[i];
    if (quoted) {
      if (c === '"' && text[i + 1] === '"') {
        cell += '"';
        i++;
      } else if (c === '"') quoted = false;
      else cell += c;
    } else if (c === '"' && cell === "") quoted = true;
    else if (c === ",") {
      row.push(cell);
      cell = "";
    } else if (c === "\r" && text[i + 1] === "\n") continue;
    else if (c === "\n" || c === "\r") endRow();
    else cell += c;
  }
  if (quoted) throw new Error("A quoted field is not closed.");
  if (cell !== "" || row.length > 0) endRow();
  return rows;
}

const xmlEscape = (s: string) => s.replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;").replace(/"/g, "&quot;");
const xmlUnescape = (s: string) =>
  s
    .replace(/&lt;/g, "<")
    .replace(/&gt;/g, ">")
    .replace(/&quot;/g, '"')
    .replace(/&apos;/g, "'")
    .replace(/&amp;/g, "&");

interface Node {
  id: string;
  owner: string;
  field: Field;
  source: string | null;
  /** The node kind `require` names: the element's kind, or `reference-row` for a seed row. */
  kind: string;
  /** The shard file name without `.json`: the domain's kebab path, `_root` or `_reference-data`. */
  stem: string;
}

interface Text {
  text: string;
  /** The first 8 hex digits of SHA-256 over the default text it was translated from (RT 3.3). */
  src: string;
}

/** The first 8 hex digits of SHA-256 over a default-locale text: a translation's source fingerprint. */
export const sourceFingerprint = (text: string | null) => sha256Hex(text ?? "").slice(0, 8);

/** Domain-model kinds (RT 3.1): physical elements, mappings, diagrams and vocabularies are not localized. */
const LOCALIZED = new Set(["package", "entity", "value-object", "scalar-type", "enum", "relation", "reference-type", "seed"]);

const kebab = (name: string) =>
  name
    .replace(/([a-z0-9])([A-Z])/g, "$1-$2")
    .replace(/([A-Z]+)([A-Z][a-z])/g, "$1-$2")
    .replace(/[^A-Za-z0-9]+/g, "-")
    .toLowerCase()
    .replace(/^-|-$/g, "");

export class MockLocalization {
  /** Translations per locale, keyed `id/field`, with their source fingerprints. */
  private readonly texts = new Map<string, Map<string, Text>>();
  /** Bumped on every write: part of the localized index's ETag. */
  version = 0;

  constructor(
    private readonly model: MockModel,
    enabled: boolean,
    private readonly newId: () => string,
  ) {
    if (!enabled) return;
    const invoice = "01J92P0V0FJ23CGSNKM7P1W5V7";
    const source = (this.model.docs().get(invoice)?.displayName as string | undefined) ?? "Invoice";
    this.texts.set("fr", new Map([[`${invoice}/displayName`, { text: "Facture", src: sourceFingerprint(source) }]]));
  }

  /** The `localization` block of maquettiste.json, read live so Settings › Locales applies at once. */
  private get settings(): { defaultLocale?: string; locales?: string[]; fallbacks?: Record<string, string[]>; require?: string[] } | null {
    return (this.model.settingsJson.localization as Json | undefined) ?? null;
  }

  get defaultLocale(): string | null {
    return this.settings?.defaultLocale ?? null;
  }

  get declared(): string[] {
    const s = this.settings;
    if (!s?.defaultLocale) return [];
    return s.locales?.length ? [...s.locales] : [s.defaultLocale];
  }

  isTranslated(locale: string): boolean {
    return this.declared.includes(locale) && locale !== this.defaultLocale;
  }

  chain(locale: string): string[] {
    const declared = this.declared;
    const fallbacks = this.settings?.fallbacks?.[locale];
    const out = [locale];
    if (fallbacks) out.push(...fallbacks);
    else for (let tag = locale; tag.includes("-");) if (declared.includes((tag = tag.slice(0, tag.lastIndexOf("-"))))) out.push(tag);
    if (this.defaultLocale && !out.includes(this.defaultLocale)) out.push(this.defaultLocale);
    return out;
  }

  shardPath(locale: string, stem: string): string {
    return `.maquettiste/model/locales/${locale}/${stem}.json`;
  }

  /** Kept for callers that name the root shard. */
  shardOf(locale: string): string {
    return this.shardPath(locale, "_root");
  }

  shardHash(locale: string, stem: string): string | null {
    const map = this.texts.get(locale);
    if (!map) return null;
    const stems = new Map(this.nodes().map((n) => [n.id, n.stem]));
    const mine = [...map.entries()].filter(([key]) => stems.get(key.slice(0, key.indexOf("/"))) === stem).sort(([a], [b]) => (a < b ? -1 : 1));
    return mine.length ? sha256Hex(JSON.stringify(mine)) : null;
  }

  private set(locale: string, id: string, field: string, value: string | null, src: string) {
    const map = this.texts.get(locale) ?? new Map<string, Text>();
    this.texts.set(locale, map);
    if (value === null || value === "") map.delete(`${id}/${field}`);
    else map.set(`${id}/${field}`, { text: value, src });
  }

  text(locale: string, id: string, field: string): string | null {
    return this.texts.get(locale)?.get(`${id}/${field}`)?.text ?? null;
  }

  /** The domain path of a package id: its ancestors' kebab names joined with "/". */
  private stemOf(packageId: string | null, rows: Map<string, { name: string; package: string | null }>): string {
    const parts: string[] = [];
    for (let at = packageId, guard = 0; at && guard < 64; guard++) {
      const row = rows.get(at);
      if (!row) break;
      parts.unshift(kebab(row.name));
      at = row.package;
    }
    return parts.length ? parts.join("/") : "_root";
  }

  /** The localizable nodes: every element's display name, its plural name and description when it has them, and every
   * seed row's label; each placed in its shard by the RT 3.1 rule. */
  private nodes(): Node[] {
    const out: Node[] = [];
    const docs = this.model.docs();
    const index = this.model.index();
    const rows = new Map(index.map((r) => [r.id, { name: r.name, package: r.package ?? null, kind: r.kind }]));
    for (const row of index) {
      if (!LOCALIZED.has(row.kind)) continue;
      const json = docs.get(row.id) ?? {};
      const target = row.kind === "seed" ? rows.get(String(json.target ?? "")) : undefined;
      const stem =
        row.kind === "reference-type" || target?.kind === "reference-type"
          ? "_reference-data"
          : this.stemOf(row.kind === "package" ? row.id : row.kind === "seed" ? (target?.package ?? null) : (row.package ?? null), rows);
      const node = (field: Field, source: string | null) => out.push({ id: row.id, owner: row.id, field, source, kind: row.kind, stem });
      node("displayName", (json.displayName as string | undefined) ?? row.name);
      // A plural name is expected of every element (RT 3.1); its source is the default plural when there is one.
      if (row.kind !== "seed") node("pluralName", typeof json.pluralName === "string" ? json.pluralName : null);
      if (typeof json.description === "string") node("description", json.description);
      if (row.kind === "seed" && Array.isArray(json.columns)) {
        const at = (json.columns as string[]).indexOf("label");
        for (const r of (json.rows as Json[] | undefined) ?? []) {
          const label = at >= 0 ? ((r.values as unknown[] | undefined)?.[at] ?? null) : null;
          out.push({ id: String(r.id), owner: row.id, field: "label", source: typeof label === "string" ? label : null, kind: "reference-row", stem });
        }
      }
    }
    return out.sort((a, b) => (a.id === b.id ? FIELDS.indexOf(a.field) - FIELDS.indexOf(b.field) : a.id < b.id ? -1 : 1));
  }

  private required(n: Node): boolean {
    const require = this.settings?.require ?? [];
    return require.length === 0 || require.includes(n.kind);
  }

  private stateOf(locale: string, n: Node): { translation: string | null; effective: string | null; state: State } {
    const own = this.texts.get(locale)?.get(`${n.id}/${n.field}`) ?? null;
    let effective: string | null = null;
    for (const l of this.chain(locale)) {
      effective = this.isTranslated(l) ? this.text(l, n.id, n.field) : n.source;
      if (effective !== null) break;
    }
    const state: State = own
      ? own.src === sourceFingerprint(n.source)
        ? "translated"
        : "stale"
      : effective !== null && effective !== n.source
        ? "fallback"
        : "missing";
    return { translation: own?.text ?? null, effective, state };
  }

  status(): Json {
    const nodes = this.nodes().filter((n) => this.required(n));
    const locales = this.declared
      .filter((l) => this.isTranslated(l))
      .map((locale) => {
        const byShard = new Map<string, { expected: number; translated: number; missing: number; stale: number }>();
        for (const n of nodes) {
          const c = byShard.get(n.stem) ?? { expected: 0, translated: 0, missing: 0, stale: 0 };
          byShard.set(n.stem, c);
          const { state } = this.stateOf(locale, n);
          c.expected++;
          if (state === "translated") c.translated++;
          else if (state === "stale") c.stale++;
          else c.missing++;
        }
        const shards = [...byShard.entries()].sort(([a], [b]) => (a < b ? -1 : 1)).map(([stem, c]) => ({ shard: this.shardPath(locale, stem), ...c }));
        return { locale, chain: this.chain(locale), shards };
      });
    return { defaultLocale: this.defaultLocale, declared: this.declared, locales };
  }

  entries(
    locale: string,
    query: { owner?: string | null; shard?: string | null; missing?: boolean; cursor?: string | null },
  ): { entries: TranslationEntryRow[]; cursor: string | null } {
    const hashes = new Map<string, string | null>();
    const hashOf = (stem: string) => {
      if (!hashes.has(stem)) hashes.set(stem, this.shardHash(locale, stem));
      return hashes.get(stem)!;
    };
    let rows = this.nodes()
      .filter((n) => !query.owner || n.owner === query.owner)
      .filter((n) => !query.shard || query.shard === this.shardPath(locale, n.stem))
      .map((n): TranslationEntryRow => {
        const { translation, effective, state } = this.stateOf(locale, n);
        return {
          id: n.id,
          owner: n.owner,
          field: n.field,
          source: n.source,
          translation,
          effective,
          state,
          shard: this.shardPath(locale, n.stem),
          shardHash: hashOf(n.stem),
        };
      });
    if (!query.missing) return { entries: rows, cursor: null };
    rows = rows.filter((r) => r.state !== "translated");
    if (query.cursor) rows = rows.slice(rows.findIndex((r) => `${r.id}/${r.field}` === query.cursor) + 1);
    const page = rows.slice(0, 200);
    return { entries: page, cursor: rows.length > 200 ? `${page[199].id}/${page[199].field}` : null };
  }

  /** PUT entries: 200 with hashes, 409 when a shard changed, 422 for an unknown node or field. */
  write(locale: string, entries: Json[], expected: Record<string, string>): { status: number; body: Json; locales: string[] } {
    const nodes = new Map(this.nodes().map((n) => [`${n.id}/${n.field}`, n]));
    const stems = [...new Set(Object.keys(expected).map((p) => p.slice(`.maquettiste/model/locales/${locale}/`.length, -".json".length)))];
    const changed = stems.filter((stem) => (expected[this.shardPath(locale, stem)] ?? "") !== (this.shardHash(locale, stem) ?? ""));
    if (changed.length)
      return {
        status: 409,
        body: {
          outcome: "conflict",
          hashes: Object.fromEntries(changed.map((s) => [this.shardPath(locale, s), this.shardHash(locale, s) ?? ""])),
          diagnostics: [],
        },
        locales: [],
      };
    for (const [i, e] of entries.entries()) {
      if (!nodes.has(`${String(e.id)}/${String(e.field)}`))
        return {
          status: 422,
          body: {
            outcome: "invalid",
            hashes: {},
            diagnostics: [
              { rule: "MQ7203", severity: "error", message: `${String(e.id)} has no translatable '${String(e.field)}'.`, jsonPointer: `/entries/${i}` },
            ],
          },
          locales: [],
        };
    }
    const touched = new Set<string>();
    for (const e of entries) {
      const node = nodes.get(`${String(e.id)}/${String(e.field)}`)!;
      touched.add(node.stem);
      const src = sourceFingerprint(node.source);
      if (e.confirm) {
        const own = this.texts.get(locale)?.get(`${node.id}/${node.field}`);
        if (own) own.src = src;
      } else this.set(locale, node.id, node.field, typeof e.value === "string" ? e.value : null, src);
    }
    this.version++;
    const hashes: Record<string, string> = {};
    for (const stem of touched) {
      const hash = this.shardHash(locale, stem);
      if (hash) hashes[this.shardPath(locale, stem)] = hash;
    }
    return { status: 200, body: { outcome: "saved", hashes, diagnostics: [] }, locales: [locale] };
  }

  /** The effective display name of every index row in a locale. */
  displayNames(locale: string): Record<string, string> {
    const out: Record<string, string> = {};
    const docs = this.model.docs();
    for (const row of this.model.index()) {
      let text: string | null = null;
      for (const l of this.chain(locale)) {
        if (!this.isTranslated(l)) break;
        if ((text = this.text(l, row.id, "displayName")) !== null) break;
      }
      out[row.id] = text ?? (docs.get(row.id)?.displayName as string | undefined) ?? row.name;
    }
    return out;
  }

  /** The `translations` of model.changed after a write in `changed` locales. */
  translationsEvent(changed: string[]): { locale: string; displayNames: Record<string, string> }[] {
    return this.declared
      .filter((l) => this.isTranslated(l) && this.chain(l).some((c) => changed.includes(c)))
      .map((locale) => ({ locale, displayNames: this.displayNames(locale) }));
  }

  exportText(locale: string, format: "xliff" | "csv", shard: string | null): string {
    const rows = this.entries(locale, { shard }).entries;
    if (format === "csv")
      return writeCsv([
        ["id", "field", "source", "translation", "state", "shard"],
        ...rows.map((r) => [r.id, r.field, r.source, r.translation, r.state, r.shard]),
      ]);
    const units = rows
      .map(
        (r) =>
          `    <unit id="${r.id}/${r.field}">\n      <segment state="${r.state === "translated" ? "translated" : "initial"}" subState="maquettiste:${r.state}">\n        <source>${xmlEscape(r.source ?? "")}</source>\n` +
          (r.translation !== null ? `        <target>${xmlEscape(r.translation)}</target>\n` : "") +
          "      </segment>\n    </unit>\n",
      )
      .join("");
    return `<?xml version="1.0" encoding="utf-8"?>\n<xliff version="2.1" srcLang="${this.defaultLocale ?? "und"}" trgLang="${locale}" xmlns="urn:oasis:names:tc:xliff:document:2.0">\n  <file id="f1" original="${shard ?? this.shardOf(locale)}">\n${units}  </file>\n</xliff>\n`;
  }

  importText(locale: string, format: "xliff" | "csv", content: string, dryRun: boolean): { status: number; body: ImportPreviewBody; locales: string[] } {
    const units: { id: string; field: string; value: string }[] = [];
    if (format === "csv") {
      const [header, ...rest] = readCsv(content);
      const at = (n: string) => header?.indexOf(n) ?? -1;
      if (at("id") < 0 || at("field") < 0 || at("translation") < 0) throw new Error("The CSV needs id, field and translation columns.");
      for (const r of rest) units.push({ id: r[at("id")], field: r[at("field")], value: r[at("translation")] ?? "" });
    } else {
      if (!/<xliff[\s>]/.test(content)) throw new Error("The document is not XLIFF 2.");
      for (const m of content.matchAll(/<unit id="([^"/]+)\/([^"]+)">([\s\S]*?)<\/unit>/g)) {
        const target = /<target>([\s\S]*?)<\/target>/.exec(m[3]);
        if (target) units.push({ id: m[1], field: m[2], value: xmlUnescape(target[1]) });
      }
    }
    const known = new Map(this.entries(locale, {}).entries.map((e) => [`${e.id}/${e.field}`, e]));
    const body: ImportPreviewBody = { added: 0, changed: [], removed: 0, diagnostics: [], applied: false };
    const edits: Json[] = [];
    for (const u of units) {
      if (u.value === "") continue;
      const entry = known.get(`${u.id}/${u.field}`);
      if (!entry) {
        body.diagnostics.push({
          rule: "MQ7203",
          severity: "error",
          message: `${u.id}/${u.field} is not a translatable field of the model; the unit is ignored.`,
        });
        continue;
      }
      if (entry.translation === u.value) continue;
      if (entry.translation === null) body.added++;
      else body.changed.push({ id: u.id, field: u.field, before: entry.translation, after: u.value });
      edits.push({ id: u.id, field: u.field, value: u.value });
    }
    if (dryRun || edits.length === 0) return { status: 200, body, locales: [] };
    const written = this.write(locale, edits, {});
    return { status: 200, body: { ...body, applied: true, shardHashes: written.body.hashes as Record<string, string> }, locales: written.locales };
  }

  private columnName(id: string): string {
    if (id === "code" || id === "label" || id === "description") return `@${id}`;
    for (const json of this.model.docs().values()) {
      for (const list of [json.attributes, json.ends]) {
        if (!Array.isArray(list)) continue;
        const hit = (list as Json[]).find((a) => a.id === id);
        if (hit) return String(hit.name ?? hit.role ?? id);
      }
    }
    return id;
  }

  seedCsv(id: string, bom: boolean, locales: string[]): string | null {
    const json = this.model.docs().get(id);
    if (!json || json.kind !== "seed") return null;
    const columns = (json.columns as string[]) ?? [];
    const wanted = locales.filter((l) => this.isTranslated(l));
    const cell = (v: unknown) =>
      v === null || v === undefined
        ? ""
        : typeof v === "string"
          ? v
          : Array.isArray(v) && v.every((x) => typeof x === "string")
            ? v.join(";")
            : typeof v === "object"
              ? JSON.stringify(v)
              : String(v);
    const rows = ((json.rows as Json[]) ?? []).map((r) => {
      const values = (r.values as unknown[]) ?? [];
      return [
        String(r.id),
        ...columns.map((_, i) => cell(values[i])),
        ...wanted.flatMap((l) => [this.text(l, String(r.id), "label"), this.text(l, String(r.id), "description")]),
      ];
    });
    return writeCsv([["@id", ...columns.map((c) => this.columnName(c)), ...wanted.flatMap((l) => [`@label:${l}`, `@description:${l}`])], ...rows], bom);
  }

  /** A seed CSV import: rows match by @id, else @code; merge or replace; applied with the seed's hash. */
  importSeedCsv(
    id: string,
    content: string,
    replace: boolean,
    dryRun: boolean,
    expectedHash: string | null,
  ): { status: number; body: ImportPreviewBody } | null {
    const doc = this.model.get(id);
    if (!doc || (doc.json as Json).kind !== "seed") return null;
    const json = structuredClone(doc.json as Json);
    const columns = (json.columns as string[]) ?? [];
    const headers = columns.map((c) => this.columnName(c));
    const [header = [], ...lines] = readCsv(content);
    const body: ImportPreviewBody = { added: 0, changed: [], removed: 0, diagnostics: [], applied: false, ignoredHeaders: [], blocked: [] };
    const map = new Map<number, number>();
    header.forEach((h, i) => {
      const at = headers.indexOf(h);
      if (at >= 0) map.set(i, at);
      else if (h !== "@id" && !/^@(label|description):/.test(h)) body.ignoredHeaders!.push(h);
    });
    const rows = ((json.rows as Json[]) ?? []).map((r) => ({ id: String(r.id), values: [...((r.values as unknown[]) ?? [])] }));
    const idAt = header.indexOf("@id");
    const codeCsv = header.indexOf("@code");
    const codeCol = columns.indexOf("code");
    const seen = new Set<string>();
    for (const line of lines) {
      let row = idAt >= 0 ? rows.find((r) => r.id === line[idAt]) : undefined;
      if (!row && codeCsv >= 0 && codeCol >= 0) row = rows.find((r) => r.values[codeCol] === line[codeCsv]);
      const parse = (text: string) => (text === "" ? null : /^-?\d+(\.\d+)?$/.test(text) ? Number(text) : text);
      if (row) {
        seen.add(row.id);
        const before: Json = {};
        const after: Json = {};
        for (const [csvAt, col] of map) {
          const value = parse(line[csvAt] ?? "");
          if ((row.values[col] ?? null) === value) continue;
          before[headers[col]] = row.values[col] ?? null;
          after[headers[col]] = value;
          row.values[col] = value;
        }
        if (Object.keys(after).length > 0) body.changed.push({ id: row.id, before, after });
      } else {
        const values: unknown[] = columns.map(() => null);
        for (const [csvAt, col] of map) values[col] = parse(line[csvAt] ?? "");
        const created = { id: idAt >= 0 && /^[0-9A-HJKMNP-TV-Z]{26}$/.test(line[idAt] ?? "") ? line[idAt] : this.newId(), values };
        rows.push(created);
        seen.add(created.id);
        body.added++;
      }
    }
    const kept = replace ? rows.filter((r) => seen.has(r.id)) : rows;
    body.removed = rows.length - kept.length;
    if (dryRun || (body.added === 0 && body.removed === 0 && body.changed.length === 0)) return { status: 200, body };
    const trim = (values: unknown[]) => {
      let n = values.length;
      while (n > 0 && (values[n - 1] === null || values[n - 1] === undefined)) n--;
      return values.slice(0, n);
    };
    json.rows = kept.map((r) => (trim(r.values).length ? { id: r.id, values: trim(r.values) } : { id: r.id }));
    const result = this.model.save(id, json, expectedHash ?? doc.hash);
    if (result.outcome !== "saved")
      return { status: result.outcome === "conflict" ? 409 : 422, body: { ...body, diagnostics: (result.diagnostics ?? []) as Json[] } };
    return { status: 200, body: { ...body, applied: true, hash: result.hash ?? undefined } };
  }

  usage(id: string): Json | null {
    const docs = this.model.docs();
    if (docs.get(id)?.kind !== "reference-type") return null;
    const usages: Json[] = [];
    for (const [owner, json] of docs) {
      for (const a of (Array.isArray(json.attributes) ? json.attributes : []) as Json[]) {
        const type = a.type as Json | string | undefined;
        if (typeof type === "object" && type?.ref === id)
          usages.push({
            attribute: a.id,
            owner,
            domain: (json.package as string | undefined) ?? null,
            collection: a.collection === true,
            required: a.required === true,
            storage: {},
          });
      }
    }
    return { usages };
  }
}
