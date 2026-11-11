// The links between operations and processes (processes-operations-design.md sections 7 and 12): which operations fire an
// event (read from the references of the event), the order the Add event picker lists processes in, and how an event's
// payload reads in small text. Pure functions over the model index and documents.
import type { ElementSummary, ReferenceInfo } from "@/api/types";

type Rec = Record<string, unknown>;

/** The ids of the operations that fire an event: the referrers whose field is `fires`, once each, in the order given. */
export function firedByIds(references: readonly ReferenceInfo[] | undefined): string[] {
  const out: string[] = [];
  for (const r of references ?? []) if (r.field === "fires" && !out.includes(r.fromElementId)) out.push(r.fromElementId);
  return out;
}

/** Whether a process row governs an entity (its one subject, or one of its several). */
export function governsEntity(row: ElementSummary, entity: string | undefined): boolean {
  return !!entity && (row.subject === entity || (row.subjects ?? []).includes(entity));
}

/** Lifecycles and orchestrations (the processes that have events), those governing `subject` first, each group by name; `filter` matches the name. */
export function groupProcessesForSubject(
  processes: readonly ElementSummary[],
  subject: string | undefined,
  filter = "",
): { on: ElementSummary[]; other: ElementSummary[] } {
  const needle = filter.trim().toLowerCase();
  const byName = (a: ElementSummary, b: ElementSummary) => (a.name < b.name ? -1 : a.name > b.name ? 1 : 0);
  const shown = processes
    .filter((p) => p.kind === "process" && p.use !== "library")
    .filter((p) => !needle || p.name.toLowerCase().includes(needle) || (p.displayName ?? "").toLowerCase().includes(needle))
    .sort(byName);
  return { on: shown.filter((p) => governsEntity(p, subject)), other: shown.filter((p) => !governsEntity(p, subject)) };
}

/** Operations whose subject is one of `subjects` first (the picker of an invoke's operation), each group by name. */
export function operationsForSubjects(operations: readonly ElementSummary[], subjects: readonly string[]): { on: ElementSummary[]; other: ElementSummary[] } {
  const byName = (a: ElementSummary, b: ElementSummary) => (a.name < b.name ? -1 : a.name > b.name ? 1 : 0);
  const sorted = operations.filter((o) => o.kind === "operation").sort(byName);
  const on = sorted.filter((o) => !!o.subject && subjects.includes(o.subject));
  return { on, other: sorted.filter((o) => !on.includes(o)) };
}

/** An event's payload in small text: "dueDays (int32, required), issuedOn (date)". */
export function payloadText(event: Rec | undefined, nameOf: (id: string) => string | undefined = () => undefined): string {
  const list = Array.isArray(event?.payload) ? (event!.payload as Rec[]) : [];
  return list
    .map((a) => {
      const type = a.type;
      const typeName =
        typeof type === "string"
          ? type
          : type && typeof type === "object" && typeof (type as Rec).ref === "string"
            ? (nameOf((type as Rec).ref as string) ?? "reference")
            : "";
      const parts = [typeName, a.required === true ? "required" : ""].filter(Boolean);
      return parts.length ? `${String(a.name)} (${parts.join(", ")})` : String(a.name);
    })
    .join(", ");
}
