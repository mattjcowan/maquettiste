-- Migration 0001 of database db1 (SQLite): schema revision 0 to 1.
-- From state: none
-- To state: f7ae59f408da
-- Facts: none
-- Written once by Maquettiste (sql-ddl/migration) from the schema diff. It is yours now: review it, adjust it and commit it.
-- Lines marked TODO need a decision the diff cannot make (data conversions, sequence restarts).
-- Run it so it stops at the first error: sqlite3 -bail (the sqliteBail parameter writes ".bail on" at the top for the sqlite3 shell).

BEGIN;

CREATE TABLE items (
    id text NOT NULL,
    CONSTRAINT pk_items PRIMARY KEY (id)
);

CREATE TABLE folders (
    id text NOT NULL,
    CONSTRAINT pk_folders PRIMARY KEY (id)
);

CREATE TABLE parts (
    id text NOT NULL,
    uid text NOT NULL,
    folder_id text NULL,
    CONSTRAINT pk_parts PRIMARY KEY (id),
    CONSTRAINT uq_parts_uid UNIQUE (uid),
    CONSTRAINT fk_parts_item FOREIGN KEY (id) REFERENCES items (id),
    CONSTRAINT fk_parts_folder FOREIGN KEY (folder_id) REFERENCES folders (id)
);

CREATE TABLE assemblies (
    id text NOT NULL,
    uid text NOT NULL,
    folder_id text NULL,
    CONSTRAINT pk_assemblies PRIMARY KEY (id),
    CONSTRAINT uq_assemblies_uid UNIQUE (uid),
    CONSTRAINT fk_assemblies_item FOREIGN KEY (id) REFERENCES items (id),
    CONSTRAINT fk_assemblies_folder FOREIGN KEY (folder_id) REFERENCES folders (id)
);

CREATE TABLE notes (
    id text NOT NULL,
    CONSTRAINT pk_notes PRIMARY KEY (id)
);

CREATE TABLE comments (
    id text NOT NULL,
    part_id text NULL,
    assembly_id text NULL,
    attached_type text NULL,
    attached_id text NULL,
    attached_uid text NULL,
    item_id text NULL,
    CONSTRAINT pk_comments PRIMARY KEY (id),
    CONSTRAINT fk_comments_part FOREIGN KEY (part_id) REFERENCES parts (id),
    CONSTRAINT fk_comments_assembly FOREIGN KEY (assembly_id) REFERENCES assemblies (id),
    CONSTRAINT fk_comments_item FOREIGN KEY (item_id) REFERENCES items (id),
    CONSTRAINT ck_comments_attached_to CHECK (part_id IS NULL OR assembly_id IS NULL),
    CONSTRAINT ck_comments_attached_type CHECK (attached_type IN ('Assembly', 'Part'))
);
CREATE UNIQUE INDEX ux_comments_attached_uid ON comments (attached_uid) WHERE attached_uid IS NOT NULL;

CREATE TABLE links (
    id text NOT NULL,
    relation_code text NOT NULL,
    from_type text NOT NULL,
    from_id text NOT NULL,
    to_type text NOT NULL,
    to_id text NOT NULL,
    note text NULL,
    CONSTRAINT pk_links PRIMARY KEY (id),
    CONSTRAINT uq_links UNIQUE (relation_code, from_type, from_id, to_type, to_id),
    CONSTRAINT ck_links_to_type CHECK (to_type IN ('Assembly', 'Part'))
);

COMMIT;
