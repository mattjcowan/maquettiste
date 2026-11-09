-- Migration 0001 of database db1 (PostgreSQL): schema revision 0 to 1.
-- From state: none
-- To state: f4ad19f3071b
-- Facts: none
-- Written once by Maquettiste (sql-ddl/migration) from the schema diff. It is yours now: review it, adjust it and commit it.
-- Lines marked TODO need a decision the diff cannot make (data conversions, sequence restarts).

BEGIN;

CREATE SCHEMA IF NOT EXISTS app;

CREATE TABLE app.items (
    id uuid NOT NULL,
    CONSTRAINT pk_items PRIMARY KEY (id)
);

CREATE TABLE app.folders (
    id uuid NOT NULL,
    CONSTRAINT pk_folders PRIMARY KEY (id)
);

CREATE TABLE app.parts (
    id uuid NOT NULL,
    uid uuid NOT NULL,
    folder_id uuid NULL,
    CONSTRAINT pk_parts PRIMARY KEY (id),
    CONSTRAINT uq_parts_uid UNIQUE (uid),
    CONSTRAINT fk_parts_item FOREIGN KEY (id) REFERENCES app.items (id),
    CONSTRAINT fk_parts_folder FOREIGN KEY (folder_id) REFERENCES app.folders (id)
);

CREATE TABLE app.assemblies (
    id uuid NOT NULL,
    uid uuid NOT NULL,
    folder_id uuid NULL,
    CONSTRAINT pk_assemblies PRIMARY KEY (id),
    CONSTRAINT uq_assemblies_uid UNIQUE (uid),
    CONSTRAINT fk_assemblies_item FOREIGN KEY (id) REFERENCES app.items (id),
    CONSTRAINT fk_assemblies_folder FOREIGN KEY (folder_id) REFERENCES app.folders (id)
);

CREATE TABLE app.notes (
    id uuid NOT NULL,
    CONSTRAINT pk_notes PRIMARY KEY (id)
);

CREATE TABLE app.comments (
    id uuid NOT NULL,
    part_id uuid NULL,
    assembly_id uuid NULL,
    attached_type varchar(20) NULL,
    attached_id uuid NULL,
    attached_uid uuid NULL,
    item_id uuid NULL,
    CONSTRAINT pk_comments PRIMARY KEY (id),
    CONSTRAINT fk_comments_part FOREIGN KEY (part_id) REFERENCES app.parts (id),
    CONSTRAINT fk_comments_assembly FOREIGN KEY (assembly_id) REFERENCES app.assemblies (id),
    CONSTRAINT fk_comments_item FOREIGN KEY (item_id) REFERENCES app.items (id),
    CONSTRAINT ck_comments_attached_to CHECK (part_id IS NULL OR assembly_id IS NULL),
    CONSTRAINT ck_comments_attached_type CHECK (attached_type IN ('Assembly', 'Part'))
);
CREATE UNIQUE INDEX ux_comments_attached_uid ON app.comments (attached_uid) WHERE attached_uid IS NOT NULL;

CREATE TABLE app.comment_links (
    comment_id uuid NOT NULL,
    target_type varchar(20) NOT NULL,
    target_id uuid NOT NULL,
    note varchar(200) NULL,
    CONSTRAINT pk_comment_links PRIMARY KEY (comment_id, target_type, target_id),
    CONSTRAINT fk_comment_links_comment FOREIGN KEY (comment_id) REFERENCES app.comments (id),
    CONSTRAINT ck_comment_links_target_type CHECK (target_type IN ('Assembly', 'Part'))
);

CREATE TABLE app.comment_parts (
    comment_id uuid NOT NULL,
    part_id uuid NOT NULL,
    CONSTRAINT pk_comment_parts PRIMARY KEY (comment_id, part_id),
    CONSTRAINT fk_comment_parts_comment FOREIGN KEY (comment_id) REFERENCES app.comments (id),
    CONSTRAINT fk_comment_parts_part FOREIGN KEY (part_id) REFERENCES app.parts (id)
);

CREATE TABLE app.comment_assemblies (
    comment_id uuid NOT NULL,
    assembly_id uuid NOT NULL,
    CONSTRAINT pk_comment_assemblies PRIMARY KEY (comment_id, assembly_id),
    CONSTRAINT fk_comment_assemblies_comment FOREIGN KEY (comment_id) REFERENCES app.comments (id),
    CONSTRAINT fk_comment_assemblies_assembly FOREIGN KEY (assembly_id) REFERENCES app.assemblies (id)
);

CREATE TABLE app.links (
    id uuid NOT NULL,
    relation_code varchar(30) NOT NULL,
    from_type varchar(20) NOT NULL,
    from_id uuid NOT NULL,
    to_type varchar(20) NOT NULL,
    to_id uuid NOT NULL,
    note varchar(200) NULL,
    CONSTRAINT pk_links PRIMARY KEY (id),
    CONSTRAINT uq_links UNIQUE (relation_code, from_type, from_id, to_type, to_id),
    CONSTRAINT ck_links_to_type CHECK (to_type IN ('Assembly', 'Part'))
);

CREATE TABLE app.id_links (
    from_id uuid NOT NULL,
    to_id uuid NOT NULL,
    CONSTRAINT pk_id_links PRIMARY KEY (from_id, to_id)
);

COMMIT;
