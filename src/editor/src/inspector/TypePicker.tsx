// The attribute grid's Type cell editor (reference-types-seeds-localization.md 4.5): a searchable drop-down list
// with sections (Recent, Built-in, Custom types, Enums, Reference data, Value objects) and one ranked search across
// them, with the Many (collection) and Required toggles beside it. Focus stays in the search box: arrows move the
// highlight, Enter picks and moves down, Tab picks and moves right, Escape cancels, Alt+M and Alt+R flip the toggles.
import { useId, useLayoutEffect, useMemo, useRef, useState, type KeyboardEvent } from "react";
import { createPortal } from "react-dom";
import type { ElementSummary } from "@/api/types";
import { cn } from "@/lib/cn";
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

export function TypePicker({ value, collection, required, options, label, onPick, onCancel }: TypePickerProps) {
  const listId = useId();
  const inputRef = useRef<HTMLInputElement>(null);
  const [query, setQuery] = useState("");
  const [many, setMany] = useState(collection);
  const [req, setReq] = useState(required);
  const groups = useMemo(() => pickerGroups(options, query), [options, query]);
  const items = useMemo(() => flatItems(groups), [groups]);
  const [active, setActive] = useState(() =>
    Math.max(
      0,
      items.findIndex((i) => i.value === value),
    ),
  );
  const [box, setBox] = useState<{ left: number; top: number; width: number } | null>(null);
  const current = items[Math.min(active, items.length - 1)];

  useLayoutEffect(() => {
    const rect = inputRef.current?.getBoundingClientRect();
    if (rect) setBox({ left: rect.left, top: rect.bottom + 2, width: Math.max(rect.width, 260) });
  }, []);
  useLayoutEffect(() => {
    document.getElementById(`${listId}-${active}`)?.scrollIntoView({ block: "nearest" });
  }, [active, listId]);

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
      {box
        ? createPortal(
            <div
              className="fixed z-50 flex max-h-80 flex-col rounded-control border border-default bg-surface text-12 shadow-lg"
              style={{ left: box.left, top: box.top, width: box.width }}
              onMouseDown={(e) => e.preventDefault()}
              data-testid="type-picker"
            >
              <div id={listId} role="listbox" aria-label="Types" className="min-h-0 flex-1 overflow-auto py-1">
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
                        </div>
                      );
                    })}
                  </div>
                ))}
                {items.length === 0 ? <p className="px-2 py-1 text-secondary">No type matches.</p> : null}
              </div>
              <div className="flex items-center gap-3 border-t border-default px-2 py-1">
                <Toggle label="Many" hint="Alt+M" pressed={many} onToggle={() => setMany((v) => !v)} />
                <Toggle label="Required" hint="Alt+R" pressed={req} onToggle={() => setReq((v) => !v)} />
              </div>
            </div>,
            document.body,
          )
        : null}
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
