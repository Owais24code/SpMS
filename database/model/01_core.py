"""core: tenancy, identity, configuration, audit, outbox, idempotency, retention."""
from dsl import *

S = "core"

table(S, "tenant", AGGREGATE, GLOBAL, pk="tenant_id", key=None, source_cols=False,
      spec="R0; §24 Tenancy; spec 'tenant directory itself now has RLS'",
      doc="Tenant directory. RLS restricts it to the current tenant like every other table.",
      statuses=["Active", "Suspended", "Closed"],
      cols=[
          col("code", "text", check="code ~ '^[a-z0-9][a-z0-9-]{1,62}$'"),
          col("name", "text"),
          col("default_locale", "text", default="'en-US'"),
          col("default_currency_code", "char(3)", default="'USD'", check="default_currency_code ~ '^[A-Z]{3}$'"),
          col("data_region", "text", doc="data residency region; decision register 'data residency per property'"),
      ],
      uniques=[("code_uq", "code")],
      extra_sql="""
ALTER TABLE core.tenant ENABLE ROW LEVEL SECURITY;
ALTER TABLE core.tenant FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_scope ON core.tenant USING (tenant_id = (SELECT core.current_tenant_id()));
""")

table(S, "department", MASTER, TENANT_OPT, key=None, handoff="kept (moved from workforce: shared by workforce and finance)",
      spec="§Income COGS and inventory accounts",
      statuses=["Active", "Retired"],
      cols=[col("department_code", "text"), col("department_name", "text")],
      uniques=[("code_uq", "NULLS NOT DISTINCT (tenant_id, property_id, department_code)")])

table(S, "property", AGGREGATE, TENANT, key=None, source_cols=True,
      spec="§53.2 Operating modes; DEC-001; DEC-011",
      doc="A spa property. operating_mode is the default; capability_ownership decides per capability and time.",
      statuses=["Active", "Inactive", "Closed"],
      cols=[
          col("code", "text"),
          col("name", "text"),
          col("timezone", "text", doc="IANA zone; the board and business day are always read in it"),
          col("currency_code", "char(3)", check="currency_code ~ '^[A-Z]{3}$'"),
          col("locale", "text", default="'en-US'"),
          col("operating_mode", "text", default="'Standalone'",
              check="operating_mode IN ('Standalone', 'MarqueeIntegrated')"),
          col("room_turnover_minutes", "integer", default="15", check="room_turnover_minutes >= 0",
              doc="default CON-002 turnover buffer; a property_service row may override"),
          col("provider_transition_minutes", "integer", default="10", check="provider_transition_minutes >= 0"),
      ],
      uniques=[("code_uq", "tenant_id, code")],
      dropped=[("record_key", "code is the property's business key")])

table(S, "property_operating_hours", MASTER, PROPERTY, key=None, handoff="new",
      spec="§53.2 Scheduling (availability grid)",
      statuses=["Active", "Retired"],
      cols=[
          col("day_of_week", "smallint", check="day_of_week BETWEEN 1 AND 7", doc="ISO: 1 = Monday"),
          col("opens_at", "time"),
          col("closes_at", "time"),
      ],
      checks=[("hours_forward", "closes_at > opens_at")],
      extra_sql="""
ALTER TABLE core.property_operating_hours ADD CONSTRAINT property_operating_hours_no_overlap
    EXCLUDE USING gist (tenant_id WITH =, property_id WITH =, day_of_week WITH =,
                        tstzrange(effective_from, coalesce(effective_to, 'infinity'), '[)') WITH &&)
    WHERE (status = 'Active');
""")

table(S, "capability_ownership", MASTER, PROPERTY, pk="ownership_id", key=None, effective=False,
      source_cols=False, spec="DEC-001 (APPROVED critical hybrid rule); MCI-001",
      doc="Exactly one authority per capability, property and instant. Hybrid cutovers are new effective-dated rows.",
      statuses=["Proposed", "Approved", "Active", "Superseded", "Rejected"],
      cols=[
          col("capability_code", "text", check="capability_code ~ '^[A-Z][A-Za-z]+$'",
              doc="e.g. Catalog, Pricing, Payment, Inventory, Entitlement, GuestProfile"),
          col("owner_system", "text", check="owner_system IN ('Spa', 'Marquee', 'Pms', 'Pos', 'External')"),
          col("effective_range", "tstzrange"),
          col("approved_by", "uuid", null=True),
          col("approved_at", "timestamptz", null=True),
      ],
      checks=[("approval_complete", "(approved_by IS NULL) = (approved_at IS NULL)"),
              ("active_is_approved", "status NOT IN ('Approved', 'Active') OR approved_by IS NOT NULL"),
              ("proposer_not_approver", "approved_by IS NULL OR approved_by IS DISTINCT FROM created_by")],
      dropped=[("approval_id", "replaced by approved_by/approved_at (who and when, not an opaque id)")],
      extra_sql="""
ALTER TABLE core.capability_ownership ADD CONSTRAINT capability_ownership_single_authority
    EXCLUDE USING gist (tenant_id WITH =, property_id WITH =, capability_code WITH =, effective_range WITH &&)
    WHERE (status IN ('Approved', 'Active'));
""")

