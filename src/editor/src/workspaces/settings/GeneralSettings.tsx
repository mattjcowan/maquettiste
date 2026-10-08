// Settings › General: the project name, the project's own properties (key to text, read by every pack's templates as
// project.properties) and branding (icon, primary color per theme), saved through
// PUT /api/project/settings; the icon is stored first through POST /api/project/branding/icon. The top bar,
// the tab icon and the accent tokens preview the draft live (src/design/branding.ts).
import { useEffect, useMemo, useRef, useState } from "react";
import { useQueryClient } from "@tanstack/react-query";
import { keys, useProject, useSettings } from "@/api/queries";
import * as endpoints from "@/api/endpoints";
import type { SettingsJson } from "@/api/types";
import { useServices } from "@/app/context";
import { projectIconUrl } from "@/app/branding";
import { Plus, X } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Field, Input } from "@/components/ui/input";
import { SectionTitle, Spinner } from "@/components/ui/misc";
import {
  MAX_ICON_BYTES,
  builtInAccent,
  contrastWarning,
  contrastWarningText,
  normalizeHexColor,
  setBrandingPreview,
  themeSurfaces,
  type ThemeName,
} from "@/design/branding";
import { colorProblem, fileToBase64, generalOf, iconContentType, propertyProblems, sameGeneral, withGeneral, type GeneralDraft } from "./general";

const THEMES: { theme: ThemeName; label: string }[] = [
  { theme: "light", label: "Light theme" },
  { theme: "dark", label: "Dark theme" },
];

