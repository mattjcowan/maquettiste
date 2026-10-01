-- Migration 0003 of database main (PostgreSQL 16): schema revision 2 to 3.
-- Written once by Maquettiste (sql-ddl/migration) from the schema diff. It is yours now: review it, adjust it and commit it.
-- Lines marked TODO need a decision the diff cannot make (data conversions, SQLite table rebuilds).

BEGIN;

COMMENT ON COLUMN northwind.journal_entries.entry_date IS 'Accounting date.';

COMMENT ON COLUMN northwind.journal_entries.reference IS 'Source document number.';

COMMENT ON COLUMN northwind.journal_entries.description IS 'What the entry records.';

COMMENT ON COLUMN northwind.journal_entries.status IS 'Posting state.';

COMMENT ON COLUMN northwind.journal_entries.source_document IS 'Kind of source, such as invoice or goods-receipt.';

COMMENT ON COLUMN northwind.journal_entries.posted_at IS 'When it was posted.';

COMMENT ON COLUMN northwind.journal_entries.created_at IS 'When the row was created.';

COMMENT ON COLUMN northwind.journal_entries.created_by IS 'User name of the creator.';

COMMENT ON COLUMN northwind.journal_entries.updated_at IS 'When the row was last changed.';

COMMENT ON COLUMN northwind.journal_entries.updated_by IS 'User name of the last editor.';

COMMENT ON COLUMN northwind.teams.id IS 'Surrogate key of the team.';

COMMENT ON COLUMN northwind.teams.name IS 'Team name.';

COMMENT ON COLUMN northwind.teams.purpose IS 'What the team does.';

COMMENT ON COLUMN northwind.teams.email IS 'Shared mailbox.';

COMMENT ON COLUMN northwind.teams.created_at IS 'When the row was created.';

COMMENT ON COLUMN northwind.teams.created_by IS 'User name of the creator.';

COMMENT ON COLUMN northwind.teams.updated_at IS 'When the row was last changed.';

COMMENT ON COLUMN northwind.teams.updated_by IS 'User name of the last editor.';

COMMENT ON COLUMN northwind.customer_groups.id IS 'Surrogate key of the customer group.';

COMMENT ON COLUMN northwind.customer_groups.name IS 'Group name.';

COMMENT ON COLUMN northwind.customer_groups.description IS 'Who belongs to the group.';

COMMENT ON COLUMN northwind.customer_groups.default_discount IS 'Discount applied when no better price exists.';

COMMENT ON COLUMN northwind.customer_groups.created_at IS 'When the row was created.';

COMMENT ON COLUMN northwind.customer_groups.created_by IS 'User name of the creator.';

COMMENT ON COLUMN northwind.customer_groups.updated_at IS 'When the row was last changed.';

COMMENT ON COLUMN northwind.customer_groups.updated_by IS 'User name of the last editor.';

COMMENT ON COLUMN northwind.brands.id IS 'Surrogate key of the brand.';

COMMENT ON COLUMN northwind.brands.name IS 'Brand name.';

COMMENT ON COLUMN northwind.brands.description IS 'Brand story.';

COMMENT ON COLUMN northwind.brands.logo_url IS 'Logo image URL.';

COMMENT ON COLUMN northwind.brands.website IS 'Brand website.';

COMMENT ON COLUMN northwind.brands.created_at IS 'When the row was created.';

COMMENT ON COLUMN northwind.brands.created_by IS 'User name of the creator.';

COMMENT ON COLUMN northwind.brands.updated_at IS 'When the row was last changed.';

COMMENT ON COLUMN northwind.brands.updated_by IS 'User name of the last editor.';

COMMENT ON COLUMN northwind.units_of_measure.code IS 'UN/CEFACT common code, such as EA, KGM or CS; the natural key.';

COMMENT ON COLUMN northwind.units_of_measure.name IS 'Display name, such as each, kilogram or case.';

COMMENT ON COLUMN northwind.units_of_measure.dimension IS 'What the unit measures - count, mass, length, volume or area.';

COMMENT ON COLUMN northwind.units_of_measure.is_base_unit IS 'Whether the unit is the base of its dimension.';

COMMENT ON COLUMN northwind.units_of_measure.decimals_allowed IS 'Decimals allowed on quantities in the unit.';

COMMENT ON COLUMN northwind.adjustment_reasons.id IS 'Surrogate key of the adjustment reason.';

COMMENT ON COLUMN northwind.adjustment_reasons.affects_cost IS 'Whether it posts a write-off to the ledger.';

COMMENT ON COLUMN northwind.adjustment_reasons.code IS 'Short stable code used in integrations and imports.';

COMMENT ON COLUMN northwind.adjustment_reasons.name IS 'Display name shown in pick lists.';

COMMENT ON COLUMN northwind.adjustment_reasons.sort_order IS 'Position in pick lists.';

COMMENT ON COLUMN northwind.adjustment_reasons.is_active IS 'Whether the value can be chosen for new records.';

COMMENT ON COLUMN northwind.customer_statements.id IS 'Surrogate key of the customer statement.';

COMMENT ON COLUMN northwind.customer_statements.statement_date IS 'Statement date.';

COMMENT ON COLUMN northwind.customer_statements.opening_balance_amount IS 'The amount in the currency''s major unit.';

COMMENT ON COLUMN northwind.customer_statements.opening_balance_currency IS 'The ISO 4217 currency of the amount.';

COMMENT ON COLUMN northwind.customer_statements.closing_balance_amount IS 'The amount in the currency''s major unit.';

COMMENT ON COLUMN northwind.customer_statements.closing_balance_currency IS 'The ISO 4217 currency of the amount.';

COMMENT ON COLUMN northwind.customer_statements.sent_at IS 'When it was sent.';

COMMENT ON COLUMN northwind.customer_statements.document_url IS 'Rendered PDF.';

COMMENT ON COLUMN northwind.return_authorizations.id IS 'Surrogate key of the return authorization.';

COMMENT ON COLUMN northwind.return_authorizations.rma_number IS 'RMA number the customer writes on the parcel.';

COMMENT ON COLUMN northwind.return_authorizations.requested_on IS 'Request date.';

COMMENT ON COLUMN northwind.return_authorizations.status IS 'Lifecycle state.';

COMMENT ON COLUMN northwind.return_authorizations.approved_on IS 'Approval date.';

COMMENT ON COLUMN northwind.return_authorizations.return_method IS 'carrier-pickup or drop-off.';

COMMENT ON COLUMN northwind.return_authorizations.restocking_fee_percent IS 'Restocking fee charged.';

COMMENT ON COLUMN northwind.return_authorizations.customer_comments IS 'What the customer said.';

COMMENT ON COLUMN northwind.return_authorizations.created_at IS 'When the row was created.';

COMMENT ON COLUMN northwind.return_authorizations.created_by IS 'User name of the creator.';

COMMENT ON COLUMN northwind.return_authorizations.updated_at IS 'When the row was last changed.';

COMMENT ON COLUMN northwind.return_authorizations.updated_by IS 'User name of the last editor.';

COMMENT ON COLUMN northwind.departments.id IS 'Surrogate key of the department.';

COMMENT ON COLUMN northwind.departments.code IS 'Short department code used in cost reports.';

COMMENT ON COLUMN northwind.departments.name IS 'Department name.';

COMMENT ON COLUMN northwind.departments.is_active IS 'Whether employees can be assigned.';

COMMENT ON COLUMN northwind.departments.created_at IS 'When the row was created.';

COMMENT ON COLUMN northwind.departments.created_by IS 'User name of the creator.';

COMMENT ON COLUMN northwind.departments.updated_at IS 'When the row was last changed.';

COMMENT ON COLUMN northwind.departments.updated_by IS 'User name of the last editor.';

COMMENT ON COLUMN northwind.roles.id IS 'Surrogate key of the role.';

COMMENT ON COLUMN northwind.roles.name IS 'Role name.';

COMMENT ON COLUMN northwind.roles.description IS 'What the role is for.';

COMMENT ON COLUMN northwind.roles.is_system IS 'Built-in role that cannot be deleted.';

COMMENT ON COLUMN northwind.roles.created_at IS 'When the row was created.';

COMMENT ON COLUMN northwind.roles.created_by IS 'User name of the creator.';

COMMENT ON COLUMN northwind.roles.updated_at IS 'When the row was last changed.';

COMMENT ON COLUMN northwind.roles.updated_by IS 'User name of the last editor.';

COMMENT ON COLUMN northwind.channel_listings.id IS 'Surrogate key of the channel listing.';

COMMENT ON COLUMN northwind.channel_listings.external_id IS 'Channel''s identifier for the listing.';

COMMENT ON COLUMN northwind.channel_listings.listed_price_amount IS 'The amount in the currency''s major unit.';

COMMENT ON COLUMN northwind.channel_listings.listed_price_currency IS 'The ISO 4217 currency of the amount.';

COMMENT ON COLUMN northwind.channel_listings.is_published IS 'Whether the listing is live.';

COMMENT ON COLUMN northwind.channel_listings.published_at IS 'When it went live.';

COMMENT ON COLUMN northwind.channel_listings.created_at IS 'When the row was created.';

COMMENT ON COLUMN northwind.channel_listings.created_by IS 'User name of the creator.';

COMMENT ON COLUMN northwind.channel_listings.updated_at IS 'When the row was last changed.';

COMMENT ON COLUMN northwind.channel_listings.updated_by IS 'User name of the last editor.';

COMMENT ON COLUMN northwind.product_attribute_values.id IS 'Surrogate key of the product attribute value.';

COMMENT ON COLUMN northwind.product_attribute_values.text_value IS 'Value of a text attribute.';

COMMENT ON COLUMN northwind.product_attribute_values.numeric_value IS 'Value of a number attribute.';

COMMENT ON COLUMN northwind.product_attribute_values.boolean_value IS 'Value of a boolean attribute.';

COMMENT ON COLUMN northwind.invoice_lines.id IS 'Surrogate key of the invoice line.';

COMMENT ON COLUMN northwind.invoice_lines.line_number IS 'Line number.';

COMMENT ON COLUMN northwind.invoice_lines.description IS 'Line text.';

COMMENT ON COLUMN northwind.invoice_lines.quantity IS 'Quantity invoiced.';

COMMENT ON COLUMN northwind.invoice_lines.unit_price_amount IS 'The amount in the currency''s major unit.';

COMMENT ON COLUMN northwind.invoice_lines.unit_price_currency IS 'The ISO 4217 currency of the amount.';

COMMENT ON COLUMN northwind.invoice_lines.discount IS 'Discount.';

COMMENT ON COLUMN northwind.invoice_lines.tax_rate IS 'Tax rate.';

COMMENT ON COLUMN northwind.invoice_lines.line_total_amount IS 'The amount in the currency''s major unit.';

COMMENT ON COLUMN northwind.invoice_lines.line_total_currency IS 'The ISO 4217 currency of the amount.';

COMMENT ON COLUMN northwind.delivery_routes.id IS 'Surrogate key of the delivery route.';

COMMENT ON COLUMN northwind.delivery_routes.route_code IS 'Route code, such as DEN-NORTH-2.';

COMMENT ON COLUMN northwind.delivery_routes.route_date IS 'Delivery date.';

COMMENT ON COLUMN northwind.delivery_routes.status IS 'planned, loading, on-road or complete.';

COMMENT ON COLUMN northwind.delivery_routes.planned_distance_km IS 'Planned distance.';

COMMENT ON COLUMN northwind.delivery_routes.created_at IS 'When the row was created.';

COMMENT ON COLUMN northwind.delivery_routes.created_by IS 'User name of the creator.';

COMMENT ON COLUMN northwind.delivery_routes.updated_at IS 'When the row was last changed.';

COMMENT ON COLUMN northwind.delivery_routes.updated_by IS 'User name of the last editor.';

COMMENT ON COLUMN northwind.carts.id IS 'Surrogate key of the cart.';

COMMENT ON COLUMN northwind.carts.cart_token IS 'Opaque token held by the browser.';

COMMENT ON COLUMN northwind.carts.last_activity_at IS 'Last change to the cart.';

COMMENT ON COLUMN northwind.carts.is_abandoned IS 'Flagged by the abandoned-cart job.';

COMMENT ON COLUMN northwind.carts.created_at IS 'When the row was created.';

COMMENT ON COLUMN northwind.carts.created_by IS 'User name of the creator.';

COMMENT ON COLUMN northwind.carts.updated_at IS 'When the row was last changed.';

COMMENT ON COLUMN northwind.carts.updated_by IS 'User name of the last editor.';

COMMENT ON COLUMN northwind.purchase_requisitions.id IS 'Surrogate key of the purchase requisition.';

COMMENT ON COLUMN northwind.purchase_requisitions.requisition_number IS 'Requisition number.';

COMMENT ON COLUMN northwind.purchase_requisitions.requested_on IS 'Request date.';

COMMENT ON COLUMN northwind.purchase_requisitions.needed_by IS 'Date the goods are needed.';

COMMENT ON COLUMN northwind.purchase_requisitions.status IS 'open, approved, ordered or rejected.';

COMMENT ON COLUMN northwind.purchase_requisitions.justification IS 'Business reason.';

COMMENT ON COLUMN northwind.purchase_requisitions.created_at IS 'When the row was created.';

COMMENT ON COLUMN northwind.purchase_requisitions.created_by IS 'User name of the creator.';

COMMENT ON COLUMN northwind.purchase_requisitions.updated_at IS 'When the row was last changed.';

COMMENT ON COLUMN northwind.purchase_requisitions.updated_by IS 'User name of the last editor.';

COMMENT ON COLUMN northwind.sales_orders.id IS 'Surrogate key of the sales order.';

COMMENT ON COLUMN northwind.sales_orders.ordered_at IS 'When the order was placed.';

COMMENT ON COLUMN northwind.sales_orders.status IS 'Lifecycle state.';

COMMENT ON COLUMN northwind.sales_orders.customer_po_number IS 'Customer''s purchase order number.';

COMMENT ON COLUMN northwind.sales_orders.source IS 'Entry channel - portal, edi, phone or email.';

COMMENT ON COLUMN northwind.sales_orders.requested_delivery_on IS 'Delivery date the customer asked for.';

COMMENT ON COLUMN northwind.sales_orders.promised_delivery_on IS 'Delivery date Northwind committed to.';

COMMENT ON COLUMN northwind.sales_orders.bill_line1 IS 'Street and number, or PO box.';

COMMENT ON COLUMN northwind.sales_orders.bill_line2 IS 'Suite, floor or building.';

COMMENT ON COLUMN northwind.sales_orders.bill_city IS 'City or locality.';

COMMENT ON COLUMN northwind.sales_orders.bill_region IS 'State, province or county.';

COMMENT ON COLUMN northwind.sales_orders.bill_postal_code IS 'Postal or ZIP code.';

COMMENT ON COLUMN northwind.sales_orders.bill_country_code IS 'ISO country code.';

COMMENT ON COLUMN northwind.sales_orders.ship_line1 IS 'Street and number, or PO box.';

COMMENT ON COLUMN northwind.sales_orders.ship_line2 IS 'Suite, floor or building.';

COMMENT ON COLUMN northwind.sales_orders.ship_city IS 'City or locality.';

COMMENT ON COLUMN northwind.sales_orders.ship_region IS 'State, province or county.';

COMMENT ON COLUMN northwind.sales_orders.ship_postal_code IS 'Postal or ZIP code.';

COMMENT ON COLUMN northwind.sales_orders.ship_country_code IS 'ISO country code.';

COMMENT ON COLUMN northwind.sales_orders.freight_terms IS 'Incoterm that decides who pays freight.';

COMMENT ON COLUMN northwind.sales_orders.subtotal_amount IS 'The amount in the currency''s major unit.';

COMMENT ON COLUMN northwind.sales_orders.subtotal_currency IS 'The ISO 4217 currency of the amount.';

COMMENT ON COLUMN northwind.sales_orders.discount_total_amount IS 'The amount in the currency''s major unit.';

COMMENT ON COLUMN northwind.sales_orders.discount_total_currency IS 'The ISO 4217 currency of the amount.';

COMMENT ON COLUMN northwind.sales_orders.tax_total_amount IS 'The amount in the currency''s major unit.';

COMMENT ON COLUMN northwind.sales_orders.tax_total_currency IS 'The ISO 4217 currency of the amount.';

COMMENT ON COLUMN northwind.sales_orders.freight_total_amount IS 'The amount in the currency''s major unit.';

COMMENT ON COLUMN northwind.sales_orders.freight_total_currency IS 'The ISO 4217 currency of the amount.';

COMMENT ON COLUMN northwind.sales_orders.grand_total_amount IS 'The amount in the currency''s major unit.';

COMMENT ON COLUMN northwind.sales_orders.grand_total_currency IS 'The ISO 4217 currency of the amount.';

COMMENT ON COLUMN northwind.sales_orders.notes IS 'Instructions from the customer.';

COMMENT ON COLUMN northwind.sales_orders.created_at IS 'When the row was created.';

COMMENT ON COLUMN northwind.sales_orders.created_by IS 'User name of the creator.';

COMMENT ON COLUMN northwind.sales_orders.updated_at IS 'When the row was last changed.';

COMMENT ON COLUMN northwind.sales_orders.updated_by IS 'User name of the last editor.';

COMMENT ON COLUMN northwind.sales_orders.deleted_at IS 'When the row was marked deleted; null while it is live.';

COMMENT ON COLUMN northwind.billing_accounts.id IS 'Surrogate key of the billing account.';

COMMENT ON COLUMN northwind.billing_accounts.account_number IS 'Billing account number.';

COMMENT ON COLUMN northwind.billing_accounts.name IS 'Account name, such as Facilities division.';

COMMENT ON COLUMN northwind.billing_accounts.billing_address_line1 IS 'Street and number, or PO box.';

COMMENT ON COLUMN northwind.billing_accounts.billing_address_line2 IS 'Suite, floor or building.';

COMMENT ON COLUMN northwind.billing_accounts.billing_address_city IS 'City or locality.';

COMMENT ON COLUMN northwind.billing_accounts.billing_address_region IS 'State, province or county.';

COMMENT ON COLUMN northwind.billing_accounts.billing_address_postal_code IS 'Postal or ZIP code.';

COMMENT ON COLUMN northwind.billing_accounts.billing_address_country_code IS 'ISO country code.';

COMMENT ON COLUMN northwind.billing_accounts.invoice_delivery_method IS 'email, portal, edi or mail.';

COMMENT ON COLUMN northwind.billing_accounts.invoice_email IS 'Mailbox for invoices.';

COMMENT ON COLUMN northwind.billing_accounts.consolidate_invoices IS 'Whether to send one monthly invoice.';

COMMENT ON COLUMN northwind.billing_accounts.created_at IS 'When the row was created.';

COMMENT ON COLUMN northwind.billing_accounts.created_by IS 'User name of the creator.';

COMMENT ON COLUMN northwind.billing_accounts.updated_at IS 'When the row was last changed.';

COMMENT ON COLUMN northwind.billing_accounts.updated_by IS 'User name of the last editor.';

COMMENT ON COLUMN northwind.order_holds.id IS 'Surrogate key of the order hold.';

COMMENT ON COLUMN northwind.order_holds.placed_at IS 'When the hold was placed.';

COMMENT ON COLUMN northwind.order_holds.released_at IS 'When it was released.';

COMMENT ON COLUMN northwind.order_holds.comment IS 'Explanation.';

COMMENT ON COLUMN northwind.order_holds.created_at IS 'When the row was created.';

COMMENT ON COLUMN northwind.order_holds.created_by IS 'User name of the creator.';

COMMENT ON COLUMN northwind.order_holds.updated_at IS 'When the row was last changed.';

COMMENT ON COLUMN northwind.order_holds.updated_by IS 'User name of the last editor.';

COMMENT ON COLUMN northwind.price_tiers.id IS 'Surrogate key of the price tier.';

COMMENT ON COLUMN northwind.price_tiers.from_quantity IS 'Quantity from which the tier applies.';

COMMENT ON COLUMN northwind.price_tiers.unit_price_amount IS 'The amount in the currency''s major unit.';

COMMENT ON COLUMN northwind.price_tiers.unit_price_currency IS 'The ISO 4217 currency of the amount.';

COMMENT ON COLUMN northwind.bank_accounts.id IS 'Surrogate key of the bank account.';

COMMENT ON COLUMN northwind.bank_accounts.name IS 'Account name, such as Operating account USD.';

COMMENT ON COLUMN northwind.bank_accounts.bank_name IS 'Bank.';

COMMENT ON COLUMN northwind.bank_accounts.account_number_masked IS 'Masked account number.';

COMMENT ON COLUMN northwind.bank_accounts.iban IS 'IBAN, for euro accounts.';

COMMENT ON COLUMN northwind.bank_accounts.routing_number IS 'ABA routing number, for US accounts.';

COMMENT ON COLUMN northwind.bank_accounts.created_at IS 'When the row was created.';

COMMENT ON COLUMN northwind.bank_accounts.created_by IS 'User name of the creator.';

COMMENT ON COLUMN northwind.bank_accounts.updated_at IS 'When the row was last changed.';

COMMENT ON COLUMN northwind.bank_accounts.updated_by IS 'User name of the last editor.';

COMMENT ON COLUMN northwind.quality_inspections.id IS 'Surrogate key of the quality inspection.';

COMMENT ON COLUMN northwind.quality_inspections.inspected_at IS 'When it was inspected.';

COMMENT ON COLUMN northwind.quality_inspections.result IS 'passed, failed or conditional.';

COMMENT ON COLUMN northwind.quality_inspections.sample_size IS 'Units inspected.';

COMMENT ON COLUMN northwind.quality_inspections.notes IS 'Findings.';

COMMENT ON COLUMN northwind.quality_inspections.created_at IS 'When the row was created.';

COMMENT ON COLUMN northwind.quality_inspections.created_by IS 'User name of the creator.';

COMMENT ON COLUMN northwind.quality_inspections.updated_at IS 'When the row was last changed.';

COMMENT ON COLUMN northwind.quality_inspections.updated_by IS 'User name of the last editor.';

COMMENT ON COLUMN northwind.product_substitutes.id IS 'Surrogate key of the product substitute.';

COMMENT ON COLUMN northwind.product_substitutes.reason IS 'Why the substitute fits.';

COMMENT ON COLUMN northwind.product_substitutes.priority IS 'Rank among substitutes; 1 is preferred.';

COMMENT ON COLUMN northwind.product_substitutes.requires_approval IS 'Whether the customer must approve the substitution.';

COMMENT ON COLUMN northwind.write_offs.id IS 'Surrogate key of the write off.';

COMMENT ON COLUMN northwind.write_offs.written_off_on IS 'Date written off.';

COMMENT ON COLUMN northwind.write_offs.amount_amount IS 'The amount in the currency''s major unit.';

COMMENT ON COLUMN northwind.write_offs.amount_currency IS 'The ISO 4217 currency of the amount.';

COMMENT ON COLUMN northwind.write_offs.reason IS 'Why it is uncollectable.';

