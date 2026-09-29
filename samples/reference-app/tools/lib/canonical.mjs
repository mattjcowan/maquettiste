// Canonical JSON for model documents: a port of the engine's CanonicalJson and SchemaRegistry layout rules
// (src/Maquettiste.Engine/Json). Keys follow each schema's x-order (else its properties order), undeclared keys follow in
// ordinal order, values equal to the schema default are omitted, arrays with x-sort are stably sorted by that key, and the
// text is two-space indented with LF and a final newline. `$schema` is the relative path to .maquettiste/.schema/v1/<file>.
import { readFileSync, readdirSync } from "node:fs";
import { join } from "node:path";

const FREE = { free: true, keys: [], props: new Map(), items: null, mapValues: null, sortKey: null };

export class Canonical {
  constructor(schemaDir) {
    this.docs = new Map();
    for (const f of readdirSync(schemaDir).filter((n) => n.endsWith(".json")).sort())
      this.docs.set(f, JSON.parse(readFileSync(join(schemaDir, f), "utf8")));
    this.byRef = new Map();
  }

  layout(file) { return this.buildRef(file, ""); }

  buildRef(file, pointer) {
    const key = file + "#" + pointer;
    if (this.byRef.has(key)) return this.byRef.get(key);
    const layout = { keys: [], props: new Map(), items: null, mapValues: null, sortKey: null };
    this.byRef.set(key, layout);
    this.fill(layout, file, this.resolve(file, pointer));
    return layout;
  }

  build(file, node) {
    if (!isObj(node)) return FREE;
    if (typeof node.$ref === "string" && !hasStructure(node)) {
      const [f, p] = splitRef(file, node.$ref);
      return this.buildRef(f, p);
    }
    const layout = { keys: [], props: new Map(), items: null, mapValues: null, sortKey: null };
    this.fill(layout, file, node);
    return layout;
  }

  fill(layout, file, node) {
    if (!isObj(node)) return;
    if (typeof node.$ref === "string") {
      const [f, p] = splitRef(file, node.$ref);
      this.fill(layout, f, this.resolve(f, p));
    }
    if (isObj(node.properties)) {
      const order = Array.isArray(node["x-order"]) ? node["x-order"] : Object.keys(node.properties);
      for (const name of order) {
        if (!Object.prototype.hasOwnProperty.call(node.properties, name)) continue;
        const ps = node.properties[name];
        if (layout.props.has(name)) continue;
        layout.props.set(name, {
          layout: this.build(file, ps),
          def: this.defaultOf(file, ps),
          unless: isObj(ps) && typeof ps["x-default-unless"] === "string" ? ps["x-default-unless"] : null,
        });
        layout.keys.push(name);
      }
    }
    if (isObj(node.additionalProperties)) layout.mapValues ??= this.build(file, node.additionalProperties);
    if (isObj(node.items)) layout.items ??= this.build(file, node.items);
    if (typeof node["x-sort"] === "string") layout.sortKey ??= node["x-sort"];
    for (const c of ["allOf", "anyOf", "oneOf"])
      if (Array.isArray(node[c])) for (const b of node[c]) this.fill(layout, file, b);
  }

  defaultOf(file, schema) {
    if (!isObj(schema)) return undefined;
    if (Object.prototype.hasOwnProperty.call(schema, "default")) return schema.default;
    if (typeof schema.$ref === "string") {
      const [f, p] = splitRef(file, schema.$ref);
      return this.defaultOf(f, this.resolve(f, p));
    }
    return undefined;
  }

