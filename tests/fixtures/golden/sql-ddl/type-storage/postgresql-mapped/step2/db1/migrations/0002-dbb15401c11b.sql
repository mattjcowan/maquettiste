-- Migration 0002 of database db1 (PostgreSQL): schema revision 1 to 2.
-- From state: f4ad19f3071b
-- To state: d2e3dc1156b9
-- Facts: none
-- Written once by Maquettiste (sql-ddl/migration) from the schema diff. It is yours now: review it, adjust it and commit it.
-- Lines marked TODO need a decision the diff cannot make (data conversions, sequence restarts).

BEGIN;

CREATE SCHEMA IF NOT EXISTS app;

ALTER TABLE app.comments DROP CONSTRAINT fk_comments_assembly;

ALTER TABLE app.comments DROP CONSTRAINT ck_comments_attached_to;

-- Data loss: dropping column comments.assembly_id deletes its values.

ALTER TABLE app.comments DROP COLUMN assembly_id;

ALTER TABLE app.comments ADD COLUMN tool_id uuid NULL;

CREATE TABLE app.tools (
    id uuid NOT NULL,
    CONSTRAINT pk_tools PRIMARY KEY (id)
);

ALTER TABLE app.comments ADD CONSTRAINT ck_comments_attached_to CHECK (part_id IS NULL OR tool_id IS NULL);

ALTER TABLE app.comments ADD CONSTRAINT fk_comments_tool FOREIGN KEY (tool_id) REFERENCES app.tools (id);

COMMIT;
