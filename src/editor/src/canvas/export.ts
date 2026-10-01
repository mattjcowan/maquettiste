// SVG and PNG export of the canvas (html-to-image over React Flow's viewport, per the React Flow
// docs' image-download recipe). The picture covers the cards and every edge label drawn beside them (a relation's
// attribute box can hang outside the cards).
import { getNodesBounds, getViewportForBounds, type Node, type Rect } from "@xyflow/react";

/** The smallest rectangle holding all the given ones (the first when it is alone). */
export function unionRect(first: Rect, ...rest: Rect[]): Rect {
  let left = first.x;
  let top = first.y;
  let right = first.x + first.width;
  let bottom = first.y + first.height;
  for (const r of rest) {
    left = Math.min(left, r.x);
    top = Math.min(top, r.y);
    right = Math.max(right, r.x + r.width);
    bottom = Math.max(bottom, r.y + r.height);
  }
  return { x: left, y: top, width: right - left, height: bottom - top };
}

/** The edge labels' boxes in flow coordinates, from where they are drawn and the viewport's pan and zoom. */
function labelRects(viewport: HTMLElement): Rect[] {
  const origin = viewport.parentElement?.getBoundingClientRect();
  const match = /translate\(([-\d.]+)px,\s*([-\d.]+)px\)\s*scale\(([\d.]+)\)/.exec(viewport.style.transform);
  if (!origin || !match) return [];
  const [x, y, zoom] = [Number(match[1]), Number(match[2]), Number(match[3]) || 1];
  return [...viewport.querySelectorAll<HTMLElement>(".react-flow__edgelabel-renderer > *")].map((el) => {
    const r = el.getBoundingClientRect();
    return { x: (r.left - origin.left - x) / zoom, y: (r.top - origin.top - y) / zoom, width: r.width / zoom, height: r.height / zoom };
  });
}

/** `root` is the workspace holding the canvas: hidden workspaces stay mounted, so a document-wide query could pick another canvas. */
export async function exportCanvas(format: "svg" | "png", nodes: Node[], fileName: string, root: ParentNode | null = document): Promise<string> {
  const { toPng, toSvg } = await import("html-to-image");
  const element = (root ?? document).querySelector<HTMLElement>(".react-flow__viewport");
  if (!element) throw new Error("No canvas to export.");
  const bounds = unionRect(getNodesBounds(nodes), ...labelRects(element));
  const width = Math.max(320, Math.round(bounds.width + 80));
  const height = Math.max(240, Math.round(bounds.height + 80));
  const viewport = getViewportForBounds(bounds, width, height, 0.25, 2, 0.05);
  const background = getComputedStyle(document.documentElement).getPropertyValue("--mq-bg-canvas").trim();
  const render = format === "svg" ? toSvg : toPng;
  const dataUrl = await render(element, {
    backgroundColor: background,
    width,
    height,
    style: { width: `${width}px`, height: `${height}px`, transform: `translate(${viewport.x}px, ${viewport.y}px) scale(${viewport.zoom})` },
  });
  const link = document.createElement("a");
  link.download = `${fileName}.${format}`;
  link.href = dataUrl;
  link.click();
  return dataUrl;
}
