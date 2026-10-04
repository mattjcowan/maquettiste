// Settings › Assistant (erratum E44): the project's house rules for the assistant and its budgets, saved in maquettiste.json
// `assistant` through PUT /api/project/settings, and what the functions can see of the host's AI (GET /api/assist/status).
// The provider, its key and the model are chosen in the host, never here.
import { useState } from "react";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { keys, useSettings } from "@/api/queries";
import * as endpoints from "@/api/endpoints";
import { getAssistStatus } from "@/api/assist";
import type { SettingsJson } from "@/api/types";
import { useServices } from "@/app/context";
import { assistKeys } from "@/assist/AssistantPanel";
import { Button } from "@/components/ui/button";
import { Field, Input } from "@/components/ui/input";
import { SectionTitle, Spinner } from "@/components/ui/misc";
import { cn } from "@/lib/cn";
import { ASSISTANT_DEFAULTS, MAX_INSTRUCTIONS, assistantOf, assistantProblems, sameAssistant, withAssistant, type AssistantDraft } from "./assistantSettings";

export function AssistantSettings() {
  const settings = useSettings();
  const status = useQuery({ queryKey: assistKeys.status, queryFn: getAssistStatus });
  const qc = useQueryClient();
  const { store } = useServices();
  const [draft, setDraft] = useState<AssistantDraft | null>(null);
  const [busy, setBusy] = useState(false);
  const [errors, setErrors] = useState<string[]>([]);
  if (settings.isPending || !settings.data) return <Spinner label="Loading settings" />;
  const base = assistantOf(settings.data.json as SettingsJson);
  const value = draft ?? base;
  const dirty = !!draft && !sameAssistant(draft, base);
  const problems = assistantProblems(value);
  const edit = (patch: Partial<AssistantDraft>) => setDraft({ ...value, ...patch });

  const save = async () => {
    if (!settings.data || !draft) return;
    setBusy(true);
    setErrors([]);
    try {
      const result = await endpoints.saveSettings(withAssistant(settings.data.json as SettingsJson, draft), settings.data.hash);
      if (result.outcome === "saved" && result.current) {
        qc.setQueryData(keys.settings, result.current);
        void qc.invalidateQueries({ queryKey: keys.project });
        void qc.invalidateQueries({ queryKey: assistKeys.status });
        setDraft(null);
        store.getState().notify("Assistant settings saved.");
      } else if (result.outcome === "conflict") {
        await qc.invalidateQueries({ queryKey: keys.settings });
        store.getState().notify("maquettiste.json changed on disk. Your changes are kept; review them and save again.", "error");
      } else setErrors(result.diagnostics.map((d) => `${d.rule} ${d.jsonPointer ?? ""} ${d.message}`));
    } finally {
      setBusy(false);
    }
  };

  const s = status.data;
  return (
    <section className="flex max-w-3xl flex-col gap-3" aria-label="Assistant" data-testid="assistant-settings">
      <div className="flex flex-col gap-1 rounded-control border border-default bg-surface p-2" data-testid="assistant-status">
        <SectionTitle>Provider</SectionTitle>
        {status.isPending ? (
          <Spinner label="Reading the assistant's status" />
        ) : status.isError || !s ? (
          <p className="text-12 text-danger">The status could not be read.</p>
        ) : (
          <>
            <p className="text-13">
              <span className={cn("font-semibold", s.configured ? "text-success" : "text-warning")} data-testid="assistant-configured">
                {s.configured ? "Configured" : "Not configured"}
              </span>
              {s.configured ? <span className="text-secondary"> · model {s.model ?? "the provider's default"}</span> : null}
            </p>
            <p className="text-12 text-secondary">
              The assistant uses the AI provider of the host that serves this editor; Maquettiste never holds a key. In the host's management UI, add a provider
              and its key under AI › providers, then pick it and the model for this site under Sites › your site › AI.
            </p>
            {s.hostAiUrl ? (
              <p className="text-12">
                <a className="text-accent underline-offset-2 hover:underline" href={s.hostAiUrl} target="_blank" rel="noreferrer noopener">
                  Open the host's management UI
                </a>
              </p>
            ) : null}
            <p className="text-12 text-secondary">
              Your tokens today: {s.usedToday.toLocaleString()} of {s.tokenBudgetPerDayPerUser.toLocaleString()}.
            </p>
          </>
        )}
      </div>

      <div className="flex items-center gap-2">
        <p className="text-12 text-secondary">House rules and budgets, saved in maquettiste.json. They change no generated output.</p>
        <span className="ml-auto flex items-center gap-2">
          {dirty ? <span className="text-12 text-secondary">Unsaved changes</span> : null}
          <Button onClick={() => setDraft(null)} disabled={!dirty || busy}>
            Discard
          </Button>
          <Button variant="primary" onClick={() => void save()} disabled={!dirty || busy || Object.keys(problems).length > 0} data-testid="save-assistant">
            Save
          </Button>
        </span>
      </div>
      {errors.length ? (
        <ul role="alert" className="text-12 text-danger">
          {errors.map((d, i) => (
            <li key={i}>{d}</li>
          ))}
        </ul>
      ) : null}
      <Field
        label="Instructions"
        htmlFor="assistant-instructions"
        hint={problems.instructions ?? `Appended to the assistant's system prompt: naming, conventions, what to avoid. At most ${MAX_INSTRUCTIONS} characters.`}
      >
        <textarea
          id="assistant-instructions"
          className="min-h-32 w-full rounded-control border border-input bg-app p-1.5 text-13"
          value={value.instructions}
          onChange={(e) => edit({ instructions: e.target.value })}
        />
      </Field>
      <div className="flex flex-wrap gap-3">
        <Field
          label="Turns per request"
          htmlFor="assistant-max-turns"
          hint={problems.maxTurns ?? `Default ${ASSISTANT_DEFAULTS.maxTurns}; the last turn must answer in text.`}
        >
          <Input id="assistant-max-turns" className="w-28" inputMode="numeric" value={value.maxTurns} onChange={(e) => edit({ maxTurns: e.target.value })} />
        </Field>
        <Field
          label="Tokens per request"
          htmlFor="assistant-request-budget"
          hint={problems.tokenBudgetPerRequest ?? `Default ${ASSISTANT_DEFAULTS.tokenBudgetPerRequest.toLocaleString()}.`}
        >
          <Input
            id="assistant-request-budget"
            className="w-36"
            inputMode="numeric"
            value={value.tokenBudgetPerRequest}
            onChange={(e) => edit({ tokenBudgetPerRequest: e.target.value })}
          />
        </Field>
        <Field
          label="Tokens per user per day"
          htmlFor="assistant-day-budget"
          hint={problems.tokenBudgetPerDayPerUser ?? `Default ${ASSISTANT_DEFAULTS.tokenBudgetPerDayPerUser.toLocaleString()} (UTC day).`}
        >
          <Input
            id="assistant-day-budget"
            className="w-36"
            inputMode="numeric"
            value={value.tokenBudgetPerDayPerUser}
            onChange={(e) => edit({ tokenBudgetPerDayPerUser: e.target.value })}
          />
        </Field>
      </div>
    </section>
  );
}
