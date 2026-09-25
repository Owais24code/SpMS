"""inventory (R1 operational): items, variants, balances, the stock ledger, laundry, service supplies, counts.

Every movement is a ledger entry: receipts, sales, transfers, laundry out/in, consumption in a
treatment, count variances. Documents that group movements (a laundry batch, a count) keep only
their own lifecycle; the quantities live in the ledger.
"""
from dsl import *

S = "inventory"

STOCK_STATES = "('Saleable', 'Clean', 'Soiled', 'InLaundry', 'Damaged', 'Quarantine')"

table(S, "inventory_item", MASTER, TENANT, key=None, handoff="completed",
      spec="§Inventory operating and data guide; §53.2 Inventory - operational",
      statuses=["Active", "Inactive", "Retired"],
      cols=[
          col("item_code", "text"),
          col("item_name", "text"),
          col("item_kind", "text", check="item_kind IN ('Retail', 'Linen', 'Amenity', 'Consumable', 'Professional')"),
          col("tracking_method", "text", default="'Quantity'", check="tracking_method IN ('Quantity', 'Lot')"),
          col("base_uom", "text", default="'ea'"),
          col("category_code", "text", null=True, doc="code_list ItemCategory"),
          col("brand_name", "text", null=True),
          col("reorder_point", QTY, default="0", check="reorder_point >= 0"),
          col("maximum_stock", QTY, null=True),
          col("lead_time_days", "integer", default="0", check="lead_time_days >= 0"),
      ],
      uniques=[("code_uq", "tenant_id, item_code")],
      dropped=[("requires_lot/requires_serial", "tracking_method")])

table(S, "inventory_item_variant", MASTER, TENANT, key=None, handoff="completed",
      doc="The stock-keeping and selling unit. A retail sale references the variant and its price.",
      statuses=["Active", "Inactive", "Retired"],
      cols=[
          col("inventory_item_id", "uuid", fk="inventory.inventory_item"),
          col("variant_code", "text"),
          col("barcode", "text", null=True, doc="GTIN/UPC as text; leading zeros are significant"),
          col("size_code", "text", null=True),
          col("color_code", "text", null=True),
          col("base_uom", "text", default="'ea'"),
          col("sell_price_minor", "bigint", null=True, check="sell_price_minor >= 0", doc="NULL = not sold"),
          col("currency_code", "char(3)", null=True, check="currency_code ~ '^[A-Z]{3}$'"),
          col("tax_code", "text", null=True),
          col("inventory_enabled", "boolean", default="true"),
      ],
      checks=[("price_has_currency", "(sell_price_minor IS NULL) = (currency_code IS NULL)")],
      uniques=[("code_uq", "tenant_id, variant_code")],
      indexes=["UNIQUE inventory_item_variant_barcode_uq ON inventory.inventory_item_variant (tenant_id, barcode) WHERE barcode IS NOT NULL"],
      dropped=[("product_lot (table)", "lot_number text on balance and ledger")])

table(S, "inventory_location_balance", CHILD, PROPERTY, handoff="completed",
      doc="Projection of the ledger, updated in the same transaction as each ledger entry.",
      cols=[
          col("inventory_item_variant_id", "uuid", fk="inventory.inventory_item_variant"),
          col("location_id", "uuid", fk="resources.location", same_property=True),
          col("lot_number", "text", null=True),
          col("stock_state", "text", default="'Saleable'", check=f"stock_state IN {STOCK_STATES}"),
          col("on_hand", QTY, default="0"),
          col("allocated", QTY, default="0", check="allocated >= 0"),
          col("available", f"{QTY} GENERATED ALWAYS AS (on_hand - allocated) STORED"),
          col("unit_cost_minor", "numeric(24,6)", default="0", check="unit_cost_minor >= 0"),
      ],
      uniques=[("grain_uq", "NULLS NOT DISTINCT (tenant_id, property_id, inventory_item_variant_id, location_id, lot_number, stock_state)")])

