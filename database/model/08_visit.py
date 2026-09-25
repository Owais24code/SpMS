"""visit: arrival, check-in, the day's exceptions and close (Golden Guest Journey)."""
from dsl import *

S = "visit"

table(S, "visit", AGGREGATE, PROPERTY, key=None,
      spec="§53.2 check-in; GOLDEN_GUEST_JOURNEY; operating modes",
      statuses=["Expected", "Arrived", "InProgress", "Closed", "Cancelled", "NoShow"],
      cols=[
          col("visit_type", "text", check="visit_type IN ('DayGuest', 'HotelGuest', 'Member', 'Group', 'WalkIn')"),
          col("primary_guest_id", "uuid", null=True, fk="guest.guest.guest_id"),
          col("operating_mode", "text", check="operating_mode IN ('Standalone', 'MarqueeIntegrated')",
              doc="frozen at open: the mode this visit was operated under"),
          col("scheduled_arrival_at", "timestamptz", null=True),
          col("scheduled_departure_at", "timestamptz", null=True),
          col("actual_arrival_at", "timestamptz", null=True),
          col("actual_departure_at", "timestamptz", null=True),
          col("opened_at", "timestamptz", default="now()"),
          col("closed_at", "timestamptz", null=True),
      ],
      checks=[("closed_complete", "(status = 'Closed') = (closed_at IS NOT NULL)")],
      indexes=["visit_day_ix ON visit.visit (tenant_id, property_id, scheduled_arrival_at)"],
      dropped=[("admission_state", "status (one lifecycle)"), ("group_event_id", "groups are R3")])

table(S, "visit_appointment", CHILD, PROPERTY, handoff="new (replaces appointment.visit_id)",
      cols=[
          col("visit_id", "uuid", fk="visit.visit", same_property=True),
          col("appointment_id", "uuid", fk="scheduling.appointment", same_property=True),
      ],
      uniques=[("appointment_uq", "tenant_id, appointment_id")])

table(S, "visit_participant", CHILD, PROPERTY,
      cols=[
          col("visit_id", "uuid", fk="visit.visit", same_property=True),
          col("guest_id", "uuid", fk="guest.guest"),
          col("participant_role", "text", check="participant_role IN ('Primary', 'Companion', 'Minor', 'Guardian')"),
          col("arrival_state", "text", default="'Expected'",
              check="arrival_state IN ('Expected', 'Arrived', 'Departed', 'NoShow')"),
          col("checked_in_at", "timestamptz", null=True),
          col("checked_in_by_staff_id", "uuid", null=True, fk="workforce.staff"),
          col("checked_out_at", "timestamptz", null=True),
      ],
      uniques=[("guest_uq", "tenant_id, visit_id, guest_id")],
      dropped=[("status", "arrival_state is this entity's lifecycle")])

table(S, "visit_event", LEDGER, PROPERTY, spec="OFFLINE_OPERATIONS_MATRIX (offline capture keeps occurred_at)",
      cols=[
          col("visit_id", "uuid", fk="visit.visit", same_property=True),
          col("visit_participant_id", "uuid", null=True, fk="visit.visit_participant", same_property=True),
          col("event_type", "text", check="event_type IN ('Opened', 'Arrived', 'CheckedIn', 'CredentialIssued', "
                                          "'CredentialReturned', 'Departed', 'Closed', 'Reopened', 'ExceptionRaised', 'ExceptionResolved')"),
          col("prior_state", "text", null=True),
          col("new_state", "text", null=True),
          col("reason_code", "text", null=True),
          col("occurred_at", "timestamptz"),
          col("performed_by_staff_id", "uuid", null=True, fk="workforce.staff"),
          col("evidence_reference", "text", null=True),
          col("details", "jsonb", null=True),
      ],
      indexes=["visit_event_visit_ix ON visit.visit_event (tenant_id, visit_id, occurred_at)"])

table(S, "visit_exception", AGGREGATE, PROPERTY, key=None, source_cols=False,
      spec="OPERATIONS_EXCEPTION_CENTER",
      statuses=["Open", "Resolved", "Waived"],
      cols=[
          col("visit_id", "uuid", fk="visit.visit", same_property=True),
          col("visit_participant_id", "uuid", null=True, fk="visit.visit_participant", same_property=True),
          col("exception_type", "text", check="exception_type IN ('PaymentOutstanding', 'IntakeIncomplete', 'IdentityMismatch', "
                                              "'CredentialFailed', 'AccessFailed', 'ConsentMissing', 'Other')"),
          col("severity", "text", check="severity IN ('Info', 'Warning', 'Blocking')"),
          col("resolved_at", "timestamptz", null=True),
          col("resolved_by_staff_id", "uuid", null=True, fk="workforce.staff"),
          col("resolution_note", "text", null=True),
      ],
      checks=[("resolution_complete", "(status = 'Open') = (resolved_at IS NULL)")],
      indexes=["visit_exception_open_ix ON visit.visit_exception (tenant_id, property_id, severity) WHERE status = 'Open'"],
      dropped=[("blocking", "severity = 'Blocking'"), ("opened_at", "created_at")])
