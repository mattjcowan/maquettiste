// The Sets editor of an action (object-effects-design.md section 10): one row per set, with an Attribute picker (the
// attributes every subject of the process holds, without the ones the engine refuses) and a Value box with its preview.
// Apply writes the action's sets as one change; a row whose text did not change keeps its stored tree.
import { useMemo, useState } from "react";
import { Plus, X } from "lucide-react";
import { useElements, useIndex } from "@/api/queries";
import { setPreviewRequest, type ExpressionPreviewRequest } from "@/api/expressionPreview";
import { Button } from "@/components/ui/button";
import { Dialog, DialogContent } from "@/components/ui/dialog";
import { Select } from "@/components/ui/input";
import { newId } from "@/lib/ids";
import { bindableAttributes } from "@/model/binding";
import { EFFECT_LABELS as L } from "@/model/effectLabels";
import { settableAttributes, treeText, type SettableAttribute, type TreeNames } from "@/model/effects";
import { stateIndex, subjectsOf, type Json, type ProcessDoc, type SetDoc } from "@/model/process";
import { indexLookup } from "@/model/index";
import { ValueBox } from "../ValueBox";

type Rec = Record<string, unknown>;

export interface SetContext {
  choices: SettableAttribute[];
  /** Every attribute the subjects hold, calculated and bound ones too (a set may name only the settable `choices`). */
  attributes: { id: string; name: string }[];
  /** Attribute name by id, over everything the subjects hold. */
  names: TreeNames;
  attributeName: (id: string) => string;
  noSubjects: boolean;
}

