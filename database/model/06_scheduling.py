"""scheduling: the guest's visit (day plan -> arrival -> close), appointments, preflight, waitlist, turnover."""
from dsl import *

S = "scheduling"

APPOINTMENT_STATUSES = ["Held", "Confirmed", "CheckedIn", "Ready", "InService", "Completed", "Cancelled", "NoShow"]

table(S, "visit", AGGREGATE, PROPERTY, key=None, handoff="kept (moved from visit; absorbs appointment_itinerary)",
      spec="§53.2 check-in; GOLDEN_GUEST_JOURNEY; operating modes",
      doc="A party's day at the spa: planned (the itinerary), then arrived, then closed. Appointments belong to it.",
      statuses=["Planned", "Arrived", "InProgress", "Closed", "Cancelled", "NoShow"],
      cols=[
          col("visit_type", "text", check="visit_type IN ('DayGuest', 'HotelGuest', 'Member', 'Group', 'WalkIn')"),
          col("primary_guest_id", "uuid", fk="guest.guest.guest_id"),
          col("visit_date", "date"),
          col("operating_mode", "text", check="operating_mode IN ('Standalone', 'MarqueeIntegrated')",
              doc="frozen at creation: the mode this visit is operated under"),
          col("pms_stay_reference", "text", null=True),
          col("scheduled_arrival_at", "timestamptz", null=True),
          col("actual_arrival_at", "timestamptz", null=True),
          col("actual_departure_at", "timestamptz", null=True),
          col("closed_at", "timestamptz", null=True),
          col("notes", "text", null=True),
      ],
      checks=[("closed_complete", "(status = 'Closed') = (closed_at IS NOT NULL)")],
      indexes=["visit_day_ix ON scheduling.visit (tenant_id, property_id, visit_date)"],
      dropped=[("appointment_itinerary, appointment_itinerary_link (tables)", "the visit is the day plan"),
               ("visit_participant (table)", "each attendee has their own appointment with checked_in_at"),
               ("visit_event (table)", "core.audit_event"), ("admission_state", "status")])

