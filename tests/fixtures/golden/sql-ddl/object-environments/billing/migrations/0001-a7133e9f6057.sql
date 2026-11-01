-- Migration 0001 of database billing (PostgreSQL 16): schema revision 0 to 1.
-- From state: none
-- To state: 489f23fdf4a8
-- Facts: none
-- Written once by Maquettiste (sql-ddl/migration) from the schema diff. It is yours now: review it, adjust it and commit it.
-- Lines marked TODO need a decision the diff cannot make (data conversions, sequence restarts).
-- One transaction: the DDL and a migration ledger row for each run-once SQL object, which runs only where the ledger does not record it yet (1).

BEGIN;

CREATE SCHEMA IF NOT EXISTS migration_ledger;
CREATE TABLE IF NOT EXISTS migration_ledger.contract (id smallint PRIMARY KEY DEFAULT 1 CHECK (id = 1),
    version integer NOT NULL);
INSERT INTO migration_ledger.contract (id, version) VALUES (1, 1) ON CONFLICT (id) DO NOTHING;
DO $ledger$ BEGIN
    IF (SELECT version FROM migration_ledger.contract WHERE id = 1) > 1 THEN
        RAISE EXCEPTION 'ledger contract version is newer than 1; use a newer writer';
    END IF;
END $ledger$;
CREATE TABLE IF NOT EXISTS migration_ledger.applied_fact (fact_id text PRIMARY KEY, content_hash text NOT NULL,
    applied_at timestamptz NOT NULL DEFAULT now(), applied_by text);
CREATE TABLE IF NOT EXISTS migration_ledger.applied_object (element_id text PRIMARY KEY, kind text NOT NULL,
    schema_name text, name text NOT NULL, content_hash text NOT NULL, revert_text text,
    applied_at timestamptz NOT NULL DEFAULT now());

CREATE SCHEMA IF NOT EXISTS billing;

CREATE TABLE billing.invoices (
    id bigint NOT NULL,
    total numeric(12,2) NULL,
    CONSTRAINT pk_invoices PRIMARY KEY (id)
);

DO $once$ BEGIN
    IF NOT EXISTS (SELECT 1 FROM migration_ledger.applied_object WHERE element_id = '01SE0000000000000000000023') THEN
        EXECUTE 'CREATE TABLE billing.invoice_audit (id bigint NOT NULL);
INSERT INTO billing.invoice_audit (id) VALUES (1);';
        INSERT INTO migration_ledger.applied_object (element_id, kind, schema_name, name, content_hash) VALUES ('01SE0000000000000000000023', 'sql-object', 'billing', 'invoice_audit', '4726c5fba6337567b8aa0e5d00eb998f15dc95d56fd814b598cd044ebe09048d');
    END IF;
END $once$;

GRANT SELECT ON billing.invoices TO PUBLIC;

COMMIT;