COMMENT ON COLUMN northwind.write_offs.created_at IS 'When the row was created.';

COMMENT ON COLUMN northwind.write_offs.created_by IS 'User name of the creator.';

COMMENT ON COLUMN northwind.write_offs.updated_at IS 'When the row was last changed.';

COMMENT ON COLUMN northwind.write_offs.updated_by IS 'User name of the last editor.';

COMMENT ON COLUMN northwind.card_payments.id IS 'Surrogate key of the payment.';

COMMENT ON COLUMN northwind.card_payments.card_brand IS 'Card network.';

COMMENT ON COLUMN northwind.card_payments.last4 IS 'Last four digits of the card.';

COMMENT ON COLUMN northwind.card_payments.expiry_month IS 'Card expiry month.';

COMMENT ON COLUMN northwind.card_payments.expiry_year IS 'Card expiry year.';

COMMENT ON COLUMN northwind.card_payments.authorization_code IS 'Issuer''s authorization code.';

COMMENT ON COLUMN northwind.card_payments.processor_reference IS 'Processor transaction id.';

COMMENT ON COLUMN northwind.public_holidays.id IS 'Surrogate key of the public holiday.';

COMMENT ON COLUMN northwind.public_holidays.observed_on IS 'Date the holiday is observed.';

COMMENT ON COLUMN northwind.public_holidays.name IS 'Name of the holiday.';

COMMENT ON COLUMN northwind.public_holidays.is_nationwide IS 'Whether it applies to the whole country rather than some regions.';

COMMENT ON COLUMN northwind.password_resets.id IS 'Surrogate key of the password reset.';

COMMENT ON COLUMN northwind.password_resets.token_hash IS 'Hash of the emailed token.';

COMMENT ON COLUMN northwind.password_resets.requested_at IS 'When the reset was requested.';

COMMENT ON COLUMN northwind.password_resets.expires_at IS 'When the token stops working.';

COMMENT ON COLUMN northwind.password_resets.used_at IS 'When the token was used.';

COMMENT ON COLUMN northwind.lead_sources.id IS 'Surrogate key of the lead source.';

COMMENT ON COLUMN northwind.lead_sources.channel IS 'Marketing channel the source rolls up to.';

COMMENT ON COLUMN northwind.lead_sources.code IS 'Short stable code used in integrations and imports.';

COMMENT ON COLUMN northwind.lead_sources.name IS 'Display name shown in pick lists.';

COMMENT ON COLUMN northwind.lead_sources.sort_order IS 'Position in pick lists.';

COMMENT ON COLUMN northwind.lead_sources.is_active IS 'Whether the value can be chosen for new records.';

COMMENT ON COLUMN northwind.service_case_comments.id IS 'Surrogate key of the service case comment.';

COMMENT ON COLUMN northwind.service_case_comments.body IS 'Message.';

COMMENT ON COLUMN northwind.service_case_comments.is_internal IS 'Hidden from the customer.';

COMMENT ON COLUMN northwind.service_case_comments.posted_at IS 'When it was posted.';

COMMENT ON COLUMN northwind.campaigns.id IS 'Surrogate key of the campaign.';

COMMENT ON COLUMN northwind.campaigns.name IS 'Campaign name.';

COMMENT ON COLUMN northwind.campaigns.description IS 'Goals and audience.';

COMMENT ON COLUMN northwind.campaigns.channel IS 'Channel, such as email, print or event.';

COMMENT ON COLUMN northwind.campaigns.period_starts_on IS 'First day of the range.';

COMMENT ON COLUMN northwind.campaigns.period_ends_on IS 'Last day of the range; null when open-ended.';

COMMENT ON COLUMN northwind.campaigns.budget_amount IS 'The amount in the currency''s major unit.';

COMMENT ON COLUMN northwind.campaigns.budget_currency IS 'The ISO 4217 currency of the amount.';

COMMENT ON COLUMN northwind.campaigns.created_at IS 'When the row was created.';

COMMENT ON COLUMN northwind.campaigns.created_by IS 'User name of the creator.';

COMMENT ON COLUMN northwind.campaigns.updated_at IS 'When the row was last changed.';

COMMENT ON COLUMN northwind.campaigns.updated_by IS 'User name of the last editor.';

COMMENT ON COLUMN northwind.product_variants.id IS 'Surrogate key of the product variant.';

COMMENT ON COLUMN northwind.product_variants.sku IS 'SKU of the variant.';

COMMENT ON COLUMN northwind.product_variants.name IS 'Variant name, such as Nitrile gloves, large.';

COMMENT ON COLUMN northwind.product_variants.option_summary IS 'Option values, such as Blue / Large.';

COMMENT ON COLUMN northwind.product_variants.list_price_amount IS 'The amount in the currency''s major unit.';

COMMENT ON COLUMN northwind.product_variants.list_price_currency IS 'The ISO 4217 currency of the amount.';

COMMENT ON COLUMN northwind.product_variants.weight_value IS 'The weight.';

COMMENT ON COLUMN northwind.product_variants.weight_unit IS 'Unit of the weight.';

COMMENT ON COLUMN northwind.product_variants.is_active IS 'Whether the variant can be ordered.';

COMMENT ON COLUMN northwind.product_variants.created_at IS 'When the row was created.';

COMMENT ON COLUMN northwind.product_variants.created_by IS 'User name of the creator.';

COMMENT ON COLUMN northwind.product_variants.updated_at IS 'When the row was last changed.';

COMMENT ON COLUMN northwind.product_variants.updated_by IS 'User name of the last editor.';

COMMENT ON COLUMN northwind.product_variants.deleted_at IS 'When the row was marked deleted; null while it is live.';

COMMENT ON COLUMN northwind.contract_prices.id IS 'Surrogate key of the contract price.';

COMMENT ON COLUMN northwind.contract_prices.unit_cost_amount IS 'The amount in the currency''s major unit.';

COMMENT ON COLUMN northwind.contract_prices.unit_cost_currency IS 'The ISO 4217 currency of the amount.';

COMMENT ON COLUMN northwind.contract_prices.minimum_quantity IS 'Minimum quantity for the cost.';

COMMENT ON COLUMN northwind.contract_prices.validity_starts_on IS 'First day of the range.';

COMMENT ON COLUMN northwind.contract_prices.validity_ends_on IS 'Last day of the range; null when open-ended.';

COMMENT ON COLUMN northwind.party_notes.id IS 'Surrogate key of the party note.';

COMMENT ON COLUMN northwind.party_notes.body IS 'Note text in Markdown.';

COMMENT ON COLUMN northwind.party_notes.is_pinned IS 'Shown at the top of the party''s page.';

COMMENT ON COLUMN northwind.party_notes.created_at IS 'When the row was created.';

COMMENT ON COLUMN northwind.party_notes.created_by IS 'User name of the creator.';

COMMENT ON COLUMN northwind.party_notes.updated_at IS 'When the row was last changed.';

COMMENT ON COLUMN northwind.party_notes.updated_by IS 'User name of the last editor.';

COMMENT ON COLUMN northwind.stock_lots.id IS 'Surrogate key of the stock lot.';

COMMENT ON COLUMN northwind.stock_lots.lot_number IS 'Lot number.';

COMMENT ON COLUMN northwind.stock_lots.supplier_lot_number IS 'Lot number on the supplier''s label.';

COMMENT ON COLUMN northwind.stock_lots.manufactured_on IS 'Production date.';

COMMENT ON COLUMN northwind.stock_lots.expires_on IS 'Expiry date, for first-expired-first-out picking.';

COMMENT ON COLUMN northwind.stock_lots.created_at IS 'When the row was created.';

COMMENT ON COLUMN northwind.stock_lots.created_by IS 'User name of the creator.';

COMMENT ON COLUMN northwind.stock_lots.updated_at IS 'When the row was last changed.';

COMMENT ON COLUMN northwind.stock_lots.updated_by IS 'User name of the last editor.';

COMMENT ON COLUMN northwind.price_change_requests.id IS 'Surrogate key of the price change request.';

COMMENT ON COLUMN northwind.price_change_requests.requested_price_amount IS 'The amount in the currency''s major unit.';

COMMENT ON COLUMN northwind.price_change_requests.requested_price_currency IS 'The ISO 4217 currency of the amount.';

COMMENT ON COLUMN northwind.price_change_requests.effective_on IS 'Date the new price should apply.';

COMMENT ON COLUMN northwind.price_change_requests.justification IS 'Reason, such as supplier cost increase.';

COMMENT ON COLUMN northwind.price_change_requests.decision IS 'pending, approved or rejected.';

COMMENT ON COLUMN northwind.price_change_requests.decided_at IS 'When the decision was made.';

COMMENT ON COLUMN northwind.price_change_requests.created_at IS 'When the row was created.';

COMMENT ON COLUMN northwind.price_change_requests.created_by IS 'User name of the creator.';

COMMENT ON COLUMN northwind.price_change_requests.updated_at IS 'When the row was last changed.';

COMMENT ON COLUMN northwind.price_change_requests.updated_by IS 'User name of the last editor.';

COMMENT ON COLUMN northwind.customer_feedback.id IS 'Surrogate key of the customer feedback.';

COMMENT ON COLUMN northwind.customer_feedback.submitted_at IS 'When it was submitted.';

COMMENT ON COLUMN northwind.customer_feedback.rating IS 'Rating from 1 to 5.';

COMMENT ON COLUMN northwind.customer_feedback.channel IS 'survey, portal or phone.';

COMMENT ON COLUMN northwind.customer_feedback.comments IS 'Free text.';

COMMENT ON COLUMN northwind.customer_feedback.follow_up_required IS 'Whether someone should call back.';

COMMENT ON COLUMN northwind.refunds.id IS 'Surrogate key of the refund.';

COMMENT ON COLUMN northwind.refunds.refund_number IS 'Refund number.';

COMMENT ON COLUMN northwind.refunds.refunded_at IS 'When it was paid out.';

COMMENT ON COLUMN northwind.refunds.amount_amount IS 'The amount in the currency''s major unit.';

COMMENT ON COLUMN northwind.refunds.amount_currency IS 'The ISO 4217 currency of the amount.';

COMMENT ON COLUMN northwind.refunds.method IS 'card, ach or check.';

COMMENT ON COLUMN northwind.refunds.status IS 'Processing state.';

COMMENT ON COLUMN northwind.refunds.reference IS 'Processor or bank reference.';

COMMENT ON COLUMN northwind.refunds.created_at IS 'When the row was created.';

COMMENT ON COLUMN northwind.refunds.created_by IS 'User name of the creator.';

COMMENT ON COLUMN northwind.refunds.updated_at IS 'When the row was last changed.';

COMMENT ON COLUMN northwind.refunds.updated_by IS 'User name of the last editor.';

COMMENT ON COLUMN northwind.rebate_programs.id IS 'Surrogate key of the rebate program.';

COMMENT ON COLUMN northwind.rebate_programs.name IS 'Program name.';

COMMENT ON COLUMN northwind.rebate_programs.description IS 'Program terms.';

COMMENT ON COLUMN northwind.rebate_programs.period_starts_on IS 'First day of the range.';

COMMENT ON COLUMN northwind.rebate_programs.period_ends_on IS 'Last day of the range; null when open-ended.';

COMMENT ON COLUMN northwind.rebate_programs.rebate_rate IS 'Rebate percentage of eligible sales.';

COMMENT ON COLUMN northwind.rebate_programs.threshold_amount_amount IS 'The amount in the currency''s major unit.';

COMMENT ON COLUMN northwind.rebate_programs.threshold_amount_currency IS 'The ISO 4217 currency of the amount.';

COMMENT ON COLUMN northwind.rebate_programs.created_at IS 'When the row was created.';

COMMENT ON COLUMN northwind.rebate_programs.created_by IS 'User name of the creator.';

COMMENT ON COLUMN northwind.rebate_programs.updated_at IS 'When the row was last changed.';

COMMENT ON COLUMN northwind.rebate_programs.updated_by IS 'User name of the last editor.';

COMMENT ON COLUMN northwind.supplier_invoices.id IS 'Surrogate key of the supplier invoice.';

COMMENT ON COLUMN northwind.supplier_invoices.invoice_number IS 'Supplier''s invoice number.';

COMMENT ON COLUMN northwind.supplier_invoices.invoice_date IS 'Invoice date.';

COMMENT ON COLUMN northwind.supplier_invoices.due_date IS 'Payment due date.';

COMMENT ON COLUMN northwind.supplier_invoices.total_amount_amount IS 'The amount in the currency''s major unit.';

COMMENT ON COLUMN northwind.supplier_invoices.total_amount_currency IS 'The ISO 4217 currency of the amount.';

COMMENT ON COLUMN northwind.supplier_invoices.tax_amount_amount IS 'The amount in the currency''s major unit.';

COMMENT ON COLUMN northwind.supplier_invoices.tax_amount_currency IS 'The ISO 4217 currency of the amount.';

COMMENT ON COLUMN northwind.supplier_invoices.status IS 'received, matched, approved, paid or disputed.';

COMMENT ON COLUMN northwind.supplier_invoices.matched_at IS 'When the three-way match succeeded.';

COMMENT ON COLUMN northwind.supplier_invoices.created_at IS 'When the row was created.';

COMMENT ON COLUMN northwind.supplier_invoices.created_by IS 'User name of the creator.';

COMMENT ON COLUMN northwind.supplier_invoices.updated_at IS 'When the row was last changed.';

COMMENT ON COLUMN northwind.supplier_invoices.updated_by IS 'User name of the last editor.';

COMMENT ON COLUMN northwind.opportunity_stages.id IS 'Surrogate key of the opportunity stage.';

COMMENT ON COLUMN northwind.opportunity_stages.default_probability IS 'Win probability assigned on entering the stage.';

COMMENT ON COLUMN northwind.opportunity_stages.is_closed IS 'Whether the stage ends the pipeline.';

COMMENT ON COLUMN northwind.opportunity_stages.is_won IS 'Whether the stage means the deal was won.';

COMMENT ON COLUMN northwind.opportunity_stages.code IS 'Short stable code used in integrations and imports.';

COMMENT ON COLUMN northwind.opportunity_stages.name IS 'Display name shown in pick lists.';

COMMENT ON COLUMN northwind.opportunity_stages.sort_order IS 'Position in pick lists.';

COMMENT ON COLUMN northwind.opportunity_stages.is_active IS 'Whether the value can be chosen for new records.';

COMMENT ON COLUMN northwind.pick_list_lines.id IS 'Surrogate key of the pick list line.';

COMMENT ON COLUMN northwind.pick_list_lines.quantity_to_pick IS 'Quantity to pick.';

COMMENT ON COLUMN northwind.pick_list_lines.quantity_picked IS 'Quantity confirmed.';

COMMENT ON COLUMN northwind.pick_list_lines.picked_at IS 'When it was confirmed.';

COMMENT ON COLUMN northwind.shipment_packages.id IS 'Surrogate key of the shipment package.';

COMMENT ON COLUMN northwind.shipment_packages.package_number IS 'Package number within the shipment.';

COMMENT ON COLUMN northwind.shipment_packages.package_type IS 'carton, pallet or envelope.';

COMMENT ON COLUMN northwind.shipment_packages.tracking_number IS 'Carrier tracking number.';

COMMENT ON COLUMN northwind.shipment_packages.dimensions_length IS 'Longest side.';

COMMENT ON COLUMN northwind.shipment_packages.dimensions_width IS 'Second side.';

COMMENT ON COLUMN northwind.shipment_packages.dimensions_height IS 'Vertical side.';

COMMENT ON COLUMN northwind.shipment_packages.dimensions_unit IS 'Unit of the three measures.';

COMMENT ON COLUMN northwind.shipment_packages.weight_value IS 'The weight.';

COMMENT ON COLUMN northwind.shipment_packages.weight_unit IS 'Unit of the weight.';

COMMENT ON COLUMN northwind.api_clients.id IS 'Surrogate key of the api client.';

COMMENT ON COLUMN northwind.api_clients.name IS 'Client name.';

COMMENT ON COLUMN northwind.api_clients.description IS 'What the integration does.';

COMMENT ON COLUMN northwind.api_clients.owner_email IS 'Contact for the integration.';

COMMENT ON COLUMN northwind.api_clients.allowed_scopes IS 'Space-separated OAuth scopes.';

COMMENT ON COLUMN northwind.api_clients.rate_limit_per_minute IS 'Request budget per minute.';

COMMENT ON COLUMN northwind.api_clients.is_enabled IS 'Whether the client can obtain tokens.';

COMMENT ON COLUMN northwind.api_clients.created_at IS 'When the row was created.';

COMMENT ON COLUMN northwind.api_clients.created_by IS 'User name of the creator.';

COMMENT ON COLUMN northwind.api_clients.updated_at IS 'When the row was last changed.';

COMMENT ON COLUMN northwind.api_clients.updated_by IS 'User name of the last editor.';

COMMENT ON COLUMN northwind.api_clients.deleted_at IS 'When the row was marked deleted; null while it is live.';

COMMENT ON COLUMN northwind.putaway_tasks.id IS 'Surrogate key of the putaway task.';

COMMENT ON COLUMN northwind.putaway_tasks.status IS 'open, in-progress or done.';

COMMENT ON COLUMN northwind.putaway_tasks.quantity IS 'Quantity to put away.';

COMMENT ON COLUMN northwind.putaway_tasks.completed_at IS 'When it was done.';

COMMENT ON COLUMN northwind.putaway_tasks.created_at IS 'When the row was created.';

COMMENT ON COLUMN northwind.putaway_tasks.created_by IS 'User name of the creator.';

COMMENT ON COLUMN northwind.putaway_tasks.updated_at IS 'When the row was last changed.';

COMMENT ON COLUMN northwind.putaway_tasks.updated_by IS 'User name of the last editor.';

COMMENT ON COLUMN northwind.request_for_quote_supplier.invited_at IS 'When the invitation was sent.';

COMMENT ON COLUMN northwind.request_for_quote_supplier.declined IS 'Whether the supplier declined to quote.';

COMMENT ON COLUMN northwind.warehouses.id IS 'Surrogate key of the warehouse.';

COMMENT ON COLUMN northwind.warehouses.code IS 'Warehouse code printed on labels.';

COMMENT ON COLUMN northwind.warehouses.name IS 'Warehouse name.';

COMMENT ON COLUMN northwind.warehouses.address_line1 IS 'Street and number, or PO box.';

COMMENT ON COLUMN northwind.warehouses.address_line2 IS 'Suite, floor or building.';

COMMENT ON COLUMN northwind.warehouses.address_city IS 'City or locality.';

COMMENT ON COLUMN northwind.warehouses.address_region IS 'State, province or county.';

COMMENT ON COLUMN northwind.warehouses.address_postal_code IS 'Postal or ZIP code.';

COMMENT ON COLUMN northwind.warehouses.address_country_code IS 'ISO country code.';

COMMENT ON COLUMN northwind.warehouses.location_latitude IS 'Degrees north of the equator.';

COMMENT ON COLUMN northwind.warehouses.location_longitude IS 'Degrees east of Greenwich.';

COMMENT ON COLUMN northwind.warehouses.time_zone IS 'IANA time zone, such as America/Denver.';

COMMENT ON COLUMN northwind.warehouses.floor_area_sqm IS 'Floor area in square meters.';

COMMENT ON COLUMN northwind.warehouses.is_active IS 'Whether it can receive and ship.';

COMMENT ON COLUMN northwind.warehouses.created_at IS 'When the row was created.';

COMMENT ON COLUMN northwind.warehouses.created_by IS 'User name of the creator.';

COMMENT ON COLUMN northwind.warehouses.updated_at IS 'When the row was last changed.';

COMMENT ON COLUMN northwind.warehouses.updated_by IS 'User name of the last editor.';

COMMENT ON COLUMN northwind.remittance_advices.id IS 'Surrogate key of the remittance advice.';

COMMENT ON COLUMN northwind.remittance_advices.received_on IS 'Date received.';

COMMENT ON COLUMN northwind.remittance_advices.payer_reference IS 'Payer''s reference.';

COMMENT ON COLUMN northwind.remittance_advices.total_amount_amount IS 'The amount in the currency''s major unit.';

COMMENT ON COLUMN northwind.remittance_advices.total_amount_currency IS 'The ISO 4217 currency of the amount.';

COMMENT ON COLUMN northwind.remittance_advices.raw_document IS 'Original document, as JSON.';

COMMENT ON COLUMN northwind.remittance_advices.is_matched IS 'Whether it was matched to a payment.';

COMMENT ON COLUMN northwind.credit_notes.id IS 'Surrogate key of the credit note.';

COMMENT ON COLUMN northwind.credit_notes.credit_note_number IS 'Credit note number.';

COMMENT ON COLUMN northwind.credit_notes.issued_on IS 'Issue date.';

COMMENT ON COLUMN northwind.credit_notes.reason IS 'Why the credit was given.';

COMMENT ON COLUMN northwind.credit_notes.total_amount_amount IS 'The amount in the currency''s major unit.';

COMMENT ON COLUMN northwind.credit_notes.total_amount_currency IS 'The ISO 4217 currency of the amount.';

COMMENT ON COLUMN northwind.credit_notes.status IS 'open, applied or refunded.';

COMMENT ON COLUMN northwind.credit_notes.created_at IS 'When the row was created.';

COMMENT ON COLUMN northwind.credit_notes.created_by IS 'User name of the creator.';

COMMENT ON COLUMN northwind.credit_notes.updated_at IS 'When the row was last changed.';

COMMENT ON COLUMN northwind.credit_notes.updated_by IS 'User name of the last editor.';

COMMENT ON COLUMN northwind.communication_preferences.id IS 'Surrogate key of the communication preference.';

COMMENT ON COLUMN northwind.communication_preferences.channel IS 'Channel, such as email or sms.';

COMMENT ON COLUMN northwind.communication_preferences.topic IS 'Topic, such as promotions or order-updates.';

COMMENT ON COLUMN northwind.communication_preferences.opted_in IS 'Whether the contact agreed to receive it.';

COMMENT ON COLUMN northwind.communication_preferences.changed_at IS 'When the preference was last set.';

COMMENT ON COLUMN northwind.communication_preferences.source IS 'Where it was set, such as portal or phone.';

COMMENT ON COLUMN northwind.sales_order_attachments.id IS 'Surrogate key of the sales order attachment.';

COMMENT ON COLUMN northwind.sales_order_attachments.file_name IS 'Original file name.';

COMMENT ON COLUMN northwind.sales_order_attachments.content_type IS 'MIME type.';

COMMENT ON COLUMN northwind.sales_order_attachments.size_bytes IS 'File size.';

COMMENT ON COLUMN northwind.sales_order_attachments.storage_key IS 'Object storage key.';

COMMENT ON COLUMN northwind.sales_order_attachments.uploaded_at IS 'When it was uploaded.';

COMMENT ON COLUMN northwind.sales_order_attachments.created_at IS 'When the row was created.';

COMMENT ON COLUMN northwind.sales_order_attachments.created_by IS 'User name of the creator.';

