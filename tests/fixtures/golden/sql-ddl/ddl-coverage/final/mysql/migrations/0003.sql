-- Migration 0003 of database mysql (MySQL): schema revision 2 to 3.
-- Written once by Maquettiste (sql-ddl/migration) from the schema diff. It is yours now: review it, adjust it and commit it.
-- Lines marked TODO need a decision the diff cannot make (data conversions, sequence restarts).

-- MySQL commits each DDL statement on its own (an implicit commit), so BEGIN and COMMIT do not make this script atomic: run it once,
-- top to bottom, and restore a backup if a statement fails.
BEGIN;

DROP VIEW active_customers;

DROP VIEW customer_order_totals;

DROP VIEW customer_order_list;

ALTER TABLE orders DROP FOREIGN KEY fk_orders_customer;

ALTER TABLE customers DROP CONSTRAINT ck_customers_status;

ALTER TABLE customers DROP INDEX uq_customers_mail;

DROP INDEX ix_orders_placed_on ON orders;

-- Data loss: dropping table notes deletes its rows.

DROP TABLE notes;

-- Data loss: dropping column customers.phone deletes its values.

ALTER TABLE customers DROP COLUMN phone;

-- Data loss: dropping column customers.loyalty deletes its values.

ALTER TABLE customers DROP COLUMN loyalty;

-- TODO: customers.name changes its native type (varchar(150) character set utf8mb4 -> varchar(150)); check that every value converts before running this.

ALTER TABLE customers MODIFY COLUMN name varchar(150) COLLATE utf8mb4_bin NOT NULL;

-- TODO: customers.balance holds fewer digits (precision: 12 -> 10); values that do not fit are refused or rounded. Check first: SELECT count(*) FROM customers WHERE abs(balance) >= 100000000 OR balance <> round(balance, 2);

ALTER TABLE customers MODIFY COLUMN balance decimal(10,2) NOT NULL DEFAULT 10;

-- TODO: orders.number gets shorter (length: 20 -> 16); longer values are cut or refused. Check first: SELECT count(*) FROM orders WHERE CHAR_LENGTH(number) > 16;

ALTER TABLE orders MODIFY COLUMN number varchar(16) NOT NULL;

ALTER TABLE orders ADD CONSTRAINT fk_orders_customer FOREIGN KEY (customer_id) REFERENCES customers (id);

COMMIT;
