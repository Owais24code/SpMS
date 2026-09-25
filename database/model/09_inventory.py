"""inventory (R1 operational): items, lots, balances, the stock ledger, linen/laundry, recipes, forecast."""
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
          col("tracking_method", "text", default="'Quantity'", check="tracking_method IN ('Quantity', 'Lot', 'Serial')"),
          col("base_uom", "text", default="'ea'"),
          col("description", "text", null=True),
          col("brand_name", "text", null=True),
          col("category_code", "text", null=True),
          col("reorder_point", QTY, default="0", check="reorder_point >= 0"),
          col("maximum_stock", QTY, null=True),
          col("lead_time_days", "integer", default="0", check="lead_time_days >= 0"),
          col("usage_notes", "text", null=True),
      ],
      uniques=[("code_uq", "tenant_id, item_code")],
      dropped=[("requires_lot/requires_serial", "tracking_method"), ("quantity/unit_of_measure", "balances hold quantity; base_uom")])

table(S, "inventory_item_variant", MASTER, TENANT, key=None, handoff="completed",
      statuses=["Active", "Inactive", "Retired"],
      cols=[
          col("inventory_item_id", "uuid", fk="inventory.inventory_item"),
          col("variant_code", "text"),
          col("barcode", "text", null=True, doc="GTIN/UPC as text; leading zeros are significant"),
          col("size_code", "text", null=True),
          col("color_code", "text", null=True),
          col("base_uom", "text", default="'ea'"),
          col("net_content", QTY, null=True),
          col("net_content_uom", "text", null=True),
          col("manufacturer_item_number", "text", null=True),
          col("commerce_catalog_item_id", "uuid", null=True, fk="catalog.commerce_catalog_item"),
          col("inventory_enabled", "boolean", default="true"),
      ],
      uniques=[("code_uq", "tenant_id, variant_code")],
      indexes=["UNIQUE inventory_item_variant_barcode_uq ON inventory.inventory_item_variant (tenant_id, barcode) WHERE barcode IS NOT NULL"],
      dropped=[("sell_price_minor", "catalog.price_rule (price has one owner)"), ("unit_of_measure_code", "base_uom"),
               ("accounting_profile_id", "finance close is out of R1")])

table(S, "product_lot", AGGREGATE, TENANT, key=None, source_cols=False, handoff="kept",
      spec="§Legacy opening and serialized items; recall readiness",
      statuses=["Active", "Expired", "Recalled", "Consumed"],
      cols=[
          col("inventory_item_variant_id", "uuid", fk="inventory.inventory_item_variant"),
          col("lot_number", "text"),
          col("expires_on", "date", null=True),
          col("received_at", "timestamptz", null=True),
      ],
      uniques=[("lot_uq", "tenant_id, inventory_item_variant_id, lot_number")])

table(S, "inventory_location_balance", CHILD, PROPERTY, handoff="completed",
      doc="Projection of the ledger, updated in the same transaction as each ledger entry.",
      cols=[
          col("inventory_item_variant_id", "uuid", fk="inventory.inventory_item_variant"),
          col("location_id", "uuid", fk="resources.location", same_property=True),
          col("product_lot_id", "uuid", null=True, fk="inventory.product_lot"),
          col("stock_state", "text", default="'Saleable'", check=f"stock_state IN {STOCK_STATES}"),
          col("on_hand", QTY, default="0"),
          col("allocated", QTY, default="0", check="allocated >= 0"),
          col("available", f"{QTY} GENERATED ALWAYS AS (on_hand - allocated) STORED"),
          col("unit_cost_minor", "numeric(24,6)", default="0", check="unit_cost_minor >= 0"),
      ],
      uniques=[("grain_uq", "NULLS NOT DISTINCT (tenant_id, property_id, inventory_item_variant_id, location_id, product_lot_id, stock_state)")],
      dropped=[("quantity/unit_of_measure", "on_hand/allocated/available"), ("typed_version", "not needed")])

