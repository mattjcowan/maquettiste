-- Migration 0005 of database sqlserver (SQL Server): schema revision 4 to 5.
-- Written once by Maquettiste (sql-ddl/migration) from the schema diff. It is yours now: review it, adjust it and commit it.
-- Lines marked TODO need a decision the diff cannot make (data conversions, sequence restarts).

-- One transaction across the batches: XACT_ABORT rolls it back on an error, and a batch that finds it gone turns execution off
-- (SET NOEXEC ON), so no later batch runs outside it.
SET XACT_ABORT ON;
BEGIN TRANSACTION;

ALTER SCHEMA shop TRANSFER archive.order_items;

DROP SCHEMA archive;

COMMIT TRANSACTION;
