// The mock model's routines, database types and SQL objects (schemas/v1/routine.json, database-type.json, sql-object.json),
// added to the billing fixture by seed.ts: the billing fixture is shared with the engine's tests, so these documents live with
// the mocks. Database main (postgresql, default schema billing) gets a function, a procedure, a domain, an enum type and a
// SQL object that grants read access to the outstanding_invoices view.
import type { SeedFile } from "./store";

const MAIN = "01J92P0V1QRN2181XM2ZWE02W4";
const BILLING_SCHEMA = "01J92P0V1RC04SKQ5353EAKHG2";
const OUTSTANDING_INVOICES = "01J92P0V1YZ4YP352KD1A50FXS";

export const INVOICE_TOTAL = "01JQRTN0000000000000000001";
export const CLOSE_PERIOD = "01JQRTN0000000000000000002";
export const EMAIL_ADDRESS = "01JQDTY0000000000000000001";
export const INVOICE_STATE = "01JQDTY0000000000000000002";
export const REPORTING_READ = "01JQSQB0000000000000000001";

const file = (folder: string, name: string, kind: string, json: Record<string, unknown>): SeedFile => ({
  path: `model/databases/main/${folder}/${name.replace(/_/g, "-")}.json`,
  text: JSON.stringify({ $schema: `../../../../.schema/v1/${kind}.json`, kind, ...json }, null, 2),
});

export function databaseObjectFiles(): SeedFile[] {
  return [
    file("routines", "invoice_total", "routine", {
      id: INVOICE_TOTAL,
      name: "invoice_total",
      database: MAIN,
      schema: BILLING_SCHEMA,
      description: "The amount due on an invoice.",
      parameters: [{ name: "p_invoice", type: "uuid" }],
      returns: { type: "decimal", precision: 18, scale: 2 },
      body: { postgresql: "begin\n  return (select coalesce(sum(total), 0) from billing.invoices where id = p_invoice);\nend" },
    }),
    file("routines", "close_period", "routine", {
      id: CLOSE_PERIOD,
      name: "close_period",
      database: MAIN,
      schema: BILLING_SCHEMA,
      routineKind: "procedure",
      parameters: [{ name: "p_until", type: "date" }],
      body: { postgresql: "begin\n  update billing.invoices set status = 3 where issued_on < p_until and status = 1;\nend" },
    }),
    file("types", "email_address", "database-type", {
      id: EMAIL_ADDRESS,
      name: "email_address",
      database: MAIN,
      schema: BILLING_SCHEMA,
      typeKind: "domain",
      base: "string",
      length: 320,
      check: "VALUE like '%@%'",
    }),
    file("types", "invoice_state", "database-type", {
      id: INVOICE_STATE,
      name: "invoice_state",
      database: MAIN,
      schema: BILLING_SCHEMA,
      typeKind: "enum",
      members: ["draft", "issued", "paid", "void"],
    }),
    file("objects", "reporting_read", "sql-object", {
      id: REPORTING_READ,
      name: "reporting_read",
      database: MAIN,
      schema: BILLING_SCHEMA,
      objectKind: "grant",
      dependsOn: [OUTSTANDING_INVOICES],
      body: { "*": "grant select on billing.outstanding_invoices to reporting;" },
    }),
  ];
}
