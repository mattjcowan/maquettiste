// The mock's rules for actors taken from the object, on the process side (ProcessRules.cs): MQ9604 an actor worked out from the
// object, used where it cannot be worked out (reported at the use), and MQ9606 a subject entry's owner that is no single-valued
// attribute or to-one end of its entity (actors-design.md decisions 10 and 11).
import type { Diagnostic } from "@/api/types";
import { subjectsOf } from "@/model/process";
import { holdsOneActor, objectFields, pathProblem, relativeForms, type FieldSources, type ObjectField, type RelativeDoc } from "@/model/objectFields";
import { referencesOf } from "./refs";
import type { ModelEntry } from "./validate";

type Json = Record<string, unknown>;
type Emit = (rule: string, severity: Diagnostic["severity"], message: string, pointer: string, elementId?: string) => void;

/** Field sources over a model's entries: the documents by id, the relations and the stereotypes by key. */
export function fieldSourcesOf(all: Iterable<ModelEntry>): FieldSources {
  const byId = new Map<string, Json>();
  const relations: Json[] = [];
  const stereotypes = new Map<string, Json>();
  for (const e of all) {
    byId.set(e.id, e.json);
    if (e.json.kind === "relation") relations.push(e.json);
    if (e.json.kind === "stereotype" && typeof e.json.key === "string") stereotypes.set(e.json.key, e.json);
  }
  return { doc: (id) => byId.get(id), relations, stereotype: (key) => stereotypes.get(key), kindOf: (id) => byId.get(id)?.kind as string | undefined };
}

const USE_FIELDS = new Set(["actors", "signers", "requiredActors"]);

export function relativeUseDiagnostics(json: Json, all: () => Iterable<ModelEntry>, emit: Emit): void {
  if (json.use === "library") return;
  const entries = [...all()];
  const src = fieldSourcesOf(entries);
  const memo = new Map<string, ObjectField[]>();
  const fieldsOf = (entity: string): ObjectField[] => {
    let list = memo.get(entity);
    if (!list) memo.set(entity, (list = objectFields(entity, src)));
    return list;
  };
  const name = String(json.name);
  const subjects = subjectsOf(json);

  // MQ9606: each subject entry's owner
  if (Array.isArray(json.subjects))
    subjects.forEach((one, i) => {
      if (!one.owner || !src.doc(one.entity)) return;
      const entity = String(src.doc(one.entity)?.name ?? one.entity);
      const field = fieldsOf(one.entity).find((f) => f.id === one.owner);
      const why = !field
        ? `'${one.owner}' is no attribute or end of '${entity}' or what it derives from`
        : !holdsOneActor(field)
          ? `'${field.name}' ${field.valueObject ? "is a value object" : "holds many"}, not one actor`
          : null;
      if (why)
        emit(
          "MQ9606",
          "error",
          `Subject '${entity}' of process '${name}' names an owner that is not a single-valued attribute or a to-one end of the entity: ${why}; clear the owner, or name a field that holds one actor.`,
          `/subjects/${i}/owner`,
          one.id,
        );
    });

  // MQ9604: each use of an actor from the object
  const actors = new Map(entries.filter((e) => e.json.kind === "actor" && e.json.relative && typeof e.json.relative === "object").map((e) => [e.id, e]));
  if (!actors.size) return;
  const entities = subjects.map((s) => s.entity).filter((id) => src.doc(id));
  for (const ref of referencesOf(json)) {
    const actor = USE_FIELDS.has(ref.field) ? actors.get(ref.toId) : undefined;
    if (!actor) continue;
    const relative = actor.json.relative as RelativeDoc;
    const forms = relativeForms(relative);
    if (forms.length !== 1) continue; // MQ9603 reports it
    const head = `Process '${name}' uses actor '${String(actor.json.name)}', which is worked out from the object, but`;
    if (!subjects.length) {
      emit("MQ9604", "error", `${head} it has no subjects to work it out from; give the process a subject, or name another actor.`, ref.pointer);
      continue;
    }
    if (forms[0] === "owner") continue;
    const path = forms[0] === "path" ? (relative.path ?? []) : (relative.within ?? []);
    for (const entity of entities) {
      const problem = pathProblem(entity, path, fieldsOf, forms[0] === "path" ? "field" : "end");
      if (!problem) continue;
      emit(
        "MQ9604",
        "error",
        `${head} ${forms[0] === "path" ? "its path" : "its context"} does not work out on '${String(src.doc(entity)?.name ?? entity)}': ${problem}; change the actor, or the process's subjects.`,
        ref.pointer,
      );
      break;
    }
  }
}
