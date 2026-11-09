-- Migration 0001 of database db1 (MySQL): schema revision 0 to 1.
-- From state: none
-- To state: 1d90caf2d6ed
-- Facts: none
-- Written once by Maquettiste (sql-ddl/migration) from the schema diff. It is yours now: review it, adjust it and commit it.
-- Lines marked TODO need a decision the diff cannot make (data conversions, sequence restarts).

-- MySQL commits each DDL statement on its own (an implicit commit), so BEGIN and COMMIT do not make this script atomic: run it once,
-- top to bottom, and restore a backup if a statement fails.
BEGIN;

CREATE SCHEMA IF NOT EXISTS app;

CREATE TABLE app.items (
    id char(36) NOT NULL,
    CONSTRAINT pk_items PRIMARY KEY (id)
);

CREATE TABLE app.folders (
    id char(36) NOT NULL,
    CONSTRAINT pk_folders PRIMARY KEY (id)
);

CREATE TABLE app.parts (
    id char(36) NOT NULL,
    uid char(36) NOT NULL,
    folder_id char(36) NULL,
    CONSTRAINT pk_parts PRIMARY KEY (id),
    CONSTRAINT uq_parts_uid UNIQUE (uid),
    CONSTRAINT fk_parts_item FOREIGN KEY (id) REFERENCES app.items (id),
    CONSTRAINT fk_parts_folder FOREIGN KEY (folder_id) REFERENCES app.folders (id)
);

CREATE TABLE app.assemblies (
    id char(36) NOT NULL,
    uid char(36) NOT NULL,
    folder_id char(36) NULL,
    CONSTRAINT pk_assemblies PRIMARY KEY (id),
    CONSTRAINT uq_assemblies_uid UNIQUE (uid),
    CONSTRAINT fk_assemblies_item FOREIGN KEY (id) REFERENCES app.items (id),
    CONSTRAINT fk_assemblies_folder FOREIGN KEY (folder_id) REFERENCES app.folders (id)
);

CREATE TABLE app.notes (
    id char(36) NOT NULL,
    CONSTRAINT pk_notes PRIMARY KEY (id)
);

CREATE TABLE app.comments (
    id char(36) NOT NULL,
    part_id char(36) NULL,
    assembly_id char(36) NULL,
    attached_type varchar(20) NULL,
    attached_id char(36) NULL,
    attached_uid char(36) NULL,
    item_id char(36) NULL,
    CONSTRAINT pk_comments PRIMARY KEY (id),
    CONSTRAINT fk_comments_part FOREIGN KEY (part_id) REFERENCES app.parts (id),
    CONSTRAINT fk_comments_assembly FOREIGN KEY (assembly_id) REFERENCES app.assemblies (id),
    CONSTRAINT fk_comments_item FOREIGN KEY (item_id) REFERENCES app.items (id),
    CONSTRAINT ck_comments_attached_to CHECK (part_id IS NULL OR assembly_id IS NULL),
    CONSTRAINT ck_comments_attached_type CHECK (attached_type IN ('Assembly', 'Part')),
    UNIQUE INDEX ux_comments_attached_uid (attached_uid)
);

CREATE TABLE app.links (
    id char(36) NOT NULL,
    relation_code varchar(30) NOT NULL,
    from_type varchar(20) NOT NULL,
    from_id char(36) NOT NULL,
    to_type varchar(20) NOT NULL,
    to_id char(36) NOT NULL,
    note varchar(200) NULL,
    CONSTRAINT pk_links PRIMARY KEY (id),
    CONSTRAINT uq_links UNIQUE (relation_code, from_type, from_id, to_type, to_id),
    CONSTRAINT ck_links_to_type CHECK (to_type IN ('Assembly', 'Part'))
);

COMMIT;
