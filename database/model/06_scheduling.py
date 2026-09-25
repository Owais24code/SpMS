"""scheduling: booking and the scheduling board (appointments, assignments, proposals, holds, waitlist)."""
from dsl import *

S = "scheduling"

APPOINTMENT_STATUSES = ["Draft", "Held", "Confirmed", "CheckedIn", "Ready", "InService",
                        "Completed", "Cancelled", "NoShow"]

table(S, "appointment", AGGREGATE, PROPERTY, key=None,
      spec="§53.2 Scheduling, Booking; DEC-002 (Spa is authoritative for appointments); DEC-005",
      doc="One booked treatment slot. Who and where live in appointment_resource_assignment; "
          "CON-002 is enforced there by an exclusion constraint.",
      statuses=APPOINTMENT_STATUSES,
      cols=[
          col("confirmation_number", "text", null=True, doc="guest-facing, quoted at the desk; unique per tenant"),
          col("guest_id", "uuid", fk="guest.guest", doc="primary guest / booking holder"),
          col("service_id", "uuid", fk="catalog.service"),
          col("service_version_id", "uuid", fk="catalog.service_version", doc="what was booked, frozen"),
          col("duration_minutes", "integer", check="duration_minutes > 0"),
          col("start_at", "timestamptz"),
          col("end_at", "timestamptz", doc="maintained by trigger from start_at + duration_minutes"),
          col("entered_timezone", "text", doc="zone the operator or guest entered the time in"),
          col("source", "text", check="source IN ('Desk', 'Online', 'Mobile', 'Phone', 'ProviderTablet', 'Marquee', 'Import')"),
          col("price_minor", "bigint", default="0", check="price_minor >= 0"),
          CURRENCY(),
          col("cancellation_reason_code", "text", null=True),
          col("cancelled_at", "timestamptz", null=True),
          col("completed_at", "timestamptz", null=True),
      ],
      checks=[("interval_forward", "end_at > start_at"),
              ("cancel_complete", "(status = 'Cancelled') = (cancelled_at IS NOT NULL)"),
              ("complete_complete", "(status = 'Completed') = (completed_at IS NOT NULL)")],
      indexes=[
          "UNIQUE appointment_confirmation_uq ON scheduling.appointment (tenant_id, confirmation_number) WHERE confirmation_number IS NOT NULL",
          "appointment_board_ix ON scheduling.appointment USING gist (tenant_id, property_id, tstzrange(start_at, end_at, '[)'))",
          "appointment_guest_window_ix ON scheduling.appointment (tenant_id, guest_id, start_at) WHERE status NOT IN ('Cancelled', 'NoShow')",
      ],
      dropped=[("record_key", "confirmation_number is the appointment's business key"),
               ("visit_id", "visit.visit_appointment (visit is a later module; no upward FK)"),
               ("settled_order_id", "commerce.commerce_order.appointment_id (no upward FK)"),
               ("redeemed_enrollment_id", "memberships/packages are R2"),
               ("appointment_assignment (table)", "appointment_resource_assignment: one assignment entity")],
      extra_sql="""
-- end_at is derived, never trusted from the caller: the exclusion constraint on
-- assignments indexes the same interval. Not a GENERATED column because
-- timestamptz + interval is STABLE, not IMMUTABLE.
CREATE FUNCTION scheduling.appointment_set_end_at() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    NEW.end_at := NEW.start_at + make_interval(mins => NEW.duration_minutes);
    RETURN NEW;
END $$;
CREATE TRIGGER appointment_end_at BEFORE INSERT OR UPDATE OF start_at, duration_minutes, end_at
    ON scheduling.appointment FOR EACH ROW EXECUTE FUNCTION scheduling.appointment_set_end_at();

-- A cancelled or no-show appointment holds nothing: release its assignments in
-- the same statement, so the room is free the instant the status changes.
CREATE FUNCTION scheduling.appointment_release_assignments() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF NEW.status IN ('Cancelled', 'NoShow') AND OLD.status NOT IN ('Cancelled', 'NoShow') THEN
        UPDATE scheduling.appointment_resource_assignment
           SET status = 'Released'
         WHERE tenant_id = NEW.tenant_id AND appointment_id = NEW.appointment_id AND status = 'Active';
    END IF;
    RETURN NEW;
END $$;
CREATE TRIGGER appointment_release_assignments AFTER UPDATE OF status ON scheduling.appointment
    FOR EACH ROW EXECUTE FUNCTION scheduling.appointment_release_assignments();
""")

