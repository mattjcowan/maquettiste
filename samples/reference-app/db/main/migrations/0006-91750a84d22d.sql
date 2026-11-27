-- Migration 0006 of database main (PostgreSQL 16): schema revision 5 to 6.
-- From state: 21cead18ac85
-- To state: 895eeaed777b
-- Facts: 01M8EJXAAG7XRNRR56A25953JZ
-- Written once by Maquettiste (sql-ddl/migration) from the schema diff. It is yours now: review it, adjust it and commit it.
-- Lines marked TODO need a decision the diff cannot make (data conversions, sequence restarts).
-- One transaction: the DDL and a migration ledger row for each migration fact the migration reaches (1).

BEGIN;

CREATE SCHEMA IF NOT EXISTS migration_ledger;
CREATE TABLE IF NOT EXISTS migration_ledger.contract (id smallint PRIMARY KEY DEFAULT 1 CHECK (id = 1),
    version integer NOT NULL);
INSERT INTO migration_ledger.contract (id, version) VALUES (1, 1) ON CONFLICT (id) DO NOTHING;
DO $ledger$ BEGIN
    IF (SELECT version FROM migration_ledger.contract WHERE id = 1) > 1 THEN
        RAISE EXCEPTION 'ledger contract version is newer than 1; use a newer writer';
    END IF;
END $ledger$;
CREATE TABLE IF NOT EXISTS migration_ledger.applied_fact (fact_id text PRIMARY KEY, content_hash text NOT NULL,
    applied_at timestamptz NOT NULL DEFAULT now(), applied_by text);
CREATE TABLE IF NOT EXISTS migration_ledger.applied_object (element_id text PRIMARY KEY, kind text NOT NULL,
    schema_name text, name text NOT NULL, content_hash text NOT NULL, revert_text text,
    applied_at timestamptz NOT NULL DEFAULT now());

INSERT INTO migration_ledger.applied_fact (fact_id, content_hash, applied_by) VALUES ('01M8EJXAAG7XRNRR56A25953JZ', '18088657f87d8072005057e9041c9cf43fa1ccb2e2f9fade3d859882370e99f9', 'sql-ddl') ON CONFLICT (fact_id) DO NOTHING;

-- Adopted by migration fact 01M8EJXAAG7XRNRR56A25953JZ: tables the earlier seed.sql made, now designed tables, and their reference rows. A database that ran it holds them; one built from the migrations alone gets them here.
CREATE TABLE IF NOT EXISTS northwind.product_statuses (
    code varchar(8) NOT NULL,
    label varchar(64) NOT NULL,
    description text NULL,
    CONSTRAINT pk_product_statuses PRIMARY KEY (code)
);
INSERT INTO northwind.product_statuses (code, label, description) VALUES
    ('DRAFT', 'Draft', 'Being set up; not sellable.'),
    ('ACTIVE', 'Active', 'Sellable and replenished.'),
    ('PHASEOUT', 'Phasing out', 'Sold from remaining stock, no longer replenished.'),
    ('DISC', 'Discontinued', 'No longer sold.'),
    ('BLOCK', 'Blocked', 'Temporarily unsellable, for example after a recall.')
ON CONFLICT (code) DO NOTHING;
CREATE TABLE IF NOT EXISTS northwind.shipment_statuses (
    code varchar(7) NOT NULL,
    label varchar(64) NOT NULL,
    description text NULL,
    CONSTRAINT pk_shipment_statuses PRIMARY KEY (code)
);
INSERT INTO northwind.shipment_statuses (code, label, description) VALUES
    ('PLAN', 'Planned', 'Created from an order, not yet picked.'),
    ('PICK', 'Picking', 'Being picked.'),
    ('PACK', 'Packed', 'Packed and labeled.'),
    ('SHIP', 'Shipped', 'Handed to the carrier or loaded on a route.'),
    ('TRANSIT', 'In transit', 'Moving, per carrier tracking.'),
    ('DLVD', 'Delivered', 'Delivered to the customer.'),
    ('EXCEPT', 'Delivery exception', 'Delayed, damaged or refused.'),
    ('CANCEL', 'Cancelled', 'Cancelled before shipping.')
ON CONFLICT (code) DO NOTHING;

-- Adopted by migration fact 01M8EJXAAG7XRNRR56A25953JZ: the constraints the earlier seed.sql gave columns holding reference codes, now declared by their tables. Each is added only where it is missing.
DO $$ BEGIN IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'fk_product_lifecycle_events_from_status_ref' AND conrelid = 'northwind.product_lifecycle_events'::regclass) THEN ALTER TABLE northwind.product_lifecycle_events ADD CONSTRAINT fk_product_lifecycle_events_from_status_ref FOREIGN KEY (from_status) REFERENCES northwind.product_statuses (code); END IF; END $$;
DO $$ BEGIN IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'fk_product_lifecycle_events_to_status_ref' AND conrelid = 'northwind.product_lifecycle_events'::regclass) THEN ALTER TABLE northwind.product_lifecycle_events ADD CONSTRAINT fk_product_lifecycle_events_to_status_ref FOREIGN KEY (to_status) REFERENCES northwind.product_statuses (code); END IF; END $$;
DO $$ BEGIN IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'fk_products_status_ref' AND conrelid = 'northwind.products'::regclass) THEN ALTER TABLE northwind.products ADD CONSTRAINT fk_products_status_ref FOREIGN KEY (status) REFERENCES northwind.product_statuses (code); END IF; END $$;
DO $$ BEGIN IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'fk_shipments_status_ref' AND conrelid = 'northwind.shipments'::regclass) THEN ALTER TABLE northwind.shipments ADD CONSTRAINT fk_shipments_status_ref FOREIGN KEY (status) REFERENCES northwind.shipment_statuses (code); END IF; END $$;

COMMIT;
