-- Migration 0002 of database sqlserver (SQL Server): schema revision 1 to 2.
-- Written once by Maquettiste (sql-ddl/migration) from the schema diff. It is yours now: review it, adjust it and commit it.
-- Lines marked TODO need a decision the diff cannot make (data conversions, sequence restarts).

-- One transaction across the batches: XACT_ABORT rolls it back on an error, and a batch that finds it gone turns execution off
-- (SET NOEXEC ON), so no later batch runs outside it.
SET XACT_ABORT ON;
SET QUOTED_IDENTIFIER ON;
BEGIN TRANSACTION;

DROP VIEW sales.customer_order_totals;

IF SCHEMA_ID(N'sales') IS NULL EXEC(N'CREATE SCHEMA sales');

BEGIN DECLARE @mq_rc1 int; EXEC @mq_rc1 = sp_rename N'sales.order_lines', N'order_items', N'OBJECT'; IF @mq_rc1 <> 0 THROW 50000, N'sp_rename failed: the migration stops here.', 1; END;

IF EXISTS (SELECT 1 FROM sys.extended_properties WHERE class = 1 AND name = N'MS_Description' AND major_id = OBJECT_ID(N'sales.customers') AND minor_id = 0) BEGIN DECLARE @mq_rc2 int; EXEC @mq_rc2 = sys.sp_updateextendedproperty @name = N'MS_Description', @level0type = N'SCHEMA', @level0name = N'sales', @level1type = N'TABLE', @level1name = N'customers', @value = N'Customers who place orders, with their balance.'; IF @mq_rc2 <> 0 THROW 50000, N'sys.sp_updateextendedproperty failed: the migration stops here.', 1; END ELSE BEGIN DECLARE @mq_rc3 int; EXEC @mq_rc3 = sys.sp_addextendedproperty @name = N'MS_Description', @level0type = N'SCHEMA', @level0name = N'sales', @level1type = N'TABLE', @level1name = N'customers', @value = N'Customers who place orders, with their balance.'; IF @mq_rc3 <> 0 THROW 50000, N'sys.sp_addextendedproperty failed: the migration stops here.', 1; END;

ALTER TABLE sales.customers DROP CONSTRAINT ck_customers_balance;

BEGIN DECLARE @mq_rc4 int; EXEC @mq_rc4 = sp_rename N'sales.uq_customers_email', N'uq_customers_mail', N'OBJECT'; IF @mq_rc4 <> 0 THROW 50000, N'sp_rename failed: the migration stops here.', 1; END;

BEGIN DECLARE @mq_rc5 int; EXEC @mq_rc5 = sp_rename N'sales.customers.ix_customers_code', N'ix_customers_code_lookup', N'INDEX'; IF @mq_rc5 <> 0 THROW 50000, N'sp_rename failed: the migration stops here.', 1; END;

DROP INDEX ix_orders_placed_on ON sales.orders;

BEGIN DECLARE @mq_rc6 int; EXEC @mq_rc6 = sp_rename N'sales.pk_orders', N'pk_orders_id', N'OBJECT'; IF @mq_rc6 <> 0 THROW 50000, N'sp_rename failed: the migration stops here.', 1; END;

BEGIN DECLARE @mq_rc7 int; EXEC @mq_rc7 = sp_rename N'sales.order_items.product_code', N'sku', N'COLUMN'; IF @mq_rc7 <> 0 THROW 50000, N'sp_rename failed: the migration stops here.', 1; END;

ALTER TABLE sales.order_items DROP CONSTRAINT ck_order_lines_quantity;

ALTER TABLE sales.order_items ALTER COLUMN quantity bigint NOT NULL;

ALTER TABLE sales.order_items ADD CONSTRAINT ck_order_lines_quantity CHECK (quantity > 0);

-- TODO: convert existing values of order_items.quantity (type: int32 -> int64); the cast keeps each value as it is.

ALTER TABLE sales.customers ADD phone nvarchar(30) NULL;

BEGIN DECLARE @mq_rc8 int; EXEC @mq_rc8 = sys.sp_addextendedproperty @name = N'MS_Description', @value = N'Contact phone.', @level0type = N'SCHEMA', @level0name = N'sales', @level1type = N'TABLE', @level1name = N'customers', @level2type = N'COLUMN', @level2name = N'phone'; IF @mq_rc8 <> 0 THROW 50000, N'sys.sp_addextendedproperty failed: the migration stops here.', 1; END;

