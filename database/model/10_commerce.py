"""commerce: orders, provider-agnostic payments, Marquee delegation.

A cart is a Draft order. A deposit, a payment, a no-show fee and a refund are all payment intents
with a purpose; what the provider actually did is a payment transaction. Settled money is integer
minor units. No card data is stored (DEC-010 provider still open).
"""
from dsl import *

S = "commerce"

table(S, "commerce_order", AGGREGATE, PROPERTY, key=None, handoff="completed (absorbs cart, receipt)",
      spec="§53.2 Commerce; DEC-001 (owner_system); DEC-002 (Marquee owns delegated commerce)",
      doc="When Marquee owns commerce for the property, owner_system = 'Marquee' and source_key is its order id.",
      statuses=["Draft", "Open", "Delegated", "PartiallyPaid", "Paid", "PartiallyRefunded", "Refunded", "Voided", "Abandoned"],
      cols=[
          col("order_number", "text", null=True),
          col("receipt_number", "text", null=True),
          col("guest_id", "uuid", null=True, fk="guest.guest"),
          col("visit_id", "uuid", null=True, fk="scheduling.visit", same_property=True),
          col("owner_system", "text", default="'Spa'", check="owner_system IN ('Spa', 'Marquee', 'Pos')"),
          col("ordered_at", "timestamptz", null=True),
          CURRENCY(),
          col("subtotal_minor", "bigint", default="0"),
          col("discount_total_minor", "bigint", default="0", check="discount_total_minor >= 0"),
          col("tax_total_minor", "bigint", default="0", check="tax_total_minor >= 0"),
          col("tip_total_minor", "bigint", default="0", check="tip_total_minor >= 0"),
          col("total_minor", "bigint", default="0"),
          col("receipt_issued_at", "timestamptz", null=True),
          col("expires_at", "timestamptz", null=True, doc="a Draft (cart) expires"),
      ],
      checks=[("delegated_is_external", "status <> 'Delegated' OR owner_system <> 'Spa'"),
              ("receipt_complete", "(receipt_number IS NULL) = (receipt_issued_at IS NULL)")],
      indexes=["UNIQUE commerce_order_number_uq ON commerce.commerce_order (tenant_id, property_id, order_number) WHERE order_number IS NOT NULL",
               "UNIQUE commerce_order_receipt_uq ON commerce.commerce_order (tenant_id, property_id, receipt_number) WHERE receipt_number IS NOT NULL"],
      dropped=[("cart, cart_line (tables)", "status 'Draft'"), ("receipt (table)", "receipt_number/receipt_issued_at"),
               ("appointment_id", "order_line.appointment_id (an order can span several appointments)"),
               ("amount/subtotal/tax_total/grand_total numeric(22,6)", "*_minor bigint")])

table(S, "order_line", CHILD, PROPERTY, handoff="completed",
      cols=[
          col("commerce_order_id", "uuid", fk="commerce.commerce_order", same_property=True),
          col("line_number", "smallint", check="line_number >= 1"),
          col("line_kind", "text", check="line_kind IN ('Service', 'Option', 'Retail', 'Fee', 'Deposit', 'Discount', 'Tip')"),
          col("appointment_id", "uuid", null=True, fk="scheduling.appointment", same_property=True),
          col("service_id", "uuid", null=True, fk="catalog.service"),
          col("option_code", "text", null=True),
          col("inventory_item_variant_id", "uuid", null=True, fk="inventory.inventory_item_variant"),
          col("location_id", "uuid", null=True, fk="resources.location", same_property=True),
          col("staff_id", "uuid", null=True, doc="provider credited (commission); workforce is not a commerce dependency"),
          col("original_order_line_id", "uuid", null=True, fk="commerce.order_line.order_line_id", doc="a return"),
          col("description", "text"),
          col("quantity", QTY, check="quantity <> 0"),
          col("unit_price_minor", "bigint"),
          col("discount_minor", "bigint", default="0"),
          col("tax_minor", "bigint", default="0"),
          col("tip_minor", "bigint", default="0"),
          col("net_minor", "bigint"),
          col("tax_code", "text", null=True),
          col("revenue_center_code", "text", null=True),
          col("fulfilled_at", "timestamptz", null=True),
      ],
      checks=[("retail_has_variant", "line_kind <> 'Retail' OR inventory_item_variant_id IS NOT NULL"),
              ("service_has_service", "line_kind NOT IN ('Service', 'Option') OR service_id IS NOT NULL")],
      uniques=[("number_uq", "tenant_id, commerce_order_id, line_number")],
      dropped=[("commerce_catalog_item_id", "service_id or inventory_item_variant_id"),
               ("quantity_sold", "quantity")],
      extra_sql="""
-- Lines of a Draft order (a cart) may be removed; once the order is placed its lines are evidence.
CREATE FUNCTION commerce.order_line_delete_only_draft() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM commerce.commerce_order o
                    WHERE o.tenant_id = OLD.tenant_id AND o.commerce_order_id = OLD.commerce_order_id
                      AND o.status = 'Draft') THEN
        RAISE EXCEPTION 'order_line can be deleted only while its order is Draft' USING ERRCODE = '42501';
    END IF;
    RETURN OLD;
END $$;
CREATE TRIGGER order_line_delete_only_draft BEFORE DELETE ON commerce.order_line
    FOR EACH ROW EXECUTE FUNCTION commerce.order_line_delete_only_draft();
""")

