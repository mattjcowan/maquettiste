// explorer/tree.ts (explorer-redesign.md sections 1.1 to 1.7, 1.9, 1.11, 4.3 and the section 6 acceptance tests for
// the tree) over the billing fixture, the medium seed and, when it has been generated, the large mock.
import fs from "node:fs";
import path from "node:path";
import { describe, expect, it } from "vitest";
import type { ElementSummary, ExplorerFolder } from "@/api/types";
import { MockBackend } from "@/mocks/backend";
import { decodeSeed } from "@/mocks/model/largeSeed";
import {
  EXPLORERS,
  breadcrumb,
  buildForest,
  childKeys,
  collapseAt,
  documentChildren,
  expandAt,
  forestOf,
  nodeOf,
  pathOf,
  relatedCounts,
  relatedKeys,
  revealPath,
  usedByOf,
  usesOf,
  visibleRows,
  vocabularyChain,
  type Forest,
  type TablesInput,
  type TreeInput,
  type TreeNode,
} from "@/explorer/tree";
import { IDS } from "./harness";

const strictTiming = process.env.MQ_SCALE_STRICT === "1";

const SETTINGS_KINDS = new Set(["tag-vocabulary", "category-tree", "stereotype"]);

function billing() {
  const backend = new MockBackend();
  const rows = backend.model.index();
  const tables = new Map<string, TablesInput>();
  for (const db of rows.filter((r) => r.kind === "database")) tables.set(db.id, backend.generation.databaseTables(db.id)!);
  return { backend, rows, tables };
}

const node = (forest: Forest, key: string) => nodeOf(forest, key)!;
const labels = (forest: Forest, key: string) => childKeys(forest, key).map((k) => node(forest, k).label);
const idOf = (rows: readonly ElementSummary[], name: string) => rows.find((r) => r.name === name)!.id;

/** The node reached from an explorer's root by child labels. */
function find(forest: Forest, explorer: (typeof EXPLORERS)[number], ...path: string[]): TreeNode {
  let key = forest.roots[explorer];
  for (const label of path) {
    const next = childKeys(forest, key).find((k) => node(forest, k).label === label);
    if (!next) throw new Error(`No "${label}" under ${breadcrumb(forest, key)}: ${labels(forest, key).join(", ")}`);
    key = next;
  }
  return node(forest, key);
}

/** Every index row has a place, reachable from its explorer's root through children (reference types and settings kinds aside). */
function expectReachable(forest: Forest, rows: readonly ElementSummary[]) {
  const reachable = new Set<string>();
  for (const explorer of EXPLORERS) {
    const stack = [forest.roots[explorer]];
    while (stack.length) {
      const key = stack.pop()!;
      reachable.add(key);
      if (node(forest, key).home) for (const c of childKeys(forest, key)) stack.push(c);
    }
  }
  const missing = rows.filter((r) => !SETTINGS_KINDS.has(r.kind) && !reachable.has(forest.place.get(r.id) ?? "-")).map((r) => `${r.kind} ${r.name}`);
  expect(missing).toEqual([]);
  for (const id of forest.unplaced) expect(SETTINGS_KINDS.has(forest.byId.get(id)!.kind)).toBe(true);
}

/** Rule 2 of section 1.1: every element row sits in a folder named after its kind, a single-kind project folder, or Other. */
function expectKindFolders(forest: Forest, folders: readonly ExplorerFolder[] = []) {
  const projectLabels = new Set(folders.map((f) => f.label));
  for (const key of [...forest.pending.keys()]) childKeys(forest, key);
  const problems: string[] = [];
  for (const n of forest.nodes.values()) {
    if (n.type !== "element" || !n.home || n.explorer !== "domain-model") continue;
    const holder = node(forest, forest.parent.get(n.key)!);
    const ok = holder.type === "folder" && (holder.kind === n.kind || holder.label === "Other" || (projectLabels.has(holder.label) && holder.kind === n.kind));
    if (!ok) problems.push(`${n.kind} ${n.label} in ${holder.label}`);
  }
  expect(problems).toEqual([]);
}

