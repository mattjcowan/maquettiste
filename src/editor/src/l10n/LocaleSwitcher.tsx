// The top bar's content-locale switcher and the explorer's content-locale chip (reference-types-seeds-localization.md
// 3.10). Both render nothing while the project declares fewer than two locales.
import { ChevronDown, Languages, X } from "lucide-react";
import { Button } from "@/components/ui/button";
import { DropdownMenu, DropdownMenuContent, DropdownMenuLabel, DropdownMenuRadioGroup, DropdownMenuRadioItem, DropdownMenuTrigger } from "@/components/ui/menu";
import { Tooltip } from "@/components/ui/tooltip";
import { setPreferredContentLocale, useContentLocale } from "./contentLocale";
import { completenessOf, localeName, percent } from "./model";
import { useLocalization } from "./queries";

export function LocaleSwitcher() {
  const l10n = useLocalization();
  const { effective } = useContentLocale();
  if (!l10n.enabled || !l10n.data?.defaultLocale) return null;
  const def = l10n.data.defaultLocale;
  const current = effective ?? def;
  return (
    <DropdownMenu>
      <DropdownMenuTrigger asChild>
        <Button variant="ghost" size="sm" aria-label={`Content locale: ${localeName(current)}`} data-testid="locale-switcher">
          <Languages className="size-4" aria-hidden />
          <span>Content: {localeName(current)}</span>
          <ChevronDown className="size-3" aria-hidden />
        </Button>
      </DropdownMenuTrigger>
      <DropdownMenuContent align="end">
        <DropdownMenuLabel>Content locale</DropdownMenuLabel>
        <DropdownMenuRadioGroup value={current} onValueChange={(v) => setPreferredContentLocale(v === def ? null : v)}>
          <DropdownMenuRadioItem value={def}>
            {localeName(def)} <span className="ml-auto pl-3 text-11 text-secondary">default</span>
          </DropdownMenuRadioItem>
          {l10n.locales.map((l) => (
            <DropdownMenuRadioItem key={l} value={l}>
              {localeName(l)} <span className="ml-auto pl-3 text-11 text-secondary">{percent(completenessOf(l10n.data, l))} %</span>
            </DropdownMenuRadioItem>
          ))}
        </DropdownMenuRadioGroup>
      </DropdownMenuContent>
    </DropdownMenu>
  );
}

/** Shown in the explorer header while labels are in a locale other than the default; removing it goes back. */
export function ContentLocaleChip() {
  const l10n = useLocalization();
  const { effective } = useContentLocale();
  if (!l10n.enabled || !effective) return null;
  return (
    <Tooltip content={`Labels are shown in ${localeName(effective)}. Remove to show the default locale.`}>
      <button
        type="button"
        onClick={() => setPreferredContentLocale(null)}
        className="inline-flex h-5 shrink-0 items-center gap-1 rounded-control border border-accent bg-accent-subtle px-1 text-11"
        aria-label={`Content locale ${effective}: show the default locale`}
        data-testid="explorer-locale-chip"
      >
        {effective}
        <X className="size-3" aria-hidden />
      </button>
    </Tooltip>
  );
}
