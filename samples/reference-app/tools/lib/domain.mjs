// Turns domain/*.yaml into model documents. Pure and deterministic: the same YAML always yields the same documents, in the
// same order, with the same ids (lib/ids.mjs). build-model.mjs writes them as files; seed.mjs posts them to an editor.
//
// The YAML grammar is documented in domain/README.md. In short:
//   attribute:  "<type>[!] [unique] [indexed] [readonly] [immutable] [pii|secret] [default=<v>] [expr=<name>] [min=<n>] [max=<n>]
//                [pattern=<re>] [| description]"   where <type> is a built-in (string(40), decimal(18,4), ...) or a type name
//                (value object, enum, scalar type), with [] for a collection
//   ref:        "<Entity>[!] [restrict|cascade|set-null|none] [back=<role>] [one] [| description]"   (many-to-one, or one-to-one)
//   child:      "<Entity> [ordered] [aggregation] [| description]"                                   (composition)
//   end:        "<Entity> <role> <1|0..1|*|1..*> [nav|nav=<name>] [restrict|cascade|set-null] [ordered]"
import { readFileSync, readdirSync } from "node:fs";
import { join } from "node:path";
import YAML from "yaml";
import { ulid } from "./ids.mjs";

const BUILTIN = new Set(["string", "text", "bool", "int16", "int32", "int64", "decimal", "float", "double", "date", "time", "datetime",
  "datetimeoffset", "duration", "uuid", "ulid", "binary", "json"]);
const ON_DELETE = new Set(["none", "cascade", "restrict", "set-null"]);

export function loadDomain(domainDir) {
  const files = readdirSync(domainDir).filter((f) => f.endsWith(".yaml")).sort();
  const docs = files.map((f) => ({ file: f, data: YAML.parse(readFileSync(join(domainDir, f), "utf8")) }));
  const project = docs.find((d) => d.data.project)?.data;
  if (!project) throw new Error("no domain file declares `project`");
  const packages = docs.filter((d) => d.data.package).map((d) => ({ ...d.data, file: d.file }));
  return { project, packages };
}

// ---------- naming helpers ----------
export const words = (name) => name.replace(/([a-z0-9])([A-Z])/g, "$1 $2").replace(/([A-Z])([A-Z][a-z])/g, "$1 $2");
export const kebab = (name) => {
  let sb = "";
  for (let i = 0; i < name.length; i++) {
    const c = name[i];
    if (!/[A-Za-z0-9]/.test(c)) { if (sb.length > 0 && !sb.endsWith("-")) sb += "-"; continue; }
    const up = (x) => x !== undefined && /[A-Z]/.test(x), low = (x) => x !== undefined && /[a-z]/.test(x), dig = (x) => x !== undefined && /[0-9]/.test(x);
    const boundary = i > 0 && up(c) && (low(name[i - 1]) || dig(name[i - 1]) || (i + 1 < name.length && up(name[i - 1]) && low(name[i + 1])));
    if (boundary && sb.length > 0 && !sb.endsWith("-")) sb += "-";
    sb += c.toLowerCase();
  }
  return sb.replace(/^-+|-+$/g, "");
};
const camel = (name) => name[0].toLowerCase() + name.slice(1);
const pascal = (name) => name[0].toUpperCase() + name.slice(1);
export function pluralize(w) {
  const irregular = { Person: "People", person: "people", Child: "Children", child: "children", Criterion: "Criteria", criterion: "criteria",
    Status: "Statuses", status: "statuses", Address: "Addresses", address: "addresses", Analysis: "Analyses", analysis: "analyses" };
  for (const [s, p] of Object.entries(irregular)) if (w.endsWith(s)) return w.slice(0, -s.length) + p;
  if (/[^aeiou]y$/.test(w)) return w.slice(0, -1) + "ies";
  if (/(s|x|z|ch|sh)$/.test(w)) return w + "es";
  return w + "s";
}
const sentence = (name) => { const w = words(name).toLowerCase(); return w[0].toUpperCase() + w.slice(1); };