table(S, "appointment_resource_assignment", CHILD, PROPERTY, handoff="kept (absorbs appointment_assignment)",
      spec="CON-001 (provider, soft) and CON-002 (room/equipment, hard); SCH reassign",
      doc="Who and what an appointment occupies, and when. Rooms and equipment cannot overlap (database-enforced). "
          "Provider overlap is soft and overridable, so it is indexed, not constrained.",
      statuses=["Active", "Released"],
      cols=[
          col("appointment_id", "uuid", fk="scheduling.appointment", same_property=True),
          col("assignment_role", "text", check="assignment_role IN ('Provider', 'Room', 'Equipment')"),
          col("staff_id", "uuid", null=True, fk="workforce.staff"),
          col("resource_id", "uuid", null=True, fk="resources.resource", same_property=True),
          col("starts_at", "timestamptz"),
          col("ends_at", "timestamptz"),
          col("guest_requested", "boolean", default="false", doc="a requested provider changes the reassign rules"),
      ],
      checks=[("range_forward", "ends_at > starts_at"),
              ("role_target", "(assignment_role = 'Provider' AND staff_id IS NOT NULL AND resource_id IS NULL) OR "
                              "(assignment_role IN ('Room', 'Equipment') AND resource_id IS NOT NULL AND staff_id IS NULL)")],
      indexes=[
          "UNIQUE appointment_resource_assignment_one_room_uq ON scheduling.appointment_resource_assignment (tenant_id, appointment_id) "
          "WHERE assignment_role = 'Room' AND status = 'Active'",
          "appointment_resource_assignment_provider_window_ix ON scheduling.appointment_resource_assignment "
          "USING gist (tenant_id, staff_id, tstzrange(starts_at, ends_at, '[)')) WHERE assignment_role = 'Provider' AND status = 'Active'",
      ],
      dropped=[("starts_at_utc/ends_at_utc", "timestamptz is already an instant"),
               ("effective_from/effective_to", "starts_at/ends_at")],
      extra_sql="""
-- CON-002, HARD: a room or piece of equipment cannot hold two bookings at once.
-- Violations raise SQLSTATE 23P01, which the adapter maps back to CON-002.
ALTER TABLE scheduling.appointment_resource_assignment ADD CONSTRAINT appointment_resource_no_overlap
    EXCLUDE USING gist (tenant_id WITH =, property_id WITH =, resource_id WITH =,
                        tstzrange(starts_at, ends_at, '[)') WITH &&)
    WHERE (resource_id IS NOT NULL AND status = 'Active');
""")

table(S, "appointment_line", CHILD, PROPERTY, handoff="completed",
      spec="§Numbers and six decimal places (line rounds quantity x integer unit price to minor units)",
      doc="Priced content of an appointment: the service and its options/add-ons. Times live on the appointment.",
      cols=[
          col("appointment_id", "uuid", fk="scheduling.appointment", same_property=True),
          col("line_number", "smallint", check="line_number >= 1"),
          col("line_type", "text", check="line_type IN ('Service', 'Option', 'AddOn', 'Fee', 'Discount')"),
          col("service_id", "uuid", null=True, fk="catalog.service"),
          col("service_option_rule_id", "uuid", null=True, fk="catalog.service_option_rule"),
          col("description", "text"),
          col("quantity", QTY, default="1", check="quantity > 0"),
          col("unit_price_minor", "bigint"),
          CURRENCY(),
          col("line_total_minor", "bigint GENERATED ALWAYS AS (round(quantity * unit_price_minor)::bigint) STORED"),
      ],
      uniques=[("number_uq", "tenant_id, appointment_id, line_number")],
      dropped=[("unit_price numeric(22,6)", "unit_price_minor"),
               ("scheduled_start/scheduled_end/starts_at/ends_at", "the appointment's own interval")])

