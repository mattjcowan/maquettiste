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

IF OBJECT_ID(N'sales.customers', N'U') IS NOT NULL ALTER SCHEMA shop TRANSFER sales.customers;

IF OBJECT_ID(N'sales.orders', N'U') IS NOT NULL ALTER SCHEMA shop TRANSFER sales.orders;

IF OBJECT_ID(N'sales.order_items', N'U') IS NOT NULL ALTER SCHEMA archive TRANSFER sales.order_items;

ALTER TABLE shop.orders DROP CONSTRAINT IF EXISTS fk_orders_customer;

ALTER TABLE shop.customers DROP CONSTRAINT IF EXISTS pk_customers;

IF OBJECT_ID(N'shop.pk_customers') IS NULL ALTER TABLE shop.customers ADD CONSTRAINT pk_customers PRIMARY KEY NONCLUSTERED (id);

IF OBJECT_ID(N'shop.fk_orders_customer') IS NULL ALTER TABLE shop.orders ADD CONSTRAINT fk_orders_customer FOREIGN KEY (customer_id) REFERENCES shop.customers (id);

IF OBJECT_ID(N'shop.df_orders_id', 'D') IS NOT NULL ALTER TABLE shop.orders DROP CONSTRAINT df_orders_id;

IF OBJECT_ID(N'sales.order_number_seq', N'SO') IS NOT NULL ALTER SCHEMA shop TRANSFER sales.order_number_seq;

ALTER TABLE shop.orders ADD CONSTRAINT df_orders_id DEFAULT (NEXT VALUE FOR shop.order_number_seq) FOR id;

DROP SCHEMA IF EXISTS sales;

COMMIT TRANSACTION;
