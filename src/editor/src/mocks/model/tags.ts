// The mock's tags across the model (ModelStore.Tags.cs): the usage of each tag a vocabulary scope governs (GET
// /api/model/tags/usage) and the batch operation retag, which removes tags or renames one in the vocabulary and in every use
// it governs. A use belongs to the nearest vocabulary on the element's domain chain that declares the tag, or to none; a scope
// governs its own declared tags' uses inside it and the undeclared ones.
import type { TagUsage } from "@/api/types";
import { isTagKey } from "@/model/vocabularies";
import { globalsOf, vocabularyChain, type ModelEntry } from "./validate";

type Json = Record<string, unknown>;

/** The members of free-form maps a tag walk does not enter. */
const FREE_FORM = new Set(["properties", "generation", "rows", "defaultProperties", "options", "body", "expression", "variables", "defaultSql", "parameters"]);

function tagsIn(node: unknown, out: string[] = []): string[] {
  if (Array.isArray(node)) for (const item of node) tagsIn(item, out);
  else if (node && typeof node === "object")
    for (const [name, value] of Object.entries(node as Json)) {
      if (FREE_FORM.has(name)) continue;
      if (name === "tags" && Array.isArray(value)) out.push(...value.filter((t): t is string => typeof t === "string"));
      else tagsIn(value, out);
    }
  return out;
}

/** The uses a scope ("" for global) governs, as [element id, tag] per use. */
function governed(entries: readonly ModelEntry[], scope: string, only?: ReadonlySet<string>): [string, string][] {
  const globals = globalsOf(entries);
  const out: [string, string][] = [];
  for (const e of entries) {
    const chain = vocabularyChain(e.json as Json, globals);
    if (scope !== "" && !chain.includes(scope)) continue;
    for (const tag of tagsIn(e.json)) {
      if (only && !only.has(tag)) continue;
      const owner = chain.find((s) => globals.tagVocabularies.get(s)?.keys.has(tag));
      if (owner === undefined || owner === scope) out.push([e.id, tag]);
    }
  }
  return out;
}

const vocabularyOf = (entries: readonly ModelEntry[], scope: string) =>
  entries.find((e) => e.json.kind === "tag-vocabulary" && (typeof e.json.package === "string" ? e.json.package : "") === scope);

/** GET /api/model/tags/usage; null when the package is not a domain. */
export function tagUsage(entries: readonly ModelEntry[], packageId: string | null): TagUsage | null {
  const scope = packageId ?? "";
  if (packageId && !entries.some((e) => e.id === packageId && e.json.kind === "package")) return null;
  const vocabulary = vocabularyOf(entries, scope);
  const declared = new Set(((vocabulary?.json.definitions as Json[] | undefined) ?? []).map((d) => String(d.key)));
  const uses = new Map<string, { uses: number; elements: Set<string> }>();
  for (const key of declared) uses.set(key, { uses: 0, elements: new Set() });
  for (const [id, tag] of governed(entries, scope)) {
    const entry = uses.get(tag) ?? { uses: 0, elements: new Set<string>() };
    entry.uses++;
    entry.elements.add(id);
    uses.set(tag, entry);
  }
  const nameOf = (id: string) => String(entries.find((e) => e.id === id)?.json.name ?? id);
  return {
    package: packageId,
    vocabulary: vocabulary?.id ?? null,
    strict: vocabulary?.json.strict === true,
    tags: [...uses.keys()]
      .sort((a, b) => (a < b ? -1 : a > b ? 1 : 0))
      .map((tag) => {
        const elements = [...uses.get(tag)!.elements].sort();
        return { tag, declared: declared.has(tag), uses: uses.get(tag)!.uses, elements, examples: elements.map(nameOf).sort().slice(0, 5) };
      }),
  };
}

function rewrite(node: unknown, tags: ReadonlySet<string>, rename: string | null): void {
  if (Array.isArray(node)) for (const item of node) rewrite(item, tags, rename);
  else if (node && typeof node === "object")
    for (const [name, value] of Object.entries(node as Json)) {
      if (FREE_FORM.has(name)) continue;
      if (name === "tags" && Array.isArray(value)) {
        const next: string[] = [];
        for (const t of value) {
          if (typeof t !== "string") continue;
          const kept = tags.has(t) ? rename : t;
          if (kept !== null && !next.includes(kept)) next.push(kept);
        }
        (node as Json).tags = next;
      } else rewrite(value, tags, rename);
    }
}

export interface RetagOp {
  tags?: string[];
  name?: string | null;
  package?: string | null;
}

/** The batch operation retag: the documents it rewrites, or why it is refused (MQ1002). */
export function retag(entries: readonly ModelEntry[], op: RetagOp): { changed: Map<string, Json> } | { error: string } {
  const tags = [...new Set(op.tags ?? [])];
  if (!tags.length) return { error: "Name at least one tag (tags)." };
  if (op.package && !entries.some((e) => e.id === op.package && e.json.kind === "package")) return { error: `'${op.package}' is not a domain.` };
  const rename = op.name ?? null;
  if (rename !== null) {
    if (tags.length !== 1) return { error: "A rename names one tag (tags) and its new key (name)." };
    if (!isTagKey(rename)) return { error: `'${rename}' is not a tag key: 1 to 64 characters without spaces or control characters.` };
    if (rename === tags[0]) return { error: `The tag is already '${rename}'.` };
  }
  const only = new Set(tags);
  const scope = op.package ?? "";
  const changed = new Map<string, Json>();
  const working = (e: ModelEntry) => {
    if (!changed.has(e.id)) changed.set(e.id, JSON.parse(JSON.stringify(e.json)) as Json);
    return changed.get(e.id)!;
  };
  for (const id of new Set(governed(entries, scope, only).map(([id]) => id))) rewrite(working(entries.find((e) => e.id === id)!), only, rename);
  const vocabulary = vocabularyOf(entries, scope);
  if (vocabulary && ((vocabulary.json.definitions as Json[] | undefined) ?? []).some((d) => only.has(String(d.key)))) {
    const json = working(vocabulary);
    const definitions = (json.definitions as Json[]) ?? [];
    const exists = rename !== null && definitions.some((d) => d.key === rename);
    json.definitions = definitions.flatMap((d) => (!only.has(String(d.key)) ? [d] : rename !== null && !exists ? [{ ...d, key: rename }] : []));
  }
  if (!changed.size) return { error: `Nothing uses or declares ${tags.length === 1 ? `tag '${tags[0]}'` : "these tags"} here.` };
  return { changed };
}