/** What the process's subjects hold: the attributes a set may name, and the names for printing a value tree. */
export function useSetContext(process: ProcessDoc): SetContext {
  const index = useIndex();
  const lookup = indexLookup(index.data);
  const entries = subjectsOf(process);
  const chains = entries.map((e) => {
    const chain: string[] = [];
    for (let id = e.entity; id && !chain.includes(id); id = lookup.byId.get(id)?.base) chain.push(id);
    return chain;
  });
  const stereotypeIds = lookup.ofKind("stereotype").map((s) => s.id);
  const held = useElements([...new Set(chains.flat()), ...stereotypeIds]).byId;
  return useMemo(() => {
    const json = (id: string) => held.get(id)?.json as Rec | undefined;
    const stereotype = (key: string) => stereotypeIds.map(json).find((s) => s?.key === key);
    const perSubject = entries.map((e) =>
      e.stereotype !== undefined
        ? (((stereotype(e.stereotype)?.attributes as Rec[] | undefined) ?? []) as Rec[])
        : bindableAttributes(json(e.entity ?? ""), json, stereotype),
    );
    const bound = new Set(entries.flatMap((e) => (e.boundAttribute ? [e.boundAttribute] : process.boundAttribute ? [process.boundAttribute] : [])));
    const kindOf = (id: string) => lookup.byId.get(id)?.kind;
    const choices = settableAttributes(perSubject as never, bound, kindOf);
    const all = new Map<string, string>();
    for (const list of perSubject) for (const a of list) all.set(String(a.id), String(a.displayName || a.name));
    const own = new Map<string, string>();
    for (const a of process.context ?? []) own.set(a.id, a.name);
    for (const e of process.events ?? []) for (const a of e.payload ?? []) own.set(a.id, a.name);
    const states = new Map([...stateIndex(process)].map(([id, info]) => [id, `${process.name}.${info.path}`]));
    const names: TreeNames = {
      name: (id) => all.get(id) ?? own.get(id),
      state: (id) => states.get(id),
      subject: (id) => all.has(id),
    };
    return {
      choices,
      attributes: [...all].map(([id, name]) => ({ id, name })),
      names,
      attributeName: (id) => all.get(id) ?? "?",
      noSubjects: entries.length === 0,
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [held, process, index.data]);
}

interface Draft {
  id: string;
  attribute: string;
  text: string;
  /** The text the row started with; unchanged text keeps the stored tree. */
  initial: string;
  stored: Json | null;
}

/** What a holder other than an action (a trigger point) says: its name and sets, and how its set values are previewed. */
export interface SetsHolder {
  name: string;
  sets: SetDoc[];
  /** The preview request of a set: stored (no text), or the text in the box. */
  request: (set: { id: string; attribute: string; text?: string }) => ExpressionPreviewRequest;
}

export function SetsDialog({
  process,
  action,
  holder,
  context,
  onApply,
  onClose,
}: {
  process: ProcessDoc;
  action: string;
  /** Sets of something that is not an action of the process; `action` is then unused. */
  holder?: SetsHolder;
  context: SetContext;
  onApply: (sets: SetDoc[]) => void;
  onClose: () => void;
}) {
  const doc = holder ? { name: holder.name, sets: holder.sets } : process.actions?.find((a) => a.id === action);
  const preview = (set: { id: string; attribute: string; text?: string }) =>
    holder ? holder.request(set) : setPreviewRequest({ action, set: set.id, attribute: set.attribute, text: set.text });
  const [rows, setRows] = useState<Draft[]>(() =>
    (doc?.sets ?? []).map((s) => {
      const text = treeText(s.value, context.names);
      return { id: s.id, attribute: s.attribute, text, initial: text, stored: s.value };
    }),
  );
  const storedIds = new Set((doc?.sets ?? []).map((s) => s.id));
  const patch = (id: string, next: Partial<Draft>) => setRows((d) => d.map((r) => (r.id === id ? { ...r, ...next } : r)));
  const taken = (except: string) => new Set(rows.filter((r) => r.id !== except).map((r) => r.attribute));
  const free = context.choices.find((c) => !rows.some((r) => r.attribute === c.id));
  const apply = () =>
    onApply(
      rows
        .filter((r) => r.attribute)
        .map((r) => ({
          id: r.id,
          attribute: r.attribute,
          value: r.text === r.initial && r.stored ? r.stored : r.text.trim() ? { text: r.text.trim() } : { null: true },
        })),
    );
  return (
    <Dialog open onOpenChange={(open) => !open && onClose()}>
      <DialogContent title={L.title(doc?.name ?? "")} wide>
        <form
          className="flex flex-col gap-2"
          data-testid="sets-dialog"
          onSubmit={(e) => {
            e.preventDefault();
            apply();
          }}
        >
          <p className="text-12 text-secondary">{L.setsHint}</p>
          {context.noSubjects ? (
            <p className="text-12 text-warning">{L.noSubjects}</p>
          ) : !context.choices.length ? (
            <p className="text-12 text-warning">{L.noAttributes}</p>
          ) : null}
          <div className="flex items-center gap-1 text-11 text-secondary">
            <span className="w-44 shrink-0">{L.attribute}</span>
            <span className="flex-1">{L.value}</span>
          </div>
          {rows.map((r) => {
            const others = taken(r.id);
            const known = context.choices.some((c) => c.id === r.attribute);
            return (
              <div key={r.id} className="flex items-start gap-1" data-testid="set-row" data-attribute={context.attributeName(r.attribute)}>
                <Select
                  aria-label={L.attribute}
                  className="h-6 w-44 shrink-0"
                  value={r.attribute}
                  onChange={(e) => patch(r.id, { attribute: e.target.value })}
                  data-testid="set-attribute"
                >
                  {r.attribute && !known ? (
                    <option value={r.attribute}>{context.attributeName(r.attribute)}</option>
                  ) : (
                    <option value="">{L.pickAttribute}</option>
                  )}
                  {context.choices
                    .filter((c) => !others.has(c.id))
                    .map((c) => (
                      <option key={c.id} value={c.id}>
                        {c.name}
                      </option>
                    ))}
                </Select>
                <ValueBox
                  id={`set-value-${r.id}`}
                  label={L.value}
                  initial={r.initial}
                  onChange={(text) => patch(r.id, { text })}
                  request={(text) =>
                    text === undefined
                      ? storedIds.has(r.id)
                        ? preview({ id: r.id, attribute: r.attribute })
                        : null
                      : text.trim() && r.attribute
                        ? preview({ id: r.id, attribute: r.attribute, text })
                        : null
                  }
                />
                <Button
                  type="button"
                  variant="ghost"
                  size="icon-row"
                  label={L.removeSet}
                  onClick={() => setRows((d) => d.filter((x) => x.id !== r.id))}
                  data-testid="remove-set"
                >
                  <X />
                </Button>
              </div>
            );
          })}
          <div className="flex items-center justify-between gap-2">
            <Button
              type="button"
              disabled={!free}
              onClick={() => free && setRows((d) => [...d, { id: newId(), attribute: free.id, text: "", initial: "", stored: null }])}
              data-testid="add-set"
            >
              <Plus /> {L.addSet}
            </Button>
            <div className="flex gap-2">
              <Button type="button" onClick={onClose}>
                Cancel
              </Button>
              <Button type="submit" variant="primary" data-testid="sets-apply">
                Apply
              </Button>
            </div>
          </div>
        </form>
      </DialogContent>
    </Dialog>
  );
}
