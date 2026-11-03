-- Migration 0001 of database main (PostgreSQL 16): schema revision 0 to 1.
-- From state: none
-- To state: aa58320ebfa4
-- Facts: none
-- Written once by Maquettiste (sql-ddl/migration) from the schema diff. It is yours now: review it, adjust it and commit it.
-- Lines marked TODO need a decision the diff cannot make (data conversions, sequence restarts).

BEGIN;

CREATE SCHEMA IF NOT EXISTS billing;

CREATE TABLE billing.customers (
    id uuid NOT NULL,
    name varchar(120) NOT NULL,
    email varchar(254) NOT NULL,
    CONSTRAINT pk_customers PRIMARY KEY (id),
    CONSTRAINT uq_customers_email UNIQUE (email)
);

CREATE TABLE billing.invoices (
    id uuid NOT NULL,
    number varchar(32) NOT NULL,
    issued_on date NULL,
    total numeric(12,2) NOT NULL,
    customer_id uuid NOT NULL,
    CONSTRAINT pk_invoices PRIMARY KEY (id),
    CONSTRAINT uq_invoices_number UNIQUE (number),
    CONSTRAINT fk_invoices_customer FOREIGN KEY (customer_id) REFERENCES billing.customers (id)
);

COMMIT;
