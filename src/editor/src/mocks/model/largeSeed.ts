// `?mock=large` (explorer-redesign.md section 5 item 3): the 5,000-entity model that
// scripts/gen-scale-model.mjs packs into src/mocks/data/large.json.gz (gitignored). The glob makes
// the file an asset of mock builds only, and resolves to nothing when it has not been generated.
import type { Seed, SeedFile } from "./store";
import { packs } from "./seed";

export const LARGE_SEED_FORMAT = "maquettiste-mock-seed/1";
export const LARGE_SEED_SCRIPT = "npm run gen:scale (node scripts/gen-scale-model.mjs) in src/editor";

const urls = import.meta.glob("../data/large.json.gz", { query: "?url", import: "default" }) as Record<string, () => Promise<string>>;

/** Timings of the load, for window.__mqPerf.mock. */
export interface SeedLoadTimings {
  fetchMs: number;
  parseMs: number;
  bytes: number;
}

/** Decodes the packed seed: gzip (magic 1f 8b), or plain JSON when the server already decoded it. */
export async function decodeSeed(bytes: Uint8Array): Promise<Seed> {
  const gzip = bytes.length > 2 && bytes[0] === 0x1f && bytes[1] === 0x8b;
  const text = gzip
    ? await new Response(new Response(bytes as BodyInit).body!.pipeThrough(new DecompressionStream("gzip"))).text()
    : new TextDecoder().decode(bytes);
  const parsed = JSON.parse(text) as { format?: string; files?: SeedFile[] };
  if (parsed.format !== LARGE_SEED_FORMAT || !Array.isArray(parsed.files))
    throw new Error(`The large mock seed is not ${LARGE_SEED_FORMAT}; regenerate it with ${LARGE_SEED_SCRIPT}.`);
  return { files: parsed.files, packs: packs() };
}

/** Loads the large seed, or returns null when it has not been generated (or cannot be fetched). */
export async function loadLargeSeed(): Promise<{ seed: Seed; timings: SeedLoadTimings } | null> {
  const url = urls["../data/large.json.gz"];
  if (!url) return null;
  const started = performance.now();
  const response = await fetch(await url());
  if (!response.ok) return null;
  const bytes = new Uint8Array(await response.arrayBuffer());
  const fetched = performance.now();
  const seed = await decodeSeed(bytes);
  return { seed, timings: { fetchMs: fetched - started, parseMs: performance.now() - fetched, bytes: bytes.length } };
}
