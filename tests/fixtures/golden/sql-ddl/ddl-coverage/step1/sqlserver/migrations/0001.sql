-- Migration 0001 of database sqlserver (SQL Server): schema revision 0 to 1.
-- Written once by Maquettiste (sql-ddl/migration) from the schema diff. It is yours now: review it, adjust it and commit it.
-- Lines marked TODO need a decision the diff cannot make (data conversions, sequence restarts).

-- One transaction across the batches: XACT_ABORT rolls it back on an error, and a batch that finds it gone turns execution off
-- (SET NOEXEC ON), so no later batch runs outside it.
SET XACT_ABORT ON;
SET QUOTED_IDENTIFIER ON;
BEGIN TRANSACTION;

IF SCHEMA_ID(N'sales') IS NULL EXEC(N'CREATE SCHEMA sales');

CREATE SEQUENCE sales.order_numbers AS bigint START WITH 1000 INCREMENT BY 1 CACHE 20;

CREATE SEQUENCE sales.audit_numbers AS int START WITH 1 INCREMENT BY 1;

CREATE TABLE sales.customers (
    id int IDENTITY(100,1) NOT NULL,
    code char(10) NOT NULL,
    name nvarchar(100) COLLATE Latin1_General_100_CI_AS NOT NULL,
    email nvarchar(200) NOT NULL,
    notes nvarchar(max) NULL,
    balance decimal(12,2) NOT NULL CONSTRAINT customers_balance_default DEFAULT 0,
    status nvarchar(1) NOT NULL CONSTRAINT df_customers_status DEFAULT N'A',
    created_at datetime2(6) NOT NULL CONSTRAINT df_customers_created_at DEFAULT SYSUTCDATETIME(),
    balance_twice AS (balance * 2) PERSISTED,
    CONSTRAINT pk_customers PRIMARY KEY CLUSTERED (id),
    CONSTRAINT uq_customers_email UNIQUE (email),
    CONSTRAINT ck_customers_status CHECK (status IN ('A', 'I')),
    CONSTRAINT ck_customers_balance CHECK (balance >= 0)
);
CREATE INDEX ix_customers_name ON sales.customers (name, created_at DESC) INCLUDE (email);
CREATE INDEX ix_customers_active ON sales.customers (status) WHERE status = 'A';
CREATE UNIQUE INDEX ix_customers_code ON sales.customers (code);
BEGIN DECLARE @mq_rc2 int; EXEC @mq_rc2 = sys.sp_addextendedproperty @name = N'MS_Description', @value = N'Customers who place orders.', @level0type = N'SCHEMA', @level0name = N'sales', @level1type = N'TABLE', @level1name = N'customers'; IF @mq_rc2 <> 0 THROW 50000, N'sys.sp_addextendedproperty failed: the migration stops here.', 1; END;
BEGIN DECLARE @mq_rc1 int; EXEC @mq_rc1 = sys.sp_addextendedproperty @name = N'MS_Description', @value = N'Free-form notes.', @level0type = N'SCHEMA', @level0name = N'sales', @level1type = N'TABLE', @level1name = N'customers', @level2type = N'COLUMN', @level2name = N'notes'; IF @mq_rc1 <> 0 THROW 50000, N'sys.sp_addextendedproperty failed: the migration stops here.', 1; END;

CREATE TABLE sales.orders (
    id bigint NOT NULL CONSTRAINT df_orders_id DEFAULT (NEXT VALUE FOR sales.order_numbers),
    number nvarchar(20) NOT NULL,
    customer_id int NOT NULL,
    total decimal(12,2) NOT NULL CONSTRAINT df_orders_total DEFAULT 0,
    placed_on date NULL,
    CONSTRAINT pk_orders PRIMARY KEY (id),
    CONSTRAINT uq_orders_customer_number UNIQUE (customer_id, number),
    CONSTRAINT fk_orders_customer FOREIGN KEY (customer_id) REFERENCES sales.customers (id) ON DELETE CASCADE
);
CREATE INDEX ix_orders_placed_on ON sales.orders (placed_on DESC);

CREATE TABLE sales.order_lines (
    order_id bigint NOT NULL,
    line_no int NOT NULL,
    product_code nchar(20) NOT NULL,
    quantity int NOT NULL,
    CONSTRAINT pk_order_lines PRIMARY KEY (order_id, line_no),
    CONSTRAINT fk_order_lines_order FOREIGN KEY (order_id) REFERENCES sales.orders (id) ON DELETE CASCADE ON UPDATE CASCADE,
    CONSTRAINT ck_order_lines_quantity CHECK (quantity > 0)
);
CREATE INDEX ix_order_lines_product ON sales.order_lines (product_code);

GO
IF @@ERROR <> 0 AND @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
IF @@TRANCOUNT = 0 SET NOEXEC ON;
GO
CREATE VIEW sales.customer_orders AS
SELECT c.id, c.name, o.number FROM sales.customers c JOIN sales.orders o ON o.customer_id = c.id;
GO
IF @@ERROR <> 0 AND @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
IF @@TRANCOUNT = 0 SET NOEXEC ON;
GO
BEGIN DECLARE @mq_rc5 int; EXEC @mq_rc5 = sys.sp_addextendedproperty @name = N'MS_Description', @value = N'Customers with their orders.', @level0type = N'SCHEMA', @level0name = N'sales', @level1type = N'VIEW', @level1name = N'customer_orders'; IF @mq_rc5 <> 0 THROW 50000, N'sys.sp_addextendedproperty failed: the migration stops here.', 1; END;
GO
IF @@ERROR <> 0 AND @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
IF @@TRANCOUNT = 0 SET NOEXEC ON;
GO

GO
IF @@ERROR <> 0 AND @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
IF @@TRANCOUNT = 0 SET NOEXEC ON;
GO
CREATE VIEW sales.customer_order_totals (customer_id, order_count) AS
SELECT id, count(*) FROM sales.customer_orders GROUP BY id;
GO
IF @@ERROR <> 0 AND @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
IF @@TRANCOUNT = 0 SET NOEXEC ON;
GO

GO
IF @@ERROR <> 0 AND @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
IF @@TRANCOUNT = 0 SET NOEXEC ON;
GO
CREATE VIEW sales.active_customers AS
SELECT id, code, status FROM sales.customers WHERE status = 'A'
WITH CHECK OPTION;
GO
IF @@ERROR <> 0 AND @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
IF @@TRANCOUNT = 0 SET NOEXEC ON;
GO

COMMIT TRANSACTION;
GO
-- A compile error in the last batch skips its COMMIT and leaves the transaction open: roll it back.
IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
SET NOEXEC OFF;
