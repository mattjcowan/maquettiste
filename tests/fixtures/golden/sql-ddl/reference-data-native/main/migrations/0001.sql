-- Migration 0001 of database main (PostgreSQL): schema revision 0 to 1.
-- Written once by Maquettiste (sql-ddl/migration) from the schema diff. It is yours now: review it, adjust it and commit it.
-- Lines marked TODO need a decision the diff cannot make (data conversions, SQLite table rebuilds).

BEGIN;

DO $$ BEGIN
    CREATE TYPE allergen_t AS ENUM ('gluten', 'nut', 'tree_nut');
EXCEPTION WHEN duplicate_object THEN NULL;
END $$;

DO $$ BEGIN
    CREATE TYPE unit_of_measure_t AS ENUM ('kg', 'g', 'pinch');
EXCEPTION WHEN duplicate_object THEN NULL;
END $$;

CREATE TABLE public.recipes (
    code varchar(32) NOT NULL,
    name varchar(120) NOT NULL,
    CONSTRAINT pk_recipes PRIMARY KEY (code)
);

CREATE TABLE public.ingredients (
    sku varchar(32) NOT NULL,
    name varchar(120) NOT NULL,
    default_unit unit_of_measure_t NOT NULL DEFAULT 'g',
    preferred_unit unit_of_measure_t NULL,
    substitute_sku varchar(32) NULL,
    CONSTRAINT pk_ingredients PRIMARY KEY (sku),
    CONSTRAINT fk_ingredients_substitute_sku FOREIGN KEY (substitute_sku) REFERENCES public.ingredients (sku)
);

CREATE TABLE public.recipe_ingredient (
    recipe_code varchar(32) NOT NULL,
    ingredient_sku varchar(32) NOT NULL,
    quantity numeric(12,3) NOT NULL,
    unit_of_measure unit_of_measure_t NOT NULL DEFAULT 'g',
    CONSTRAINT pk_recipe_ingredient PRIMARY KEY (recipe_code, ingredient_sku),
    CONSTRAINT fk_recipe_ingredient_recipe_code FOREIGN KEY (recipe_code) REFERENCES public.recipes (code) ON DELETE CASCADE,
    CONSTRAINT fk_recipe_ingredient_ingredient_sku FOREIGN KEY (ingredient_sku) REFERENCES public.ingredients (sku) ON DELETE CASCADE
);

COMMIT;
