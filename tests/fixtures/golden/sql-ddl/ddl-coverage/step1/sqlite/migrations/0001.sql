-- Migration 0001 of database sqlite (SQLite): schema revision 0 to 1.
-- Written once by Maquettiste (sql-ddl/migration) from the schema diff. It is yours now: review it, adjust it and commit it.
-- Lines marked TODO need a decision the diff cannot make (data conversions, sequence restarts).
-- Run it so it stops at the first error: sqlite3 -bail (the sqliteBail parameter writes ".bail on" at the top for the sqlite3 shell).

BEGIN;

CREATE TABLE customers (
    id INTEGER NOT NULL CONSTRAINT pk_customers PRIMARY KEY,
    code text NOT NULL,
    name text COLLATE NOCASE NOT NULL,
    email text NOT NULL,
    -- Free-form notes.
    notes text NULL,
    balance numeric NOT NULL DEFAULT 0,
    status text NOT NULL DEFAULT 'A',
    created_at text NOT NULL DEFAULT CURRENT_TIMESTAMP,
    balance_twice numeric GENERATED ALWAYS AS (balance * 2) STORED,
    CONSTRAINT uq_customers_email UNIQUE (email),
    CONSTRAINT ck_customers_status CHECK (status IN ('A', 'I')),
    CONSTRAINT ck_customers_balance CHECK (balance >= 0)
);
CREATE INDEX ix_customers_name ON customers (name, created_at DESC);
CREATE INDEX ix_customers_active ON customers (status) WHERE status = 'A';
CREATE UNIQUE INDEX ix_customers_code ON customers (code);
CREATE INDEX ix_customers_email_lower ON customers ((lower(email)));

CREATE TABLE orders (
    id INTEGER NOT NULL CONSTRAINT pk_orders PRIMARY KEY,
    number text NOT NULL,
    customer_id integer NOT NULL,
    total numeric NOT NULL DEFAULT 0,
    placed_on text NULL,
    CONSTRAINT uq_orders_customer_number UNIQUE (customer_id, number),
    CONSTRAINT fk_orders_customer FOREIGN KEY (customer_id) REFERENCES customers (id) ON DELETE CASCADE DEFERRABLE INITIALLY DEFERRED
);
CREATE INDEX ix_orders_placed_on ON orders (placed_on DESC);

CREATE TABLE order_lines (
    order_id integer NOT NULL,
    line_no integer NOT NULL,
    product_code text NOT NULL,
    quantity integer NOT NULL,
    CONSTRAINT pk_order_lines PRIMARY KEY (order_id, line_no),
    CONSTRAINT fk_order_lines_order FOREIGN KEY (order_id) REFERENCES orders (id) ON DELETE CASCADE ON UPDATE CASCADE,
    CONSTRAINT ck_order_lines_quantity CHECK (quantity > 0)
);
CREATE INDEX ix_order_lines_product ON order_lines (product_code);

CREATE VIEW customer_orders AS
SELECT c.id, c.name, o.number FROM customers c JOIN orders o ON o.customer_id = c.id;

CREATE VIEW customer_order_totals (customer_id, order_count) AS
SELECT id, count(*) FROM customer_orders GROUP BY id;

CREATE VIEW active_customers AS
SELECT id, code, status FROM customers WHERE status = 'A';

CREATE TRIGGER IF NOT EXISTS customers_touch AFTER UPDATE OF name ON customers BEGIN UPDATE customers SET status = status WHERE id = NEW.id; END;

COMMIT;