table(S, "inventory_ledger_entry", LEDGER, PROPERTY, pk="entry_id", time_col="occurred_at", partition_by="occurred_at",
      spec="§Stock grain and posting; §Numbers and six decimal places",
      doc="Signed stock movements. Never updated: a correction is a reversing entry (original_entry_id).",
      cols=[
          col("inventory_item_variant_id", "uuid", fk="inventory.inventory_item_variant"),
          col("location_id", "uuid", fk="resources.location", same_property=True),
          col("product_lot_id", "uuid", null=True, fk="inventory.product_lot"),
          col("stock_state", "text", check=f"stock_state IN {STOCK_STATES}"),
          col("movement_type", "text", check="movement_type IN ('Receipt', 'Issue', 'Sale', 'Return', 'Adjustment', "
                                             "'TransferOut', 'TransferIn', 'LaundryOut', 'LaundryIn', 'Consumption', "
                                             "'CountVariance', 'StateChange', 'Reversal')"),
          col("quantity", QTY, check="quantity <> 0"),
          col("base_uom", "text"),
          col("unit_cost_minor", "numeric(24,6)", null=True),
          col("value_delta_minor", "numeric(24,6)", null=True),
          col("reason_code", "text", null=True),
          col("original_entry_id", "uuid", null=True, doc="reversal target (no FK: partitioned)"),
          col("source_document_type", "text", null=True),
          col("source_document_id", "uuid", null=True),
          col("owner_system", "text", default="'Spa'", check="owner_system IN ('Spa', 'Marquee', 'Pos', 'External')"),
          col("idempotency_key", "text"),
      ],
      checks=[("reversal_has_target", "(movement_type = 'Reversal') = (original_entry_id IS NOT NULL)")],
      indexes=["inventory_ledger_entry_grain_ix ON inventory.inventory_ledger_entry "
               "(tenant_id, property_id, inventory_item_variant_id, location_id, occurred_at)",
               "inventory_ledger_entry_source_ix ON inventory.inventory_ledger_entry (tenant_id, source_document_type, source_document_id)"],
      dropped=[("item_reference", "inventory_item_variant_id"), ("balance_after", "the balance projection"),
               ("transaction_line_id/appointment_id/recipe_version_id/command_id", "source_document_type + source_document_id"),
               ("actor_id", "created_by"), ("typed_version/theoretical_quantity/waste_in_actual", "recipe variance is R2")])

table(S, "inventory_transfer", AGGREGATE, PROPERTY, key=None, source_cols=False, handoff="completed",
      statuses=["Draft", "Shipped", "Received", "Cancelled"],
      cols=[
          col("inventory_item_variant_id", "uuid", fk="inventory.inventory_item_variant"),
          col("product_lot_id", "uuid", null=True, fk="inventory.product_lot"),
          col("from_location_id", "uuid", fk="resources.location", same_property=True),
          col("to_location_id", "uuid", fk="resources.location", same_property=True),
          col("from_state", "text", default="'Saleable'", check=f"from_state IN {STOCK_STATES}"),
          col("to_state", "text", default="'Saleable'", check=f"to_state IN {STOCK_STATES}"),
          col("quantity", QTY, check="quantity > 0", doc="requested"),
          col("shipped_quantity", QTY, default="0"),
          col("received_quantity", QTY, default="0"),
          col("damaged_quantity", QTY, default="0"),
          col("shipped_at", "timestamptz", null=True),
          col("received_at", "timestamptz", null=True),
          col("approved_by", "uuid", null=True),
      ],
      checks=[("distinct_ends", "from_location_id <> to_location_id OR from_state <> to_state"),
              ("received_within_shipped", "received_quantity + damaged_quantity <= shipped_quantity")],
      dropped=[("transfer_state", "status"), ("unit_of_measure", "the variant's base_uom"), ("typed_version", "not needed")])

table(S, "laundry_batch", AGGREGATE, PROPERTY, key=None, source_cols=False, handoff="completed",
      spec="§53.2 Inventory: towels, robes, linens - wash actions",
      statuses=["Draft", "Dispatched", "PartiallyReceived", "Received", "Closed"],
      cols=[
          col("dispatch_location_id", "uuid", fk="resources.location", same_property=True),
          col("return_location_id", "uuid", fk="resources.location", same_property=True),
          col("dispatched_at", "timestamptz", null=True),
          col("expected_return_at", "timestamptz", null=True),
          col("received_at", "timestamptz", null=True),
          col("supplier_reference", "text", null=True),
      ],
      dropped=[("batch_state", "status")])

table(S, "laundry_batch_line", CHILD, PROPERTY,
      cols=[
          col("laundry_batch_id", "uuid", fk="inventory.laundry_batch", same_property=True),
          col("inventory_item_variant_id", "uuid", fk="inventory.inventory_item_variant"),
          col("product_lot_id", "uuid", null=True, fk="inventory.product_lot"),
          col("dispatched_quantity", QTY, check="dispatched_quantity > 0"),
          col("received_quantity", QTY, default="0", check="received_quantity >= 0"),
          col("lost_quantity", QTY, default="0", check="lost_quantity >= 0"),
      ],
      checks=[("accounted", "received_quantity + lost_quantity <= dispatched_quantity")],
      uniques=[("variant_uq", "NULLS NOT DISTINCT (tenant_id, laundry_batch_id, inventory_item_variant_id, product_lot_id)")])

