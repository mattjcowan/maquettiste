// The Problems panel's quick fix for MQ1003 and MQ1010 (canonical.ts): rewrites the finding's file in canonical form, and the
// report re-validates after it. No dialog: the rewrite changes no content.
import { useState } from "react";
import { useQueryClient } from "@tanstack/react-query";
import { Wrench } from "lucide-react";
import type { Diagnostic } from "@/api/types";
import { Button } from "@/components/ui/button";
import { useServices } from "@/app/context";
import { formatModelFiles, formatNotice } from "./canonical";

export function CanonicalFixButton({ diagnostic }: { diagnostic: Diagnostic }) {
  const { store } = useServices();
  const qc = useQueryClient();
  const [busy, setBusy] = useState(false);
  const path = diagnostic.filePath;
  if (!path) return null;
  const run = async () => {
    setBusy(true);
    try {
      const notice = formatNotice(await formatModelFiles(qc, [path]));
      store.getState().notify(notice.message, notice.level);
    } catch (e) {
      store.getState().notify(`Rewrite in canonical form: ${e instanceof Error ? e.message : String(e)}`, "error");
    } finally {
      setBusy(false);
    }
  };
  return (
    <Button
      size="sm"
      variant="ghost"
      className="mr-2 shrink-0"
      disabled={busy}
      data-testid="fix-canonical"
      title={`Quick fix: rewrite ${path} in canonical form (the content does not change)`}
      onClick={() => void run()}
    >
      <Wrench /> Rewrite in canonical form
    </Button>
  );
}
