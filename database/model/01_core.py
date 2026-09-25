"""core: tenancy, identity, governed settings, code lists, audit, outbox, idempotency, legal hold."""
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
          col("data_region", "text", doc="data residency region"),
      ],
      uniques=[("code_uq", "code")],
      extra_sql="""
ALTER TABLE core.tenant ENABLE ROW LEVEL SECURITY;
ALTER TABLE core.tenant FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_scope ON core.tenant USING (tenant_id = (SELECT core.current_tenant_id()));
""")

table(S, "property", AGGREGATE, TENANT, key=None,
      spec="§53.2 Operating modes; DEC-001; DEC-011",
      doc="A spa property. operating_mode is the default; capability_ownership decides per capability and time.",
      statuses=["Active", "Inactive", "Closed"],
      cols=[
          col("code", "text"),
          col("name", "text"),
          col("timezone", "text", doc="IANA zone; the board and business day are always read in it"),
          CURRENCY(),
          col("locale", "text", default="'en-US'"),
          col("operating_mode", "text", default="'Standalone'",
              check="operating_mode IN ('Standalone', 'MarqueeIntegrated')"),
          col("opening_hours", "jsonb", default="'[]'",
              check="jsonb_typeof(opening_hours) = 'array'",
              doc='weekly hours: [{"day": 1, "opens": "09:00", "closes": "21:00"}], ISO day 1 = Monday'),
          col("room_turnover_minutes", "integer", default="15", check="room_turnover_minutes >= 0"),
          col("provider_transition_minutes", "integer", default="10", check="provider_transition_minutes >= 0"),
      ],
      uniques=[("code_uq", "tenant_id, code")],
      dropped=[("record_key", "code"), ("property_operating_hours (table)", "opening_hours jsonb: read whole, never queried by day")])

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

table(S, "principal_login", AGGREGATE, TENANT, key=None, source_cols=False,
      handoff="new (replaces workforce.provider_identity and core.service_identity)",
      spec="§24 OIDC/SSO, MFA, service accounts; §Security and audit (no password rows)",
      doc="An external identity that signs in as a principal: Entra user (oid) or client-credentials app.",
      statuses=["Active", "Disabled", "Locked"],
      cols=[
          col("principal_id", "uuid", fk="core.principal"),
          col("login_type", "text", check="login_type IN ('EntraUser', 'EntraApplication')"),
          col("idp_issuer", "text"),
          col("idp_subject", "text", doc="Entra object id (oid) or application object id"),
          col("username", "text", null=True),
          col("mfa_required", "boolean", default="true"),
          col("credential_rotated_at", "timestamptz", null=True),
          col("last_authenticated_at", "timestamptz", null=True),
      ],
      uniques=[("subject_uq", "idp_issuer, idp_subject")])

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

# ------------------------------------------------------ authority & config --
table(S, "capability_ownership", MASTER, PROPERTY, pk="ownership_id", key=None, effective=False,
      source_cols=False, spec="DEC-001 (APPROVED critical hybrid rule); MCI-001",
      doc="Exactly one authority per capability, property and instant. Kept typed (not a setting) because "
          "the database must enforce it.",
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
      dropped=[("approval_id", "approved_by/approved_at")],
      extra_sql="""
ALTER TABLE core.capability_ownership ADD CONSTRAINT capability_ownership_single_authority
    EXCLUDE USING gist (tenant_id WITH =, property_id WITH =, capability_code WITH =, effective_range WITH &&)
    WHERE (status IN ('Approved', 'Active'));
""")

table(S, "setting", MASTER, TENANT_OPT, key=None, source_cols=False,
      handoff="new (replaces feature_flag, policy_definition, policy_version, configuration_version, data_retention_rule)",
      spec="§Configurable policies and accountability; SEC-005 retention; SEC-014 segregation of duties",
      doc="Governed, effective-dated configuration. One active value per key, scope and instant. A change is a new "
          "row that someone other than its author approves; the old row becomes Superseded. Keys are namespaced: "
          "feature.*, policy.cancellation, policy.deposit, retention.<data_class>, messaging.quiet_hours ...",
      statuses=["Proposed", "Approved", "Active", "Superseded", "Rejected"],
      cols=[
          col("setting_key", "text", check="setting_key ~ '^[a-z][a-z0-9_]*(\\.[a-z0-9_]+)+$'"),
          col("value_json", "jsonb"),
          col("deployment_scope", "text", default="'Production'",
              check="deployment_scope IN ('Training', 'Pilot', 'Production')"),
          col("reason", "text", null=True),
          col("approved_by", "uuid", null=True),
          col("approved_at", "timestamptz", null=True),
      ],
      checks=[("active_is_approved", "status NOT IN ('Approved', 'Active') OR approved_by IS NOT NULL"),
              ("approver_not_author", "approved_by IS NULL OR approved_by IS DISTINCT FROM created_by")],
      extra_sql="""
ALTER TABLE core.setting ADD CONSTRAINT setting_single_active
    EXCLUDE USING gist (tenant_id WITH =, (coalesce(property_id, '00000000-0000-0000-0000-000000000000'::uuid)) WITH =,
                        setting_key WITH =, deployment_scope WITH =,
                        tstzrange(effective_from, coalesce(effective_to, 'infinity'), '[)') WITH &&)
    WHERE (status IN ('Approved', 'Active'));
""")

