// Where a model document references other elements, as the engine's ReferenceWalker reports it:
// the holder (element or sub-element id), the JSON pointer, the field and the referenced id.
// `required` marks references that `remove-references` cannot clear (a delete becomes invalid).
type Json = Record<string, unknown>;

export interface Ref {
  fromId: string;
  pointer: string;
  field: string;
  toId: string;
  required: boolean;
  /** How remove-references clears it: drop the array item at `removeItem`, or delete the field. */
  removeItem?: string;
}

const arr = (v: unknown): Json[] => (Array.isArray(v) ? (v as Json[]) : []);
const str = (v: unknown): string | null => (typeof v === "string" ? v : null);

function attributeRefs(owner: string, base: string, attributes: unknown, out: Ref[]): void {
  arr(attributes).forEach((a, i) => {
    const holder = str(a.id) ?? owner;
    const type = a.type as Json | string | undefined;
    if (type && typeof type === "object" && str(type.ref))
      out.push({ fromId: holder, pointer: `${base}/${i}/type/ref`, field: "ref", toId: type.ref as string, required: true });
    if (str(a.category)) out.push({ fromId: holder, pointer: `${base}/${i}/category`, field: "category", toId: a.category as string, required: false });
  });
}

/** Every outgoing reference of one document. Intra-document references (a key naming its own attribute) are left out. */
export function referencesOf(doc: Json): Ref[] {
  const id = str(doc.id) ?? "";
  const out: Ref[] = [];
  const own = new Set(subElementIds(doc));
  const push = (pointer: string, field: string, value: unknown, required: boolean, removeItem?: string) => {
    const to = str(value);
    if (to && !own.has(to) && to !== id) out.push({ fromId: id, pointer, field, toId: to, required, removeItem });
  };
  push("/package", "package", doc.package, false);
  push("/category", "category", doc.category, false);
  switch (doc.kind) {
    case "package":
      push("/parent", "parent", doc.parent, false);
      break;
    case "entity":
      push("/base", "base", doc.base, false);
      attributeRefs(id, "/attributes", doc.attributes, out);
      break;
    case "value-object":
    case "stereotype":
      attributeRefs(id, "/attributes", doc.attributes, out);
      break;
    case "relation":
      arr(doc.ends).forEach((e, i) => {
        if (str(e.entity)) out.push({ fromId: str(e.id) ?? id, pointer: `/ends/${i}/entity`, field: "entity", toId: e.entity as string, required: true });
      });
      attributeRefs(id, "/attributes", doc.attributes, out);
      break;
    case "diagram":
      arr(doc.members).forEach((m, i) => push(`/members/${i}/element`, "element", m.element, false, `/members/${i}`));
      break;
    case "database":
      // An entry is a package id or { package, schema } (erratum E26).
      (Array.isArray(doc.packages) ? (doc.packages as unknown[]) : []).forEach((p, i) =>
        typeof p === "string"
          ? push(`/packages/${i}`, "packages", p, false, `/packages/${i}`)
          : push(`/packages/${i}/package`, "packages", (p as { package?: unknown }).package, false, `/packages/${i}`),
      );
      break;
    case "mapping":
      push("/database", "database", doc.database, true);
      push("/entity", "entity", doc.entity, true);
      push("/relation", "relation", doc.relation, true);
      push("/table", "table", doc.table, false);
      push("/foreignKeyEnd", "foreignKeyEnd", doc.foreignKeyEnd, false);
      arr(doc.attributes).forEach((a, i) => {
        push(`/attributes/${i}/attribute`, "attribute", a.attribute, false, `/attributes/${i}`);
        push(`/attributes/${i}/column`, "column", a.column, false);
      });
      arr(doc.ends).forEach((e, i) => push(`/ends/${i}/end`, "end", e.end, false, `/ends/${i}`));
      break;
    case "table":
      push("/database", "database", doc.database, true);
      push("/entity", "entity", doc.entity, false);
      push("/relation", "relation", doc.relation, false);
      push("/schema", "schema", doc.schema, false);
      arr(doc.columns).forEach((c, i) => {
        const to = str(c.attribute);
        if (to && !own.has(to)) out.push({ fromId: str(c.id) ?? id, pointer: `/columns/${i}/attribute`, field: "attribute", toId: to, required: false });
      });
      arr(doc.indexes).forEach((ix, i) =>
        arr(ix.columns).forEach((c, j) => {
          const to = str(c.column);
          if (to && !own.has(to))
            out.push({ fromId: str(ix.id) ?? id, pointer: `/indexes/${i}/columns/${j}/column`, field: "column", toId: to, required: true });
        }),
      );
      break;
    case "process": {
      push("/subject", "subject", doc.subject, false);
      push("/boundAttribute", "boundAttribute", doc.boundAttribute, false);
      arr(doc.events).forEach((e, i) =>
        arr(e.actors).forEach((_, j) => push(`/events/${i}/actors/${j}`, "actors", (e.actors as unknown[])[j], false, `/events/${i}/actors/${j}`)),
      );
      const states = (list: unknown, base: string) =>
        arr(list).forEach((st, i) => {
          arr(st.invoke).forEach((inv, k) => {
            push(`${base}/${i}/invoke/${k}/process`, "process", inv.process, false);
            arr(inv.actors).forEach((a, j) => push(`${base}/${i}/invoke/${k}/actors/${j}`, "actors", a, false, `${base}/${i}/invoke/${k}/actors/${j}`));
          });
          states(st.states, `${base}/${i}/states`);
        });
      states(doc.states, "/states");
      arr(doc.transitions).forEach((t, i) => {
        const gate = t.gate as Json | undefined;
        if (!gate) return;
        arr(gate.signers).forEach((a, j) => push(`/transitions/${i}/gate/signers/${j}`, "signers", a, true));
        arr(gate.requiredActors).forEach((a, j) =>
          push(`/transitions/${i}/gate/requiredActors/${j}`, "requiredActors", a, false, `/transitions/${i}/gate/requiredActors/${j}`),
        );
      });
      break;
    }
    case "scenario":
      push("/process", "process", doc.process, true);
      break;
    case "sequence":
    case "view":
      push("/database", "database", doc.database, true);
      push("/schema", "schema", doc.schema, false);
      break;
  }
  return out;
}

/** Ids of the sub-elements a document holds (attributes, ends, keys, members, columns…). */
export function subElementIds(doc: Json): string[] {
  const ids: string[] = [];
  const collect = (list: unknown) => arr(list).forEach((x) => str(x.id) && ids.push(x.id as string));
  collect(doc.attributes);
  collect(doc.ends);
  collect(doc.alternateKeys);
  collect(doc.categories);
  collect(doc.schemas);
  collect(doc.columns);
  collect(doc.indexes);
  collect(doc.checks);
  if (doc.kind === "enum") collect(doc.members);
  if (doc.kind === "process") {
    collect(doc.context);
    collect(doc.events);
    collect(doc.guards);
    collect(doc.actions);
    const states = (list: unknown) =>
      arr(list).forEach((st) => {
        if (str(st.id)) ids.push(st.id as string);
        collect(st.invoke);
        states(st.states);
      });
    states(doc.states);
    arr(doc.transitions).forEach((t) => {
      if (str(t.id)) ids.push(t.id as string);
      const gate = t.gate as Json | undefined;
      if (gate && str(gate.id)) ids.push(gate.id as string);
      if (gate) collect(gate.meanings);
    });
  }
  if (doc.kind === "scenario") collect(doc.steps);
  return ids;
}
