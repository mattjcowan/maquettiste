// The in-house unified-diff viewer (PD16): inline or split, virtualized, syntax colors by file
// extension.
import { useMemo, useRef, useState } from "react";
import { useVirtualizer } from "@tanstack/react-virtual";
import { cn } from "@/lib/cn";
import { Button } from "@/components/ui/button";
import { EmptyState } from "@/components/ui/misc";
import { inlineRows, parseUnifiedDiff, splitRows, type DiffLine } from "./parse";
import { languageOf, tokenize } from "./highlight";

const LINE_BG = {
  add: "bg-[color-mix(in_srgb,var(--mq-status-success)_8%,var(--mq-bg-surface))]",
  del: "bg-[color-mix(in_srgb,var(--mq-status-danger)_8%,var(--mq-bg-surface))]",
  context: "",
};
const TOKEN_CLASS = { keyword: "text-cat-4 font-medium", string: "text-cat-8", comment: "text-secondary italic", plain: "" };

function Code({ text, language }: { text: string; language: ReturnType<typeof languageOf> }) {
  return (
    <>
      {tokenize(text, language).map((t, i) => (
        <span key={i} className={TOKEN_CLASS[t.kind]}>
          {t.text}
        </span>
      ))}
    </>
  );
}

function Gutter({ n }: { n: number | null }) {
  return <span className="inline-block w-10 shrink-0 select-none pr-2 text-right text-secondary">{n ?? ""}</span>;
}

function Mark({ type }: { type: DiffLine["type"] | null }) {
  return (
    <span aria-hidden className={cn("inline-block w-4 shrink-0 select-none text-center", type === "add" && "text-success", type === "del" && "text-danger")}>
      {type === "add" ? "+" : type === "del" ? "−" : " "}
    </span>
  );
}

export function DiffViewer({ path, text }: { path: string; text: string }) {
  const [mode, setMode] = useState<"inline" | "split">("inline");
  const diff = useMemo(() => parseUnifiedDiff(text), [text]);
  const language = languageOf(path);
  const rows = useMemo(() => (mode === "inline" ? inlineRows(diff) : splitRows(diff)), [diff, mode]);
  const scrollRef = useRef<HTMLDivElement>(null);
  const virtualizer = useVirtualizer({ count: rows.length, getScrollElement: () => scrollRef.current, estimateSize: () => 20, overscan: 30 });

  if (!diff.hunks.length)
    return <EmptyState title="No changes in this file">The planned bytes equal the file on disk, or the file is kept or hand-edited.</EmptyState>;

  return (
    <div className="flex h-full min-h-0 flex-col" data-testid="diff-viewer">
      <div className="flex h-8 shrink-0 items-center gap-2 border-b border-default px-2 text-12">
        <span className="min-w-0 flex-1 truncate font-mono" title={path}>
          {path}
        </span>
        <span className="text-success">+{diff.additions}</span>
        <span className="text-danger">−{diff.deletions}</span>
        <div role="group" aria-label="Diff layout" className="flex">
          <Button size="sm" variant={mode === "inline" ? "primary" : "ghost"} aria-pressed={mode === "inline"} onClick={() => setMode("inline")}>
            Inline
          </Button>
          <Button size="sm" variant={mode === "split" ? "primary" : "ghost"} aria-pressed={mode === "split"} onClick={() => setMode("split")}>
            Split
          </Button>
        </div>
      </div>
      <div ref={scrollRef} className="min-h-0 flex-1 overflow-auto bg-surface font-mono text-12" tabIndex={0} aria-label={`Diff of ${path}`} role="region">
        <div style={{ height: virtualizer.getTotalSize(), position: "relative", minWidth: "100%" }}>
          {virtualizer.getVirtualItems().map((item) => {
            const row = rows[item.index];
            const style = { position: "absolute" as const, top: 0, left: 0, right: 0, height: item.size, transform: `translateY(${item.start}px)` };
            if (row.kind === "hunk")
              return (
                <div key={item.key} style={style} className="flex items-center bg-app px-2 text-secondary">
                  {row.text}
                </div>
              );
            if (row.kind === "line") {
              const l = row.line;
              return (
                <div key={item.key} style={style} className={cn("flex items-center whitespace-pre", LINE_BG[l.type])} data-line={l.type}>
                  <Gutter n={l.oldNo} />
                  <Gutter n={l.newNo} />
                  <Mark type={l.type} />
                  <Code text={l.text} language={language} />
                </div>
              );
            }
            return (
              <div key={item.key} style={style} className="grid grid-cols-2">
                {[row.left, row.right].map((l, side) => (
                  <div
                    key={side}
                    className={cn(
                      "flex items-center overflow-hidden whitespace-pre",
                      l ? LINE_BG[l === row.right && l.type === "context" ? "context" : l.type] : "bg-app",
                      side === 0 && "border-r border-default",
                    )}
                  >
                    <Gutter n={l ? (side === 0 ? l.oldNo : l.newNo) : null} />
                    <Mark type={l?.type ?? null} />
                    {l ? <Code text={l.text} language={language} /> : null}
                  </div>
                ))}
              </div>
            );
          })}
        </div>
      </div>
    </div>
  );
}