table(S, "supply_recipe", MASTER, TENANT, key=None, handoff="completed",
      doc="What a service consumes (towels, robes, product) - the input to the booking-driven forecast.",
      statuses=["Draft", "Published", "Retired"],
      cols=[
          col("service_id", "uuid", fk="catalog.service"),
          col("recipe_code", "text"),
          col("recipe_revision", "integer", default="1", check="recipe_revision >= 1"),
          col("yield_quantity", QTY, default="1", check="yield_quantity > 0"),
          col("published_at", "timestamptz", null=True),
      ],
      uniques=[("revision_uq", "tenant_id, recipe_code, recipe_revision")])

table(S, "supply_recipe_line", CHILD, TENANT,
      cols=[
          col("supply_recipe_id", "uuid", fk="inventory.supply_recipe"),
          col("inventory_item_variant_id", "uuid", fk="inventory.inventory_item_variant"),
          col("base_quantity", QTY, check="base_quantity > 0"),
          col("waste_percent", RATE, default="0", check="waste_percent >= 0 AND waste_percent < 1"),
          col("is_returnable", "boolean", default="false", doc="linen returns to laundry rather than being consumed"),
          col("substitute_variant_id", "uuid", null=True, fk="inventory.inventory_item_variant.inventory_item_variant_id"),
      ],
      uniques=[("variant_uq", "tenant_id, supply_recipe_id, inventory_item_variant_id")],
      dropped=[("quantity/unit_of_measure", "base_quantity in the variant's base_uom")])

table(S, "demand_forecast", AGGREGATE, PROPERTY, key=None, source_cols=False, handoff="completed",
      statuses=["Draft", "Published", "Superseded"],
      cols=[
          col("forecast_at", "timestamptz", default="now()"),
          col("horizon_start", "timestamptz"),
          col("horizon_end", "timestamptz"),
          col("model_version", "text"),
          col("input_watermark", "timestamptz"),
          col("assumptions", "jsonb", default="'{}'"),
      ],
      checks=[("horizon_forward", "horizon_end > horizon_start")])

table(S, "forecast_line", CHILD, PROPERTY,
      cols=[
          col("demand_forecast_id", "uuid", fk="inventory.demand_forecast", same_property=True),
          col("inventory_item_variant_id", "uuid", fk="inventory.inventory_item_variant"),
          col("location_id", "uuid", fk="resources.location", same_property=True),
          col("bucket_start", "timestamptz"),
          col("bucket_end", "timestamptz"),
          col("gross_clean", QTY), col("reserved", QTY), col("gross_demand", QTY),
          col("expected_receipts", QTY), col("expected_laundry", QTY), col("safety_stock", QTY),
          col("projected_balance", QTY),
          col("confidence", "numeric(7,6)", check="confidence BETWEEN 0 AND 1"),
      ],
      checks=[("bucket_forward", "bucket_end > bucket_start")],
      uniques=[("bucket_uq", "tenant_id, demand_forecast_id, inventory_item_variant_id, location_id, bucket_start")])

table(S, "stock_count", AGGREGATE, PROPERTY, key=None, source_cols=False, handoff="completed",
      statuses=["Open", "Counted", "Recounted", "Approved", "Posted", "Cancelled"],
      cols=[
          col("inventory_item_variant_id", "uuid", fk="inventory.inventory_item_variant"),
          col("location_id", "uuid", fk="resources.location", same_property=True),
          col("product_lot_id", "uuid", null=True, fk="inventory.product_lot"),
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
          col("posted_entry_id", "uuid", null=True, doc="the CountVariance ledger entry"),
      ],
      checks=[("approver_not_counter", "approved_by IS NULL OR approved_by IS DISTINCT FROM counted_by")],
      dropped=[("inventory_count_session_id", "count sessions are R2"), ("first_observed_quantity/first_counted_by/recount_history",
                                                                        "audit_event holds the history"),
               ("quantity/unit_of_measure", "expected/observed/recount quantities")])

table(S, "service_product_use", LEDGER, PROPERTY, handoff="completed",
      spec="provider tablet: products used in a treatment",
      cols=[
          col("appointment_id", "uuid", fk="scheduling.appointment", same_property=True),
          col("inventory_item_variant_id", "uuid", fk="inventory.inventory_item_variant"),
          col("product_lot_id", "uuid", null=True, fk="inventory.product_lot"),
          col("quantity", QTY, check="quantity > 0"),
          col("used_by", "uuid", doc="principal of the provider"),
          col("ledger_entry_id", "uuid", null=True, doc="the Consumption ledger entry (no FK: partitioned)"),
      ],
      dropped=[("effective_from/effective_to", "a use is a moment: created_at")])
