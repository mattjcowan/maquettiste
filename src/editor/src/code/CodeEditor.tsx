import Editor, { DiffEditor, type OnMount } from "@monaco-editor/react";
import { useEffect, useRef, useState } from "react";
import { monacoTheme, monaco, setJsonSchema } from "./monaco-setup";
import { setCompletionData } from "./scribanProviders";
import type { TemplateCompletionData } from "./scribanCompletion";

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
  /** Ctrl+S (Cmd+S) inside the editor, with the editor's text at that moment (the state's copy may lag a keystroke). */
  onSave?: (text: string) => void;
  /** The text lost focus (an editor that saves on leaving it). */
  onBlur?: () => void;
  markers?: CodeMarker[];
  /**
   * With a revision the editor owns its text while typing (a controlled value lags fast typing and would overwrite
   * keystrokes); `value` replaces the text only when the revision changes (reverting to the disk text, say).
   */
  revision?: number;
  /** Scriban completion and hover data (a unit's template context). */
  completion?: TemplateCompletionData | null;
  /** The 1-based line of the cursor, on every move. */
  onCursorLine?: (line: number) => void;
  /** 1-based lines to highlight (the lines of a template that match a chosen output line). */
  highlightLines?: number[];
  /**
   * A JSON schema to validate the text against as it is typed: `ref` is the `$schema` value the file carries (resolved against the
   * model's URI, so a file that names it finds this schema without a request), `schema` the schema itself.
   */
  jsonSchema?: { ref: string; schema: unknown } | null;
  /** The editor's own findings (a JSON syntax or schema problem), on every change. */
  onMarkers?: (markers: CodeMarker[]) => void;
}

const SEVERITY = { error: 8, warning: 4, info: 2 } as const;

export default function CodeEditor({
  language,
  value,
  onChange,
  readOnly,
  label,
  path,
  onSave,
  onBlur,
  markers,
  revision,
  completion,
  onCursorLine,
  highlightLines,
  jsonSchema,
  onMarkers,
}: CodeEditorProps) {
  const theme = useTheme();
  const saveRef = useRef(onSave);
  useEffect(() => {
    saveRef.current = onSave;
  }, [onSave]);
  const blurRef = useRef(onBlur);
  useEffect(() => {
    blurRef.current = onBlur;
  }, [onBlur]);
  const [editor, setEditor] = useState<monaco.editor.IStandaloneCodeEditor | null>(null);
  const cursorRef = useRef(onCursorLine);
  useEffect(() => {
    cursorRef.current = onCursorLine;
  }, [onCursorLine]);
  const handleMount: OnMount = (instance) => {
    instance.addCommand(monaco.KeyMod.CtrlCmd | monaco.KeyCode.KeyS, () => saveRef.current?.(instance.getValue()));
    instance.onDidChangeCursorPosition((e) => cursorRef.current?.(e.position.lineNumber));
    instance.onDidBlurEditorText(() => blurRef.current?.());
    setEditor(instance);
  };
  useEffect(() => {
    const uri = editor?.getModel()?.uri.toString();
    if (!uri || !completion) return;
    setCompletionData(uri, completion);
    return () => setCompletionData(uri, null);
  }, [editor, completion, path]);
  useEffect(() => {
    const uri = editor?.getModel()?.uri.toString();
    if (!uri || !jsonSchema) return;
    let schemaUri: string;
    try {
      schemaUri = new URL(jsonSchema.ref, uri).toString();
    } catch {
      schemaUri = `maquettiste:/${jsonSchema.ref.replace(/^[./]+/, "")}`;
    }
    setJsonSchema(uri, { uri: schemaUri, fileMatch: [uri], schema: jsonSchema.schema });
    return () => setJsonSchema(uri, null);
  }, [editor, jsonSchema, path]);
  const markersRef = useRef(onMarkers);
  useEffect(() => {
    markersRef.current = onMarkers;
  }, [onMarkers]);
  useEffect(() => {
    const model = editor?.getModel();
    if (!model || !markersRef.current) return;
    const uri = model.uri.toString();
    // Only the other owners' markers, and only when they change: this editor's own markers (set from props) also fire the event.
    let last = "";
    const read = () => {
      const found: CodeMarker[] = monaco.editor
        .getModelMarkers({ resource: model.uri })
        .filter((m) => m.owner !== "maquettiste")
        .map((m) => ({
          line: m.startLineNumber,
          column: m.startColumn,
          message: m.message,
          severity: m.severity >= SEVERITY.error ? "error" : m.severity >= SEVERITY.warning ? "warning" : "info",
        }));
      const key = JSON.stringify(found);
      if (key === last) return;
      last = key;
      markersRef.current?.(found);
    };
    read();
    const sub = monaco.editor.onDidChangeMarkers((uris) => {
      if (uris.some((u) => u.toString() === uri)) read();
    });
    return () => sub.dispose();
  }, [editor, path]);
  const decorations = useRef<monaco.editor.IEditorDecorationsCollection | null>(null);
  useEffect(() => {
    if (!editor) return;
    decorations.current ??= editor.createDecorationsCollection();
    const lines = highlightLines ?? [];
    decorations.current.set(
      lines.map((line) => ({
        range: new monaco.Range(line, 1, line, 1),
        options: { isWholeLine: true, className: "mq-line-match", linesDecorationsClassName: "mq-line-match-gutter" },
      })),
    );
    if (lines.length) editor.revealLineInCenterIfOutsideViewport(lines[0]);
  }, [editor, highlightLines, path]);
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
    // `nokey`: a canvas mounted behind (the Database screen under an editor tab) leaves the keys typed here alone (React Flow
    // takes Space to pan unless the target is an input, and Monaco's text area may be a plain element).
    <div className="nokey h-full min-h-40" data-testid={`code-${language}`} data-highlight={highlightLines?.length ? highlightLines.join(",") : undefined}>
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
  // Detach the models from the diff editor before they are disposed (the library, left to it, disposes the models first and
  // Monaco reports "TextModel got disposed before DiffEditorWidget model got reset"). This cleanup runs before the child's.
  useEffect(
    () => () => {
      const editor = editorRef.current;
      if (!editor) return;
      const model = editor.getModel();
      editor.setModel(null);
      model?.original.dispose();
      model?.modified.dispose();
      editorRef.current = null;
    },
    [],
  );
  return (
    <div className="h-full min-h-72">
      <DiffEditor
        keepCurrentOriginalModel
        keepCurrentModifiedModel
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
