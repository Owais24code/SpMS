"""catalog: bookable services, per-property offering, options and tax.

Retail items are inventory variants (they carry their own price); a sold service is the service
itself. There is no separate sellable-item or price-rule table.
"""
from dsl import *

S = "catalog"

table(S, "service", MASTER, TENANT, key=None, spec="§53.2 Catalog; treatment_service_master.schema.json",
      doc="Tenant-wide service master. Changes are audited; an appointment freezes the duration and price it booked.",
      statuses=["Draft", "Active", "Inactive", "Retired"],
      cols=[
          col("code", "text"),
          col("name", "text"),
          col("catalog_type", "text", default="'Service'", check="catalog_type IN ('Service', 'AddOn', 'Package')"),
          col("category_code", "text", null=True, doc="code_list ServiceCategory"),
          col("short_description", "text", null=True),
          col("duration_minutes", "integer", check="duration_minutes > 0",
              doc="a zero duration would make end_at = start_at and disable CON-002"),
          col("pre_buffer_minutes", "integer", default="0", check="pre_buffer_minutes >= 0"),
          col("post_buffer_minutes", "integer", default="0", check="post_buffer_minutes >= 0"),
          col("base_price_minor", "bigint", default="0", check="base_price_minor >= 0"),
          CURRENCY(),
          col("tax_code", "text", null=True),
          col("revenue_center_code", "text", null=True, doc="code_list RevenueCenter"),
          col("capacity_maximum", "smallint", default="1", check="capacity_maximum >= 1"),
          col("required_resource_type", "text", null=True,
              check="required_resource_type IN ('TreatmentRoom', 'WetRoom', 'CoupleRoom', 'Chair')"),
          col("required_capabilities", "text[]", default="'{}'"),
          col("required_license_type_codes", "text[]", default="'{}'",
              doc="CON-003: a provider needs a verified credential of each type (code_list LicenseType)"),
          col("requires_intake", "boolean", default="true"),
          col("intake_form_code", "text", null=True),
          col("deposit_required", "boolean", default="false"),
          col("online_bookable", "boolean", default="false"),
          col("minimum_guest_age", "smallint", null=True, check="minimum_guest_age BETWEEN 0 AND 120"),
      ],
      uniques=[("code_uq", "tenant_id, code")],
      dropped=[("service_version (table)", "appointment freezes duration/price; history is audit_event"),
               ("service_resource_requirement (table)", "required_resource_type/required_capabilities"),
               ("service_qualification_requirement (table)", "required_license_type_codes"),
               ("service_protocol (table)", "treatment content is CMS, not booking truth"),
               ("commerce_catalog_item, price_rule (tables)", "services and inventory variants carry their own price"),
               ("active", "status")])

table(S, "property_service", MASTER, PROPERTY, key=None, handoff="new",
      spec="§53.2 Operating modes (per-property catalog authority); buffer overrides",
      doc="Which services a property offers, with its own price and buffer overrides.",
      statuses=["Active", "Inactive"],
      cols=[
          col("service_id", "uuid", fk="catalog.service"),
          col("price_minor", "bigint", null=True, check="price_minor >= 0"),
          CURRENCY(null=True),
          col("online_bookable", "boolean", null=True),
          col("room_turnover_minutes", "integer", null=True, check="room_turnover_minutes >= 0"),
          col("provider_transition_minutes", "integer", null=True, check="provider_transition_minutes >= 0"),
      ],
      checks=[("price_has_currency", "(price_minor IS NULL) = (currency_code IS NULL)")],
      uniques=[("service_uq", "tenant_id, property_id, service_id")])

table(S, "service_option_rule", MASTER, TENANT, key=None, handoff="completed",
      spec="Provider tablet add-ons; booking options",
      statuses=["Active", "Retired"],
      cols=[
          col("service_id", "uuid", fk="catalog.service"),
          col("option_code", "text"),
          col("option_type", "text", check="option_type IN ('AddOn', 'Enhancement', 'DurationExtension', 'Preference')"),
          col("label", "text"),
          col("price_delta_minor", "bigint", default="0"),
          col("duration_delta_minutes", "integer", default="0", check="duration_delta_minutes >= 0"),
          col("max_quantity", "smallint", default="1", check="max_quantity >= 1"),
      ],
      uniques=[("code_uq", "tenant_id, service_id, option_code")],
      dropped=[("definition_json", "typed columns")])

table(S, "tax_rule", MASTER, TENANT_OPT, key=None, handoff="completed",
      doc="Kept typed: money-critical, never a code-list attribute.",
      statuses=["Active", "Retired"],
      cols=[
          col("tax_code", "text"),
          col("jurisdiction", "text"),
          col("rate", RATE, check="rate >= 0 AND rate < 1"),
          col("inclusive", "boolean", default="false"),
          col("compound", "boolean", default="false"),
      ],
      uniques=[("code_uq", "NULLS NOT DISTINCT (tenant_id, property_id, tax_code, effective_from)")])
