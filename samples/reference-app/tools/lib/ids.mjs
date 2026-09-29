// Deterministic ULIDs: the same domain key always gets the same id, so rebuilding the model never churns ids.
// The 48-bit time part is a fixed base (2026-09-01T00:00:00Z) plus up to about 24 days taken from the key's hash, so the ids
// look like ordinary ULIDs minted over a few weeks; the 80-bit random part is the next ten bytes of the hash.
import { createHash } from "node:crypto";

const CROCKFORD = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
const BASE_TIME = Date.UTC(2026, 8, 1);
const seen = new Map();

export function ulid(key) {
  const h = createHash("sha256").update("northwind-operations:" + key).digest();
  let time = BigInt(BASE_TIME + (h.readUInt32BE(0) % 2_000_000_000));
  let t = "";
  for (let i = 0; i < 10; i++) { t = CROCKFORD[Number(time & 31n)] + t; time >>= 5n; }
  let rand = 0n;
  for (let i = 4; i < 14; i++) rand = (rand << 8n) | BigInt(h[i]);
  let r = "";
  for (let i = 0; i < 16; i++) { r = CROCKFORD[Number(rand & 31n)] + r; rand >>= 5n; }
  const id = t + r;
  const prior = seen.get(id);
  if (prior !== undefined && prior !== key) throw new Error(`id collision between ${prior} and ${key}`);
  seen.set(id, key);
  return id;
}
