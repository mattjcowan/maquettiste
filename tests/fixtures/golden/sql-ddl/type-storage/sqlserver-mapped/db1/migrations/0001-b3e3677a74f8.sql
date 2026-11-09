-- Migration 0001 of database db1 (SQL Server): schema revision 0 to 1.
-- From state: none
-- To state: c05d08add7cd
-- Facts: none
-- Written once by Maquettiste (sql-ddl/migration) from the schema diff. It is yours now: review it, adjust it and commit it.
-- Lines marked TODO need a decision the diff cannot make (data conversions, sequence restarts).

-- One transaction across the batches: XACT_ABORT rolls it back on an error, and a batch that finds it gone turns execution off
-- (SET NOEXEC ON), so no later batch runs outside it.
SET XACT_ABORT ON;
SET QUOTED_IDENTIFIER ON;
BEGIN TRANSACTION;

IF SCHEMA_ID(N'app') IS NULL EXEC(N'CREATE SCHEMA app');

CREATE TABLE app.items (
    id uniqueidentifier NOT NULL,
    CONSTRAINT pk_items PRIMARY KEY (id)
);

CREATE TABLE app.folders (
    id uniqueidentifier NOT NULL,
    CONSTRAINT pk_folders PRIMARY KEY (id)
);

CREATE TABLE app.parts (
    id uniqueidentifier NOT NULL,
    uid uniqueidentifier NOT NULL,
    folder_id uniqueidentifier NULL,
    CONSTRAINT pk_parts PRIMARY KEY (id),
    CONSTRAINT uq_parts_uid UNIQUE (uid),
    CONSTRAINT fk_parts_item FOREIGN KEY (id) REFERENCES app.items (id),
    CONSTRAINT fk_parts_folder FOREIGN KEY (folder_id) REFERENCES app.folders (id)
);

CREATE TABLE app.assemblies (
    id uniqueidentifier NOT NULL,
    uid uniqueidentifier NOT NULL,
    folder_id uniqueidentifier NULL,
    CONSTRAINT pk_assemblies PRIMARY KEY (id),
    CONSTRAINT uq_assemblies_uid UNIQUE (uid),
    CONSTRAINT fk_assemblies_item FOREIGN KEY (id) REFERENCES app.items (id),
    CONSTRAINT fk_assemblies_folder FOREIGN KEY (folder_id) REFERENCES app.folders (id)
);

CREATE TABLE app.notes (
    id uniqueidentifier NOT NULL,
    CONSTRAINT pk_notes PRIMARY KEY (id)
);

CREATE TABLE app.comments (
    id uniqueidentifier NOT NULL,
    part_id uniqueidentifier NULL,
    assembly_id uniqueidentifier NULL,
    attached_type nvarchar(20) NULL,
    attached_id uniqueidentifier NULL,
    attached_uid uniqueidentifier NULL,
    item_id uniqueidentifier NULL,
    CONSTRAINT pk_comments PRIMARY KEY (id),
    CONSTRAINT fk_comments_part FOREIGN KEY (part_id) REFERENCES app.parts (id),
    CONSTRAINT fk_comments_assembly FOREIGN KEY (assembly_id) REFERENCES app.assemblies (id),
    CONSTRAINT fk_comments_item FOREIGN KEY (item_id) REFERENCES app.items (id),
    CONSTRAINT ck_comments_attached_to CHECK (part_id IS NULL OR assembly_id IS NULL),
    CONSTRAINT ck_comments_attached_type CHECK (attached_type IN ('Assembly', 'Part'))
);
CREATE UNIQUE INDEX ux_comments_attached_uid ON app.comments (attached_uid) WHERE attached_uid IS NOT NULL;

CREATE TABLE app.links (
    id uniqueidentifier NOT NULL,
    relation_code nvarchar(30) NOT NULL,
    from_type nvarchar(20) NOT NULL,
    from_id uniqueidentifier NOT NULL,
    to_type nvarchar(20) NOT NULL,
    to_id uniqueidentifier NOT NULL,
    note nvarchar(200) NULL,
    CONSTRAINT pk_links PRIMARY KEY (id),
    CONSTRAINT uq_links UNIQUE (relation_code, from_type, from_id, to_type, to_id),
    CONSTRAINT ck_links_to_type CHECK (to_type IN ('Assembly', 'Part'))
);

COMMIT TRANSACTION;
