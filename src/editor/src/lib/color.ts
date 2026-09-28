// Colors read back from CSS custom properties for APIs that want #rrggbb[aa] (Monaco themes).
// The production CSS minifier shortens #ffffff to #fff, which Monaco's token theme rejects.

/** #rgb, #rgba, #rrggbb, #rrggbbaa, rgb() and rgba() as #rrggbb or #rrggbbaa; anything else unchanged. */
export function toLongHex(value: string): string {
  const v = value.trim();
  const short = /^#([0-9a-f]{3,4})$/i.exec(v);
  if (short) return `#${[...short[1]].map((c) => c + c).join("")}`.toLowerCase();
  if (/^#([0-9a-f]{6}|[0-9a-f]{8})$/i.test(v)) return v.toLowerCase();
  const rgb = /^rgba?\(\s*(\d+)[\s,]+(\d+)[\s,]+(\d+)(?:\s*[,/]\s*([\d.]+%?))?\s*\)$/i.exec(v);
  if (rgb) {
    const hex = (n: number) =>
      Math.max(0, Math.min(255, Math.round(n)))
        .toString(16)
        .padStart(2, "0");
    const alpha = rgb[4] === undefined ? "" : hex((rgb[4].endsWith("%") ? parseFloat(rgb[4]) / 100 : parseFloat(rgb[4])) * 255);
    return `#${hex(+rgb[1])}${hex(+rgb[2])}${hex(+rgb[3])}${alpha === "ff" ? "" : alpha}`;
  }
  return v;
}