describe("tree over the billing fixture", () => {
  const { backend, rows, tables } = billing();
  const forest = buildForest({ rows, tables });

  it("nests Catalog under Billing, a top-level domain, with rolled-up counts", () => {
    // The mock's actors (mocks/model/processSeed.ts) are in People and access.
    expect(labels(forest, forest.roots["domain-model"])).toEqual(["Billing", "People and access"]);
    const billingNode = find(forest, "domain-model", "Billing");
    expect(billingNode.type).toBe("domain");
    expect(billingNode.tooltip).toBe("Domain: Billing");
    expect(billingNode.secondary).toBe("1 sub-domain · 5 entities");
    expect(labels(forest, billingNode.key)).toEqual(["Catalog", "Entities", "Relationships", "Enums", "Value objects", "Custom types", "Processes"]);
    expect(find(forest, "domain-model", "Billing", "Catalog").secondary).toBe("1 entity");
    expect(find(forest, "domain-model", "Billing", "Entities").count).toBe(4);
    expect(labels(forest, find(forest, "domain-model", "Billing", "Entities").key)).toEqual(["Customer", "Invoice", "InvoiceLine", "Payment"]);
    expect(forest.headers["domain-model"]).toBe("2 domains · 5 entities");
  });

  it("builds one tree per explorer, with no Project, Settings or Reference data row", () => {
    for (const explorer of EXPLORERS) expect(node(forest, forest.roots[explorer]).explorer).toBe(explorer);
    const top = labels(forest, forest.roots["domain-model"]);
    for (const word of ["Project", "Settings", "Reference data", "Not in a domain"]) expect(top).not.toContain(word);
    expect(labels(forest, forest.roots["reference-data"])).toEqual([]);
  });

  it("reaches every index row, and places no vocabulary or stereotype", () => {
    expectReachable(forest, rows);
    expectKindFolders(forest);
    expect(forest.unplaced.length).toBe(6); // five vocabularies and stereotypes of the fixture, and the mock's persona
  });

  it("reaches physical rows without the E5 database member, in Not in a database", () => {
    const old = rows.map((r) => {
      const { database: _database, ...rest } = r;
      return rest as ElementSummary;
    });
    const f = buildForest({ rows: old });
    expectReachable(f, old);
    expect(labels(f, find(f, "databases", "Not in a database").key)).toEqual([
      "Tables",
      "Views",
      "Sequences",
      "Routines",
      "Types",
      "Objects",
      "Queries",
      "Customised mappings",
    ]);
  });

  it("shapes Databases: main › billing (by name) › Tables, Views, Sequences, Routines, Types, Objects, Queries, then Customised mappings", () => {
    const main = find(forest, "databases", "main");
    expect(main.pending).toBeUndefined();
    expect(labels(forest, main.key)).toEqual(["billing", "Customised mappings"]);
    expect(labels(forest, find(forest, "databases", "main", "billing").key)).toEqual([
      "Tables",
      "Views",
      "Sequences",
      "Routines",
      "Types",
      "Objects",
      "Queries",
    ]);
    expect(labels(forest, find(forest, "databases", "main", "billing", "Queries").key)).toEqual([
      "FindCustomersWithIssuedInvoices",
      "InvoicesByCustomer",
      "RevenueByMonth",
    ]);
    expect(labels(forest, find(forest, "databases", "main", "billing", "Routines").key)).toEqual(["close_period", "invoice_total"]);
    expect(labels(forest, find(forest, "databases", "main", "billing", "Types").key)).toEqual(["email_address", "invoice_state"]);
    expect(labels(forest, find(forest, "databases", "main", "billing", "Objects").key)).toEqual(["reporting_read"]);
    expect(find(forest, "databases", "main", "billing").secondary).toBe(
      "6 tables · 1 view · 1 sequence · 2 routines · 2 database types · 1 SQL object · 3 queries",
    );
    const tableNames = labels(forest, find(forest, "databases", "main", "billing", "Tables").key);
    expect(tableNames).toContain("invoices");
    expect(main.secondary).toBe(
      "6 tables · 1 view · 1 sequence · 2 routines · 2 database types · 1 SQL object · 3 queries · 2 customised mappings · 5 entities mapped",
    );
    expect(forest.headers.databases).toBe("1 database · 6 tables");
    // The table overlay file is the invoices row; the junction carries its marker.
    expect(forest.place.get(rows.find((r) => r.kind === "table")!.id)).toBe(find(forest, "databases", "main", "billing", "Tables", "invoices").key);
    expect(find(forest, "databases", "main", "billing", "Tables", "payment_invoice").markers).toEqual(["junction"]);
    expect(labels(forest, find(forest, "databases", "main", "Customised mappings").key)).toEqual(["Invoice → invoices", "settles in main"]);
  });

  it("shows the index's rows and a pending database before the table summaries arrive", () => {
    const f = buildForest({ rows });
    const main = find(f, "databases", "main");
    expect(main.pending).toBe(true);
    expect(labels(f, main.key)).toEqual(["Default schema", "Customised mappings"]);
    expectReachable(f, rows);
    const named = buildForest({ rows, databases: new Map([[main.id!, { defaultSchema: "billing" }]]) });
    expect(labels(named, find(named, "databases", "main").key)).toEqual(["billing", "Customised mappings"]);
  });

  it("shows a Default schema node for tables with a null schema", () => {
    const dbId = idOf(rows, "main");
    const noSchema = new Map([[dbId, { tables: tables.get(dbId)!.tables.map((t) => ({ ...t, schema: null })) }]]);
    const f = buildForest({ rows, tables: noSchema });
    expect(labels(f, find(f, "databases", "main").key)).toEqual(["Default schema", "Customised mappings"]);
  });

  it("answers an entity's and a relation's children from the index", () => {
    const invoice = node(forest, IDS.invoice);
    expect(invoice.load).toBe("document");
    expect(labels(forest, invoice.key)).toEqual(["Relationships", "Mappings"]);
    const relations = find(forest, "domain-model", "Billing", "Entities", "Invoice", "Relationships");
    expect(labels(forest, relations.key)).toEqual(["contains", "places", "settles"]);
    const contains = node(forest, childKeys(forest, relations.key)[0]);
    expect(contains.home).toBe(false);
    expect(contains.target).toBe(idOf(rows, "contains"));
    expect(contains.secondary).toBe("Invoice → InvoiceLine");
    const mapping = node(forest, childKeys(forest, find(forest, "domain-model", "Billing", "Entities", "Invoice", "Mappings").key)[0]);
    expect(mapping.label).toBe("main → invoices");
    expect(mapping.markers).toEqual(["customised"]);
    const ends = find(forest, "domain-model", "Billing", "Relationships", "contains", "Ends");
    expect(labels(forest, ends.key)).toEqual(["Invoice", "InvoiceLine"]);
    // A relation from another domain names its domain path.
    const product = find(forest, "domain-model", "Billing", "Catalog", "Entities", "Product", "Relationships");
    expect(node(forest, childKeys(forest, product.key)[0]).secondary).toBe("InvoiceLine → Product · Billing");
  });

  it("adds Attributes first once the entity's document is loaded", () => {
    const f = buildForest({ rows, tables });
    const children = documentChildren(f, IDS.invoice, backend.model.get(IDS.invoice)!);
    expect(children.map((k) => node(f, k).label)).toEqual(["Attributes", "Relationships", "Mappings"]);
    const attributes = node(f, children[0]);
    expect(attributes.count).toBeGreaterThan(0);
    expect(node(f, attributes.children![0]).type).toBe("item");
  });

  it("lists diagrams outside the domains, in a group named after the home domain", () => {
    const group = find(forest, "diagrams", "Billing");
    expect(group.icon).toBe("domain");
    expect(group.tooltip).toBe("Diagrams whose home is Billing");
    expect(labels(forest, group.key)).toEqual(["Billing overview", "Purchase approval"]);
    expect(find(forest, "diagrams", "Billing", "Billing overview").secondary).toBe("8 members");
    // A process diagram (phase-3-design.md 6.5) says whose statechart it is.
    expect(find(forest, "diagrams", "Billing", "Purchase approval").secondary).toBe("statechart of Purchase approval");
  });

  it("rolls up error badges", () => {
    const f = buildForest({ rows, tables, errors: new Map([[IDS.invoice, 2]]) });
    expect(node(f, IDS.invoice).errors).toBe(2);
    expect(find(f, "domain-model", "Billing", "Entities").errors).toBe(2);
    expect(find(f, "domain-model", "Billing").errors).toBe(2);
    expect(node(f, f.roots["domain-model"]).errors).toBe(2);
    expect(find(f, "domain-model", "Billing", "Catalog").errors).toBe(0);
  });

  it("computes the related sets of an entity, a relation and a table, and the n related counts", () => {
    const invoice = relatedKeys(forest, IDS.invoice);
    for (const name of ["contains", "places", "settles", "InvoiceLine", "Customer", "Payment"]) expect(invoice.has(idOf(rows, name))).toBe(true);
    const invoicesTable = find(forest, "databases", "main", "billing", "Tables", "invoices").key;
    expect(invoice.has(invoicesTable)).toBe(true);
    const settles = relatedKeys(forest, idOf(rows, "settles"));
    expect(settles).toEqual(new Set([IDS.payment, IDS.invoice, find(forest, "databases", "main", "billing", "Tables", "payment_invoice").key]));
    expect(relatedKeys(forest, invoicesTable)).toEqual(new Set([IDS.invoice]));
    const counts = relatedCounts(forest, invoice);
    expect(counts.get(find(forest, "domain-model", "Billing", "Entities").key)).toBe(3);
    expect(counts.get(find(forest, "domain-model", "Billing", "Relationships").key)).toBe(3);
    // A navigation row relates like the row it leads to.
    const nav = childKeys(forest, find(forest, "domain-model", "Billing", "Entities", "Invoice", "Relationships").key)[0];
    expect(relatedKeys(forest, nav).has(IDS.invoice)).toBe(true);
  });

  it("maps uses and used by from the index", () => {
    expect(usesOf(forest, idOf(rows, "contains"))).toEqual([IDS.invoice, idOf(rows, "InvoiceLine")]);
    expect(usedByOf(forest, IDS.invoice)).toEqual(expect.arrayContaining([idOf(rows, "contains"), idOf(rows, "Invoice in main")]));
  });

  it("reveals, splices and walks the visible rows", () => {
    expect(revealPath(forest, IDS.invoice)).toEqual(pathOf(forest, IDS.invoice).slice(0, -1));
    expect(breadcrumb(forest, IDS.invoice)).toBe("Domain model › Billing › Entities › Invoice");
    const expanded = new Set<string>();
    const rowsNow = visibleRows(forest, "domain-model", expanded);
    expect(rowsNow.map((r) => r.key)).toEqual([find(forest, "domain-model", "Billing").key, "@domain-model/people"]);
    for (const key of revealPath(forest, IDS.invoice).slice(1)) {
      expanded.add(key);
      expandAt(
        forest,
        rowsNow,
        rowsNow.findIndex((r) => r.key === key),
        expanded,
      );
      expect(rowsNow).toEqual(visibleRows(forest, "domain-model", expanded));
    }
    const at = rowsNow.findIndex((r) => r.key === IDS.invoice);
    expect(at).toBeGreaterThan(0);
    expect(rowsNow[at].depth).toBe(2);
    const billingKey = find(forest, "domain-model", "Billing").key;
    expanded.delete(billingKey);
    collapseAt(
      rowsNow,
      rowsNow.findIndex((r) => r.key === billingKey),
    );
    expect(rowsNow).toEqual(visibleRows(forest, "domain-model", expanded));
  });

  it("is memoized by index tag", () => {
    const input: TreeInput = { rows, tag: "a", tables };
    const first = forestOf(input);
    expect(forestOf({ ...input, rows: [...rows] })).toBe(first);
    expect(forestOf({ ...input, tag: "b" })).not.toBe(first);
  });
});

