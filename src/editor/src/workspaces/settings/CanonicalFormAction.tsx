// Settings › Validation: "Rewrite all model files in canonical form" (POST /api/model/format with no paths, what
// `maquettiste format` does), with the count of files the validation report flags (MQ1003, MQ1010). See problems/canonical.ts.
import { useMemo, useState } from "react";
import { useQueryClient } from "@tanstack/react-query";
import { useValidation } from "@/api/queries";
import { useServices } from "@/app/context";
import { Button } from "@/components/ui/button";
import { formatModelFiles, formatNotice, nonCanonicalFiles } from "@/problems/canonical";

export function CanonicalFormAction() {
  const { store } = useServices();
  const qc = useQueryClient();
  const validation = useValidation();
  const files = useMemo(() => nonCanonicalFiles(validation.data?.diagnostics ?? []), [validation.data]);
  const [busy, setBusy] = useState(false);
  const run = async () => {
    setBusy(true);
    try {
      const notice = formatNotice(await formatModelFiles(qc));
      store.getState().notify(notice.message, notice.level);
    } catch (e) {
      store.getState().notify(`Rewrite in canonical form: ${e instanceof Error ? e.message : String(e)}`, "error");
    } finally {
      setBusy(false);
    }
  };
  const count = files.length;
  return (
    <div className="flex items-center gap-2 rounded-control border border-default bg-surface px-2 py-1" data-testid="canonical-form-action">
      <p className="text-12">
        <span data-testid="canonical-form-count">
          {validation.data
            ? count === 0
              ? "Every model file is in canonical form."
              : `${count} model ${count === 1 ? "file is" : "files are"} not in canonical form.`
            : "Checking the model files."}
        </span>{" "}
        <span className="text-secondary">
          Canonical form is the layout the editor writes (MQ1003); rewriting changes no content and also drops settings members that are no longer used
          (MQ1010).
        </span>
      </p>
      <Button
        size="sm"
        className="ml-auto shrink-0"
        disabled={busy}
        title="Rewrite maquettiste.json and every model file in canonical form, as maquettiste format does"
        onClick={() => void run()}
        data-testid="format-all"
      >
        Rewrite all model files in canonical form{count ? ` (${count})` : ""}
      </Button>
    </div>
  );
}
