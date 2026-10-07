-- Migration 0003 of database pg (PostgreSQL): schema revision 2 to 3.
-- Written once by Maquettiste (sql-ddl/migration) from the schema diff. It is yours now: review it, adjust it and commit it.
-- Lines marked TODO need a decision the diff cannot make (data conversions, sequence restarts).

BEGIN;

DROP FUNCTION IF EXISTS app.tenant_count;

-- Partition audit_events_2026_03 of audit_events is dropped with its rows.

DROP TABLE app.audit_events_2026_03;

DROP INDEX app.ix_documents_title_trgm;

ALTER TABLE app.bookings DROP CONSTRAINT uq_bookings_guest;

ALTER TABLE app.reservations RENAME CONSTRAINT ex_reservations_overlap TO ex_reservations_room_overlap;

COMMIT;