describe("tree places", () => {
  const { rows, tables } = billing();
  const billingId = idOf(rows, "Billing");
  const catalogId = idOf(rows, "Catalog");
  const row = (id: string, kind: string, name: string, pkg: string | null, extra: Partial<ElementSummary> = {}) =>
    ({ id, kind, name, package: pkg, tags: [], category: null, stereotypes: [], hash: "0", path: `model/${id}.json`, ...extra }) as ElementSummary;

  it("puts an entity with no package first, in Not in a domain; People and access, then Other elements, last", () => {
    const extra = [
      row("7A", "entity", "Loose", null),
      row("7B", "actor", "Clerk", null),
      row("7C", "gizmo", "Widget", null),
      row("7D", "gizmo", "Sprocket", billingId),
    ];
    const f = buildForest({ rows: [...rows, ...extra], tables });
    expect(labels(f, f.roots["domain-model"])).toEqual(["Not in a domain", "Billing", "People and access", "Other elements"]);
    expect(labels(f, find(f, "domain-model", "Not in a domain").key)).toEqual(["Entities"]);
    expect(find(f, "domain-model", "People and access", "Actors", "Clerk").kind).toBe("actor");
    expect(find(f, "domain-model", "Other elements", "gizmo", "Widget").kind).toBe("gizmo");
    expect(find(f, "domain-model", "Billing", "Other", "Sprocket").kind).toBe("gizmo");
    expectReachable(f, [...rows, ...extra]);
  });

  it("does not loop on a parent cycle, and marks broken chains", () => {
    const extra = [
      row("7X", "package", "Ping", "7Y"),
      row("7Y", "package", "Pong", "7X"),
      row("7Z", "package", "Lost", "7Q"),
      row("7E", "entity", "Stray", "7Q"),
    ];
    const f = buildForest({ rows: [...rows, ...extra], tables });
    const top = find(f, "domain-model", "Not in a domain");
    expect(labels(f, top.key)).toEqual(["Lost", "Ping", "Entities"]);
    expect(find(f, "domain-model", "Not in a domain", "Ping").warning).toBe("Its parent chain loops");
    expect(labels(f, find(f, "domain-model", "Not in a domain", "Ping").key)).toEqual(["Pong"]);
    expect(find(f, "domain-model", "Not in a domain", "Lost").warning).toBe("Its parent domain does not exist");
    expect(find(f, "domain-model", "Not in a domain", "Entities", "Stray").warning).toBe("Its domain does not exist");
    expectReachable(f, [...rows, ...extra]);
  });

  it("lists an element matching a project-defined folder there once, and counts it on the kind folder", () => {
    const folders: ExplorerFolder[] = [{ label: "Agents", icon: "bot", kind: "entity", match: { stereotype: "aggregate-root", tag: null, category: null } }];
    const f = buildForest({ rows, tables, folders });
    const agents = find(f, "domain-model", "Billing", "Agents");
    expect(labels(f, agents.key)).toEqual(["Customer", "Invoice"]);
    expect(agents.icon).toBe("bot");
    expect(agents.tooltip).toBe("Entities with stereotype `aggregate-root`, from project settings");
    const entities = find(f, "domain-model", "Billing", "Entities");
    expect(labels(f, entities.key)).toEqual(["InvoiceLine", "Payment"]);
    expect(entities.tooltip).toContain("plus 2 in Agents");
    expect(find(f, "domain-model", "Billing").secondary).toBe("1 sub-domain · 5 entities");
    expectKindFolders(f, folders);
    // A category condition matches the node's descendants.
    const invoiceCategory = rows.find((r) => r.id === IDS.invoice)!.category!;
    const byCategory = buildForest({
      rows,
      folders: [{ label: "Core", icon: null, kind: "entity", match: { stereotype: null, tag: null, category: "7P" } }],
      categoryParents: new Map([[invoiceCategory, "7P"]]),
    });
    expect(labels(byCategory, find(byCategory, "domain-model", "Billing", "Core").key)).toEqual(["Customer", "Invoice", "InvoiceLine", "Payment"]);
  });

  it("groups an enum's lookup table under the enum's domain, mapped by the enum, with no unlinked marker", () => {
    const dbId = idOf(rows, "main");
    const enumId = idOf(rows, "InvoiceStatus");
    const lookup = {
      key: "7L",
      name: "invoice_statuses",
      schema: "billing",
      origin: "synthesized" as const,
      entityId: null,
      relationId: null,
      isJunction: false,
      isLookup: true,
      columnCount: 2,
      enumId,
    };
    const withLookup = new Map([[dbId, { tables: [...tables.get(dbId)!.tables, lookup] }]]);
    const f = buildForest({ rows, tables: withLookup, groupTablesByDomain: true });
    const table = find(f, "databases", "main", "billing", "Tables", "Billing", "invoice_statuses");
    // No "lookup" marker (EX 1.3): the deprecated isLookup flag places nothing; the enum link still relates them.
    expect(table.markers ?? []).toEqual([]);
    expect(relatedKeys(f, table.key)).toEqual(new Set([enumId]));
    expect(relatedKeys(f, enumId).has(table.key)).toBe(true);
    const flat = buildForest({ rows, tables: new Map([[dbId, { tables: [{ ...lookup, enumId: null, isLookup: false, entityId: null }] }]]) });
    expect(find(flat, "databases", "main", "billing", "Tables", "invoice_statuses").markers).toEqual(["unlinked"]);
  });

  it("builds the Reference data explorer with a leaf per type, and seeds by target", () => {
    const extra = [
      row("7R", "reference-type", "Unit of measure", null, { rowCount: 12 } as Partial<ElementSummary>),
      row("7S", "seed", "units", null, { target: "7R", rowCount: 12 } as Partial<ElementSummary>),
      row("7T", "seed", "sample invoices", null, { target: IDS.invoice, rowCount: 3 } as Partial<ElementSummary>),
    ];
    const f = buildForest({ rows: [...rows, ...extra], tables });
    expect(labels(f, f.roots["reference-data"])).toEqual(["Unit of measure"]);
    expect(f.headers["reference-data"]).toBe("1 type · 12 rows");
    // A type's only seed is its rows, whatever its name: no child, the seed placed on the type.
    const unit = find(f, "reference-data", "Unit of measure");
    expect(childKeys(f, unit.key)).toEqual([]);
    expect(f.place.get("7S")).toBe(unit.key);
    expect(find(f, "domain-model", "Billing", "Seed data", "sample invoices").secondary).toBe("3 rows");
    expect(find(f, "domain-model", "Billing", "Entities", "Invoice", "Seed data").secondary).toBe("1 seed · 3 rows");
    expectReachable(f, [...rows, ...extra]);
  });

  it("lists no child under a type with one seed, and places that seed on the type", () => {
    const extra = [
      row("7R", "reference-type", "CarModel", null, { displayName: "Car models" } as Partial<ElementSummary>),
      row("7S", "seed", "CarModel", null, { target: "7R", rowCount: 4 } as Partial<ElementSummary>),
      row("7U", "reference-type", "Country", null),
      row("7V", "seed", "Country", null, { target: "7U", rowCount: 2 } as Partial<ElementSummary>),
      row("7W", "seed", "Demo", null, { target: "7U", rowCount: 1 } as Partial<ElementSummary>),
    ];
    const f = buildForest({ rows: [...rows, ...extra], tables });
    const car = find(f, "reference-data", "Car models");
    expect(childKeys(f, car.key)).toEqual([]);
    expect(f.place.get("7S")).toBe(car.key);
    // Several seeds show, each with its row count.
    const country = find(f, "reference-data", "Country");
    expect(labels(f, country.key)).toEqual(["Country", "Demo"]);
    expect(find(f, "reference-data", "Country", "Demo").secondary).toBe("1 row");
    expect(f.headers["reference-data"]).toBe("2 types · 7 rows");
    expectReachable(f, [...rows, ...extra]);
  });

  it("nests the Reference data explorer by category path with counts (EX 1.7)", () => {
    const extra = [
      row("7R", "reference-type", "Unit of measure", null, { category: "measures" } as Partial<ElementSummary>),
      row("7Q", "reference-type", "Currency", null, { category: "money" } as Partial<ElementSummary>),
      row("7P", "reference-type", "Country", null),
    ];
    const categoryParents = new Map<string, string | null>([
      ["measures", "general"],
      ["money", "general"],
      ["general", null],
    ]);
    const categoryNames = new Map([
      ["measures", "Measures"],
      ["money", "Money"],
      ["general", "General"],
    ]);
    const f = buildForest({ rows: [...rows, ...extra], tables, categoryParents, categoryNames });
    expect(labels(f, f.roots["reference-data"])).toEqual(["General", "No category"]);
    const general = find(f, "reference-data", "General");
    expect(general.count).toBe(2);
    expect(labels(f, general.key)).toEqual(["Measures", "Money"]);
    expect(labels(f, find(f, "reference-data", "General", "Measures").key)).toEqual(["Unit of measure"]);
    expect(labels(f, find(f, "reference-data", "No category").key)).toEqual(["Country"]);
    expectReachable(f, [...rows, ...extra]);
    // The A to Z option: one list of every type, no category groups.
    const flat = buildForest({ rows: [...rows, ...extra], tables, categoryParents, categoryNames, referenceFlat: true });
    const flatLabels = labels(flat, flat.roots["reference-data"]);
    expect(flatLabels).toContain("Country");
    expect(flatLabels).toContain("Unit of measure");
    expect(flatLabels).not.toContain("General");
    expect(flatLabels).toEqual([...flatLabels].sort((a, b) => a.localeCompare(b, "en", { sensitivity: "base" })));
    expectReachable(flat, [...rows, ...extra]);
  });

  it("chains a domain's vocabularies: nearest first, then the enclosing domains', then global, never a sibling's", () => {
    const extra = [
      row("7G", "tag-vocabulary", "catalog tags", catalogId),
      row("7H", "tag-vocabulary", "billing tags", billingId),
      row("7J", "package", "Sales", null),
      row("7K", "category-tree", "sales categories", "7J"),
    ];
    const f = buildForest({ rows: [...rows, ...extra], tables });
    const product = idOf(rows, "Product");
    const chain = vocabularyChain(f, product);
    expect(chain.tags.map((v) => [v.id, v.scope])).toEqual([
      ["7G", catalogId],
      ["7H", billingId],
      [idOf(rows, "tags"), null],
    ]);
    expect(chain.categories.map((v) => v.id)).toEqual([idOf(rows, "categories")]);
    expect(vocabularyChain(f, "7J").categories.map((v) => v.id)).toEqual(["7K", idOf(rows, "categories")]);
    expect(vocabularyChain(f, null).tags.map((v) => v.id)).toEqual([idOf(rows, "tags")]);
  });
});