export function GeneralSettings() {
  const settings = useSettings();
  const project = useProject();
  const qc = useQueryClient();
  const { store } = useServices();
  const base = useMemo(() => (settings.data ? generalOf(settings.data.json as SettingsJson) : null), [settings.data]);
  const [draft, setDraft] = useState<GeneralDraft | null>(null);
  const [localIcon, setLocalIcon] = useState<string | null>(null);
  const [notes, setNotes] = useState<string[]>([]);
  const [errors, setErrors] = useState<string[]>([]);
  const [busy, setBusy] = useState(false);
  const fileInput = useRef<HTMLInputElement>(null);
  const value = draft ?? base;
  const dirty = !!draft && !!base && !sameGeneral(draft, base);

  const surfaces = useMemo(() => ({ light: themeSurfaces(document, "light"), dark: themeSurfaces(document, "dark") }), []);
  const defaults = useMemo(() => ({ light: builtInAccent(document, "light"), dark: builtInAccent(document, "dark") }), []);

  // The live preview: the top bar, the tab icon and the tokens follow the draft until it is saved or left.
  useEffect(() => {
    if (!dirty || !draft) {
      setBrandingPreview(null);
      return;
    }
    const savedIcon = projectIconUrl(base?.icon, project.data?.iconHash);
    setBrandingPreview({
      name: draft.name.trim() || undefined,
      colors: { light: normalizeHexColor(draft.light), dark: normalizeHexColor(draft.dark) },
      iconUrl: draft.icon === base?.icon && !localIcon ? savedIcon : draft.icon ? localIcon : null,
    });
  }, [dirty, draft, base, localIcon, project.data?.iconHash]);
  useEffect(() => () => setBrandingPreview(null), []);

  if (settings.isPending || !value || !base) return <Spinner label="Loading settings" />;

  const edit = (patch: Partial<GeneralDraft>) => setDraft({ ...value, ...patch });
  const problems = propertyProblems(value.properties);
  const editProperty = (i: number, patch: Partial<GeneralDraft["properties"][number]>) =>
    edit({ properties: value.properties.map((p, k) => (k === i ? { ...p, ...patch } : p)) });

  const upload = async (file: File) => {
    setNotes([]);
    setErrors([]);
    const contentType = iconContentType(file);
    if (!contentType) return setErrors(["The icon must be an SVG or a PNG file."]);
    if (file.size > MAX_ICON_BYTES) return setErrors([`The icon is ${Math.ceil(file.size / 1024)} KB; the limit is 512 KB.`]);
    setBusy(true);
    try {
      const data = await fileToBase64(file);
      const saved = await endpoints.uploadBrandingIcon({ contentType, data });
      setLocalIcon(`data:${contentType};base64,${data}`);
      edit({ icon: saved.icon });
      if (saved.removed.length) setNotes([`Removed from the SVG before it was stored: ${saved.removed.join(", ")}.`]);
    } catch (e) {
      setErrors([e instanceof Error ? e.message : String(e)]);
    } finally {
      setBusy(false);
      if (fileInput.current) fileInput.current.value = "";
    }
  };

  const save = async () => {
    if (!draft || !settings.data) return;
    setBusy(true);
    setErrors([]);
    try {
      const result = await endpoints.saveSettings(withGeneral(settings.data.json as SettingsJson, draft), settings.data.hash);
      if (result.outcome === "saved" && result.current) {
        qc.setQueryData(keys.settings, result.current);
        await qc.invalidateQueries({ queryKey: keys.project });
        setDraft(null);
        setLocalIcon(null);
        setNotes([]);
        store.getState().notify("General settings saved.");
      } else if (result.outcome === "conflict") {
        await qc.invalidateQueries({ queryKey: keys.settings });
        store.getState().notify("maquettiste.json changed on disk. Your changes are kept; review them and save again.", "error");
      } else setErrors(result.diagnostics.map((d) => `${d.rule} ${d.jsonPointer ?? ""} ${d.message}`));
    } finally {
      setBusy(false);
    }
  };

  const iconPreview = value.icon ? (localIcon ?? projectIconUrl(base.icon === value.icon ? base.icon : null, project.data?.iconHash)) : null;

  return (
    <section className="flex max-w-3xl flex-col gap-2" aria-label="General">
      <div className="flex items-center gap-2">
        <p className="text-12 text-secondary">
          The project name, its properties, icon and primary colors, saved in maquettiste.json. The icon and colors change no generated output.
        </p>
        <span className="ml-auto flex items-center gap-2">
          {dirty ? <span className="text-12 text-secondary">Unsaved changes</span> : null}
          <Button
            onClick={() => {
              setDraft(null);
              setLocalIcon(null);
              setErrors([]);
              setNotes([]);
            }}
            disabled={!dirty || busy}
          >
            Discard
          </Button>
          <Button
            variant="primary"
            onClick={() => void save()}
            disabled={!dirty || busy || !!colorProblem(value.light) || !!colorProblem(value.dark) || problems.size > 0}
            data-testid="save-general"
          >
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

      <Field label="Project name" htmlFor="general-name" hint="Shown in the top bar; maquettiste init --name writes the same field.">
        <Input id="general-name" className="w-80" value={value.name} placeholder={project.data?.name} onChange={(e) => edit({ name: e.target.value })} />
      </Field>

      <div>
        <SectionTitle
          actions={
            <Button
              size="sm"
              variant="ghost"
              onClick={() => edit({ properties: [...value.properties, { key: "", value: "" }] })}
              data-testid="general-add-property"
            >
              <Plus /> Add property
            </Button>
          }
        >
          Properties
        </SectionTitle>
        <p className="text-12 text-secondary">
          Values every pack&apos;s templates read as <code className="font-mono">project.properties.&lt;key&gt;</code>, such as a base namespace. A pack&apos;s
          own parameters stay separate (<code className="font-mono">pack.params</code>).
        </p>
        {value.properties.length ? (
          <table className="mt-1 w-full max-w-2xl text-13" aria-label="Project properties" data-testid="general-properties">
            <thead className="text-left text-11 text-secondary">
              <tr>
                <th className="w-56 font-medium">Key</th>
                <th className="font-medium">Value</th>
                <th className="w-7" />
              </tr>
            </thead>
            <tbody>
              {value.properties.map((p, i) => (
                <tr key={i} className="align-top">
                  <td className="py-0.5 pr-1">
                    <Input
                      aria-label={`Key of property ${i + 1}`}
                      className="h-7 font-mono"
                      value={p.key}
                      placeholder="baseNamespace"
                      aria-invalid={problems.has(i)}
                      onChange={(e) => editProperty(i, { key: e.target.value })}
                    />
                    {problems.has(i) ? <p className="text-11 text-danger">{problems.get(i)}</p> : null}
                  </td>
                  <td className="py-0.5 pr-1">
                    <Input
                      aria-label={`Value of property ${p.key || i + 1}`}
                      className="h-7"
                      value={p.value}
                      placeholder="Acme.Billing"
                      onChange={(e) => editProperty(i, { value: e.target.value })}
                    />
                  </td>
                  <td className="py-0.5">
                    <Button
                      size="icon-sm"
                      variant="ghost"
                      label={`Remove the property ${p.key || i + 1}`}
                      onClick={() => edit({ properties: value.properties.filter((_, k) => k !== i) })}
                    >
                      <X />
                    </Button>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        ) : (
          <p className="mt-1 text-12 text-secondary" data-testid="general-no-properties">
            None yet.
          </p>
        )}
      </div>

      <div>
        <SectionTitle>Icon</SectionTitle>
        <div className="flex items-center gap-2">
          {iconPreview ? (
            <img src={iconPreview} alt="Project icon" className="size-6 rounded-control object-contain" data-testid="general-icon-preview" />
          ) : (
            <span aria-label="Default mark" className="grid size-6 place-items-center rounded-control bg-accent text-12 font-semibold text-accent-foreground">
              M
            </span>
          )}
          <input
            ref={fileInput}
            id="general-icon"
            type="file"
            accept=".svg,.png,image/svg+xml,image/png"
            className="sr-only"
            aria-label="Upload an icon"
            onChange={(e) => {
              const file = e.target.files?.[0];
              if (file) void upload(file);
            }}
          />
          <Button onClick={() => fileInput.current?.click()} disabled={busy}>
            {value.icon ? "Change icon" : "Upload icon"}
          </Button>
          {value.icon ? (
            <Button
              variant="ghost"
              onClick={() => {
                edit({ icon: null });
                setLocalIcon(null);
              }}
              disabled={busy}
            >
              Use the default mark
            </Button>
          ) : null}
          <span className="text-12 text-secondary">{value.icon ?? "SVG or PNG, at most 512 KB; stored under .maquettiste/branding/."}</span>
        </div>
        {notes.map((n, i) => (
          <p key={i} role="status" className="text-12 text-secondary">
            {n}
          </p>
        ))}
      </div>

      <div>
        <SectionTitle>Primary color</SectionTitle>
        <div className="grid grid-cols-2 gap-2">
          {THEMES.map(({ theme, label }) => {
            const color = value[theme];
            const problem = colorProblem(color);
            const warning = color && !problem ? contrastWarning(color, surfaces[theme]) : null;
            const id = `general-color-${theme}`;
            return (
              <Field key={theme} label={label} htmlFor={id}>
                <div className="flex items-center gap-2">
                  <input
                    type="color"
                    aria-label={`${label} color picker`}
                    className="h-6 w-8 cursor-pointer rounded-control border border-input bg-surface p-0"
                    value={normalizeHexColor(color) ?? defaults[theme] ?? undefined}
                    onChange={(e) => edit({ [theme]: e.target.value })}
                  />
                  <Input
                    id={id}
                    className="w-28 font-mono"
                    value={color ?? ""}
                    placeholder={defaults[theme] ? `built-in ${defaults[theme]}` : "built-in"}
                    onChange={(e) => edit({ [theme]: e.target.value.trim() || null })}
                    aria-invalid={!!problem}
                  />
                  {color ? (
                    <Button variant="ghost" onClick={() => edit({ [theme]: null })}>
                      Reset
                    </Button>
                  ) : null}
                </div>
                {problem ? <p className="text-11 text-danger">{problem}</p> : null}
                {warning ? (
                  <p className="text-11 text-warning" role="status" data-testid={`contrast-${theme}`}>
                    {contrastWarningText(warning, theme)}
                  </p>
                ) : null}
              </Field>
            );
          })}
        </div>
      </div>
    </section>
  );
}
