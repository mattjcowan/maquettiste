// The Generate toolbar's Options popover (planOptions.ts): Re-render every file and the hand-edit choice for the next plan. One
// run only: nothing is saved, and the button shows a dot while a choice differs from the default.
import { SlidersHorizontal } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Checkbox } from "@/components/ui/checkbox";
import { Select } from "@/components/ui/input";
import { Popover, PopoverContent, PopoverTrigger } from "@/components/ui/popover";
import { HAND_EDIT_CHOICES, hasNonDefault, projectDefaultLabel, type HandEditPolicy, type PlanOptions } from "./planOptions";

export function PlanOptionsButton({
  value,
  onChange,
  projectDefault,
  disabled,
}: {
  value: PlanOptions;
  onChange: (next: PlanOptions) => void;
  projectDefault: HandEditPolicy | null | undefined;
  disabled?: boolean;
}) {
  const changed = hasNonDefault(value);
  return (
    <Popover>
      <PopoverTrigger asChild>
        <Button size="sm" variant="ghost" title="Plan options" disabled={disabled} data-testid="generate-options" data-changed={changed || undefined}>
          <SlidersHorizontal aria-hidden />
          Options
          {changed ? (
            <>
              <span aria-hidden className="size-1.5 rounded-full bg-accent" data-testid="generate-options-dot" />
              <span className="sr-only">(changed)</span>
            </>
          ) : null}
        </Button>
      </PopoverTrigger>
      <PopoverContent className="flex w-80 flex-col gap-2" align="end" aria-label="Plan options" data-testid="generate-options-panel">
        <p className="text-11 text-secondary">For the next plan only; nothing is saved.</p>
        <div className="flex items-start gap-2">
          <Checkbox
            id="plan-option-force"
            className="mt-0.5"
            checked={value.force}
            onCheckedChange={(v) => onChange({ ...value, force: v === true })}
            data-testid="plan-option-force"
          />
          <label htmlFor="plan-option-force" className="flex flex-col">
            <span className="font-medium">Re-render every file</span>
            <span className="text-11 text-secondary">
              Every unit renders again even if nothing it reads changed. Nothing is written until Apply, and Apply still writes only the files whose content
              differs.
            </span>
          </label>
        </div>
        <div className="flex flex-col gap-1">
          <label htmlFor="plan-option-hand-edits" className="font-medium">
            If a generated file was edited by hand
          </label>
          <Select
            id="plan-option-hand-edits"
            className="h-6 text-12"
            value={value.handEdits ?? ""}
            onChange={(e) => onChange({ ...value, handEdits: (e.target.value || null) as HandEditPolicy | null })}
            data-testid="plan-option-hand-edits"
          >
            <option value="">{projectDefaultLabel(projectDefault)}</option>
            {HAND_EDIT_CHOICES.map((c) => (
              <option key={c.value} value={c.value}>
                {c.label}
              </option>
            ))}
          </Select>
          <span className="text-11 text-secondary">
            Stop and report it leaves the file and lists it as a conflict; Overwrite it replaces your edit; Keep it and skip leaves your edit and generates
            nothing for it.
          </span>
        </div>
      </PopoverContent>
    </Popover>
  );
}