COMMENT ON COLUMN northwind.sales_order_attachments.updated_at IS 'When the row was last changed.';

COMMENT ON COLUMN northwind.sales_order_attachments.updated_by IS 'User name of the last editor.';

COMMENT ON COLUMN northwind.carrier_services.id IS 'Surrogate key of the carrier service.';

COMMENT ON COLUMN northwind.carrier_services.code IS 'Carrier''s service code.';

COMMENT ON COLUMN northwind.carrier_services.name IS 'Service name.';

COMMENT ON COLUMN northwind.carrier_services.service_level IS 'ground, express, overnight, ltl or ftl.';

COMMENT ON COLUMN northwind.carrier_services.transit_days IS 'Typical business days in transit.';

COMMENT ON COLUMN northwind.carrier_services.is_active IS 'Whether it can be booked.';

COMMENT ON COLUMN northwind.carrier_services.created_at IS 'When the row was created.';

COMMENT ON COLUMN northwind.carrier_services.created_by IS 'User name of the creator.';

COMMENT ON COLUMN northwind.carrier_services.updated_at IS 'When the row was last changed.';

COMMENT ON COLUMN northwind.carrier_services.updated_by IS 'User name of the last editor.';

COMMENT ON COLUMN northwind.shipping_labels.id IS 'Surrogate key of the shipping label.';

COMMENT ON COLUMN northwind.shipping_labels.format IS 'ZPL or PDF.';

COMMENT ON COLUMN northwind.shipping_labels.storage_key IS 'Object storage key of the label file.';

COMMENT ON COLUMN northwind.shipping_labels.generated_at IS 'When the label was generated.';

COMMENT ON COLUMN northwind.employees.id IS 'Surrogate key of the employee.';

COMMENT ON COLUMN northwind.employees.employee_number IS 'Payroll number.';

COMMENT ON COLUMN northwind.employees.name_given_name IS 'First or given name.';

COMMENT ON COLUMN northwind.employees.name_family_name IS 'Last or family name.';

COMMENT ON COLUMN northwind.employees.name_title IS 'Salutation, such as Dr. or Ms.';

COMMENT ON COLUMN northwind.employees.work_email IS 'Company email address.';

COMMENT ON COLUMN northwind.employees.work_phone_number IS 'Number in E.164 form, such as +14155550100.';

COMMENT ON COLUMN northwind.employees.work_phone_extension IS 'Internal extension.';

COMMENT ON COLUMN northwind.employees.job_title IS 'Position title.';

COMMENT ON COLUMN northwind.employees.hired_on IS 'First day of employment.';

COMMENT ON COLUMN northwind.employees.terminated_on IS 'Last day of employment; null while employed.';

COMMENT ON COLUMN northwind.employees.is_sales_rep IS 'Whether the employee can own customer accounts and earn commission.';

COMMENT ON COLUMN northwind.employees.created_at IS 'When the row was created.';

COMMENT ON COLUMN northwind.employees.created_by IS 'User name of the creator.';

COMMENT ON COLUMN northwind.employees.updated_at IS 'When the row was last changed.';

COMMENT ON COLUMN northwind.employees.updated_by IS 'User name of the last editor.';

COMMENT ON COLUMN northwind.employees.deleted_at IS 'When the row was marked deleted; null while it is live.';

COMMENT ON COLUMN northwind.product_packagings.id IS 'Surrogate key of the product packaging.';

COMMENT ON COLUMN northwind.product_packagings.packaging_level IS 'Packaging level - each, inner, case or pallet.';

COMMENT ON COLUMN northwind.product_packagings.units_per_package IS 'Base units in one package.';

COMMENT ON COLUMN northwind.product_packagings.dimensions_length IS 'Longest side.';

COMMENT ON COLUMN northwind.product_packagings.dimensions_width IS 'Second side.';

COMMENT ON COLUMN northwind.product_packagings.dimensions_height IS 'Vertical side.';

COMMENT ON COLUMN northwind.product_packagings.dimensions_unit IS 'Unit of the three measures.';

COMMENT ON COLUMN northwind.product_packagings.gross_weight_value IS 'The weight.';

COMMENT ON COLUMN northwind.product_packagings.gross_weight_unit IS 'Unit of the weight.';

COMMENT ON COLUMN northwind.product_packagings.gtin IS 'GTIN of the package.';

COMMENT ON COLUMN northwind.cancellation_reasons.id IS 'Surrogate key of the cancellation reason.';

COMMENT ON COLUMN northwind.cancellation_reasons.counts_as_lost_sale IS 'Whether the cancellation counts as a lost sale in reports.';

COMMENT ON COLUMN northwind.cancellation_reasons.code IS 'Short stable code used in integrations and imports.';

COMMENT ON COLUMN northwind.cancellation_reasons.name IS 'Display name shown in pick lists.';

COMMENT ON COLUMN northwind.cancellation_reasons.sort_order IS 'Position in pick lists.';

COMMENT ON COLUMN northwind.cancellation_reasons.is_active IS 'Whether the value can be chosen for new records.';

COMMENT ON COLUMN northwind.purchase_order_lines.id IS 'Surrogate key of the purchase order line.';

COMMENT ON COLUMN northwind.purchase_order_lines.line_number IS 'Line number.';

COMMENT ON COLUMN northwind.purchase_order_lines.quantity IS 'Quantity ordered.';

COMMENT ON COLUMN northwind.purchase_order_lines.unit_cost_amount IS 'The amount in the currency''s major unit.';

COMMENT ON COLUMN northwind.purchase_order_lines.unit_cost_currency IS 'The ISO 4217 currency of the amount.';

COMMENT ON COLUMN northwind.purchase_order_lines.line_total_amount IS 'The amount in the currency''s major unit.';

COMMENT ON COLUMN northwind.purchase_order_lines.line_total_currency IS 'The ISO 4217 currency of the amount.';

COMMENT ON COLUMN northwind.purchase_order_lines.expected_on IS 'Line delivery date, when split.';

COMMENT ON COLUMN northwind.purchase_order_lines.received_quantity IS 'Quantity received so far.';

COMMENT ON COLUMN northwind.purchase_order_lines.is_closed IS 'Whether no more receipts are expected.';

COMMENT ON COLUMN northwind.purchase_order_lines.created_at IS 'When the row was created.';

COMMENT ON COLUMN northwind.purchase_order_lines.created_by IS 'User name of the creator.';

COMMENT ON COLUMN northwind.purchase_order_lines.updated_at IS 'When the row was last changed.';

COMMENT ON COLUMN northwind.purchase_order_lines.updated_by IS 'User name of the last editor.';

COMMENT ON COLUMN northwind.payments.id IS 'Surrogate key of the payment.';

COMMENT ON COLUMN northwind.payments.payment_number IS 'Payment number.';

COMMENT ON COLUMN northwind.payments.received_at IS 'When the payment was received.';

COMMENT ON COLUMN northwind.payments.amount_amount IS 'The amount in the currency''s major unit.';

COMMENT ON COLUMN northwind.payments.amount_currency IS 'The ISO 4217 currency of the amount.';

COMMENT ON COLUMN northwind.payments.unapplied_amount_amount IS 'The amount in the currency''s major unit.';

COMMENT ON COLUMN northwind.payments.unapplied_amount_currency IS 'The ISO 4217 currency of the amount.';

COMMENT ON COLUMN northwind.payments.status IS 'Processing state.';

COMMENT ON COLUMN northwind.payments.reference IS 'Payer''s reference, such as a check number.';

COMMENT ON COLUMN northwind.payments.created_at IS 'When the row was created.';

COMMENT ON COLUMN northwind.payments.created_by IS 'User name of the creator.';

COMMENT ON COLUMN northwind.payments.updated_at IS 'When the row was last changed.';

COMMENT ON COLUMN northwind.payments.updated_by IS 'User name of the last editor.';

COMMENT ON COLUMN northwind.languages.code IS 'BCP 47 language tag, such as en or fr-CA; the natural key.';

COMMENT ON COLUMN northwind.languages.name IS 'English name.';

COMMENT ON COLUMN northwind.languages.native_name IS 'Name in the language itself.';

COMMENT ON COLUMN northwind.languages.is_right_to_left IS 'Whether text is written right to left.';

COMMENT ON COLUMN northwind.warehouse_zones.id IS 'Surrogate key of the warehouse zone.';

COMMENT ON COLUMN northwind.warehouse_zones.code IS 'Zone code within the warehouse.';

COMMENT ON COLUMN northwind.warehouse_zones.name IS 'Zone name.';

COMMENT ON COLUMN northwind.warehouse_zones.zone_type IS 'receiving, bulk, pick, staging, quarantine or shipping.';

COMMENT ON COLUMN northwind.warehouse_zones.created_at IS 'When the row was created.';

COMMENT ON COLUMN northwind.warehouse_zones.created_by IS 'User name of the creator.';

COMMENT ON COLUMN northwind.warehouse_zones.updated_at IS 'When the row was last changed.';

COMMENT ON COLUMN northwind.warehouse_zones.updated_by IS 'User name of the last editor.';

COMMENT ON COLUMN northwind.stock_movements.id IS 'Surrogate key of the stock movement.';

COMMENT ON COLUMN northwind.stock_movements.movement_type IS 'Kind of movement.';

COMMENT ON COLUMN northwind.stock_movements.quantity IS 'Quantity moved, in the base unit; negative for issues.';

COMMENT ON COLUMN northwind.stock_movements.occurred_at IS 'When the movement happened.';

COMMENT ON COLUMN northwind.stock_movements.reference_document IS 'Source document number.';

COMMENT ON COLUMN northwind.stock_movements.unit_cost_amount IS 'The amount in the currency''s major unit.';

COMMENT ON COLUMN northwind.stock_movements.unit_cost_currency IS 'The ISO 4217 currency of the amount.';

COMMENT ON COLUMN northwind.reorder_suggestions.id IS 'Surrogate key of the reorder suggestion.';

COMMENT ON COLUMN northwind.reorder_suggestions.suggested_quantity IS 'Quantity to order.';

COMMENT ON COLUMN northwind.reorder_suggestions.suggested_on IS 'Date computed.';

COMMENT ON COLUMN northwind.reorder_suggestions.is_accepted IS 'Whether the buyer accepted it; null while undecided.';

COMMENT ON COLUMN northwind.reorder_suggestions.dismissed_reason IS 'Why it was dismissed.';

COMMENT ON COLUMN northwind.promotion_rewards.id IS 'Surrogate key of the promotion reward.';

COMMENT ON COLUMN northwind.promotion_rewards.reward_type IS 'Kind of benefit.';

COMMENT ON COLUMN northwind.promotion_rewards.value IS 'Percentage, amount or price, depending on the type.';

COMMENT ON COLUMN northwind.promotion_rewards.free_quantity IS 'Free units, for free goods.';

COMMENT ON COLUMN northwind.product_collections.id IS 'Surrogate key of the product collection.';

COMMENT ON COLUMN northwind.product_collections.name IS 'Collection name.';

COMMENT ON COLUMN northwind.product_collections.description IS 'Description shown to customers.';

COMMENT ON COLUMN northwind.product_collections.is_featured IS 'Shown on the portal home page.';

COMMENT ON COLUMN northwind.product_collections.validity_starts_on IS 'First day of the range.';

COMMENT ON COLUMN northwind.product_collections.validity_ends_on IS 'Last day of the range; null when open-ended.';

COMMENT ON COLUMN northwind.product_collections.created_at IS 'When the row was created.';

COMMENT ON COLUMN northwind.product_collections.created_by IS 'User name of the creator.';

COMMENT ON COLUMN northwind.product_collections.updated_at IS 'When the row was last changed.';

COMMENT ON COLUMN northwind.product_collections.updated_by IS 'User name of the last editor.';

COMMENT ON COLUMN northwind.freight_rates.id IS 'Surrogate key of the freight rate.';

COMMENT ON COLUMN northwind.freight_rates.weight_from_kg IS 'Lower bound of the weight band.';

COMMENT ON COLUMN northwind.freight_rates.weight_to_kg IS 'Upper bound; null for no limit.';

COMMENT ON COLUMN northwind.freight_rates.rate_amount IS 'The amount in the currency''s major unit.';

COMMENT ON COLUMN northwind.freight_rates.rate_currency IS 'The ISO 4217 currency of the amount.';

COMMENT ON COLUMN northwind.freight_rates.validity_starts_on IS 'First day of the range.';

COMMENT ON COLUMN northwind.freight_rates.validity_ends_on IS 'Last day of the range; null when open-ended.';

COMMENT ON COLUMN northwind.freight_rates.created_at IS 'When the row was created.';

COMMENT ON COLUMN northwind.freight_rates.created_by IS 'User name of the creator.';

COMMENT ON COLUMN northwind.freight_rates.updated_at IS 'When the row was last changed.';

COMMENT ON COLUMN northwind.freight_rates.updated_by IS 'User name of the last editor.';

COMMENT ON COLUMN northwind.coupon_redemptions.id IS 'Surrogate key of the coupon redemption.';

COMMENT ON COLUMN northwind.coupon_redemptions.redeemed_at IS 'When the coupon was applied.';

COMMENT ON COLUMN northwind.coupon_redemptions.discount_amount_amount IS 'The amount in the currency''s major unit.';

COMMENT ON COLUMN northwind.coupon_redemptions.discount_amount_currency IS 'The ISO 4217 currency of the amount.';

COMMENT ON COLUMN northwind.sales_quotes.id IS 'Surrogate key of the sales quote.';

COMMENT ON COLUMN northwind.sales_quotes.quote_number IS 'Quote number.';

COMMENT ON COLUMN northwind.sales_quotes.issued_on IS 'Date the quote was sent.';

COMMENT ON COLUMN northwind.sales_quotes.valid_until IS 'Last day the prices hold.';

COMMENT ON COLUMN northwind.sales_quotes.status IS 'Lifecycle state.';

COMMENT ON COLUMN northwind.sales_quotes.total_amount_amount IS 'The amount in the currency''s major unit.';

COMMENT ON COLUMN northwind.sales_quotes.total_amount_currency IS 'The ISO 4217 currency of the amount.';

COMMENT ON COLUMN northwind.sales_quotes.notes IS 'Terms and remarks.';

COMMENT ON COLUMN northwind.sales_quotes.created_at IS 'When the row was created.';

COMMENT ON COLUMN northwind.sales_quotes.created_by IS 'User name of the creator.';

COMMENT ON COLUMN northwind.sales_quotes.updated_at IS 'When the row was last changed.';

COMMENT ON COLUMN northwind.sales_quotes.updated_by IS 'User name of the last editor.';

COMMENT ON COLUMN northwind.sales_quotes.deleted_at IS 'When the row was marked deleted; null while it is live.';

COMMENT ON COLUMN northwind.stock_transfer_lines.id IS 'Surrogate key of the stock transfer line.';

COMMENT ON COLUMN northwind.stock_transfer_lines.quantity IS 'Quantity shipped.';

COMMENT ON COLUMN northwind.stock_transfer_lines.received_quantity IS 'Quantity received.';

COMMENT ON COLUMN northwind.replenishment_rules.id IS 'Surrogate key of the replenishment rule.';

COMMENT ON COLUMN northwind.replenishment_rules.min_quantity IS 'Reorder when available stock falls below it.';

COMMENT ON COLUMN northwind.replenishment_rules.max_quantity IS 'Order up to this level.';

COMMENT ON COLUMN northwind.replenishment_rules.reorder_quantity IS 'Fixed order quantity, when used instead of max.';

COMMENT ON COLUMN northwind.replenishment_rules.is_active IS 'Whether the rule is evaluated.';

COMMENT ON COLUMN northwind.replenishment_rules.created_at IS 'When the row was created.';

COMMENT ON COLUMN northwind.replenishment_rules.created_by IS 'User name of the creator.';

COMMENT ON COLUMN northwind.replenishment_rules.updated_at IS 'When the row was last changed.';

COMMENT ON COLUMN northwind.replenishment_rules.updated_by IS 'User name of the last editor.';

COMMENT ON COLUMN northwind.supplier_contracts.id IS 'Surrogate key of the supplier contract.';

COMMENT ON COLUMN northwind.supplier_contracts.contract_number IS 'Contract number.';

COMMENT ON COLUMN northwind.supplier_contracts.validity_starts_on IS 'First day of the range.';

COMMENT ON COLUMN northwind.supplier_contracts.validity_ends_on IS 'Last day of the range; null when open-ended.';

COMMENT ON COLUMN northwind.supplier_contracts.terms IS 'Key terms.';

COMMENT ON COLUMN northwind.supplier_contracts.auto_renew IS 'Whether it renews automatically.';

COMMENT ON COLUMN northwind.supplier_contracts.spend_commitment_amount IS 'The amount in the currency''s major unit.';

COMMENT ON COLUMN northwind.supplier_contracts.spend_commitment_currency IS 'The ISO 4217 currency of the amount.';

COMMENT ON COLUMN northwind.supplier_contracts.created_at IS 'When the row was created.';

COMMENT ON COLUMN northwind.supplier_contracts.created_by IS 'User name of the creator.';

COMMENT ON COLUMN northwind.supplier_contracts.updated_at IS 'When the row was last changed.';

COMMENT ON COLUMN northwind.supplier_contracts.updated_by IS 'User name of the last editor.';

COMMENT ON COLUMN northwind.product_images.id IS 'Surrogate key of the product image.';

COMMENT ON COLUMN northwind.product_images.url IS 'Image URL.';

COMMENT ON COLUMN northwind.product_images.alt_text IS 'Accessible description.';

COMMENT ON COLUMN northwind.product_images.width_px IS 'Width in pixels.';

COMMENT ON COLUMN northwind.product_images.height_px IS 'Height in pixels.';

COMMENT ON COLUMN northwind.product_images.is_primary IS 'Image used in listings.';

COMMENT ON COLUMN northwind.bundle_components.id IS 'Surrogate key of the bundle component.';

COMMENT ON COLUMN northwind.bundle_components.quantity IS 'Quantity per bundle, in the product''s base unit.';

COMMENT ON COLUMN northwind.standing_order_lines.id IS 'Surrogate key of the standing order line.';

COMMENT ON COLUMN northwind.standing_order_lines.quantity IS 'Quantity per run.';

COMMENT ON COLUMN northwind.price_lists.id IS 'Surrogate key of the price list.';

COMMENT ON COLUMN northwind.price_lists.code IS 'Price list code.';

COMMENT ON COLUMN northwind.price_lists.name IS 'Price list name.';

COMMENT ON COLUMN northwind.price_lists.validity_starts_on IS 'First day of the range.';

COMMENT ON COLUMN northwind.price_lists.validity_ends_on IS 'Last day of the range; null when open-ended.';

COMMENT ON COLUMN northwind.price_lists.is_default IS 'List used when a customer has no other list.';

COMMENT ON COLUMN northwind.price_lists.prices_include_tax IS 'Whether prices are tax-inclusive.';

COMMENT ON COLUMN northwind.price_lists.created_at IS 'When the row was created.';

COMMENT ON COLUMN northwind.price_lists.created_by IS 'User name of the creator.';

COMMENT ON COLUMN northwind.price_lists.updated_at IS 'When the row was last changed.';

COMMENT ON COLUMN northwind.price_lists.updated_by IS 'User name of the last editor.';

COMMENT ON COLUMN northwind.state_provinces.id IS 'Surrogate key of the state province.';

COMMENT ON COLUMN northwind.state_provinces.code IS 'ISO 3166-2 subdivision code without the country prefix, such as CA.';

COMMENT ON COLUMN northwind.state_provinces.name IS 'Name of the subdivision.';

COMMENT ON COLUMN northwind.state_provinces.subdivision_type IS 'Kind of subdivision, such as state, province or county.';

COMMENT ON COLUMN northwind.blanket_order_lines.id IS 'Surrogate key of the blanket order line.';

COMMENT ON COLUMN northwind.blanket_order_lines.committed_quantity IS 'Quantity committed.';

COMMENT ON COLUMN northwind.blanket_order_lines.released_quantity IS 'Quantity released so far.';

COMMENT ON COLUMN northwind.blanket_order_lines.unit_price_amount IS 'The amount in the currency''s major unit.';

COMMENT ON COLUMN northwind.blanket_order_lines.unit_price_currency IS 'The ISO 4217 currency of the amount.';

COMMENT ON COLUMN northwind.products.id IS 'Surrogate key of the product.';

COMMENT ON COLUMN northwind.products.sku IS 'Stock keeping unit of the base product.';

COMMENT ON COLUMN northwind.products.short_description IS 'One-paragraph description for listings.';

COMMENT ON COLUMN northwind.products.long_description IS 'Full description in Markdown.';

COMMENT ON COLUMN northwind.products.status IS 'Lifecycle state.';

COMMENT ON COLUMN northwind.products.is_serialized IS 'Whether each unit carries a serial number.';

COMMENT ON COLUMN northwind.products.is_lot_controlled IS 'Whether stock is tracked by lot or batch.';

COMMENT ON COLUMN northwind.products.shelf_life_days IS 'Days from production to expiry, for perishables.';

COMMENT ON COLUMN northwind.products.dimensions_length IS 'Longest side.';

COMMENT ON COLUMN northwind.products.dimensions_width IS 'Second side.';

COMMENT ON COLUMN northwind.products.dimensions_height IS 'Vertical side.';

COMMENT ON COLUMN northwind.products.dimensions_unit IS 'Unit of the three measures.';

COMMENT ON COLUMN northwind.products.weight_value IS 'The weight.';

COMMENT ON COLUMN northwind.products.weight_unit IS 'Unit of the weight.';

COMMENT ON COLUMN northwind.products.list_price_amount IS 'The amount in the currency''s major unit.';

COMMENT ON COLUMN northwind.products.list_price_currency IS 'The ISO 4217 currency of the amount.';

COMMENT ON COLUMN northwind.products.country_of_origin IS 'Where the goods were produced, for customs.';

COMMENT ON COLUMN northwind.products.launched_on IS 'First sale date.';

COMMENT ON COLUMN northwind.products.discontinued_on IS 'Last sale date.';

COMMENT ON COLUMN northwind.products.created_at IS 'When the row was created.';

COMMENT ON COLUMN northwind.products.created_by IS 'User name of the creator.';

COMMENT ON COLUMN northwind.products.updated_at IS 'When the row was last changed.';

COMMENT ON COLUMN northwind.products.updated_by IS 'User name of the last editor.';

COMMENT ON COLUMN northwind.products.deleted_at IS 'When the row was marked deleted; null while it is live.';

COMMENT ON COLUMN northwind.purchase_approvals.id IS 'Surrogate key of the purchase approval.';

COMMENT ON COLUMN northwind.purchase_approvals.approval_level IS 'Step number.';

COMMENT ON COLUMN northwind.purchase_approvals.decision IS 'pending, approved or rejected.';

COMMENT ON COLUMN northwind.purchase_approvals.decided_at IS 'When the approver decided.';

COMMENT ON COLUMN northwind.purchase_approvals.comment IS 'Approver''s comment.';

COMMENT ON COLUMN northwind.purchase_approvals.created_at IS 'When the row was created.';

