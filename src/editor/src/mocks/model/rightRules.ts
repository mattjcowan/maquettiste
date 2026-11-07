// The mock's rights rules (rights-design.md section 5, the texts of RuleCatalog.cs): MQ9901 a rights entry that is both an
// inline right and a use, or neither, a use of something that is no named right, and rights on a process that is no lifecycle;
// MQ9902 a state that is no state of the lifecycle; MQ9903 ends the subjects don't all hold, or beside a verb that can't act on
// a link; MQ9904 an allow right where the default allows; MQ9905 an event or task no one can do under a deny default; MQ9906 an
// except entry the same right's actors name. MQ3001 reports two inline rights of one lifecycle with one name. The rules that
// read a condition need the expression engine, which the mock lacks, so a condition is only checked for the ids it names.
import type { Diagnostic } from "@/api/types";
import { commonFields, objectFields } from "@/model/objectFields";
import { subjectsOf } from "@/model/process";
import { ATTRIBUTE_VERBS, LINK_VERBS } from "@/model/rights";
import { fieldSourcesOf } from "./relativeRules";
import type { ModelEntry } from "./validate";

type Json = Record<string, unknown>;
type Emit = (rule: string, severity: Diagnostic["severity"], message: string, pointer: string, elementId?: string) => void;

const arr = (v: unknown): Json[] => (Array.isArray(v) ? (v as Json[]) : []);
const strings = (v: unknown): string[] => (Array.isArray(v) ? v.filter((x): x is string => typeof x === "string") : []);
const str = (v: unknown): string | null => (typeof v === "string" && v !== "" ? v : null);

/** The keys that say what an inline right allows or denies: none of them may stand beside `right`. */
const INLINE_KEYS = ["name", "displayName", "access", "actors", "except", "verbs", "ends", "attributes", "whileFrozen", "condition"] as const;

/** The ids of the states a right may name: atomic, compound, parallel and final ones, not history or choice (MQ9902). */
function ownStates(json: Json): Set<string> {
  const ids = new Set<string>();
  const walk = (list: unknown) =>
    arr(list).forEach((s) => {
      if (s.type !== "history" && s.type !== "choice" && str(s.id)) ids.add(s.id as string);
      walk(s.states);
    });
  walk(json.states);
  return ids;
}

/** MQ9906 for one right's own lists; `pointer` is where its `except` sits. */
function exceptFindings(right: Json, nameOf: (id: string) => string, pointer: string, emit: Emit, holder: string, what: string): void {
  const actors = new Set(strings(right.actors));
  strings(right.except).forEach((id, j) => {
    if (actors.has(id))
      emit(
        "MQ9906",
        "warning",
        `${what} names '${nameOf(id)}' in both actors and except, so it never applies to it; remove it from except.`,
        `${pointer}/except/${j}`,
        holder,
      );
  });
}

