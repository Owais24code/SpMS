"""catalog: what can be sold and booked, versioned, with per-property offering."""
from dsl import *

S = "catalog"

table(S, "service", MASTER, TENANT, key=None, spec="§53.2 Catalog; treatment_service_master.schema.json",
      doc="Tenant-wide service master. Booking always freezes the service_version it used.",
      statuses=["Active", "Inactive", "Retired"],
      cols=[
          col("code", "text"),
          col("name", "text"),
          col("catalog_type", "text", default="'Service'", check="catalog_type IN ('Service', 'AddOn', 'Package', 'Class')"),
          col("category_code", "text", null=True),
          col("duration_minutes", "integer", check="duration_minutes > 0",
              doc="a zero duration would make end_at = start_at and disable CON-002 for the service"),
          col("pre_buffer_minutes", "integer", default="0", check="pre_buffer_minutes >= 0"),
          col("post_buffer_minutes", "integer", default="0", check="post_buffer_minutes >= 0"),
          col("base_price_minor", "bigint", default="0", check="base_price_minor >= 0"),
          CURRENCY(),
          col("requires_intake", "boolean", default="true"),
          col("minimum_guest_age", "smallint", null=True, check="minimum_guest_age BETWEEN 0 AND 120"),
          col("tax_code", "text", null=True),
          col("revenue_center_code", "text", null=True),
      ],
      uniques=[("code_uq", "tenant_id, code")],
      dropped=[("active", "status carries the lifecycle")])

table(S, "service_version", MASTER, TENANT, key=None, handoff="completed",
      spec="treatment_service_master.schema.json; DEC-005",
      doc="Published, effective-dated snapshot of a service. Appointments reference the version booked.",
      statuses=["Draft", "Published", "Retired"],
      cols=[
          col("service_id", "uuid", fk="catalog.service"),
          col("version_number", "integer", check="version_number >= 1"),
          col("service_name", "text"),
          col("internal_name", "text", null=True),
          col("short_description", "text", null=True),
          col("full_description", "text", null=True),
          col("duration_minutes", "integer", check="duration_minutes > 0"),
          col("minimum_duration_minutes", "integer", null=True),
          col("maximum_duration_minutes", "integer", null=True),
          col("pre_buffer_minutes", "integer", default="0", check="pre_buffer_minutes >= 0"),
          col("post_buffer_minutes", "integer", default="0", check="post_buffer_minutes >= 0"),
          col("price_minor", "bigint", check="price_minor >= 0"),
          CURRENCY(),
          col("capacity_minimum", "smallint", default="1", check="capacity_minimum >= 1"),
          col("capacity_maximum", "smallint", default="1"),
          col("online_bookable", "boolean", default="false"),
          col("deposit_required", "boolean", default="false"),
          col("cancellation_policy_code", "text", null=True),
          col("late_arrival_policy_code", "text", null=True),
          col("intake_requirement_codes", "text[]", default="'{}'"),
          col("consent_requirement_codes", "text[]", default="'{}'"),
          col("minimum_guest_age", "smallint", null=True),
          col("maximum_guest_age", "smallint", null=True),
          col("published_at", "timestamptz", null=True),
          col("published_by", "uuid", null=True),
      ],
      checks=[("capacity_range", "capacity_maximum >= capacity_minimum"),
              ("duration_range", "(minimum_duration_minutes IS NULL OR minimum_duration_minutes <= duration_minutes) "
                                 "AND (maximum_duration_minutes IS NULL OR maximum_duration_minutes >= duration_minutes)"),
              ("age_range", "maximum_guest_age IS NULL OR minimum_guest_age IS NULL OR maximum_guest_age >= minimum_guest_age"),
              ("published_complete", "status <> 'Published' OR (published_at IS NOT NULL AND published_by IS NOT NULL)")],
      uniques=[("number_uq", "tenant_id, service_id, version_number")],
      dropped=[("definition_json", "typed columns"), ("publication_status", "status"),
               ("marketing_content/image_uri/display_order", "presentation, not booking truth; CMS concern"),
               ("room_type_code/equipment_requirements", "catalog.service_resource_requirement")],
      extra_sql="""
ALTER TABLE catalog.service_version ADD CONSTRAINT service_version_single_published
    EXCLUDE USING gist (tenant_id WITH =, service_id WITH =,
                        tstzrange(effective_from, coalesce(effective_to, 'infinity'), '[)') WITH &&)
    WHERE (status = 'Published');
""")

table(S, "service_resource_requirement", MASTER, TENANT, key=None, handoff="new",
      spec="§53.2 rooms/resources; replaces service_version.room_type_code/equipment_requirements",
      statuses=["Active", "Retired"],
      cols=[
          col("service_id", "uuid", fk="catalog.service"),
          col("resource_type", "text", check="resource_type IN ('TreatmentRoom', 'WetRoom', 'CoupleRoom', 'Chair', 'Equipment')"),
          col("capability_code", "text", null=True),
          col("quantity", "smallint", default="1", check="quantity >= 1"),
      ])

