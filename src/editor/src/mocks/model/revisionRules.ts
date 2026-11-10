// The mock's revision rules (revisions-design.md section 7): MQ8301 a level's shape and values, MQ8303 a separator that cannot be read back,
// MQ8305 a bound attribute that does not fit, MQ8306 a move's `nextRevision`, MQ8308 `duplicate` for an end that has one owner, MQ8312 an
// entry that cannot stand, and the revision cases of MQ9305 on a scenario. Each checks the entity's merged block through
// `resolveRevisions` (decision 9) and reports on the file that holds the offending key.
// Not checked here, since the mock lacks the machinery: MQ2020 on entries (package dependencies) and MQ9802's case for a set naming a
// revision's bound attribute.
import type { Diagnostic } from "@/api/types";
import { subjectsOf } from "@/model/process";
import { isRevisionValue, readRevision, resolveRevisions, type MergedRevisions, type RevisionsBlock, type SequenceLevel } from "@/model/revisionSequence";
import { Coverage } from "./coverage";
import type { ModelEntry } from "./validate";

type Json = Record<string, unknown>;
type Emit = (rule: string, severity: Diagnostic["severity"], message: string, pointer: string, elementId?: string) => void;

const arr = (v: unknown): Json[] => (Array.isArray(v) ? (v as Json[]) : []);
const str = (v: unknown): string | null => (typeof v === "string" && v !== "" ? v : null);

/** The entity and the entities it is based on, nearest first, as documents. */
function chainOf(entity: Json, lookup: (id: string) => Json | undefined): Json[] {
  const out: Json[] = [];
  for (let at: Json | undefined = entity; at && !out.includes(at); at = typeof at.base === "string" ? lookup(at.base) : undefined) out.push(at);
  return out;
}

/** The model as the rules read it, built once per call. */
class World {
  readonly docs = new Map<string, Json>();
  readonly lifecycles: Json[] = [];
  readonly entities: Json[] = [];
  readonly coverage: Coverage;
  constructor(entries: Iterable<ModelEntry>) {
    const list = [...entries];
    for (const e of list) {
      this.docs.set(e.id, e.json);
      if (e.json.kind === "process" && e.json.use === "lifecycle") this.lifecycles.push(e.json);
      if (e.json.kind === "entity") this.entities.push(e.json);
    }
    this.coverage = new Coverage(list);
  }
  lookup = (id: string) => this.docs.get(id);
  merged(entity: Json): MergedRevisions | null {
    const id = String(entity.id);
    const governing = this.lifecycles.filter((l) => this.coverage.governs(l, id)).map((l) => String(l.id));
    return resolveRevisions(id, new Map(), (x) => this.docs.get(x) as never, governing);
  }
  /** The entity and every entity derived from it. */
  family(id: string): string[] {
    return this.entities.filter((e) => chainOf(e, this.lookup).some((c) => c.id === id)).map((e) => String(e.id));
  }
}

const name = (j: Json) => String(j.name ?? j.id ?? "?");

