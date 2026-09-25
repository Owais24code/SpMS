"""resources: locations, rooms/equipment and their closures."""
from dsl import *

S = "resources"

table(S, "location", MASTER, PROPERTY, key=None, handoff="completed",
      spec="stock grain; rooms; lockers",
      doc="A place at the property, nestable (floor > wing > room store). The optional layout drives the floor plan.",
      statuses=["Active", "Retired"],
      cols=[
          col("parent_location_id", "uuid", null=True, fk="resources.location", same_property=True),
          col("location_code", "text"),
          col("location_name", "text"),
          col("location_type", "text", check="location_type IN ('Facility', 'Floor', 'Treatment', 'Storage', 'Laundry', "
                                             "'Retail', 'Reception', 'Locker', 'Relaxation')"),
          col("layout", "jsonb", null=True, doc="floor-plan geometry for the board; presentation only"),
      ],
      uniques=[("code_uq", "tenant_id, property_id, location_code")],
      dropped=[("facility (table)", "a location of type Facility"),
               ("resource_layout, resource_position (tables)", "location.layout jsonb")])

table(S, "resource", MASTER, PROPERTY, key=None, spec="CON-002; §53.2 rooms/resources",
      doc="A bookable room or chair. CON-002 (no double booking) is enforced on scheduling.appointment.",
      statuses=["Active", "OutOfService", "Retired"],
      cols=[
          col("resource_type", "text", check="resource_type IN ('TreatmentRoom', 'WetRoom', 'CoupleRoom', 'Chair')"),
          col("code", "text"),
          col("name", "text"),
          col("location_id", "uuid", null=True, fk="resources.location", same_property=True),
          col("capacity", "smallint", default="1", check="capacity >= 1"),
          col("capabilities", "text[]", default="'{}'"),
          col("accessible", "boolean", default="false"),
      ],
      uniques=[("code_uq", "tenant_id, property_id, code")],
      indexes=["resource_capabilities_ix ON resources.resource USING gin (capabilities)"],
      dropped=[("resource_schedule (table)", "rooms follow property opening hours; exceptions are maintenance_window")])

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
      extra_sql="""
ALTER TABLE resources.maintenance_window ADD CONSTRAINT maintenance_window_no_overlap
    EXCLUDE USING gist (tenant_id WITH =, property_id WITH =, resource_id WITH =,
                        tstzrange(starts_at, ends_at, '[)') WITH &&)
    WHERE (status IN ('Planned', 'Active'));
""")
