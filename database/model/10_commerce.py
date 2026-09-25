"""commerce: carts, orders, provider-agnostic payments, refunds, deposits, Marquee delegation.

Settled money is integer minor units (spec 'Six decimal places'). No card data is stored;
payment providers are addressed by provider_code + provider_reference (DEC-010 still open).
"""
from dsl import *

S = "commerce"

table(S, "tender_definition", MASTER, TENANT_OPT, key=None,
      statuses=["Active", "Retired"],
      cols=[
          col("tender_code", "text"),
          col("tender_name", "text"),
          col("tender_type", "text", check="tender_type IN ('Card', 'Cash', 'RoomCharge', 'GiftCard', 'MemberAccount', 'External')"),
          CURRENCY(null=True),
          col("external_mapping_id", "uuid", null=True, fk="core.external_mapping"),
      ],
      uniques=[("code_uq", "NULLS NOT DISTINCT (tenant_id, property_id, tender_code)")])

table(S, "cart", AGGREGATE, PROPERTY, key=None, source_cols=False, handoff="completed",
      statuses=["Open", "Converted", "Abandoned", "Expired"],
      cols=[
          col("guest_id", "uuid", null=True, fk="guest.guest"),
          col("appointment_id", "uuid", null=True, fk="scheduling.appointment", same_property=True),
          col("visit_id", "uuid", null=True, fk="visit.visit", same_property=True),
          CURRENCY(),
          col("expires_at", "timestamptz", null=True),
          col("converted_order_id", "uuid", null=True, fk="commerce.commerce_order", same_property=True),
      ],
      checks=[("converted_has_order", "(status = 'Converted') = (converted_order_id IS NOT NULL)")])

table(S, "cart_line", CHILD, PROPERTY, handoff="completed",
      cols=[
          col("cart_id", "uuid", fk="commerce.cart", same_property=True),
          col("line_number", "smallint", check="line_number >= 1"),
          col("commerce_catalog_item_id", "uuid", fk="catalog.commerce_catalog_item"),
          col("inventory_item_variant_id", "uuid", null=True, fk="inventory.inventory_item_variant"),
          col("appointment_id", "uuid", null=True, fk="scheduling.appointment", same_property=True),
          col("description", "text"),
          col("quantity", QTY, default="1", check="quantity > 0"),
          col("unit_price_minor", "bigint"),
      ],
      uniques=[("number_uq", "tenant_id, cart_id, line_number")])

table(S, "commerce_order", AGGREGATE, PROPERTY, key=None, handoff="completed",
      spec="§53.2 Commerce; DEC-001 (owner_system); DEC-002 (Marquee owns delegated commerce)",
      doc="When Marquee owns commerce for the property, owner_system = 'Marquee' and source_key is its order id.",
      statuses=["Draft", "Open", "Delegated", "PartiallyPaid", "Paid", "PartiallyRefunded", "Refunded", "Voided"],
      cols=[
          col("order_number", "text", null=True),
          col("guest_id", "uuid", null=True, fk="guest.guest"),
          col("appointment_id", "uuid", null=True, fk="scheduling.appointment", same_property=True),
          col("visit_id", "uuid", null=True, fk="visit.visit", same_property=True),
          col("owner_system", "text", default="'Spa'", check="owner_system IN ('Spa', 'Marquee', 'Pos')"),
          col("ordered_at", "timestamptz", default="now()"),
          CURRENCY(),
          col("subtotal_minor", "bigint", default="0"),
          col("discount_total_minor", "bigint", default="0", check="discount_total_minor >= 0"),
          col("tax_total_minor", "bigint", default="0", check="tax_total_minor >= 0"),
          col("tip_total_minor", "bigint", default="0", check="tip_total_minor >= 0"),
          col("total_minor", "bigint", default="0"),
      ],
      checks=[("delegated_is_external", "status <> 'Delegated' OR owner_system <> 'Spa'")],
      indexes=["UNIQUE commerce_order_number_uq ON commerce.commerce_order (tenant_id, property_id, order_number) WHERE order_number IS NOT NULL"],
      dropped=[("amount/subtotal/tax_total/grand_total numeric(22,6)", "*_minor bigint (settled amounts)"),
               ("currency", "currency_code"), ("effective_from/effective_to", "an order is not effective-dated")])

