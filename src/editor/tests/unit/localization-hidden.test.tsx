// RT 3.10 "nothing appears while one locale is declared": the switcher, the Translations section and the Rows grid's
// locale columns render nothing without two locales, and appear with them.
import { render, screen, waitFor } from "@testing-library/react";
import { QueryClientProvider } from "@tanstack/react-query";
import { describe, expect, it } from "vitest";
import { ServicesProvider } from "@/app/context";
import { LocaleSwitcher, ContentLocaleChip } from "@/l10n/LocaleSwitcher";
import { TranslationsSection } from "@/l10n/TranslationsSection";
import { l10nKeys } from "@/l10n/queries";
import { localeColumns } from "@/workspaces/reference-data/rowsModel";
import { IDS, useMockApi } from "./harness";

function Harness({ api }: { api: ReturnType<typeof useMockApi> }) {
  return (
    <ServicesProvider services={api.services}>
      <QueryClientProvider client={api.services.queryClient}>
        <LocaleSwitcher />
        <ContentLocaleChip />
        <TranslationsSection id={IDS.invoice} kind="entity" />
      </QueryClientProvider>
    </ServicesProvider>
  );
}

describe("with one locale", () => {
  const api = useMockApi();
  it("renders no switcher, no chip, no Translations section and no locale columns", async () => {
    render(<Harness api={api} />);
    await waitFor(() => expect(api.services.queryClient.getQueryData(l10nKeys.status)).toBeTruthy());
    expect(screen.queryByTestId("locale-switcher")).toBeNull();
    expect(screen.queryByTestId("explorer-locale-chip")).toBeNull();
    expect(screen.queryByTestId("translations-section")).toBeNull();
    expect(localeColumns([])).toEqual([]);
  });
});

describe("with two or more locales", () => {
  const api = useMockApi({ scenarios: ["locales"] });
  it("shows the switcher and a collapsed Translations section, but no chip in the default locale", async () => {
    render(<Harness api={api} />);
    expect(await screen.findByTestId("locale-switcher")).toBeTruthy();
    const toggle = screen.getByRole("button", { name: /Translations/ });
    expect(toggle.getAttribute("aria-expanded")).toBe("false");
    expect(screen.queryByTestId("explorer-locale-chip")).toBeNull();
    expect(localeColumns(["fr", "fr-CA"]).map((c) => c.key)).toEqual(["@label:fr", "@label:fr-CA"]);
  });
});