export function rightDiagnostics(json: Json, all: () => Iterable<ModelEntry>, emit: Emit): void {
  const kind = json.kind;
  if (kind === "scenario") return scenarioDiagnostics(json, emit);
  if (kind !== "process" && kind !== "right") return;
  const entries = [...all()];
  const byId = new Map(entries.map((e) => [e.id, e.json]));
  const nameOf = (id: string) => String(byId.get(id)?.name ?? id);
  const name = String(json.name ?? "");
  if (kind === "right") {
    exceptFindings(json, nameOf, "", emit, String(json.id), `The named right '${name}'`);
    return;
  }
  const rights = arr(json.rights);
  const lifecycle = json.use === "lifecycle";
  if (!lifecycle) {
    if (json.rights !== undefined)
      emit("MQ9901", "error", `'${name}' is not a lifecycle, so it holds no rights; only lifecycles do. Remove the entry.`, "/rights");
    if (json.rightsDefault !== undefined)
      emit("MQ9901", "error", `'${name}' is not a lifecycle, so it has no rightsDefault; only lifecycles do. Remove the entry.`, "/rightsDefault");
    return;
  }
  const denies = json.rightsDefault === "deny";
  const states = ownStates(json);
  const subjects = subjectsOf(json);
  // The ends every subject holds, lazily: only a right that names ends needs them.
  let held: Set<string> | null = null;
  const subjectEnds = (): Set<string> => {
    if (held) return held;
    const src = fieldSourcesOf(entries);
    const lists = subjects.map((s) => (s.entity !== undefined ? objectFields(s.entity, src).filter((f) => f.kind === "end") : []));
    return (held = new Set(subjects.length ? commonFields(lists).map((f) => f.id) : []));
  };
  let heldAttributes: Set<string> | null = null;
  const subjectAttributes = (): Set<string> => {
    if (heldAttributes) return heldAttributes;
    const src = fieldSourcesOf(entries);
    const lists = subjects.map((s) => (s.entity !== undefined ? objectFields(s.entity, src).filter((f) => f.kind === "attribute") : []));
    return (heldAttributes = new Set(subjects.length ? commonFields(lists).map((f) => f.id) : []));
  };
  const names = new Map<string, number>();
  const allowing = new Set<string>();
  rights.forEach((entry, i) => {
    const at = `/rights/${i}`;
    const id = str(entry.id) ?? undefined;
    const use = str(entry.right);
    const named = use ? byId.get(use) : undefined;
    if (use) {
      const inline = INLINE_KEYS.filter((k) => entry[k] !== undefined);
      if (inline.length)
        emit(
          "MQ9901",
          "error",
          `A rights entry is a use of a named right or an inline right, not both: this one names a right and also says ${inline.join(", ")}. Remove the entry.`,
          at,
          id,
        );
      if (named && named.kind !== "right")
        emit("MQ9901", "error", `A use must name a named right, and '${nameOf(use)}' is a ${String(named.kind)}. Remove the entry.`, `${at}/right`, id);
    } else if (!str(entry.name) || !str(entry.access) || strings(entry.verbs).length === 0)
      emit("MQ9901", "error", "An inline right needs a name, an access and at least one verb. Remove the entry or complete it.", at, id);
    const source = use ? (named?.kind === "right" ? named : null) : entry;
    strings(entry.states).forEach((s, j) => {
      if (!states.has(s))
        emit(
          "MQ9902",
          "error",
          `A right names '${nameOf(s)}' as a state, which is not a state of '${name}' (a history or a choice state does not count). Remove the state.`,
          `${at}/states/${j}`,
          id,
        );
    });
    if (!source) return;
    const label = use ? `The named right '${String(source.name)}'` : `The right '${String(source.name ?? "")}'`;
    const verbs = strings(source.verbs);
    const ends = strings(source.ends);
    if (ends.length) {
      const bad = verbs.filter((v) => !LINK_VERBS.has(v));
      if (bad.length)
        emit(
          "MQ9903",
          "error",
          `${label} names ends, but ${bad.join(", ")} cannot act on a link; only connect, disconnect, read, modify, freeze and thaw can. Remove the ends.`,
          use ? at : `${at}/ends`,
          id,
        );
      const have = subjectEnds();
      ends.forEach((e, j) => {
        if (!have.has(e))
          emit(
            "MQ9903",
            "error",
            `${label} names the end '${nameOf(e)}', which not every subject of '${name}' holds. Remove it.`,
            use ? at : `${at}/ends/${j}`,
            id,
          );
      });
    }
    const attributes = strings(source.attributes);
    if (attributes.length) {
      const bad = verbs.filter((v) => !ATTRIBUTE_VERBS.has(v));
      if (bad.length)
        emit(
          "MQ9903",
          "error",
          `${label} names attributes, but ${bad.join(", ")} cannot act on attributes; only read and modify can. Remove the attributes.`,
          use ? at : `${at}/attributes`,
          id,
        );
      if (ends.length)
        emit(
          "MQ9903",
          "error",
          `${label} names attributes beside ends: with ends it speaks of links, which hold none of the subject's attributes. Remove the attributes or the ends, or split the right.`,
          use ? at : `${at}/attributes`,
          id,
        );
      const have = subjectAttributes();
      attributes.forEach((a, j) => {
        if (!have.has(a))
          emit(
            "MQ9903",
            "error",
            `${label} names the attribute '${nameOf(a)}', which not every subject of '${name}' holds. Remove it.`,
            use ? at : `${at}/attributes/${j}`,
            id,
          );
      });
    }
    if (!use) {
      const key = String(source.name ?? "").toLowerCase();
      if (key) {
        if (names.has(key))
          emit("MQ3001", "error", `Right name '${String(source.name)}' is used twice in '${name}' (also by right ${names.get(key)}).`, `${at}/name`, id);
        else names.set(key, i + 1);
      }
      exceptFindings(source, nameOf, at, emit, id ?? "", label);
    }
    if (source.access === "allow") {
      if (attributes.length === 0) for (const v of verbs) allowing.add(v);
      if (!denies)
        emit(
          "MQ9904",
          "warning",
          `${label} allows, but '${name}' allows by default, so it cannot change any answer (a deny still wins over it). Remove it, or set rightsDefault to deny.`,
          use ? at : `${at}/access`,
          id,
        );
    }
  });
  if (!denies) return;
  // MQ9905: under deny, an event or a human task no one can ever do.
  const signers = (event: string) =>
    arr(json.transitions).some((t) => t.event === event && arr(t.gate ? [t.gate] : []).some((g) => strings(g.signers).length > 0));
  arr(json.events).forEach((e, i) => {
    const verb = str(e.verb);
    const reach = strings(e.actors).length > 0 || signers(String(e.id)) || (verb !== null && allowing.has(verb));
    if (!reach)
      emit(
        "MQ9905",
        "warning",
        verb === null
          ? `The event '${String(e.name)}' has no verb, no actors and no signers, so under deny by default no one can send it. Add actors.`
          : `The event '${String(e.name)}' has no actors, and no right allows '${verb}', so under deny by default no one can send it. Add actors, or add an allow right.`,
        `/events/${i}`,
        str(e.id) ?? undefined,
      );
  });
  const tasks = (list: unknown, base: string): void =>
    arr(list).forEach((s, i) => {
      arr(s.invoke).forEach((inv, k) => {
        if (inv.type === "human-task" && strings(inv.actors).length === 0 && !allowing.has("complete"))
          emit(
            "MQ9905",
            "warning",
            `The task '${String(inv.name)}' has no actors, and no right allows 'complete', so under deny by default no one can complete it. Add actors, or add an allow right.`,
            `${base}/${i}/invoke/${k}`,
            str(inv.id) ?? undefined,
          );
      });
      tasks(s.states, `${base}/${i}/states`);
    });
  tasks(json.states, "/states");
}

/** MQ9305, part 2: `attributes` beside a verb other than `read`, and `frozen` on a link of an object other than the subject. */
function scenarioDiagnostics(json: Json, emit: Emit): void {
  const id = str(json.id) ?? undefined;
  arr(json.objects).forEach((o, i) =>
    arr(o.links).forEach((l, j) => {
      if (l.frozen === true && o.id !== json.subject)
        emit(
          "MQ9305",
          "error",
          `The link of '${String(o.name)}' is frozen, but only the subject's links are read; freeze a link of the subject, or remove frozen.`,
          `/objects/${i}/links/${j}/frozen`,
          id,
        );
    }),
  );
  arr(json.steps).forEach((st, i) => {
    if (st.input === "verb" && strings(st.attributes).length > 0 && st.verb !== "read")
      emit(
        "MQ9305",
        "error",
        `Step ${i + 1} names attributes beside the verb '${String(st.verb ?? "")}'; only a read step names attributes. Remove them.`,
        `/steps/${i}/attributes`,
        id,
      );
  });
}
