// The shared placement of the editor's portal popovers: below with room, above when only above fits, the larger side
// when neither fits, the height clamped to the room less the margin, and the panel kept inside the viewport.
import { describe, expect, it } from "vitest";
import { placePopover, POPOVER_MARGIN } from "@/ui/usePopoverPlacement";

const viewport = { width: 1440, height: 600 };
const anchorAt = (top: number, left = 100, height = 24) => ({ top, bottom: top + height, left, right: left + 200 });

describe("placePopover", () => {
  it("places below the anchor when the panel fits there", () => {
    const p = placePopover({ anchor: anchorAt(100), viewport, preferredHeight: 320, maxHeight: 320, width: 260 });
    expect(p).toMatchObject({ side: "below", top: 126, left: 100, width: 260, maxHeight: 320 });
    expect(p.bottom).toBeUndefined();
  });

  it("flips above when only the space above fits, pinned by its bottom edge", () => {
    const p = placePopover({ anchor: anchorAt(500), viewport, preferredHeight: 320, maxHeight: 320, width: 260 });
    expect(p).toMatchObject({ side: "above", bottom: 102, maxHeight: 320 });
    expect(p.top).toBeUndefined();
  });

  it("keeps a short panel below even when the anchor is low", () => {
    const p = placePopover({ anchor: anchorAt(500), viewport, preferredHeight: 60, maxHeight: 320, width: 260 });
    expect(p.side).toBe("below");
  });

  it("takes the larger side when neither fits, and clamps the height to it less the margin", () => {
    const tall = { width: 1440, height: 400 };
    const low = placePopover({ anchor: anchorAt(250), viewport: tall, preferredHeight: 1000, width: 260 });
    expect(low).toMatchObject({ side: "above", maxHeight: 250 - 2 - POPOVER_MARGIN });
    const high = placePopover({ anchor: anchorAt(120), viewport: tall, preferredHeight: 1000, width: 260 });
    expect(high).toMatchObject({ side: "below", top: 146, maxHeight: 400 - 144 - 2 - POPOVER_MARGIN });
  });

  it("never reports a negative height", () => {
    const p = placePopover({ anchor: { top: -50, bottom: 700, left: 0, right: 10 }, viewport, preferredHeight: 300, width: 100 });
    expect(p.maxHeight).toBe(0);
  });

  it("keeps the panel inside the viewport horizontally", () => {
    const right = placePopover({ anchor: { top: 10, bottom: 34, left: 1400, right: 1430 }, viewport, preferredHeight: 100, width: 260 });
    expect(right.left).toBe(1440 - POPOVER_MARGIN - 260);
    const left = placePopover({ anchor: { top: 10, bottom: 34, left: -30, right: 10 }, viewport, preferredHeight: 100, width: 260 });
    expect(left.left).toBe(POPOVER_MARGIN);
    const narrow = placePopover({
      anchor: { top: 10, bottom: 34, left: 0, right: 10 },
      viewport: { width: 200, height: 600 },
      preferredHeight: 100,
      width: 260,
    });
    expect(narrow).toMatchObject({ left: POPOVER_MARGIN, width: 200 - 2 * POPOVER_MARGIN });
  });
});