COMMENT ON COLUMN northwind.purchase_approvals.created_by IS 'User name of the creator.';

COMMENT ON COLUMN northwind.purchase_approvals.updated_at IS 'When the row was last changed.';

COMMENT ON COLUMN northwind.purchase_approvals.updated_by IS 'User name of the last editor.';

COMMENT ON COLUMN northwind.sales_territories.id IS 'Surrogate key of the sales territory.';

COMMENT ON COLUMN northwind.sales_territories.code IS 'Territory code.';

COMMENT ON COLUMN northwind.sales_territories.name IS 'Territory name.';

COMMENT ON COLUMN northwind.sales_territories.annual_quota_amount IS 'The amount in the currency''s major unit.';

COMMENT ON COLUMN northwind.sales_territories.annual_quota_currency IS 'The ISO 4217 currency of the amount.';

COMMENT ON COLUMN northwind.sales_territories.created_at IS 'When the row was created.';

COMMENT ON COLUMN northwind.sales_territories.created_by IS 'User name of the creator.';

COMMENT ON COLUMN northwind.sales_territories.updated_at IS 'When the row was last changed.';

COMMENT ON COLUMN northwind.sales_territories.updated_by IS 'User name of the last editor.';

COMMENT ON COLUMN northwind.repair_orders.id IS 'Surrogate key of the repair order.';

COMMENT ON COLUMN northwind.repair_orders.repair_number IS 'Repair number.';

COMMENT ON COLUMN northwind.repair_orders.received_on IS 'Date the unit arrived.';

COMMENT ON COLUMN northwind.repair_orders.status IS 'open, diagnosing, repairing or done.';

COMMENT ON COLUMN northwind.repair_orders.diagnosis IS 'Technician''s diagnosis.';

COMMENT ON COLUMN northwind.repair_orders.labor_hours IS 'Hours worked.';

COMMENT ON COLUMN northwind.repair_orders.cost_amount IS 'The amount in the currency''s major unit.';

COMMENT ON COLUMN northwind.repair_orders.cost_currency IS 'The ISO 4217 currency of the amount.';

COMMENT ON COLUMN northwind.repair_orders.completed_on IS 'Date finished.';

COMMENT ON COLUMN northwind.repair_orders.created_at IS 'When the row was created.';

COMMENT ON COLUMN northwind.repair_orders.created_by IS 'User name of the creator.';

COMMENT ON COLUMN northwind.repair_orders.updated_at IS 'When the row was last changed.';

COMMENT ON COLUMN northwind.repair_orders.updated_by IS 'User name of the last editor.';

COMMENT ON COLUMN northwind.product_categories.id IS 'Surrogate key of the product category.';

COMMENT ON COLUMN northwind.product_categories.code IS 'Stable category code used in feeds.';

COMMENT ON COLUMN northwind.product_categories.name IS 'Category name.';

COMMENT ON COLUMN northwind.product_categories.description IS 'Category landing page copy.';

COMMENT ON COLUMN northwind.product_categories.sort_order IS 'Position among its siblings.';

COMMENT ON COLUMN northwind.product_categories.is_active IS 'Whether the category is shown.';

COMMENT ON COLUMN northwind.product_categories.created_at IS 'When the row was created.';

COMMENT ON COLUMN northwind.product_categories.created_by IS 'User name of the creator.';

COMMENT ON COLUMN northwind.product_categories.updated_at IS 'When the row was last changed.';

COMMENT ON COLUMN northwind.product_categories.updated_by IS 'User name of the last editor.';

COMMENT ON COLUMN northwind.supplier_invoice_lines.id IS 'Surrogate key of the supplier invoice line.';

COMMENT ON COLUMN northwind.supplier_invoice_lines.description IS 'Line text.';

COMMENT ON COLUMN northwind.supplier_invoice_lines.quantity IS 'Quantity invoiced.';

COMMENT ON COLUMN northwind.supplier_invoice_lines.unit_cost_amount IS 'The amount in the currency''s major unit.';

COMMENT ON COLUMN northwind.supplier_invoice_lines.unit_cost_currency IS 'The ISO 4217 currency of the amount.';

COMMENT ON COLUMN northwind.supplier_invoice_lines.line_total_amount IS 'The amount in the currency''s major unit.';

COMMENT ON COLUMN northwind.supplier_invoice_lines.line_total_currency IS 'The ISO 4217 currency of the amount.';

COMMENT ON COLUMN northwind.drivers.id IS 'Surrogate key of the driver.';

COMMENT ON COLUMN northwind.drivers.license_number IS 'Driving license number.';

COMMENT ON COLUMN northwind.drivers.license_class IS 'License class, such as CDL-B.';

COMMENT ON COLUMN northwind.drivers.license_expires_on IS 'License expiry.';

COMMENT ON COLUMN northwind.drivers.mobile_phone_number IS 'Number in E.164 form, such as +14155550100.';

COMMENT ON COLUMN northwind.drivers.mobile_phone_extension IS 'Internal extension.';

COMMENT ON COLUMN northwind.drivers.created_at IS 'When the row was created.';

COMMENT ON COLUMN northwind.drivers.created_by IS 'User name of the creator.';

COMMENT ON COLUMN northwind.drivers.updated_at IS 'When the row was last changed.';

COMMENT ON COLUMN northwind.drivers.updated_by IS 'User name of the last editor.';

COMMENT ON COLUMN northwind.vehicles.id IS 'Surrogate key of the vehicle.';

COMMENT ON COLUMN northwind.vehicles.registration_number IS 'License plate.';

COMMENT ON COLUMN northwind.vehicles.vehicle_type IS 'box-truck, van or tractor.';

COMMENT ON COLUMN northwind.vehicles.capacity_weight_value IS 'The weight.';

COMMENT ON COLUMN northwind.vehicles.capacity_weight_unit IS 'Unit of the weight.';

COMMENT ON COLUMN northwind.vehicles.capacity_volume_m3 IS 'Cargo volume.';

COMMENT ON COLUMN northwind.vehicles.is_refrigerated IS 'Whether it can carry chilled goods.';

COMMENT ON COLUMN northwind.vehicles.is_active IS 'Whether it is in service.';

COMMENT ON COLUMN northwind.vehicles.created_at IS 'When the row was created.';

COMMENT ON COLUMN northwind.vehicles.created_by IS 'User name of the creator.';

COMMENT ON COLUMN northwind.vehicles.updated_at IS 'When the row was last changed.';

COMMENT ON COLUMN northwind.vehicles.updated_by IS 'User name of the last editor.';

COMMENT ON COLUMN northwind.cycle_count_lines.id IS 'Surrogate key of the cycle count line.';

COMMENT ON COLUMN northwind.cycle_count_lines.expected_quantity IS 'Quantity on record.';

COMMENT ON COLUMN northwind.cycle_count_lines.counted_quantity IS 'Quantity found.';

COMMENT ON COLUMN northwind.cycle_count_lines.counted_at IS 'When it was counted.';

COMMENT ON COLUMN northwind.cycle_count_lines.variance_approved IS 'Whether a variance was approved.';

COMMENT ON COLUMN northwind.sales_activities.id IS 'Surrogate key of the sales activity.';

COMMENT ON COLUMN northwind.sales_activities.activity_type IS 'Kind of activity.';

COMMENT ON COLUMN northwind.sales_activities.subject IS 'One-line summary.';

COMMENT ON COLUMN northwind.sales_activities.due_at IS 'When the activity is scheduled or due.';

COMMENT ON COLUMN northwind.sales_activities.completed_at IS 'When it was done.';

COMMENT ON COLUMN northwind.sales_activities.notes IS 'Outcome and details.';

COMMENT ON COLUMN northwind.sales_activities.created_at IS 'When the row was created.';

COMMENT ON COLUMN northwind.sales_activities.created_by IS 'User name of the creator.';

COMMENT ON COLUMN northwind.sales_activities.updated_at IS 'When the row was last changed.';

COMMENT ON COLUMN northwind.sales_activities.updated_by IS 'User name of the last editor.';

COMMENT ON COLUMN northwind.bin_locations.id IS 'Surrogate key of the bin location.';

COMMENT ON COLUMN northwind.bin_locations.code IS 'Location code, such as A-01-02-03.';

COMMENT ON COLUMN northwind.bin_locations.aisle IS 'Aisle.';

COMMENT ON COLUMN northwind.bin_locations.rack IS 'Rack or bay.';

COMMENT ON COLUMN northwind.bin_locations.level IS 'Shelf level.';

COMMENT ON COLUMN northwind.bin_locations.position IS 'Position on the shelf.';

COMMENT ON COLUMN northwind.bin_locations.max_weight_value IS 'The weight.';

COMMENT ON COLUMN northwind.bin_locations.max_weight_unit IS 'Unit of the weight.';

COMMENT ON COLUMN northwind.bin_locations.is_pick_face IS 'Whether pickers pick from it.';

COMMENT ON COLUMN northwind.bin_locations.is_active IS 'Whether stock can be put away there.';

COMMENT ON COLUMN northwind.inventory_cost_layers.id IS 'Surrogate key of the inventory cost layer.';

COMMENT ON COLUMN northwind.inventory_cost_layers.received_on IS 'Date the layer was created.';

COMMENT ON COLUMN northwind.inventory_cost_layers.quantity IS 'Quantity received.';

COMMENT ON COLUMN northwind.inventory_cost_layers.remaining_quantity IS 'Quantity not yet consumed.';

COMMENT ON COLUMN northwind.inventory_cost_layers.unit_cost_amount IS 'The amount in the currency''s major unit.';

COMMENT ON COLUMN northwind.inventory_cost_layers.unit_cost_currency IS 'The ISO 4217 currency of the amount.';

COMMENT ON COLUMN northwind.agreement_lines.id IS 'Surrogate key of the agreement line.';

COMMENT ON COLUMN northwind.agreement_lines.agreed_price_amount IS 'The amount in the currency''s major unit.';

COMMENT ON COLUMN northwind.agreement_lines.agreed_price_currency IS 'The ISO 4217 currency of the amount.';

COMMENT ON COLUMN northwind.agreement_lines.discount IS 'Discount off list price, when no fixed price is agreed.';

COMMENT ON COLUMN northwind.agreement_lines.minimum_quantity IS 'Minimum order quantity for the price.';

COMMENT ON COLUMN northwind.sign_in_attempts.id IS 'Surrogate key of the sign in attempt.';

COMMENT ON COLUMN northwind.sign_in_attempts.attempted_at IS 'When the attempt happened.';

COMMENT ON COLUMN northwind.sign_in_attempts.user_name IS 'User name entered.';

COMMENT ON COLUMN northwind.sign_in_attempts.succeeded IS 'Whether the attempt succeeded.';

COMMENT ON COLUMN northwind.sign_in_attempts.failure_reason IS 'Why it failed, such as bad-password or locked.';

COMMENT ON COLUMN northwind.sign_in_attempts.ip_address IS 'Client IP address.';

COMMENT ON COLUMN northwind.product_attribute_options.id IS 'Surrogate key of the product attribute option.';

COMMENT ON COLUMN northwind.product_attribute_options.value IS 'Option value.';

COMMENT ON COLUMN northwind.product_attribute_options.label IS 'Display label when it differs from the value.';

COMMENT ON COLUMN northwind.industries.id IS 'Surrogate key of the industry.';

COMMENT ON COLUMN northwind.industries.naics_code IS 'NAICS code of the sector.';

COMMENT ON COLUMN northwind.industries.code IS 'Short stable code used in integrations and imports.';

COMMENT ON COLUMN northwind.industries.name IS 'Display name shown in pick lists.';

COMMENT ON COLUMN northwind.industries.sort_order IS 'Position in pick lists.';

COMMENT ON COLUMN northwind.industries.is_active IS 'Whether the value can be chosen for new records.';

COMMENT ON COLUMN northwind.disputes.id IS 'Surrogate key of the dispute.';

COMMENT ON COLUMN northwind.disputes.dispute_number IS 'Dispute number from the processor.';

COMMENT ON COLUMN northwind.disputes.opened_on IS 'Date opened.';

COMMENT ON COLUMN northwind.disputes.reason_code IS 'Network reason code.';

COMMENT ON COLUMN northwind.disputes.amount_amount IS 'The amount in the currency''s major unit.';

COMMENT ON COLUMN northwind.disputes.amount_currency IS 'The ISO 4217 currency of the amount.';

COMMENT ON COLUMN northwind.disputes.status IS 'open, evidence-submitted, won or lost.';

COMMENT ON COLUMN northwind.disputes.resolved_on IS 'Date resolved.';

COMMENT ON COLUMN northwind.disputes.resolution IS 'Outcome notes.';

COMMENT ON COLUMN northwind.disputes.created_at IS 'When the row was created.';

COMMENT ON COLUMN northwind.disputes.created_by IS 'User name of the creator.';

COMMENT ON COLUMN northwind.disputes.updated_at IS 'When the row was last changed.';

COMMENT ON COLUMN northwind.disputes.updated_by IS 'User name of the last editor.';

COMMENT ON COLUMN northwind.campaign_responses.id IS 'Surrogate key of the campaign response.';

COMMENT ON COLUMN northwind.campaign_responses.responded_at IS 'When the response happened.';

COMMENT ON COLUMN northwind.campaign_responses.response_type IS 'Kind of response, such as clicked or visited-booth.';

COMMENT ON COLUMN northwind.campaign_responses.notes IS 'Details.';

COMMENT ON COLUMN northwind.request_for_quotes.id IS 'Surrogate key of the request for quote.';

COMMENT ON COLUMN northwind.request_for_quotes.rfq_number IS 'RFQ number.';

COMMENT ON COLUMN northwind.request_for_quotes.issued_on IS 'Date sent.';

COMMENT ON COLUMN northwind.request_for_quotes.response_due_on IS 'Deadline for quotes.';

COMMENT ON COLUMN northwind.request_for_quotes.status IS 'open, evaluating, awarded or cancelled.';

COMMENT ON COLUMN northwind.request_for_quotes.description IS 'Requirements.';

COMMENT ON COLUMN northwind.request_for_quotes.created_at IS 'When the row was created.';

COMMENT ON COLUMN northwind.request_for_quotes.created_by IS 'User name of the creator.';

COMMENT ON COLUMN northwind.request_for_quotes.updated_at IS 'When the row was last changed.';

COMMENT ON COLUMN northwind.request_for_quotes.updated_by IS 'User name of the last editor.';

COMMENT ON COLUMN northwind.goods_receipts.id IS 'Surrogate key of the goods receipt.';

COMMENT ON COLUMN northwind.goods_receipts.receipt_number IS 'Receipt number.';

COMMENT ON COLUMN northwind.goods_receipts.received_at IS 'When the goods were received.';

COMMENT ON COLUMN northwind.goods_receipts.delivery_note IS 'Supplier''s delivery note number.';

COMMENT ON COLUMN northwind.goods_receipts.notes IS 'Damage or discrepancies noted.';

COMMENT ON COLUMN northwind.goods_receipts.created_at IS 'When the row was created.';

COMMENT ON COLUMN northwind.goods_receipts.created_by IS 'User name of the creator.';

COMMENT ON COLUMN northwind.goods_receipts.updated_at IS 'When the row was last changed.';

COMMENT ON COLUMN northwind.goods_receipts.updated_by IS 'User name of the last editor.';

COMMENT ON COLUMN northwind.account_assignments.id IS 'Surrogate key of the account assignment.';

COMMENT ON COLUMN northwind.account_assignments.assignment_role IS 'Role on the account, such as key-account.';

COMMENT ON COLUMN northwind.account_assignments.validity_starts_on IS 'First day of the range.';

COMMENT ON COLUMN northwind.account_assignments.validity_ends_on IS 'Last day of the range; null when open-ended.';

COMMENT ON COLUMN northwind.account_assignments.is_primary IS 'Primary owner of the account.';

COMMENT ON COLUMN northwind.account_assignments.created_at IS 'When the row was created.';

COMMENT ON COLUMN northwind.account_assignments.created_by IS 'User name of the creator.';

COMMENT ON COLUMN northwind.account_assignments.updated_at IS 'When the row was last changed.';

COMMENT ON COLUMN northwind.account_assignments.updated_by IS 'User name of the last editor.';

COMMENT ON COLUMN northwind.storage_conditions.id IS 'Surrogate key of the storage condition.';

COMMENT ON COLUMN northwind.storage_conditions.min_temperature_c IS 'Lowest allowed temperature.';

COMMENT ON COLUMN northwind.storage_conditions.max_temperature_c IS 'Highest allowed temperature.';

COMMENT ON COLUMN northwind.storage_conditions.requires_humidity_control IS 'Whether humidity is controlled.';

COMMENT ON COLUMN northwind.storage_conditions.code IS 'Short stable code used in integrations and imports.';

COMMENT ON COLUMN northwind.storage_conditions.name IS 'Display name shown in pick lists.';

COMMENT ON COLUMN northwind.storage_conditions.sort_order IS 'Position in pick lists.';

COMMENT ON COLUMN northwind.storage_conditions.is_active IS 'Whether the value can be chosen for new records.';

COMMENT ON COLUMN northwind.api_keys.id IS 'Surrogate key of the api key.';

COMMENT ON COLUMN northwind.api_keys.prefix IS 'Public prefix shown in the console to identify the key.';

COMMENT ON COLUMN northwind.api_keys.key_hash IS 'SHA-256 hash of the secret.';

COMMENT ON COLUMN northwind.api_keys.expires_on IS 'Expiry date; null for no expiry.';

COMMENT ON COLUMN northwind.api_keys.last_used_at IS 'Most recent use.';

COMMENT ON COLUMN northwind.api_keys.revoked_at IS 'When the key was revoked.';

COMMENT ON COLUMN northwind.api_keys.created_at IS 'When the row was created.';

COMMENT ON COLUMN northwind.api_keys.created_by IS 'User name of the creator.';

COMMENT ON COLUMN northwind.api_keys.updated_at IS 'When the row was last changed.';

COMMENT ON COLUMN northwind.api_keys.updated_by IS 'User name of the last editor.';

COMMENT ON COLUMN northwind.sales_order_status_changes.id IS 'Surrogate key of the sales order status change.';

COMMENT ON COLUMN northwind.sales_order_status_changes.from_status IS 'Status before.';

COMMENT ON COLUMN northwind.sales_order_status_changes.to_status IS 'Status after.';

COMMENT ON COLUMN northwind.sales_order_status_changes.changed_at IS 'When the change happened.';

COMMENT ON COLUMN northwind.sales_order_status_changes.reason IS 'Why it changed.';

COMMENT ON COLUMN northwind.budget_lines.id IS 'Surrogate key of the budget line.';

COMMENT ON COLUMN northwind.budget_lines.amount_amount IS 'The amount in the currency''s major unit.';

COMMENT ON COLUMN northwind.budget_lines.amount_currency IS 'The ISO 4217 currency of the amount.';

COMMENT ON COLUMN northwind.sales_quote_lines.id IS 'Surrogate key of the sales quote line.';

COMMENT ON COLUMN northwind.sales_quote_lines.quantity IS 'Quantity quoted.';

COMMENT ON COLUMN northwind.sales_quote_lines.unit_price_amount IS 'The amount in the currency''s major unit.';

COMMENT ON COLUMN northwind.sales_quote_lines.unit_price_currency IS 'The ISO 4217 currency of the amount.';

COMMENT ON COLUMN northwind.sales_quote_lines.discount IS 'Quoted discount.';

COMMENT ON COLUMN northwind.sales_quote_lines.line_total_amount IS 'The amount in the currency''s major unit.';

COMMENT ON COLUMN northwind.sales_quote_lines.line_total_currency IS 'The ISO 4217 currency of the amount.';

COMMENT ON COLUMN northwind.dunning_notices.id IS 'Surrogate key of the dunning notice.';

COMMENT ON COLUMN northwind.dunning_notices.level IS 'Escalation level.';

COMMENT ON COLUMN northwind.dunning_notices.issued_on IS 'Date sent.';

COMMENT ON COLUMN northwind.dunning_notices.amount_overdue_amount IS 'The amount in the currency''s major unit.';

COMMENT ON COLUMN northwind.dunning_notices.amount_overdue_currency IS 'The ISO 4217 currency of the amount.';

COMMENT ON COLUMN northwind.dunning_notices.fee_amount_amount IS 'The amount in the currency''s major unit.';

COMMENT ON COLUMN northwind.dunning_notices.fee_amount_currency IS 'The ISO 4217 currency of the amount.';

COMMENT ON COLUMN northwind.dunning_notices.sent_via IS 'email or mail.';

COMMENT ON COLUMN northwind.invoices.id IS 'Surrogate key of the invoice.';

COMMENT ON COLUMN northwind.invoices.invoice_number IS 'Invoice number.';

COMMENT ON COLUMN northwind.invoices.invoice_date IS 'Invoice date.';

COMMENT ON COLUMN northwind.invoices.due_date IS 'Payment due date.';

COMMENT ON COLUMN northwind.invoices.status IS 'Lifecycle state.';

COMMENT ON COLUMN northwind.invoices.billing_address_line1 IS 'Street and number, or PO box.';

COMMENT ON COLUMN northwind.invoices.billing_address_line2 IS 'Suite, floor or building.';

COMMENT ON COLUMN northwind.invoices.billing_address_city IS 'City or locality.';

COMMENT ON COLUMN northwind.invoices.billing_address_region IS 'State, province or county.';

COMMENT ON COLUMN northwind.invoices.billing_address_postal_code IS 'Postal or ZIP code.';

COMMENT ON COLUMN northwind.invoices.billing_address_country_code IS 'ISO country code.';

COMMENT ON COLUMN northwind.invoices.subtotal_amount IS 'The amount in the currency''s major unit.';

COMMENT ON COLUMN northwind.invoices.subtotal_currency IS 'The ISO 4217 currency of the amount.';

COMMENT ON COLUMN northwind.invoices.tax_total_amount IS 'The amount in the currency''s major unit.';

COMMENT ON COLUMN northwind.invoices.tax_total_currency IS 'The ISO 4217 currency of the amount.';

COMMENT ON COLUMN northwind.invoices.grand_total_amount IS 'The amount in the currency''s major unit.';

COMMENT ON COLUMN northwind.invoices.grand_total_currency IS 'The ISO 4217 currency of the amount.';

COMMENT ON COLUMN northwind.invoices.amount_paid_amount IS 'The amount in the currency''s major unit.';

COMMENT ON COLUMN northwind.invoices.amount_paid_currency IS 'The ISO 4217 currency of the amount.';

COMMENT ON COLUMN northwind.invoices.balance_due_amount IS 'The amount in the currency''s major unit.';

COMMENT ON COLUMN northwind.invoices.balance_due_currency IS 'The ISO 4217 currency of the amount.';

COMMENT ON COLUMN northwind.invoices.sent_at IS 'When it was delivered to the customer.';

COMMENT ON COLUMN northwind.invoices.notes IS 'Remarks printed on the invoice.';

COMMENT ON COLUMN northwind.invoices.created_at IS 'When the row was created.';

