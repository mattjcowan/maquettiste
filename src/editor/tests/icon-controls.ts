// The owner's rule: every control whose visible content is an icon only shows a tooltip on hover (the title
// attribute) and exposes the same text to assistive technology. Shared by the unit guard (jsdom) and the mock
// Playwright spec (live DOM); it runs in the page, so it stays self-contained.

export interface IconControlScan {
  /** Controls without visible text that were checked. */
  checked: number;
  /** A short description of each one missing a title or an accessible name. */
  missing: string[];
}

export function scanIconControls(root: ParentNode = document): IconControlScan {
  // Hidden inside the control: an aria-hidden or screen-reader-only part (a modal dialog hides the page around it
  // with aria-hidden, so the walk stops at the control).
  const hidden = (el: Element | null, control: Element): boolean => {
    for (let e = el; e && e !== control.parentElement; e = e.parentElement) {
      if (e.getAttribute("aria-hidden") === "true") return true;
      if (e.classList.contains("sr-only")) return true;
    }
    return false;
  };
  const visibleText = (el: Element): string => {
    let text = "";
    const walker = (el.ownerDocument ?? document).createTreeWalker(el, NodeFilter.SHOW_TEXT);
    for (let n = walker.nextNode(); n; n = walker.nextNode()) if (!hidden(n.parentElement, el)) text += n.textContent ?? "";
    return text;
  };
  const accessibleName = (el: Element): string => {
    const label = el.getAttribute("aria-label")?.trim();
    if (label) return label;
    const by = el.getAttribute("aria-labelledby");
    if (by) {
      const doc = el.ownerDocument ?? document;
      const text = by
        .split(/\s+/)
        .map((id) => doc.getElementById(id)?.textContent ?? "")
        .join(" ")
        .trim();
      if (text) return text;
    }
    return el.getAttribute("title")?.trim() ?? "";
  };
  const missing: string[] = [];
  let checked = 0;
  for (const el of Array.from(root.querySelectorAll('button, [role="button"]'))) {
    // A button with another role (a checkbox, a switch, a tab) is that control, named by its own label.
    const role = el.getAttribute("role");
    if (role && role !== "button") continue;
    // Letters or digits are visible text; a lone symbol ("…", "+", "×") is an icon.
    if (/[\p{L}\p{N}]/u.test(visibleText(el))) continue;
    checked++;
    const title = el.getAttribute("title")?.trim() ?? "";
    if (title && accessibleName(el)) continue;
    const id = el.getAttribute("data-testid") ?? el.getAttribute("aria-label") ?? el.className.toString().slice(0, 60);
    missing.push(
      `${el.tagName.toLowerCase()}[${id}] title=${JSON.stringify(title)} parent=${el.parentElement?.getAttribute("data-testid") ?? el.parentElement?.className.toString().slice(0, 40)}`,
    );
  }
  return { checked, missing };
}
