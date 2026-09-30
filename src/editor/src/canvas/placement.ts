// Incremental placement: when a canvas gains cards that have no position yet (a new entity, a table just added, an
// entity that joined a domain), only those cards are placed and every placed card stays where the user put it. A new
// card goes into free space beside its nearest related card (one it shares a relation or foreign key with), else in
// rows along the bottom edge of what is already drawn. Kept free of React so it can be unit tested.

export interface PlacedBox {
  id: string;
  x: number;
  y: number;
  width: number;
  height: number;
}

export interface UnplacedBox {
  id: string;
  width: number;
  height: number;
}

export type Gap = { x: number; y: number };

/** The automatic layout's spacing (layout.ts ELK_OPTIONS): 96 between layers (left to right), 48 between cards. */
export const PLACEMENT_GAP: Gap = { x: 96, y: 48 };

/** Rows along the bottom edge are at least this many cards wide when the drawing is narrower. */
const MIN_ROW_CARDS = 4;

/** How many rings of candidate slots around a related card are tried before falling back to the bottom rows. */
const RINGS = 4;

function overlaps(a: PlacedBox, b: PlacedBox, gap: Gap): boolean {
  return a.x < b.x + b.width + gap.x && b.x < a.x + a.width + gap.x && a.y < b.y + b.height + gap.y && b.y < a.y + a.height + gap.y;
}

/**
 * Positions for the unplaced cards only (rounded to whole units), in the order given; the placed cards are never moved.
 * `edges` are the relations or foreign keys between cards (either direction). With no placed card the first card starts
 * at the origin; a canvas where every card is unplaced should use the full automatic layout instead.
 */
export function placeNodes(input: {
  placed: readonly PlacedBox[];
  unplaced: readonly UnplacedBox[];
  edges: readonly { source: string; target: string }[];
  gap?: Gap;
}): Record<string, { x: number; y: number }> {
  const gap = input.gap ?? PLACEMENT_GAP;
  const boxes: PlacedBox[] = input.placed.map((b) => ({ ...b }));
  const byId = new Map(boxes.map((b) => [b.id, b]));
  const neighbours = new Map<string, string[]>();
  for (const e of input.edges) {
    if (e.source === e.target) continue;
    (neighbours.get(e.source) ?? neighbours.set(e.source, []).get(e.source)!).push(e.target);
    (neighbours.get(e.target) ?? neighbours.set(e.target, []).get(e.target)!).push(e.source);
  }
  const free = (box: PlacedBox) => boxes.every((b) => !overlaps(box, b, gap));

  // The bottom rows start under the drawing as it was, left-aligned with it.
  const left = boxes.length ? Math.min(...boxes.map((b) => b.x)) : 0;
  const right = boxes.length ? Math.max(...boxes.map((b) => b.x + b.width)) : 0;
  const bottom = boxes.length ? Math.max(...boxes.map((b) => b.y + b.height)) + gap.y : 0;
  const widest = Math.max(0, ...input.unplaced.map((u) => u.width));
  const rowEnd = Math.max(right, left + MIN_ROW_CARDS * (widest + gap.x) - gap.x);
  const row = { x: left, y: bottom, height: 0 };

  const result: Record<string, { x: number; y: number }> = {};
  const put = (box: PlacedBox) => {
    box.x = Math.round(box.x);
    box.y = Math.round(box.y);
    boxes.push(box);
    byId.set(box.id, box);
    result[box.id] = { x: box.x, y: box.y };
  };

  for (const node of input.unplaced) {
    const related = (neighbours.get(node.id) ?? []).map((id) => byId.get(id)).filter((b): b is PlacedBox => !!b);
    const beside = related.length ? besideRelated(node, related, gap, free) : null;
    if (beside) {
      put(beside);
      continue;
    }
    // Along the bottom edge, in rows as wide as the drawing.
    for (;;) {
      if (row.x > left && row.x + node.width > rowEnd) {
        row.x = left;
        row.y += row.height + gap.y;
        row.height = 0;
      }
      const box = { id: node.id, x: row.x, y: row.y, width: node.width, height: node.height };
      row.x += node.width + gap.x;
      row.height = Math.max(row.height, node.height);
      if (free(box)) {
        put(box);
        break;
      }
    }
  }
  return result;
}

/** The first free slot around the related card nearest the middle of all related cards: right, below, left, above, then further out. */
function besideRelated(node: UnplacedBox, related: readonly PlacedBox[], gap: Gap, free: (box: PlacedBox) => boolean): PlacedBox | null {
  const cx = related.reduce((s, b) => s + b.x + b.width / 2, 0) / related.length;
  const cy = related.reduce((s, b) => s + b.y + b.height / 2, 0) / related.length;
  const anchor = [...related].sort(
    (a, b) => Math.hypot(a.x + a.width / 2 - cx, a.y + a.height / 2 - cy) - Math.hypot(b.x + b.width / 2 - cx, b.y + b.height / 2 - cy),
  )[0];
  for (let ring = 1; ring <= RINGS; ring++) {
    const dx = ring * gap.x + (ring - 1) * node.width;
    const dy = ring * gap.y + (ring - 1) * node.height;
    const slots = [
      { x: anchor.x + anchor.width + dx, y: anchor.y },
      { x: anchor.x, y: anchor.y + anchor.height + dy },
      { x: anchor.x - node.width - dx, y: anchor.y },
      { x: anchor.x, y: anchor.y - node.height - dy },
      { x: anchor.x + anchor.width + dx, y: anchor.y + anchor.height + dy },
      { x: anchor.x - node.width - dx, y: anchor.y + anchor.height + dy },
      { x: anchor.x + anchor.width + dx, y: anchor.y - node.height - dy },
      { x: anchor.x - node.width - dx, y: anchor.y - node.height - dy },
    ];
    for (const slot of slots) {
      const box = { id: node.id, ...slot, width: node.width, height: node.height };
      if (free(box)) return box;
    }
  }
  return null;
}

/** A canvas viewport rounded to three decimals, so the saved file does not change on sub-pixel moves. */
export function roundViewport(v: { x: number; y: number; zoom: number }): { x: number; y: number; zoom: number } {
  const r = (n: number) => Math.round(n * 1000) / 1000 || 0;
  return { x: r(v.x), y: r(v.y), zoom: r(v.zoom) };
}

/** A saved viewport the canvas can restore: one with x, y and zoom (the canvas writes all three), else null (fit instead). */
export function savedViewport(v: { x?: number; y?: number; zoom?: number } | null | undefined): { x: number; y: number; zoom: number } | null {
  if (!v || typeof v.x !== "number" || typeof v.y !== "number" || typeof v.zoom !== "number" || !(v.zoom > 0)) return null;
  return { x: v.x, y: v.y, zoom: v.zoom };
}

/** True when two viewports are the same once rounded. */
export function sameViewport(
  a: { x: number; y: number; zoom: number } | null | undefined,
  b: { x: number; y: number; zoom: number } | null | undefined,
): boolean {
  if (!a || !b) return a === b;
  const ra = roundViewport(a);
  const rb = roundViewport(b);
  return ra.x === rb.x && ra.y === rb.y && ra.zoom === rb.zoom;
}
