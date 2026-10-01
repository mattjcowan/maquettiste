// The `comments` convention in Settings > Conventions: whether a table's or column's database comment comes from its
// description (the engine default) or only from an explicit comment. Like the other conventions, a database can override the
// project, and an unset value inherits (the project's, else the engine default).
import type { SettingsJson } from "@/api/types";
import { Field, Select } from "@/components/ui/input";

export const COMMENT_SOURCES = ["descriptions", "none"] as const;
export type CommentSource = (typeof COMMENT_SOURCES)[number];

const LABELS: Record<CommentSource, string> = {
  descriptions: "descriptions: fill from descriptions",
  none: "none: explicit comments only",
};

type Bag = Record<string, unknown> | undefined;

/** The value set at the scope ("" for the project), or undefined; and what an unset value inherits. */
export function commentsAt(json: SettingsJson, scope: string): { value: CommentSource | undefined; inherited: string } {
  const project = (json as { conventions?: Bag }).conventions?.comments as CommentSource | undefined;
  const own = scope ? ((json as { databases?: Record<string, Bag> }).databases?.[scope]?.comments as CommentSource | undefined) : project;
  return { value: own, inherited: scope && project ? `${project} (project)` : "descriptions (engine default)" };
}

export function CommentsConvention({ json, scope, onChange }: { json: SettingsJson; scope: string; onChange: (value: CommentSource | undefined) => void }) {
  const { value, inherited } = commentsAt(json, scope);
  return (
    <Field
      label="comments"
      htmlFor="conv-comments"
      hint="Without an explicit comment, a table or column takes its own description, else its entity's or attribute's. The sql-ddl pack writes comments while its comments parameter is on."
    >
      <Select
        id="conv-comments"
        data-testid="conv-comments"
        title="Where database comments come from"
        value={value ?? ""}
        onChange={(e) => onChange(e.target.value === "" ? undefined : (e.target.value as CommentSource))}
      >
        <option value="">inherit: {inherited}</option>
        {COMMENT_SOURCES.map((o) => (
          <option key={o} value={o}>
            {LABELS[o]}
          </option>
        ))}
      </Select>
    </Field>
  );
}
