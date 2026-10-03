-- Migration 0004 of database sqlserver (SQL Server): schema revision 3 to 4.
-- Written once by Maquettiste (sql-ddl/migration) from the schema diff. It is yours now: review it, adjust it and commit it.
-- Lines marked TODO need a decision the diff cannot make (data conversions, sequence restarts).

-- One transaction across the batches: XACT_ABORT rolls it back on an error, and a batch that finds it gone turns execution off
-- (SET NOEXEC ON), so no later batch runs outside it.
SET XACT_ABORT ON;
SET QUOTED_IDENTIFIER ON;
BEGIN TRANSACTION;

IF SCHEMA_ID(N'archive') IS NULL EXEC(N'CREATE SCHEMA archive');

IF SCHEMA_ID(N'shop') IS NULL EXEC(N'CREATE SCHEMA shop');

ALTER SCHEMA shop TRANSFER sales.customers;

ALTER SCHEMA shop TRANSFER sales.orders;

ALTER SCHEMA archive TRANSFER sales.order_items;

ALTER TABLE shop.orders DROP CONSTRAINT fk_orders_customer;

ALTER TABLE shop.customers DROP CONSTRAINT pk_customers;

ALTER TABLE shop.customers ADD CONSTRAINT pk_customers PRIMARY KEY NONCLUSTERED (id);

ALTER TABLE shop.orders ADD CONSTRAINT fk_orders_customer FOREIGN KEY (customer_id) REFERENCES shop.customers (id);

IF OBJECT_ID(N'shop.df_orders_id', 'D') IS NOT NULL ALTER TABLE shop.orders DROP CONSTRAINT df_orders_id;

ALTER SCHEMA shop TRANSFER sales.order_number_seq;

ALTER TABLE shop.orders ADD CONSTRAINT df_orders_id DEFAULT (NEXT VALUE FOR shop.order_number_seq) FOR id;

DROP SCHEMA sales;

COMMIT TRANSACTION;