// ---------- spec parsers ----------
function splitDescription(spec) {
  const i = spec.indexOf(" | ");
  return i < 0 ? [spec.trim(), undefined] : [spec.slice(0, i).trim(), spec.slice(i + 3).trim()];
}

function literal(v) {
  if (v === "true") return true;
  if (v === "false") return false;
  if (/^-?\d+(\.\d+)?$/.test(v)) return Number(v);
  return v;
}

function parseAttribute(ctx, owner, name, spec) {
  if (typeof spec !== "string") throw new Error(`${owner}.${name}: attribute spec must be a string`);
  const [head, description] = splitDescription(spec);
  const tokens = head.split(/\s+/);
  let typeToken = tokens.shift();
  const a = { id: ctx.id(`attr:${owner}.${name}`), name };
  let required = false;
  if (typeToken.endsWith("!")) { required = true; typeToken = typeToken.slice(0, -1); }
  let collection = false;
  if (typeToken.endsWith("[]")) { collection = true; typeToken = typeToken.slice(0, -2); }
  const m = /^([A-Za-z][A-Za-z0-9]*)(?:\((\d+)(?:,(\d+))?\))?$/.exec(typeToken);
  if (!m) throw new Error(`${owner}.${name}: bad type '${typeToken}'`);
  const [, t, p1, p2] = m;
  if (BUILTIN.has(t)) {
    a.type = t;
    if (p1 !== undefined) {
      if (t === "string") a.length = Number(p1);
      else if (t === "decimal") { a.precision = Number(p1); if (p2 !== undefined) a.scale = Number(p2); }
      else if (t === "binary") a.length = Number(p1);
      else throw new Error(`${owner}.${name}: facets on ${t}`);
    }
  } else {
    a.type = { ref: ctx.typeRef(t, `${owner}.${name}`) };
  }
  const validation = {};
  for (const tok of tokens) {
    if (tok === "required") required = true;
    else if (tok === "unique") a.unique = true;
    else if (tok === "indexed") a.indexed = true;
    else if (tok === "readonly") a.readOnly = true;
    else if (tok === "immutable") a.immutable = true;
    else if (tok === "pii" || tok === "secret") a.sensitive = tok;
    else if (tok.startsWith("default=")) a.default = literal(tok.slice(8));
    else if (tok.startsWith("expr=")) a.defaultExpression = tok.slice(5);
    else if (tok.startsWith("min=")) validation.min = literal(tok.slice(4));
    else if (tok.startsWith("max=")) validation.max = literal(tok.slice(4));
    else if (tok.startsWith("pattern=")) validation.pattern = tok.slice(8);
    else throw new Error(`${owner}.${name}: unknown attribute flag '${tok}'`);
  }
  if (required) a.required = true;
  if (collection) a.collection = true;
  if (Object.keys(validation).length) a.validation = validation;
  if (!description) throw new Error(`${owner}.${name}: missing description`);
  a.description = description;
  return a;
}

function parseCard(card, where) {
  switch (card) {
    case "1": return { min: 1, max: 1 };
    case "0..1": return { min: 0, max: 1 };
    case "*": return { min: 0, max: "*" };
    case "1..*": return { min: 1, max: "*" };
    default: throw new Error(`${where}: bad cardinality '${card}'`);
  }
}

