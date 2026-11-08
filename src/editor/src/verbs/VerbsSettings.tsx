// The Verbs tab of Settings (verbs-design.md section 9) and the pieces the editors share: the built-in verbs by group (read-only,
// each with a Hide box), the project's own verbs as a grid, "Add a verb…" with its suggestions, and rename and remove through
// the batch operations (one undo step each). Also the verb picker of the event, transition and operation editors.
import { useState } from "react";
import { Pencil, Plus, Trash2 } from "lucide-react";
import { useIndex } from "@/api/queries";
import type { ElementSummary } from "@/api/types";
import { useServices } from "@/app/context";
import { useEditorNavigation } from "@/app/navigation";
import { Button } from "@/components/ui/button";
import { Dialog, DialogContent } from "@/components/ui/dialog";
import { Field, Input, Select } from "@/components/ui/input";
import { SectionTitle, Spinner } from "@/components/ui/misc";
import { displayName, KIND_LABELS } from "@/model/model";
import { builtInVerbs } from "@/model/verbs";
import {
  BUILT_IN_GROUP_LABELS,
  describeVerb,
  freeKey,
  KEY_PATTERN,
  labelOfKey,
  usageText,
  verbGroups,
  verbSuggestions,
  verbOperationFiles,
  isUsed,
  verbUsage,
  verbUsers,
  type OwnVerb,
  type VerbPlace,
} from "@/model/verbList";
import { builtInVerbGroups } from "@/model/verbs";
import { commitOperation } from "@/vocabularies/retag";
import { useAddVerb, useVerbs } from "./useVerbs";

const ADD = "__add-verb__";

/** The verb picker: None, then the groups for the place, hidden verbs left out, and "Add a verb…" at the foot. */
export function VerbPicker({
  place,
  value,
  onChange,
  id,
  label = "Verb",
  disabled,
}: {
  place: VerbPlace;
  value: string | undefined;
  onChange: (key: string | undefined) => void;
  id?: string;
  label?: string;
  disabled?: boolean;
}) {
  const { own, hidden } = useVerbs();
  const [adding, setAdding] = useState(false);
  const groups = verbGroups(place, own, hidden, value);
  const known = !value || groups.some((g) => g.options.some((o) => o.key === value));
  const meaning = value ? describeVerb(value, own) : null;
  return (
    <>
      <div className="flex flex-col gap-0.5">
        <Select
          id={id}
          aria-label={label}
          data-testid="verb-picker"
          value={value ?? ""}
          disabled={disabled}
          onChange={(e) => (e.target.value === ADD ? setAdding(true) : onChange(e.target.value || undefined))}
        >
          <option value="">None</option>
          {known ? null : <option value={value}>{value} (unknown)</option>}
          {groups.map((g) => (
            <optgroup key={g.id} label={g.label}>
              {g.options.map((o) => (
                <option key={o.key} value={o.key} title={o.meaning}>
                  {o.label}
                </option>
              ))}
            </optgroup>
          ))}
          <option value={ADD}>Add a verb…</option>
        </Select>
        {meaning?.meaning ? <span className="text-11 text-secondary">{meaning.meaning}</span> : null}
      </div>
      {adding ? <AddVerbDialog onClose={() => setAdding(false)} onAdded={(key) => onChange(key)} /> : null}
    </>
  );
}