COMMENT ON COLUMN northwind.invoices.created_by IS 'User name of the creator.';

COMMENT ON COLUMN northwind.invoices.updated_at IS 'When the row was last changed.';

COMMENT ON COLUMN northwind.invoices.updated_by IS 'User name of the last editor.';

COMMENT ON COLUMN northwind.shipment_lines.id IS 'Surrogate key of the shipment line.';

COMMENT ON COLUMN northwind.shipment_lines.quantity IS 'Quantity shipped.';

COMMENT ON COLUMN northwind.party_addresses.id IS 'Surrogate key of the party address.';

COMMENT ON COLUMN northwind.party_addresses.label IS 'Name of the location, such as Denver branch.';

COMMENT ON COLUMN northwind.party_addresses.address_type IS 'Purpose of the address.';

COMMENT ON COLUMN northwind.party_addresses.address_line1 IS 'Street and number, or PO box.';

COMMENT ON COLUMN northwind.party_addresses.address_line2 IS 'Suite, floor or building.';

COMMENT ON COLUMN northwind.party_addresses.address_city IS 'City or locality.';

COMMENT ON COLUMN northwind.party_addresses.address_region IS 'State, province or county.';

COMMENT ON COLUMN northwind.party_addresses.address_postal_code IS 'Postal or ZIP code.';

COMMENT ON COLUMN northwind.party_addresses.address_country_code IS 'ISO country code.';

COMMENT ON COLUMN northwind.party_addresses.location_latitude IS 'Degrees north of the equator.';

COMMENT ON COLUMN northwind.party_addresses.location_longitude IS 'Degrees east of Greenwich.';

COMMENT ON COLUMN northwind.party_addresses.is_default IS 'Default address of its type.';

COMMENT ON COLUMN northwind.party_addresses.delivery_instructions IS 'Dock hours, gate codes and the like.';

COMMENT ON COLUMN northwind.party_addresses.created_at IS 'When the row was created.';

COMMENT ON COLUMN northwind.party_addresses.created_by IS 'User name of the creator.';

COMMENT ON COLUMN northwind.party_addresses.updated_at IS 'When the row was last changed.';

COMMENT ON COLUMN northwind.party_addresses.updated_by IS 'User name of the last editor.';

COMMENT ON COLUMN northwind.shipping_zones.id IS 'Surrogate key of the shipping zone.';

COMMENT ON COLUMN northwind.shipping_zones.code IS 'Zone code.';

COMMENT ON COLUMN northwind.shipping_zones.name IS 'Zone name.';

COMMENT ON COLUMN northwind.shipping_zones.description IS 'Postal code ranges in the zone.';

COMMENT ON COLUMN northwind.backorders.id IS 'Surrogate key of the backorder.';

COMMENT ON COLUMN northwind.backorders.quantity IS 'Quantity waiting.';

COMMENT ON COLUMN northwind.backorders.expected_on IS 'Expected availability date.';

COMMENT ON COLUMN northwind.backorders.customer_notified_at IS 'When the customer was told.';

COMMENT ON COLUMN northwind.backorders.is_fulfilled IS 'Whether it has been allocated since.';

COMMENT ON COLUMN northwind.backorders.created_at IS 'When the row was created.';

COMMENT ON COLUMN northwind.backorders.created_by IS 'User name of the creator.';

COMMENT ON COLUMN northwind.backorders.updated_at IS 'When the row was last changed.';

COMMENT ON COLUMN northwind.backorders.updated_by IS 'User name of the last editor.';

COMMENT ON COLUMN northwind.payment_invoice.allocated_amount_amount IS 'The amount in the currency''s major unit.';

COMMENT ON COLUMN northwind.payment_invoice.allocated_amount_currency IS 'The ISO 4217 currency of the amount.';

COMMENT ON COLUMN northwind.payment_invoice.allocated_at IS 'When the allocation was made.';

COMMENT ON COLUMN northwind.payment_invoice.discount_taken_amount IS 'The amount in the currency''s major unit.';

COMMENT ON COLUMN northwind.payment_invoice.discount_taken_currency IS 'The ISO 4217 currency of the amount.';

COMMENT ON COLUMN northwind.user_accounts.id IS 'Surrogate key of the user account.';

COMMENT ON COLUMN northwind.user_accounts.user_name IS 'Sign-in name, lower case.';

COMMENT ON COLUMN northwind.user_accounts.email IS 'Address for sign-in links and notifications.';

COMMENT ON COLUMN northwind.user_accounts.display_name IS 'Name shown in the user interface.';

COMMENT ON COLUMN northwind.user_accounts.status IS 'Lifecycle state of the account.';

COMMENT ON COLUMN northwind.user_accounts.password_hash IS 'Argon2id hash of the password; null for single sign-on users.';

COMMENT ON COLUMN northwind.user_accounts.mfa_enabled IS 'Whether a second factor is required.';

COMMENT ON COLUMN northwind.user_accounts.last_sign_in_at IS 'Most recent successful sign-in.';

COMMENT ON COLUMN northwind.user_accounts.failed_sign_in_count IS 'Consecutive failed attempts since the last success.';

COMMENT ON COLUMN northwind.user_accounts.preferred_language IS 'BCP 47 tag of the interface language.';

COMMENT ON COLUMN northwind.user_accounts.created_at IS 'When the row was created.';

COMMENT ON COLUMN northwind.user_accounts.created_by IS 'User name of the creator.';

COMMENT ON COLUMN northwind.user_accounts.updated_at IS 'When the row was last changed.';

COMMENT ON COLUMN northwind.user_accounts.updated_by IS 'User name of the last editor.';

COMMENT ON COLUMN northwind.user_accounts.deleted_at IS 'When the row was marked deleted; null while it is live.';

COMMENT ON COLUMN northwind.cost_centers.id IS 'Surrogate key of the cost center.';

COMMENT ON COLUMN northwind.cost_centers.code IS 'Cost center code.';

COMMENT ON COLUMN northwind.cost_centers.name IS 'Cost center name.';

COMMENT ON COLUMN northwind.cost_centers.is_active IS 'Whether it can be charged.';

COMMENT ON COLUMN northwind.cost_centers.created_at IS 'When the row was created.';

COMMENT ON COLUMN northwind.cost_centers.created_by IS 'User name of the creator.';

COMMENT ON COLUMN northwind.cost_centers.updated_at IS 'When the row was last changed.';

COMMENT ON COLUMN northwind.cost_centers.updated_by IS 'User name of the last editor.';

COMMENT ON COLUMN northwind.requisition_lines.id IS 'Surrogate key of the requisition line.';

COMMENT ON COLUMN northwind.requisition_lines.description IS 'What is needed.';

COMMENT ON COLUMN northwind.requisition_lines.quantity IS 'Quantity.';

COMMENT ON COLUMN northwind.requisition_lines.estimated_cost_amount IS 'The amount in the currency''s major unit.';

COMMENT ON COLUMN northwind.requisition_lines.estimated_cost_currency IS 'The ISO 4217 currency of the amount.';

COMMENT ON COLUMN northwind.inventory_items.id IS 'Surrogate key of the inventory item.';

COMMENT ON COLUMN northwind.inventory_items.quantity_on_hand IS 'Physical quantity in the warehouse.';

COMMENT ON COLUMN northwind.inventory_items.quantity_allocated IS 'Quantity reserved for open orders.';

COMMENT ON COLUMN northwind.inventory_items.quantity_on_order IS 'Quantity on open purchase orders.';

COMMENT ON COLUMN northwind.inventory_items.reorder_point IS 'Quantity that triggers replenishment.';

COMMENT ON COLUMN northwind.inventory_items.last_counted_on IS 'Date of the last cycle count.';

COMMENT ON COLUMN northwind.inventory_items.created_at IS 'When the row was created.';

COMMENT ON COLUMN northwind.inventory_items.created_by IS 'User name of the creator.';

COMMENT ON COLUMN northwind.inventory_items.updated_at IS 'When the row was last changed.';

COMMENT ON COLUMN northwind.inventory_items.updated_by IS 'User name of the last editor.';

COMMENT ON COLUMN northwind.product_attributes.id IS 'Surrogate key of the product attribute.';

COMMENT ON COLUMN northwind.product_attributes.code IS 'Attribute code used in feeds.';

COMMENT ON COLUMN northwind.product_attributes.name IS 'Display name.';

COMMENT ON COLUMN northwind.product_attributes.data_type IS 'Value type - text, number, boolean or option.';

COMMENT ON COLUMN northwind.product_attributes.unit IS 'Unit of numeric values, such as mm.';

COMMENT ON COLUMN northwind.product_attributes.is_filterable IS 'Whether the storefront offers it as a filter.';

COMMENT ON COLUMN northwind.journal_lines.id IS 'Surrogate key of the journal line.';

COMMENT ON COLUMN northwind.journal_lines.debit_amount IS 'The amount in the currency''s major unit.';

COMMENT ON COLUMN northwind.journal_lines.debit_currency IS 'The ISO 4217 currency of the amount.';

COMMENT ON COLUMN northwind.journal_lines.credit_amount IS 'The amount in the currency''s major unit.';

COMMENT ON COLUMN northwind.journal_lines.credit_currency IS 'The ISO 4217 currency of the amount.';

COMMENT ON COLUMN northwind.journal_lines.memo IS 'Line memo.';

COMMENT ON COLUMN northwind.promotions.id IS 'Surrogate key of the promotion.';

COMMENT ON COLUMN northwind.promotions.code IS 'Promotion code.';

COMMENT ON COLUMN northwind.promotions.name IS 'Promotion name.';

COMMENT ON COLUMN northwind.promotions.description IS 'Terms shown to customers.';

COMMENT ON COLUMN northwind.promotions.status IS 'Lifecycle state.';

COMMENT ON COLUMN northwind.promotions.validity_starts_on IS 'First day of the range.';

COMMENT ON COLUMN northwind.promotions.validity_ends_on IS 'Last day of the range; null when open-ended.';

COMMENT ON COLUMN northwind.promotions.budget_amount IS 'The amount in the currency''s major unit.';

COMMENT ON COLUMN northwind.promotions.budget_currency IS 'The ISO 4217 currency of the amount.';

COMMENT ON COLUMN northwind.promotions.max_redemptions IS 'Cap on redemptions across all customers.';

COMMENT ON COLUMN northwind.promotions.created_at IS 'When the row was created.';

COMMENT ON COLUMN northwind.promotions.created_by IS 'User name of the creator.';

COMMENT ON COLUMN northwind.promotions.updated_at IS 'When the row was last changed.';

COMMENT ON COLUMN northwind.promotions.updated_by IS 'User name of the last editor.';

COMMENT ON COLUMN northwind.payment_batches.id IS 'Surrogate key of the payment batch.';

COMMENT ON COLUMN northwind.payment_batches.batch_number IS 'Batch number.';

COMMENT ON COLUMN northwind.payment_batches.batch_date IS 'Deposit date.';

COMMENT ON COLUMN northwind.payment_batches.source IS 'lockbox, ach-file or card-settlement.';

COMMENT ON COLUMN northwind.payment_batches.item_count IS 'Payments in the batch.';

COMMENT ON COLUMN northwind.payment_batches.total_amount_amount IS 'The amount in the currency''s major unit.';

COMMENT ON COLUMN northwind.payment_batches.total_amount_currency IS 'The ISO 4217 currency of the amount.';

COMMENT ON COLUMN northwind.payment_batches.is_posted IS 'Whether it was posted to the ledger.';

COMMENT ON COLUMN northwind.payment_batches.created_at IS 'When the row was created.';

COMMENT ON COLUMN northwind.payment_batches.created_by IS 'User name of the creator.';

COMMENT ON COLUMN northwind.payment_batches.updated_at IS 'When the row was last changed.';

COMMENT ON COLUMN northwind.payment_batches.updated_by IS 'User name of the last editor.';

COMMENT ON COLUMN northwind.cart_items.id IS 'Surrogate key of the cart item.';

COMMENT ON COLUMN northwind.cart_items.quantity IS 'Quantity wanted.';

COMMENT ON COLUMN northwind.cart_items.added_at IS 'When it was added.';

COMMENT ON COLUMN northwind.cart_items.unit_price_snapshot_amount IS 'The amount in the currency''s major unit.';

COMMENT ON COLUMN northwind.cart_items.unit_price_snapshot_currency IS 'The ISO 4217 currency of the amount.';

COMMENT ON COLUMN northwind.parties_remit_to_addresses.line1 IS 'Street and number, or PO box.';

COMMENT ON COLUMN northwind.parties_remit_to_addresses.line2 IS 'Suite, floor or building.';

COMMENT ON COLUMN northwind.parties_remit_to_addresses.city IS 'City or locality.';

COMMENT ON COLUMN northwind.parties_remit_to_addresses.region IS 'State, province or county.';

COMMENT ON COLUMN northwind.parties_remit_to_addresses.postal_code IS 'Postal or ZIP code.';

COMMENT ON COLUMN northwind.parties_remit_to_addresses.country_code IS 'ISO country code.';

COMMENT ON COLUMN northwind.parties.id IS 'Surrogate key of the party.';

COMMENT ON COLUMN northwind.parties.party_number IS 'Business number shown on documents, such as C-104233.';

COMMENT ON COLUMN northwind.parties.name IS 'Legal name.';

COMMENT ON COLUMN northwind.parties.trading_name IS 'Name the party trades under, when different.';

COMMENT ON COLUMN northwind.parties.tax_id IS 'VAT or EIN registration number.';

COMMENT ON COLUMN northwind.parties.website IS 'Public website URL.';

COMMENT ON COLUMN northwind.parties.email IS 'General mailbox.';

COMMENT ON COLUMN northwind.parties.phone_number IS 'Number in E.164 form, such as +14155550100.';

COMMENT ON COLUMN northwind.parties.phone_extension IS 'Internal extension.';

COMMENT ON COLUMN northwind.parties.hq_line1 IS 'Street and number, or PO box.';

COMMENT ON COLUMN northwind.parties.hq_line2 IS 'Suite, floor or building.';

COMMENT ON COLUMN northwind.parties.hq_city IS 'City or locality.';

COMMENT ON COLUMN northwind.parties.hq_region IS 'State, province or county.';

COMMENT ON COLUMN northwind.parties.hq_postal_code IS 'Postal or ZIP code.';

COMMENT ON COLUMN northwind.parties.hq_country_code IS 'ISO country code.';

COMMENT ON COLUMN northwind.parties.is_active IS 'Whether new transactions may reference the party.';

COMMENT ON COLUMN northwind.parties.created_at IS 'When the row was created.';

COMMENT ON COLUMN northwind.parties.created_by IS 'User name of the creator.';

COMMENT ON COLUMN northwind.parties.updated_at IS 'When the row was last changed.';

COMMENT ON COLUMN northwind.parties.updated_by IS 'User name of the last editor.';

COMMENT ON COLUMN northwind.parties.deleted_at IS 'When the row was marked deleted; null while it is live.';

COMMENT ON COLUMN northwind.parties.scac_code IS 'Standard Carrier Alpha Code.';

COMMENT ON COLUMN northwind.parties.carrier_account_number IS 'Northwind''s shipper account number with the carrier.';

COMMENT ON COLUMN northwind.parties.tracking_url_template IS 'URL with a {tracking} placeholder.';

COMMENT ON COLUMN northwind.parties.offers_ltl IS 'Whether the carrier moves less-than-truckload freight.';

COMMENT ON COLUMN northwind.parties.insurance_expires_on IS 'Expiry of the carrier''s cargo insurance certificate.';

COMMENT ON COLUMN northwind.parties.account_number IS 'Customer account number used on orders and remittances.';

COMMENT ON COLUMN northwind.parties.customer_since IS 'Date the account was opened.';

COMMENT ON COLUMN northwind.parties.on_credit_hold IS 'Whether new orders are held for credit review.';

COMMENT ON COLUMN northwind.parties.is_tax_exempt IS 'Whether sales tax is waived; requires a valid exemption certificate.';

COMMENT ON COLUMN northwind.parties.annual_revenue_amount IS 'The amount in the currency''s major unit.';

COMMENT ON COLUMN northwind.parties.annual_revenue_currency IS 'The ISO 4217 currency of the amount.';

COMMENT ON COLUMN northwind.parties.lead_time_days IS 'Typical days from purchase order to receipt.';

COMMENT ON COLUMN northwind.parties.minimum_order_value_amount IS 'The amount in the currency''s major unit.';

COMMENT ON COLUMN northwind.parties.minimum_order_value_currency IS 'The ISO 4217 currency of the amount.';

COMMENT ON COLUMN northwind.parties.is_preferred IS 'Whether buyers should source from this supplier first.';

COMMENT ON COLUMN northwind.parties.onboarded_on IS 'Date the supplier passed vendor onboarding.';

COMMENT ON COLUMN northwind.price_list_entries.id IS 'Surrogate key of the price list entry.';

COMMENT ON COLUMN northwind.price_list_entries.unit_price_amount IS 'The amount in the currency''s major unit.';

COMMENT ON COLUMN northwind.price_list_entries.unit_price_currency IS 'The ISO 4217 currency of the amount.';

COMMENT ON COLUMN northwind.price_list_entries.minimum_quantity IS 'Smallest quantity the price applies to.';

COMMENT ON COLUMN northwind.price_list_entries.validity_starts_on IS 'First day of the range.';

COMMENT ON COLUMN northwind.price_list_entries.validity_ends_on IS 'Last day of the range; null when open-ended.';

COMMENT ON COLUMN northwind.price_list_entries.created_at IS 'When the row was created.';

COMMENT ON COLUMN northwind.price_list_entries.created_by IS 'User name of the creator.';

COMMENT ON COLUMN northwind.price_list_entries.updated_at IS 'When the row was last changed.';

COMMENT ON COLUMN northwind.price_list_entries.updated_by IS 'User name of the last editor.';

COMMENT ON COLUMN northwind.budgets.id IS 'Surrogate key of the budget.';

COMMENT ON COLUMN northwind.budgets.name IS 'Budget name.';

COMMENT ON COLUMN northwind.budgets.fiscal_year IS 'Fiscal year.';

COMMENT ON COLUMN northwind.budgets.is_approved IS 'Whether finance approved it.';

COMMENT ON COLUMN northwind.budgets.notes IS 'Assumptions.';

COMMENT ON COLUMN northwind.budgets.created_at IS 'When the row was created.';

COMMENT ON COLUMN northwind.budgets.created_by IS 'User name of the creator.';

COMMENT ON COLUMN northwind.budgets.updated_at IS 'When the row was last changed.';

COMMENT ON COLUMN northwind.budgets.updated_by IS 'User name of the last editor.';

COMMENT ON COLUMN northwind.return_inspections.id IS 'Surrogate key of the return inspection.';

COMMENT ON COLUMN northwind.return_inspections.inspected_at IS 'When it was inspected.';

COMMENT ON COLUMN northwind.return_inspections.outcome IS 'resaleable, refurbish or scrap.';

COMMENT ON COLUMN northwind.return_inspections.notes IS 'Findings.';

COMMENT ON COLUMN northwind.return_inspections.photo_url IS 'Photo of the goods.';

COMMENT ON COLUMN northwind.goods_receipt_lines.id IS 'Surrogate key of the goods receipt line.';

COMMENT ON COLUMN northwind.goods_receipt_lines.quantity_received IS 'Quantity accepted.';

COMMENT ON COLUMN northwind.goods_receipt_lines.quantity_rejected IS 'Quantity refused.';

COMMENT ON COLUMN northwind.goods_receipt_lines.rejection_reason IS 'Why goods were refused.';

COMMENT ON COLUMN northwind.product_bundles.id IS 'Surrogate key of the product bundle.';

COMMENT ON COLUMN northwind.product_bundles.sku IS 'SKU of the bundle.';

COMMENT ON COLUMN northwind.product_bundles.name IS 'Bundle name.';

COMMENT ON COLUMN northwind.product_bundles.price_amount IS 'The amount in the currency''s major unit.';

COMMENT ON COLUMN northwind.product_bundles.price_currency IS 'The ISO 4217 currency of the amount.';

COMMENT ON COLUMN northwind.product_bundles.is_active IS 'Whether the bundle can be ordered.';

COMMENT ON COLUMN northwind.product_bundles.created_at IS 'When the row was created.';

COMMENT ON COLUMN northwind.product_bundles.created_by IS 'User name of the creator.';

COMMENT ON COLUMN northwind.product_bundles.updated_at IS 'When the row was last changed.';

COMMENT ON COLUMN northwind.product_bundles.updated_by IS 'User name of the last editor.';

COMMENT ON COLUMN northwind.product_bundles.deleted_at IS 'When the row was marked deleted; null while it is live.';

COMMENT ON COLUMN northwind.stock_transfers.id IS 'Surrogate key of the stock transfer.';

COMMENT ON COLUMN northwind.stock_transfers.transfer_number IS 'Transfer number.';

COMMENT ON COLUMN northwind.stock_transfers.status IS 'Lifecycle state.';

COMMENT ON COLUMN northwind.stock_transfers.requested_on IS 'Date requested.';

COMMENT ON COLUMN northwind.stock_transfers.shipped_at IS 'When it left the source.';

COMMENT ON COLUMN northwind.stock_transfers.received_at IS 'When it arrived.';

COMMENT ON COLUMN northwind.stock_transfers.created_at IS 'When the row was created.';

COMMENT ON COLUMN northwind.stock_transfers.created_by IS 'User name of the creator.';

COMMENT ON COLUMN northwind.stock_transfers.updated_at IS 'When the row was last changed.';

COMMENT ON COLUMN northwind.stock_transfers.updated_by IS 'User name of the last editor.';

COMMENT ON COLUMN northwind.supplier_products.id IS 'Surrogate key of the supplier product.';

COMMENT ON COLUMN northwind.supplier_products.supplier_sku IS 'Supplier''s item number.';

COMMENT ON COLUMN northwind.supplier_products.supplier_description IS 'Supplier''s item description.';

COMMENT ON COLUMN northwind.supplier_products.unit_cost_amount IS 'The amount in the currency''s major unit.';

COMMENT ON COLUMN northwind.supplier_products.unit_cost_currency IS 'The ISO 4217 currency of the amount.';

COMMENT ON COLUMN northwind.supplier_products.minimum_order_quantity IS 'Minimum order quantity.';

COMMENT ON COLUMN northwind.supplier_products.pack_size IS 'Base units per purchase unit.';

COMMENT ON COLUMN northwind.supplier_products.lead_time_days IS 'Lead time for the item.';

COMMENT ON COLUMN northwind.supplier_products.is_preferred IS 'Preferred source for the product.';

COMMENT ON COLUMN northwind.supplier_products.created_at IS 'When the row was created.';

COMMENT ON COLUMN northwind.supplier_products.created_by IS 'User name of the creator.';

COMMENT ON COLUMN northwind.supplier_products.updated_at IS 'When the row was last changed.';

COMMENT ON COLUMN northwind.supplier_products.updated_by IS 'User name of the last editor.';