table(S, "inventory_ledger_entry", LEDGER, PROPERTY, pk="entry_id", time_col="occurred_at", partition_by="occurred_at",
      handoff="kept (absorbs inventory_transfer, laundry_batch_line, service_product_use)",
      spec="§Stock grain and posting; §Numbers and six decimal places",
      doc="Signed stock movements. Never updated: a correction is a reversing entry (original_entry_id).",
      cols=[
          col("inventory_item_variant_id", "uuid", fk="inventory.inventory_item_variant"),
          col("location_id", "uuid", fk="resources.location", same_property=True),
          col("lot_number", "text", null=True),
          col("stock_state", "text", check=f"stock_state IN {STOCK_STATES}"),
          col("movement_type", "text", check="movement_type IN ('Receipt', 'Issue', 'Sale', 'Return', 'Adjustment', "
                                             "'TransferOut', 'TransferIn', 'LaundryOut', 'LaundryIn', 'LaundryLoss', "
                                             "'Consumption', 'CountVariance', 'StateChange', 'Reversal')"),
          col("quantity", QTY, check="quantity <> 0"),
          col("unit_cost_minor", "numeric(24,6)", null=True),
          col("value_delta_minor", "numeric(24,6)", null=True),
          col("reason_code", "text", null=True),
          col("original_entry_id", "uuid", null=True, doc="reversal target (no FK: partitioned)"),
          col("source_document_type", "text", null=True,
              check="source_document_type IN ('Appointment', 'OrderLine', 'LaundryBatch', 'StockCount', 'Transfer', 'Receipt')"),
          col("source_document_id", "uuid", null=True),
          col("owner_system", "text", default="'Spa'", check="owner_system IN ('Spa', 'Marquee', 'Pos', 'External')"),
          col("idempotency_key", "text"),
      ],
      checks=[("reversal_has_target", "(movement_type = 'Reversal') = (original_entry_id IS NOT NULL)"),
              ("source_pair", "(source_document_type IS NULL) = (source_document_id IS NULL)")],
      indexes=["inventory_ledger_entry_grain_ix ON inventory.inventory_ledger_entry "
               "(tenant_id, property_id, inventory_item_variant_id, location_id, occurred_at)",
               "inventory_ledger_entry_source_ix ON inventory.inventory_ledger_entry (tenant_id, source_document_type, source_document_id)"],
      dropped=[("inventory_transfer (table)", "TransferOut/TransferIn pair sharing source_document_id"),
               ("laundry_batch_line (table)", "LaundryOut/LaundryIn/LaundryLoss entries of the batch"),
               ("service_product_use (table)", "Consumption entries with source Appointment"),
               ("item_reference/balance_after/typed_version", "variant FK / the balance projection / not needed")])

table(S, "laundry_batch", AGGREGATE, PROPERTY, key=None, source_cols=False, handoff="completed",
      spec="§53.2 Inventory: towels, robes, linens - wash actions",
      doc="The batch's lifecycle only; what went out and came back are its ledger entries.",
      statuses=["Draft", "Dispatched", "PartiallyReceived", "Received", "Closed"],
      cols=[
          col("dispatch_location_id", "uuid", fk="resources.location", same_property=True),
          col("return_location_id", "uuid", fk="resources.location", same_property=True),
          col("dispatched_at", "timestamptz", null=True),
          col("expected_return_at", "timestamptz", null=True),
          col("received_at", "timestamptz", null=True),
          col("supplier_reference", "text", null=True),
      ])

table(S, "service_supply", MASTER, TENANT, key=None, handoff="new (replaces supply_recipe + supply_recipe_line)",
      doc="What one appointment of a service consumes or cycles (towels, robes, product) - the booking-driven "
          "forecast is computed from this and the schedule, not stored.",
      statuses=["Active", "Retired"],
      cols=[
          col("service_id", "uuid", fk="catalog.service"),
          col("inventory_item_variant_id", "uuid", fk="inventory.inventory_item_variant"),
          col("quantity", QTY, check="quantity > 0"),
          col("waste_percent", RATE, default="0", check="waste_percent >= 0 AND waste_percent < 1"),
          col("is_returnable", "boolean", default="false", doc="linen returns to laundry rather than being consumed"),
      ],
      uniques=[("variant_uq", "tenant_id, service_id, inventory_item_variant_id, effective_from")],
      dropped=[("demand_forecast, forecast_line (tables)", "computed on demand from service_supply and appointments")])

table(S, "stock_count", AGGREGATE, PROPERTY, key=None, source_cols=False, handoff="completed",
      doc="Kept typed: a count has an approval step (counter != approver) before its variance posts.",
      statuses=["Open", "Counted", "Recounted", "Approved", "Posted", "Cancelled"],
      cols=[
          col("inventory_item_variant_id", "uuid", fk="inventory.inventory_item_variant"),
          col("location_id", "uuid", fk="resources.location", same_property=True),
          col("lot_number", "text", null=True),
          col("stock_state", "text", default="'Saleable'", check=f"stock_state IN {STOCK_STATES}"),
          col("expected_quantity", QTY),
          col("observed_quantity", QTY, null=True),
          col("recount_quantity", QTY, null=True),
          col("variance_quantity", f"{QTY} GENERATED ALWAYS AS (coalesce(recount_quantity, observed_quantity) - expected_quantity) STORED"),
          col("counted_at", "timestamptz", null=True),
          col("counted_by", "uuid", null=True),
          col("approved_by", "uuid", null=True),
          col("approved_at", "timestamptz", null=True),
          col("reason_code", "text", null=True),
      ],
      checks=[("approver_not_counter", "approved_by IS NULL OR approved_by IS DISTINCT FROM counted_by")])