/** Adds an own verb, offering the verbs left to projects as suggestions that only pre-fill it. */
export function AddVerbDialog({ onClose, onAdded }: { onClose: () => void; onAdded?: (key: string) => void }) {
  const { own } = useVerbs();
  const services = useServices();
  const add = useAddVerb();
  const [key, setKey] = useState("");
  const [displayName, setDisplayName] = useState("");
  const [description, setDescription] = useState("");
  const [busy, setBusy] = useState(false);
  const taken = new Set(own.map((v) => v.key));
  const problem = !key
    ? null
    : !KEY_PATTERN.test(key)
      ? "A verb key is lower-case words joined by hyphens."
      : builtInVerbs.some((v) => v.key === key)
        ? `${key} is a built-in verb: use it instead.`
        : taken.has(key)
          ? `You already have a verb ${key}.`
          : null;
  const suggestions = verbSuggestions(own);
  return (
    <Dialog open onOpenChange={(o) => !o && onClose()}>
      <DialogContent title="Add a verb" description="A verb your project names on events, transitions and operations.">
        <form
          className="flex flex-col gap-2"
          onSubmit={async (e) => {
            e.preventDefault();
            if (!key || problem) return;
            setBusy(true);
            await add({
              key,
              ...(displayName.trim() ? { displayName: displayName.trim() } : {}),
              ...(description.trim() ? { description: description.trim() } : {}),
            });
            setBusy(false);
            services.store.getState().notify(`Added the verb ${key}.`);
            onAdded?.(key);
            onClose();
          }}
        >
          {suggestions.length ? (
            <div className="flex flex-wrap items-center gap-1 text-12" data-testid="verb-suggestions">
              <span className="text-secondary">Suggestions</span>
              {suggestions.map((s) => (
                <Button
                  key={s.key}
                  type="button"
                  size="sm"
                  onClick={() => {
                    setKey(freeKey(s.key, taken));
                    setDisplayName(s.displayName);
                  }}
                >
                  {s.key}
                </Button>
              ))}
            </div>
          ) : null}
          <Field label="Key" htmlFor="verb-key">
            <Input id="verb-key" className="font-mono" value={key} autoFocus onChange={(e) => setKey(e.target.value)} />
          </Field>
          <Field label="Display name" htmlFor="verb-display">
            <Input id="verb-display" value={displayName} placeholder={key ? labelOfKey(key) : ""} onChange={(e) => setDisplayName(e.target.value)} />
          </Field>
          <Field label="Description" htmlFor="verb-description">
            <Input id="verb-description" value={description} onChange={(e) => setDescription(e.target.value)} />
          </Field>
          {problem ? <p className="text-12 text-danger">{problem}</p> : null}
          <div className="flex justify-end gap-2">
            <Button type="button" onClick={onClose}>
              Cancel
            </Button>
            <Button type="submit" variant="primary" disabled={busy || !key || !!problem} data-testid="verb-add-apply">
              Add
            </Button>
          </div>
        </form>
      </DialogContent>
    </Dialog>
  );
}

/** The elements as links that open them (the dialog closes first), "Order lifecycle, Ship operation". */
function ElementLinks({ elements, onOpen }: { elements: readonly ElementSummary[]; onOpen: () => void }) {
  const { openEditor } = useEditorNavigation();
  return (
    <>
      {elements.map((e, i) => (
        <span key={e.id}>
          {i ? ", " : ""}
          <button
            type="button"
            className="text-accent underline focus-visible:outline-2 focus-visible:outline-accent"
            title={`Open ${KIND_LABELS[e.kind].toLowerCase()} ${displayName(e)}`}
            data-testid="verb-user-link"
            onClick={() => {
              onOpen();
              openEditor(e, false);
            }}
          >
            {displayName(e)}
          </button>
        </span>
      ))}
    </>
  );
}

/** "Used by" as a link: the elements that name an own verb, each a link. */
function UsedByDialog({ verb, onClose }: { verb: { key: string }; onClose: () => void }) {
  const index = useIndex();
  const users = verbUsers(index.data ?? [], verb.key);
  return (
    <Dialog open onOpenChange={(o) => !o && onClose()}>
      <DialogContent title={`Used by ${verb.key}`} description="The named trigger points, processes and operations that name this verb.">
        <p className="text-13" data-testid="verb-used-by">
          {users.length ? <ElementLinks elements={users} onOpen={onClose} /> : "Nothing names it."}
        </p>
        <div className="flex justify-end">
          <Button onClick={onClose}>Close</Button>
        </div>
      </DialogContent>
    </Dialog>
  );
}