/** MQ8301, MQ8303, MQ8305, MQ8308 and MQ8312 on an entity with its own block. */
function entityRules(entity: Json, world: World, emit: Emit): void {
  const block = entity.revisions as RevisionsBlock | undefined;
  if (!block || typeof block !== "object") return;
  const id = String(entity.id);
  const who = name(entity);
  const merged = world.merged(entity)!;
  const separator = merged.separator;
  const own = block.levels ?? [];

  // MQ8301: one message per fault.
  if (!merged.levels.length)
    emit("MQ8301", "error", `'${who}' has a revisions block, but neither it nor a base names levels; give it a levels list.`, "/revisions", id);
  const names = new Set<string>();
  own.forEach((level: SequenceLevel, i) => {
    const at = `/revisions/levels/${i}`;
    const label = `Level ${i + 1}${level.name ? ` '${level.name}'` : ""} of '${who}'`;
    const hasFormat = level.format !== undefined;
    const hasValues = level.values !== undefined;
    if (hasFormat && hasValues) emit("MQ8301", "error", `${label} has both format and values; keep one.`, at, id);
    else if (!hasFormat && !hasValues) emit("MQ8301", "error", `${label} has neither format nor values; give one.`, at, id);
    if (level.skip !== undefined && level.format !== "letters")
      emit("MQ8301", "error", `${label} has skip, which only a letters level takes; remove it.`, `${at}/skip`, id);
    if (level.width !== undefined && level.format !== "numbers")
      emit("MQ8301", "error", `${label} has width, which only a numbers level takes; remove it.`, `${at}/width`, id);
    if (hasValues)
      for (const key of ["first", "skip", "width"] as const)
        if (level[key] !== undefined)
          emit("MQ8301", "error", `${label} has a list of values and ${key}; a list takes none of first, skip and width. Remove ${key}.`, `${at}/${key}`, id);
    if (level.name !== undefined) {
      if (names.has(level.name)) emit("MQ8301", "error", `${label} repeats the name '${level.name}'; give each level its own name.`, `${at}/name`, id);
      names.add(level.name);
    }
    if (i > 0 && !level.startEmpty && own.slice(0, i).some((l) => l.startEmpty))
      emit("MQ8301", "error", `${label} does not start empty, but a level above it does; every level below one that starts empty starts empty too.`, at, id);
    if (level.first !== undefined && level.format === "letters") {
      const first = String(level.first);
      if (!/^[A-Z]+$/.test(first) || [...first].some((c) => (level.skip ?? []).includes(c)))
        emit("MQ8301", "error", `${label} has first '${first}', which is not a value of its letters (upper case, not skipped).`, `${at}/first`, id);
    }
    if (level.first !== undefined && level.format === "numbers" && !(typeof level.first === "number" && Number.isInteger(level.first) && level.first >= 0))
      emit("MQ8301", "error", `${label} has first '${String(level.first)}', which is not a whole number from 0.`, `${at}/first`, id);
    if (hasValues) {
      const seen = new Set<string>();
      (level.values ?? []).forEach((v, j) => {
        const vat = `${at}/values/${j}`;
        if (v === "") emit("MQ8301", "error", `${label} has an empty value; every value needs text.`, vat, id);
        else if (seen.has(v)) emit("MQ8301", "error", `${label} lists '${v}' twice; give each value once.`, vat, id);
        else if (separator !== "" && v.includes(separator))
          emit("MQ8301", "error", `${label} has the value '${v}', which holds the separator '${separator}'; change one of them.`, vat, id);
        seen.add(v);
      });
    }
  });

  // MQ8303: written revisions that cannot be read back (checked where the levels or the separator are written).
  if (own.length || block.separator !== undefined) {
    const levels = merged.levels;
    if (/[A-Za-z0-9]/.test(separator)) {
      emit(
        "MQ8303",
        "error",
        `'${who}' uses the separator '${separator}', which holds a letter or a digit, so its revisions cannot be read back; use another separator.`,
        "/revisions/separator",
        id,
      );
    } else if (separator === "") {
      const kind = (l: SequenceLevel) => (l.values ? "list" : l.format);
      const bad = levels.findIndex(
        (l, i) => i > 0 && !((kind(levels[i - 1]) === "letters" && kind(l) === "numbers") || (kind(levels[i - 1]) === "numbers" && kind(l) === "letters")),
      );
      if (bad > 0)
        emit(
          "MQ8303",
          "error",
          `'${who}' writes levels ${bad} and ${bad + 1} with no separator, and they are not one letters and one numbers level, so its revisions could be read two ways; give a separator.`,
          "/revisions/separator",
          id,
        );
    }
  }

  const chain = chainOf(entity, world.lookup);

  // MQ8305: the bound attribute.
  if (block.boundAttribute) {
    const at = "/revisions/boundAttribute";
    const attribute = chain.flatMap((c) => arr(c.attributes)).find((a) => a.id === block.boundAttribute);
    if (!attribute) emit("MQ8305", "error", `The bound attribute of '${who}' is not an attribute it holds; pick one of its attributes.`, at, id);
    else {
      const aname = String(attribute.name);
      const type = attribute.type as Json | string | undefined;
      const textual = (t: unknown): boolean => {
        for (let x = t, hops = 0; hops < 8; hops++) {
          if (x === "string" || x === "text") return true;
          const ref = typeof x === "object" && x !== null ? str((x as Json).ref) : null;
          const scalar = ref ? world.lookup(ref) : typeof x === "string" ? world.lookup(x) : undefined;
          if (!scalar || scalar.kind !== "scalar-type") return false;
          x = scalar.base;
        }
        return false;
      };
      if (attribute.derived)
        emit("MQ8305", "error", `Attribute '${aname}' of '${who}' is derived, so it cannot hold the revision; hold it in an attribute that is stored.`, at, id);
      else if (attribute.collection === true)
        emit("MQ8305", "error", `Attribute '${aname}' of '${who}' holds many values; hold the revision in a single-valued attribute.`, at, id);
      else if (!textual(type))
        emit(
          "MQ8305",
          "error",
          `Attribute '${aname}' of '${who}' is ${typeof type === "string" ? `a ${type}` : "not a string"}, and its revisions are text; hold the revision in a string attribute.`,
          at,
          id,
        );
      else if (
        world.lifecycles.some((l) => subjectsOf(l).some((s) => s.boundAttribute === block.boundAttribute && !!s.entity && chain.some((c) => c.id === s.entity)))
      )
        emit("MQ8305", "error", `Attribute '${aname}' of '${who}' is the bound attribute of a lifecycle; hold the revision in another attribute.`, at, id);
      else if (merged.attributes.some((a) => a.attribute === block.boundAttribute))
        emit("MQ8305", "error", `Attribute '${aname}' of '${who}' holds the revision and is also listed under attributes; remove the entry.`, at, id);
      else if (typeof attribute.default === "string" && merged.levels.length && !isRevisionValue({ levels: merged.levels, separator }, attribute.default))
        emit(
          "MQ8305",
          "error",
          `The default '${attribute.default}' of attribute '${aname}' of '${who}' is not a value of its revision sequence; change the default.`,
          at,
          id,
        );
    }
  }

  // MQ8308 and MQ8312 on the entries.
  const held = new Map(chain.flatMap((c) => arr(c.attributes)).map((a) => [String(a.id), a] as const));
  const keyIds = new Set(chain.flatMap((c) => ((c.key as Json | undefined)?.attributes as string[] | undefined) ?? []));
  const seen = { lifecycles: new Set<string>(), attributes: new Set<string>(), links: new Set<string>() };
  const family = world.family(id);
  arr(block.lifecycles).forEach((e, i) => {
    const at = `/revisions/lifecycles/${i}`;
    const lifecycle = world.lookup(String(e.lifecycle));
    const lname = String(lifecycle?.name ?? e.lifecycle);
    if (seen.lifecycles.has(String(e.lifecycle))) emit("MQ8312", "error", `'${who}' has two entries for lifecycle '${lname}'; keep one.`, at, id);
    seen.lifecycles.add(String(e.lifecycle));
    if (!lifecycle || !family.some((f) => world.coverage.governs(lifecycle, f)))
      return emit("MQ8312", "error", `Lifecycle '${lname}' governs neither '${who}' nor an entity derived from it; remove the entry.`, `${at}/lifecycle`, id);
    const onRevise = String(e.onRevise);
    if (onRevise !== "restart" && onRevise !== "keep") {
      const state = (function find(list: unknown): Json | undefined {
        for (const s of arr(list)) {
          if (s.id === onRevise) return s;
          const inner = find(s.states);
          if (inner) return inner;
        }
        return undefined;
      })(lifecycle.states);
      if (!state)
        emit("MQ8312", "error", `'${onRevise}' is not a state of lifecycle '${lname}'; pick one of its states, or restart or keep.`, `${at}/onRevise`, id);
      else if (state.type === "history" || state.type === "choice")
        emit(
          "MQ8312",
          "error",
          `State '${String(state.name)}' of lifecycle '${lname}' is a ${String(state.type)} state, which a revise cannot go to; pick another state.`,
          `${at}/onRevise`,
          id,
        );
    }
  });
  arr(block.attributes).forEach((e, i) => {
    const at = `/revisions/attributes/${i}`;
    const attribute = held.get(String(e.attribute));
    const aname = String(attribute?.name ?? e.attribute);
    if (seen.attributes.has(String(e.attribute))) emit("MQ8312", "error", `'${who}' has two entries for attribute '${aname}'; keep one.`, at, id);
    seen.attributes.add(String(e.attribute));
    if (!attribute) return emit("MQ8312", "error", `'${String(e.attribute)}' is not an attribute '${who}' holds; remove the entry.`, `${at}/attribute`, id);
    if (attribute.derived)
      return emit("MQ8312", "error", `Attribute '${aname}' is derived, so a revise cannot reset it; remove the entry.`, `${at}/attribute`, id);
    if (world.lifecycles.some((l) => subjectsOf(l).some((s) => s.boundAttribute === e.attribute)))
      return emit(
        "MQ8312",
        "error",
        `Attribute '${aname}' is a lifecycle's bound state attribute, so a revise cannot reset it; remove the entry.`,
        `${at}/attribute`,
        id,
      );
    if (keyIds.has(String(e.attribute)) && e.onRevise !== "copy")
      emit("MQ8312", "error", `Attribute '${aname}' is part of the key of '${who}', so a revision can't reset it; remove the entry.`, `${at}/attribute`, id);
  });
  // The ends the type reaches: for a relation of two ends, the end whose other end covers the entity (or one derived from it).
  const reached = new Map<string, { relation: Json; end: Json; own: Json | undefined; ends: number }>();
  for (const r of world.docs.values()) {
    if (r.kind !== "relation") continue;
    const ends = arr(r.ends);
    ends.forEach((end, k) => {
      const other = ends[1 - k];
      const covers =
        ends.length === 2 &&
        !!other &&
        (other.anyEntity === true || [other.entity, ...arr(other.types).map((t) => t.entity)].some((x) => typeof x === "string" && family.includes(x)));
      reached.set(String(end.id), { relation: r, end, own: covers ? other : undefined, ends: ends.length });
    });
  }
  arr(block.links).forEach((e, i) => {
    const at = `/revisions/links/${i}`;
    const found = reached.get(String(e.end));
    const label = found ? `${name(found.relation)}.${String(found.end.role)}` : String(e.end);
    if (seen.links.has(String(e.end))) emit("MQ8312", "error", `'${who}' has two entries for end '${label}'; keep one.`, at, id);
    seen.links.add(String(e.end));
    if (!found) return emit("MQ8312", "error", `'${String(e.end)}' is not a relation end; remove the entry.`, `${at}/end`, id);
    if (found.ends > 2)
      return emit(
        "MQ8312",
        "error",
        `End '${label}' belongs to a relation of more than two ends, which a revise cannot say anything about yet; remove the entry.`,
        `${at}/end`,
        id,
      );
    if (!found.own) return emit("MQ8312", "error", `'${who}' does not reach end '${label}'; remove the entry.`, `${at}/end`, id);
    if (e.onRevise === "duplicate" && found.own.max === 1)
      emit(
        "MQ8308",
        "error",
        `Revising '${who}' can't duplicate its '${String(found.end.role)}' links: each one has a single owner; use move or stay.`,
        `${at}/onRevise`,
        id,
      );
  });
}

