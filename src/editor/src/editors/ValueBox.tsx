// The Value box (object-effects-design.md section 10): an expression written as text, with its preview under it: the
// printed form, the type and each finding once. One shared component, written so the guard and check inspectors can use
// it later; sets use it first. It holds the text while typing and gives it back on commit (blur or Enter). The caller
// saves `{ text }`, which the server stores as a tree.
import { useEffect, useState } from "react";
import { useQuery } from "@tanstack/react-query";
import { previewExpression, type ExpressionPreviewRequest } from "@/api/expressionPreview";
import { Input } from "@/components/ui/input";
import { EFFECT_LABELS as L } from "@/model/effectLabels";

function useDebounced<T>(value: T, ms: number): T {
  const [out, setOut] = useState(value);
  useEffect(() => {
    const t = setTimeout(() => setOut(value), ms);
    return () => clearTimeout(t);
  }, [value, ms]);
  return out;
}

export interface ValueBoxProps {
  id: string;
  label: string;
  /** The text the box starts with. */
  initial: string;
  /** The preview request for the text now in the box, or null to ask nothing (the text is empty). Given `undefined` text, the stored expression is previewed. */
  request: (text: string | undefined) => ExpressionPreviewRequest | null;
  /** The text changed (every keystroke): the dialog keeps its draft. */
  onChange: (text: string) => void;
  onCommit?: (text: string) => void;
  placeholder?: string;
  /** The expression is a condition (true or false): its type line says so, in yes or no words. */
  condition?: boolean;
}

export function ValueBox({ id, label, initial, request, onChange, onCommit, placeholder = L.valuePlaceholder, condition }: ValueBoxProps) {
  const [text, setText] = useState(initial);
  const edited = text !== initial;
  const wanted = useDebounced(edited ? text : undefined, 250);
  const body = (edited ? wanted !== undefined : true) ? request(edited ? wanted : undefined) : null;
  const preview = useQuery({
    queryKey: ["expression-preview", body],
    queryFn: ({ signal }) => previewExpression(body!, signal),
    enabled: !!body,
    staleTime: 5_000,
  });
  const detail = preview.data?.preview;
  const findings = [...new Map((preview.data?.diagnostics ?? []).map((d) => [`${d.rule}|${d.message}`, d])).values()];
  return (
    <div className="flex min-w-0 flex-1 flex-col gap-0.5" data-testid="value-box">
      <Input
        id={id}
        aria-label={label}
        className="h-6 font-mono"
        value={text}
        placeholder={placeholder}
        onChange={(e) => {
          setText(e.target.value);
          onChange(e.target.value);
        }}
        onBlur={() => onCommit?.(text)}
        onKeyDown={(e) => {
          if (e.key === "Enter") onCommit?.(text);
        }}
      />
      {detail ? (
        <span className="truncate text-11 text-secondary" title={detail.text} data-testid="value-preview">
          {condition ? `Preview: ${detail.text} · ` : detail.text !== text.trim() ? `${detail.text} · ` : ""}
          {condition ? L.conditionType(detail.type, detail.nullable) : L.previewType(detail.type, detail.nullable)}
        </span>
      ) : null}
      {findings.map((d) => (
        <span key={`${d.rule}|${d.message}`} className="text-11 text-danger" data-testid="value-finding" title={d.message}>
          {d.rule} {d.message}
        </span>
      ))}
    </div>
  );
}