table(S, "order_line", CHILD, PROPERTY, handoff="completed",
      cols=[
          col("commerce_order_id", "uuid", fk="commerce.commerce_order", same_property=True),
          col("line_number", "smallint", check="line_number >= 1"),
          col("line_kind", "text", check="line_kind IN ('Service', 'Retail', 'Fee', 'Deposit', 'Discount', 'Tip')"),
          col("commerce_catalog_item_id", "uuid", null=True, fk="catalog.commerce_catalog_item"),
          col("appointment_id", "uuid", null=True, fk="scheduling.appointment", same_property=True),
          col("inventory_item_variant_id", "uuid", null=True, fk="inventory.inventory_item_variant"),
          col("location_id", "uuid", null=True, fk="resources.location", same_property=True),
          col("product_lot_id", "uuid", null=True, fk="inventory.product_lot"),
          col("staff_id", "uuid", null=True, doc="provider credited (commission); workforce is not a commerce dependency"),
          col("original_order_line_id", "uuid", null=True, fk="commerce.order_line.order_line_id"),
          col("description", "text"),
          col("quantity_sold", QTY, check="quantity_sold <> 0"),
          col("unit_price_minor", "bigint"),
          col("discount_minor", "bigint", default="0"),
          col("tax_minor", "bigint", default="0"),
          col("tip_minor", "bigint", default="0"),
          col("net_minor", "bigint"),
          col("tax_code", "text", null=True),
          col("revenue_center_code", "text", null=True),
          col("fulfilled_at", "timestamptz", null=True),
      ],
      uniques=[("number_uq", "tenant_id, commerce_order_id, line_number")],
      dropped=[("amount/currency", "*_minor on the line, currency on the order"), ("visit_id", "the order's")])

table(S, "payment_intent", AGGREGATE, PROPERTY, key=None, source_cols=False, handoff="completed",
      spec="§53.2 Commerce (standalone deposit/payment/refund through an approved provider); DEC-010 open",
      statuses=["RequiresPaymentMethod", "RequiresAction", "Authorized", "Captured", "Cancelled", "Failed", "Expired"],
      cols=[
          col("commerce_order_id", "uuid", null=True, fk="commerce.commerce_order", same_property=True),
          col("appointment_id", "uuid", null=True, fk="scheduling.appointment", same_property=True),
          col("purpose", "text", check="purpose IN ('Deposit', 'Payment', 'NoShowFee', 'CancellationFee')"),
          col("amount_minor", "bigint", check="amount_minor > 0"),
          CURRENCY(),
          col("provider_code", "text", doc="payment adapter, chosen when DEC-010 lands"),
          col("provider_reference", "text", null=True),
          col("capture_method", "text", default="'Automatic'", check="capture_method IN ('Automatic', 'Manual')"),
          col("idempotency_key", "text"),
          col("expires_at", "timestamptz", null=True),
      ],
      checks=[("has_subject", "num_nonnulls(commerce_order_id, appointment_id) >= 1")],
      uniques=[("idempotency_uq", "tenant_id, idempotency_key")],
      indexes=["UNIQUE payment_intent_provider_uq ON commerce.payment_intent (tenant_id, provider_code, provider_reference) "
               "WHERE provider_reference IS NOT NULL"],
      dropped=[("amount numeric/currency", "amount_minor/currency_code")])

table(S, "payment_transaction", LEDGER, PROPERTY, handoff="completed",
      spec="PAYMENT_OUTCOME_AMBIGUOUS -> outcome 'Unknown' pending reconciliation",
      cols=[
          col("payment_intent_id", "uuid", null=True, fk="commerce.payment_intent", same_property=True),
          col("commerce_order_id", "uuid", null=True, fk="commerce.commerce_order", same_property=True),
          col("visit_id", "uuid", null=True, fk="visit.visit", same_property=True),
          col("tender_definition_id", "uuid", fk="commerce.tender_definition"),
          col("transaction_type", "text", check="transaction_type IN ('Authorization', 'Capture', 'Sale', 'Void', "
                                                "'Refund', 'Chargeback', 'Adjustment')"),
          col("outcome", "text", check="outcome IN ('Approved', 'Declined', 'Error', 'Pending', 'Unknown')"),
          col("amount_minor", "bigint", check="amount_minor >= 0"),
          CURRENCY(),
          col("provider_code", "text"),
          col("provider_reference", "text", null=True),
          col("card_brand", "text", null=True),
          col("card_last4", "char(4)", null=True, check="card_last4 ~ '^[0-9]{4}$'"),
          col("processed_at", "timestamptz"),
          col("original_transaction_id", "uuid", null=True, fk="commerce.payment_transaction.payment_transaction_id"),
          col("idempotency_key", "text"),
      ],
      checks=[("reversal_has_original", "transaction_type NOT IN ('Void', 'Refund', 'Chargeback') OR original_transaction_id IS NOT NULL")],
      uniques=[("idempotency_uq", "tenant_id, idempotency_key")],
      dropped=[("tender_code", "tender_definition_id"), ("amount numeric/currency", "amount_minor/currency_code"),
               ("effective_from/effective_to", "processed_at")])

