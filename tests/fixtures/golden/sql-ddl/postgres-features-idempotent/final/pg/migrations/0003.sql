-- Migration 0003 of database pg (PostgreSQL): schema revision 2 to 3.
-- Written once by Maquettiste (sql-ddl/migration) from the schema diff. It is yours now: review it, adjust it and commit it.
-- Lines marked TODO need a decision the diff cannot make (data conversions, sequence restarts).

BEGIN;

DROP FUNCTION IF EXISTS app.tenant_count;

-- Partition audit_events_2026_03 of audit_events is dropped with its rows.

DROP TABLE IF EXISTS app.audit_events_2026_03;

DROP INDEX IF EXISTS app.ix_documents_title_trgm;

ALTER TABLE app.bookings DROP CONSTRAINT IF EXISTS uq_bookings_guest;

DO $$ BEGIN IF EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ex_reservations_overlap' AND conrelid = 'app.reservations'::regclass) THEN ALTER TABLE app.reservations RENAME CONSTRAINT ex_reservations_overlap TO ex_reservations_room_overlap; END IF; END $$;

COMMIT;
