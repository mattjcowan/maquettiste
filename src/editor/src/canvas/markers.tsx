// SVG markers for relation ends: UML diamonds and crow's feet, drawn in the edge's stroke color.
export function MarkerDefs() {
  const stroke = "var(--mq-border-strong)";
  return (
    <svg aria-hidden style={{ position: "absolute", width: 0, height: 0 }}>
      <defs>
        <marker
          id="mq-diamond-filled"
          viewBox="0 0 20 12"
          refX="1"
          refY="6"
          markerWidth="20"
          markerHeight="12"
          orient="auto-start-reverse"
          markerUnits="userSpaceOnUse"
        >
          <path d="M1 6 L10 1 L19 6 L10 11 Z" fill={stroke} stroke={stroke} />
        </marker>
        <marker
          id="mq-diamond-hollow"
          viewBox="0 0 20 12"
          refX="1"
          refY="6"
          markerWidth="20"
          markerHeight="12"
          orient="auto-start-reverse"
          markerUnits="userSpaceOnUse"
        >
          <path d="M1 6 L10 1 L19 6 L10 11 Z" fill="var(--mq-bg-canvas)" stroke={stroke} />
        </marker>
        {(
          [
            ["one", "M4 1 V11 M8 1 V11"],
            ["zero-or-one", "M5 1 V11 M13 6 m-3 0 a3 3 0 1 0 6 0 a3 3 0 1 0 -6 0"],
            ["many", "M1 6 L12 1 M1 6 L12 11 M1 6 H16"],
            ["one-or-many", "M1 6 L12 1 M1 6 L12 11 M1 6 H18 M15 1 V11"],
          ] as const
        ).map(([id, d]) => (
          <marker
            key={id}
            id={`mq-cf-${id}`}
            viewBox="0 0 20 12"
            refX="1"
            refY="6"
            markerWidth="20"
            markerHeight="12"
            orient="auto-start-reverse"
            markerUnits="userSpaceOnUse"
          >
            <path d={d} fill="none" stroke={stroke} strokeWidth="1.25" />
          </marker>
        ))}
        <marker id="mq-arrow" viewBox="0 0 10 10" refX="9" refY="5" markerWidth="8" markerHeight="8" orient="auto-start-reverse">
          <path d="M0 0 L10 5 L0 10 Z" fill={stroke} />
        </marker>
      </defs>
    </svg>
  );
}
