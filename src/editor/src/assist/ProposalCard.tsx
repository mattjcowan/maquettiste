// A proposal of the assistant: its summary, each file it would create, change or delete (expandable to the diff of its
// text before and after), and Apply / Discard; after a conflict, Ask again.
import { useState } from "react";
import { ChevronDown, ChevronRight } from "lucide-react";
import type { AssistProposal } from "@/api/assist";
import { Button } from "@/components/ui/button";
import { CodeDiff } from "@/code";
import { cn } from "@/lib/cn";
import { isOpen, prettyText, type CardState } from "./model";

const STATE_LABEL: Record<CardState["kind"], string> = {
  pending: "Waiting for review",
  applying: "Applying…",
  applied: "Applied",
  discarded: "Discarded",
  conflict: "Conflict",
  failed: "Not applied",
};

const ACTION_CLASS: Record<string, string> = {
  created: "text-success",
  changed: "text-warning",
  deleted: "text-danger",
};

export function ProposalCard({
  proposal,
  state,
  canApply,
  busy,
  onApply,
  onDiscard,
  onAskAgain,
}: {
  proposal: AssistProposal;
  state: CardState;
  canApply: boolean;
  busy: boolean;
  onApply: () => void;
  onDiscard: () => void;
  onAskAgain: () => void;
}) {
  const [expanded, setExpanded] = useState<string | null>(null);
  const open = isOpen(state);
  return (
    <section
      className="flex flex-col gap-1 rounded-control border border-default bg-app p-1.5"
      aria-label={`Proposed change: ${proposal.summary}`}
      data-testid="proposal-card"
      data-state={state.kind}
    >
      <div className="flex items-start gap-2">
        <div className="min-w-0 flex-1">
          <div className="text-11 font-semibold uppercase tracking-wide text-secondary">Proposed change</div>
          <div className="break-words text-13 font-medium">{proposal.summary}</div>
        </div>
        <span
          className={cn(
            "shrink-0 rounded-full border border-default px-1.5 text-11",
            state.kind === "applied" && "text-success",
            (state.kind === "conflict" || state.kind === "failed") && "text-danger",
            state.kind === "discarded" && "text-secondary",
          )}
          data-testid="proposal-state"
        >
          {STATE_LABEL[state.kind]}
        </span>
      </div>
      <ul className="flex flex-col" aria-label="Files">
        {proposal.files.map((file) => {
          const shown = expanded === file.path;
          return (
            <li key={file.path} className="flex flex-col">
              <button
                type="button"
                className="flex items-center gap-1 rounded-control px-1 py-0.5 text-left text-12 hover:bg-accent-subtle"
                aria-expanded={shown}
                title={shown ? `Hide the diff of ${file.path}` : `Show the diff of ${file.path}`}
                onClick={() => setExpanded(shown ? null : file.path)}
                data-testid="proposal-file"
              >
                {shown ? <ChevronDown className="size-3.5 shrink-0" aria-hidden /> : <ChevronRight className="size-3.5 shrink-0" aria-hidden />}
                <span className={cn("w-14 shrink-0 font-medium", ACTION_CLASS[file.action])}>{file.action}</span>
                <span className="shrink-0 text-secondary">{file.kind ?? "file"}</span>
                <span className="truncate font-medium">{file.name ?? file.path.split("/").pop()}</span>
              </button>
              {shown ? (
                <div className="flex flex-col gap-0.5 pl-4">
                  <span className="truncate font-mono text-11 text-secondary" title={file.path}>
                    {file.path}
                  </span>
                  <div className="h-64 overflow-hidden rounded-control border border-default" data-testid="proposal-diff">
                    <CodeDiff inline label={`Diff of ${file.path}`} original={prettyText(file.before)} modified={prettyText(file.after)} />
                  </div>
                </div>
              ) : null}
            </li>
          );
        })}
      </ul>
      {state.kind === "conflict" || state.kind === "failed" ? (
        <p role="alert" className="text-12 text-danger">
          {state.message}
        </p>
      ) : null}
      {open ? (
        <div className="flex flex-wrap items-center justify-end gap-1">
          {state.kind === "conflict" ? (
            <Button size="sm" onClick={onAskAgain} disabled={busy} data-testid="proposal-ask-again">
              Ask again
            </Button>
          ) : null}
          <Button size="sm" onClick={onDiscard} data-testid="proposal-discard">
            Discard
          </Button>
          <span title={canApply ? undefined : "Applying a change needs the editor role."}>
            <Button size="sm" variant="primary" onClick={onApply} disabled={!canApply} data-testid="proposal-apply">
              Apply
            </Button>
          </span>
        </div>
      ) : null}
    </section>
  );
}
