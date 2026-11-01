-- Migration 0002 of database pg (PostgreSQL 16): schema revision 1 to 2.
-- From state: ac8745c31853
-- To state: efcccacaab61
-- Facts: none
-- Written once by Maquettiste (sql-ddl/migration) from the schema diff. It is yours now: review it, adjust it and commit it.
-- Lines marked TODO need a decision the diff cannot make (data conversions, sequence restarts).

BEGIN;

-- TODO: slots.seats changes its native type (int4range -> int8range); check that every value converts before running this.

ALTER TABLE app.slots ALTER COLUMN seats TYPE int8range USING seats::text::int8range;

ALTER TABLE app.slots ALTER COLUMN stay TYPE datemultirange USING stay::datemultirange;

-- TODO: convert existing values of slots.stay (type: range -> multirange); the cast keeps each value as it is.

-- TODO: slots.free changes its native type (int4multirange -> int8multirange); check that every value converts before running this.

ALTER TABLE app.slots ALTER COLUMN free TYPE int8multirange USING free::text::int8multirange;

COMMIT;