  resolve(file, pointer) {
    let node = this.docs.get(file);
    if (node === undefined) throw new Error(`unknown schema file ${file}`);
    if (!pointer) return node;
    for (const raw of pointer.replace(/^\//, "").split("/")) {
      const seg = raw.replaceAll("~1", "/").replaceAll("~0", "~");
      if (Array.isArray(node)) node = node[Number(seg)];
      else node = node[seg];
      if (node === undefined) throw new Error(`schema ref ${file}#${pointer} does not resolve`);
    }
    return node;
  }

  normalize(node, layout) {
    if (node === null || node === undefined) return null;
    if (Array.isArray(node)) {
      const itemLayout = layout.items ?? FREE;
      let items = node.map((i) => this.normalize(i, itemLayout));
      if (layout.sortKey) {
        const k = layout.sortKey;
        const sv = (i) => (isObj(i) && Number.isInteger(i[k]) ? i[k] : 0);
        items = items.map((v, i) => [v, i]).sort((a, b) => sv(a[0]) - sv(b[0]) || a[1] - b[1]).map((p) => p[0]);
      }
      return items;
    }
    if (isObj(node) && layout.keys.length > 0) {
      const out = {};
      for (const key of layout.keys) {
        if (key === "$schema" || node[key] === undefined || node[key] === null) continue;
        const prop = layout.props.get(key);
        const v = this.normalize(node[key], prop.layout);
        if (prop.def !== undefined && deepEqual(v, prop.def) && (!prop.unless || node[prop.unless] == null)) continue;
        out[key] = v;
      }
      for (const key of Object.keys(node).filter((k) => !layout.props.has(k)).sort(ordinal))
        if (node[key] !== null && node[key] !== undefined) out[key] = this.normalize(node[key], FREE);
      return out;
    }
    if (isObj(node)) {
      const vl = layout.mapValues ?? FREE;
      const out = {};
      for (const key of Object.keys(node).sort(ordinal)) out[key] = this.normalize(node[key], vl);
      return out;
    }
    return node;
  }

  /** Writes a document; `modelPath` is relative to .maquettiste/ (for example model/entities/invoice.json). */
  write(doc, schemaFile, modelPath) {
    const layout = this.layout(schemaFile);
    let normalized = this.normalize(doc, layout);
    if (layout.props.has("$schema")) normalized = { $schema: schemaReference(schemaFile, modelPath), ...normalized };
    if (schemaFile === "seed.json" && Array.isArray(normalized.rows)) {
      // A seed's rows are "x-layout": "row-per-line": one row per line, written inline, trailing null values trimmed.
      const rows = normalized.rows.map((r) => (Array.isArray(r.values) ? { ...r, values: trimNulls(r.values) } : r));
      const marker = "\u0000rows";
      const text = stringify({ ...normalized, rows: marker }).replace(JSON.stringify(marker), rows.length === 0 ? "[]" : "[\n" + rows.map((r) => "    " + inline(r)).join(",\n") + "\n  ]");
      return text + "\n";
    }
    return stringify(normalized) + "\n";
  }
}

export function schemaReference(fileName, modelPath) {
  const depth = modelPath.split("/").filter(Boolean).length - 1;
  return "../".repeat(depth) + ".schema/v1/" + fileName;
}

function trimNulls(values) {
  let end = values.length;
  while (end > 0 && values[end - 1] === null) end--;
  return values.slice(0, end);
}

function inline(v) {
  if (Array.isArray(v)) return "[" + v.map(inline).join(", ") + "]";
  if (isObj(v)) {
    const keys = Object.keys(v);
    return keys.length === 0 ? "{}" : "{ " + keys.map((k) => JSON.stringify(k) + ": " + inline(v[k])).join(", ") + " }";
  }
  return stringify(v);
}

function stringify(v) {
  // JSON.stringify matches System.Text.Json's indented output for the ASCII text the model uses; reject anything else.
  const text = JSON.stringify(v, null, 2);
  if (/[^\x20-\x7e\n]/.test(text)) throw new Error("non-ASCII or control character in model text: " + text.match(/[^\x20-\x7e\n]/)[0].codePointAt(0));
  return text;
}

const ordinal = (a, b) => (a < b ? -1 : a > b ? 1 : 0);
const isObj = (v) => v !== null && typeof v === "object" && !Array.isArray(v);
const hasStructure = (n) => ["properties", "items", "additionalProperties", "x-sort", "allOf", "anyOf", "oneOf"].some((k) => k in n);
function splitRef(current, ref) {
  const h = ref.indexOf("#");
  const f = h < 0 ? ref : ref.slice(0, h);
  return [f.length === 0 ? current : f, h < 0 ? "" : ref.slice(h + 1)];
}
function deepEqual(a, b) {
  if (a === b) return true;
  if (typeof a !== typeof b || a === null || b === null || typeof a !== "object") return false;
  if (Array.isArray(a) !== Array.isArray(b)) return false;
  if (Array.isArray(a)) return a.length === b.length && a.every((x, i) => deepEqual(x, b[i]));
  const ka = Object.keys(a), kb = Object.keys(b);
  return ka.length === kb.length && ka.every((k) => Object.prototype.hasOwnProperty.call(b, k) && deepEqual(a[k], b[k]));
}