table(S, "configuration_version", MASTER, TENANT_OPT, pk="configuration_id", key=None, effective=False,
      source_cols=False, spec="SEC-014 segregation of duties; configuration:propose/approve",
      statuses=["Proposed", "Approved", "Active", "Superseded", "Rejected", "RolledBack"],
      cols=[
          col("object_type", "text"),
          col("object_key", "text"),
          col("value_json", "jsonb"),
          col("effective_range", "tstzrange"),
          col("proposed_by", "uuid"),
          col("approved_by", "uuid", null=True),
          col("approved_at", "timestamptz", null=True),
      ],
      checks=[("proposer_not_approver", "approved_by IS NULL OR approved_by <> proposed_by")],
      dropped=[("approval_id", "replaced by approved_by/approved_at")],
      indexes=["configuration_version_object_ix ON core.configuration_version (tenant_id, object_type, object_key)"])

table(S, "feature_flag", MASTER, TENANT_OPT, handoff="completed", spec="R0 feature flags",
      statuses=["Active", "Retired"],
      cols=[col("enabled", "boolean", default="false"), col("description", "text", null=True)])

table(S, "reason_code", MASTER, TENANT_OPT, key=None, handoff="completed",
      spec="CON register override reasons; refunds; inventory adjustments; visit exceptions",
      statuses=["Active", "Retired"],
      cols=[
          col("reason_domain", "text", check="reason_domain ~ '^[A-Z][A-Za-z]+$'",
              doc="ScheduleOverride, Cancellation, NoShow, Refund, Comp, InventoryAdjustment, VisitException, ..."),
          col("code", "text"),
          col("label", "text"),
          col("requires_note", "boolean", default="false"),
          col("sort_order", "integer", default="0"),
      ],
      uniques=[("code_uq", "NULLS NOT DISTINCT (tenant_id, property_id, reason_domain, code)")])

table(S, "policy_definition", MASTER, TENANT_OPT, key=None, handoff="completed",
      spec="§Configurable policies and accountability",
      statuses=["Active", "Retired"],
      cols=[
          col("policy_code", "text"),
          col("policy_type", "text", check="policy_type ~ '^[A-Z][A-Za-z]+$'",
              doc="Cancellation, Deposit, NoShow, LateArrival, Buffer, Messaging, Override, Retention"),
          col("description", "text", null=True),
      ],
      uniques=[("code_uq", "NULLS NOT DISTINCT (tenant_id, property_id, policy_code)")],
      dropped=[("definition_json", "the rule body is versioned in policy_version")])

table(S, "policy_version", MASTER, TENANT_OPT, key=None, handoff="completed",
      statuses=["Draft", "Approved", "Active", "Retired"],
      cols=[
          col("policy_definition_id", "uuid", fk="core.policy_definition"),
          col("version_number", "integer", check="version_number >= 1"),
          col("definition_json", "jsonb"),
          col("deployment_scope", "text", default="'Training'",
              check="deployment_scope IN ('Training', 'Pilot', 'Production')"),
          col("approved_by", "uuid", null=True),
          col("approved_at", "timestamptz", null=True),
      ],
      checks=[("approver_not_author", "approved_by IS NULL OR approved_by IS DISTINCT FROM created_by")],
      uniques=[("number_uq", "tenant_id, policy_definition_id, version_number")])

# ---------------------------------------------------------------- identity --
table(S, "principal", AGGREGATE, TENANT, key=None, source_cols=False, handoff="new",
      spec="§24 Authentication; OpenFGA user identity",
      doc="Anyone or anything that acts: staff, guest, service or device. principal_id is the OpenFGA user id "
          "(user:<principal_id>), so replacing the identity provider never rewrites authorization tuples.",
      statuses=["Active", "Disabled", "Revoked"],
      cols=[
          col("principal_type", "text", check="principal_type IN ('Staff', 'Guest', 'Service', 'Device')"),
          col("display_name", "text"),
          col("last_authenticated_at", "timestamptz", null=True),
      ])

table(S, "service_identity", AGGREGATE, TENANT, key=None, source_cols=False, handoff="new",
      spec="§24 service accounts with rotation; integration_service role",
      statuses=["Active", "Disabled"],
      cols=[
          col("principal_id", "uuid", fk="core.principal"),
          col("idp_issuer", "text"),
          col("idp_subject", "text", doc="Entra application (client) object id"),
          col("description", "text"),
          col("credential_rotated_at", "timestamptz", null=True),
      ],
      uniques=[("subject_uq", "idp_issuer, idp_subject"), ("principal_uq", "principal_id")])

