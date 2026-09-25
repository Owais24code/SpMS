"""resources: facilities, locations, rooms and equipment, and their availability."""
from dsl import *

S = "resources"

table(S, "facility", MASTER, PROPERTY, key=None, spec="§53.2 rooms/resources",
      statuses=["Active", "Retired"],
      cols=[col("code", "text"), col("name", "text"),
            col("facility_type", "text", check="facility_type IN ('Spa', 'Salon', 'Fitness', 'Pool', 'Thermal', 'Retail')")],
      uniques=[("code_uq", "tenant_id, property_id, code")])

table(S, "location", MASTER, PROPERTY, key=None, handoff="completed",
      spec="§Inventory operating and data guide (stock grain); lockers; rooms",
      statuses=["Active", "Retired"],
      cols=[
          col("facility_id", "uuid", null=True, fk="resources.facility", same_property=True),
          col("parent_location_id", "uuid", null=True, fk="resources.location", same_property=True),
          col("location_code", "text"),
          col("location_name", "text"),
          col("floor_code", "text", null=True),
          col("location_type", "text", check="location_type IN ('Treatment', 'Storage', 'Laundry', 'Retail', "
                                             "'Reception', 'Locker', 'Relaxation')"),
      ],
      uniques=[("code_uq", "tenant_id, property_id, location_code")])

table(S, "resource", MASTER, PROPERTY, key=None, spec="CON-002; §53.2 rooms/resources",
      doc="A bookable room or piece of equipment. CON-002 (no double booking) is enforced on its assignments.",
      statuses=["Active", "OutOfService", "Retired"],
      cols=[
          col("resource_type", "text", check="resource_type IN ('TreatmentRoom', 'WetRoom', 'CoupleRoom', 'Chair', 'Equipment')"),
          col("code", "text"),
          col("name", "text"),
          col("location_id", "uuid", null=True, fk="resources.location", same_property=True),
          col("capacity", "smallint", default="1", check="capacity >= 1",
              doc="guests one booking may seat (a couple room is 2); one booking at a time regardless"),
          col("capabilities", "text[]", default="'{}'"),
          col("accessible", "boolean", default="false"),
      ],
      uniques=[("code_uq", "tenant_id, property_id, code")],
      indexes=["resource_capabilities_ix ON resources.resource USING gin (capabilities)"],
      dropped=[("capabilities jsonb", "text[] of capability codes with a GIN index; same name")])

table(S, "resource_layout", MASTER, PROPERTY, key=None, effective=False,
      statuses=["Draft", "Active", "Retired"],
      cols=[
          col("resource_id", "uuid", fk="resources.resource", same_property=True),
          col("layout_name", "text"),
          col("layout_version", "integer", check="layout_version >= 1"),
          col("width_units", "numeric(14,6)", check="width_units > 0"),
          col("height_units", "numeric(14,6)", check="height_units > 0"),
      ],
      uniques=[("version_uq", "tenant_id, resource_id, layout_version")],
      dropped=[("state", "status")])

table(S, "resource_position", CHILD, PROPERTY,
      cols=[
          col("resource_layout_id", "uuid", fk="resources.resource_layout", same_property=True),
          col("position_code", "text"),
          col("x", "numeric(14,6)"),
          col("y", "numeric(14,6)"),
          col("accessible", "boolean", default="false"),
      ],
      uniques=[("code_uq", "tenant_id, resource_layout_id, position_code")],
      dropped=[("state/status", "a position lives and dies with its layout")])

table(S, "resource_schedule", MASTER, PROPERTY, key=None, handoff="completed",
      spec="§53.2 availability",
      doc="Weekly recurring availability of a resource. One-off closures are maintenance_window rows.",
      statuses=["Active", "Retired"],
      cols=[
          col("resource_id", "uuid", fk="resources.resource", same_property=True),
          col("day_of_week", "smallint", check="day_of_week BETWEEN 1 AND 7"),
          col("start_time", "time"),
          col("end_time", "time"),
      ],
      checks=[("time_forward", "end_time > start_time")],
      dropped=[("starts_at/ends_at", "one-off periods are maintenance_window; this entity is the weekly pattern")])

table(S, "maintenance_window", AGGREGATE, PROPERTY, key=None, handoff="completed",
      statuses=["Planned", "Active", "Completed", "Cancelled"],
      cols=[
          col("resource_id", "uuid", fk="resources.resource", same_property=True),
          col("starts_at", "timestamptz"),
          col("ends_at", "timestamptz"),
          col("reason_code", "text"),
          col("note", "text", null=True),
      ],
      checks=[("range_forward", "ends_at > starts_at")],
      dropped=[("effective_from/effective_to", "starts_at/ends_at")],
      extra_sql="""
ALTER TABLE resources.maintenance_window ADD CONSTRAINT maintenance_window_no_overlap
    EXCLUDE USING gist (tenant_id WITH =, property_id WITH =, resource_id WITH =,
                        tstzrange(starts_at, ends_at, '[)') WITH &&)
    WHERE (status IN ('Planned', 'Active'));
""")

table(S, "sanitation_record", LEDGER, PROPERTY, handoff="completed",
      spec="housekeeping room_readiness",
      cols=[
          col("resource_id", "uuid", fk="resources.resource", same_property=True),
          col("performed_at", "timestamptz", doc="when it happened; may precede created_at for offline capture"),
          col("performed_by", "uuid", doc="principal"),
          col("checklist_code", "text"),
          col("result", "text", check="result IN ('Pass', 'Fail', 'NeedsAttention')"),
          col("note", "text", null=True),
      ],
      indexes=["sanitation_record_resource_ix ON resources.sanitation_record (tenant_id, resource_id, performed_at DESC)"])