COMMENT ON COLUMN northwind.manufacturers.id IS 'Surrogate key of the manufacturer.';

COMMENT ON COLUMN northwind.manufacturers.name IS 'Manufacturer name.';

COMMENT ON COLUMN northwind.manufacturers.website IS 'Website.';

COMMENT ON COLUMN northwind.manufacturers.support_email IS 'Technical support mailbox.';

COMMENT ON COLUMN northwind.manufacturers.support_phone_number IS 'Number in E.164 form, such as +14155550100.';

COMMENT ON COLUMN northwind.manufacturers.support_phone_extension IS 'Internal extension.';

COMMENT ON COLUMN northwind.manufacturers.country_code IS 'Country of the head office.';

COMMENT ON COLUMN northwind.manufacturers.created_at IS 'When the row was created.';

COMMENT ON COLUMN northwind.manufacturers.created_by IS 'User name of the creator.';

COMMENT ON COLUMN northwind.manufacturers.updated_at IS 'When the row was last changed.';

COMMENT ON COLUMN northwind.manufacturers.updated_by IS 'User name of the last editor.';

COMMENT ON COLUMN northwind.product_lifecycle_events.id IS 'Surrogate key of the product lifecycle event.';

COMMENT ON COLUMN northwind.product_lifecycle_events.from_status IS 'Status before the change.';

COMMENT ON COLUMN northwind.product_lifecycle_events.to_status IS 'Status after the change.';

COMMENT ON COLUMN northwind.product_lifecycle_events.occurred_at IS 'When the change happened.';

COMMENT ON COLUMN northwind.product_lifecycle_events.reason IS 'Why the status changed.';

COMMENT ON COLUMN northwind.late_fees.id IS 'Surrogate key of the late fee.';

COMMENT ON COLUMN northwind.late_fees.assessed_on IS 'Date assessed.';

COMMENT ON COLUMN northwind.late_fees.amount_amount IS 'The amount in the currency''s major unit.';

COMMENT ON COLUMN northwind.late_fees.amount_currency IS 'The ISO 4217 currency of the amount.';

COMMENT ON COLUMN northwind.late_fees.rate IS 'Monthly rate applied.';

COMMENT ON COLUMN northwind.late_fees.is_waived IS 'Whether it was waived.';

COMMENT ON COLUMN northwind.late_fees.waived_reason IS 'Why it was waived.';

COMMENT ON COLUMN northwind.tax_exemption_certificates.id IS 'Surrogate key of the tax exemption certificate.';

COMMENT ON COLUMN northwind.tax_exemption_certificates.certificate_number IS 'Number on the certificate.';

COMMENT ON COLUMN northwind.tax_exemption_certificates.exemption_reason IS 'Reason, such as resale or nonprofit.';

COMMENT ON COLUMN northwind.tax_exemption_certificates.validity_starts_on IS 'First day of the range.';

COMMENT ON COLUMN northwind.tax_exemption_certificates.validity_ends_on IS 'Last day of the range; null when open-ended.';

COMMENT ON COLUMN northwind.tax_exemption_certificates.document_url IS 'Link to the scanned certificate.';

COMMENT ON COLUMN northwind.tax_exemption_certificates.created_at IS 'When the row was created.';

COMMENT ON COLUMN northwind.tax_exemption_certificates.created_by IS 'User name of the creator.';

COMMENT ON COLUMN northwind.tax_exemption_certificates.updated_at IS 'When the row was last changed.';

COMMENT ON COLUMN northwind.tax_exemption_certificates.updated_by IS 'User name of the last editor.';

COMMENT ON COLUMN northwind.freight_claims.id IS 'Surrogate key of the freight claim.';

COMMENT ON COLUMN northwind.freight_claims.claim_number IS 'Claim number.';

COMMENT ON COLUMN northwind.freight_claims.filed_on IS 'Filing date.';

COMMENT ON COLUMN northwind.freight_claims.claim_type IS 'loss, damage or delay.';

COMMENT ON COLUMN northwind.freight_claims.claimed_amount_amount IS 'The amount in the currency''s major unit.';

COMMENT ON COLUMN northwind.freight_claims.claimed_amount_currency IS 'The ISO 4217 currency of the amount.';

COMMENT ON COLUMN northwind.freight_claims.settled_amount_amount IS 'The amount in the currency''s major unit.';

COMMENT ON COLUMN northwind.freight_claims.settled_amount_currency IS 'The ISO 4217 currency of the amount.';

COMMENT ON COLUMN northwind.freight_claims.status IS 'filed, under-review, settled or denied.';

COMMENT ON COLUMN northwind.freight_claims.notes IS 'Claim narrative.';

COMMENT ON COLUMN northwind.freight_claims.created_at IS 'When the row was created.';

COMMENT ON COLUMN northwind.freight_claims.created_by IS 'User name of the creator.';

COMMENT ON COLUMN northwind.freight_claims.updated_at IS 'When the row was last changed.';

COMMENT ON COLUMN northwind.freight_claims.updated_by IS 'User name of the last editor.';

COMMENT ON COLUMN northwind.package_contents.id IS 'Surrogate key of the package content.';

COMMENT ON COLUMN northwind.package_contents.quantity IS 'Quantity packed.';

COMMENT ON COLUMN northwind.rebate_accruals.id IS 'Surrogate key of the rebate accrual.';

COMMENT ON COLUMN northwind.rebate_accruals.accrued_on IS 'Date of accrual.';

COMMENT ON COLUMN northwind.rebate_accruals.base_amount_amount IS 'The amount in the currency''s major unit.';

COMMENT ON COLUMN northwind.rebate_accruals.base_amount_currency IS 'The ISO 4217 currency of the amount.';

COMMENT ON COLUMN northwind.rebate_accruals.rebate_amount_amount IS 'The amount in the currency''s major unit.';

COMMENT ON COLUMN northwind.rebate_accruals.rebate_amount_currency IS 'The ISO 4217 currency of the amount.';

COMMENT ON COLUMN northwind.rebate_accruals.is_settled IS 'Whether it was paid out or credited.';

COMMENT ON COLUMN northwind.tax_jurisdictions.id IS 'Surrogate key of the tax jurisdiction.';

COMMENT ON COLUMN northwind.tax_jurisdictions.code IS 'Jurisdiction code.';

COMMENT ON COLUMN northwind.tax_jurisdictions.name IS 'Name.';

COMMENT ON COLUMN northwind.tax_jurisdictions.level IS 'country, state, county, city or district.';

COMMENT ON COLUMN northwind.tax_jurisdictions.created_at IS 'When the row was created.';

COMMENT ON COLUMN northwind.tax_jurisdictions.created_by IS 'User name of the creator.';

COMMENT ON COLUMN northwind.tax_jurisdictions.updated_at IS 'When the row was last changed.';

COMMENT ON COLUMN northwind.tax_jurisdictions.updated_by IS 'User name of the last editor.';

COMMENT ON COLUMN northwind.sales_order_lines.id IS 'Surrogate key of the sales order line.';

COMMENT ON COLUMN northwind.sales_order_lines.line_number IS 'Line number printed on documents.';

COMMENT ON COLUMN northwind.sales_order_lines.description IS 'Line text, defaulting to the product name.';

COMMENT ON COLUMN northwind.sales_order_lines.quantity IS 'Quantity ordered, in the line''s unit.';

COMMENT ON COLUMN northwind.sales_order_lines.unit_price_amount IS 'The amount in the currency''s major unit.';

COMMENT ON COLUMN northwind.sales_order_lines.unit_price_currency IS 'The ISO 4217 currency of the amount.';

COMMENT ON COLUMN northwind.sales_order_lines.discount IS 'Line discount.';

COMMENT ON COLUMN northwind.sales_order_lines.tax_rate IS 'Sales tax rate applied.';

COMMENT ON COLUMN northwind.sales_order_lines.line_total_amount IS 'The amount in the currency''s major unit.';

COMMENT ON COLUMN northwind.sales_order_lines.line_total_currency IS 'The ISO 4217 currency of the amount.';

COMMENT ON COLUMN northwind.sales_order_lines.status IS 'Fulfillment state.';

COMMENT ON COLUMN northwind.sales_order_lines.requested_on IS 'Requested delivery date, when it differs from the order''s.';

COMMENT ON COLUMN northwind.sales_order_lines.created_at IS 'When the row was created.';

COMMENT ON COLUMN northwind.sales_order_lines.created_by IS 'User name of the creator.';

COMMENT ON COLUMN northwind.sales_order_lines.updated_at IS 'When the row was last changed.';

COMMENT ON COLUMN northwind.sales_order_lines.updated_by IS 'User name of the last editor.';

COMMENT ON COLUMN northwind.serial_numbers.id IS 'Surrogate key of the serial number.';

COMMENT ON COLUMN northwind.serial_numbers.serial IS 'Serial number.';

COMMENT ON COLUMN northwind.serial_numbers.status IS 'in-stock, shipped, returned or scrapped.';

COMMENT ON COLUMN northwind.serial_numbers.received_at IS 'When it was received.';

COMMENT ON COLUMN northwind.shipments.id IS 'Surrogate key of the shipment.';

COMMENT ON COLUMN northwind.shipments.shipment_number IS 'Shipment number printed on the packing slip.';

COMMENT ON COLUMN northwind.shipments.status IS 'Lifecycle state.';

COMMENT ON COLUMN northwind.shipments.ship_to_line1 IS 'Street and number, or PO box.';

COMMENT ON COLUMN northwind.shipments.ship_to_line2 IS 'Suite, floor or building.';

COMMENT ON COLUMN northwind.shipments.ship_to_city IS 'City or locality.';

COMMENT ON COLUMN northwind.shipments.ship_to_region IS 'State, province or county.';

COMMENT ON COLUMN northwind.shipments.ship_to_postal_code IS 'Postal or ZIP code.';

COMMENT ON COLUMN northwind.shipments.ship_to_country_code IS 'ISO country code.';

COMMENT ON COLUMN northwind.shipments.freight_terms IS 'Incoterm of the shipment.';

COMMENT ON COLUMN northwind.shipments.tracking_number IS 'Master tracking or PRO number.';

COMMENT ON COLUMN northwind.shipments.estimated_delivery_on IS 'Carrier''s estimate.';

COMMENT ON COLUMN northwind.shipments.shipped_at IS 'When it left the dock.';

COMMENT ON COLUMN northwind.shipments.delivered_at IS 'When it was delivered.';

COMMENT ON COLUMN northwind.shipments.freight_cost_amount IS 'The amount in the currency''s major unit.';

COMMENT ON COLUMN northwind.shipments.freight_cost_currency IS 'The ISO 4217 currency of the amount.';

COMMENT ON COLUMN northwind.shipments.declared_value_amount IS 'The amount in the currency''s major unit.';

COMMENT ON COLUMN northwind.shipments.declared_value_currency IS 'The ISO 4217 currency of the amount.';

COMMENT ON COLUMN northwind.shipments.total_weight_value IS 'The weight.';

COMMENT ON COLUMN northwind.shipments.total_weight_unit IS 'Unit of the weight.';

COMMENT ON COLUMN northwind.shipments.created_at IS 'When the row was created.';

COMMENT ON COLUMN northwind.shipments.created_by IS 'User name of the creator.';

COMMENT ON COLUMN northwind.shipments.updated_at IS 'When the row was last changed.';

COMMENT ON COLUMN northwind.shipments.updated_by IS 'User name of the last editor.';

COMMENT ON COLUMN northwind.exchange_rates.id IS 'Surrogate key of the exchange rate.';

COMMENT ON COLUMN northwind.exchange_rates.rate IS 'Units of the target currency per unit of the source currency.';

COMMENT ON COLUMN northwind.exchange_rates.effective_on IS 'Date the rate applies.';

COMMENT ON COLUMN northwind.exchange_rates.rate_type IS 'spot, average or budget.';

COMMENT ON COLUMN northwind.exchange_rates.source IS 'Feed the rate came from.';

COMMENT ON COLUMN northwind.blanket_orders.id IS 'Surrogate key of the blanket order.';

COMMENT ON COLUMN northwind.blanket_orders.blanket_number IS 'Agreement number.';

COMMENT ON COLUMN northwind.blanket_orders.validity_starts_on IS 'First day of the range.';

COMMENT ON COLUMN northwind.blanket_orders.validity_ends_on IS 'Last day of the range; null when open-ended.';

COMMENT ON COLUMN northwind.blanket_orders.committed_amount_amount IS 'The amount in the currency''s major unit.';

COMMENT ON COLUMN northwind.blanket_orders.committed_amount_currency IS 'The ISO 4217 currency of the amount.';

COMMENT ON COLUMN northwind.blanket_orders.released_amount_amount IS 'The amount in the currency''s major unit.';

COMMENT ON COLUMN northwind.blanket_orders.released_amount_currency IS 'The ISO 4217 currency of the amount.';

COMMENT ON COLUMN northwind.blanket_orders.is_closed IS 'Whether the commitment is closed.';

COMMENT ON COLUMN northwind.blanket_orders.created_at IS 'When the row was created.';

COMMENT ON COLUMN northwind.blanket_orders.created_by IS 'User name of the creator.';

COMMENT ON COLUMN northwind.blanket_orders.updated_at IS 'When the row was last changed.';

COMMENT ON COLUMN northwind.blanket_orders.updated_by IS 'User name of the last editor.';

COMMENT ON COLUMN northwind.product_documents.id IS 'Surrogate key of the product document.';

COMMENT ON COLUMN northwind.product_documents.title IS 'Document title.';

COMMENT ON COLUMN northwind.product_documents.document_type IS 'Kind, such as sds, datasheet or manual.';

COMMENT ON COLUMN northwind.product_documents.url IS 'Document URL.';

COMMENT ON COLUMN northwind.product_documents.language_code IS 'Language of the document.';

COMMENT ON COLUMN northwind.product_documents.revision IS 'Revision label.';

COMMENT ON COLUMN northwind.product_documents.published_on IS 'Date the revision was published.';

COMMENT ON COLUMN northwind.product_documents.created_at IS 'When the row was created.';

COMMENT ON COLUMN northwind.product_documents.created_by IS 'User name of the creator.';

COMMENT ON COLUMN northwind.product_documents.updated_at IS 'When the row was last changed.';

COMMENT ON COLUMN northwind.product_documents.updated_by IS 'User name of the last editor.';

COMMENT ON COLUMN northwind.hold_reasons.id IS 'Surrogate key of the hold reason.';

COMMENT ON COLUMN northwind.hold_reasons.requires_credit_approval IS 'Whether only the credit department can release it.';

COMMENT ON COLUMN northwind.hold_reasons.code IS 'Short stable code used in integrations and imports.';

COMMENT ON COLUMN northwind.hold_reasons.name IS 'Display name shown in pick lists.';

COMMENT ON COLUMN northwind.hold_reasons.sort_order IS 'Position in pick lists.';

COMMENT ON COLUMN northwind.hold_reasons.is_active IS 'Whether the value can be chosen for new records.';

COMMENT ON COLUMN northwind.bank_transfers.id IS 'Surrogate key of the payment.';

COMMENT ON COLUMN northwind.bank_transfers.transfer_reference IS 'Reference on the bank statement.';

COMMENT ON COLUMN northwind.bank_transfers.payer_bank_name IS 'Payer''s bank.';

COMMENT ON COLUMN northwind.bank_transfers.payer_account_last4 IS 'Last digits of the payer''s account.';

COMMENT ON COLUMN northwind.bank_transfers.value_date IS 'Date the funds became available.';

COMMENT ON COLUMN northwind.fiscal_periods.id IS 'Surrogate key of the fiscal period.';

COMMENT ON COLUMN northwind.fiscal_periods.name IS 'Period name, such as FY2026-09.';

COMMENT ON COLUMN northwind.fiscal_periods.fiscal_year IS 'Fiscal year.';

COMMENT ON COLUMN northwind.fiscal_periods.period_number IS 'Period within the year; 13 is the adjustment period.';

COMMENT ON COLUMN northwind.fiscal_periods.period_starts_on IS 'First day of the range.';

COMMENT ON COLUMN northwind.fiscal_periods.period_ends_on IS 'Last day of the range; null when open-ended.';

COMMENT ON COLUMN northwind.fiscal_periods.is_closed IS 'Whether posting is closed.';

COMMENT ON COLUMN northwind.fiscal_periods.closed_at IS 'When it was closed.';

COMMENT ON COLUMN northwind.fiscal_periods.created_at IS 'When the row was created.';

COMMENT ON COLUMN northwind.fiscal_periods.created_by IS 'User name of the creator.';

COMMENT ON COLUMN northwind.fiscal_periods.updated_at IS 'When the row was last changed.';

COMMENT ON COLUMN northwind.fiscal_periods.updated_by IS 'User name of the last editor.';

COMMENT ON COLUMN northwind.pick_waves.id IS 'Surrogate key of the pick wave.';

COMMENT ON COLUMN northwind.pick_waves.wave_number IS 'Wave number.';

COMMENT ON COLUMN northwind.pick_waves.status IS 'planned, released or complete.';

COMMENT ON COLUMN northwind.pick_waves.planned_start_at IS 'Planned release time.';

COMMENT ON COLUMN northwind.pick_waves.released_at IS 'Actual release time.';

COMMENT ON COLUMN northwind.pick_waves.created_at IS 'When the row was created.';

COMMENT ON COLUMN northwind.pick_waves.created_by IS 'User name of the creator.';

COMMENT ON COLUMN northwind.pick_waves.updated_at IS 'When the row was last changed.';

COMMENT ON COLUMN northwind.pick_waves.updated_by IS 'User name of the last editor.';

COMMENT ON COLUMN northwind.cycle_counts.id IS 'Surrogate key of the cycle count.';

COMMENT ON COLUMN northwind.cycle_counts.count_number IS 'Count number.';

COMMENT ON COLUMN northwind.cycle_counts.scheduled_on IS 'Planned date.';

COMMENT ON COLUMN northwind.cycle_counts.status IS 'planned, counting, review or closed.';

COMMENT ON COLUMN northwind.cycle_counts.completed_at IS 'When counting finished.';

COMMENT ON COLUMN northwind.cycle_counts.created_at IS 'When the row was created.';

COMMENT ON COLUMN northwind.cycle_counts.created_by IS 'User name of the creator.';

COMMENT ON COLUMN northwind.cycle_counts.updated_at IS 'When the row was last changed.';

COMMENT ON COLUMN northwind.cycle_counts.updated_by IS 'User name of the last editor.';

COMMENT ON COLUMN northwind.service_cases.id IS 'Surrogate key of the service case.';

COMMENT ON COLUMN northwind.service_cases.case_number IS 'Case number.';

COMMENT ON COLUMN northwind.service_cases.subject IS 'One-line summary.';

COMMENT ON COLUMN northwind.service_cases.description IS 'Customer''s description.';

COMMENT ON COLUMN northwind.service_cases.priority IS 'Urgency.';

COMMENT ON COLUMN northwind.service_cases.status IS 'Lifecycle state.';

COMMENT ON COLUMN northwind.service_cases.opened_at IS 'When the case was opened.';

COMMENT ON COLUMN northwind.service_cases.sla_due_at IS 'When the first answer is due.';

COMMENT ON COLUMN northwind.service_cases.resolved_at IS 'When it was resolved.';

COMMENT ON COLUMN northwind.service_cases.created_at IS 'When the row was created.';

COMMENT ON COLUMN northwind.service_cases.created_by IS 'User name of the creator.';

COMMENT ON COLUMN northwind.service_cases.updated_at IS 'When the row was last changed.';

COMMENT ON COLUMN northwind.service_cases.updated_by IS 'User name of the last editor.';

COMMENT ON COLUMN northwind.stock_reservations.id IS 'Surrogate key of the stock reservation.';

COMMENT ON COLUMN northwind.stock_reservations.quantity IS 'Quantity reserved.';

COMMENT ON COLUMN northwind.stock_reservations.reserved_at IS 'When it was reserved.';

COMMENT ON COLUMN northwind.stock_reservations.expires_at IS 'When a soft reservation lapses.';

COMMENT ON COLUMN northwind.stock_reservations.status IS 'active, picked, released or expired.';

COMMENT ON COLUMN northwind.purchase_orders.id IS 'Surrogate key of the purchase order.';

COMMENT ON COLUMN northwind.purchase_orders.ordered_on IS 'Order date.';

COMMENT ON COLUMN northwind.purchase_orders.status IS 'Lifecycle state.';

COMMENT ON COLUMN northwind.purchase_orders.expected_on IS 'Expected delivery date.';

COMMENT ON COLUMN northwind.purchase_orders.freight_terms IS 'Incoterm agreed.';

COMMENT ON COLUMN northwind.purchase_orders.subtotal_amount IS 'The amount in the currency''s major unit.';

COMMENT ON COLUMN northwind.purchase_orders.subtotal_currency IS 'The ISO 4217 currency of the amount.';

COMMENT ON COLUMN northwind.purchase_orders.tax_total_amount IS 'The amount in the currency''s major unit.';

COMMENT ON COLUMN northwind.purchase_orders.tax_total_currency IS 'The ISO 4217 currency of the amount.';

COMMENT ON COLUMN northwind.purchase_orders.freight_total_amount IS 'The amount in the currency''s major unit.';

COMMENT ON COLUMN northwind.purchase_orders.freight_total_currency IS 'The ISO 4217 currency of the amount.';

COMMENT ON COLUMN northwind.purchase_orders.grand_total_amount IS 'The amount in the currency''s major unit.';

COMMENT ON COLUMN northwind.purchase_orders.grand_total_currency IS 'The ISO 4217 currency of the amount.';

COMMENT ON COLUMN northwind.purchase_orders.notes IS 'Instructions to the supplier.';

COMMENT ON COLUMN northwind.purchase_orders.created_at IS 'When the row was created.';

COMMENT ON COLUMN northwind.purchase_orders.created_by IS 'User name of the creator.';

COMMENT ON COLUMN northwind.purchase_orders.updated_at IS 'When the row was last changed.';

COMMENT ON COLUMN northwind.purchase_orders.updated_by IS 'User name of the last editor.';

COMMENT ON COLUMN northwind.purchase_orders.deleted_at IS 'When the row was marked deleted; null while it is live.';

COMMENT ON COLUMN northwind.credit_note_lines.id IS 'Surrogate key of the credit note line.';

COMMENT ON COLUMN northwind.credit_note_lines.description IS 'Line text.';

COMMENT ON COLUMN northwind.credit_note_lines.quantity IS 'Quantity credited.';

COMMENT ON COLUMN northwind.credit_note_lines.unit_price_amount IS 'The amount in the currency''s major unit.';

COMMENT ON COLUMN northwind.credit_note_lines.unit_price_currency IS 'The ISO 4217 currency of the amount.';

COMMENT ON COLUMN northwind.credit_note_lines.line_total_amount IS 'The amount in the currency''s major unit.';

COMMENT ON COLUMN northwind.credit_note_lines.line_total_currency IS 'The ISO 4217 currency of the amount.';

COMMENT ON COLUMN northwind.sales_order_notes.id IS 'Surrogate key of the sales order note.';