table(S, "appointment", AGGREGATE, PROPERTY, key=None,
      spec="§53.2 Scheduling, Booking; DEC-002; DEC-005; CON-001/002/005",
      doc="One guest's treatment in one room with one provider. Held = the online slot hold (expires_at).",
      statuses=APPOINTMENT_STATUSES,
      cols=[
          col("confirmation_number", "text", null=True, doc="guest-facing, quoted at the desk; unique per tenant"),
          col("visit_id", "uuid", null=True, fk="scheduling.visit", same_property=True),
          col("guest_id", "uuid", fk="guest.guest", doc="the attendee"),
          col("service_id", "uuid", fk="catalog.service"),
          col("provider_id", "uuid", null=True, fk="workforce.staff.staff_id"),
          col("room_id", "uuid", null=True, fk="resources.resource.resource_id", same_property=True),
          col("guest_requested_provider", "boolean", default="false"),
          col("duration_minutes", "integer", check="duration_minutes > 0", doc="frozen from the service at booking"),
          col("start_at", "timestamptz"),
          col("end_at", "timestamptz", doc="maintained by trigger from start_at + duration_minutes"),
          col("entered_timezone", "text"),
          col("source", "text", check="source IN ('Desk', 'Online', 'Mobile', 'Phone', 'ProviderTablet', 'Marquee', 'Import')"),
          col("price_minor", "bigint", default="0", check="price_minor >= 0", doc="frozen at booking"),
          CURRENCY(),
          col("options", "jsonb", default="'[]'", check="jsonb_typeof(options) = 'array'",
              doc="chosen add-ons, frozen: [{option_code, quantity, price_delta_minor, duration_delta_minutes}]"),
          col("hold_expires_at", "timestamptz", null=True),
          col("checked_in_at", "timestamptz", null=True),
          col("intake_acknowledged_at", "timestamptz", null=True),
          col("cancellation_reason_code", "text", null=True),
          col("cancelled_at", "timestamptz", null=True),
          col("completed_at", "timestamptz", null=True),
      ],
      checks=[("interval_forward", "end_at > start_at"),
              ("hold_expires", "(status = 'Held') = (hold_expires_at IS NOT NULL)"),
              ("cancel_complete", "(status = 'Cancelled') = (cancelled_at IS NOT NULL)"),
              ("complete_complete", "(status = 'Completed') = (completed_at IS NOT NULL)")],
      indexes=[
          "UNIQUE appointment_confirmation_uq ON scheduling.appointment (tenant_id, confirmation_number) WHERE confirmation_number IS NOT NULL",
          "appointment_board_ix ON scheduling.appointment USING gist (tenant_id, property_id, tstzrange(start_at, end_at, '[)'))",
          "appointment_guest_window_ix ON scheduling.appointment (tenant_id, guest_id, start_at) WHERE status NOT IN ('Cancelled', 'NoShow')",
          "appointment_provider_window_ix ON scheduling.appointment USING gist (tenant_id, provider_id, tstzrange(start_at, end_at, '[)')) "
          "WHERE provider_id IS NOT NULL AND status NOT IN ('Cancelled', 'NoShow')",
          "appointment_hold_expiry_ix ON scheduling.appointment (hold_expires_at) WHERE status = 'Held'",
      ],
      dropped=[("record_key", "confirmation_number"),
               ("appointment_resource_assignment, appointment_assignment (tables)", "provider_id/room_id"),
               ("appointment_line (table)", "price_minor + options; the financial lines are commerce.order_line"),
               ("appointment_participant (table)", "one attendee per appointment; the party is the visit"),
               ("appointment_status_history (table)", "core.audit_event (from_status/to_status)"),
               ("availability_hold (table)", "status 'Held' + hold_expires_at"),
               ("service_version_id", "duration, price and options are frozen on the row")],
      extra_sql="""
-- end_at is derived, never trusted from the caller: the room exclusion indexes it.
-- Not a GENERATED column because timestamptz + interval is STABLE, not IMMUTABLE.
CREATE FUNCTION scheduling.appointment_set_end_at() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    NEW.end_at := NEW.start_at + make_interval(mins => NEW.duration_minutes);
    RETURN NEW;
END $$;
CREATE TRIGGER appointment_end_at BEFORE INSERT OR UPDATE OF start_at, duration_minutes, end_at
    ON scheduling.appointment FOR EACH ROW EXECUTE FUNCTION scheduling.appointment_set_end_at();

-- CON-002, HARD: a room cannot hold two treatments at once. Cancelled and no-show
-- rows hold nothing. Violations raise SQLSTATE 23P01, mapped back to CON-002.
-- CON-001 (provider overlap) is SOFT and overridable, so it is only indexed.
ALTER TABLE scheduling.appointment ADD CONSTRAINT appointment_room_no_overlap
    EXCLUDE USING gist (tenant_id WITH =, property_id WITH =, room_id WITH =,
                        tstzrange(start_at, end_at, '[)') WITH &&)
    WHERE (room_id IS NOT NULL AND status NOT IN ('Cancelled', 'NoShow'));
""")

