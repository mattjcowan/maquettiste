-- Migration 0005 of database postgres (PostgreSQL): schema revision 4 to 5.
-- Written once by Maquettiste (sql-ddl/migration) from the schema diff. It is yours now: review it, adjust it and commit it.
-- Lines marked TODO need a decision the diff cannot make (data conversions, sequence restarts).

BEGIN;

ALTER TABLE archive.order_items SET SCHEMA shop;

DROP SCHEMA archive;

COMMIT;
