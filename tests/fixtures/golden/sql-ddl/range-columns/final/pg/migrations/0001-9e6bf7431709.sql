-- Migration 0001 of database pg (PostgreSQL 16): schema revision 0 to 1.
-- From state: none
-- To state: ac8745c31853
-- Facts: none
-- Written once by Maquettiste (sql-ddl/migration) from the schema diff. It is yours now: review it, adjust it and commit it.
-- Lines marked TODO need a decision the diff cannot make (data conversions, sequence restarts).

BEGIN;

CREATE SCHEMA IF NOT EXISTS app;

CREATE TABLE app.slots (
    id bigint NOT NULL,
    seats int4range NULL,
    stay daterange NULL,
    free int4multirange NULL,
    during tstzrange NOT NULL DEFAULT '[2026-01-01,2027-01-01)',
    CONSTRAINT pk_slots PRIMARY KEY (id)
);
CREATE INDEX ix_slots_during ON app.slots USING gist (during);

COMMIT;