COMMENT ON COLUMN northwind.sales_order_notes.body IS 'Note text.';

COMMENT ON COLUMN northwind.sales_order_notes.is_customer_visible IS 'Shown on the customer portal.';

COMMENT ON COLUMN northwind.sales_order_notes.created_at IS 'When the row was created.';

COMMENT ON COLUMN northwind.sales_order_notes.created_by IS 'User name of the creator.';

COMMENT ON COLUMN northwind.sales_order_notes.updated_at IS 'When the row was last changed.';

COMMENT ON COLUMN northwind.sales_order_notes.updated_by IS 'User name of the last editor.';

COMMENT ON COLUMN northwind.collection_cases.id IS 'Surrogate key of the collection case.';

COMMENT ON COLUMN northwind.collection_cases.case_number IS 'Case number.';

COMMENT ON COLUMN northwind.collection_cases.opened_on IS 'Date opened.';

COMMENT ON COLUMN northwind.collection_cases.status IS 'open, promise-to-pay, agency or closed.';

COMMENT ON COLUMN northwind.collection_cases.total_overdue_amount IS 'The amount in the currency''s major unit.';

COMMENT ON COLUMN northwind.collection_cases.total_overdue_currency IS 'The ISO 4217 currency of the amount.';

COMMENT ON COLUMN northwind.collection_cases.agency_name IS 'Collection agency, when outsourced.';

COMMENT ON COLUMN northwind.collection_cases.closed_on IS 'Date closed.';

COMMENT ON COLUMN northwind.collection_cases.notes IS 'Case notes.';

COMMENT ON COLUMN northwind.collection_cases.created_at IS 'When the row was created.';

COMMENT ON COLUMN northwind.collection_cases.created_by IS 'User name of the creator.';

COMMENT ON COLUMN northwind.collection_cases.updated_at IS 'When the row was last changed.';

COMMENT ON COLUMN northwind.collection_cases.updated_by IS 'User name of the last editor.';

COMMENT ON COLUMN northwind.customer_price_agreements.id IS 'Surrogate key of the customer price agreement.';

COMMENT ON COLUMN northwind.customer_price_agreements.agreement_number IS 'Contract number.';

COMMENT ON COLUMN northwind.customer_price_agreements.validity_starts_on IS 'First day of the range.';

COMMENT ON COLUMN northwind.customer_price_agreements.validity_ends_on IS 'Last day of the range; null when open-ended.';

COMMENT ON COLUMN northwind.customer_price_agreements.is_approved IS 'Whether pricing approved the agreement.';

COMMENT ON COLUMN northwind.customer_price_agreements.approved_on IS 'Approval date.';

COMMENT ON COLUMN northwind.customer_price_agreements.notes IS 'Negotiation notes.';

COMMENT ON COLUMN northwind.customer_price_agreements.created_at IS 'When the row was created.';

COMMENT ON COLUMN northwind.customer_price_agreements.created_by IS 'User name of the creator.';

COMMENT ON COLUMN northwind.customer_price_agreements.updated_at IS 'When the row was last changed.';

COMMENT ON COLUMN northwind.customer_price_agreements.updated_by IS 'User name of the last editor.';

COMMENT ON COLUMN northwind.leads.id IS 'Surrogate key of the lead.';

COMMENT ON COLUMN northwind.leads.company_name IS 'Prospect''s company.';

COMMENT ON COLUMN northwind.leads.contact_name_given_name IS 'First or given name.';

COMMENT ON COLUMN northwind.leads.contact_name_family_name IS 'Last or family name.';

COMMENT ON COLUMN northwind.leads.contact_name_title IS 'Salutation, such as Dr. or Ms.';

COMMENT ON COLUMN northwind.leads.email IS 'Prospect''s email.';

COMMENT ON COLUMN northwind.leads.phone_number IS 'Number in E.164 form, such as +14155550100.';

COMMENT ON COLUMN northwind.leads.phone_extension IS 'Internal extension.';

COMMENT ON COLUMN northwind.leads.status IS 'Qualification state.';

COMMENT ON COLUMN northwind.leads.estimated_annual_value_amount IS 'The amount in the currency''s major unit.';

COMMENT ON COLUMN northwind.leads.estimated_annual_value_currency IS 'The ISO 4217 currency of the amount.';

COMMENT ON COLUMN northwind.leads.notes IS 'Qualification notes.';

COMMENT ON COLUMN northwind.leads.converted_on IS 'Date the lead became a customer.';

COMMENT ON COLUMN northwind.leads.created_at IS 'When the row was created.';

COMMENT ON COLUMN northwind.leads.created_by IS 'User name of the creator.';

COMMENT ON COLUMN northwind.leads.updated_at IS 'When the row was last changed.';

COMMENT ON COLUMN northwind.leads.updated_by IS 'User name of the last editor.';

COMMENT ON COLUMN northwind.leads.deleted_at IS 'When the row was marked deleted; null while it is live.';

COMMENT ON COLUMN northwind.applied_promotions.id IS 'Surrogate key of the applied promotion.';

COMMENT ON COLUMN northwind.applied_promotions.discount_amount_amount IS 'The amount in the currency''s major unit.';

COMMENT ON COLUMN northwind.applied_promotions.discount_amount_currency IS 'The ISO 4217 currency of the amount.';

COMMENT ON COLUMN northwind.applied_promotions.applied_at IS 'When it was applied.';

COMMENT ON COLUMN northwind.countries.code IS 'ISO 3166-1 alpha-2 code; the natural key.';

COMMENT ON COLUMN northwind.countries.alpha3_code IS 'ISO 3166-1 alpha-3 code.';

COMMENT ON COLUMN northwind.countries.numeric_code IS 'ISO 3166-1 numeric code.';

COMMENT ON COLUMN northwind.countries.name IS 'English short name.';

COMMENT ON COLUMN northwind.countries.calling_code IS 'International dialling prefix, such as +49.';

COMMENT ON COLUMN northwind.countries.is_eu_member IS 'Whether intra-community VAT rules apply.';

COMMENT ON COLUMN northwind.countries.is_sanctioned IS 'Whether trade with the country is blocked by export controls.';

COMMENT ON COLUMN northwind.stock_adjustments.id IS 'Surrogate key of the stock adjustment.';

COMMENT ON COLUMN northwind.stock_adjustments.adjustment_number IS 'Adjustment number.';

COMMENT ON COLUMN northwind.stock_adjustments.adjusted_at IS 'When the adjustment was booked.';

COMMENT ON COLUMN northwind.stock_adjustments.quantity_delta IS 'Change in quantity.';

COMMENT ON COLUMN northwind.stock_adjustments.value_delta_amount IS 'The amount in the currency''s major unit.';

COMMENT ON COLUMN northwind.stock_adjustments.value_delta_currency IS 'The ISO 4217 currency of the amount.';

COMMENT ON COLUMN northwind.stock_adjustments.notes IS 'Explanation.';

COMMENT ON COLUMN northwind.stock_adjustments.created_at IS 'When the row was created.';

COMMENT ON COLUMN northwind.stock_adjustments.created_by IS 'User name of the creator.';

COMMENT ON COLUMN northwind.stock_adjustments.updated_at IS 'When the row was last changed.';

COMMENT ON COLUMN northwind.stock_adjustments.updated_by IS 'User name of the last editor.';

COMMENT ON COLUMN northwind.opportunities.id IS 'Surrogate key of the opportunity.';

COMMENT ON COLUMN northwind.opportunities.name IS 'Short description of the deal.';

COMMENT ON COLUMN northwind.opportunities.amount_amount IS 'The amount in the currency''s major unit.';

COMMENT ON COLUMN northwind.opportunities.amount_currency IS 'The ISO 4217 currency of the amount.';

COMMENT ON COLUMN northwind.opportunities.probability IS 'Win probability; defaults from the stage.';

COMMENT ON COLUMN northwind.opportunities.expected_close_on IS 'Expected decision date.';

COMMENT ON COLUMN northwind.opportunities.closed_on IS 'Date the deal was won or lost.';

COMMENT ON COLUMN northwind.opportunities.loss_reason IS 'Why the deal was lost.';

COMMENT ON COLUMN northwind.opportunities.created_at IS 'When the row was created.';

COMMENT ON COLUMN northwind.opportunities.created_by IS 'User name of the creator.';

COMMENT ON COLUMN northwind.opportunities.updated_at IS 'When the row was last changed.';

COMMENT ON COLUMN northwind.opportunities.updated_by IS 'User name of the last editor.';

COMMENT ON COLUMN northwind.customer_segments.id IS 'Surrogate key of the customer segment.';

COMMENT ON COLUMN northwind.customer_segments.description IS 'Who belongs to the segment.';

COMMENT ON COLUMN northwind.customer_segments.code IS 'Short stable code used in integrations and imports.';

COMMENT ON COLUMN northwind.customer_segments.name IS 'Display name shown in pick lists.';

COMMENT ON COLUMN northwind.customer_segments.sort_order IS 'Position in pick lists.';

COMMENT ON COLUMN northwind.customer_segments.is_active IS 'Whether the value can be chosen for new records.';

COMMENT ON COLUMN northwind.coupons.id IS 'Surrogate key of the coupon.';

COMMENT ON COLUMN northwind.coupons.code IS 'Coupon code.';

COMMENT ON COLUMN northwind.coupons.max_uses IS 'Maximum redemptions of the code.';

COMMENT ON COLUMN northwind.coupons.used_count IS 'Redemptions so far.';

COMMENT ON COLUMN northwind.coupons.expires_on IS 'Last day the code works.';

COMMENT ON COLUMN northwind.coupons.created_at IS 'When the row was created.';

COMMENT ON COLUMN northwind.coupons.created_by IS 'User name of the creator.';

COMMENT ON COLUMN northwind.coupons.updated_at IS 'When the row was last changed.';

COMMENT ON COLUMN northwind.coupons.updated_by IS 'User name of the last editor.';

COMMENT ON COLUMN northwind.contacts.id IS 'Surrogate key of the contact.';

COMMENT ON COLUMN northwind.contacts.name_given_name IS 'First or given name.';

COMMENT ON COLUMN northwind.contacts.name_family_name IS 'Last or family name.';

COMMENT ON COLUMN northwind.contacts.name_title IS 'Salutation, such as Dr. or Ms.';

COMMENT ON COLUMN northwind.contacts.job_title IS 'Position at the party.';

COMMENT ON COLUMN northwind.contacts.email IS 'Work email.';

COMMENT ON COLUMN northwind.contacts.phone_number IS 'Number in E.164 form, such as +14155550100.';

COMMENT ON COLUMN northwind.contacts.phone_extension IS 'Internal extension.';

COMMENT ON COLUMN northwind.contacts.additional_phones IS 'Mobile and other numbers.';

COMMENT ON COLUMN northwind.contacts.is_primary IS 'Main contact of the party.';

COMMENT ON COLUMN northwind.contacts.is_billing_contact IS 'Receives invoices and statements.';

COMMENT ON COLUMN northwind.contacts.created_at IS 'When the row was created.';

COMMENT ON COLUMN northwind.contacts.created_by IS 'User name of the creator.';

COMMENT ON COLUMN northwind.contacts.updated_at IS 'When the row was last changed.';

COMMENT ON COLUMN northwind.contacts.updated_by IS 'User name of the last editor.';

COMMENT ON COLUMN northwind.contacts.deleted_at IS 'When the row was marked deleted; null while it is live.';

COMMENT ON COLUMN northwind.product_recommendations.id IS 'Surrogate key of the product recommendation.';

COMMENT ON COLUMN northwind.product_recommendations.recommendation_type IS 'Kind of suggestion - cross-sell, up-sell or accessory.';

COMMENT ON COLUMN northwind.product_recommendations.score IS 'Relevance score from the recommender.';

COMMENT ON COLUMN northwind.product_recommendations.is_manual IS 'Whether merchandising added it by hand.';

COMMENT ON COLUMN northwind.return_reasons.id IS 'Surrogate key of the return reason.';

COMMENT ON COLUMN northwind.return_reasons.is_customer_fault IS 'Whether a restocking fee applies.';

COMMENT ON COLUMN northwind.return_reasons.requires_inspection IS 'Whether goods must be inspected before credit.';

COMMENT ON COLUMN northwind.return_reasons.code IS 'Short stable code used in integrations and imports.';

COMMENT ON COLUMN northwind.return_reasons.name IS 'Display name shown in pick lists.';

COMMENT ON COLUMN northwind.return_reasons.sort_order IS 'Position in pick lists.';

COMMENT ON COLUMN northwind.return_reasons.is_active IS 'Whether the value can be chosen for new records.';

COMMENT ON COLUMN northwind.product_translations.id IS 'Surrogate key of the product translation.';

COMMENT ON COLUMN northwind.product_translations.name IS 'Translated name.';

COMMENT ON COLUMN northwind.product_translations.short_description IS 'Translated short description.';

COMMENT ON COLUMN northwind.product_translations.long_description IS 'Translated long description.';

COMMENT ON COLUMN northwind.replacements.id IS 'Surrogate key of the replacement.';

COMMENT ON COLUMN northwind.replacements.quantity IS 'Quantity sent.';

COMMENT ON COLUMN northwind.replacements.shipped_on IS 'Ship date.';

COMMENT ON COLUMN northwind.replacements.is_charged IS 'Whether the customer pays for it.';

COMMENT ON COLUMN northwind.replacements.notes IS 'Remarks.';

COMMENT ON COLUMN northwind.replacements.created_at IS 'When the row was created.';

COMMENT ON COLUMN northwind.replacements.created_by IS 'User name of the creator.';

COMMENT ON COLUMN northwind.replacements.updated_at IS 'When the row was last changed.';

COMMENT ON COLUMN northwind.replacements.updated_by IS 'User name of the last editor.';

COMMENT ON COLUMN northwind.stored_payment_methods.id IS 'Surrogate key of the stored payment method.';

COMMENT ON COLUMN northwind.stored_payment_methods.method_type IS 'card or bank-account.';

COMMENT ON COLUMN northwind.stored_payment_methods.card_brand IS 'Card network, for cards.';

COMMENT ON COLUMN northwind.stored_payment_methods.last4 IS 'Last four digits.';

COMMENT ON COLUMN northwind.stored_payment_methods.expires_on IS 'Expiry, for cards.';

COMMENT ON COLUMN northwind.stored_payment_methods.processor_token IS 'Token issued by the processor.';

COMMENT ON COLUMN northwind.stored_payment_methods.is_default IS 'Used for automatic payments.';

COMMENT ON COLUMN northwind.stored_payment_methods.created_at IS 'When the row was created.';

COMMENT ON COLUMN northwind.stored_payment_methods.created_by IS 'User name of the creator.';

COMMENT ON COLUMN northwind.stored_payment_methods.updated_at IS 'When the row was last changed.';

COMMENT ON COLUMN northwind.stored_payment_methods.updated_by IS 'User name of the last editor.';

COMMENT ON COLUMN northwind.stored_payment_methods.deleted_at IS 'When the row was marked deleted; null while it is live.';

COMMENT ON COLUMN northwind.credit_profiles.id IS 'Surrogate key of the credit profile.';

COMMENT ON COLUMN northwind.credit_profiles.credit_limit_amount IS 'The amount in the currency''s major unit.';

COMMENT ON COLUMN northwind.credit_profiles.credit_limit_currency IS 'The ISO 4217 currency of the amount.';

COMMENT ON COLUMN northwind.credit_profiles.risk_rating IS 'Internal rating from A1 (best) to D (worst).';

COMMENT ON COLUMN northwind.credit_profiles.external_score IS 'Score from the credit bureau.';

COMMENT ON COLUMN northwind.credit_profiles.last_reviewed_on IS 'Date of the last credit review.';

COMMENT ON COLUMN northwind.credit_profiles.review_notes IS 'Reviewer''s notes.';

COMMENT ON COLUMN northwind.credit_profiles.created_at IS 'When the row was created.';

COMMENT ON COLUMN northwind.credit_profiles.created_by IS 'User name of the creator.';

COMMENT ON COLUMN northwind.credit_profiles.updated_at IS 'When the row was last changed.';

COMMENT ON COLUMN northwind.credit_profiles.updated_by IS 'User name of the last editor.';

COMMENT ON COLUMN northwind.warehouse_shifts.id IS 'Surrogate key of the warehouse shift.';

COMMENT ON COLUMN northwind.warehouse_shifts.name IS 'Shift name.';

COMMENT ON COLUMN northwind.warehouse_shifts.starts_at IS 'Start time, local to the warehouse.';

COMMENT ON COLUMN northwind.warehouse_shifts.ends_at IS 'End time.';

COMMENT ON COLUMN northwind.warehouse_shifts.weekdays IS 'Days worked, such as Mon-Fri.';

COMMENT ON COLUMN northwind.discount_rules.id IS 'Surrogate key of the discount rule.';

COMMENT ON COLUMN northwind.discount_rules.name IS 'Rule name.';

COMMENT ON COLUMN northwind.discount_rules.discount_type IS 'How the discount is computed.';

COMMENT ON COLUMN northwind.discount_rules.value IS 'Percentage or amount, depending on the type.';

COMMENT ON COLUMN northwind.discount_rules.priority IS 'Evaluation order; lower runs first.';

COMMENT ON COLUMN northwind.discount_rules.is_stackable IS 'Whether it combines with other discounts.';

COMMENT ON COLUMN northwind.discount_rules.validity_starts_on IS 'First day of the range.';

COMMENT ON COLUMN northwind.discount_rules.validity_ends_on IS 'Last day of the range; null when open-ended.';

COMMENT ON COLUMN northwind.discount_rules.created_at IS 'When the row was created.';

COMMENT ON COLUMN northwind.discount_rules.created_by IS 'User name of the creator.';

COMMENT ON COLUMN northwind.discount_rules.updated_at IS 'When the row was last changed.';

COMMENT ON COLUMN northwind.discount_rules.updated_by IS 'User name of the last editor.';

COMMENT ON COLUMN northwind.sales_commissions.id IS 'Surrogate key of the sales commission.';

COMMENT ON COLUMN northwind.sales_commissions.commission_rate IS 'Rate applied.';

COMMENT ON COLUMN northwind.sales_commissions.commission_amount_amount IS 'The amount in the currency''s major unit.';

COMMENT ON COLUMN northwind.sales_commissions.commission_amount_currency IS 'The ISO 4217 currency of the amount.';

COMMENT ON COLUMN northwind.sales_commissions.earned_on IS 'Date it was earned, usually the invoice date.';

COMMENT ON COLUMN northwind.sales_commissions.is_paid IS 'Whether payroll paid it.';

COMMENT ON COLUMN northwind.sales_commissions.paid_on IS 'Payroll date.';

COMMENT ON COLUMN northwind.sales_commissions.created_at IS 'When the row was created.';

COMMENT ON COLUMN northwind.sales_commissions.created_by IS 'User name of the creator.';

COMMENT ON COLUMN northwind.sales_commissions.updated_at IS 'When the row was last changed.';

COMMENT ON COLUMN northwind.sales_commissions.updated_by IS 'User name of the last editor.';

COMMENT ON COLUMN northwind.supplier_quotes.id IS 'Surrogate key of the supplier quote.';

COMMENT ON COLUMN northwind.supplier_quotes.quoted_on IS 'Date quoted.';

COMMENT ON COLUMN northwind.supplier_quotes.unit_cost_amount IS 'The amount in the currency''s major unit.';

COMMENT ON COLUMN northwind.supplier_quotes.unit_cost_currency IS 'The ISO 4217 currency of the amount.';

COMMENT ON COLUMN northwind.supplier_quotes.lead_time_days IS 'Quoted lead time.';

COMMENT ON COLUMN northwind.supplier_quotes.valid_until IS 'Last day the quote holds.';

COMMENT ON COLUMN northwind.supplier_quotes.is_awarded IS 'Whether the business was awarded.';

COMMENT ON COLUMN northwind.promotion_conditions.id IS 'Surrogate key of the promotion condition.';

COMMENT ON COLUMN northwind.promotion_conditions.condition_type IS 'Kind of condition - product-quantity, category-amount or order-amount.';

COMMENT ON COLUMN northwind.promotion_conditions.threshold_quantity IS 'Minimum quantity.';

COMMENT ON COLUMN northwind.promotion_conditions.threshold_amount_amount IS 'The amount in the currency''s major unit.';

COMMENT ON COLUMN northwind.promotion_conditions.threshold_amount_currency IS 'The ISO 4217 currency of the amount.';

COMMENT ON COLUMN northwind.hazard_classes.id IS 'Surrogate key of the hazard class.';

COMMENT ON COLUMN northwind.hazard_classes.un_number IS 'UN number of the typical substance.';

COMMENT ON COLUMN northwind.hazard_classes.packing_group IS 'Packing group I, II or III.';

COMMENT ON COLUMN northwind.hazard_classes.requires_placard IS 'Whether vehicles must display a placard.';

COMMENT ON COLUMN northwind.hazard_classes.code IS 'Short stable code used in integrations and imports.';

COMMENT ON COLUMN northwind.hazard_classes.name IS 'Display name shown in pick lists.';

COMMENT ON COLUMN northwind.hazard_classes.sort_order IS 'Position in pick lists.';

COMMENT ON COLUMN northwind.hazard_classes.is_active IS 'Whether the value can be chosen for new records.';

COMMENT ON COLUMN northwind.return_lines.id IS 'Surrogate key of the return line.';

COMMENT ON COLUMN northwind.return_lines.quantity IS 'Quantity authorized.';

COMMENT ON COLUMN northwind.return_lines.received_quantity IS 'Quantity received.';

COMMENT ON COLUMN northwind.return_lines.item_condition IS 'new, opened or damaged.';

COMMENT ON COLUMN northwind.return_lines.disposition IS 'restock, scrap or return-to-vendor.';

COMMENT ON COLUMN northwind.return_lines.credit_amount_amount IS 'The amount in the currency''s major unit.';

COMMENT ON COLUMN northwind.return_lines.credit_amount_currency IS 'The ISO 4217 currency of the amount.';

COMMENT ON COLUMN northwind.return_lines.created_at IS 'When the row was created.';

COMMENT ON COLUMN northwind.return_lines.created_by IS 'User name of the creator.';

COMMENT ON COLUMN northwind.return_lines.updated_at IS 'When the row was last changed.';

COMMENT ON COLUMN northwind.return_lines.updated_by IS 'User name of the last editor.';

COMMENT ON COLUMN northwind.dock_doors.id IS 'Surrogate key of the dock door.';

COMMENT ON COLUMN northwind.dock_doors.door_number IS 'Door number.';

COMMENT ON COLUMN northwind.dock_doors.door_type IS 'inbound, outbound or both.';

COMMENT ON COLUMN northwind.dock_doors.has_leveler IS 'Whether it has a dock leveler.';

COMMENT ON COLUMN northwind.dock_doors.is_active IS 'Whether it can be scheduled.';

COMMENT ON COLUMN northwind.supplier_scorecards.id IS 'Surrogate key of the supplier scorecard.';

