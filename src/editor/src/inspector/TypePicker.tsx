// The attribute grid's Type cell editor (reference-types-seeds-localization.md 4.5): a searchable drop-down list
// with sections (Recent, Built-in, Custom types, Enums, Reference data, Value objects) and one ranked search across
// them, with the Many (collection) and Required toggles beside it. A reference type shows its effective storage beside
// its name ("Country · check"), read from the type and the project settings while a query client is at hand. Focus stays in the search box: arrows move the
// highlight, Enter picks and moves down, Tab picks and moves right, Escape cancels, Alt+M and Alt+R flip the toggles.
import { useContext, useId, useLayoutEffect, useMemo, useState, type KeyboardEvent } from "react";
import { createPortal } from "react-dom";
import { QueryClientContext } from "@tanstack/react-query";
import { useElements, useSettings } from "@/api/queries";
import type { ElementSummary, ReferenceTypeDoc, StorageChoice } from "@/api/types";
import { cn } from "@/lib/cn";
import { usePopoverPlacement } from "@/ui/usePopoverPlacement";
import { storageLabel } from "@/workspaces/reference-data/storageChoices";
import { flatItems, pickerGroups, rememberPick } from "./typePicker";

export interface TypePick {
  value: string;
  collection: boolean;
  required: boolean;
}

export interface TypePickerProps {
  /** The current type value: a built-in name or `ref:<id>`. */
  value: string;
  collection: boolean;
  required: boolean;
  options: readonly ElementSummary[];
  label: string;
  onPick(pick: TypePick, move: "down" | "right" | "left" | "none"): void;
  /** Closes without a change; `refocus` when the cell should take focus back (Escape, not a blur). */
  onCancel(refocus: boolean): void;
}

const NO_STORAGE: ReadonlyMap<string, string> = new Map();

export function TypePicker(props: TypePickerProps) {
  const client = useContext(QueryClientContext);
  return client ? <StoredTypePicker {...props} /> : <PickerList {...props} storage={NO_STORAGE} />;
}

/** The effective storage of the reference types among the options, by id (the documents are read in one batch). */
function StoredTypePicker(props: TypePickerProps) {
  const ids = useMemo(() => props.options.filter((o) => o.kind === "reference-type").map((o) => o.id), [props.options]);
  const docs = useElements(ids);
  const settings = useSettings();
  const storage = useMemo(() => {
    const project = (settings.data?.json as { conventions?: { referenceStorage?: StorageChoice } } | undefined)?.conventions?.referenceStorage;
    const out = new Map<string, string>();
    for (const id of ids) {
      const doc = docs.byId.get(id)?.json as unknown as ReferenceTypeDoc | undefined;
      if (doc) out.set(id, storageLabel(doc.storage as Record<string, StorageChoice> | undefined, project));
    }
    return out;
  }, [ids, docs.byId, settings.data]);
  return <PickerList {...props} storage={storage} />;
}

