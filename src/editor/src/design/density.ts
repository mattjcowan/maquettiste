// The editor's one density (generation-ui.md 6, E23: "we don't need comfortable rows, make everything very
// dense"). ROW_H is the row height every virtualizer and derived height reads; tokens.css sets the same value as
// --mq-row-h (tests/unit/density.test.ts asserts they agree). A coarse pointer (touch) gets 44 px rows through
// the CSS media query; rowHeight() reads it for the virtualizers.
export const ROW_H = 24;
export const TOUCH_ROW_H = 44;

/** The row height for this device: 44 px under a coarse pointer, else ROW_H. */
export function rowHeight(): number {
  if (typeof window === "undefined" || !window.matchMedia) return ROW_H;
  return window.matchMedia("(pointer: coarse)").matches ? TOUCH_ROW_H : ROW_H;
}
