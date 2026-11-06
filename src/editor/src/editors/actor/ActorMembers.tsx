// Membership in the actor editor (actors-design.md section 9): a Member of field (roles, groups and organizations, never the
// actor itself), and for a role, group or organization its members, with "Add members…" writing the members' files in one batch.
import { useMemo, useState } from "react";
import { useIndex } from "@/api/queries";
import { Button } from "@/components/ui/button";
import { Dialog, DialogContent } from "@/components/ui/dialog";
import { SectionTitle } from "@/components/ui/misc";
import { ChipsEditor } from "@/inspector/fields";
import { directMembers, isMemberHolder, memberCount, memberOfChoices, wouldCycle, type ActorRow } from "@/model/actors";
import { indexLookup } from "@/model/index";
import { ACTOR_TYPE_LABELS, ACTOR_MEMBER_LABELS } from "@/model/labels";
import { useBatchEdit } from "@/explorer/marks";
import { ElementLink } from "../EntityEditor";
import type { EditorContext } from "../EditorFrame";

type Rec = Record<string, unknown>;

const typeText = (r: ActorRow) => (r.actorType ? ACTOR_TYPE_LABELS[r.actorType] : "");

function useActorRows() {
  const index = useIndex();
  return useMemo(() => (index.data ?? []).filter((r) => r.kind === "actor") as ActorRow[], [index.data]);
}

/** The Member of field: the roles, groups and organizations this actor belongs to, each shown with its type. */
export function MemberOfField({ ctx }: { ctx: Pick<EditorContext, "id" | "json" | "edit" | "flush"> }) {
  const rows = useActorRows();
  const rec = ctx.json as Rec;
  const values = Array.isArray(rec.memberOf) ? (rec.memberOf as string[]) : [];
  const options = memberOfChoices(rows, ctx.id).map((r) => ({ value: r.id, label: `${r.name} (${typeText(r)})` }));
  return (
    <ChipsEditor
      label={ACTOR_MEMBER_LABELS.memberOf}
      values={values}
      options={options}
      allowFree={false}
      empty={ACTOR_MEMBER_LABELS.noHolders}
      onChange={(next) => {
        ctx.edit((j) => {
          if (next.length) (j as Rec).memberOf = next;
          else delete (j as Rec).memberOf;
        });
        ctx.flush();
      }}
    />
  );
}

/** The members of a role, group or organization: direct members with links, a count at any depth, and Add members. */
export function MembersSection({ id, type }: { id: string; type: string }) {
  const rows = useActorRows();
  const [adding, setAdding] = useState(false);
  const lookup = indexLookup(useIndex().data);
  if (!isMemberHolder(type)) return null;
  const direct = directMembers(rows, id).sort((a, b) => a.name.localeCompare(b.name));
  const all = memberCount(rows, id);
  return (
    <section className="flex flex-col gap-1" aria-label={ACTOR_MEMBER_LABELS.members} data-testid="actor-members">
      <div className="flex items-center gap-2">
        <SectionTitle>{ACTOR_MEMBER_LABELS.members}</SectionTitle>
        <span className="text-11 text-secondary" data-testid="actor-members-count">
          {ACTOR_MEMBER_LABELS.count(direct.length, all)}
        </span>
        <Button type="button" className="ml-auto" onClick={() => setAdding(true)} data-testid="actor-add-members">
          {ACTOR_MEMBER_LABELS.add}
        </Button>
      </div>
      {direct.length ? (
        <ul className="flex flex-col" data-testid="actor-members-list">
          {direct.map((m) => {
            const summary = lookup.byId.get(m.id);
            return summary ? (
              <li key={m.id}>
                <ElementLink summary={summary} secondary={typeText(m)} />
              </li>
            ) : null;
          })}
        </ul>
      ) : (
        <p className="text-12 text-secondary">{ACTOR_MEMBER_LABELS.noMembers}</p>
      )}
      {adding ? <AddMembersDialog id={id} rows={rows} onClose={() => setAdding(false)} /> : null}
    </section>
  );
}

function AddMembersDialog({ id, rows, onClose }: { id: string; rows: ActorRow[]; onClose: () => void }) {
  const batchEdit = useBatchEdit();
  const [chosen, setChosen] = useState<string[]>([]);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const holder = rows.find((r) => r.id === id);
  // Not the holder, not a member already, and not one that holds the holder (that would close a cycle).
  const candidates = rows.filter((r) => !r.relative && !r.memberOf?.includes(id) && !wouldCycle(rows, id, r.id)).sort((a, b) => a.name.localeCompare(b.name));
  const save = async () => {
    if (!chosen.length || busy) return;
    setBusy(true);
    const ok = await batchEdit(ACTOR_MEMBER_LABELS.addUndo(holder?.name ?? ""), chosen, (json) => {
      const list = Array.isArray(json.memberOf) ? (json.memberOf as string[]) : [];
      if (!list.includes(id)) json.memberOf = [...list, id];
    });
    setBusy(false);
    if (ok) onClose();
    else setError(ACTOR_MEMBER_LABELS.failed);
  };
  return (
    <Dialog open onOpenChange={(open) => !open && onClose()}>
      <DialogContent title={ACTOR_MEMBER_LABELS.addTitle(holder?.name ?? "")}>
        <form
          className="flex flex-col gap-2"
          data-testid="add-members-dialog"
          onSubmit={(e) => {
            e.preventDefault();
            void save();
          }}
        >
          {candidates.length ? (
            <fieldset className="flex max-h-72 flex-col overflow-auto" aria-label={ACTOR_MEMBER_LABELS.members}>
              {candidates.map((r) => (
                <label key={r.id} className="flex h-6 items-center gap-2 px-1 text-12">
                  <input
                    type="checkbox"
                    checked={chosen.includes(r.id)}
                    onChange={(e) => setChosen((c) => (e.target.checked ? [...c, r.id] : c.filter((x) => x !== r.id)))}
                    data-testid={`add-member-${r.name}`}
                  />
                  <span className="min-w-0 flex-1 truncate">{r.name}</span>
                  <span className="text-secondary">{typeText(r)}</span>
                </label>
              ))}
            </fieldset>
          ) : (
            <p className="text-12 text-secondary">{ACTOR_MEMBER_LABELS.noCandidates}</p>
          )}
          {error ? <p className="text-12 text-danger">{error}</p> : null}
          <div className="flex justify-end gap-2">
            <Button type="button" onClick={onClose}>
              Cancel
            </Button>
            <Button type="submit" variant="primary" disabled={!chosen.length || busy} data-testid="add-members-apply">
              {ACTOR_MEMBER_LABELS.addApply(chosen.length)}
            </Button>
          </div>
        </form>
      </DialogContent>
    </Dialog>
  );
}