function PickerList({ value, collection, required, options, label, onPick, onCancel, storage }: TypePickerProps & { storage: ReadonlyMap<string, string> }) {
  const listId = useId();
  const [query, setQuery] = useState("");
  const [many, setMany] = useState(collection);
  const [req, setReq] = useState(required);
  const groups = useMemo(() => pickerGroups(options, query, undefined, storage), [options, query, storage]);
  const items = useMemo(() => flatItems(groups), [groups]);
  const [active, setActive] = useState(() =>
    Math.max(
      0,
      items.findIndex((i) => i.value === value),
    ),
  );
  // Below the cell when the list fits there, above it otherwise, clamped to the viewport (a low row keeps every section
  // reachable by scrolling). The panel renders at once, hidden until measured.
  const { anchorRef: inputRef, panelRef, style, placement } = usePopoverPlacement<HTMLInputElement>({ open: true, maxHeight: 320, minWidth: 260 });
  const placed = placement !== null;
  const current = items[Math.min(active, items.length - 1)];

  // The highlighted item stays in view when the highlight moves and once the panel is placed.
  useLayoutEffect(() => {
    if (placed) document.getElementById(`${listId}-${active}`)?.scrollIntoView?.({ block: "nearest" });
  }, [active, listId, placed]);

  const pick = (index: number, move: "down" | "right" | "left" | "none") => {
    const item = items[index];
    if (!item) return;
    rememberPick(item.value);
    onPick({ value: item.value, collection: many, required: req }, move);
  };

  const onKeyDown = (e: KeyboardEvent<HTMLInputElement>) => {
    if (e.altKey && (e.key === "m" || e.key === "M")) {
      e.preventDefault();
      setMany((v) => !v);
    } else if (e.altKey && (e.key === "r" || e.key === "R")) {
      e.preventDefault();
      setReq((v) => !v);
    } else if (e.key === "ArrowDown" || e.key === "ArrowUp") {
      e.preventDefault();
      const n = items.length;
      if (n) setActive((a) => (e.key === "ArrowDown" ? (a + 1) % n : (a - 1 + n) % n));
    } else if (e.key === "Enter") {
      e.preventDefault();
      pick(active, "down");
    } else if (e.key === "Tab") {
      e.preventDefault();
      pick(active, e.shiftKey ? "left" : "right");
    } else if (e.key === "Escape") {
      e.preventDefault();
      onCancel(true);
    }
    e.stopPropagation();
  };

  let index = -1;
  return (
    <>
      <input
        ref={inputRef}
        role="combobox"
        aria-label={label}
        aria-expanded
        aria-controls={listId}
        aria-activedescendant={current ? `${listId}-${active}` : undefined}
        aria-autocomplete="list"
        placeholder="Search types…"
        className="h-6 w-full rounded-[4px] border border-input bg-surface px-1 font-mono text-12"
        value={query}
        onChange={(e) => {
          setQuery(e.target.value);
          setActive(0);
        }}
        onKeyDown={onKeyDown}
        onBlur={() => onCancel(false)}
        data-testid="type-picker-input"
      />
      {createPortal(
        <div
          ref={panelRef}
          className="z-50 flex flex-col rounded-control border border-default bg-surface text-12 shadow-lg"
          style={style}
          onMouseDown={(e) => e.preventDefault()}
          data-testid="type-picker"
          data-side={placement?.side}
        >
          <div id={listId} role="listbox" aria-label="Types" className="mq-scroll min-h-0 flex-1 overflow-auto py-1" data-popover-scroll>
            {groups.map((g) => (
              <div key={g.section} role="group" aria-labelledby={`${listId}-${g.section}`}>
                <div id={`${listId}-${g.section}`} className="px-2 pt-1 text-11 font-semibold uppercase tracking-wide text-secondary">
                  {g.label}
                </div>
                {g.items.map((item) => {
                  index++;
                  const at = index;
                  return (
                    <div
                      key={`${g.section}-${item.value}`}
                      id={`${listId}-${at}`}
                      role="option"
                      aria-selected={at === active}
                      className={cn("flex cursor-default items-center gap-2 px-2 py-0.5", at === active && "bg-accent-subtle text-accent")}
                      onMouseEnter={() => setActive(at)}
                      onClick={() => pick(at, "none")}
                    >
                      <span className="truncate font-mono">{item.label}</span>
                      {item.detail ? <span className="truncate text-11 text-secondary">{item.detail}</span> : null}
                      {item.storage ? <span className="ml-auto shrink-0 text-11 text-secondary">· {item.storage}</span> : null}
                    </div>
                  );
                })}
              </div>
            ))}
            {items.length === 0 ? <p className="px-2 py-1 text-secondary">No type matches.</p> : null}
          </div>
          <div className="flex items-center gap-2 border-t border-default px-2 py-1">
            <Toggle label="Many" hint="Alt+M" pressed={many} onToggle={() => setMany((v) => !v)} />
            <Toggle label="Required" hint="Alt+R" pressed={req} onToggle={() => setReq((v) => !v)} />
          </div>
        </div>,
        document.body,
      )}
    </>
  );
}

function Toggle({ label, hint, pressed, onToggle }: { label: string; hint: string; pressed: boolean; onToggle(): void }) {
  return (
    <button
      type="button"
      tabIndex={-1}
      aria-pressed={pressed}
      title={hint}
      onClick={onToggle}
      className={cn("rounded-[4px] border px-1.5 py-0.5 text-11", pressed ? "border-accent bg-accent-subtle text-accent" : "border-input text-secondary")}
    >
      {label}
    </button>
  );
}