ALTER TABLE sales.customers ADD loyalty int NULL;

ALTER TABLE sales.customers ADD vip bit NOT NULL CONSTRAINT df_customers_vip DEFAULT 0;

ALTER TABLE sales.orders DROP CONSTRAINT fk_orders_customer;

ALTER TABLE sales.customers DROP COLUMN balance_twice;

DROP INDEX ix_customers_active ON sales.customers;

ALTER TABLE sales.customers DROP CONSTRAINT pk_customers;

ALTER TABLE sales.customers ALTER COLUMN id bigint NOT NULL;

ALTER TABLE sales.customers ADD balance_twice AS (balance * 2) PERSISTED;

CREATE INDEX ix_customers_active ON sales.customers (status) WHERE status = 'A';

ALTER TABLE sales.customers ADD CONSTRAINT pk_customers PRIMARY KEY CLUSTERED (id);

-- TODO: convert existing values of customers.id (type: int32 -> int64); the cast keeps each value as it is.

DROP INDEX ix_customers_name ON sales.customers;

DROP INDEX ix_customers_active ON sales.customers;

ALTER TABLE sales.customers ALTER COLUMN name nvarchar(150) COLLATE Latin1_General_100_CI_AS NOT NULL;

CREATE INDEX ix_customers_name ON sales.customers (name, created_at DESC) INCLUDE (email);

CREATE INDEX ix_customers_active ON sales.customers (status) WHERE status = 'A';

DROP INDEX ix_customers_name ON sales.customers;

ALTER TABLE sales.customers ALTER COLUMN email nvarchar(200) NULL;

CREATE INDEX ix_customers_name ON sales.customers (name, created_at DESC) INCLUDE (email);

BEGIN DECLARE @mq_rc9 int; EXEC @mq_rc9 = sp_rename N'sales.customers.notes', N'remarks', N'COLUMN'; IF @mq_rc9 <> 0 THROW 50000, N'sp_rename failed: the migration stops here.', 1; END;

IF EXISTS (SELECT 1 FROM sys.extended_properties WHERE class = 1 AND name = N'MS_Description' AND major_id = OBJECT_ID(N'sales.customers') AND minor_id = COLUMNPROPERTY(OBJECT_ID(N'sales.customers'), N'remarks', 'ColumnId')) BEGIN DECLARE @mq_rc10 int; EXEC @mq_rc10 = sys.sp_updateextendedproperty @name = N'MS_Description', @level0type = N'SCHEMA', @level0name = N'sales', @level1type = N'TABLE', @level1name = N'customers', @level2type = N'COLUMN', @level2name = N'remarks', @value = N'Remarks about the customer.'; IF @mq_rc10 <> 0 THROW 50000, N'sys.sp_updateextendedproperty failed: the migration stops here.', 1; END ELSE BEGIN DECLARE @mq_rc11 int; EXEC @mq_rc11 = sys.sp_addextendedproperty @name = N'MS_Description', @level0type = N'SCHEMA', @level0name = N'sales', @level1type = N'TABLE', @level1name = N'customers', @level2type = N'COLUMN', @level2name = N'remarks', @value = N'Remarks about the customer.'; IF @mq_rc11 <> 0 THROW 50000, N'sys.sp_addextendedproperty failed: the migration stops here.', 1; END;

IF OBJECT_ID(N'sales.customers_balance_default', 'D') IS NOT NULL BEGIN DECLARE @mq_rc12 int; EXEC @mq_rc12 = sp_rename N'sales.customers_balance_default', N'customers_balance_dflt', N'OBJECT'; IF @mq_rc12 <> 0 THROW 50000, N'sp_rename failed: the migration stops here.', 1; END;

IF OBJECT_ID(N'sales.customers_balance_dflt', 'D') IS NOT NULL ALTER TABLE sales.customers DROP CONSTRAINT customers_balance_dflt;

ALTER TABLE sales.customers ADD CONSTRAINT customers_balance_dflt DEFAULT 10 FOR balance;

