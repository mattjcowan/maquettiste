-- Migration 0002 of database mysql (MySQL): schema revision 1 to 2.
-- Written once by Maquettiste (sql-ddl/migration) from the schema diff. It is yours now: review it, adjust it and commit it.
-- Lines marked TODO need a decision the diff cannot make (data conversions, sequence restarts).

-- MySQL commits each DDL statement on its own (an implicit commit), so BEGIN and COMMIT do not make this script atomic: run it once,
-- top to bottom, and restore a backup if a statement fails.
BEGIN;

DROP VIEW customer_order_totals;

ALTER TABLE order_lines RENAME TO order_items;

ALTER TABLE customers COMMENT = 'Customers who place orders, with their balance.';

ALTER TABLE customers DROP CONSTRAINT ck_customers_balance;

ALTER TABLE customers RENAME INDEX uq_customers_email TO uq_customers_mail;

ALTER TABLE customers RENAME INDEX ix_customers_code TO ix_customers_code_lookup;

DROP INDEX ix_orders_placed_on ON orders;

ALTER TABLE order_items RENAME COLUMN product_code TO sku;

ALTER TABLE order_items MODIFY COLUMN quantity bigint NOT NULL;

-- TODO: convert existing values of order_items.quantity (type: int32 -> int64); MODIFY COLUMN converts each value as it can.

ALTER TABLE customers ADD COLUMN phone varchar(30) NULL COMMENT 'Contact phone.';

ALTER TABLE customers ADD COLUMN loyalty int NULL;

ALTER TABLE customers ADD COLUMN vip tinyint(1) NOT NULL DEFAULT 0;

ALTER TABLE orders DROP FOREIGN KEY fk_orders_customer;

ALTER TABLE customers MODIFY COLUMN id bigint NOT NULL AUTO_INCREMENT;

-- TODO: convert existing values of customers.id (type: int32 -> int64); MODIFY COLUMN converts each value as it can.

ALTER TABLE customers MODIFY COLUMN name varchar(150) character set utf8mb4 COLLATE utf8mb4_bin NOT NULL;

ALTER TABLE customers MODIFY COLUMN email varchar(200) NULL;

ALTER TABLE customers RENAME COLUMN notes TO remarks;

ALTER TABLE customers MODIFY COLUMN remarks longtext NULL COMMENT 'Remarks about the customer.';

ALTER TABLE customers MODIFY COLUMN balance decimal(12,2) NOT NULL DEFAULT 10;

ALTER TABLE orders MODIFY COLUMN customer_id bigint NOT NULL;

-- TODO: convert existing values of orders.customer_id (type: int32 -> int64); MODIFY COLUMN converts each value as it can.

CREATE TABLE notes (
    id bigint NOT NULL AUTO_INCREMENT,
    customer_id bigint NULL,
    body longtext NOT NULL DEFAULT ('-'),
    CONSTRAINT pk_notes PRIMARY KEY (id),
    CONSTRAINT fk_notes_customer FOREIGN KEY (customer_id) REFERENCES customers (id) ON DELETE SET NULL,
    INDEX ix_notes_customer (customer_id),
    INDEX ix_notes_body (body(50))
);

ALTER TABLE customers ADD CONSTRAINT ck_customers_balance CHECK (balance >= -100);

ALTER TABLE orders ADD CONSTRAINT ck_orders_total CHECK (total >= 0);

CREATE INDEX ix_orders_placed_on ON orders (placed_on DESC, number);

ALTER TABLE orders ADD CONSTRAINT fk_orders_customer FOREIGN KEY (customer_id) REFERENCES customers (id) ON DELETE CASCADE;

RENAME TABLE customer_orders TO customer_order_list;

CREATE VIEW customer_order_totals (customer_id, order_count) AS
SELECT id, count(*) FROM customer_order_list GROUP BY id;

COMMIT;
