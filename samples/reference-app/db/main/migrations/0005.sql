-- Migration 0005 of database main (PostgreSQL 16): schema revision 4 to 5.
-- Written once by Maquettiste (sql-ddl/migration) from the schema diff. It is yours now: review it, adjust it and commit it.
-- Lines marked TODO need a decision the diff cannot make (data conversions, sequence restarts).

BEGIN;

COMMENT ON VIEW northwind.open_sales_orders IS 'Excludes soft-deleted orders.';

COMMIT;