ALTER TABLE sales.orders DROP CONSTRAINT uq_orders_customer_number;

ALTER TABLE sales.orders ALTER COLUMN customer_id bigint NOT NULL;

ALTER TABLE sales.orders ADD CONSTRAINT uq_orders_customer_number UNIQUE (customer_id, number);

-- TODO: convert existing values of orders.customer_id (type: int32 -> int64); the cast keeps each value as it is.

CREATE TABLE sales.notes (
    id bigint IDENTITY(1,1) NOT NULL,
    customer_id bigint NULL,
    body nvarchar(max) NOT NULL CONSTRAINT df_notes_body DEFAULT N'-',
    CONSTRAINT pk_notes PRIMARY KEY (id),
    CONSTRAINT fk_notes_customer FOREIGN KEY (customer_id) REFERENCES sales.customers (id) ON DELETE SET NULL
);
CREATE INDEX ix_notes_customer ON sales.notes (customer_id);

ALTER TABLE sales.customers ADD CONSTRAINT ck_customers_balance CHECK (balance >= -100);

ALTER TABLE sales.orders ADD CONSTRAINT ck_orders_total CHECK (total >= 0);

CREATE INDEX ix_orders_placed_on ON sales.orders (placed_on DESC, number);

ALTER TABLE sales.orders ADD CONSTRAINT fk_orders_customer FOREIGN KEY (customer_id) REFERENCES sales.customers (id) ON DELETE CASCADE;

BEGIN DECLARE @mq_rc13 int; EXEC @mq_rc13 = sp_rename N'sales.customer_orders', N'customer_order_list', N'OBJECT'; IF @mq_rc13 <> 0 THROW 50000, N'sp_rename failed: the migration stops here.', 1; END;

IF EXISTS (SELECT 1 FROM sys.extended_properties WHERE class = 1 AND name = N'MS_Description' AND major_id = OBJECT_ID(N'sales.customer_order_list') AND minor_id = 0) BEGIN DECLARE @mq_rc16 int; EXEC @mq_rc16 = sys.sp_updateextendedproperty @name = N'MS_Description', @level0type = N'SCHEMA', @level0name = N'sales', @level1type = N'VIEW', @level1name = N'customer_order_list', @value = N'Every customer with each order.'; IF @mq_rc16 <> 0 THROW 50000, N'sys.sp_updateextendedproperty failed: the migration stops here.', 1; END ELSE BEGIN DECLARE @mq_rc17 int; EXEC @mq_rc17 = sys.sp_addextendedproperty @name = N'MS_Description', @level0type = N'SCHEMA', @level0name = N'sales', @level1type = N'VIEW', @level1name = N'customer_order_list', @value = N'Every customer with each order.'; IF @mq_rc17 <> 0 THROW 50000, N'sys.sp_addextendedproperty failed: the migration stops here.', 1; END;

GO
IF @@ERROR <> 0 AND @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
IF @@TRANCOUNT = 0 SET NOEXEC ON;
GO
CREATE VIEW sales.customer_order_totals (customer_id, order_count) AS
SELECT id, count(*) FROM sales.customer_order_list GROUP BY id;
GO
IF @@ERROR <> 0 AND @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
IF @@TRANCOUNT = 0 SET NOEXEC ON;
GO

IF OBJECT_ID(N'sales.df_orders_id', 'D') IS NOT NULL ALTER TABLE sales.orders DROP CONSTRAINT df_orders_id;

BEGIN DECLARE @mq_rc18 int; EXEC @mq_rc18 = sp_rename N'sales.order_numbers', N'order_number_seq', N'OBJECT'; IF @mq_rc18 <> 0 THROW 50000, N'sp_rename failed: the migration stops here.', 1; END;

ALTER TABLE sales.orders ADD CONSTRAINT df_orders_id DEFAULT (NEXT VALUE FOR sales.order_number_seq) FOR id;

ALTER SEQUENCE sales.order_number_seq INCREMENT BY 5 MAXVALUE 999999999 CYCLE;

COMMIT TRANSACTION;
GO
-- A compile error in the last batch skips its COMMIT and leaves the transaction open: roll it back.
IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
SET NOEXEC OFF;
