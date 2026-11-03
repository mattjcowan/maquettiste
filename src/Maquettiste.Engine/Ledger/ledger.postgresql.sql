CREATE SCHEMA IF NOT EXISTS {schema};
CREATE TABLE IF NOT EXISTS {schema}.contract (id smallint PRIMARY KEY DEFAULT 1 CHECK (id = 1),
    version integer NOT NULL);
INSERT INTO {schema}.contract (id, version) VALUES (1, 1) ON CONFLICT (id) DO NOTHING;
DO $ledger$ BEGIN
    IF (SELECT version FROM {schema}.contract WHERE id = 1) > 1 THEN
        RAISE EXCEPTION 'ledger contract version is newer than 1; use a newer writer';
    END IF;
END $ledger$;
CREATE TABLE IF NOT EXISTS {schema}.applied_fact (fact_id text PRIMARY KEY, content_hash text NOT NULL,
    applied_at timestamptz NOT NULL DEFAULT now(), applied_by text);
CREATE TABLE IF NOT EXISTS {schema}.applied_object (element_id text PRIMARY KEY, kind text NOT NULL,
    schema_name text, name text NOT NULL, content_hash text NOT NULL, revert_text text,
    applied_at timestamptz NOT NULL DEFAULT now());
