-- Migration 0004 of database postgres (PostgreSQL): schema revision 3 to 4.
-- Written once by Maquettiste (sql-ddl/migration) from the schema diff. It is yours now: review it, adjust it and commit it.
-- Lines marked TODO need a decision the diff cannot make (data conversions, sequence restarts).

BEGIN;

CREATE SCHEMA IF NOT EXISTS archive;

DO $$ BEGIN IF EXISTS (SELECT 1 FROM pg_namespace WHERE nspname = 'sales') THEN ALTER SCHEMA sales RENAME TO shop; END IF; END $$;

ALTER TABLE IF EXISTS shop.order_items SET SCHEMA archive;

-- customers: nothing to change in the database (a property the DDL does not write changed).

-- orders: nothing to change in the database (a property the DDL does not write changed).

COMMIT;
