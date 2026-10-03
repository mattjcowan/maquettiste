-- Migration 0001 of database local (SQLite): schema revision 0 to 1.
-- Written once by Maquettiste (sql-ddl/migration) from the schema diff. It is yours now: review it, adjust it and commit it.
-- Lines marked TODO need a decision the diff cannot make (data conversions, sequence restarts).
-- Run it so it stops at the first error: sqlite3 -bail (the sqliteBail parameter writes ".bail on" at the top for the sqlite3 shell).

BEGIN;

CREATE TABLE recipes (
    code text NOT NULL,
    name text NOT NULL,
    CONSTRAINT pk_recipes PRIMARY KEY (code)
);

CREATE TABLE ingredients (
    sku text NOT NULL,
    name text NOT NULL,
    default_unit text NOT NULL DEFAULT 'g',
    preferred_unit text NULL,
    substitute_sku text NULL,
    CONSTRAINT pk_ingredients PRIMARY KEY (sku),
    CONSTRAINT fk_ingredients_substitute_sku FOREIGN KEY (substitute_sku) REFERENCES ingredients (sku)
);

CREATE TABLE recipe_ingredient (
    recipe_code text NOT NULL,
    ingredient_sku text NOT NULL,
    quantity numeric NOT NULL,
    unit_of_measure text NOT NULL DEFAULT 'g',
    CONSTRAINT pk_recipe_ingredient PRIMARY KEY (recipe_code, ingredient_sku),
    CONSTRAINT fk_recipe_ingredient_recipe_code FOREIGN KEY (recipe_code) REFERENCES recipes (code) ON DELETE CASCADE,
    CONSTRAINT fk_recipe_ingredient_ingredient_sku FOREIGN KEY (ingredient_sku) REFERENCES ingredients (sku) ON DELETE CASCADE
);

COMMIT;
