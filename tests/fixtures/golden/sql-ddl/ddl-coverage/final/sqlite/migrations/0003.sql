-- Migration 0003 of database sqlite (SQLite): schema revision 2 to 3.
-- Written once by Maquettiste (sql-ddl/migration) from the schema diff. It is yours now: review it, adjust it and commit it.
-- Lines marked TODO need a decision the diff cannot make (data conversions, sequence restarts).
-- Run it so it stops at the first error: sqlite3 -bail (the sqliteBail parameter writes ".bail on" at the top for the sqlite3 shell).

-- SQLite rebuilds tables below: foreign keys are not enforced while they are rebuilt, and checked before the commit.
PRAGMA foreign_keys = OFF;

BEGIN;

DROP VIEW active_customers;

DROP VIEW customer_order_totals;

DROP VIEW customer_order_list;

-- Data loss: dropping table notes deletes its rows.

DROP TABLE notes;

-- Rebuild customers (SQLite cannot change it in place: keys and constraints).

-- Data loss: dropping column customers.phone deletes its values.

-- Data loss: dropping column customers.loyalty deletes its values.

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
    vip integer NOT NULL DEFAULT 0,
    CONSTRAINT ck_customers_balance CHECK (balance >= -100)
);

INSERT INTO "_mq_new_customers" (id, code, name, email, remarks, balance, status, created_at, vip)
SELECT id, code, name, email, remarks, balance, status, created_at, vip FROM customers;

DROP TABLE customers;

PRAGMA legacy_alter_table = ON;
ALTER TABLE "_mq_new_customers" RENAME TO customers;
PRAGMA legacy_alter_table = OFF;

CREATE INDEX ix_customers_name ON customers (name, created_at DESC);

CREATE UNIQUE INDEX ix_customers_code_lookup ON customers (code);

CREATE INDEX ix_customers_email_lower ON customers ((lower(email)));

CREATE TRIGGER IF NOT EXISTS customers_touch AFTER UPDATE OF name ON customers BEGIN UPDATE customers SET status = status WHERE id = NEW.id; END;

-- SQLite dropped the triggers of customers with the old table; the model's are created again above: recreate any other trigger on it by hand.

-- Rebuild orders (SQLite cannot change it in place: keys and constraints).

CREATE TABLE "_mq_new_orders" (
    id INTEGER NOT NULL CONSTRAINT pk_orders_id PRIMARY KEY,
    number text NOT NULL,
    customer_id integer NOT NULL,
    total numeric NOT NULL DEFAULT 0,
    placed_on text NULL,
    CONSTRAINT uq_orders_customer_number UNIQUE (customer_id, number),
    CONSTRAINT fk_orders_customer FOREIGN KEY (customer_id) REFERENCES customers (id) DEFERRABLE INITIALLY IMMEDIATE,
    CONSTRAINT ck_orders_total CHECK (total >= 0)
);

INSERT INTO "_mq_new_orders" (id, number, customer_id, total, placed_on)
SELECT id, number, customer_id, total, placed_on FROM orders;

DROP TABLE orders;

PRAGMA legacy_alter_table = ON;
ALTER TABLE "_mq_new_orders" RENAME TO orders;
PRAGMA legacy_alter_table = OFF;

-- SQLite dropped the triggers of orders with the old table: recreate any other trigger on it by hand.

-- The rebuilds must leave every foreign key satisfied: a violation (PRAGMA foreign_key_check lists them) makes the trigger below
-- roll the whole migration back, whether or not the runner stops at the first error.
CREATE TEMP TABLE mq_foreign_key_check (violations INTEGER);
CREATE TEMP TRIGGER mq_foreign_key_check BEFORE INSERT ON mq_foreign_key_check WHEN NEW.violations > 0
BEGIN SELECT RAISE(ROLLBACK, 'foreign keys broken by the rebuilds (PRAGMA foreign_key_check lists them): the migration was rolled back'); END;
INSERT INTO mq_foreign_key_check SELECT count(*) FROM pragma_foreign_key_check;
DROP TABLE mq_foreign_key_check;

COMMIT;
PRAGMA foreign_keys = ON;
