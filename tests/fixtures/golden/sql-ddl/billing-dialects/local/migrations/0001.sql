-- Migration 0001 of database local (SQLite): schema revision 0 to 1.
-- Written once by Maquettiste (sql-ddl/migration) from the schema diff. It is yours now: review it, adjust it and commit it.
-- Lines marked TODO need a decision the diff cannot make (data conversions, sequence restarts).
-- Run it so it stops at the first error: sqlite3 -bail (the sqliteBail parameter writes ".bail on" at the top for the sqlite3 shell).

BEGIN;

-- SQLite has no domain types; email_address is not created (columns of this type are text).

CREATE TABLE customers (
    id text NOT NULL,
    name text NOT NULL,
    email text NOT NULL,
    customer_since text NULL,
    created_at text NOT NULL,
    updated_at text NULL,
    CONSTRAINT pk_customers PRIMARY KEY (id),
    CONSTRAINT uq_customers_email UNIQUE (email)
);

CREATE TABLE invoices (
    id text NOT NULL,
    number text NOT NULL,
    issued_on text NOT NULL,
    total_amount numeric NULL,
    total_currency text NULL,
    status integer NOT NULL DEFAULT 0,
    notes text NULL,
    created_at text NOT NULL,
    updated_at text NULL,
    deleted_at text NULL,
    reason text NULL,
    discriminator text NOT NULL,
    customer_id text NOT NULL,
    CONSTRAINT pk_invoices PRIMARY KEY (id),
    CONSTRAINT uq_invoices_number UNIQUE (number),
    CONSTRAINT fk_invoices_customer_id FOREIGN KEY (customer_id) REFERENCES customers (id) ON DELETE RESTRICT
);
CREATE INDEX ix_invoices_issued_on ON invoices (issued_on);

CREATE TABLE payments (
    id INTEGER NOT NULL CONSTRAINT pk_payments PRIMARY KEY,
    amount_amount numeric NOT NULL,
    amount_currency text NOT NULL,
    received_at text NOT NULL,
    reference text NULL,
    method text NULL,
    ledger_position blob NULL,
    created_at text NOT NULL,
    updated_at text NULL
);

CREATE TABLE products (
    id text NOT NULL,
    sku text NOT NULL,
    name text NOT NULL,
    list_price_amount numeric NULL,
    list_price_currency text NULL,
    deleted_at text NULL,
    CONSTRAINT pk_products PRIMARY KEY (id),
    CONSTRAINT uq_products_sku UNIQUE (sku)
);

CREATE TABLE invoice_lines (
    id text NOT NULL,
    quantity integer NOT NULL,
    unit_price_amount numeric NOT NULL,
    unit_price_currency text NOT NULL,
    description text NULL,
    invoice_id text NOT NULL,
    product_id text NOT NULL,
    CONSTRAINT pk_invoice_lines PRIMARY KEY (id),
    CONSTRAINT fk_invoice_lines_invoice_id FOREIGN KEY (invoice_id) REFERENCES invoices (id) ON DELETE CASCADE,
    CONSTRAINT fk_invoice_lines_product_id FOREIGN KEY (product_id) REFERENCES products (id) ON DELETE RESTRICT
);

CREATE TABLE payment_invoice (
    payments_id integer NOT NULL,
    invoices_id text NOT NULL,
    allocated_amount numeric NOT NULL,
    allocated_currency text NOT NULL,
    CONSTRAINT pk_payment_invoice PRIMARY KEY (payments_id, invoices_id),
    CONSTRAINT fk_payment_invoice_payments_id FOREIGN KEY (payments_id) REFERENCES payments (id) ON DELETE CASCADE,
    CONSTRAINT fk_payment_invoice_invoices_id FOREIGN KEY (invoices_id) REFERENCES invoices (id) ON DELETE CASCADE
);

CREATE TABLE invoice_notes (
    id text NOT NULL,
    invoice_id text NOT NULL,
    body text NOT NULL,
    created_at text NULL,
    CONSTRAINT pk_invoice_notes PRIMARY KEY (id)
);

CREATE TABLE customer_notes (
    id text NOT NULL,
    customer_id text NOT NULL,
    body text NOT NULL,
    created_at text NULL,
    CONSTRAINT pk_customer_notes PRIMARY KEY (id)
);

CREATE TABLE revenue_months (
    month text NOT NULL,
    invoice_count integer NOT NULL,
    revenue numeric NOT NULL,
    CONSTRAINT pk_revenue_months PRIMARY KEY (month)
);

-- SQLite has no stored routines; function invoice_total is not created.

CREATE TRIGGER invoices_keep_number BEFORE UPDATE OF number ON invoices FOR EACH ROW WHEN NEW.number <> OLD.number
BEGIN
    SELECT RAISE(ABORT, 'an invoice keeps its number');
END;

COMMIT;
