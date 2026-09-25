"""intake: forms, health intake, provider acknowledgement, treatment notes (restricted; SEC-008).

Every table here is reachable only by spms_intake, which the API must SET LOCAL ROLE into.
Restricted content is envelope-encrypted in the application (Key Vault-wrapped data keys),
so the database, its backups and its replicas hold ciphertext only.
"""
from dsl import *

S = "intake"

table(S, "form_template", MASTER, TENANT, key=None, handoff="completed", grants="intake",
      spec="§53.2 Booking and intake", statuses=["Draft", "Active", "Retired"],
      cols=[
          col("template_code", "text"),
          col("title", "text"),
          col("purpose", "text", check="purpose IN ('HealthIntake', 'Consent', 'Waiver', 'Feedback')"),
      ],
      uniques=[("code_uq", "tenant_id, template_code")],
      dropped=[("definition_json", "form_version.schema_json")])

table(S, "form_version", MASTER, TENANT, key=None, handoff="completed", grants="intake",
      statuses=["Draft", "Published", "Retired"],
      cols=[
          col("form_template_id", "uuid", fk="intake.form_template"),
          col("version_number", "integer", check="version_number >= 1"),
          col("schema_json", "jsonb", doc="field definitions, including which answers form the minimum-necessary summary"),
          col("published_at", "timestamptz", null=True),
          col("published_by", "uuid", null=True),
      ],
      checks=[("published_complete", "status <> 'Published' OR (published_at IS NOT NULL AND published_by IS NOT NULL)")],
      uniques=[("number_uq", "tenant_id, form_template_id, version_number")],
      dropped=[("definition_json", "schema_json")])

table(S, "form_assignment", AGGREGATE, PROPERTY, key=None, source_cols=False, handoff="completed", grants="intake",
      statuses=["Assigned", "InProgress", "Submitted", "Waived", "Expired"],
      cols=[
          col("form_version_id", "uuid", fk="intake.form_version"),
          col("guest_id", "uuid", fk="guest.guest"),
          col("appointment_id", "uuid", null=True, fk="scheduling.appointment", same_property=True),
          col("assigned_at", "timestamptz", default="now()"),
          col("due_at", "timestamptz", null=True),
          col("completed_at", "timestamptz", null=True),
      ],
      indexes=["form_assignment_appointment_ix ON intake.form_assignment (tenant_id, appointment_id) WHERE status <> 'Waived'"])

table(S, "intake_submission", AGGREGATE, PROPERTY, pk="submission_id", key=None, source_cols=False, grants="intake",
      spec="SEC-008; DEC-004; intake:update:own:before_lock; provider intake:read:assigned:minimum_necessary",
      doc="response_cipher is the full answer set (guest and HR-authorised review only). summary_cipher is the "
          "minimum-necessary subset the assigned provider sees.",
      statuses=["Draft", "Submitted", "Locked", "Reviewed", "Superseded"],
      cols=[
          col("form_assignment_id", "uuid", null=True, fk="intake.form_assignment", same_property=True),
          col("form_version_id", "uuid", fk="intake.form_version"),
          col("appointment_id", "uuid", null=True, fk="scheduling.appointment", same_property=True),
          col("guest_id", "uuid", fk="guest.guest"),
          col("submitted_by_guest_id", "uuid", null=True, fk="guest.guest.guest_id", doc="guardian for a minor"),
          col("response_cipher", "bytea", restricted=True),
          col("summary_cipher", "bytea", null=True, restricted=True),
          col("key_version", "text"),
          col("requires_review", "boolean", default="false"),
          col("submitted_at", "timestamptz", null=True),
          col("locked_at", "timestamptz", null=True),
          col("reviewed_by", "uuid", null=True),
          col("reviewed_at", "timestamptz", null=True),
      ],
      checks=[("submitted_complete", "status = 'Draft' OR submitted_at IS NOT NULL"),
              ("locked_complete", "status NOT IN ('Locked', 'Reviewed') OR locked_at IS NOT NULL")],
      dropped=[("template_id text/template_version", "form_version_id FK")])

table(S, "provider_acknowledgement", LEDGER, PROPERTY, handoff="completed", grants="intake",
      spec="provider intake:acknowledge:assigned",
      cols=[
          col("submission_id", "uuid", fk="intake.intake_submission"),
          col("appointment_id", "uuid", fk="scheduling.appointment", same_property=True),
          col("staff_id", "uuid", fk="workforce.staff"),
      ],
      uniques=[("once_uq", "tenant_id, submission_id, staff_id, appointment_id")],
      dropped=[("effective_from/effective_to", "an acknowledgement is a moment: created_at")])

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
      uniques=[("supersedes_uq", "supersedes_note_id")],
      dropped=[("template_id", "template_code")])
