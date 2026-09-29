import Editor, { DiffEditor, type OnMount } from "@monaco-editor/react";
import { useEffect, useRef, useState } from "react";
import { monacoTheme, monaco } from "./monaco-setup";

function useTheme(): string {
  const [theme, setTheme] = useState(() => monacoTheme());
  useEffect(() => {
    const observer = new MutationObserver(() => setTheme(monacoTheme()));
    observer.observe(document.documentElement, { attributes: true, attributeFilter: ["data-theme"] });
    return () => observer.disconnect();
  }, []);
  return theme;
}

const options: monaco.editor.IStandaloneEditorConstructionOptions = {
  minimap: { enabled: false },
  fontFamily: "JetBrains Mono Variable, JetBrains Mono, monospace",
  fontSize: 12,
  lineHeight: 18,
  scrollBeyondLastLine: false,
  renderLineHighlight: "none",
  automaticLayout: true,
  tabSize: 2,
  accessibilitySupport: "on",
};

/** A diagnostic shown inline (1-based line and column). */
export interface CodeMarker {
  line: number;
  column?: number | null;
  message: string;
  severity: "error" | "warning" | "info";
}

export interface CodeEditorProps {
  language: "json" | "sql" | "csharp" | "plaintext" | "scriban" | "javascript" | "markdown";
  value: string;
  onChange?: (value: string) => void;
  readOnly?: boolean;
  label: string;
  /** A model per path (its own undo history), for editors that switch between files. */
  path?: string;
  /** Ctrl+S (Cmd+S) inside the editor. */
  onSave?: () => void;
  markers?: CodeMarker[];
  /**
   * With a revision the editor owns its text while typing (a controlled value lags fast typing and would overwrite
   * keystrokes); `value` replaces the text only when the revision changes (reverting to the disk text, say).
   */
  revision?: number;
}

const SEVERITY = { error: 8, warning: 4, info: 2 } as const;

export default function CodeEditor({ language, value, onChange, readOnly, label, path, onSave, markers, revision }: CodeEditorProps) {
  const theme = useTheme();
  const saveRef = useRef(onSave);
  useEffect(() => {
    saveRef.current = onSave;
  }, [onSave]);
  const [editor, setEditor] = useState<monaco.editor.IStandaloneCodeEditor | null>(null);
  const handleMount: OnMount = (instance) => {
    instance.addCommand(monaco.KeyMod.CtrlCmd | monaco.KeyCode.KeyS, () => saveRef.current?.());
    setEditor(instance);
  };
  const latest = useRef(value);
  useEffect(() => {
    latest.current = value;
  }, [value]);
  useEffect(() => {
    const model = editor?.getModel();
    if (revision === undefined || !model || model.getValue() === latest.current) return;
    model.pushEditOperations([], [{ range: model.getFullModelRange(), text: latest.current }], () => null);
  }, [editor, revision, path]);
  useEffect(() => {
    const model = editor?.getModel();
    if (!model) return;
    monaco.editor.setModelMarkers(
      model,
      "maquettiste",
      (markers ?? []).map((m) => ({
        startLineNumber: m.line,
        startColumn: m.column ?? 1,
        endLineNumber: m.line,
        endColumn: m.column ? m.column + 1 : model.getLineMaxColumn(Math.min(m.line, model.getLineCount())),
        message: m.message,
        severity: SEVERITY[m.severity],
      })),
    );
  }, [editor, markers, path, value]);
  return (
    <div className="h-full min-h-40" data-testid={`code-${language}`}>
      <Editor
        language={language}
        {...(revision === undefined ? { value } : { defaultValue: value })}
        path={path}
        theme={theme}
        onMount={handleMount}
        onChange={(v) => onChange?.(v ?? "")}
        options={{ ...options, readOnly, ariaLabel: label, domReadOnly: readOnly }}
      />
      <pre className="sr-only" aria-hidden data-testid={`code-text-${language}`}>
        {value}
      </pre>
    </div>
  );
}

export function CodeDiffEditor({
  original,
  modified,
  onMount,
  label,
}: {
  original: string;
  modified: string;
  onMount?: (getModified: () => string) => void;
  label: string;
}) {
  const theme = useTheme();
  const editorRef = useRef<monaco.editor.IStandaloneDiffEditor | null>(null);
  const handleMount: Parameters<typeof DiffEditor>[0]["onMount"] = (editor) => {
    editorRef.current = editor;
    onMount?.(() => editor.getModifiedEditor().getValue());
  };
  return (
    <div className="h-full min-h-72">
      <DiffEditor
        language="json"
        original={original}
        modified={modified}
        theme={theme}
        onMount={handleMount}
        options={{ ...options, renderSideBySide: true, originalEditable: false, ariaLabel: label }}
      />
    </div>
  );
}

export type { OnMount };
