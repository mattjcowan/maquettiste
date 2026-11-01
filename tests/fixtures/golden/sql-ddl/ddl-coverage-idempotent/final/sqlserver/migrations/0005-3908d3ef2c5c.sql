-- Migration 0005 of database sqlserver (SQL Server): schema revision 4 to 5.
-- From state: 765e0ac75c01
-- To state: 6fbea688d5f0
-- Facts: none
-- Written once by Maquettiste (sql-ddl/migration) from the schema diff. It is yours now: review it, adjust it and commit it.
-- Lines marked TODO need a decision the diff cannot make (data conversions, sequence restarts).

-- One transaction across the batches: XACT_ABORT rolls it back on an error, and a batch that finds it gone turns execution off
-- (SET NOEXEC ON), so no later batch runs outside it.
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID(N'archive.order_items', N'U') IS NOT NULL ALTER SCHEMA shop TRANSFER archive.order_items;

DROP SCHEMA IF EXISTS archive;

COMMIT TRANSACTION;
