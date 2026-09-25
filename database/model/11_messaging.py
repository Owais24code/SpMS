"""messaging: versioned templates (with their reminder timing) and the messages sent from them.

SEC-010: content and links exclude health intake, treatment notes, payment credentials.
Provider delivery callbacks update the message and are written to core.audit_event.
"""
from dsl import *

S = "messaging"
CHANNEL = "channel IN ('Email', 'Sms', 'WhatsApp', 'Push')"

table(S, "message_template", MASTER, TENANT_OPT, key=None,
      handoff="completed (absorbs message_template_version, reminder_rule)",
      spec="§53.2 Guest messaging: reminders, quiet hours, consent; DEC-008",
      doc="One row per approved version of a template in a channel and locale. Reminder timing is part of the "
          "template: trigger_event + offset_minutes.",
      statuses=["Draft", "Approved", "Active", "Retired"],
      cols=[
          col("template_code", "text"),
          col("version_number", "integer", check="version_number >= 1"),
          col("channel", "text", check=CHANNEL),
          col("locale", "text"),
          col("purpose", "text", check="purpose IN ('Confirmation', 'Reminder', 'Cancellation', 'IntakeRequest', "
                                       "'Receipt', 'Waitlist', 'Marketing', 'Transactional')"),
          col("subject", "text", null=True),
          col("body_template", "text"),
          col("variables", "text[]", default="'{}'"),
          col("provider_template_reference", "text", null=True, doc="WhatsApp/SMS provider-approved template id"),
          col("trigger_event", "text", null=True, check="trigger_event IN ('AppointmentConfirmed', 'BeforeStart', "
                                                        "'AfterCompletion', 'IntakeDue', 'WaitlistOffer')"),
          col("offset_minutes", "integer", null=True, doc="negative = before the event"),
          col("service_id", "uuid", null=True, fk="catalog.service", doc="NULL = every service"),
          col("respect_quiet_hours", "boolean", default="true"),
          col("approved_by", "uuid", null=True),
          col("approved_at", "timestamptz", null=True),
      ],
      checks=[("active_is_approved", "status NOT IN ('Approved', 'Active') OR approved_by IS NOT NULL"),
              ("trigger_pair", "(trigger_event IS NULL) = (offset_minutes IS NULL)")],
      uniques=[("version_uq", "NULLS NOT DISTINCT (tenant_id, property_id, template_code, channel, locale, version_number)")],
      dropped=[("message_template_version (table)", "version_number on the template row"),
               ("reminder_rule (table)", "trigger_event/offset_minutes; quiet-hours window is the setting messaging.quiet_hours")])

table(S, "scheduled_message", AGGREGATE, PROPERTY, key=None, source_cols=False,
      handoff="completed (absorbs message_delivery)",
      doc="A message to one guest, from scheduling to delivery.",
      statuses=["Scheduled", "Sending", "Sent", "Delivered", "Failed", "Bounced", "Cancelled", "Expired", "Suppressed"],
      cols=[
          col("guest_id", "uuid", fk="guest.guest"),
          col("appointment_id", "uuid", null=True, fk="scheduling.appointment", same_property=True),
          col("message_template_id", "uuid", fk="messaging.message_template"),
          col("consent_record_id", "uuid", null=True, fk="guest.consent_record", doc="required for Marketing purpose"),
          col("channel", "text", check=CHANNEL),
          col("recipient_address_cipher", "bytea", restricted=True),
          col("key_version", "text"),
          col("send_after", "timestamptz"),
          col("expires_at", "timestamptz", null=True),
          col("provider_code", "text", null=True),
          col("provider_message_id", "text", null=True),
          col("attempt_count", "smallint", default="0", check="attempt_count >= 0"),
          col("sent_at", "timestamptz", null=True),
          col("delivered_at", "timestamptz", null=True),
          col("failure_code", "text", null=True),
          col("idempotency_key", "text"),
      ],
      checks=[("sent_complete", "status NOT IN ('Sent', 'Delivered') OR sent_at IS NOT NULL"),
              ("delivered_complete", "(status = 'Delivered') = (delivered_at IS NOT NULL)")],
      uniques=[("idempotency_uq", "tenant_id, idempotency_key")],
      indexes=["scheduled_message_due_ix ON messaging.scheduled_message (send_after) WHERE status = 'Scheduled'",
               "UNIQUE scheduled_message_provider_uq ON messaging.scheduled_message (provider_code, provider_message_id) "
               "WHERE provider_message_id IS NOT NULL"],
      dropped=[("message_delivery (table)", "delivery state on the message; callbacks in core.audit_event"),
               ("inbound_message (table)", "replies are provider webhooks: STOP revokes consent, others go to audit")])