table(S, "code_list", MASTER, TENANT_OPT, key=None, source_cols=False, effective=False,
      handoff="new (replaces reason_code, department, tender_definition, provider_license_type, revenue_center_ref)",
      spec="CON override reasons; tenders; §Provider license catalog (37 types); §Income accounts",
      doc="Simple enumerations: (list_code, code) -> label + attributes. Referencing columns hold the code; "
          "commands validate it. Anything with money or enforcement semantics stays a typed table.",
      statuses=["Active", "Retired"],
      cols=[
          col("list_code", "text", check="list_code IN ('ReasonCode', 'Department', 'Tender', 'LicenseType', "
                                         "'RevenueCenter', 'ServiceCategory', 'ItemCategory')"),
          col("code", "text"),
          col("label", "text"),
          col("sort_order", "integer", default="0"),
          col("attributes", "jsonb", default="'{}'",
              doc="list-specific: ReasonCode {domain, requires_note}; Tender {tender_type}; LicenseType {issuer, jurisdiction_dependent}"),
      ],
      uniques=[("code_uq", "NULLS NOT DISTINCT (tenant_id, property_id, list_code, code)")])

# ------------------------------------------------------------------- audit --
table(S, "audit_event", LEDGER, TENANT_OPT, pk="audit_id", time_col="occurred_at", omit=["created_by"],
      partition_by="occurred_at", spec="SEC-004; SEC-007/008 sensitive-read audit; DEC-005",
      doc="Append-only business and security audit, and the history of every entity (status changes, visit events, "
          "service and policy changes). before/after_data never contain restricted columns.",
      cols=[
          col("actor_type", "text", check="actor_type IN ('Staff', 'Guest', 'Service', 'Device', 'System')"),
          col("actor_principal_id", "uuid", null=True),
          col("on_behalf_of_principal_id", "uuid", null=True, doc="delegated authority or AI acting for a principal"),
          col("purpose", "text", null=True),
          col("action", "text"),
          col("entity_type", "text"),
          col("entity_id", "uuid"),
          col("entity_version", "integer", null=True),
          col("from_status", "text", null=True),
          col("to_status", "text", null=True),
          col("before_hash", HASH, null=True),
          col("after_hash", HASH, null=True),
          col("before_data", "jsonb", null=True),
          col("after_data", "jsonb", null=True),
          col("changed_fields", "text[]", null=True),
          col("conflict_codes", "text[]", default="'{}'"),
          col("reason_code", "text", null=True),
          col("reason_text", "text", null=True),
          col("authorization_decision", "jsonb", null=True, doc="OpenFGA check: {relation, object, model_id, allowed}"),
          col("db_role", "text", default="current_user"),
      ],
      dropped=[("operation_audit, appointment_status_history, visit_event (tables)", "one history for every entity"),
               ("actor_id", "actor_principal_id")],
      indexes=[
          "audit_event_entity_ix ON core.audit_event (tenant_id, entity_type, entity_id, occurred_at DESC)",
          "audit_event_scope_ix ON core.audit_event (tenant_id, property_id, occurred_at DESC)",
          "audit_event_actor_ix ON core.audit_event (tenant_id, actor_principal_id, occurred_at DESC)",
          "audit_event_correlation_ix ON core.audit_event (correlation_id)",
      ])

table(S, "audit_seal", LEDGER, TENANT, key=None, handoff="new", omit=["correlation_id"],
      spec="§24 Audit: tamper-evident",
      doc="Periodic digest over a tenant's audit rows, chained to the previous seal.",
      cols=[
          col("period_start", "timestamptz"),
          col("period_end", "timestamptz"),
          col("row_count", "bigint", check="row_count >= 0"),
          col("digest", HASH),
          col("previous_digest", HASH, null=True),
      ],
      checks=[("period_forward", "period_end > period_start")],
      uniques=[("period_uq", "tenant_id, period_start")])

# ---------------------------------------------------------------- plumbing --
table(S, "event_outbox", CHILD, TENANT_OPT, pk="event_id", partition_by="occurred_at",
      omit=["created_at", "created_by", "updated_by"],
      spec="NFR-001; §26 Integration operations; FGA tuple sync",
      doc="Written in the same transaction as the change it announces. No secrets, card data or clinical content.",
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
      dropped=[("payload_cipher/key_version", "events carry no restricted content")],
      indexes=["event_outbox_pending_ix ON core.event_outbox (next_attempt_at) WHERE published_at IS NULL"])

table(S, "event_inbox", LEDGER, TENANT, key=None, time_col="processed_at", omit=["created_by"],
      spec="exactly-once consumption of inbound integration events",
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
      spec="MCI-004 identity and external mappings; PMS/POS ingestion (Marquee-Integrated mode)",
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
      indexes=["external_mapping_local_ix ON core.external_mapping (tenant_id, entity_type, local_id)"])

table(S, "legal_hold", AGGREGATE, TENANT_OPT, key=None, handoff="completed", spec="SEC-005 legal hold",
      doc="Kept typed: retention and erasure jobs must be able to find active holds with one indexed query.",
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
      indexes=["legal_hold_entity_ix ON core.legal_hold (tenant_id, entity_table, entity_key) WHERE status = 'Active'"])