table(S, "payment_intent", AGGREGATE, PROPERTY, key=None, source_cols=False,
      handoff="completed (absorbs refund, deposit_ledger_entry)",
      spec="§53.2 Commerce (standalone deposit/payment/refund); commerce:refund:approved; SEC-014",
      doc="A request to move money, in either direction. Refunds need an approver other than the requester.",
      statuses=["Requested", "Approved", "RequiresPaymentMethod", "RequiresAction", "Authorized", "Captured",
                "Succeeded", "Cancelled", "Failed", "Expired", "Rejected"],
      cols=[
          col("purpose", "text", check="purpose IN ('Deposit', 'Payment', 'NoShowFee', 'CancellationFee', 'Refund', "
                                       "'DepositApplied', 'DepositForfeited')"),
          col("commerce_order_id", "uuid", null=True, fk="commerce.commerce_order", same_property=True),
          col("appointment_id", "uuid", null=True, fk="scheduling.appointment", same_property=True),
          col("original_transaction_id", "uuid", null=True, fk="commerce.payment_transaction.payment_transaction_id",
              doc="the transaction a refund returns"),
          col("amount_minor", "bigint", check="amount_minor > 0"),
          CURRENCY(),
          col("provider_code", "text", doc="payment adapter, chosen when DEC-010 lands"),
          col("provider_reference", "text", null=True),
          col("capture_method", "text", default="'Automatic'", check="capture_method IN ('Automatic', 'Manual')"),
          col("reason_code", "text", null=True),
          col("approved_by", "uuid", null=True),
          col("approved_at", "timestamptz", null=True),
          col("idempotency_key", "text"),
          col("expires_at", "timestamptz", null=True),
      ],
      checks=[("has_subject", "num_nonnulls(commerce_order_id, appointment_id) >= 1"),
              ("refund_names_original", "(purpose = 'Refund') = (original_transaction_id IS NOT NULL)"),
              ("refund_approved", "purpose <> 'Refund' OR status IN ('Requested', 'Rejected', 'Cancelled') OR approved_by IS NOT NULL"),
              ("dual_control", "approved_by IS NULL OR approved_by IS DISTINCT FROM created_by")],
      uniques=[("idempotency_uq", "tenant_id, idempotency_key")],
      indexes=["UNIQUE payment_intent_provider_uq ON commerce.payment_intent (tenant_id, provider_code, provider_reference) "
               "WHERE provider_reference IS NOT NULL",
               "payment_intent_appointment_ix ON commerce.payment_intent (tenant_id, appointment_id, purpose)"],
      dropped=[("refund (table)", "purpose 'Refund' + approval columns"),
               ("deposit_ledger_entry (table)", "Deposit / DepositApplied / DepositForfeited intents and their transactions")])

table(S, "payment_transaction", LEDGER, PROPERTY, handoff="completed (absorbs visit_charge_reference)",
      spec="PAYMENT_OUTCOME_AMBIGUOUS -> outcome 'Unknown' pending reconciliation",
      doc="What the payment provider, PMS folio or cash drawer actually did. Append-only.",
      cols=[
          col("payment_intent_id", "uuid", null=True, fk="commerce.payment_intent", same_property=True),
          col("commerce_order_id", "uuid", null=True, fk="commerce.commerce_order", same_property=True),
          col("tender_code", "text", doc="code_list Tender (Card, Cash, RoomCharge, GiftCard, MemberAccount)"),
          col("transaction_type", "text", check="transaction_type IN ('Authorization', 'Capture', 'Sale', 'Void', "
                                                "'Refund', 'Chargeback', 'Adjustment', 'FolioPost')"),
          col("outcome", "text", check="outcome IN ('Approved', 'Declined', 'Error', 'Pending', 'Unknown')"),
          col("amount_minor", "bigint", check="amount_minor >= 0"),
          CURRENCY(),
          col("provider_code", "text"),
          col("provider_reference", "text", null=True),
          col("external_folio_reference", "text", null=True, doc="PMS folio for a room charge"),
          col("card_brand", "text", null=True),
          col("card_last4", "char(4)", null=True, check="card_last4 ~ '^[0-9]{4}$'"),
          col("processed_at", "timestamptz"),
          col("original_transaction_id", "uuid", null=True, fk="commerce.payment_transaction.payment_transaction_id"),
          col("idempotency_key", "text"),
      ],
      checks=[("reversal_has_original", "transaction_type NOT IN ('Void', 'Refund', 'Chargeback') OR original_transaction_id IS NOT NULL"),
              ("folio_has_reference", "transaction_type <> 'FolioPost' OR external_folio_reference IS NOT NULL")],
      uniques=[("idempotency_uq", "tenant_id, idempotency_key")],
      dropped=[("tender_definition (table)", "code_list Tender"),
               ("visit_charge_reference (table)", "FolioPost transactions with external_folio_reference")])

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
      uniques=[("idempotency_uq", "tenant_id, idempotency_key")])
