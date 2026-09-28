// SVG and PNG export of the canvas (html-to-image over React Flow's viewport, per the React Flow
// docs' image-download recipe).
import { getNodesBounds, getViewportForBounds, type Node } from "@xyflow/react";

/** `root` is the workspace holding the canvas: hidden workspaces stay mounted, so a document-wide query could pick another canvas. */
export async function exportCanvas(format: "svg" | "png", nodes: Node[], fileName: string, root: ParentNode | null = document): Promise<string> {
  const { toPng, toSvg } = await import("html-to-image");
  const bounds = getNodesBounds(nodes);
  const width = Math.max(320, Math.round(bounds.width + 80));
  const height = Math.max(240, Math.round(bounds.height + 80));
  const viewport = getViewportForBounds(bounds, width, height, 0.25, 2, 0.05);
  const element = (root ?? document).querySelector<HTMLElement>(".react-flow__viewport");
  if (!element) throw new Error("No canvas to export.");
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