describe("tree over the medium seed", () => {
  const backend = new MockBackend({ scenarios: ["medium"] });
  const rows = backend.model.index();

  it("reaches every row, with kind folders only, before and after the table summaries", () => {
    const bare = buildForest({ rows });
    expectReachable(bare, rows);
    expectKindFolders(bare);
    const tables = new Map<string, TablesInput>();
    for (const db of rows.filter((r) => r.kind === "database")) tables.set(db.id, backend.generation.databaseTables(db.id)!);
    const full = buildForest({ rows, tables, groupTablesByDomain: true });
    expectReachable(full, rows);
    expectKindFolders(full);
  });
});

const largeSeed = path.resolve(import.meta.dirname, "../../src/mocks/data/large.json.gz");

describe.skipIf(!fs.existsSync(largeSeed))("tree over the large mock (?mock=large)", () => {
  let rows: ElementSummary[] = [];
  let backend: MockBackend;

  it("loads the large seed", async () => {
    backend = new MockBackend({ seed: await decodeSeed(new Uint8Array(fs.readFileSync(largeSeed))) });
    rows = backend.model.index();
    expect(rows.length).toBeGreaterThan(25_000);
  }, 60_000);

  it("nests the domains, reaches every row and keeps kind folders", () => {
    const forest = buildForest({ rows });
    expect(forest.headers["domain-model"]).toMatch(/^40 domains · 5,\d{3} entities$/);
    expect(forest.pending.size).toBeGreaterThan(0);
    expectReachable(forest, rows);
    expectKindFolders(forest);
    const tops = childKeys(forest, forest.roots["domain-model"]).map((k) => node(forest, k));
    expect(tops.filter((t) => t.type === "domain").length).toBe(2);
    // Relation-heavy entity children are answered from the index.
    const busiest = [...forest.related.relationsOf.entries()].sort((a, b) => b[1].length - a[1].length)[0];
    const relations = childKeys(forest, busiest[0])
      .map((k) => node(forest, k))
      .find((n) => n.label === "Relationships")!;
    expect(relations.count).toBe(busiest[1].length);
  });

  // Wall-clock budgets are asserted only with MQ_SCALE_STRICT=1 (the Playwright scale project owns the EX 4.5 timings);
  // by default the times are reported, so a busy machine cannot fail the unit suite.
  it("builds the tree within the section 4.5 budget (30 ms including the related maps, + 25 % for CI noise)", () => {
    for (let i = 0; i < 10; i++) buildForest({ rows });
    const times: number[] = [];
    for (let i = 0; i < 7; i++) {
      const started = performance.now();
      buildForest({ rows });
      times.push(performance.now() - started);
    }
    times.sort((a, b) => a - b);
    const median = times[3];
    console.info(`tree build, ${rows.length} rows: median ${median.toFixed(1)} ms`);
    if (strictTiming) expect(median).toBeLessThanOrEqual(30 * 1.25);
  });

  it("shapes Databases once the table summaries arrive, and splices the largest Tables folder", () => {
    const tables = new Map<string, TablesInput>();
    for (const db of rows.filter((r) => r.kind === "database")) tables.set(db.id, backend.generation.databaseTables(db.id)!);
    const started = performance.now();
    const forest = buildForest({ rows, tables });
    console.info(`tree build with ${[...tables.values()].reduce((n, t) => n + t.tables.length, 0)} tables: ${(performance.now() - started).toFixed(1)} ms`);
    expectReachable(forest, rows);
    const folders = [...forest.nodes.values()].filter((n) => n.type === "folder" && n.kind === "table" && n.explorer === "databases");
    const largest = folders.sort((a, b) => (b.count ?? 0) - (a.count ?? 0))[0];
    expect(largest.count).toBeGreaterThan(5_000);
    const expanded = new Set(pathOf(forest, largest.key).slice(1));
    const visible = visibleRows(forest, "databases", new Set([...expanded].filter((k) => k !== largest.key)));
    const at = visible.findIndex((r) => r.key === largest.key);
    const t0 = performance.now();
    expandAt(forest, visible, at, expanded);
    const ms = performance.now() - t0;
    expect(visible.length).toBeGreaterThan(largest.count!);
    console.info(`expand ${largest.count} tables: ${ms.toFixed(1)} ms`);
    if (strictTiming) expect(ms).toBeLessThanOrEqual(16 * 1.25);
  }, 120_000);
});
