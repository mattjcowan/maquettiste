// Placement for the editor's own portal popovers (position fixed, rendered on the document body): below the anchor
// when the panel fits there, above when it fits there instead, otherwise on the larger side; the height is clamped to
// the space on that side less an 8 px margin, and the panel is kept inside the viewport horizontally. Placed above,
// the panel is pinned by its bottom edge so a shorter list (a filtered one) stays against its anchor. The placement is
// measured again on a window resize, on a scroll of any ancestor (a capture-phase listener) and when the anchor, the
// panel or its content changes size. The menus and popovers built on the dialog library use its own collision
// handling with the same margin (POPOVER_MARGIN).
import { useCallback, useLayoutEffect, useRef, useState, type CSSProperties, type RefObject } from "react";

/** The gap kept between a popover and the viewport edge, in pixels. */
export const POPOVER_MARGIN = 8;

/** Marks the element that scrolls inside a popover, so its full content height counts as the panel's natural height. */
export const POPOVER_SCROLL_ATTR = "data-popover-scroll";

export interface AnchorRect {
  top: number;
  bottom: number;
  left: number;
  right: number;
}

export interface PlacementInput {
  anchor: AnchorRect;
  viewport: { width: number; height: number };
  /** The height the panel wants: its natural height, capped by the caller's own maximum. */
  preferredHeight: number;
  /** The caller's maximum height (a cap even where there is more room). */
  maxHeight?: number;
  /** The panel's width (narrowed to fit the viewport). */
  width: number;
  /** Space between the anchor and the panel. */
  gap?: number;
  margin?: number;
}

export interface Placement {
  side: "below" | "above";
  /** Set when below: the panel's top edge. */
  top?: number;
  /** Set when above: the distance from the viewport's bottom edge to the panel's bottom edge. */
  bottom?: number;
  left: number;
  width: number;
  maxHeight: number;
}

export function placePopover({ anchor, viewport, preferredHeight, maxHeight = Infinity, width, gap = 2, margin = POPOVER_MARGIN }: PlacementInput): Placement {
  const roomBelow = Math.max(0, viewport.height - anchor.bottom - gap - margin);
  const roomAbove = Math.max(0, anchor.top - gap - margin);
  const want = Math.min(preferredHeight, maxHeight);
  const side = want <= roomBelow ? "below" : want <= roomAbove ? "above" : roomAbove > roomBelow ? "above" : "below";
  const room = side === "below" ? roomBelow : roomAbove;
  const w = Math.max(0, Math.min(width, viewport.width - 2 * margin));
  const left = Math.max(margin, Math.min(anchor.left, viewport.width - margin - w));
  const placed: Placement = { side, left, width: w, maxHeight: Math.min(maxHeight, room) };
  if (side === "below") placed.top = anchor.bottom + gap;
  else placed.bottom = viewport.height - anchor.top + gap;
  return placed;
}

function same(a: Placement | null, b: Placement): boolean {
  return !!a && a.side === b.side && a.top === b.top && a.bottom === b.bottom && a.left === b.left && a.width === b.width && a.maxHeight === b.maxHeight;
}

/** The panel's height with nothing clamped: its own height plus what its scrolling element hides. */
function naturalHeight(panel: HTMLElement): number {
  const scroller = panel.querySelector<HTMLElement>(`[${POPOVER_SCROLL_ATTR}]`) ?? panel;
  return panel.offsetHeight - scroller.clientHeight + scroller.scrollHeight;
}

export interface PopoverPlacementOptions {
  open: boolean;
  /** The caller's maximum height in pixels. */
  maxHeight?: number;
  /** The panel is at least this wide (and at least as wide as the anchor). */
  minWidth?: number;
  gap?: number;
}

export interface PopoverPlacement<A extends HTMLElement, P extends HTMLElement> {
  anchorRef: RefObject<A | null>;
  panelRef: RefObject<P | null>;
  /** Position fixed; hidden until the first measurement so the panel never shows in the wrong place. */
  style: CSSProperties;
  placement: Placement | null;
}

export function usePopoverPlacement<A extends HTMLElement = HTMLElement, P extends HTMLElement = HTMLDivElement>({
  open,
  maxHeight,
  minWidth = 0,
  gap,
}: PopoverPlacementOptions): PopoverPlacement<A, P> {
  const anchorRef = useRef<A>(null);
  const panelRef = useRef<P>(null);
  const [placement, setPlacement] = useState<Placement | null>(null);

  const update = useCallback(() => {
    const anchor = anchorRef.current;
    if (!anchor) return;
    const rect = anchor.getBoundingClientRect();
    const panel = panelRef.current;
    const natural = panel ? naturalHeight(panel) : (maxHeight ?? Infinity);
    const next = placePopover({
      anchor: rect,
      viewport: { width: window.innerWidth, height: window.innerHeight },
      preferredHeight: natural,
      maxHeight,
      width: Math.max(rect.width, minWidth),
      gap,
    });
    setPlacement((prev) => (same(prev, next) ? prev : next));
  }, [maxHeight, minWidth, gap]);

  // Every render: the content (a filtered list) may have changed the natural height.
  useLayoutEffect(() => {
    if (open) update();
  });

  useLayoutEffect(() => {
    if (!open) {
      setPlacement(null);
      return;
    }
    window.addEventListener("resize", update);
    window.addEventListener("scroll", update, true);
    let observer: ResizeObserver | undefined;
    if (typeof ResizeObserver !== "undefined") {
      observer = new ResizeObserver(() => update());
      for (const el of [anchorRef.current, panelRef.current, panelRef.current?.querySelector(`[${POPOVER_SCROLL_ATTR}]`)?.firstElementChild])
        if (el) observer.observe(el);
    }
    return () => {
      window.removeEventListener("resize", update);
      window.removeEventListener("scroll", update, true);
      observer?.disconnect();
    };
  }, [open, update]);

  const style: CSSProperties = placement
    ? { position: "fixed", top: placement.top, bottom: placement.bottom, left: placement.left, width: placement.width, maxHeight: placement.maxHeight }
    : { position: "fixed", top: 0, left: 0, visibility: "hidden" };
  return { anchorRef, panelRef, style, placement };
}