/** MQ8306: a transition's `nextRevision` on a lifecycle that governs a type with revisions that has the level. */
function processRules(process: Json, world: World, emit: Emit): void {
  const transitions = arr(process.transitions);
  const id = String(process.id);
  const lifecycle = process.use === "lifecycle";
  const governed = lifecycle ? world.entities.filter((e) => world.coverage.governs(process, String(e.id))) : [];
  const withRevisions = governed.flatMap((e) => {
    const m = world.merged(e);
    return m && m.levels.length ? [{ entity: e, merged: m }] : [];
  });
  transitions.forEach((t, i) => {
    if (typeof t.nextRevision !== "number") return;
    const at = `/transitions/${i}/nextRevision`;
    const label = `Transition ${String(t.id)}`;
    if (!lifecycle) return emit("MQ8306", "error", `${label} names a revision level, but '${name(process)}' is not a lifecycle; remove it.`, at, id);
    if (!withRevisions.length)
      return emit(
        "MQ8306",
        "error",
        `${label} names revision level ${t.nextRevision}, but '${name(process)}' governs no type with revisions; add revisions to the type, or remove it.`,
        at,
        id,
      );
    const lacking = withRevisions.find((w) => w.merged.levels.length < (t.nextRevision as number));
    if (lacking) {
      const n = lacking.merged.levels.length;
      emit(
        "MQ8306",
        "error",
        `${label} names revision level ${t.nextRevision}, but entity '${name(lacking.entity)}' has ${n === 1 ? "one level" : `${n} levels`}; name level 1${n > 1 ? ` to ${n}` : ""}, or add a level.`,
        at,
        id,
      );
    }
  });
}

