-- Migration 0002 of database mysql (MySQL): schema revision 1 to 2.
-- Written once by Maquettiste (sql-ddl/migration) from the schema diff. It is yours now: review it, adjust it and commit it.
-- Lines marked TODO need a decision the diff cannot make (data conversions, sequence restarts).

-- MySQL commits each DDL statement on its own (an implicit commit), so BEGIN and COMMIT do not make this script atomic: run it once,
-- top to bottom, and restore a backup if a statement fails.
BEGIN;

DROP VIEW IF EXISTS customer_order_totals;

SET @mq_sql = IF(EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = DATABASE() AND table_name = 'order_lines'), 'ALTER TABLE order_lines RENAME TO order_items', 'DO 0');
PREPARE mq_stmt FROM @mq_sql;
EXECUTE mq_stmt;
DEALLOCATE PREPARE mq_stmt;

ALTER TABLE customers COMMENT = 'Customers who place orders, with their balance.';

SET @mq_sql = IF(EXISTS (SELECT 1 FROM information_schema.table_constraints WHERE table_schema = DATABASE() AND table_name = 'customers' AND constraint_name = 'ck_customers_balance'), 'ALTER TABLE customers DROP CONSTRAINT ck_customers_balance', 'DO 0');
PREPARE mq_stmt FROM @mq_sql;
EXECUTE mq_stmt;
DEALLOCATE PREPARE mq_stmt;

SET @mq_sql = IF(EXISTS (SELECT 1 FROM information_schema.statistics WHERE table_schema = DATABASE() AND table_name = 'customers' AND index_name = 'uq_customers_email'), 'ALTER TABLE customers RENAME INDEX uq_customers_email TO uq_customers_mail', 'DO 0');
PREPARE mq_stmt FROM @mq_sql;
EXECUTE mq_stmt;
DEALLOCATE PREPARE mq_stmt;

SET @mq_sql = IF(EXISTS (SELECT 1 FROM information_schema.statistics WHERE table_schema = DATABASE() AND table_name = 'customers' AND index_name = 'ix_customers_code'), 'ALTER TABLE customers RENAME INDEX ix_customers_code TO ix_customers_code_lookup', 'DO 0');
PREPARE mq_stmt FROM @mq_sql;
EXECUTE mq_stmt;
DEALLOCATE PREPARE mq_stmt;

SET @mq_sql = IF(EXISTS (SELECT 1 FROM information_schema.statistics WHERE table_schema = DATABASE() AND table_name = 'orders' AND index_name = 'ix_orders_placed_on'), 'DROP INDEX ix_orders_placed_on ON orders', 'DO 0');
PREPARE mq_stmt FROM @mq_sql;
EXECUTE mq_stmt;
DEALLOCATE PREPARE mq_stmt;

SET @mq_sql = IF(EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = DATABASE() AND table_name = 'order_items' AND column_name = 'product_code'), 'ALTER TABLE order_items RENAME COLUMN product_code TO sku', 'DO 0');
PREPARE mq_stmt FROM @mq_sql;
EXECUTE mq_stmt;
DEALLOCATE PREPARE mq_stmt;

ALTER TABLE order_items MODIFY COLUMN quantity bigint NOT NULL;

-- TODO: convert existing values of order_items.quantity (type: int32 -> int64); MODIFY COLUMN converts each value as it can.

SET @mq_sql = IF(NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = DATABASE() AND table_name = 'customers' AND column_name = 'phone'), 'ALTER TABLE customers ADD COLUMN phone varchar(30) NULL COMMENT ''Contact phone.''', 'DO 0');
PREPARE mq_stmt FROM @mq_sql;
EXECUTE mq_stmt;
DEALLOCATE PREPARE mq_stmt;

SET @mq_sql = IF(NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = DATABASE() AND table_name = 'customers' AND column_name = 'loyalty'), 'ALTER TABLE customers ADD COLUMN loyalty int NULL', 'DO 0');
PREPARE mq_stmt FROM @mq_sql;
EXECUTE mq_stmt;
DEALLOCATE PREPARE mq_stmt;

SET @mq_sql = IF(NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = DATABASE() AND table_name = 'customers' AND column_name = 'vip'), 'ALTER TABLE customers ADD COLUMN vip tinyint(1) NOT NULL DEFAULT 0', 'DO 0');
PREPARE mq_stmt FROM @mq_sql;
EXECUTE mq_stmt;
DEALLOCATE PREPARE mq_stmt;

SET @mq_sql = IF(EXISTS (SELECT 1 FROM information_schema.table_constraints WHERE table_schema = DATABASE() AND table_name = 'notes' AND constraint_name = 'fk_notes_customer' AND constraint_type = 'FOREIGN KEY'), 'ALTER TABLE notes DROP FOREIGN KEY fk_notes_customer', 'DO 0');
PREPARE mq_stmt FROM @mq_sql;
EXECUTE mq_stmt;
DEALLOCATE PREPARE mq_stmt;

