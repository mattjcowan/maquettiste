// ?mock=large: the packed seed decodes from gzip or plain JSON, and, when scripts/gen-scale-model.mjs
// has been run, the 5,000-entity model loads into the mock backend with no diagnostics.
import fs from "node:fs";
import path from "node:path";
import zlib from "node:zlib";
import { describe, expect, it } from "vitest";
import { MockBackend } from "@/mocks/backend";
import { decodeSeed, LARGE_SEED_FORMAT } from "@/mocks/model/largeSeed";

const generated = path.resolve(import.meta.dirname, "../../src/mocks/data/large.json.gz");

describe("large mock seed", () => {
  const sample = JSON.stringify({ format: LARGE_SEED_FORMAT, files: [{ path: "maquettiste.json", text: '{"formatVersion":1}' }] });

  it("decodes gzip and plain JSON and refuses another format", async () => {
    const gz = await decodeSeed(new Uint8Array(zlib.gzipSync(sample)));
    const plain = await decodeSeed(new TextEncoder().encode(sample));
    expect(gz.files).toEqual(plain.files);
    expect(gz.packs.length).toBeGreaterThan(0);
    await expect(decodeSeed(new TextEncoder().encode('{"files":[]}'))).rejects.toThrow(/gen-scale-model/);
  });

  it.skipIf(!fs.existsSync(generated))("loads the generated model with no diagnostics", async () => {
    const seed = await decodeSeed(new Uint8Array(fs.readFileSync(generated)));
    const backend = new MockBackend({ seed });
    const counts = new Map<string, number>();
    for (const entry of backend.model.entries.values()) counts.set(String(entry.json.kind), (counts.get(String(entry.json.kind)) ?? 0) + 1);
    expect(counts.get("entity")).toBeGreaterThanOrEqual(5000);
    expect(counts.get("diagram")).toBeGreaterThan(0);
    expect(backend.model.validate().diagnostics).toEqual([]);
  });
});