table(S, "device_registration", AGGREGATE, PROPERTY, key=None, source_cols=False, handoff="new",
      spec="SEC-013 device-bound encryption, remote revocation; provider tablet PWA; kiosk",
      statuses=["Pending", "Active", "Revoked"],
      cols=[
          col("principal_id", "uuid", fk="core.principal"),
          col("device_kind", "text", check="device_kind IN ('ProviderTablet', 'Kiosk', 'FrontDesk')"),
          col("device_name", "text"),
          col("public_key_spki", "bytea", doc="device key for the encrypted offline queue"),
          col("last_seen_at", "timestamptz", null=True),
          col("revoked_at", "timestamptz", null=True),
          col("revoked_by", "uuid", null=True),
      ],
      checks=[("revocation_complete", "(status = 'Revoked') = (revoked_at IS NOT NULL)")],
      uniques=[("principal_uq", "principal_id")])

# ------------------------------------------------------------------- audit --
table(S, "audit_event", LEDGER, TENANT_OPT, pk="audit_id", time_col="occurred_at", omit=["created_by"],
      partition_by="occurred_at", spec="SEC-004; SEC-007/008 sensitive-read audit; §Audit trail and security",
      doc="Append-only business and security audit. before/after_data never contain restricted columns; "
          "the hashes prove what changed without copying restricted data.",
      cols=[
          col("actor_type", "text", check="actor_type IN ('Staff', 'Guest', 'Service', 'Device', 'System')"),
          col("actor_principal_id", "uuid", null=True),
          col("on_behalf_of_principal_id", "uuid", null=True, doc="delegated authority or AI acting for a principal"),
          col("purpose", "text", null=True),
          col("action", "text"),
          col("entity_type", "text"),
          col("entity_id", "uuid"),
          col("entity_version", "integer", null=True),
          col("before_hash", HASH, null=True),
          col("after_hash", HASH, null=True),
          col("before_data", "jsonb", null=True),
          col("after_data", "jsonb", null=True),
          col("changed_fields", "text[]", null=True),
          col("conflict_codes", "text[]", default="'{}'"),
          col("reason_code", "text", null=True),
          col("reason_text", "text", null=True),
          col("authorization_decision", "jsonb", null=True,
              doc="OpenFGA check: {relation, object, model_id, allowed}"),
          col("db_role", "text", default="current_user"),
      ],
      dropped=[("actor_id", "actor_principal_id (uuid, the principal)"),
               ("operation_audit (table)", "merged: one audit entity, not one per use case")],
      indexes=[
          "audit_event_entity_ix ON core.audit_event (tenant_id, entity_type, entity_id, occurred_at DESC)",
          "audit_event_scope_ix ON core.audit_event (tenant_id, property_id, occurred_at DESC)",
          "audit_event_actor_ix ON core.audit_event (tenant_id, actor_principal_id, occurred_at DESC)",
          "audit_event_correlation_ix ON core.audit_event (correlation_id)",
      ])

table(S, "audit_seal", LEDGER, TENANT, key=None, handoff="new", omit=["correlation_id"],
      spec="§24 Audit: tamper-evident",
      doc="Periodic digest over a tenant's audit rows, chained to the previous seal. Recomputing a period "
          "and comparing proves the rows were not altered or removed.",
      cols=[
          col("period_start", "timestamptz"),
          col("period_end", "timestamptz"),
          col("row_count", "bigint", check="row_count >= 0"),
          col("digest", HASH),
          col("previous_digest", HASH, null=True),
      ],
      checks=[("period_forward", "period_end > period_start")],
      uniques=[("period_uq", "tenant_id, period_start")])

# ---------------------------------------------------- messaging plumbing --
table(S, "event_outbox", CHILD, TENANT_OPT, pk="event_id", partition_by="occurred_at",
      omit=["created_at", "created_by", "updated_by"], grants="app",
      spec="NFR-001; §26 Integration operations; FGA tuple sync",
      doc="Written in the same transaction as the change it announces. Payloads carry no secrets, "
          "payment credentials or clinical content (spec: logs and events must omit them).",
      cols=[
          col("occurred_at", "timestamptz", default="now()"),
          col("event_type", "text"),
          col("schema_version", "integer", default="1"),
          col("aggregate_type", "text"),
          col("aggregate_id", "uuid"),
          col("aggregate_version", "integer", null=True),
          col("payload", "jsonb"),
          col("causation_id", "text", null=True),
          col("published_at", "timestamptz", null=True),
          col("attempt_count", "integer", default="0", check="attempt_count >= 0"),
          col("next_attempt_at", "timestamptz", default="now()"),
          col("last_error", "text", null=True),
      ],
      dropped=[("payload_cipher/key_version", "events carry no restricted content, so nothing to encrypt")],
      indexes=["event_outbox_pending_ix ON core.event_outbox (next_attempt_at) WHERE published_at IS NULL"])