table(S, "refund", AGGREGATE, PROPERTY, key=None, source_cols=False, handoff="completed",
      spec="finance commerce:refund:approved; SEC-014 dual control",
      statuses=["Requested", "Approved", "Rejected", "Submitted", "Settled", "Failed"],
      cols=[
          col("payment_transaction_id", "uuid", fk="commerce.payment_transaction", same_property=True),
          col("amount_minor", "bigint", check="amount_minor > 0"),
          CURRENCY(),
          col("reason_code", "text"),
          col("approved_by", "uuid", null=True),
          col("approved_at", "timestamptz", null=True),
          col("provider_reference", "text", null=True),
          col("settled_at", "timestamptz", null=True),
          col("idempotency_key", "text"),
      ],
      checks=[("approved_complete", "status NOT IN ('Approved', 'Submitted', 'Settled') OR approved_by IS NOT NULL"),
              ("dual_control", "approved_by IS NULL OR approved_by IS DISTINCT FROM created_by")],
      uniques=[("idempotency_uq", "tenant_id, idempotency_key")],
      dropped=[("requested_at", "created_at"), ("reason_code_value", "reason_code")])

table(S, "deposit_ledger_entry", LEDGER, PROPERTY, handoff="completed",
      cols=[
          col("appointment_id", "uuid", fk="scheduling.appointment", same_property=True),
          col("payment_transaction_id", "uuid", null=True, fk="commerce.payment_transaction", same_property=True),
          col("entry_type", "text", check="entry_type IN ('Collected', 'Applied', 'Refunded', 'Forfeited')"),
          col("amount_minor", "bigint", doc="signed: Collected positive; Applied/Refunded/Forfeited negative"),
          CURRENCY(),
          col("reason_code", "text", null=True),
      ],
      checks=[("sign_matches_type", "(entry_type = 'Collected') = (amount_minor > 0) AND amount_minor <> 0")])

table(S, "receipt", AGGREGATE, PROPERTY, key=None, source_cols=False, handoff="completed",
      statuses=["Issued", "Voided", "Reissued"],
      cols=[
          col("commerce_order_id", "uuid", fk="commerce.commerce_order", same_property=True),
          col("receipt_number", "text"),
          col("issued_at", "timestamptz", default="now()"),
          col("delivery_channel", "text", check="delivery_channel IN ('Print', 'Email', 'Sms')"),
      ],
      uniques=[("number_uq", "tenant_id, property_id, receipt_number")],
      dropped=[("amount/currency", "the order's totals")])

table(S, "commerce_reference", AGGREGATE, PROPERTY, pk="reference_id", key=None, source_cols=False,
      spec="MCI Marquee commerce delegation; OWNERSHIP_AMBIGUOUS",
      doc="An outbound call to the system that owns commerce, and what came back.",
      statuses=["Pending", "Sent", "Confirmed", "Failed", "Ambiguous"],
      cols=[
          col("appointment_id", "uuid", null=True, fk="scheduling.appointment", same_property=True),
          col("commerce_order_id", "uuid", null=True, fk="commerce.commerce_order", same_property=True),
          col("capability_code", "text"),
          col("owner_system", "text", check="owner_system IN ('Marquee', 'Pos', 'Pms', 'External')"),
          col("operation", "text", check="operation IN ('CreateCart', 'AddLine', 'Checkout', 'Refund', 'Void', 'PostCharge')"),
          col("external_id", "text", null=True),
          col("amount_minor", "bigint", null=True),
          CURRENCY(null=True),
          col("idempotency_key", "text"),
          col("last_error", "text", null=True),
      ],
      uniques=[("idempotency_uq", "tenant_id, idempotency_key")],
      dropped=[("amount numeric/currency", "amount_minor/currency_code")])

table(S, "visit_charge_reference", CHILD, PROPERTY, handoff="kept (moved from visit: visit is below commerce)",
      statuses=["Pending", "Posted", "Reconciled", "Failed"],
      cols=[
          col("visit_id", "uuid", fk="visit.visit", same_property=True),
          col("commerce_order_id", "uuid", null=True, fk="commerce.commerce_order", same_property=True),
          col("payment_transaction_id", "uuid", null=True, fk="commerce.payment_transaction", same_property=True),
          col("charge_role", "text", check="charge_role IN ('RoomCharge', 'Folio', 'Direct', 'MemberAccount')"),
          col("external_folio_reference", "text", null=True),
          col("amount_minor", "bigint"),
          CURRENCY(),
          col("reconciled_at", "timestamptz", null=True),
      ],
      checks=[("reconciled_complete", "(status = 'Reconciled') = (reconciled_at IS NOT NULL)")],
      dropped=[("amount numeric(20,6)/currency", "amount_minor/currency_code")])
