-- Migration 0002 of database main (PostgreSQL 16): schema revision 1 to 2.
-- Written once by Maquettiste (sql-ddl/migration) from the schema diff. It is yours now: review it, adjust it and commit it.
-- Lines marked TODO need a decision the diff cannot make (data conversions, SQLite table rebuilds).
--
-- Hand-edited: ProductStatus and ShipmentStatus moved from enum lookup tables (integer ids) to reference types (codes).
-- products.status and shipments.status held lookup ids, so each is converted to the code of its old lookup row before
-- the old lookup tables are dropped. seed.sql then recreates product_statuses and shipment_statuses keyed by code and
-- adds the foreign keys to them. product_lifecycle_events.from_status and to_status already held codes (varchar(8)),
-- so their type change is a no-op cast.

BEGIN;

ALTER TABLE northwind.products DROP CONSTRAINT fk_products_status;

ALTER TABLE northwind.shipments DROP CONSTRAINT fk_shipments_status;

-- products.status: lookup id -> code.
ALTER TABLE northwind.products ALTER COLUMN status DROP DEFAULT;

ALTER TABLE northwind.products ALTER COLUMN status TYPE varchar(8) USING status::varchar(8);

UPDATE northwind.products AS p
SET status = s.code
FROM northwind.product_statuses AS s
WHERE p.status = s.id::varchar(8);

ALTER TABLE northwind.products ALTER COLUMN status SET DEFAULT 'DRAFT';

-- shipments.status: lookup id -> code.
ALTER TABLE northwind.shipments ALTER COLUMN status DROP DEFAULT;

ALTER TABLE northwind.shipments ALTER COLUMN status TYPE varchar(7) USING status::varchar(7);

UPDATE northwind.shipments AS sh
SET status = s.code
FROM northwind.shipment_statuses AS s
WHERE sh.status = s.id::varchar(7);

ALTER TABLE northwind.shipments ALTER COLUMN status SET DEFAULT 'PLAN';

-- The old lookup tables are no longer needed once every row holds a code; seed.sql recreates them keyed by code.
DROP TABLE northwind.product_statuses;

DROP TABLE northwind.shipment_statuses;

-- Already codes: the type change keeps every value.
ALTER TABLE northwind.product_lifecycle_events ALTER COLUMN from_status TYPE varchar(8) USING from_status::varchar(8);

ALTER TABLE northwind.product_lifecycle_events ALTER COLUMN to_status TYPE varchar(8) USING to_status::varchar(8);

COMMIT;