COMMENT ON COLUMN northwind.supplier_scorecards.period_starts_on IS 'First day of the range.';

COMMENT ON COLUMN northwind.supplier_scorecards.period_ends_on IS 'Last day of the range; null when open-ended.';

COMMENT ON COLUMN northwind.supplier_scorecards.on_time_rate IS 'Share of lines delivered on time.';

COMMENT ON COLUMN northwind.supplier_scorecards.fill_rate IS 'Share of quantity delivered.';

COMMENT ON COLUMN northwind.supplier_scorecards.quality_rate IS 'Share of quantity accepted.';

COMMENT ON COLUMN northwind.supplier_scorecards.overall_score IS 'Weighted score.';

COMMENT ON COLUMN northwind.supplier_scorecards.comments IS 'Reviewer''s comments.';

COMMENT ON COLUMN northwind.supplier_scorecards.created_at IS 'When the row was created.';

COMMENT ON COLUMN northwind.supplier_scorecards.created_by IS 'User name of the creator.';

COMMENT ON COLUMN northwind.supplier_scorecards.updated_at IS 'When the row was last changed.';

COMMENT ON COLUMN northwind.supplier_scorecards.updated_by IS 'User name of the last editor.';

COMMENT ON COLUMN northwind.advance_ship_notices.id IS 'Surrogate key of the advance ship notice.';

COMMENT ON COLUMN northwind.advance_ship_notices.asn_number IS 'Supplier''s ASN number.';

COMMENT ON COLUMN northwind.advance_ship_notices.shipped_on IS 'Ship date.';

COMMENT ON COLUMN northwind.advance_ship_notices.expected_arrival_on IS 'Expected arrival.';

COMMENT ON COLUMN northwind.advance_ship_notices.carrier_name IS 'Carrier.';

COMMENT ON COLUMN northwind.advance_ship_notices.tracking_number IS 'Tracking number.';

COMMENT ON COLUMN northwind.advance_ship_notices.raw_document IS 'Original EDI document, as JSON.';

COMMENT ON COLUMN northwind.product_certifications.id IS 'Surrogate key of the product certification.';

COMMENT ON COLUMN northwind.product_certifications.certification_type IS 'Scheme, such as UL or CE.';

COMMENT ON COLUMN northwind.product_certifications.certificate_number IS 'Certificate number.';

COMMENT ON COLUMN northwind.product_certifications.issued_by IS 'Certification body.';

COMMENT ON COLUMN northwind.product_certifications.validity_starts_on IS 'First day of the range.';

COMMENT ON COLUMN northwind.product_certifications.validity_ends_on IS 'Last day of the range; null when open-ended.';

COMMENT ON COLUMN northwind.product_certifications.created_at IS 'When the row was created.';

COMMENT ON COLUMN northwind.product_certifications.created_by IS 'User name of the creator.';

COMMENT ON COLUMN northwind.product_certifications.updated_at IS 'When the row was last changed.';

COMMENT ON COLUMN northwind.product_certifications.updated_by IS 'User name of the last editor.';

COMMENT ON COLUMN northwind.proofs_of_delivery.id IS 'Surrogate key of the proof of delivery.';

COMMENT ON COLUMN northwind.proofs_of_delivery.delivered_at IS 'Delivery time.';

COMMENT ON COLUMN northwind.proofs_of_delivery.received_by_name IS 'Name of the person who signed.';

COMMENT ON COLUMN northwind.proofs_of_delivery.signature_image_url IS 'Signature capture.';

COMMENT ON COLUMN northwind.proofs_of_delivery.photo_url IS 'Photo of the delivered goods.';

COMMENT ON COLUMN northwind.proofs_of_delivery.location_latitude IS 'Degrees north of the equator.';

COMMENT ON COLUMN northwind.proofs_of_delivery.location_longitude IS 'Degrees east of Greenwich.';

COMMENT ON COLUMN northwind.proofs_of_delivery.notes IS 'Driver''s remarks.';

COMMENT ON COLUMN northwind.standard_costs.id IS 'Surrogate key of the standard cost.';

COMMENT ON COLUMN northwind.standard_costs.cost_amount IS 'The amount in the currency''s major unit.';

COMMENT ON COLUMN northwind.standard_costs.cost_currency IS 'The ISO 4217 currency of the amount.';

COMMENT ON COLUMN northwind.standard_costs.validity_starts_on IS 'First day of the range.';

COMMENT ON COLUMN northwind.standard_costs.validity_ends_on IS 'Last day of the range; null when open-ended.';

COMMENT ON COLUMN northwind.standard_costs.costing_method IS 'standard, average or last-purchase.';

COMMENT ON COLUMN northwind.standard_costs.created_at IS 'When the row was created.';

COMMENT ON COLUMN northwind.standard_costs.created_by IS 'User name of the creator.';

COMMENT ON COLUMN northwind.standard_costs.updated_at IS 'When the row was last changed.';

COMMENT ON COLUMN northwind.standard_costs.updated_by IS 'User name of the last editor.';

COMMENT ON COLUMN northwind.landed_costs.id IS 'Surrogate key of the landed cost.';

COMMENT ON COLUMN northwind.landed_costs.cost_type IS 'freight, duty, insurance or brokerage.';

COMMENT ON COLUMN northwind.landed_costs.amount_amount IS 'The amount in the currency''s major unit.';

COMMENT ON COLUMN northwind.landed_costs.amount_currency IS 'The ISO 4217 currency of the amount.';

COMMENT ON COLUMN northwind.landed_costs.allocation_method IS 'value, weight or quantity.';

COMMENT ON COLUMN northwind.route_stops.id IS 'Surrogate key of the route stop.';

COMMENT ON COLUMN northwind.route_stops.stop_number IS 'Position on the route.';

COMMENT ON COLUMN northwind.route_stops.address_line1 IS 'Street and number, or PO box.';

COMMENT ON COLUMN northwind.route_stops.address_line2 IS 'Suite, floor or building.';

COMMENT ON COLUMN northwind.route_stops.address_city IS 'City or locality.';

COMMENT ON COLUMN northwind.route_stops.address_region IS 'State, province or county.';

COMMENT ON COLUMN northwind.route_stops.address_postal_code IS 'Postal or ZIP code.';

COMMENT ON COLUMN northwind.route_stops.address_country_code IS 'ISO country code.';

COMMENT ON COLUMN northwind.route_stops.location_latitude IS 'Degrees north of the equator.';

COMMENT ON COLUMN northwind.route_stops.location_longitude IS 'Degrees east of Greenwich.';

COMMENT ON COLUMN northwind.route_stops.planned_arrival_at IS 'Planned arrival.';

COMMENT ON COLUMN northwind.route_stops.actual_arrival_at IS 'Actual arrival.';

COMMENT ON COLUMN northwind.surcharges.id IS 'Surrogate key of the surcharge.';

COMMENT ON COLUMN northwind.surcharges.code IS 'Surcharge code.';

COMMENT ON COLUMN northwind.surcharges.name IS 'Name printed on invoices.';

COMMENT ON COLUMN northwind.surcharges.calculation IS 'percent or flat.';

COMMENT ON COLUMN northwind.surcharges.rate IS 'Percentage, for percent surcharges.';

COMMENT ON COLUMN northwind.surcharges.amount_amount IS 'The amount in the currency''s major unit.';

COMMENT ON COLUMN northwind.surcharges.amount_currency IS 'The ISO 4217 currency of the amount.';

COMMENT ON COLUMN northwind.surcharges.validity_starts_on IS 'First day of the range.';

COMMENT ON COLUMN northwind.surcharges.validity_ends_on IS 'Last day of the range; null when open-ended.';

COMMENT ON COLUMN northwind.surcharges.created_at IS 'When the row was created.';

COMMENT ON COLUMN northwind.surcharges.created_by IS 'User name of the creator.';

COMMENT ON COLUMN northwind.surcharges.updated_at IS 'When the row was last changed.';

COMMENT ON COLUMN northwind.surcharges.updated_by IS 'User name of the last editor.';

COMMENT ON COLUMN northwind.tracking_events.id IS 'Surrogate key of the tracking event.';

COMMENT ON COLUMN northwind.tracking_events.occurred_at IS 'When the scan happened.';

COMMENT ON COLUMN northwind.tracking_events.event_code IS 'Carrier event code.';

COMMENT ON COLUMN northwind.tracking_events.description IS 'Event text.';

COMMENT ON COLUMN northwind.tracking_events.city IS 'Scan location.';

COMMENT ON COLUMN northwind.tracking_events.country_code IS 'Scan country.';

COMMENT ON COLUMN northwind.audit_events.id IS 'Surrogate key of the audit event.';

COMMENT ON COLUMN northwind.audit_events.occurred_at IS 'When the action happened.';

COMMENT ON COLUMN northwind.audit_events.action IS 'What happened, such as role.granted.';

COMMENT ON COLUMN northwind.audit_events.entity_type IS 'Kind of record affected.';

COMMENT ON COLUMN northwind.audit_events.entity_key IS 'Key of the record affected.';

COMMENT ON COLUMN northwind.audit_events.ip_address IS 'Client IP address.';

COMMENT ON COLUMN northwind.currencies.code IS 'ISO 4217 alphabetic code; the natural key.';

COMMENT ON COLUMN northwind.currencies.numeric_code IS 'ISO 4217 numeric code.';

COMMENT ON COLUMN northwind.currencies.name IS 'English name, such as Euro.';

COMMENT ON COLUMN northwind.currencies.symbol IS 'Display symbol, such as the euro sign.';

COMMENT ON COLUMN northwind.currencies.minor_units IS 'Number of decimals in the minor unit.';

COMMENT ON COLUMN northwind.currencies.is_active IS 'Whether new documents may use the currency.';

COMMENT ON COLUMN northwind.user_account_role.granted_at IS 'When the role was granted.';

COMMENT ON COLUMN northwind.user_account_role.granted_by IS 'User name of the administrator who granted it.';

COMMENT ON COLUMN northwind.user_account_role.expires_on IS 'Date the grant lapses; null for permanent grants.';

COMMENT ON COLUMN northwind.tax_rates.id IS 'Surrogate key of the tax rate.';

COMMENT ON COLUMN northwind.tax_rates.name IS 'Rate name, such as Colorado state sales tax.';

COMMENT ON COLUMN northwind.tax_rates.rate IS 'Rate.';

COMMENT ON COLUMN northwind.tax_rates.tax_category IS 'Goods category, such as general or food.';

COMMENT ON COLUMN northwind.tax_rates.validity_starts_on IS 'First day of the range.';

COMMENT ON COLUMN northwind.tax_rates.validity_ends_on IS 'Last day of the range; null when open-ended.';

COMMENT ON COLUMN northwind.tax_rates.created_at IS 'When the row was created.';

COMMENT ON COLUMN northwind.tax_rates.created_by IS 'User name of the creator.';

COMMENT ON COLUMN northwind.tax_rates.updated_at IS 'When the row was last changed.';

COMMENT ON COLUMN northwind.tax_rates.updated_by IS 'User name of the last editor.';

COMMENT ON COLUMN northwind.user_sessions.id IS 'Surrogate key of the user session.';

COMMENT ON COLUMN northwind.user_sessions.started_at IS 'When the session was created.';

COMMENT ON COLUMN northwind.user_sessions.expires_at IS 'Absolute expiry.';

COMMENT ON COLUMN northwind.user_sessions.last_seen_at IS 'Last request on the session.';

COMMENT ON COLUMN northwind.user_sessions.ip_address IS 'Client IP address (IPv4 or IPv6).';

COMMENT ON COLUMN northwind.user_sessions.user_agent IS 'Client user agent string.';

COMMENT ON COLUMN northwind.user_sessions.revoked_at IS 'When the session was revoked; null while valid.';

COMMENT ON COLUMN northwind.warehouse_equipment.id IS 'Surrogate key of the warehouse equipment.';

COMMENT ON COLUMN northwind.warehouse_equipment.asset_tag IS 'Asset tag.';

COMMENT ON COLUMN northwind.warehouse_equipment.equipment_type IS 'Kind, such as forklift or pallet jack.';

COMMENT ON COLUMN northwind.warehouse_equipment.manufacturer_name IS 'Maker.';

COMMENT ON COLUMN northwind.warehouse_equipment.model_name IS 'Model.';

COMMENT ON COLUMN northwind.warehouse_equipment.last_inspected_on IS 'Last safety inspection.';

COMMENT ON COLUMN northwind.warehouse_equipment.is_operational IS 'Whether it can be used.';

COMMENT ON COLUMN northwind.warehouse_equipment.created_at IS 'When the row was created.';

COMMENT ON COLUMN northwind.warehouse_equipment.created_by IS 'User name of the creator.';

COMMENT ON COLUMN northwind.warehouse_equipment.updated_at IS 'When the row was last changed.';

COMMENT ON COLUMN northwind.warehouse_equipment.updated_by IS 'User name of the last editor.';

COMMENT ON COLUMN northwind.ledger_accounts.id IS 'Surrogate key of the ledger account.';

COMMENT ON COLUMN northwind.ledger_accounts.account_number IS 'Account number.';

COMMENT ON COLUMN northwind.ledger_accounts.name IS 'Account name.';

COMMENT ON COLUMN northwind.ledger_accounts.account_type IS 'Statement classification.';

COMMENT ON COLUMN northwind.ledger_accounts.normal_balance IS 'debit or credit.';

COMMENT ON COLUMN northwind.ledger_accounts.is_postable IS 'Whether journal lines may post to it; summary accounts are not postable.';

COMMENT ON COLUMN northwind.ledger_accounts.is_active IS 'Whether it can be used.';

COMMENT ON COLUMN northwind.ledger_accounts.created_at IS 'When the row was created.';

COMMENT ON COLUMN northwind.ledger_accounts.created_by IS 'User name of the creator.';

COMMENT ON COLUMN northwind.ledger_accounts.updated_at IS 'When the row was last changed.';

COMMENT ON COLUMN northwind.ledger_accounts.updated_by IS 'User name of the last editor.';

COMMENT ON COLUMN northwind.standing_orders.id IS 'Surrogate key of the standing order.';

COMMENT ON COLUMN northwind.standing_orders.name IS 'Name shown to the customer.';

COMMENT ON COLUMN northwind.standing_orders.frequency IS 'weekly, biweekly or monthly.';

COMMENT ON COLUMN northwind.standing_orders.next_run_on IS 'Date the next order will be placed.';

COMMENT ON COLUMN northwind.standing_orders.is_active IS 'Whether it is running.';

COMMENT ON COLUMN northwind.standing_orders.validity_starts_on IS 'First day of the range.';

COMMENT ON COLUMN northwind.standing_orders.validity_ends_on IS 'Last day of the range; null when open-ended.';

COMMENT ON COLUMN northwind.standing_orders.created_at IS 'When the row was created.';

COMMENT ON COLUMN northwind.standing_orders.created_by IS 'User name of the creator.';

COMMENT ON COLUMN northwind.standing_orders.updated_at IS 'When the row was last changed.';

COMMENT ON COLUMN northwind.standing_orders.updated_by IS 'User name of the last editor.';

COMMENT ON COLUMN northwind.payment_terms.id IS 'Surrogate key of the payment term.';

COMMENT ON COLUMN northwind.payment_terms.net_days IS 'Days until the invoice is due.';

COMMENT ON COLUMN northwind.payment_terms.discount_days IS 'Days within which the early payment discount applies.';

COMMENT ON COLUMN northwind.payment_terms.discount_percent IS 'Early payment discount.';

COMMENT ON COLUMN northwind.payment_terms.code IS 'Short stable code used in integrations and imports.';

COMMENT ON COLUMN northwind.payment_terms.name IS 'Display name shown in pick lists.';

COMMENT ON COLUMN northwind.payment_terms.sort_order IS 'Position in pick lists.';

COMMENT ON COLUMN northwind.payment_terms.is_active IS 'Whether the value can be chosen for new records.';

COMMENT ON COLUMN northwind.permissions.id IS 'Surrogate key of the permission.';

COMMENT ON COLUMN northwind.permissions.code IS 'Resource and action, such as sales-order:approve.';

COMMENT ON COLUMN northwind.permissions.resource IS 'Protected resource.';

COMMENT ON COLUMN northwind.permissions.action IS 'Action on the resource.';

COMMENT ON COLUMN northwind.permissions.description IS 'What the permission allows.';

COMMENT ON COLUMN northwind.tariff_codes.id IS 'Surrogate key of the tariff code.';

COMMENT ON COLUMN northwind.tariff_codes.code IS 'HS or HTS code, such as 4015.19.1010.';

COMMENT ON COLUMN northwind.tariff_codes.description IS 'Official description.';

COMMENT ON COLUMN northwind.tariff_codes.duty_rate IS 'General duty rate.';

COMMENT ON COLUMN northwind.pick_lists.id IS 'Surrogate key of the pick list.';

COMMENT ON COLUMN northwind.pick_lists.pick_list_number IS 'Pick list number.';

COMMENT ON COLUMN northwind.pick_lists.status IS 'open, picking, done or short.';

COMMENT ON COLUMN northwind.pick_lists.released_at IS 'When it was released to the floor.';

COMMENT ON COLUMN northwind.pick_lists.completed_at IS 'When the last pick was confirmed.';

COMMENT ON COLUMN northwind.pick_lists.created_at IS 'When the row was created.';

COMMENT ON COLUMN northwind.pick_lists.created_by IS 'User name of the creator.';

COMMENT ON COLUMN northwind.pick_lists.updated_at IS 'When the row was last changed.';

COMMENT ON COLUMN northwind.pick_lists.updated_by IS 'User name of the last editor.';

COMMENT ON COLUMN northwind.inventory_holds.id IS 'Surrogate key of the inventory hold.';

COMMENT ON COLUMN northwind.inventory_holds.reason IS 'Why the stock is held.';

COMMENT ON COLUMN northwind.inventory_holds.placed_at IS 'When the hold was placed.';

COMMENT ON COLUMN northwind.inventory_holds.released_at IS 'When it was released.';

COMMENT ON COLUMN northwind.inventory_holds.quantity IS 'Quantity held; null for everything in scope.';

COMMENT ON COLUMN northwind.inventory_holds.created_at IS 'When the row was created.';

COMMENT ON COLUMN northwind.inventory_holds.created_by IS 'User name of the creator.';

COMMENT ON COLUMN northwind.inventory_holds.updated_at IS 'When the row was last changed.';

COMMENT ON COLUMN northwind.inventory_holds.updated_by IS 'User name of the last editor.';

COMMENT ON COLUMN northwind.warranty_claims.id IS 'Surrogate key of the warranty claim.';

COMMENT ON COLUMN northwind.warranty_claims.claim_number IS 'Claim number.';

COMMENT ON COLUMN northwind.warranty_claims.filed_on IS 'Filing date.';

COMMENT ON COLUMN northwind.warranty_claims.failure_description IS 'What failed.';

COMMENT ON COLUMN northwind.warranty_claims.status IS 'filed, approved, denied or reimbursed.';

COMMENT ON COLUMN northwind.warranty_claims.claim_amount_amount IS 'The amount in the currency''s major unit.';

COMMENT ON COLUMN northwind.warranty_claims.claim_amount_currency IS 'The ISO 4217 currency of the amount.';

COMMENT ON COLUMN northwind.warranty_claims.resolution IS 'Outcome.';

COMMENT ON COLUMN northwind.warranty_claims.created_at IS 'When the row was created.';

COMMENT ON COLUMN northwind.warranty_claims.created_by IS 'User name of the creator.';

COMMENT ON COLUMN northwind.warranty_claims.updated_at IS 'When the row was last changed.';

COMMENT ON COLUMN northwind.warranty_claims.updated_by IS 'User name of the last editor.';

COMMENT ON COLUMN northwind.supplier_certifications.id IS 'Surrogate key of the supplier certification.';

COMMENT ON COLUMN northwind.supplier_certifications.certification_type IS 'Scheme.';

COMMENT ON COLUMN northwind.supplier_certifications.certificate_number IS 'Certificate number.';

COMMENT ON COLUMN northwind.supplier_certifications.validity_starts_on IS 'First day of the range.';

COMMENT ON COLUMN northwind.supplier_certifications.validity_ends_on IS 'Last day of the range; null when open-ended.';

COMMENT ON COLUMN northwind.supplier_certifications.document_url IS 'Scanned certificate.';

COMMENT ON COLUMN northwind.supplier_certifications.created_at IS 'When the row was created.';

COMMENT ON COLUMN northwind.supplier_certifications.created_by IS 'User name of the creator.';

COMMENT ON COLUMN northwind.supplier_certifications.updated_at IS 'When the row was last changed.';

COMMENT ON COLUMN northwind.supplier_certifications.updated_by IS 'User name of the last editor.';

COMMENT ON COLUMN northwind.order_charges.id IS 'Surrogate key of the order charge.';

COMMENT ON COLUMN northwind.order_charges.charge_type IS 'freight, handling, fuel-surcharge or small-order.';

COMMENT ON COLUMN northwind.order_charges.description IS 'Text printed on the invoice.';

COMMENT ON COLUMN northwind.order_charges.amount_amount IS 'The amount in the currency''s major unit.';

COMMENT ON COLUMN northwind.order_charges.amount_currency IS 'The ISO 4217 currency of the amount.';

COMMENT ON COLUMN northwind.order_charges.is_taxable IS 'Whether sales tax applies.';

COMMENT ON COLUMN northwind.product_barcodes.id IS 'Surrogate key of the product barcode.';

COMMENT ON COLUMN northwind.product_barcodes.symbology IS 'Barcode type, such as GTIN-13, UPC-A or GS1-128.';

COMMENT ON COLUMN northwind.product_barcodes.value IS 'Encoded value.';

COMMENT ON COLUMN northwind.product_barcodes.is_primary IS 'Barcode printed on labels by default.';

COMMENT ON COLUMN northwind.sales_channels.id IS 'Surrogate key of the sales channel.';

COMMENT ON COLUMN northwind.sales_channels.code IS 'Channel code.';

COMMENT ON COLUMN northwind.sales_channels.name IS 'Channel name.';

COMMENT ON COLUMN northwind.sales_channels.is_marketplace IS 'Whether a third party hosts the channel.';

COMMENT ON COLUMN northwind.sales_channels.commission_rate IS 'Fee the channel charges on sales.';

COMMENT ON COLUMN northwind.sales_channels.created_at IS 'When the row was created.';

COMMENT ON COLUMN northwind.sales_channels.created_by IS 'User name of the creator.';

COMMENT ON COLUMN northwind.sales_channels.updated_at IS 'When the row was last changed.';

COMMENT ON COLUMN northwind.sales_channels.updated_by IS 'User name of the last editor.';

-- product_collection_product: the table's schema or comment changed; review by hand.

-- price_list_customer_group: the table's schema or comment changed; review by hand.

-- team_employee: the table's schema or comment changed; review by hand.

-- promotion_customer_segment: the table's schema or comment changed; review by hand.

-- role_permission: the table's schema or comment changed; review by hand.

COMMIT;
