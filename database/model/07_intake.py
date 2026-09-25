"""intake: form definitions, submissions, treatment notes (restricted; SEC-008).

Reachable only by spms_intake, which the API must SET LOCAL ROLE into. Restricted content is
envelope-encrypted in the application, so the database and its backups hold ciphertext only.
"""
from dsl import *

S = "intake"

table(S, "form_definition", MASTER, TENANT, key=None, grants="intake",
      handoff="new (replaces form_template + form_version)",
      spec="§53.2 Booking and intake",
      doc="One row per published version of a form. A submission references the exact version answered.",
      statuses=["Draft", "Published", "Retired"],
      cols=[
          col("form_code", "text"),
          col("version_number", "integer", check="version_number >= 1"),
          col("title", "text"),
          col("purpose", "text", check="purpose IN ('HealthIntake', 'Consent', 'Waiver', 'Feedback')"),
          col("schema_json", "jsonb", doc="field definitions, including which answers form the minimum-necessary summary"),
          col("published_at", "timestamptz", null=True),
          col("published_by", "uuid", null=True),
      ],
      checks=[("published_complete", "status <> 'Published' OR (published_at IS NOT NULL AND published_by IS NOT NULL)")],
      uniques=[("version_uq", "tenant_id, form_code, version_number")])

table(S, "intake_submission", AGGREGATE, PROPERTY, pk="submission_id", key=None, source_cols=False, grants="intake",
      handoff="completed (absorbs form_assignment and provider_acknowledgement)",
      spec="SEC-008; DEC-004; intake:update:own:before_lock; provider intake:read:assigned:minimum_necessary",
      doc="Created as Assigned when a form is due. response_cipher is the full answer set; summary_cipher is the "
          "minimum-necessary subset the assigned provider sees and acknowledges.",
      statuses=["Assigned", "Draft", "Submitted", "Locked", "Reviewed", "Waived", "Superseded"],
      cols=[
          col("form_definition_id", "uuid", fk="intake.form_definition"),
          col("appointment_id", "uuid", null=True, fk="scheduling.appointment", same_property=True),
          col("guest_id", "uuid", fk="guest.guest"),
          col("submitted_by_guest_id", "uuid", null=True, fk="guest.guest.guest_id", doc="guardian for a minor"),
          col("due_at", "timestamptz", null=True),
          col("response_cipher", "bytea", null=True, restricted=True),
          col("summary_cipher", "bytea", null=True, restricted=True),
          col("key_version", "text", null=True),
          col("requires_review", "boolean", default="false"),
          col("submitted_at", "timestamptz", null=True),
          col("locked_at", "timestamptz", null=True),
          col("reviewed_by", "uuid", null=True),
          col("reviewed_at", "timestamptz", null=True),
          col("acknowledged_by_staff_id", "uuid", null=True, fk="workforce.staff"),
          col("acknowledged_at", "timestamptz", null=True),
      ],
      checks=[("answers_have_key", "(response_cipher IS NULL) = (key_version IS NULL)"),
              ("submitted_has_answers", "status IN ('Assigned', 'Draft', 'Waived') OR (response_cipher IS NOT NULL AND submitted_at IS NOT NULL)"),
              ("locked_complete", "status NOT IN ('Locked', 'Reviewed') OR locked_at IS NOT NULL"),
              ("ack_complete", "(acknowledged_by_staff_id IS NULL) = (acknowledged_at IS NULL)")],
      indexes=["intake_submission_appointment_ix ON intake.intake_submission (tenant_id, appointment_id) WHERE status <> 'Superseded'"],
      dropped=[("form_assignment (table)", "status 'Assigned' + due_at"),
               ("provider_acknowledgement (table)", "acknowledged_by_staff_id/acknowledged_at")])

table(S, "treatment_note", LEDGER, PROPERTY, pk="note_id", grants="intake",
      spec="treatment_notes: create:assigned, read:authored_or_assigned, amend:authored; SEC-008",
      doc="Notes are never edited. An amendment is a new note that supersedes the old one, with a reason.",
      cols=[
          col("appointment_id", "uuid", fk="scheduling.appointment", same_property=True),
          col("provider_staff_id", "uuid", fk="workforce.staff"),
          col("template_code", "text", null=True),
          col("content_cipher", "bytea", restricted=True),
          col("key_version", "text"),
          col("authored_at", "timestamptz", doc="may precede created_at when captured offline on the tablet"),
          col("supersedes_note_id", "uuid", null=True, fk="intake.treatment_note.note_id"),
          col("amendment_reason", "text", null=True),
      ],
      checks=[("amendment_explained", "(supersedes_note_id IS NULL) = (amendment_reason IS NULL)")],
      uniques=[("supersedes_uq", "supersedes_note_id")])