table(S, "appointment_participant", CHILD, PROPERTY, handoff="completed", spec="couples, minors, guardians",
      cols=[
          col("appointment_id", "uuid", fk="scheduling.appointment", same_property=True),
          col("guest_id", "uuid", fk="guest.guest"),
          col("participant_role", "text", check="participant_role IN ('Primary', 'Companion', 'Minor', 'Guardian')"),
      ],
      uniques=[("guest_uq", "tenant_id, appointment_id, guest_id")],
      indexes=["UNIQUE appointment_participant_one_primary_uq ON scheduling.appointment_participant (tenant_id, appointment_id) "
               "WHERE participant_role = 'Primary'"],
      dropped=[("is_primary", "participant_role = 'Primary'"), ("starts_at/ends_at", "the appointment's")])

table(S, "appointment_status_history", LEDGER, PROPERTY, handoff="completed", spec="DEC-005 audited commands",
      cols=[
          col("appointment_id", "uuid", fk="scheduling.appointment", same_property=True),
          col("from_status", "text", null=True),
          col("to_status", "text", check="to_status IN (" + ", ".join(f"'{s}'" for s in APPOINTMENT_STATUSES) + ")"),
          col("reason_code", "text", null=True),
          col("reason_text", "text", null=True),
          col("override_conflict_codes", "text[]", default="'{}'"),
      ],
      indexes=["appointment_status_history_appt_ix ON scheduling.appointment_status_history (tenant_id, appointment_id, created_at)"])

table(S, "appointment_itinerary", AGGREGATE, PROPERTY, key=None, handoff="completed",
      doc="A guest's day: several appointments in sequence.",
      statuses=["Draft", "Confirmed", "Completed", "Cancelled"],
      cols=[
          col("guest_id", "uuid", fk="guest.guest"),
          col("itinerary_date", "date"),
          col("notes", "text", null=True),
      ],
      dropped=[("appointment_id", "appointment_itinerary_link")])

table(S, "appointment_itinerary_link", CHILD, PROPERTY, handoff="completed",
      cols=[
          col("appointment_itinerary_id", "uuid", fk="scheduling.appointment_itinerary", same_property=True),
          col("appointment_id", "uuid", fk="scheduling.appointment", same_property=True),
          col("sequence_number", "smallint", check="sequence_number >= 1"),
      ],
      uniques=[("appointment_uq", "tenant_id, appointment_id"),
               ("sequence_uq", "tenant_id, appointment_itinerary_id, sequence_number")])

table(S, "availability_hold", CHILD, PROPERTY, pk="hold_id", spec="§Booking transaction; online slot hold",
      doc="Short-lived slot token from availability search. It does not block CON-002: conversion to a Held "
          "appointment is what occupies the room.",
      statuses=["Active", "Converted", "Expired", "Released"],
      cols=[
          col("slot_token_hash", HASH),
          col("guest_id", "uuid", null=True, fk="guest.guest"),
          col("service_id", "uuid", fk="catalog.service"),
          col("start_at", "timestamptz"),
          col("end_at", "timestamptz"),
          col("resource_snapshot", "jsonb"),
          col("expires_at", "timestamptz"),
          col("idempotency_key", "text"),
          col("appointment_id", "uuid", null=True, fk="scheduling.appointment", same_property=True),
      ],
      checks=[("range_forward", "end_at > start_at"),
              ("converted_has_appointment", "(status = 'Converted') = (appointment_id IS NOT NULL)")],
      uniques=[("token_uq", "slot_token_hash"), ("idempotency_uq", "tenant_id, property_id, idempotency_key")],
      indexes=["availability_hold_expiry_ix ON scheduling.availability_hold (expires_at) WHERE status = 'Active'"])

