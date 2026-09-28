// Helpers for the sql-ddl pack. They run in the Maquettiste sandbox: no CLR, no files, no clock. Every helper is a pure function
// of its arguments (D12): nothing is kept between calls, so output does not depend on the order units render in.

// One unit per database: the schema, migration and seed units use "for": "select databases".
maquettiste.selector("databases", (model) => model.databases);

// The foreign-key graph arrives as plain text built by dependency_spec in _objects.scriban (walking thousands of model objects
// from JavaScript is much slower than from the template): one entry per table in input order, separated by ";", each a
// comma-separated list with one item per foreign key: the referenced table's index, or "x" when it is outside the list or the
// table itself.
function parseSpec(spec) {
  if (spec === "") return [];
  return spec.split(";").map((entry) => (entry === "" ? [] : entry.split(",").map((v) => (v === "x" ? -1 : Number(v)))));
}

// Table indexes in creation order: each table after the tables its foreign keys reference, which is the order CREATE TABLE
// statements must run in on PostgreSQL and SQL Server. The walk is a depth-first search in input order (the resolver's schema,
// name order), so a table comes right after its dependencies and the result stays stable and close to the input order. In a
// foreign-key cycle the table reached first is emitted before the one closing the cycle (see ddl_cycle_breaks). Iterative, so
// long reference chains do not hit the sandbox's recursion limit; linear in tables plus foreign keys.
function order(deps) {
  const state = new Array(deps.length).fill(0); // 0 new, 1 on the stack, 2 emitted
  const result = [];
  for (let root = 0; root < deps.length; root++) {
    if (state[root] !== 0) continue;
    const stack = [[root, 0]];
    state[root] = 1;
    while (stack.length > 0) {
      const frame = stack[stack.length - 1];
      const node = frame[0];
      if (frame[1] < deps[node].length) {
        const dep = deps[node][frame[1]];
        frame[1] += 1;
        if (dep >= 0 && state[dep] === 0) {
          state[dep] = 1;
          stack.push([dep, 0]);
        }
      } else {
        stack.pop();
        state[node] = 2;
        result.push(node);
      }
    }
  }
  return result;
}

maquettiste.helper("ddl_order", (spec) => order(parseSpec(spec)));

// Foreign keys that reference a table created later in ddl_order (a foreign-key cycle), as "<table index>:<foreign key index>".
// Empty for every acyclic model.
maquettiste.helper("ddl_cycle_breaks", (spec) => {
  const deps = parseSpec(spec);
  const position = new Array(deps.length);
  order(deps).forEach((node, i) => { position[node] = i; });
  const result = [];
  deps.forEach((refs, t) => {
    refs.forEach((r, f) => {
      if (r >= 0 && position[r] > position[t]) result.push(t + ":" + f);
    });
  });
  return result;
});