function ChangeVerbDialog({ verb, mode, vocabularyId, onClose }: { verb: OwnVerb; mode: "rename" | "remove"; vocabularyId: string; onClose: () => void }) {
  const services = useServices();
  const { own } = useVerbs();
  const index = useIndex();
  const [to, setTo] = useState(verb.key);
  const [busy, setBusy] = useState(false);
  const rename = mode === "rename";
  // The dry run: the files the operation writes. The uses of a built-in key are left alone, so nothing names it then.
  const files = verbOperationFiles(index.data ?? [], verb.key);
  const elements = files.users;
  const builtIn = builtInVerbs.some((v) => v.key === to);
  const problem = !rename
    ? null
    : !KEY_PATTERN.test(to)
      ? "A verb key is lower-case words joined by hyphens."
      : to === verb.key && !builtIn
        ? "Type the new key."
        : null;
  const merges = rename && to !== verb.key && (builtIn || own.some((v) => v.key === to));
  const run = async () => {
    setBusy(true);
    const label = rename ? `Rename verb ${verb.key} to ${to}` : `Remove verb ${verb.key}`;
    const failed = await commitOperation(services, label, rename ? { op: "rename-verb", verb: verb.key, to } : { op: "remove-verb", verb: verb.key }, [
      vocabularyId,
      ...elements.map((e) => e.id),
    ]);
    setBusy(false);
    if (failed) services.store.getState().notify(failed, "error");
    else {
      services.store.getState().notify(rename ? `Renamed ${verb.key} to ${to}.` : `Removed ${verb.key}.`);
      onClose();
    }
  };
  return (
    <Dialog open onOpenChange={(o) => !o && onClose()}>
      <DialogContent
        title={rename ? `Rename ${verb.key}` : `Remove ${verb.key}`}
        description={
          rename
            ? "The verb and every use of it change together. One undo step."
            : "The verb goes, and every event, transition and operation that names it loses its verb. One undo step."
        }
      >
        <div className="flex flex-col gap-2 text-13">
          {rename ? (
            <>
              <Input aria-label="New key" className="font-mono" value={to} autoFocus onChange={(e) => setTo(e.target.value)} />
              {to === verb.key && builtIn ? (
                <p className="text-12 text-secondary" role="status">
                  {to} is a built-in verb: its uses move to the built-in and your verb goes.
                </p>
              ) : merges ? (
                <p className="text-12 text-secondary" role="status">
                  {to} exists already: the uses of {verb.key} move to it and {verb.key} goes.
                </p>
              ) : null}
              {problem ? <p className="text-12 text-danger">{problem}</p> : null}
            </>
          ) : null}
          <p className="text-12 text-secondary" data-testid="verb-change-uses">
            {elements.length ? (
              <>
                Named by: <ElementLinks elements={elements} onOpen={onClose} />
              </>
            ) : (
              "Nothing names it."
            )}
          </p>
          <div className="flex justify-end gap-2">
            <Button onClick={onClose}>Cancel</Button>
            <Button
              variant={rename ? "primary" : "danger"}
              disabled={busy || index.isPending || !!problem}
              onClick={() => void run()}
              data-testid="verb-change-apply"
            >
              {rename ? "Rename" : "Remove"}
            </Button>
          </div>
        </div>
      </DialogContent>
    </Dialog>
  );
}

