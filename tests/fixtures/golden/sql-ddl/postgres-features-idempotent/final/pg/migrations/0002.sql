-- Migration 0002 of database pg (PostgreSQL): schema revision 1 to 2.
-- Written once by Maquettiste (sql-ddl/migration) from the schema diff. It is yours now: review it, adjust it and commit it.
-- Lines marked TODO need a decision the diff cannot make (data conversions, sequence restarts).

BEGIN;

DROP VIEW IF EXISTS app.my_documents;

-- Views dropped and created again lose the privileges granted on them: the SQL objects that depend on them run again below; a grant that is not one of them is lost.

DROP FUNCTION IF EXISTS app.current_tenant;

ALTER TABLE app.documents SET (fillfactor = 80);

ALTER TABLE app.documents RESET (autovacuum_enabled);

CREATE TABLE IF NOT EXISTS app.audit_events_2026_03 PARTITION OF app.audit_events FOR VALUES FROM ('2026-03-01') TO ('2026-04-01') WITH (autovacuum_vacuum_scale_factor = 0.03, fillfactor = 70);

ALTER TABLE IF EXISTS app.audit_events_2026_02 RENAME TO audit_events_2026_02_full;

ALTER TABLE app.audit_events DETACH PARTITION app.audit_events_2026_02_full;

ALTER TABLE app.audit_events ATTACH PARTITION app.audit_events_2026_02_full FOR VALUES FROM ('2026-02-01') TO ('2026-03-01');

ALTER TABLE IF EXISTS app.audit_events_default RENAME TO audit_events_rest;

ALTER TABLE IF EXISTS app.audit_events_2026_01 RESET (autovacuum_enabled);

ALTER TABLE IF EXISTS app.audit_events_2026_02_full RESET (autovacuum_enabled);

ALTER TABLE IF EXISTS app.audit_events_rest RESET (autovacuum_enabled);

ALTER TABLE IF EXISTS app.audit_events_2026_03 RESET (autovacuum_enabled);

ALTER TABLE app.reservations DROP CONSTRAINT IF EXISTS ex_reservations_overlap;

ALTER INDEX IF EXISTS app.ix_documents_created SET (pages_per_range = 64);

DO $$ BEGIN IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'uq_bookings_guest' AND conrelid = 'app.bookings'::regclass) THEN ALTER TABLE app.bookings ADD CONSTRAINT uq_bookings_guest UNIQUE (guest_id, stay WITHOUT OVERLAPS); END IF; END $$;

DO $$ BEGIN IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ex_reservations_overlap' AND conrelid = 'app.reservations'::regclass) THEN ALTER TABLE app.reservations ADD CONSTRAINT ex_reservations_overlap EXCLUDE USING gist (room_id WITH =, during WITH &&) WHERE (NOT cancelled) DEFERRABLE INITIALLY DEFERRED; END IF; END $$;

CREATE OR REPLACE FUNCTION app.current_tenant()
RETURNS bigint
LANGUAGE sql
IMMUTABLE
SET work_mem = '64MB'
AS $$
SELECT 1::bigint
$$;

CREATE OR REPLACE VIEW app.my_documents WITH (security_invoker = true) AS
SELECT id, title FROM app.documents WHERE tenant_id = app.current_tenant();

GRANT SELECT ON app.my_documents TO PUBLIC;

COMMIT;