SET @mq_sql = IF(EXISTS (SELECT 1 FROM information_schema.table_constraints WHERE table_schema = DATABASE() AND table_name = 'orders' AND constraint_name = 'fk_orders_customer' AND constraint_type = 'FOREIGN KEY'), 'ALTER TABLE orders DROP FOREIGN KEY fk_orders_customer', 'DO 0');
PREPARE mq_stmt FROM @mq_sql;
EXECUTE mq_stmt;
DEALLOCATE PREPARE mq_stmt;

ALTER TABLE customers MODIFY COLUMN id bigint NOT NULL AUTO_INCREMENT;

-- TODO: convert existing values of customers.id (type: int32 -> int64); MODIFY COLUMN converts each value as it can.

ALTER TABLE customers MODIFY COLUMN name varchar(150) character set utf8mb4 COLLATE utf8mb4_bin NOT NULL;

ALTER TABLE customers MODIFY COLUMN email varchar(200) NULL;

SET @mq_sql = IF(EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = DATABASE() AND table_name = 'customers' AND column_name = 'notes'), 'ALTER TABLE customers RENAME COLUMN notes TO remarks', 'DO 0');
PREPARE mq_stmt FROM @mq_sql;
EXECUTE mq_stmt;
DEALLOCATE PREPARE mq_stmt;

ALTER TABLE customers MODIFY COLUMN remarks longtext NULL COMMENT 'Remarks about the customer.';

ALTER TABLE customers MODIFY COLUMN balance decimal(12,2) NOT NULL DEFAULT 10;

ALTER TABLE orders MODIFY COLUMN customer_id bigint NOT NULL;

-- TODO: convert existing values of orders.customer_id (type: int32 -> int64); MODIFY COLUMN converts each value as it can.

CREATE TABLE IF NOT EXISTS notes (
    id bigint NOT NULL AUTO_INCREMENT,
    customer_id bigint NULL,
    body longtext NOT NULL DEFAULT ('-'),
    CONSTRAINT pk_notes PRIMARY KEY (id),
    CONSTRAINT fk_notes_customer FOREIGN KEY (customer_id) REFERENCES customers (id) ON DELETE SET NULL,
    INDEX ix_notes_customer (customer_id),
    INDEX ix_notes_body (body(50))
);

SET @mq_sql = IF(NOT EXISTS (SELECT 1 FROM information_schema.table_constraints WHERE table_schema = DATABASE() AND table_name = 'customers' AND constraint_name = 'ck_customers_balance'), 'ALTER TABLE customers ADD CONSTRAINT ck_customers_balance CHECK (balance >= -100)', 'DO 0');
PREPARE mq_stmt FROM @mq_sql;
EXECUTE mq_stmt;
DEALLOCATE PREPARE mq_stmt;

SET @mq_sql = IF(NOT EXISTS (SELECT 1 FROM information_schema.table_constraints WHERE table_schema = DATABASE() AND table_name = 'orders' AND constraint_name = 'ck_orders_total'), 'ALTER TABLE orders ADD CONSTRAINT ck_orders_total CHECK (total >= 0)', 'DO 0');
PREPARE mq_stmt FROM @mq_sql;
EXECUTE mq_stmt;
DEALLOCATE PREPARE mq_stmt;

SET @mq_sql = IF(NOT EXISTS (SELECT 1 FROM information_schema.statistics WHERE table_schema = DATABASE() AND table_name = 'orders' AND index_name = 'ix_orders_placed_on'), 'CREATE INDEX ix_orders_placed_on ON orders (placed_on DESC, number)', 'DO 0');
PREPARE mq_stmt FROM @mq_sql;
EXECUTE mq_stmt;
DEALLOCATE PREPARE mq_stmt;

SET @mq_sql = IF(NOT EXISTS (SELECT 1 FROM information_schema.table_constraints WHERE table_schema = DATABASE() AND table_name = 'notes' AND constraint_name = 'fk_notes_customer'), 'ALTER TABLE notes ADD CONSTRAINT fk_notes_customer FOREIGN KEY (customer_id) REFERENCES customers (id) ON DELETE SET NULL', 'DO 0');
PREPARE mq_stmt FROM @mq_sql;
EXECUTE mq_stmt;
DEALLOCATE PREPARE mq_stmt;

SET @mq_sql = IF(NOT EXISTS (SELECT 1 FROM information_schema.table_constraints WHERE table_schema = DATABASE() AND table_name = 'orders' AND constraint_name = 'fk_orders_customer'), 'ALTER TABLE orders ADD CONSTRAINT fk_orders_customer FOREIGN KEY (customer_id) REFERENCES customers (id) ON DELETE CASCADE', 'DO 0');
PREPARE mq_stmt FROM @mq_sql;
EXECUTE mq_stmt;
DEALLOCATE PREPARE mq_stmt;

SET @mq_sql = IF(EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = DATABASE() AND table_name = 'customer_orders'), 'RENAME TABLE customer_orders TO customer_order_list', 'DO 0');
PREPARE mq_stmt FROM @mq_sql;
EXECUTE mq_stmt;
DEALLOCATE PREPARE mq_stmt;

CREATE OR REPLACE VIEW customer_order_totals (customer_id, order_count) AS
SELECT id, count(*) FROM customer_order_list GROUP BY id;

COMMIT;
