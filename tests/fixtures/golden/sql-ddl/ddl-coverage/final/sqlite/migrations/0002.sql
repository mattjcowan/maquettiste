-- Migration 0002 of database sqlite (SQLite): schema revision 1 to 2.
-- Written once by Maquettiste (sql-ddl/migration) from the schema diff. It is yours now: review it, adjust it and commit it.
-- Lines marked TODO need a decision the diff cannot make (data conversions, sequence restarts).
-- Run it so it stops at the first error: sqlite3 -bail (the sqliteBail parameter writes ".bail on" at the top for the sqlite3 shell).

-- SQLite rebuilds tables below: foreign keys are not enforced while they are rebuilt, and checked before the commit.
PRAGMA foreign_keys = OFF;

BEGIN;

DROP VIEW customer_order_totals;

ALTER TABLE order_lines RENAME TO order_items;

-- customers: the table comment changed; SQLite keeps comments in the schema script only.

-- Rebuild order_items (SQLite cannot change it in place: quantity type: int32 -> int64).

CREATE TABLE "_mq_new_order_items" (
    order_id integer NOT NULL,
    line_no integer NOT NULL,
    sku text NOT NULL,
    quantity integer NOT NULL,
    CONSTRAINT pk_order_lines PRIMARY KEY (order_id, line_no),
    CONSTRAINT fk_order_lines_order FOREIGN KEY (order_id) REFERENCES orders (id) ON DELETE CASCADE ON UPDATE CASCADE,
    CONSTRAINT ck_order_lines_quantity CHECK (quantity > 0)
);

INSERT INTO "_mq_new_order_items" (order_id, line_no, sku, quantity)
SELECT order_id, line_no, product_code, quantity FROM order_items;

DROP TABLE order_items;

PRAGMA legacy_alter_table = ON;
ALTER TABLE "_mq_new_order_items" RENAME TO order_items;
PRAGMA legacy_alter_table = OFF;

CREATE INDEX ix_order_lines_product ON order_items (sku);

-- SQLite dropped the triggers of order_items with the old table: recreate any other trigger on it by hand.

-- Rebuild customers (SQLite cannot change it in place: id type: int32 -> int64, email nullable: false -> true, balance default: 0 -> 10; defaultName: customers_balance_default -> customers_balance_dflt, keys and constraints).

CREATE TABLE "_mq_new_customers" (
    id INTEGER NOT NULL CONSTRAINT pk_customers PRIMARY KEY,
    code text NOT NULL,
    name text COLLATE NOCASE NOT NULL,
    email text NULL,
    -- Remarks about the customer.
    remarks text NULL,
    balance numeric NOT NULL DEFAULT 10,
    status text NOT NULL DEFAULT 'A',
    created_at text NOT NULL DEFAULT CURRENT_TIMESTAMP,
    balance_twice numeric GENERATED ALWAYS AS (balance * 2) STORED,
    -- Contact phone.
    phone text NULL,
    loyalty integer NULL,
    vip integer NOT NULL DEFAULT 0,
    CONSTRAINT uq_customers_mail UNIQUE (email),
    CONSTRAINT ck_customers_status CHECK (status IN ('A', 'I')),
    CONSTRAINT ck_customers_balance CHECK (balance >= -100)
);

INSERT INTO "_mq_new_customers" (id, code, name, email, remarks, balance, status, created_at)
SELECT id, code, name, email, notes, balance, status, created_at FROM customers;

DROP TABLE customers;

PRAGMA legacy_alter_table = ON;
ALTER TABLE "_mq_new_customers" RENAME TO customers;
PRAGMA legacy_alter_table = OFF;

CREATE INDEX ix_customers_name ON customers (name, created_at DESC);

CREATE INDEX ix_customers_active ON customers (status) WHERE status = 'A';

CREATE UNIQUE INDEX ix_customers_code_lookup ON customers (code);

CREATE INDEX ix_customers_email_lower ON customers ((lower(email)));

CREATE TRIGGER IF NOT EXISTS customers_touch AFTER UPDATE OF name ON customers BEGIN UPDATE customers SET status = status WHERE id = NEW.id; END;

-- SQLite dropped the triggers of customers with the old table; the model's are created again above: recreate any other trigger on it by hand.

-- Rebuild orders (SQLite cannot change it in place: customer_id type: int32 -> int64, keys and constraints).

CREATE TABLE "_mq_new_orders" (
    id INTEGER NOT NULL CONSTRAINT pk_orders_id PRIMARY KEY,
    number text NOT NULL,
    customer_id integer NOT NULL,
    total numeric NOT NULL DEFAULT 0,
    placed_on text NULL,
    CONSTRAINT uq_orders_customer_number UNIQUE (customer_id, number),
    CONSTRAINT fk_orders_customer FOREIGN KEY (customer_id) REFERENCES customers (id) ON DELETE CASCADE DEFERRABLE INITIALLY IMMEDIATE,
    CONSTRAINT ck_orders_total CHECK (total >= 0)
);

INSERT INTO "_mq_new_orders" (id, number, customer_id, total, placed_on)
SELECT id, number, customer_id, total, placed_on FROM orders;

DROP TABLE orders;

PRAGMA legacy_alter_table = ON;
ALTER TABLE "_mq_new_orders" RENAME TO orders;
PRAGMA legacy_alter_table = OFF;

CREATE INDEX ix_orders_placed_on ON orders (placed_on DESC, number);

-- SQLite dropped the triggers of orders with the old table: recreate any other trigger on it by hand.

CREATE TABLE notes (
    id INTEGER NOT NULL CONSTRAINT pk_notes PRIMARY KEY,
    customer_id integer NULL,
    body text NOT NULL DEFAULT '-',
    CONSTRAINT fk_notes_customer FOREIGN KEY (customer_id) REFERENCES customers (id) ON DELETE SET NULL
);
CREATE INDEX ix_notes_customer ON notes (customer_id);

DROP VIEW customer_orders;

CREATE VIEW customer_order_list AS
SELECT c.id, c.name, o.number FROM customers c JOIN orders o ON o.customer_id = c.id;

CREATE VIEW customer_order_totals (customer_id, order_count) AS
SELECT id, count(*) FROM customer_order_list GROUP BY id;

-- The rebuilds must leave every foreign key satisfied: a violation (PRAGMA foreign_key_check lists them) makes the trigger below
-- roll the whole migration back, whether or not the runner stops at the first error.
CREATE TEMP TABLE mq_foreign_key_check (violations INTEGER);
CREATE TEMP TRIGGER mq_foreign_key_check BEFORE INSERT ON mq_foreign_key_check WHEN NEW.violations > 0
BEGIN SELECT RAISE(ROLLBACK, 'foreign keys broken by the rebuilds (PRAGMA foreign_key_check lists them): the migration was rolled back'); END;
INSERT INTO mq_foreign_key_check SELECT count(*) FROM pragma_foreign_key_check;
DROP TABLE mq_foreign_key_check;

COMMIT;
PRAGMA foreign_keys = ON;
