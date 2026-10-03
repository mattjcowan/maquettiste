-- Migration 0003 of database sqlserver (SQL Server): schema revision 2 to 3.
-- Written once by Maquettiste (sql-ddl/migration) from the schema diff. It is yours now: review it, adjust it and commit it.
-- Lines marked TODO need a decision the diff cannot make (data conversions, sequence restarts).

-- One transaction across the batches: XACT_ABORT rolls it back on an error, and a batch that finds it gone turns execution off
-- (SET NOEXEC ON), so no later batch runs outside it.
SET XACT_ABORT ON;
SET QUOTED_IDENTIFIER ON;
BEGIN TRANSACTION;

DROP VIEW IF EXISTS sales.active_customers;

DROP VIEW IF EXISTS sales.customer_order_totals;

DROP VIEW IF EXISTS sales.customer_order_list;

ALTER TABLE sales.orders DROP CONSTRAINT IF EXISTS fk_orders_customer;

ALTER TABLE sales.customers DROP CONSTRAINT IF EXISTS ck_customers_status;

ALTER TABLE sales.customers DROP CONSTRAINT IF EXISTS uq_customers_mail;

DROP INDEX IF EXISTS ix_customers_active ON sales.customers;

DROP INDEX IF EXISTS ix_orders_placed_on ON sales.orders;

-- Data loss: dropping table notes deletes its rows.

DROP TABLE IF EXISTS sales.notes;

-- Data loss: dropping column customers.phone deletes its values.

IF OBJECT_ID(N'sales.df_customers_phone', 'D') IS NOT NULL ALTER TABLE sales.customers DROP CONSTRAINT df_customers_phone;

ALTER TABLE sales.customers DROP COLUMN IF EXISTS phone;

-- Data loss: dropping column customers.loyalty deletes its values.

IF OBJECT_ID(N'sales.df_customers_loyalty', 'D') IS NOT NULL ALTER TABLE sales.customers DROP CONSTRAINT df_customers_loyalty;

ALTER TABLE sales.customers DROP COLUMN IF EXISTS loyalty;

-- TODO: customers.name switches from Unicode to single-byte text (nvarchar(150) -> varchar(150)); characters outside the code page become '?'. Check first: SELECT count(*) FROM sales.customers WHERE CAST(CAST(name AS varchar(150)) AS nvarchar(150)) <> name;

DROP INDEX IF EXISTS ix_customers_name ON sales.customers;

ALTER TABLE sales.customers ALTER COLUMN name varchar(150) COLLATE Latin1_General_100_CI_AS NOT NULL;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'ix_customers_name' AND object_id = OBJECT_ID(N'sales.customers')) CREATE INDEX ix_customers_name ON sales.customers (name, created_at DESC) INCLUDE (email);

-- TODO: customers.balance holds fewer digits (precision: 12 -> 10); values that do not fit are refused or rounded. Check first: SELECT count(*) FROM sales.customers WHERE abs(balance) >= 100000000 OR balance <> round(balance, 2);

ALTER TABLE sales.customers DROP CONSTRAINT IF EXISTS ck_customers_balance;

ALTER TABLE sales.customers DROP COLUMN balance_twice;

IF OBJECT_ID(N'sales.customers_balance_dflt', 'D') IS NOT NULL ALTER TABLE sales.customers DROP CONSTRAINT customers_balance_dflt;

ALTER TABLE sales.customers ALTER COLUMN balance decimal(10,2) NOT NULL;

IF OBJECT_ID(N'sales.customers_balance_dflt', 'D') IS NULL ALTER TABLE sales.customers ADD CONSTRAINT customers_balance_dflt DEFAULT 10 FOR balance;

IF OBJECT_ID(N'sales.ck_customers_balance') IS NULL ALTER TABLE sales.customers ADD CONSTRAINT ck_customers_balance CHECK (balance >= -100);

IF COL_LENGTH(N'sales.customers', N'balance_twice') IS NULL ALTER TABLE sales.customers ADD balance_twice AS (balance * 2) PERSISTED;

-- TODO: orders.number gets shorter (length: 20 -> 16); longer values are cut or refused. Check first: SELECT count(*) FROM sales.orders WHERE LEN(number) > 16;

ALTER TABLE sales.orders DROP CONSTRAINT IF EXISTS uq_orders_customer_number;

ALTER TABLE sales.orders ALTER COLUMN number nvarchar(16) NOT NULL;

IF OBJECT_ID(N'sales.uq_orders_customer_number') IS NULL ALTER TABLE sales.orders ADD CONSTRAINT uq_orders_customer_number UNIQUE (customer_id, number);

IF OBJECT_ID(N'sales.fk_orders_customer') IS NULL ALTER TABLE sales.orders ADD CONSTRAINT fk_orders_customer FOREIGN KEY (customer_id) REFERENCES sales.customers (id);

DROP SEQUENCE IF EXISTS sales.audit_numbers;

COMMIT TRANSACTION;
