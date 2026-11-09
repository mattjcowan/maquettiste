-- Migration 0001 of database db1 (Oracle): schema revision 0 to 1.
-- From state: none
-- To state: e378564f0253
-- Facts: none
-- Written once by Maquettiste (sql-ddl/migration) from the schema diff. It is yours now: review it, adjust it and commit it.
-- Lines marked TODO need a decision the diff cannot make (data conversions, sequence restarts).

-- Oracle commits each DDL statement: run the script once, top to bottom.

CREATE TABLE app.items (
    id raw(16) NOT NULL,
    CONSTRAINT pk_items PRIMARY KEY (id)
);

CREATE TABLE app.folders (
    id raw(16) NOT NULL,
    CONSTRAINT pk_folders PRIMARY KEY (id)
);

CREATE TABLE app.parts (
    id raw(16) NOT NULL,
    "uid" raw(16) NOT NULL,
    folder_id raw(16) NULL,
    CONSTRAINT pk_parts PRIMARY KEY (id),
    CONSTRAINT uq_parts_uid UNIQUE ("uid"),
    CONSTRAINT fk_parts_item FOREIGN KEY (id) REFERENCES app.items (id),
    CONSTRAINT fk_parts_folder FOREIGN KEY (folder_id) REFERENCES app.folders (id)
);

CREATE TABLE app.assemblies (
    id raw(16) NOT NULL,
    "uid" raw(16) NOT NULL,
    folder_id raw(16) NULL,
    CONSTRAINT pk_assemblies PRIMARY KEY (id),
    CONSTRAINT uq_assemblies_uid UNIQUE ("uid"),
    CONSTRAINT fk_assemblies_item FOREIGN KEY (id) REFERENCES app.items (id),
    CONSTRAINT fk_assemblies_folder FOREIGN KEY (folder_id) REFERENCES app.folders (id)
);

CREATE TABLE app.notes (
    id raw(16) NOT NULL,
    CONSTRAINT pk_notes PRIMARY KEY (id)
);

CREATE TABLE app.comments (
    id raw(16) NOT NULL,
    part_id raw(16) NULL,
    assembly_id raw(16) NULL,
    attached_type varchar2(20 char) NULL,
    attached_id raw(16) NULL,
    attached_uid raw(16) NULL,
    item_id raw(16) NULL,
    CONSTRAINT pk_comments PRIMARY KEY (id),
    CONSTRAINT fk_comments_part FOREIGN KEY (part_id) REFERENCES app.parts (id),
    CONSTRAINT fk_comments_assembly FOREIGN KEY (assembly_id) REFERENCES app.assemblies (id),
    CONSTRAINT fk_comments_item FOREIGN KEY (item_id) REFERENCES app.items (id),
    CONSTRAINT ck_comments_attached_to CHECK (part_id IS NULL OR assembly_id IS NULL),
    CONSTRAINT ck_comments_attached_type CHECK (attached_type IN ('Assembly', 'Part'))
);
CREATE UNIQUE INDEX ux_comments_attached_uid ON app.comments (attached_uid);

CREATE TABLE app.links (
    id raw(16) NOT NULL,
    relation_code varchar2(30 char) NOT NULL,
    from_type varchar2(20 char) NOT NULL,
    from_id raw(16) NOT NULL,
    to_type varchar2(20 char) NOT NULL,
    to_id raw(16) NOT NULL,
    note varchar2(200 char) NULL,
    CONSTRAINT pk_links PRIMARY KEY (id),
    CONSTRAINT uq_links UNIQUE (relation_code, from_type, from_id, to_type, to_id),
    CONSTRAINT ck_links_to_type CHECK (to_type IN ('Assembly', 'Part'))
);
