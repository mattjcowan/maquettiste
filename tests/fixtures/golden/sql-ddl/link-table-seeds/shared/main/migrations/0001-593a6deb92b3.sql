-- Migration 0001 of database main (PostgreSQL): schema revision 0 to 1.
-- From state: none
-- To state: 86ddd50e3f08
-- Facts: none
-- Written once by Maquettiste (sql-ddl/migration) from the schema diff. It is yours now: review it, adjust it and commit it.
-- Lines marked TODO need a decision the diff cannot make (data conversions, sequence restarts).

BEGIN;

CREATE TABLE public.parts (
    id uuid NOT NULL,
    CONSTRAINT pk_parts PRIMARY KEY (id)
);

CREATE TABLE public.tools (
    id uuid NOT NULL,
    CONSTRAINT pk_tools PRIMARY KEY (id)
);

CREATE TABLE public.part_links (
    part_id uuid NOT NULL,
    tool_id uuid NOT NULL,
    rel_code varchar(1) NOT NULL,
    CONSTRAINT pk_part_links PRIMARY KEY (part_id, tool_id, rel_code),
    CONSTRAINT fk_part_links_part FOREIGN KEY (part_id) REFERENCES public.parts (id),
    CONSTRAINT fk_part_links_tool FOREIGN KEY (tool_id) REFERENCES public.tools (id)
);

COMMIT;
