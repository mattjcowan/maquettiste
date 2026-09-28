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

export interface CodeEditorProps {
  language: "json" | "sql" | "csharp" | "plaintext";
  value: string;
  onChange?: (value: string) => void;
  readOnly?: boolean;
  label: string;
}

export default function CodeEditor({ language, value, onChange, readOnly, label }: CodeEditorProps) {
  const theme = useTheme();
  return (
    <div className="h-full min-h-40" data-testid={`code-${language}`}>
      <Editor
        language={language}
        value={value}
        theme={theme}
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
