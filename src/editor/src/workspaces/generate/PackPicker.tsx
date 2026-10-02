// The Generate screen's pack picker (the owner: "what happens when we have 100 of them?"): one toolbar button that
// counts the chosen packs and opens a dense checkbox list, with a filter box past FILTER_AFTER packs and All / None.
// A disabled pack is listed greyed and cannot be ticked; the list keeps the packs' manifest order.
import { useMemo, useRef, useState, type KeyboardEvent } from "react";
import { ChevronDown, TriangleAlert } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Checkbox } from "@/components/ui/checkbox";
import { Input } from "@/components/ui/input";
import { Popover, PopoverContent, PopoverTrigger } from "@/components/ui/popover";
import { cn } from "@/lib/cn";

/** The filter box appears when the list holds more packs than this. */
export const FILTER_AFTER = 8;

export interface PickerPack {
  name: string;
  enabled: boolean;
  /** The pack's diagnostics, shown with the explorer's warning marker; 0 or absent shows none. */
  warnings?: number;
}

export const packCountLabel = (selected: number, total: number) => `Packs · ${selected} of ${total}`;

export function PackPicker({ packs, selected, onChange }: { packs: readonly PickerPack[]; selected: readonly string[]; onChange: (next: string[]) => void }) {
  const [open, setOpen] = useState(false);
  const [text, setText] = useState("");
  const list = useRef<HTMLDivElement>(null);
  const query = text.trim().toLowerCase();
  const shown = useMemo(() => (query ? packs.filter((p) => p.name.toLowerCase().includes(query)) : packs), [packs, query]);
  const isOn = (p: PickerPack) => p.enabled && selected.includes(p.name);
  const count = packs.filter(isOn).length;
  // Selections stay in manifest order; All and None act on the packs the filter shows.
  const commit = (on: (p: PickerPack) => boolean) => onChange(packs.filter((p) => p.enabled && on(p)).map((p) => p.name));
  const visible = new Set(shown.map((p) => p.name));

  // Arrow keys move between the rows (and from the filter or the buttons into the first row); Tab works as well.
  const onListKey = (e: KeyboardEvent<HTMLDivElement>) => {
    if (e.key !== "ArrowDown" && e.key !== "ArrowUp") return;
    const rows = Array.from(list.current?.querySelectorAll<HTMLElement>('[role="checkbox"]:not([disabled])') ?? []);
    if (!rows.length) return;
    e.preventDefault();
    const at = rows.indexOf(document.activeElement as HTMLElement);
    const to = at < 0 ? (e.key === "ArrowDown" ? 0 : rows.length - 1) : (at + (e.key === "ArrowDown" ? 1 : -1) + rows.length) % rows.length;
    rows[to]?.focus();
  };

  return (
    <Popover
      open={open}
      onOpenChange={(o) => {
        setOpen(o);
        if (o) setText("");
      }}
    >
      <PopoverTrigger asChild>
        <Button
          size="sm"
          className="ml-4"
          title="Choose the packs to plan"
          data-testid="pack-picker"
          onKeyDown={(e) => {
            if (e.key === "ArrowDown") {
              e.preventDefault();
              setText("");
              setOpen(true);
            }
          }}
        >
          {packCountLabel(count, packs.length)}
          <ChevronDown aria-hidden className="text-secondary" />
        </Button>
      </PopoverTrigger>
      <PopoverContent className="flex w-64 flex-col gap-1 p-1" align="start" aria-label="Packs to plan" data-testid="pack-picker-list" onKeyDown={onListKey}>
        {packs.length > FILTER_AFTER ? (
          <Input aria-label="Filter packs" placeholder="Filter packs" className="h-6 text-12" value={text} onChange={(e) => setText(e.target.value)} />
        ) : null}
        <div className="flex items-center gap-1">
          <Button size="sm" variant="ghost" title="Tick every enabled pack listed" onClick={() => commit((p) => isOn(p) || visible.has(p.name))}>
            All
          </Button>
          <Button size="sm" variant="ghost" title="Untick every pack listed" onClick={() => commit((p) => isOn(p) && !visible.has(p.name))}>
            None
          </Button>
        </div>
        <div ref={list} role="group" aria-label="Packs" className="mq-scroll max-h-72 overflow-y-auto">
          {shown.map((p) => (
            <label
              key={p.name}
              className={cn("flex h-6 items-center gap-1.5 rounded-[4px] px-1 text-12 hover:bg-accent-subtle", !p.enabled && "text-secondary")}
            >
              <Checkbox
                checked={isOn(p)}
                disabled={!p.enabled}
                aria-label={`Pack ${p.name}`}
                onCheckedChange={(v) => commit((x) => (x.name === p.name ? v === true : isOn(x)))}
              />
              <span className="min-w-0 flex-1 truncate" title={p.name}>
                {p.name}
                {p.enabled ? null : " (disabled)"}
              </span>
              {p.warnings ? (
                <span className="flex shrink-0 items-center gap-0.5 text-11 text-warning" aria-label={`${p.warnings} diagnostics`}>
                  <TriangleAlert className="size-3" aria-hidden />
                  {p.warnings}
                </span>
              ) : null}
            </label>
          ))}
          {!shown.length ? <p className="px-1 text-12 text-secondary">No pack matches.</p> : null}
        </div>
      </PopoverContent>
    </Popover>
  );
}