table(S, "event_inbox", LEDGER, TENANT, key=None, time_col="processed_at", omit=["created_by"],
      spec="exactly-once consumption",
      cols=[
          col("consumer_name", "text"),
          col("event_id", "uuid"),
          col("outcome", "text", check="outcome IN ('Processed', 'Skipped', 'Failed')"),
      ],
      uniques=[("consumer_event_uq", "tenant_id, consumer_name, event_id")])

table(S, "idempotency_record", CHILD, PROPERTY, spec="API-001",
      doc="Claim-then-complete. Scoped by tenant, property, operation and route.",
      cols=[
          col("operation", "text"),
          col("route", "text"),
          col("idempotency_key", "text", check="length(idempotency_key) BETWEEN 8 AND 200"),
          col("request_hash", HASH),
          col("request_hash_format", "text", default="'sha256-canonical-json-v1'"),
          col("completed", "boolean", default="false"),
          col("response_code", "integer", null=True),
          col("response_cipher", "bytea", null=True, restricted=True),
          col("key_version", "text", null=True),
          col("expires_at", "timestamptz"),
      ],
      checks=[("cipher_has_key", "(response_cipher IS NULL) = (key_version IS NULL)")],
      uniques=[("key_uq", "tenant_id, property_id, operation, route, idempotency_key")],
      indexes=["idempotency_record_expiry_ix ON core.idempotency_record (expires_at)"])

table(S, "external_mapping", MASTER, TENANT_OPT, key=None, handoff="completed",
      spec="MCI-004 identity and external mappings; §PMS and enterprise POS guest ingestion",
      doc="Local entity <-> external system id. source_system/source_key are the external side.",
      statuses=["Active", "Retired", "ReviewRequired"],
      cols=[
          col("entity_type", "text"),
          col("local_id", "uuid"),
          col("external_property_reference", "text", null=True),
          col("source_version", "bigint", null=True),
          col("snapshot_cipher", "bytea", null=True, restricted=True),
          col("encryption_key_version", "text", null=True),
      ],
      checks=[("is_external", "source_system <> 'Spa' AND source_key IS NOT NULL"),
              ("cipher_has_key", "(snapshot_cipher IS NULL) = (encryption_key_version IS NULL)")],
      uniques=[("external_uq", "NULLS NOT DISTINCT (tenant_id, source_system, entity_type, external_property_reference, source_key)")],
      indexes=["external_mapping_local_ix ON core.external_mapping (tenant_id, entity_type, local_id)"],
      dropped=[("local_guest_id", "generalised to local_id + entity_type")])

# --------------------------------------------------------------- retention --
table(S, "data_retention_rule", MASTER, TENANT_OPT, key=None, handoff="completed",
      spec="SEC-005; §Authorization and retention (five data classes)",
      statuses=["Draft", "Approved", "Active", "Retired"],
      cols=[
          col("data_class", "text", check="data_class IN ('Operational', 'Personal', 'Commercial', "
                                          "'CommunityRestricted', 'ClinicalRestricted')"),
          col("entity_table", "text"),
          col("trigger_column", "text"),
          col("retention_interval", "interval", check="retention_interval > interval '0'"),
          col("retention_action", "text", check="retention_action IN ('Delete', 'Redact', 'Deidentify', 'Archive')"),
          col("jurisdiction", "text", null=True),
          col("legal_basis", "text", null=True),
          col("owner_role", "text"),
          col("approved_by", "uuid", null=True),
          col("approved_at", "timestamptz", null=True),
      ],
      checks=[("active_is_approved", "status NOT IN ('Approved', 'Active') OR approved_by IS NOT NULL")],
      dropped=[("definition_json", "every field is now a typed column")])

table(S, "legal_hold", AGGREGATE, TENANT_OPT, key=None, handoff="completed", spec="SEC-005 legal hold",
      statuses=["Active", "Released"],
      cols=[
          col("entity_table", "text"),
          col("entity_key", "text"),
          col("hold_scope", "text", check="hold_scope IN ('Entity', 'Guest', 'Matter')"),
          col("reason", "text"),
          col("starts_at", "timestamptz", default="now()"),
          col("ends_at", "timestamptz", null=True),
          col("released_at", "timestamptz", null=True),
          col("released_by", "uuid", null=True),
      ],
      checks=[("release_complete", "(status = 'Released') = (released_at IS NOT NULL AND released_by IS NOT NULL)")],
      indexes=["legal_hold_entity_ix ON core.legal_hold (tenant_id, entity_table, entity_key) WHERE status = 'Active'"],
      dropped=[("effective_from/effective_to", "starts_at/ends_at are the hold's own period")])
