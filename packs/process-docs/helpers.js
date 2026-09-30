// Helpers for the process-docs pack. They run in the Maquettiste sandbox: no CLR, no files, no clock. Each one is a pure function
// of the strings it receives, so pages do not depend on the order units render in.

// Text for one Markdown table cell or list line: Markdown punctuation escaped, and line breaks turned into spaces because a table
// row must stay on one line.
maquettiste.helper("md_cell", (text) =>
  String(text ?? "")
    .replace(/\r?\n/g, " ")
    .replace(/([\\`*_{}\[\]<>()!|~#])/g, "\\$1"));

// An inline code span that is safe inside a table cell: a pipe is escaped (it would end the cell even inside the span), and a
// text holding a backtick is wrapped in double backticks with spaces, as Markdown requires.
maquettiste.helper("md_code", (text) => {
  const t = String(text ?? "").replace(/\r?\n/g, " ").replace(/\|/g, "\\|");
  if (t === "") return "";
  return t.includes("`") ? "`` " + t + " ``" : "`" + t + "`";
});

// Text inside the state diagram (a state's label, an edge label, a note line). Characters the diagram text reads as syntax are
// written as its entity codes: "#" and ";" end or start statements, a double quote ends a quoted label, "<" and ">" would be
// read as markup, and a line break would end the statement.
const entities = { "#": "#35;", ";": "#59;", '"': "#quot;", "<": "#lt;", ">": "#gt;" };
maquettiste.helper("diagram_text", (text) =>
  String(text ?? "")
    .replace(/\r?\n/g, " ")
    .replace(/[#;"<>]/g, (c) => entities[c]));

// Words the diagram text reads as keywords whatever their case; a state named like one gets an id derived from its path.
const reserved = new Set([
  "accdescr", "acctitle", "as", "class", "classdef", "click", "direction", "end", "hide", "href", "left", "note", "of", "right",
  "scale", "state", "style",
]);

// Whether a state name can stand as its own id in the diagram.
maquettiste.helper("diagram_reserved", (name) => reserved.has(String(name ?? "").toLowerCase()));
