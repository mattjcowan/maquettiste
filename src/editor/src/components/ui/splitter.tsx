// A resizable split handle with pixel bounds, operable by pointer and keyboard (WAI-ARIA
// window splitter: role=separator, arrow keys, Home/End).
import { useRef, type KeyboardEvent, type PointerEvent } from "react";
import { cn } from "@/lib/cn";

export interface SplitterProps {
  orientation: "vertical" | "horizontal";
  value: number;
  min: number;
  max: number;
  onChange: (value: number) => void;
  /** +1 when dragging right/down grows the panel, -1 when it shrinks it. */
  direction: 1 | -1;
  label: string;
  controls?: string;
}

export function Splitter({ orientation, value, min, max, onChange, direction, label, controls }: SplitterProps) {
  const start = useRef<{ pos: number; value: number } | null>(null);
  const clamp = (v: number) => Math.max(min, Math.min(max, Math.round(v)));
  const onPointerDown = (e: PointerEvent<HTMLDivElement>) => {
    (e.target as HTMLElement).setPointerCapture(e.pointerId);
    start.current = { pos: orientation === "vertical" ? e.clientX : e.clientY, value };
  };
  const onPointerMove = (e: PointerEvent<HTMLDivElement>) => {
    if (!start.current) return;
    const pos = orientation === "vertical" ? e.clientX : e.clientY;
    onChange(clamp(start.current.value + direction * (pos - start.current.pos)));
  };
  const onPointerUp = () => {
    start.current = null;
  };
  const onKeyDown = (e: KeyboardEvent<HTMLDivElement>) => {
    const step = e.shiftKey ? 48 : 16;
    const grow =
      orientation === "vertical" ? (e.key === "ArrowRight" ? 1 : e.key === "ArrowLeft" ? -1 : 0) : e.key === "ArrowDown" ? 1 : e.key === "ArrowUp" ? -1 : 0;
    if (grow) {
      e.preventDefault();
      onChange(clamp(value + grow * direction * step));
    } else if (e.key === "Home") {
      e.preventDefault();
      onChange(min);
    } else if (e.key === "End") {
      e.preventDefault();
      onChange(max);
    }
  };
  return (
    <div
      role="separator"
      tabIndex={0}
      aria-label={label}
      aria-orientation={orientation}
      aria-valuemin={min}
      aria-valuemax={max}
      aria-valuenow={value}
      aria-controls={controls}
      onPointerDown={onPointerDown}
      onPointerMove={onPointerMove}
      onPointerUp={onPointerUp}
      onKeyDown={onKeyDown}
      className={cn(
        "relative z-10 shrink-0 bg-default hover:bg-accent focus-visible:bg-accent focus-visible:outline-2 focus-visible:outline-offset-0 focus-visible:outline-accent",
        orientation === "vertical"
          ? "w-px cursor-col-resize after:absolute after:inset-y-0 after:-left-1 after:-right-1"
          : "h-px cursor-row-resize after:absolute after:inset-x-0 after:-top-1 after:-bottom-1",
      )}
    />
  );
}