/** The revision cases of MQ9305 on a scenario. */
function scenarioRules(scenario: Json, world: World, emit: Emit): void {
  const id = String(scenario.id);
  const process = world.lookup(String(scenario.process));
  const objects = arr(scenario.objects);
  const subject = objects.find((o) => o.id === scenario.subject);
  const subjectEntity = subject ? world.lookup(String(subject.entity)) : undefined;
  const merged = subjectEntity ? world.merged(subjectEntity) : null;
  const seq = merged && merged.levels.length ? { levels: merged.levels, separator: merged.separator } : null;
  objects.forEach((o, i) => {
    const entity = world.lookup(String(o.entity));
    const m = entity ? world.merged(entity) : null;
    if (o.revision !== undefined) {
      const at = `/objects/${i}/revision`;
      if (!m) emit("MQ9305", "error", `Object '${String(o.name)}' has a revision, but its entity has no revisions; remove it.`, at, id);
      else if (m.boundAttribute)
        emit(
          "MQ9305",
          "error",
          `Object '${String(o.name)}' has a revision, and its revision is held in an attribute; set the attribute in values instead.`,
          at,
          id,
        );
      else if (!isRevisionValue({ levels: m.levels, separator: m.separator }, String(o.revision)))
        emit(
          "MQ9305",
          "error",
          `Object '${String(o.name)}' has revision '${String(o.revision)}', which is not a value of its entity's revision sequence; give a value of the sequence.`,
          at,
          id,
        );
    }
    // A bound revision attribute's value in values that is not a value of the sequence.
    const v = m?.boundAttribute ? (o.values as Json | undefined)?.[m.boundAttribute] : undefined;
    if (m && typeof v === "string" && !isRevisionValue({ levels: m.levels, separator: m.separator }, v))
      emit(
        "MQ9305",
        "error",
        `Object '${String(o.name)}' holds revision '${v}', which is not a value of its entity's revision sequence; give a value of the sequence.`,
        `/objects/${i}/values`,
        id,
      );
  });
  const events = new Map(arr(process?.events).map((e) => [String(e.id), e]));
  arr(scenario.steps).forEach((s, i) => {
    const expect = s.expect as Json | undefined;
    const at = `/steps/${i}`;
    if (s.revision !== undefined) {
      if (!merged || !seq) emit("MQ9305", "error", `Step ${i + 1} has a revision, but the subject's type has no revisions; remove it.`, `${at}/revision`, id);
      else {
        const input = String(s.input ?? "event");
        const event = events.get(String(s.event));
        const takes =
          (input === "verb" && s.verb === "revise") ||
          (input === "event" &&
            !!event &&
            (event.verb === "revise" || arr(process?.transitions).some((t) => t.event === event.id && t.nextRevision !== undefined)));
        if (!takes) emit("MQ9305", "error", `Step ${i + 1} has a revision, but it cannot revise or label the revision; remove it.`, `${at}/revision`, id);
        else if (typeof s.revision === "number") {
          if (input === "event")
            emit(
              "MQ9305",
              "error",
              `Step ${i + 1} is an event step, which takes a revision name, not a level; give a name or remove it.`,
              `${at}/revision`,
              id,
            );
          else if (s.revision < 1 || s.revision > merged.levels.length)
            emit(
              "MQ9305",
              "error",
              `Step ${i + 1} names revision level ${s.revision}, but the subject's type has ${merged.levels.length === 1 ? "one level" : `${merged.levels.length} levels`}; name a level it has.`,
              `${at}/revision`,
              id,
            );
        } else if (typeof s.revision === "string" && !readRevision(seq, s.revision))
          emit(
            "MQ9305",
            "error",
            `Step ${i + 1} names revision '${s.revision}', which the subject's revision sequence cannot form; give a value of the sequence.`,
            `${at}/revision`,
            id,
          );
      }
    }
    if (expect?.revision !== undefined && !merged)
      emit("MQ9305", "error", `Step ${i + 1} expects a revision, but the subject's type has no revisions; remove it.`, `${at}/expect/revision`, id);
    if (s.input === "verb" && s.verb === "revise" && s.end !== undefined && merged)
      emit(
        "MQ9305",
        "error",
        `Step ${i + 1} revises with an end, but a subject whose type has revisions is revised as a whole; remove the end.`,
        `${at}/end`,
        id,
      );
  });
}

/** The revision rules for one entry. Nothing is built for a model that uses none of this. */
export function revisionDiagnostics(entry: ModelEntry, all: () => Iterable<ModelEntry>, emit: Emit): void {
  const json = entry.json;
  if (json.kind === "entity") {
    if (json.revisions) entityRules(json, new World(all()), emit);
  } else if (json.kind === "process") {
    if (arr(json.transitions).some((t) => t.nextRevision !== undefined)) processRules(json, new World(all()), emit);
  } else if (json.kind === "scenario") {
    const uses =
      arr(json.objects).some((o) => o.revision !== undefined) ||
      arr(json.steps).some(
        (s) => s.revision !== undefined || (s.expect as Json | undefined)?.revision !== undefined || (s.verb === "revise" && s.end !== undefined),
      );
    if (uses) scenarioRules(json, new World(all()), emit);
  }
}
