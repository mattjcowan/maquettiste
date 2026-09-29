// The translation queue (reference-types-seeds-localization.md 3.10): one locale and shard as a two-column list
// (source, translation). Enter saves and goes to the next entry that needs work, Ctrl+Enter confirms a stale one and
// goes on, the arrow keys move between rows, and "Open element" goes to the owner.
import { useEffect, useMemo, useRef, useState, type KeyboardEvent } from "react";
import { useQueryClient } from "@tanstack/react-query";
import type { TranslationEntry } from "@/api/types";
import { useIndex } from "@/api/queries";
import { useServices } from "@/app/context";
import { useEditorNavigation } from "@/app/navigation";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Badge, Kbd, Spinner } from "@/components/ui/misc";
import { displayName } from "@/model/model";
import { entryKey, localeName, nextOpen, shardLabel, shardStem } from "./model";
import { useShardTranslations, writeTranslations } from "./queries";

const FIELD = { displayName: "display name", pluralName: "plural name", label: "label", description: "description" } as const;

export function TranslationQueue({ locale, shard, onClose }: { locale: string; shard: string; onClose(): void }) {
  const page = useShardTranslations(locale, shard);
  const index = useIndex();
  const qc = useQueryClient();
  const { store } = useServices();
  const { goTo } = useEditorNavigation();
  const entries = useMemo(() => page.data?.entries ?? [], [page.data]);
  const [values, setValues] = useState<Record<string, string>>({});
  const inputs = useRef(new Map<number, HTMLInputElement>());
  const names = useMemo(() => new Map((index.data ?? []).map((r) => [r.id, r])), [index.data]);
  const open = entries.filter((e) => e.state !== "translated").length;

  // First focus: the first entry that needs work.
  const started = useRef(false);
  useEffect(() => {
    if (started.current || entries.length === 0) return;
    started.current = true;
    const first = nextOpen(entries, -1);
    inputs.current.get(first >= 0 ? first : 0)?.focus();
  }, [entries]);

  const valueOf = (e: TranslationEntry) => values[entryKey(e)] ?? e.translation ?? "";
  const focusAt = (i: number) => {
    const el = inputs.current.get(i);
    el?.focus();
    el?.scrollIntoView({ block: "nearest" });
  };

  const save = async (i: number, confirm: boolean) => {
    const e = entries[i];
    const value = valueOf(e);
    const changed = value !== (e.translation ?? "");
    if (changed || confirm) {
      const result = await writeTranslations(qc, locale, [{ id: e.id, field: e.field, value: value || null, confirm: confirm && !changed }], entries);
      if (!result.ok) {
        store.getState().notify(result.message, "error");
        return;
      }
    }
    const next = nextOpen(entries, i);
    if (next >= 0) focusAt(next);
    else store.getState().notify(`Nothing left to translate in ${shardLabel(shardStem(shard, locale))} for ${locale}.`);
  };

  const onKey = (i: number) => (ev: KeyboardEvent<HTMLInputElement>) => {
    if (ev.key === "Enter") {
      ev.preventDefault();
      void save(i, ev.ctrlKey || ev.metaKey);
    } else if (ev.key === "ArrowDown" && i + 1 < entries.length) {
      ev.preventDefault();
      focusAt(i + 1);
    } else if (ev.key === "ArrowUp" && i > 0) {
      ev.preventDefault();
      focusAt(i - 1);
    } else if (ev.key === "Escape") {
      ev.preventDefault();
      setValues((v) => {
        const next = { ...v };
        delete next[entryKey(entries[i])];
        return next;
      });
    }
  };

  const title = `${localeName(locale)}, ${shardLabel(shardStem(shard, locale))}`;
  return (
    <section className="flex min-h-0 flex-col gap-2" aria-label={`Translation queue: ${title}`} data-testid="translation-queue">
      <header className="flex items-center gap-2">
        <h3 className="text-14 font-semibold">Translation queue: {title}</h3>
        <span className="text-12 text-secondary" data-testid="queue-open">
          {open} to do
        </span>
        <span className="flex-1" />
        <span className="text-11 text-secondary">
          <Kbd>Enter</Kbd> save and next · <Kbd>Ctrl Enter</Kbd> confirm · <Kbd>↑</Kbd> <Kbd>↓</Kbd> move
        </span>
        <Button size="sm" variant="ghost" onClick={onClose}>
          Close
        </Button>
      </header>
      {page.isPending ? <Spinner label="Loading translations" /> : null}
      <ol className="flex max-h-[28rem] flex-col overflow-auto rounded-control border border-default" aria-label="Entries">
        {entries.map((e, i) => {
          const owner = names.get(e.owner ?? e.id);
          const label = `${FIELD[e.field]} of ${owner ? displayName(owner) : e.id} (${locale})`;
          return (
            <li
              key={entryKey(e)}
              className="grid grid-cols-[1fr_1fr_auto] items-center gap-2 border-b border-default px-2 py-1 last:border-b-0"
              data-state={e.state}
              data-testid="queue-entry"
            >
              <div className="min-w-0">
                <div className="truncate text-13">{e.source ?? <span className="italic text-secondary">(no source text)</span>}</div>
                <div className="truncate text-11 text-secondary">
                  {owner ? displayName(owner) : e.id} · {FIELD[e.field]}
                  {e.state === "stale" ? (
                    <Badge tone="warning" className="ml-1">
                      stale
                    </Badge>
                  ) : e.state === "translated" ? (
                    <Badge tone="success" className="ml-1">
                      done
                    </Badge>
                  ) : null}
                </div>
              </div>
              <Input
                ref={(el) => {
                  if (el) inputs.current.set(i, el);
                  else inputs.current.delete(i);
                }}
                aria-label={label}
                value={valueOf(e)}
                placeholder={e.state === "fallback" ? (e.effective ?? "") : ""}
                className="placeholder:italic placeholder:text-secondary"
                onChange={(ev) => setValues((v) => ({ ...v, [entryKey(e)]: ev.target.value }))}
                onKeyDown={onKey(i)}
              />
              <Button size="sm" variant="ghost" onClick={() => goTo(e.owner ?? e.id)} aria-label={`Open element ${owner ? displayName(owner) : e.id}`}>
                Open element
              </Button>
            </li>
          );
        })}
      </ol>
    </section>
  );
}