export function VerbsSettings() {
  const verbs = useVerbs();
  const [adding, setAdding] = useState(false);
  const [changing, setChanging] = useState<{ verb: OwnVerb; mode: "rename" | "remove" } | null>(null);
  const [usedBy, setUsedBy] = useState<string | null>(null);
  // A hide box answers at once: the choice shows while the file is saved (or created, for the first one), then the file's own state does.
  const [pendingHide, setPendingHide] = useState<Record<string, boolean>>({});
  if (verbs.loading) return <Spinner />;
  const usage = verbUsage(verbs.rows);
  const toggleHidden = async (key: string, hide: boolean) => {
    setPendingHide((p) => ({ ...p, [key]: hide }));
    const next = [...new Set(hide ? [...verbs.hidden, key] : [...verbs.hidden].filter((k) => k !== key))];
    try {
      if (verbs.id === null) await verbs.create({ hidden: next });
      else {
        verbs.edit((d) => (next.length ? void (d.hidden = next) : void delete d.hidden));
        await verbs.flush();
      }
    } finally {
      setPendingHide(({ [key]: _done, ...rest }) => rest);
    }
  };
  /** The "Used by" cell: the count, a link to the elements when there are some. */
  const usedByCell = (key: string) => {
    const used = usage.get(key);
    const text = usageText(used);
    return isUsed(used) ? (
      <button
        type="button"
        className="text-accent underline focus-visible:outline-2 focus-visible:outline-accent"
        title={`Show what uses ${key}`}
        data-testid={`verb-used-by-${key}`}
        onClick={() => setUsedBy(key)}
      >
        {text}
      </button>
    ) : (
      text
    );
  };
  return (
    <section className="flex max-w-5xl flex-col gap-3" aria-label="Verbs" data-testid="verbs-settings">
      <p className="text-12 text-secondary">
        A verb says what an event, a move or an operation does. Add verbs of your own; the built-in verbs have fixed meanings, and you can hide the ones your
        project does not use.
      </p>
      <SectionTitle
        actions={
          <Button size="sm" onClick={() => setAdding(true)} data-testid="verbs-add">
            <Plus /> Add a verb…
          </Button>
        }
      >
        Your verbs
      </SectionTitle>
      {!verbs.own.length ? (
        <p className="text-12 text-secondary">No verbs of your own: the file is created with the first one you add.</p>
      ) : (
        <table className="w-full text-13" aria-label="Your verbs">
          <thead>
            <tr className="text-left text-11 text-secondary">
              <th className="font-semibold">Key</th>
              <th className="font-semibold">Display name</th>
              <th className="font-semibold">Description</th>
              <th className="font-semibold">Used by</th>
              <th>
                <span className="sr-only">Actions</span>
              </th>
            </tr>
          </thead>
          <tbody>
            {verbs.own.map((v, i) => {
              const used = usage.get(v.key);
              const inUse = isUsed(used);
              const set = (field: "key" | "displayName" | "description", value: string) =>
                verbs.edit((d) => {
                  const one = d.verbs![i];
                  if (field === "key") one.key = value;
                  else if (value) one[field] = value;
                  else delete one[field];
                });
              return (
                <tr key={v.id} data-testid={`own-verb-${v.key}`}>
                  <td className="py-1 pr-2">
                    <Input
                      aria-label={`Key of verb ${i + 1}`}
                      className="font-mono"
                      value={v.key}
                      readOnly={inUse}
                      title={inUse ? "In use: rename it everywhere with the rename button" : undefined}
                      onChange={(e) => set("key", e.target.value)}
                      onBlur={() => void verbs.flush()}
                    />
                  </td>
                  <td className="py-1 pr-2">
                    <Input
                      aria-label={`Display name of ${v.key}`}
                      value={v.displayName ?? ""}
                      onChange={(e) => set("displayName", e.target.value)}
                      onBlur={() => void verbs.flush()}
                    />
                  </td>
                  <td className="py-1 pr-2">
                    <Input
                      aria-label={`Description of ${v.key}`}
                      value={v.description ?? ""}
                      onChange={(e) => set("description", e.target.value)}
                      onBlur={() => void verbs.flush()}
                    />
                  </td>
                  <td className="whitespace-nowrap py-1 pr-2 text-12 text-secondary">{usedByCell(v.key)}</td>
                  <td className="whitespace-nowrap">
                    <Button size="icon-sm" variant="ghost" label={`Rename ${v.key} everywhere`} onClick={() => setChanging({ verb: v, mode: "rename" })}>
                      <Pencil />
                    </Button>
                    <Button size="icon-sm" variant="ghost" label={`Remove verb ${v.key}`} onClick={() => setChanging({ verb: v, mode: "remove" })}>
                      <Trash2 />
                    </Button>
                  </td>
                </tr>
              );
            })}
          </tbody>
        </table>
      )}
      <SectionTitle>Built-in verbs</SectionTitle>
      <table className="w-full table-fixed text-13" aria-label="Built-in verbs">
        <colgroup>
          <col className="w-56" />
          <col />
          <col className="w-64" />
          <col className="w-20" />
        </colgroup>
        <thead>
          <tr className="text-left text-11 text-secondary">
            <th className="font-semibold">Verb</th>
            <th className="font-semibold">Meaning</th>
            <th className="font-semibold">Used by</th>
            <th className="font-semibold">Hide</th>
          </tr>
        </thead>
        {builtInVerbGroups.map((g) => (
          <tbody key={g} data-testid={`builtin-group-${g}`}>
            <tr>
              <th colSpan={4} className="pt-2 text-left text-11 font-semibold uppercase text-secondary">
                {BUILT_IN_GROUP_LABELS[g]}
              </th>
            </tr>
            {builtInVerbs
              .filter((v) => v.group === g)
              .map((v) => {
                const hidden = pendingHide[v.key] ?? verbs.hidden.has(v.key);
                return (
                  <tr key={v.key} data-testid={`builtin-${v.key}`}>
                    <td className="py-0.5 pr-2">
                      <span className="font-medium">{v.displayName}</span> <span className="font-mono text-11 text-secondary">{v.key}</span>
                    </td>
                    <td className="py-0.5 pr-2 text-12 text-secondary">{v.meaning}</td>
                    <td className="py-0.5 pr-2 text-12 text-secondary">{usedByCell(v.key)}</td>
                    <td className="py-0.5">
                      <label className="flex w-fit items-center gap-1 text-12 text-secondary" title={`Hide ${v.displayName} from the verb pickers`}>
                        <input type="checkbox" aria-label={`Hide ${v.key}`} checked={hidden} onChange={(e) => void toggleHidden(v.key, e.target.checked)} />
                        <span aria-hidden>{hidden ? "Hidden" : "Hide"}</span>
                      </label>
                    </td>
                  </tr>
                );
              })}
          </tbody>
        ))}
      </table>
      {adding ? <AddVerbDialog onClose={() => setAdding(false)} /> : null}
      {usedBy ? <UsedByDialog verb={{ key: usedBy }} onClose={() => setUsedBy(null)} /> : null}
      {changing && verbs.id ? <ChangeVerbDialog {...changing} vocabularyId={verbs.id} onClose={() => setChanging(null)} /> : null}
    </section>
  );
}
