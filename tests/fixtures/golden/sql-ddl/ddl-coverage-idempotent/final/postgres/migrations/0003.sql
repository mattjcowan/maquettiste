-- Migration 0003 of database postgres (PostgreSQL): schema revision 2 to 3.
-- Written once by Maquettiste (sql-ddl/migration) from the schema diff. It is yours now: review it, adjust it and commit it.
-- Lines marked TODO need a decision the diff cannot make (data conversions, sequence restarts).

BEGIN;

DROP MATERIALIZED VIEW IF EXISTS sales.order_counts;

DROP VIEW IF EXISTS sales.active_customers;

DROP VIEW IF EXISTS sales.customer_order_totals;

DROP VIEW IF EXISTS sales.customer_order_list;

ALTER TABLE sales.orders DROP CONSTRAINT IF EXISTS fk_orders_customer;

ALTER TABLE sales.customers DROP CONSTRAINT IF EXISTS ck_customers_status;

ALTER TABLE sales.customers DROP CONSTRAINT IF EXISTS uq_customers_mail;

DROP INDEX IF EXISTS sales.ix_customers_active;

DROP INDEX IF EXISTS sales.ix_orders_placed_on;

-- Data loss: dropping table notes deletes its rows.

DROP TABLE IF EXISTS sales.notes;

-- Data loss: dropping column customers.phone deletes its values.

ALTER TABLE sales.customers DROP COLUMN IF EXISTS phone;

-- Data loss: dropping column customers.loyalty deletes its values.

ALTER TABLE sales.customers DROP COLUMN IF EXISTS loyalty;

-- TODO: customers.balance holds fewer digits (precision: 12 -> 10); values that do not fit are refused or rounded. Check first: SELECT count(*) FROM sales.customers WHERE abs(balance) >= 100000000 OR balance <> round(balance, 2);

ALTER TABLE sales.customers DROP COLUMN balance_twice;

ALTER TABLE sales.customers ALTER COLUMN balance TYPE numeric(10,2);

ALTER TABLE sales.customers ADD COLUMN IF NOT EXISTS balance_twice numeric(14,2) GENERATED ALWAYS AS (balance * 2) STORED;

-- TODO: orders.number gets shorter (length: 20 -> 16); longer values are cut or refused. Check first: SELECT count(*) FROM sales.orders WHERE length(number) > 16;

ALTER TABLE sales.orders ALTER COLUMN number TYPE varchar(16);

DO $$ BEGIN IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'fk_orders_customer' AND conrelid = 'sales.orders'::regclass) THEN ALTER TABLE sales.orders ADD CONSTRAINT fk_orders_customer FOREIGN KEY (customer_id) REFERENCES sales.customers (id) DEFERRABLE INITIALLY IMMEDIATE; END IF; END $$;

DROP SEQUENCE IF EXISTS sales.audit_numbers;

COMMIT;
