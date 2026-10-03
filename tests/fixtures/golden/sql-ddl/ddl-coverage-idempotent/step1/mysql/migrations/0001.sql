-- Migration 0001 of database mysql (MySQL): schema revision 0 to 1.
-- Written once by Maquettiste (sql-ddl/migration) from the schema diff. It is yours now: review it, adjust it and commit it.
-- Lines marked TODO need a decision the diff cannot make (data conversions, sequence restarts).

-- MySQL commits each DDL statement on its own (an implicit commit), so BEGIN and COMMIT do not make this script atomic: run it once,
-- top to bottom, and restore a backup if a statement fails.
BEGIN;

CREATE TABLE IF NOT EXISTS customers (
    id int NOT NULL AUTO_INCREMENT,
    code char(10) NOT NULL,
    name varchar(100) character set utf8mb4 COLLATE utf8mb4_bin NOT NULL,
    email varchar(200) NOT NULL,
    notes longtext NULL COMMENT 'Free-form notes.',
    balance decimal(12,2) NOT NULL DEFAULT 0,
    status varchar(1) NOT NULL DEFAULT 'A',
    created_at datetime(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    balance_twice decimal(14,2) GENERATED ALWAYS AS (balance * 2) STORED,
    CONSTRAINT pk_customers PRIMARY KEY (id),
    CONSTRAINT uq_customers_email UNIQUE (email),
    CONSTRAINT ck_customers_status CHECK (status IN ('A', 'I')),
    CONSTRAINT ck_customers_balance CHECK (balance >= 0),
    INDEX ix_customers_name (name, created_at DESC),
    UNIQUE INDEX ix_customers_code (code) USING BTREE
) AUTO_INCREMENT = 100 COMMENT = 'Customers who place orders.';

CREATE TABLE IF NOT EXISTS orders (
    id bigint NOT NULL AUTO_INCREMENT,
    number varchar(20) NOT NULL,
    customer_id int NOT NULL,
    total decimal(12,2) NOT NULL DEFAULT 0,
    placed_on date NULL,
    CONSTRAINT pk_orders PRIMARY KEY (id),
    CONSTRAINT uq_orders_customer_number UNIQUE (customer_id, number),
    CONSTRAINT fk_orders_customer FOREIGN KEY (customer_id) REFERENCES customers (id) ON DELETE CASCADE,
    INDEX ix_orders_placed_on (placed_on DESC)
);

CREATE TABLE IF NOT EXISTS order_lines (
    order_id bigint NOT NULL,
    line_no int NOT NULL,
    product_code char(20) NOT NULL,
    quantity int NOT NULL,
    CONSTRAINT pk_order_lines PRIMARY KEY (order_id, line_no),
    CONSTRAINT fk_order_lines_order FOREIGN KEY (order_id) REFERENCES orders (id) ON DELETE CASCADE ON UPDATE CASCADE,
    CONSTRAINT ck_order_lines_quantity CHECK (quantity > 0),
    INDEX ix_order_lines_product (product_code)
);

CREATE OR REPLACE VIEW customer_orders AS
SELECT c.id, c.name, o.number FROM customers c JOIN orders o ON o.customer_id = c.id;

CREATE OR REPLACE VIEW customer_order_totals (customer_id, order_count) AS
SELECT id, count(*) FROM customer_orders GROUP BY id;

CREATE OR REPLACE VIEW active_customers AS
SELECT id, code, status FROM customers WHERE status = 'A'
WITH CHECK OPTION;

COMMIT;
