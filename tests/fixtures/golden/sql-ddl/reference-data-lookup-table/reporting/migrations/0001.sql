-- Migration 0001 of database reporting (SQL Server): schema revision 0 to 1.
-- Written once by Maquettiste (sql-ddl/migration) from the schema diff. It is yours now: review it, adjust it and commit it.
-- Lines marked TODO need a decision the diff cannot make (data conversions, SQLite table rebuilds).

SET XACT_ABORT ON;
BEGIN TRANSACTION;

CREATE TABLE dbo.recipes (
    code nvarchar(32) NOT NULL,
    name nvarchar(120) NOT NULL,
    CONSTRAINT pk_recipes PRIMARY KEY (code)
);

CREATE TABLE dbo.ingredients (
    sku nvarchar(32) NOT NULL,
    name nvarchar(120) NOT NULL,
    default_unit nvarchar(8) NOT NULL CONSTRAINT df_ingredients_default_unit DEFAULT N'g',
    preferred_unit nvarchar(8) NULL,
    substitute_sku nvarchar(32) NULL,
    CONSTRAINT pk_ingredients PRIMARY KEY (sku),
    CONSTRAINT fk_ingredients_substitute_sku FOREIGN KEY (substitute_sku) REFERENCES dbo.ingredients (sku)
);

CREATE TABLE dbo.recipe_ingredient (
    recipe_code nvarchar(32) NOT NULL,
    ingredient_sku nvarchar(32) NOT NULL,
    quantity decimal(12,3) NOT NULL,
    unit_of_measure nvarchar(8) NOT NULL CONSTRAINT df_recipe_ingredient_unit_of_measure DEFAULT N'g',
    CONSTRAINT pk_recipe_ingredient PRIMARY KEY (recipe_code, ingredient_sku),
    CONSTRAINT fk_recipe_ingredient_recipe_code FOREIGN KEY (recipe_code) REFERENCES dbo.recipes (code) ON DELETE CASCADE,
    CONSTRAINT fk_recipe_ingredient_ingredient_sku FOREIGN KEY (ingredient_sku) REFERENCES dbo.ingredients (sku) ON DELETE CASCADE
);

COMMIT TRANSACTION;
