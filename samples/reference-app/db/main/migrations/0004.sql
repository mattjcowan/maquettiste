-- Migration 0004 of database main (PostgreSQL 16): schema revision 3 to 4.
-- Written once by Maquettiste (sql-ddl/migration) from the schema diff. It is yours now: review it, adjust it and commit it.
-- Lines marked TODO need a decision the diff cannot make (data conversions, SQLite table rebuilds).

BEGIN;

CREATE SCHEMA IF NOT EXISTS northwind;

CREATE TABLE northwind.remarks (
    id uuid NOT NULL,
    subject_type varchar(32) NOT NULL,
    subject_id uuid NOT NULL,
    body text NOT NULL,
    created_at timestamptz(6) NOT NULL DEFAULT CURRENT_TIMESTAMP,
    CONSTRAINT pk_remarks PRIMARY KEY (id)
);
CREATE INDEX ix_remarks_subject ON northwind.remarks (subject_type, subject_id);
COMMENT ON TABLE northwind.remarks IS 'Written by the order desk; read on the customer and order pages.';
COMMENT ON COLUMN northwind.remarks.id IS 'Remark key.';
COMMENT ON COLUMN northwind.remarks.subject_type IS 'What the remark is about, customer or sales-order.';
COMMENT ON COLUMN northwind.remarks.subject_id IS 'Key of the customer or sales order.';
COMMENT ON COLUMN northwind.remarks.body IS 'Remark text.';
COMMENT ON COLUMN northwind.remarks.created_at IS 'When the remark was written.';

COMMIT;