table(S, "schedule_change_proposal", AGGREGATE, PROPERTY, key=None, source_cols=False, handoff="completed (absorbs conflict_result)",
      spec="SCH-020 / GUI-003 preflight; CON-006 undo window",
      doc="A preflight: what the operator was shown and agreed to. Single use; commit re-evaluates and compares.",
      statuses=["Open", "Committed", "Expired", "Rejected", "Superseded"],
      cols=[
          col("appointment_id", "uuid", fk="scheduling.appointment", same_property=True),
          col("token_hash", HASH),
          col("proposed_start", "timestamptz"),
          col("proposed_end", "timestamptz"),
          col("proposed_provider_id", "uuid", null=True, fk="workforce.staff.staff_id"),
          col("proposed_room_id", "uuid", null=True, fk="resources.resource.resource_id", same_property=True),
          col("from_version", "integer"),
          col("conflicts", "jsonb", default="'[]'", check="jsonb_typeof(conflicts) = 'array'",
              doc="as shown: [{code: 'CON-001', severity: 'Soft', subject, override_allowed, overridden}]"),
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
      dropped=[("conflict_result (table)", "conflicts jsonb: a record of what was shown, never queried relationally")],
      extra_sql="""
-- A hard conflict can never be recorded as overridden.
ALTER TABLE scheduling.schedule_change_proposal ADD CONSTRAINT schedule_change_proposal_hard_not_overridden
    CHECK (NOT jsonb_path_exists(conflicts, '$[*] ? (@.severity == "Hard" && @.overridden == true)'));
""")

table(S, "waitlist_entry", AGGREGATE, PROPERTY, pk="waitlist_id", key=None, source_cols=False,
      spec="§Reschedule, cancel and waitlist",
      statuses=["Waiting", "Offered", "Accepted", "Expired", "Cancelled"],
      cols=[
          col("guest_id", "uuid", fk="guest.guest"),
          col("service_id", "uuid", null=True, fk="catalog.service"),
          col("criteria", "jsonb"),
          col("expires_at", "timestamptz", null=True),
          col("offer_expires_at", "timestamptz", null=True),
          col("accepted_appointment_id", "uuid", null=True, fk="scheduling.appointment", same_property=True),
      ],
      checks=[("accepted_has_appointment", "(status = 'Accepted') = (accepted_appointment_id IS NOT NULL)")])

table(S, "turnaround_task", AGGREGATE, PROPERTY, key=None, source_cols=False,
      handoff="completed (absorbs sanitation_record)",
      spec="room readiness; housekeeping room_readiness:update",
      statuses=["Pending", "InProgress", "Completed", "Skipped"],
      cols=[
          col("appointment_id", "uuid", null=True, fk="scheduling.appointment", same_property=True),
          col("resource_id", "uuid", fk="resources.resource", same_property=True),
          col("task_type", "text", check="task_type IN ('Turnover', 'DeepClean', 'Sanitation', 'Restock')"),
          col("due_at", "timestamptz"),
          col("assigned_staff_id", "uuid", null=True, fk="workforce.staff"),
          col("checklist_code", "text", null=True),
          col("result", "text", null=True, check="result IN ('Pass', 'Fail', 'NeedsAttention')"),
          col("completed_at", "timestamptz", null=True),
      ],
      checks=[("completed_complete", "(status = 'Completed') = (completed_at IS NOT NULL AND result IS NOT NULL)")],
      indexes=["turnaround_task_open_ix ON scheduling.turnaround_task (tenant_id, property_id, due_at) WHERE status IN ('Pending', 'InProgress')"],
      dropped=[("sanitation_record (table)", "task_type 'Sanitation' + result")])

table(S, "visit_exception", AGGREGATE, PROPERTY, key=None, source_cols=False,
      spec="OPERATIONS_EXCEPTION_CENTER",
      doc="Kept typed: the exception center queries open items across the day.",
      statuses=["Open", "Resolved", "Waived"],
      cols=[
          col("visit_id", "uuid", fk="scheduling.visit", same_property=True),
          col("appointment_id", "uuid", null=True, fk="scheduling.appointment", same_property=True),
          col("exception_type", "text", check="exception_type IN ('PaymentOutstanding', 'IntakeIncomplete', 'IdentityMismatch', "
                                              "'CredentialFailed', 'AccessFailed', 'ConsentMissing', 'Other')"),
          col("severity", "text", check="severity IN ('Info', 'Warning', 'Blocking')"),
          col("resolved_at", "timestamptz", null=True),
          col("resolved_by_staff_id", "uuid", null=True, fk="workforce.staff"),
          col("resolution_note", "text", null=True),
      ],
      checks=[("resolution_complete", "(status = 'Open') = (resolved_at IS NULL)")],
      indexes=["visit_exception_open_ix ON scheduling.visit_exception (tenant_id, property_id, severity) WHERE status = 'Open'"])