table(S, "schedule_change_proposal", AGGREGATE, PROPERTY, key=None, source_cols=False, handoff="completed",
      spec="SCH-020 / GUI-003 preflight; CON-006 undo window",
      doc="A preflight: what the operator was shown and agreed to. Single use; commit re-evaluates and compares.",
      statuses=["Open", "Committed", "Expired", "Rejected", "Superseded"],
      cols=[
          col("appointment_id", "uuid", fk="scheduling.appointment", same_property=True),
          col("token_hash", HASH),
          col("proposed_start", "timestamptz"),
          col("proposed_end", "timestamptz"),
          col("proposed_staff_id", "uuid", null=True, fk="workforce.staff"),
          col("proposed_resource_id", "uuid", null=True, fk="resources.resource", same_property=True),
          col("from_version", "integer"),
          col("reason_code", "text", null=True),
          col("override_reason", "text", null=True),
          col("expires_at", "timestamptz"),
          col("committed_at", "timestamptz", null=True),
          col("undo_until", "timestamptz", null=True, doc="CON-006 undo window end"),
      ],
      checks=[("range_forward", "proposed_end > proposed_start"),
              ("committed_complete", "(status = 'Committed') = (committed_at IS NOT NULL)")],
      uniques=[("token_uq", "token_hash")],
      indexes=["schedule_change_proposal_expiry_ix ON scheduling.schedule_change_proposal (expires_at) WHERE status = 'Open'"],
      dropped=[("starts_at/ends_at", "proposed_start/proposed_end"), ("reason_code_value", "reason_code"),
               ("conflicts_json", "scheduling.conflict_result rows")])

table(S, "conflict_result", CHILD, PROPERTY, handoff="completed", spec="CON-001..CON-007 register",
      cols=[
          col("schedule_change_proposal_id", "uuid", fk="scheduling.schedule_change_proposal", same_property=True),
          col("conflict_code", "text", check="conflict_code ~ '^CON-[0-9]{3}$'"),
          col("severity", "text", check="severity IN ('Soft', 'Hard')"),
          col("subject_type", "text"),
          col("subject_id", "uuid", null=True),
          col("detail", "jsonb", default="'{}'"),
          col("override_allowed", "boolean"),
          col("overridden", "boolean", default="false"),
      ],
      checks=[("hard_not_overridable", "severity = 'Soft' OR (NOT override_allowed AND NOT overridden)"),
              ("override_permitted", "NOT overridden OR override_allowed")])

table(S, "waitlist_entry", AGGREGATE, PROPERTY, pk="waitlist_id", key=None, source_cols=False,
      spec="§Reschedule, cancel and waitlist",
      statuses=["Waiting", "Offered", "Accepted", "Expired", "Cancelled"],
      cols=[
          col("guest_id", "uuid", fk="guest.guest"),
          col("service_id", "uuid", null=True, fk="catalog.service"),
          col("criteria", "jsonb"),
          col("priority_rule_version", "text"),
          col("expires_at", "timestamptz", null=True),
          col("offered_at", "timestamptz", null=True),
          col("offer_expires_at", "timestamptz", null=True),
          col("accepted_appointment_id", "uuid", null=True, fk="scheduling.appointment", same_property=True),
      ],
      checks=[("accepted_has_appointment", "(status = 'Accepted') = (accepted_appointment_id IS NOT NULL)")])

table(S, "turnaround_task", AGGREGATE, PROPERTY, key=None, source_cols=False, handoff="completed (moved from resources)",
      spec="room readiness; housekeeping room_readiness:update",
      statuses=["Pending", "InProgress", "Completed", "Skipped"],
      cols=[
          col("appointment_id", "uuid", null=True, fk="scheduling.appointment", same_property=True),
          col("resource_id", "uuid", fk="resources.resource", same_property=True),
          col("task_type", "text", check="task_type IN ('Turnover', 'DeepClean', 'Restock')"),
          col("due_at", "timestamptz"),
          col("assigned_staff_id", "uuid", null=True, fk="workforce.staff"),
          col("started_at", "timestamptz", null=True),
          col("completed_at", "timestamptz", null=True),
      ],
      checks=[("completed_complete", "(status = 'Completed') = (completed_at IS NOT NULL)")],
      indexes=["turnaround_task_open_ix ON scheduling.turnaround_task (tenant_id, property_id, due_at) WHERE status IN ('Pending', 'InProgress')"])
