// The mock's rules for an action's sets (ProcessRules.cs, object-effects-design.md section 5): MQ9801 a set's attribute is not
// one every subject of the process holds under one id (unknown, another entity's, a collection or a value object; no subjects; a
// context attribute named `subject` hides the object), MQ9802 it is derived or holds a lifecycle's state, MQ9803 one action
// sets one attribute twice. Rules that read a value (MQ3103, MQ3104, MQ3116) need the expression engine, which the mock lacks.
import { bindableAttributes } from "@/model/binding";
import { subjectsOf } from "@/model/process";

type Json = Record<string, unknown>;
const arr = (v: unknown): Json[] => (Array.isArray(v) ? (v as Json[]) : []);

export interface EffectFinding {
  rule: "MQ9801" | "MQ9802" | "MQ9803";
  message: string;
  pointer: string;
}

export function setDiagnostics(
  json: Json,
  lookup: (id: string) => Json | undefined,
  stereotype: (key: string) => Json | undefined,
  lifecycles: Iterable<Json>,
): EffectFinding[] {
  const actions = arr(json.actions);
  if (!actions.some((a) => arr(a.sets).length)) return [];
  const name = String(json.name ?? "");
  const entries = subjectsOf(json);
  const perSubject = entries.map((e) =>
    e.stereotype !== undefined ? arr(stereotype(e.stereotype)?.attributes) : bindableAttributes(lookup(e.entity ?? ""), lookup, stereotype),
  );
  const held = perSubject.length ? perSubject[0].filter((a) => perSubject.every((list) => list.some((b) => b.id === a.id))) : [];
  const hidden = arr(json.context).some((a) => a.name === "subject");
  // The attributes that hold a state: this process's own and those of every other lifecycle that governs one of its subjects.
  const entities = new Set(entries.flatMap((e) => (e.entity ? [e.entity] : [])));
  const bound = new Set<string>();
  for (const e of entries) if (e.boundAttribute) bound.add(e.boundAttribute);
  if (typeof json.boundAttribute === "string") bound.add(json.boundAttribute);
  for (const p of lifecycles)
    if (p.id !== json.id && p.use === "lifecycle" && subjectsOf(p).some((e) => e.entity && entities.has(e.entity) && e.boundAttribute))
      for (const e of subjectsOf(p)) if (e.entity && entities.has(e.entity) && e.boundAttribute) bound.add(e.boundAttribute);
  const out: EffectFinding[] = [];
  actions.forEach((action, i) => {
    const seen = new Set<string>();
    arr(action.sets).forEach((set, j) => {
      const at = `/actions/${i}/sets/${j}`;
      const attribute = String(set.attribute);
      const found = held.find((a) => a.id === attribute);
      const which = `action '${String(action.name)}'`;
      if (!entries.length) {
        out.push({
          rule: "MQ9801",
          message: `A set of ${which} names an attribute, but process '${name}' has no subject; give the process a subject, or remove the set.`,
          pointer: `${at}/attribute`,
        });
      } else if (hidden) {
        out.push({
          rule: "MQ9801",
          message: `A set of ${which} cannot name the object: a context attribute of process '${name}' is named subject and hides it; rename the attribute.`,
          pointer: `${at}/attribute`,
        });
      } else if (!found || found.collection === true || lookup(String(((found.type as Json | undefined) ?? {}).ref ?? ""))?.kind === "value-object") {
        out.push({
          rule: "MQ9801",
          message: `A set of ${which} names ${found ? `the attribute '${String(found.name)}', which cannot be set (a collection or a value object)` : "an attribute that every subject of the process does not hold under one id"}; pick an attribute of the subject, or remove the set.`,
          pointer: `${at}/attribute`,
        });
      } else if (found.derived || bound.has(attribute)) {
        out.push({
          rule: "MQ9802",
          message: `A set of ${which} names '${String(found.name)}', which ${found.derived ? "is derived from other attributes" : "holds a lifecycle's state"}; only its chart can move it. Remove the set.`,
          pointer: `${at}/attribute`,
        });
      }
      if (seen.has(attribute))
        out.push({ rule: "MQ9803", message: `${which[0].toUpperCase()}${which.slice(1)} sets one attribute twice; remove the second set.`, pointer: at });
      seen.add(attribute);
    });
  });
  return out;
}
