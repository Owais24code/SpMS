"""messaging: templates, reminder rules, scheduled messages, deliveries, suppression, inbound.

SEC-010: content and links exclude health intake, treatment notes, payment credentials.
"""
from dsl import *

S = "messaging"
CHANNEL = "channel IN ('Email', 'Sms', 'WhatsApp', 'Push')"

table(S, "message_template", MASTER, TENANT_OPT, key=None, handoff="completed",
      spec="§53.2 Guest messaging; DEC-008", statuses=["Draft", "Active", "Retired"],
      cols=[
          col("template_code", "text"),
          col("purpose", "text", check="purpose IN ('Confirmation', 'Reminder', 'Cancellation', 'IntakeRequest', "
                                       "'Receipt', 'Waitlist', 'Marketing', 'Transactional')"),
          col("channel", "text", check=CHANNEL),
      ],
      uniques=[("code_uq", "NULLS NOT DISTINCT (tenant_id, property_id, template_code, channel)")],
      dropped=[("channel_code", "channel"), ("scheduled_at/delivered_at", "scheduled_message/message_delivery"),
               ("definition_json", "message_template_version")])

table(S, "message_template_version", MASTER, TENANT_OPT, key=None, handoff="completed",
      statuses=["Draft", "Approved", "Active", "Retired"],
      cols=[
          col("message_template_id", "uuid", fk="messaging.message_template"),
          col("version_number", "integer", check="version_number >= 1"),
          col("locale", "text"),
          col("subject", "text", null=True),
          col("body_template", "text"),
          col("variables", "text[]", default="'{}'"),
          col("provider_template_reference", "text", null=True, doc="WhatsApp/SMS provider-approved template id"),
          col("approved_by", "uuid", null=True),
          col("approved_at", "timestamptz", null=True),
      ],
      checks=[("active_is_approved", "status NOT IN ('Approved', 'Active') OR approved_by IS NOT NULL")],
      uniques=[("number_uq", "tenant_id, message_template_id, locale, version_number")],
      dropped=[("channel/scheduled_at/delivered_at", "the template's / the delivery's"), ("definition_json", "typed columns")])

table(S, "reminder_rule", MASTER, TENANT_OPT, key=None, handoff="completed",
      spec="§53.2 Guest messaging: reminders, quiet hours", statuses=["Active", "Retired"],
      cols=[
          col("message_template_id", "uuid", fk="messaging.message_template"),
          col("service_id", "uuid", null=True, fk="catalog.service", doc="NULL = every service"),
          col("trigger_event", "text", check="trigger_event IN ('AppointmentConfirmed', 'BeforeStart', 'AfterCompletion', "
                                             "'IntakeDue', 'WaitlistOffer')"),
          col("offset_minutes", "integer", doc="negative = before the event"),
          col("respect_quiet_hours", "boolean", default="true"),
          col("quiet_hours_start", "time", null=True),
          col("quiet_hours_end", "time", null=True),
      ],
      checks=[("quiet_pair", "(quiet_hours_start IS NULL) = (quiet_hours_end IS NULL)")],
      dropped=[("definition_json", "typed columns")])

table(S, "scheduled_message", AGGREGATE, PROPERTY, key=None, source_cols=False, handoff="completed",
      statuses=["Scheduled", "Sending", "Sent", "Cancelled", "Expired", "Suppressed"],
      cols=[
          col("guest_id", "uuid", fk="guest.guest"),
          col("appointment_id", "uuid", null=True, fk="scheduling.appointment", same_property=True),
          col("message_template_version_id", "uuid", fk="messaging.message_template_version"),
          col("reminder_rule_id", "uuid", null=True, fk="messaging.reminder_rule"),
          col("consent_record_id", "uuid", null=True, fk="guest.consent_record",
              doc="required for Marketing purpose templates"),
          col("channel", "text", check=CHANNEL),
          col("recipient_address_cipher", "bytea", restricted=True),
          col("key_version", "text"),
          col("send_after", "timestamptz"),
          col("expires_at", "timestamptz", null=True),
      ],
      indexes=["scheduled_message_due_ix ON messaging.scheduled_message (send_after) WHERE status = 'Scheduled'"],
      dropped=[("scheduled_at", "send_after"), ("delivered_at", "message_delivery"), ("starts_at/ends_at/effective_*", "send_after/expires_at")])

table(S, "message_delivery", LEDGER, PROPERTY, pk="delivery_id", time_col="occurred_at", partition_by="occurred_at",
      spec="delivery status callbacks", doc="One row per delivery event (queued, sent, delivered, failed...).",
      cols=[
          col("scheduled_message_id", "uuid", fk="messaging.scheduled_message", same_property=True),
          col("provider_code", "text"),
          col("provider_message_id", "text", null=True),
          col("delivery_state", "text", check="delivery_state IN ('Queued', 'Sent', 'Delivered', 'Failed', 'Bounced', 'Undeliverable')"),
          col("attempt_number", "smallint", default="1", check="attempt_number >= 1"),
          col("provider_event_at", "timestamptz", null=True),
          col("error_code", "text", null=True),
          col("idempotency_key", "text"),
      ],
      indexes=["message_delivery_message_ix ON messaging.message_delivery (tenant_id, scheduled_message_id, occurred_at)"],
      dropped=[("recipient_reference/template_id/template_version/purpose/consent_reference", "scheduled_message owns them")])

table(S, "message_suppression", MASTER, TENANT, key=None, handoff="completed",
      statuses=["Active", "Lifted"],
      cols=[
          col("guest_id", "uuid", null=True, fk="guest.guest"),
          col("lookup_hash", HASH, null=True, doc="address-level suppression for an unknown sender"),
          col("channel", "text", check=CHANNEL),
          col("suppression_reason", "text", check="suppression_reason IN ('OptOut', 'Bounce', 'Complaint', 'DoNotContact', 'Legal')"),
      ],
      checks=[("has_subject", "num_nonnulls(guest_id, lookup_hash) >= 1")],
      indexes=["message_suppression_lookup_ix ON messaging.message_suppression (tenant_id, channel, lookup_hash) WHERE status = 'Active'"],
      dropped=[("channel_code", "channel"), ("scheduled_at/delivered_at", "not this entity's")])

table(S, "inbound_message", LEDGER, PROPERTY, time_col="received_at", handoff="completed",
      cols=[
          col("guest_id", "uuid", null=True, fk="guest.guest"),
          col("in_reply_to_scheduled_message_id", "uuid", null=True, fk="messaging.scheduled_message", same_property=True),
          col("channel", "text", check=CHANNEL),
          col("provider_message_id", "text"),
          col("sender_address_cipher", "bytea", restricted=True),
          col("content_cipher", "bytea", restricted=True),
          col("key_version", "text"),
          col("classification", "text", default="'Unknown'", check="classification IN ('Reply', 'OptOut', 'OptIn', 'Unknown')"),
      ],
      uniques=[("provider_uq", "tenant_id, channel, provider_message_id")],
      dropped=[("scheduled_at/delivered_at/effective_*", "received_at")])
