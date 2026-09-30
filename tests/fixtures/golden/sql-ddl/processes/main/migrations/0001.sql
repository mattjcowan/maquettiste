-- Migration 0001 of database main (PostgreSQL 16): schema revision 0 to 1.
-- Written once by Maquettiste (sql-ddl/migration) from the schema diff. It is yours now: review it, adjust it and commit it.
-- Lines marked TODO need a decision the diff cannot make (data conversions, SQLite table rebuilds).

BEGIN;

CREATE TABLE public.sales_orders (
    id uuid NOT NULL,
    status integer NOT NULL DEFAULT 0,
    configuration jsonb NULL,
    total numeric(18,2) NOT NULL,
    credit_limit numeric(18,2) NOT NULL,
    CONSTRAINT pk_sales_orders PRIMARY KEY (id)
);

CREATE TABLE public.sales_order_histories (
    id uuid NOT NULL,
    sales_order_id uuid NOT NULL,
    from_state varchar(255) NULL,
    to_state varchar(255) NOT NULL,
    at timestamptz(6) NOT NULL,
    CONSTRAINT pk_sales_order_histories PRIMARY KEY (id)
);

CREATE TABLE public.purchase_requests (
    id uuid NOT NULL,
    amount numeric(18,2) NOT NULL,
    cost_centre varchar(255) NULL,
    CONSTRAINT pk_purchase_requests PRIMARY KEY (id)
);

CREATE TABLE public.process_instances (
    id uuid NOT NULL,
    process varchar(255) NOT NULL,
    subject uuid NULL,
    configuration jsonb NOT NULL,
    context jsonb NULL,
    updated_at timestamptz(6) NOT NULL,
    CONSTRAINT pk_process_instances PRIMARY KEY (id)
);

CREATE TABLE public.gate_signatures (
    id uuid NOT NULL,
    instance varchar(255) NOT NULL,
    gate varchar(255) NOT NULL,
    sequence bigint NOT NULL,
    signer varchar(255) NOT NULL,
    actor varchar(255) NOT NULL,
    meaning varchar(255) NULL,
    reason text NULL,
    outcome varchar(255) NOT NULL,
    at timestamptz(6) NOT NULL,
    CONSTRAINT pk_gate_signatures PRIMARY KEY (id)
);

COMMIT;
