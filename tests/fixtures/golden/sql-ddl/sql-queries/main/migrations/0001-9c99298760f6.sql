-- Migration 0001 of database main (PostgreSQL 16): schema revision 0 to 1.
-- From state: none
-- To state: 4674176872e2
-- Facts: none
-- Written once by Maquettiste (sql-ddl/migration) from the schema diff. It is yours now: review it, adjust it and commit it.
-- Lines marked TODO need a decision the diff cannot make (data conversions, sequence restarts).

BEGIN;

CREATE TABLE public.categories (
    id bigint NOT NULL,
    parent_id bigint NULL,
    name varchar(100) NOT NULL,
    CONSTRAINT pk_categories PRIMARY KEY (id),
    CONSTRAINT fk_categories_parent_id FOREIGN KEY (parent_id) REFERENCES public.categories (id)
);
COMMENT ON TABLE public.categories IS 'A category tree: each category names its parent, the top ones none.';

CREATE TABLE public.customers (
    id bigint NOT NULL,
    name varchar(120) NOT NULL,
    region varchar(40) NOT NULL,
    CONSTRAINT pk_customers PRIMARY KEY (id)
);

CREATE TABLE public.invoices (
    id bigint NOT NULL,
    customer_id bigint NOT NULL,
    total numeric(12,2) NOT NULL,
    status varchar(20) NOT NULL,
    issued_on date NOT NULL,
    due_on date NOT NULL,
    CONSTRAINT pk_invoices PRIMARY KEY (id),
    CONSTRAINT fk_invoices_customer_id FOREIGN KEY (customer_id) REFERENCES public.customers (id)
);

COMMIT;