table(S, "property_service", MASTER, PROPERTY, key=None, handoff="new",
      spec="§53.2 Operating modes (per-property catalog authority); buffer overrides",
      doc="Which services a property offers, with its own price and buffer overrides.",
      statuses=["Active", "Inactive"],
      cols=[
          col("service_id", "uuid", fk="catalog.service"),
          col("price_minor", "bigint", null=True, check="price_minor >= 0"),
          CURRENCY(null=True),
          col("online_bookable", "boolean", default="false"),
          col("room_turnover_minutes", "integer", null=True, check="room_turnover_minutes >= 0"),
          col("provider_transition_minutes", "integer", null=True, check="provider_transition_minutes >= 0"),
      ],
      checks=[("price_has_currency", "(price_minor IS NULL) = (currency_code IS NULL)")],
      uniques=[("service_uq", "tenant_id, property_id, service_id")])

table(S, "service_option_rule", MASTER, TENANT, key=None, handoff="completed",
      spec="§53.2 Provider tablet add-ons; booking options",
      statuses=["Active", "Retired"],
      cols=[
          col("service_id", "uuid", fk="catalog.service"),
          col("option_code", "text"),
          col("option_type", "text", check="option_type IN ('AddOn', 'Enhancement', 'DurationExtension', 'Preference')"),
          col("label", "text"),
          col("addon_service_id", "uuid", null=True, fk="catalog.service.service_id"),
          col("price_delta_minor", "bigint", default="0"),
          col("duration_delta_minutes", "integer", default="0", check="duration_delta_minutes >= 0"),
          col("is_required", "boolean", default="false"),
          col("max_quantity", "smallint", default="1", check="max_quantity >= 1"),
      ],
      uniques=[("code_uq", "tenant_id, service_id, option_code")],
      dropped=[("definition_json", "typed columns")])

table(S, "service_protocol", MASTER, TENANT, key=None, handoff="completed",
      spec="§Provider tablet; treatment protocol",
      statuses=["Draft", "Active", "Retired"],
      cols=[
          col("service_id", "uuid", fk="catalog.service"),
          col("protocol_code", "text"),
          col("title", "text"),
          col("steps_json", "jsonb", default="'[]'"),
          col("contraindication_profile_codes", "text[]", default="'{}'"),
      ],
      uniques=[("code_uq", "tenant_id, service_id, protocol_code")],
      dropped=[("definition_json", "steps_json + typed columns")])

table(S, "commerce_catalog_item", MASTER, TENANT, key=None, handoff="completed",
      spec="§Spa item catalog and import; §Item identifiers",
      statuses=["Active", "Inactive", "Retired"],
      cols=[
          col("sku", "text"),
          col("item_type", "text", check="item_type IN ('Service', 'Retail', 'Package', 'Fee', 'Deposit', 'GiftCard')"),
          col("display_name", "text"),
          col("service_id", "uuid", null=True, fk="catalog.service"),
          col("tax_code", "text", null=True),
          col("revenue_center_code", "text", null=True),
      ],
      checks=[("service_items_link", "(item_type = 'Service') = (service_id IS NOT NULL)")],
      uniques=[("sku_uq", "tenant_id, sku")])

table(S, "price_rule", MASTER, TENANT_OPT, key=None, handoff="completed", spec="§Numbers and six decimal places",
      statuses=["Active", "Retired"],
      cols=[
          col("commerce_catalog_item_id", "uuid", fk="catalog.commerce_catalog_item"),
          col("price_minor", "bigint", check="price_minor >= 0"),
          CURRENCY(),
          col("priority", "integer", default="100"),
          col("channel", "text", null=True, check="channel IN ('Desk', 'Online', 'Mobile', 'Marquee')"),
          col("guest_segment", "text", null=True, check="guest_segment IN ('Member', 'HotelGuest', 'DayGuest', 'Local')"),
          col("day_of_week_mask", "smallint", null=True, check="day_of_week_mask BETWEEN 1 AND 127"),
          col("time_from", "time", null=True),
          col("time_to", "time", null=True),
      ],
      checks=[("time_window", "(time_from IS NULL) = (time_to IS NULL) AND (time_to IS NULL OR time_to > time_from)")],
      dropped=[("price_amount numeric(22,6)", "a sell price is a settled amount: integer minor units"),
               ("definition_json", "typed columns")],
      indexes=["price_rule_item_ix ON catalog.price_rule (tenant_id, commerce_catalog_item_id, priority) WHERE status = 'Active'"])

table(S, "tax_rule", MASTER, TENANT_OPT, key=None, handoff="completed",
      statuses=["Active", "Retired"],
      cols=[
          col("tax_code", "text"),
          col("jurisdiction", "text"),
          col("rate", RATE, check="rate >= 0 AND rate < 1"),
          col("inclusive", "boolean", default="false"),
          col("compound", "boolean", default="false"),
      ],
      uniques=[("code_uq", "NULLS NOT DISTINCT (tenant_id, property_id, tax_code, effective_from)")],
      dropped=[("definition_json", "typed columns")])

table(S, "revenue_center_ref", MASTER, TENANT_OPT, key=None, handoff="completed",
      spec="§Income COGS and inventory accounts",
      statuses=["Active", "Retired"],
      cols=[
          col("revenue_center_code", "text"),
          col("name", "text"),
          col("department_id", "uuid", null=True, fk="core.department"),
          col("external_account_code", "text", null=True),
      ],
      uniques=[("code_uq", "NULLS NOT DISTINCT (tenant_id, property_id, revenue_center_code)")])
