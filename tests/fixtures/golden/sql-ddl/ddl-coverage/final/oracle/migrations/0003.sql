-- Migration 0003 of database oracle (Oracle): schema revision 2 to 3.
-- Written once by Maquettiste (sql-ddl/migration) from the schema diff. It is yours now: review it, adjust it and commit it.
-- Lines marked TODO need a decision the diff cannot make (data conversions, sequence restarts).

-- Oracle commits each DDL statement: run the script once, top to bottom.

DROP MATERIALIZED VIEW order_counts;

DROP VIEW active_customers;

DROP VIEW customer_order_totals;

DROP VIEW customer_order_list;

ALTER TABLE orders DROP CONSTRAINT fk_orders_customer;

ALTER TABLE customers DROP CONSTRAINT ck_customers_status;

ALTER TABLE customers DROP CONSTRAINT uq_customers_mail;

DROP INDEX ix_orders_placed_on;

-- Data loss: dropping table notes deletes its rows.

DROP TABLE notes;

-- Data loss: dropping column customers.phone deletes its values.

ALTER TABLE customers DROP COLUMN phone;

-- Data loss: dropping column customers.loyalty deletes its values.

ALTER TABLE customers DROP COLUMN loyalty;

-- TODO: customers.name switches from Unicode to single-byte text (nvarchar2(150) -> varchar2(150 char)); characters outside the code page become '?'. Check first: SELECT count(*) FROM customers WHERE TO_NCHAR(TO_CHAR(name)) <> name;

ALTER TABLE customers MODIFY (name varchar2(150 char));

-- TODO: customers.balance holds fewer digits (precision: 12 -> 10); values that do not fit are refused or rounded. Check first: SELECT count(*) FROM customers WHERE abs(balance) >= 100000000 OR balance <> round(balance, 2);

ALTER TABLE customers MODIFY (balance number(10,2));

-- TODO: orders.number gets shorter (length: 20 -> 16); longer values are cut or refused. Check first: SELECT count(*) FROM orders WHERE length("number") > 16;

ALTER TABLE orders MODIFY ("number" varchar2(16 char));

ALTER TABLE orders ADD CONSTRAINT fk_orders_customer FOREIGN KEY (customer_id) REFERENCES customers (id) DEFERRABLE INITIALLY IMMEDIATE;

DROP SEQUENCE audit_numbers;