// ---------- the compiler ----------
export function compile(domain) {
  const { project, packages } = domain;
  const out = []; // { phase, modelPath, schema, doc }
  const id = (key) => ulid(key);
  const entities = new Map(); // name -> { doc, pkg, spec, attrNames:Set }
  const types = new Map(); // name -> id (value objects, scalar types, enums)
  const relationNames = new Set();
  const ctx = {
    id,
    typeRef(name, where) {
      const r = types.get(name);
      if (!r) throw new Error(`${where}: unknown type '${name}'`);
      return r;
    },
  };
  const emit = (phase, modelPath, schema, doc) => out.push({ phase, modelPath, schema, doc });

  // Vocabularies: tags, categories (one per package, grouped), stereotypes.
  const tagKeys = new Set(Object.keys(project.tags));
  emit("vocabularies", "model/vocabularies/tags.json", "tag-vocabulary.json", {
    kind: "tag-vocabulary", id: id("vocabulary:tags"), name: "tags", strict: true,
    definitions: Object.entries(project.tags).map(([key, t]) => ({ key, description: t.description, color: t.color })),
  });
  const categoryOf = new Map(); // package name -> category id
  const categories = [];
  let groupOrder = 0;
  for (const [group, g] of Object.entries(project.categories)) {
    const gid = id(`category:${group}`);
    categories.push({ id: gid, name: group, order: ++groupOrder, description: g.description });
    let childOrder = 0;
    for (const pkgName of g.packages) {
      const pkg = packages.find((p) => p.package === pkgName);
      if (!pkg) throw new Error(`category ${group}: unknown package ${pkgName}`);
      const cid = id(`category:${group}/${pkgName}`);
      categories.push({ id: cid, name: pkg.displayName, parent: gid, order: ++childOrder, description: `Elements of the ${pkg.displayName} subject area.` });
      categoryOf.set(pkgName, cid);
    }
  }
  for (const p of packages) if (!categoryOf.has(p.package)) throw new Error(`package ${p.package} has no category`);
  emit("vocabularies", "model/vocabularies/categories.json", "category-tree.json", {
    kind: "category-tree", id: id("vocabulary:categories"), name: "categories", categories,
  });
  const stereotypeAttrs = new Map();
  for (const [key, s] of Object.entries(project.stereotypes)) {
    const attrs = Object.entries(s.attributes ?? {}).map(([n, spec]) => parseAttribute(ctx, `stereotype:${key}`, n, spec));
    stereotypeAttrs.set(key, attrs.map((a) => a.name));
    emit("vocabularies", `model/vocabularies/stereotypes/${kebab(s.name)}.json`, "stereotype.json", {
      kind: "stereotype", id: id(`stereotype:${key}`), key, name: s.name, description: s.description, appliesTo: s.appliesTo ?? ["entity"],
      attributes: attrs.length ? attrs : undefined,
    });
  }

  // Packages.
  const packageId = new Map();
  for (const p of packages) {
    const pid = id(`package:${p.package}`);
    packageId.set(p.package, pid);
    emit("packages", `model/packages/${kebab(p.package)}.json`, "package.json", {
      kind: "package", id: pid, name: p.package, displayName: p.displayName, description: p.description, category: categoryOf.get(p.package),
    });
  }
  const pkgOf = (name, where) => { const r = packageId.get(name); if (!r) throw new Error(`${where}: unknown package ${name}`); return r; };

  // Types: enums first (value objects may use them), then scalar types, then value objects.
  for (const p of packages) {
    for (const [name, e] of Object.entries(p.enums ?? {})) {
      if (types.has(name)) throw new Error(`duplicate type ${name}`);
      types.set(name, id(`enum:${name}`));
    }
  }
  for (const name of Object.keys(project.scalarTypes ?? {})) types.set(name, id(`scalar:${name}`));
  for (const name of Object.keys(project.valueObjects ?? {})) types.set(name, id(`vo:${name}`));
  for (const p of packages) {
    for (const [name, e] of Object.entries(p.enums ?? {})) {
      let value = 0;
      const members = Object.entries(e.members).map(([m, spec]) => {
        const [code, description] = splitDescription(String(spec));
        if (!description) throw new Error(`enum ${name}.${m}: missing description`);
        return { id: id(`member:${name}.${m}`), name: m, value: value++, code, description };
      });
      emit("types", `model/enums/${kebab(name)}.json`, "enum.json", {
        kind: "enum", id: types.get(name), name, displayName: e.displayName ?? sentence(name), package: pkgOf(p.package, name),
        description: e.description, tags: e.tags, category: categoryOf.get(p.package), members,
      });
    }
  }
  const typesPackage = project.project.typesPackage;
  for (const [name, s] of Object.entries(project.scalarTypes ?? {})) {
    const validation = s.pattern || s.min !== undefined || s.max !== undefined ? { min: s.min, max: s.max, pattern: s.pattern } : undefined;
    emit("types", `model/types/${kebab(name)}.json`, "scalar-type.json", {
      kind: "scalar-type", id: types.get(name), name, displayName: s.displayName ?? sentence(name), package: pkgOf(typesPackage, name),
      description: s.description, category: categoryOf.get(typesPackage), base: s.base, length: s.length, precision: s.precision, scale: s.scale, validation,
    });
  }
  for (const [name, v] of Object.entries(project.valueObjects ?? {})) {
    emit("types", `model/types/${kebab(name)}.json`, "value-object.json", {
      kind: "value-object", id: types.get(name), name, displayName: v.displayName ?? sentence(name), package: pkgOf(typesPackage, name),
      description: v.description, category: categoryOf.get(typesPackage),
      attributes: Object.entries(v.attributes).map(([n, spec]) => parseAttribute(ctx, `vo:${name}`, n, spec)),
    });
  }

  // Database, schema, sequences and designed tables.
  const db = project.database;
  const dbId = id(`database:${db.name}`);
  const schemaId = id(`database:${db.name}/schema:${db.schema}`);
  emit("database", `model/databases/${kebab(db.name)}/database.json`, "database.json", {
    kind: "database", id: dbId, name: db.name, displayName: db.displayName, dialect: db.dialect, version: String(db.version), defaultSchema: db.schema,
    description: db.description, schemas: [{ id: schemaId, name: db.schema, description: db.schemaDescription }],
  });
  const sequenceId = new Map();
  for (const [name, s] of Object.entries(db.sequences ?? {})) {
    const sid = id(`sequence:${name}`);
    sequenceId.set(name, sid);
    emit("database", `model/databases/${kebab(db.name)}/sequences/${kebab(name)}.json`, "sequence.json", {
      kind: "sequence", id: sid, name, database: dbId, schema: schemaId, description: s.description, type: s.type, start: s.start, increment: s.increment, cache: s.cache,
    });
  }
  for (const [name, v] of Object.entries(db.views ?? {})) {
    emit("database", `model/databases/${kebab(db.name)}/views/${kebab(name)}.json`, "view.json", {
      kind: "view", id: id(`view:${name}`), name, database: dbId, schema: schemaId, description: v.description, tags: v.tags,
      body: { "*": v.body.trim() },
      columns: Object.entries(v.columns).map(([cn, spec]) => {
        const [type, flag] = spec.split(" ");
        return { name: cn, type, nullable: flag === "not-null" ? false : undefined };
      }),
      comment: v.comment,
    });
  }
  for (const [name, t] of Object.entries(db.tables ?? {})) {
    const tid = id(`table:${name}`);
    const cols = Object.entries(t.columns).map(([cn, c]) => ({
      id: id(`table:${name}.${cn}`), name: cn, type: c.type, length: c.length, precision: c.precision, scale: c.scale, nullable: c.nullable,
      default: c.default, defaultSql: c.defaultSql, generated: c.generated, comment: c.comment,
    }));
    const colId = (cn) => { if (!t.columns[cn]) throw new Error(`table ${name}: unknown column ${cn}`); return id(`table:${name}.${cn}`); };
    emit("database", `model/databases/${kebab(db.name)}/tables/${kebab(name)}.json`, "table.json", {
      kind: "table", id: tid, name, database: dbId, schema: schemaId, origin: "designed", description: t.description, tags: t.tags,
      columns: cols, primaryKey: { name: t.primaryKey.name, columns: t.primaryKey.columns.map((c) => colId(c)) },
      indexes: (t.indexes ?? []).map((ix, i) => ({
        id: id(`table:${name}/index:${i}`), name: ix.name,
        columns: ix.columns.map((c) => { const [cn, dir] = c.split(" "); return { column: colId(cn), descending: dir === "desc" ? true : undefined }; }),
        where: ix.where, unique: ix.unique,
      })),
      checks: Object.entries(t.checks ?? {}).map(([cn, expr]) => ({ id: id(`table:${name}/check:${cn}`), name: cn, expression: { "*": expr } })),
      comment: t.comment,
    });
  }

  // Entities: pass 1 registers names and ids, pass 2 builds documents.
  for (const p of packages) {
    for (const [name, e] of Object.entries(p.entities ?? {})) {
      if (entities.has(name) || types.has(name)) throw new Error(`duplicate element name ${name}`);
      entities.set(name, { id: id(`entity:${name}`), pkg: p.package, spec: e, members: new Map() });
    }
  }
  const entityRef = (name, where) => { const r = entities.get(name); if (!r) throw new Error(`${where}: unknown entity ${name}`); return r; };
  const relations = []; // { pkg, doc, endEntities:[names] }
  const mappings = [];
  const tables = [];

  for (const p of packages) {
    for (const [name, e] of Object.entries(p.entities ?? {})) {
      const ent = entities.get(name);
      if (!e.description) throw new Error(`${name}: missing description`);
      const attrs = [];
      let key;
      const keySpec = e.base ? null : (e.key ?? "uuid-v7");
      const keyTypes = { "uuid-v7": ["uuid", "uuid-v7"], ulid: ["ulid", "ulid"], identity: ["int64", "database-identity"], identity32: ["int32", "database-identity"], sequence: ["int64", "sequence"] };
      if (keySpec && keyTypes[keySpec]) {
        const [type, strategy] = keyTypes[keySpec];
        const kid = id(`attr:${name}.id`);
        attrs.push({ id: kid, name: "id", type, required: true, readOnly: true, description: `Surrogate key of the ${words(name).toLowerCase()}.` });
        key = { attributes: [kid], strategy };
      }
      for (const [an, spec] of Object.entries(e.attributes ?? {})) attrs.push(parseAttribute(ctx, name, an, spec));
      if (keySpec && !keyTypes[keySpec]) {
        const natural = attrs.find((a) => a.name === keySpec);
        if (!natural) throw new Error(`${name}: key '${keySpec}' is neither a strategy nor an attribute`);
        key = { attributes: [natural.id], strategy: "application" };
      }
      for (const a of attrs) ent.members.set(a.name, "attribute");
      for (const st of e.stereotypes ?? []) {
        if (!project.stereotypes[st]) throw new Error(`${name}: unknown stereotype ${st}`);
        for (const an of stereotypeAttrs.get(st)) {
          if (ent.members.has(an)) throw new Error(`${name}: attribute ${an} collides with stereotype ${st}`);
          ent.members.set(an, "stereotype");
        }
      }
      for (const t of e.tags ?? []) if (!tagKeys.has(t)) throw new Error(`${name}: unknown tag ${t}`);
      const alternateKeys = Object.entries(e.alternateKeys ?? {}).map(([akName, names]) => ({
        id: id(`altkey:${name}.${akName}`), name: akName,
        attributes: names.map((n) => { const a = attrs.find((x) => x.name === n); if (!a) throw new Error(`${name}: alternate key ${akName} names unknown ${n}`); return a.id; }),
      }));
      ent.attrs = attrs;
      ent.doc = {
        kind: "entity", id: ent.id, name, displayName: e.displayName ?? sentence(name), pluralName: e.pluralName ?? pluralize(sentence(name)),
        package: pkgOf(p.package, name), abstract: e.abstract, base: e.base ? entityRef(e.base, name).id : undefined, description: e.description.trim(),
        stereotypes: e.stereotypes, tags: e.tags, category: categoryOf.get(p.package), key, alternateKeys: alternateKeys.length ? alternateKeys : undefined,
        attributes: attrs,
      };
    }
  }
  // Inherited members count for collisions.
  const allMembers = (name) => {
    const ent = entities.get(name);
    const base = ent.spec.base ? allMembers(ent.spec.base) : new Map();
    return new Map([...base, ...ent.members]);
  };
  const attrId = (entName, attrName) => {
    let ent = entities.get(entName);
    while (ent) {
      const a = ent.attrs.find((x) => x.name === attrName);
      if (a) return a.id;
      ent = ent.spec.base ? entities.get(ent.spec.base) : undefined;
    }
    throw new Error(`${entName}: unknown attribute ${attrName}`);
  };
  const addNavigation = (entityName, nav, where) => {
    if (!nav) return;
    const members = allMembers(entityName);
    if (members.has(nav)) throw new Error(`${where}: navigation ${nav} collides with a member of ${entityName}`);
    if (pascal(nav) === entityName) throw new Error(`${where}: navigation ${nav} equals its class name ${entityName}`);
    entities.get(entityName).members.set(nav, "navigation");
  };
  const addRelation = (pkg, rel) => {
    if (relationNames.has(rel.name)) throw new Error(`duplicate relation name ${rel.name}`);
    relationNames.add(rel.name);
    relations.push({ pkg, doc: rel });
  };

  // Relations from refs and children, then explicit relations.
  for (const p of packages) {
    for (const [name, e] of Object.entries(p.entities ?? {})) {
      for (const [role, spec] of Object.entries(e.refs ?? {})) {
        const [head, desc] = splitDescription(spec);
        const tokens = head.split(/\s+/);
        let target = tokens.shift();
        let required = false;
        if (target.endsWith("!")) { required = true; target = target.slice(0, -1); }
        let onDelete = required ? "restrict" : "set-null";
        let back, one = false, relName;
        for (const t of tokens) {
          if (ON_DELETE.has(t)) onDelete = t;
          else if (t.startsWith("back=")) back = t.slice(5);
          else if (t === "one") one = true;
          else if (t.startsWith("name=")) relName = t.slice(5).replaceAll("_", " ");
          else throw new Error(`${name}.${role}: unknown ref flag ${t}`);
        }
        const tgt = entityRef(target, `${name}.${role}`);
        const rname = relName ?? `${words(name).toLowerCase()} ${words(role).toLowerCase()}`;
        const manyRole = back ?? (one ? camel(name) : camel(pluralize(name)));
        addNavigation(name, role, rname);
        if (back) addNavigation(target, back, rname);
        addRelation(p.package, {
          kind: "relation", id: id(`relation:${rname}`), name: rname, package: pkgOf(p.package, rname),
          description: desc ?? `Each ${words(name).toLowerCase()} ${required ? "references its" : "may reference a"} ${words(role).toLowerCase()} (${words(target).toLowerCase()}).`,
          category: categoryOf.get(p.package),
          ends: [
            { id: id(`end:${rname}#0`), entity: tgt.id, role, navigation: role, min: required ? 1 : 0, max: 1, onDelete },
            { id: id(`end:${rname}#1`), entity: entities.get(name).id, role: manyRole, navigation: back, max: one ? 1 : undefined },
          ],
        });
      }
      for (const [role, spec] of Object.entries(e.children ?? {})) {
        const [head, desc] = splitDescription(spec);
        const tokens = head.split(/\s+/);
        const child = tokens.shift();
        const ordered = tokens.includes("ordered");
        const aggregation = tokens.includes("aggregation");
        for (const t of tokens) if (t !== "ordered" && t !== "aggregation") throw new Error(`${name}.${role}: unknown child flag ${t}`);
        const ch = entityRef(child, `${name}.${role}`);
        const rname = `${words(name).toLowerCase()} ${words(role).toLowerCase()}`;
        const parentRole = camel(name);
        addNavigation(name, role, rname);
        addNavigation(child, parentRole, rname);
        addRelation(p.package, {
          kind: "relation", id: id(`relation:${rname}`), name: rname, package: pkgOf(p.package, rname), relationKind: aggregation ? "aggregation" : "composition",
          description: desc ?? `A ${words(name).toLowerCase()} ${aggregation ? "groups" : "owns"} its ${words(role).toLowerCase()}.`,
          category: categoryOf.get(p.package),
          ends: [
            { id: id(`end:${rname}#0`), entity: entities.get(name).id, role: parentRole, navigation: parentRole, min: aggregation ? 0 : 1, max: 1, onDelete: aggregation ? "set-null" : "cascade" },
            { id: id(`end:${rname}#1`), entity: ch.id, role, navigation: role, ordered: ordered || undefined },
          ],
        });
      }
    }
    for (const r of p.relations ?? []) {
      if (!r.description) throw new Error(`relation ${r.name}: missing description`);
      const ends = r.ends.map((s, i) => {
        const tokens = s.split(/\s+/);
        const [entName, role, card] = tokens.splice(0, 3);
        const e = { id: id(`end:${r.name}#${i}`), entity: entityRef(entName, r.name).id, role, ...parseCard(card, r.name) };
        for (const t of tokens) {
          if (t === "nav") e.navigation = role;
          else if (t.startsWith("nav=")) e.navigation = t.slice(4);
          else if (ON_DELETE.has(t)) e.onDelete = t;
          else if (t === "ordered") e.ordered = true;
          else throw new Error(`${r.name}: unknown end flag ${t}`);
        }
        return { e, entName };
      });
      ends.forEach(({ e }, i) => addNavigation(ends[1 - i]?.entName ?? ends[0].entName, e.navigation, r.name));
      const attributes = Object.entries(r.attributes ?? {}).map(([n, spec]) => parseAttribute(ctx, `relation:${r.name}`, n, spec));
      addRelation(p.package, {
        kind: "relation", id: id(`relation:${r.name}`), name: r.name, inverseName: r.inverseName, package: pkgOf(p.package, r.name),
        relationKind: r.kind, description: r.description, tags: r.tags, category: categoryOf.get(p.package), ends: ends.map((x) => x.e),
        attributes: attributes.length ? attributes : undefined, allowDuplicates: r.allowDuplicates,
      });
      if (r.mapping) {
        mappings.push({
          kind: "mapping", id: id(`mapping:${r.name}`), name: `${r.name} in ${db.name}`, database: dbId, relation: id(`relation:${r.name}`),
          description: r.mapping.description, shape: r.mapping.shape,
        });
      }
    }
  }

  // Entity mappings and table overlays.
  for (const p of packages) {
    for (const [name, e] of Object.entries(p.entities ?? {})) {
      const ent = entities.get(name);
      if (e.mapping) {
        const m = e.mapping;
        const attributes = [];
        for (const [an, storage] of Object.entries(m.storage ?? {})) attributes.push({ attribute: attrId(name, an), storage });
        for (const [an, prefix] of Object.entries(m.prefix ?? {})) attributes.push({ attribute: attrId(name, an), prefix });
        mappings.push({
          kind: "mapping", id: id(`mapping:${name}`), name: `${name} in ${db.name}`, database: dbId, entity: ent.id, description: m.description,
          inheritance: m.inheritance, discriminatorValue: m.discriminator, attributes: attributes.length ? attributes : undefined,
        });
      }
      if (e.overlay) {
        const o = e.overlay;
        const columns = Object.entries(o.columns ?? {}).map(([an, c]) => ({
          id: id(`overlay:${name}.${an}`), attribute: attrId(name, an), nativeType: c.nativeType, defaultSql: c.defaultSql ? { "*": c.defaultSql } : undefined,
          generated: c.sequence ? "sequence" : undefined, sequence: c.sequence ? sequenceId.get(c.sequence) : undefined, comment: c.comment,
        }));
        for (const c of Object.values(o.columns ?? {})) if (c.sequence && !sequenceId.has(c.sequence)) throw new Error(`${name}: unknown sequence ${c.sequence}`);
        const tid = id(`overlay:${name}`);
        tables.push({
          modelPath: `model/databases/${kebab(db.name)}/tables/${tid.toLowerCase()}.json`,
          doc: {
            kind: "table", id: tid, database: dbId, origin: "synthesized", entity: ent.id, description: o.description,
            columns: columns.length ? columns : undefined,
            checks: Object.entries(o.checks ?? {}).map(([cn, expr]) => ({ id: id(`overlay:${name}/check:${cn}`), name: cn, expression: { "*": expr } })),
            indexes: (o.indexes ?? []).map((ix, i) => ({
              id: id(`overlay:${name}/index:${i}`), name: ix.name,
              columns: ix.columns.map((c) => { const [an, dir] = c.split(" "); return { column: attrId(name, an), descending: dir === "desc" ? true : undefined }; }),
              where: ix.where, unique: ix.unique,
            })),
            comment: o.comment,
          },
        });
      }
    }
  }

  // Emit entities (per package, in file order), relations, tables, mappings, diagrams.
  for (const p of packages) {
    for (const name of Object.keys(p.entities ?? {})) {
      const ent = entities.get(name);
      emit(`entities:${p.package}`, `model/entities/${kebab(name)}.json`, "entity.json", ent.doc);
    }
  }
  for (const r of relations) emit("relations", `model/relations/${kebab(r.doc.name)}.json`, "relation.json", r.doc);
  for (const t of tables) emit("physical", t.modelPath, "table.json", t.doc);
  for (const m of mappings) emit("mappings", `model/mappings/${kebab(m.name)}.json`, "mapping.json", m);

  for (const p of packages) {
    const d = p.diagram;
    const names = Object.keys(p.entities ?? {});
    const cols = d.columns ?? 4;
    const members = names.map((n, i) => ({ element: entities.get(n).id, x: 40 + (i % cols) * 320, y: 40 + Math.floor(i / cols) * 240 }));
    const inPkg = new Set(names.map((n) => entities.get(n).id));
    for (const r of relations) if (r.doc.ends.every((e) => inPkg.has(e.entity))) members.push({ element: r.doc.id });
    emit("diagrams", `model/diagrams/${kebab(d.name)}.json`, "diagram.json", {
      kind: "diagram", id: id(`diagram:${p.package}`), name: d.name, package: pkgOf(p.package, d.name), description: d.description,
      category: categoryOf.get(p.package), members, viewport: { zoom: d.zoom ?? 0.8 },
    });
  }

  const paths = new Set();
  for (const o of out) {
    if (paths.has(o.modelPath)) throw new Error(`two elements write ${o.modelPath}`);
    paths.add(o.modelPath);
  }
  return {
    documents: out,
    stats: {
      packages: packages.length, entities: entities.size, relations: relations.length, enums: [...types.keys()].filter((t) => out.some((o) => o.schema === "enum.json" && o.doc.name === t)).length,
      valueObjects: Object.keys(project.valueObjects ?? {}).length, scalarTypes: Object.keys(project.scalarTypes ?? {}).length, mappings: mappings.length,
      diagrams: packages.length, tables: tables.length + Object.keys(db.tables ?? {}).length, views: Object.keys(db.views ?? {}).length, sequences: sequenceId.size,
    },
  };
}

/** The project settings document (.maquettiste/maquettiste.json). */
export function settings(project) {
  return project.settings;
}
