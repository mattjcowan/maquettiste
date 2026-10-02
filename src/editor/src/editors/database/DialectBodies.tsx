// SQL text per dialect, one code editor per dialect the document holds, with Add dialect for the others: a view's, a routine's
// and a SQL object's `body`, a database type's `definition`. A text is saved when its editor loses focus, on Ctrl+S or with
// Save, one undo step each; adding or removing a dialect is one step too.
import { useEffect, useRef, useState } from "react";
import { Plus, Trash2 } from "lucide-react";
import { Button } from "@/components/ui/button";
import { DropdownMenu, DropdownMenuContent, DropdownMenuItem, DropdownMenuTrigger } from "@/components/ui/menu";
import { Badge, EmptyState } from "@/components/ui/misc";
import { CodeView } from "@/code";
import { DIALECTS } from "@/inspector/fields";
import { useElements } from "@/api/queries";
import { ANY_DIALECT } from "@/explorer/databaseCreate";
import type { EditorContext } from "../EditorFrame";
import { dialectKeys, removeDialectText, setDialectText } from "./databaseDocs";

type Rec = Record<string, unknown>;

/** A dialect as the body tabs name it. */
export const dialectLabel = (d: string) => (d === ANY_DIALECT ? "Any dialect (*)" : d);

export interface DialectBodiesProps {
  ctx: EditorContext;
  /** The per-dialect member: `body` or `definition`. */
  member: string;
  /** The test id prefix and file name stem (`view` gives view-body, view-add-dialect, view-body-<dialect>). */
  prefix: string;
  /** What the object is, for labels ("View", "Routine"). */
  noun: string;
  /** The line above the editors. */
  intro: string;
  /** A starting text for a dialect added when there is none to copy. */
  template: (dialect: string) => string;
  /** The member is required: its last dialect cannot be removed. (A text is never saved empty.) */
  required: boolean;
  /** Shown when an optional member holds no dialect. */
  empty?: { title: string; text: string };
}

export function DialectBodies({ ctx, member, prefix, noun, intro, template, required, empty }: DialectBodiesProps) {
  const doc = ctx.json as unknown as Rec;
  const texts = (doc[member] as Record<string, string> | undefined) ?? {};
  const dialects = dialectKeys(doc, member);
  const database = String(doc.database ?? "");
  const dbDialect = String((useElements(database ? [database] : []).byId.get(database)?.json as Rec | undefined)?.dialect ?? "");
  const missing = [ANY_DIALECT, ...DIALECTS].filter((d) => !dialects.includes(d));
  const update = (mutate: (doc: Rec) => void) => {
    ctx.edit((j) => void mutate(j as unknown as Rec));
    ctx.flush();
  };
  return (
    <div className="flex flex-col gap-2" data-testid={`${prefix}-body`}>
      <div className="flex items-center gap-2">
        <p className="min-w-0 flex-1 text-12 text-secondary">
          {intro}
          {dbDialect ? ` This database is ${dbDialect}.` : ""}
        </p>
        <DropdownMenu>
          <DropdownMenuTrigger asChild>
            <Button size="sm" variant="ghost" disabled={!missing.length} data-testid={`${prefix}-add-dialect`}>
              <Plus /> Add dialect
            </Button>
          </DropdownMenuTrigger>
          <DropdownMenuContent align="end">
            {missing.map((d) => (
              <DropdownMenuItem
                key={d}
                onSelect={() => update((j) => setDialectText(j, member, d, texts[dialects[0] ?? ""] ?? template(d)))}
                data-testid={`${prefix}-add-dialect-${d}`}
              >
                {dialectLabel(d)}
              </DropdownMenuItem>
            ))}
          </DropdownMenuContent>
        </DropdownMenu>
      </div>
      {!dialects.length && empty ? <EmptyState title={empty.title}>{empty.text}</EmptyState> : null}
      {dialects.map((d) => (
        <BodySection
          key={d}
          path={`${prefix}-${ctx.id}-${d === ANY_DIALECT ? "any" : d}.sql`}
          prefix={prefix}
          noun={noun}
          textNoun={member === "definition" ? "definition" : "body"}
          dialect={d}
          saved={texts[d] ?? ""}
          removable={!required || dialects.length > 1}
          onCommit={(text) => update((j) => setDialectText(j, member, d, text))}
          onRemove={() => update((j) => void removeDialectText(j, member, d, required))}
        />
      ))}
    </div>
  );
}

/** One dialect's text: typed locally (Monaco owns the text while typing) and saved as one edit when it leaves the editor. */
function BodySection({
  path,
  prefix,
  noun,
  textNoun,
  dialect,
  saved,
  removable,
  onCommit,
  onRemove,
}: {
  path: string;
  prefix: string;
  noun: string;
  /** "body" or "definition". */
  textNoun: string;
  dialect: string;
  saved: string;
  removable: boolean;
  onCommit: (text: string) => void;
  onRemove: () => void;
}) {
  const [text, setText] = useState(saved);
  const [revision, setRevision] = useState(0);
  const textRef = useRef(text);
  textRef.current = text;
  const shown = useRef(saved);
  // The saved text changed elsewhere (undo, another window): the editor shows it.
  useEffect(() => {
    if (saved === shown.current) return;
    shown.current = saved;
    setText(saved);
    setRevision((r) => r + 1);
  }, [saved]);
  const dirty = text !== saved;
  const empty = !text.trim();
  const commit = () => {
    const now = textRef.current;
    if (now === shown.current || !now.trim()) return;
    shown.current = now;
    onCommit(now);
  };
  return (
    <section
      className="flex flex-col rounded-control border border-default"
      aria-label={`${textNoun[0].toUpperCase()}${textNoun.slice(1)} for ${dialectLabel(dialect)}`}
      data-testid={`${prefix}-body-${dialect}`}
    >
      <div className="flex h-7 items-center gap-2 border-b border-default px-2 text-12">
        <span className="font-medium">{dialectLabel(dialect)}</span>
        {dirty ? <Badge tone="accent">Unsaved</Badge> : null}
        {empty ? <span className="text-danger">A {textNoun} cannot be empty.</span> : null}
        <span className="flex-1" />
        <Button size="sm" variant="ghost" disabled={!dirty || empty} onClick={commit} data-testid={`${prefix}-body-save-${dialect}`}>
          Save
        </Button>
        <Button
          size="icon-row"
          variant="ghost"
          label={removable ? `Remove the ${dialectLabel(dialect)} ${textNoun}` : `A ${noun.toLowerCase()} keeps at least one ${textNoun}`}
          disabled={!removable}
          onClick={onRemove}
        >
          <Trash2 />
        </Button>
      </div>
      <div className="h-48">
        <CodeView
          language="sql"
          label={`${noun} ${textNoun} for ${dialectLabel(dialect)}`}
          path={path}
          value={text}
          revision={revision}
          onChange={setText}
          onBlur={commit}
          onSave={commit}
        />
      </div>
    </section>
  );
}
