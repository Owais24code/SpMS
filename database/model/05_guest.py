"""guest: tenant-wide identity (IDN-005), contact, household, delegation, consent, privacy."""
from dsl import *

S = "guest"

table(S, "guest", AGGREGATE, TENANT, key=None,
      spec="§43 Guest identity, household and delegated authority; IDN-001/002/004/005",
      doc="One person, recognised across the tenant's properties. Never merged silently (IDN-001): "
          "a merge sets merged_into_guest_id and is reversible through guest_merge_case.",
      statuses=["Active", "Restricted", "Merged", "Deceased", "Erased"],
      cols=[
          col("principal_id", "uuid", null=True, fk="core.principal", doc="set once the guest signs in (magic link)"),
          col("home_property_id", "uuid", null=True, fk="core.property.property_id"),
          col("legal_first_name", "text", null=True),
          col("legal_last_name", "text", null=True),
          col("preferred_name", "text", null=True),
          col("birth_date", "date", null=True, restricted=True, doc="minor/guardian rules (IDN-004)"),
          col("locale", "text", null=True),
          col("merged_into_guest_id", "uuid", null=True, fk="guest.guest.guest_id"),
      ],
      checks=[("merge_consistent", "(status = 'Merged') = (merged_into_guest_id IS NOT NULL)"),
              ("not_self_merged", "merged_into_guest_id IS DISTINCT FROM guest_id")],
      uniques=[("principal_uq", "principal_id")],
      indexes=[
          "guest_name_trgm_ix ON guest.guest USING gin ((coalesce(legal_last_name, '') || ' ' || coalesce(legal_first_name, '') "
          "|| ' ' || coalesce(preferred_name, '')) gin_trgm_ops) WHERE status IN ('Active', 'Restricted')",
      ],
      dropped=[("contact_cipher", "guest_contact_point (one entity per contact point)")])

table(S, "guest_contact_point", AGGREGATE, TENANT, key=None, handoff="completed",
      spec="UX-002 universal search by phone/email; SEC-011 verified contact methods",
      doc="lookup_hash is an HMAC of the normalised value (key in Key Vault), so search works without decrypting.",
      statuses=["Active", "Retired"], effective=False,
      cols=[
          col("guest_id", "uuid", fk="guest.guest"),
          col("contact_type", "text", check="contact_type IN ('Email', 'Mobile', 'Phone', 'Address')"),
          col("contact_cipher", "bytea", restricted=True),
          col("key_version", "text"),
          col("lookup_hash", HASH),
          col("display_hint", "text", doc="masked form for screens, e.g. j***@example.com"),
          col("is_primary", "boolean", default="false"),
          col("verified_at", "timestamptz", null=True),
      ],
      indexes=["guest_contact_point_lookup_ix ON guest.guest_contact_point (tenant_id, contact_type, lookup_hash) WHERE status = 'Active'",
               "UNIQUE guest_contact_point_primary_uq ON guest.guest_contact_point (tenant_id, guest_id, contact_type) "
               "WHERE is_primary AND status = 'Active'"],
)

table(S, "guest_household", AGGREGATE, TENANT, key=None, handoff="completed", spec="§43 Relationships: household",
      statuses=["Active", "Dissolved"],
      cols=[
          col("household_name", "text"),
          col("primary_guest_id", "uuid", fk="guest.guest.guest_id"),
      ],
      dropped=[("guest_id/starts_at/ends_at", "membership is guest_household_member")])

table(S, "guest_household_member", MASTER, TENANT, key=None, handoff="new",
      statuses=["Active", "Ended"],
      cols=[
          col("guest_household_id", "uuid", fk="guest.guest_household"),
          col("guest_id", "uuid", fk="guest.guest"),
          col("member_role", "text", check="member_role IN ('Primary', 'Adult', 'Minor', 'Dependent')"),
      ],
      uniques=[("member_uq", "tenant_id, guest_household_id, guest_id, effective_from")])

table(S, "guest_relationship", MASTER, TENANT, key=None, handoff="completed", spec="§43 Relationships",
      statuses=["Active", "Ended"],
      cols=[
          col("guest_id", "uuid", fk="guest.guest"),
          col("related_guest_id", "uuid", fk="guest.guest.guest_id"),
          col("relationship_type", "text", check="relationship_type IN ('SpousePartner', 'ParentGuardian', 'Child', "
                                                 "'Dependent', 'Assistant', 'Organizer', 'Payer', 'Recipient', 'EmergencyContact')"),
      ],
      checks=[("not_self", "guest_id <> related_guest_id")],
      extra_sql="""
ALTER TABLE guest.guest_relationship ADD CONSTRAINT guest_relationship_no_overlap
    EXCLUDE USING gist (tenant_id WITH =, guest_id WITH =, related_guest_id WITH =, relationship_type WITH =,
                        tstzrange(effective_from, coalesce(effective_to, 'infinity'), '[)') WITH &&)
    WHERE (status = 'Active');
""")

table(S, "guest_preference", AGGREGATE, TENANT_OPT, pk="preference_id", key=None, source_cols=False,
      spec="IDN-005 property-specific preferences; DEC-004 (health stays in intake)",
      statuses=["Active", "Expired", "Retired"],
      cols=[
          col("guest_id", "uuid", fk="guest.guest"),
          col("category", "text"),
          col("value_json", "jsonb"),
          col("strength", "text", check="strength IN ('Required', 'Preferred', 'Avoid')"),
          col("source", "text", check="source IN ('Guest', 'Staff', 'Import')"),
          col("privacy_class", "text", check="privacy_class IN ('Operational', 'Personal')",
              doc="clinical data is refused here by design; it belongs to intake"),
          col("observed_at", "timestamptz", default="now()"),
          col("expires_at", "timestamptz", null=True),
      ])

table(S, "guest_merge_case", AGGREGATE, TENANT, key=None, handoff="completed",
      spec="IDN-001; Closure_and_Acceptance/IDENTITY_MERGE_SPLIT_CONTROL.md",
      statuses=["Candidate", "Approved", "Merged", "Rejected", "Split"],
      cols=[
          col("surviving_guest_id", "uuid", fk="guest.guest.guest_id"),
          col("duplicate_guest_id", "uuid", fk="guest.guest.guest_id"),
          col("match_signals", "jsonb", default="'{}'"),
          col("confidence", "numeric(7,6)", check="confidence BETWEEN 0 AND 1"),
          col("decision_reason", "text", null=True),
          col("reviewed_by", "uuid", null=True),
          col("reviewed_at", "timestamptz", null=True),
          col("merged_at", "timestamptz", null=True),
          col("split_at", "timestamptz", null=True),
          col("split_reason", "text", null=True),
      ],
      checks=[("distinct_guests", "surviving_guest_id <> duplicate_guest_id"),
              ("human_reviewed", "status NOT IN ('Approved', 'Merged') OR reviewed_by IS NOT NULL"),
              ("split_explained", "status <> 'Split' OR (split_at IS NOT NULL AND split_reason IS NOT NULL)")],
      dropped=[("guest_id", "surviving_guest_id/duplicate_guest_id")])

table(S, "privacy_alias", MASTER, TENANT, key=None, handoff="completed", spec="IDN-002",
      doc="Display alias and public queue identifier, exposed instead of legal name where purpose allows.",
      statuses=["Active", "Retired"],
      cols=[
          col("guest_id", "uuid", fk="guest.guest"),
          col("alias_type", "text", check="alias_type IN ('DisplayAlias', 'PublicQueueId')"),
          col("alias_value", "text"),
      ],
      indexes=["UNIQUE privacy_alias_value_uq ON guest.privacy_alias (tenant_id, alias_type, alias_value) WHERE status = 'Active'"],
)

table(S, "delegated_authority", MASTER, TENANT, key=None, handoff="completed",
      spec="IDN-003 (explicit scope, actions, financial limit, visibility, expiry, revocation, evidence)",
      doc="A delegate acts only within what is written here and never inherits health data by relationship. "
          "Mirrored to OpenFGA as a conditional tuple (time_bound).",
      statuses=["Active", "Revoked", "Expired"],
      cols=[
          col("guest_id", "uuid", fk="guest.guest", doc="the guest whose affairs are delegated"),
          col("delegate_guest_id", "uuid", null=True, fk="guest.guest.guest_id"),
          col("delegate_principal_id", "uuid", null=True, fk="core.principal"),
          col("guest_relationship_id", "uuid", null=True, fk="guest.guest_relationship"),
          col("allowed_actions", "text[]",
              check="allowed_actions <@ ARRAY['Book', 'Cancel', 'Reschedule', 'Pay', 'ViewItinerary', 'CompleteIntake']::text[] "
                    "AND cardinality(allowed_actions) > 0"),
          col("property_ids", "uuid[]", null=True, doc="NULL = every property of the tenant"),
          col("financial_limit_minor", "bigint", null=True, check="financial_limit_minor >= 0"),
          CURRENCY(null=True),
          col("information_visibility", "text", check="information_visibility IN ('ItineraryOnly', 'Standard')",
              doc="there is deliberately no level that includes intake or treatment notes"),
          col("evidence_reference", "text"),
          col("revoked_at", "timestamptz", null=True),
          col("revoked_by", "uuid", null=True),
          col("fga_synced_at", "timestamptz", null=True),
      ],
      checks=[("one_delegate", "num_nonnulls(delegate_guest_id, delegate_principal_id) = 1"),
              ("not_self", "delegate_guest_id IS DISTINCT FROM guest_id"),
              ("limit_has_currency", "(financial_limit_minor IS NULL) = (currency_code IS NULL)"),
              ("revocation_complete", "(status = 'Revoked') = (revoked_at IS NOT NULL AND revoked_by IS NOT NULL)"),
              ("expires", "effective_to IS NOT NULL")])

table(S, "consent_record", AGGREGATE, TENANT_OPT, pk="consent_id", key=None, source_cols=False, effective=False,
      spec="§24 Privacy purpose/consent; DEC-008; IDN-004 guardian consent",
      doc="Consent evidence is immutable once written; only revocation may change afterwards (trigger-enforced).",
      statuses=["Active", "Revoked", "Expired"],
      cols=[
          col("guest_id", "uuid", fk="guest.guest"),
          col("granted_by_guest_id", "uuid", null=True, fk="guest.guest.guest_id", doc="guardian granting for a minor"),
          col("purpose", "text", check="purpose IN ('Transactional', 'Marketing', 'HealthIntake', 'Photography', "
                                       "'MinorService', 'DataSharing', 'Profiling')"),
          col("channel", "text", null=True, check="channel IN ('Email', 'Sms', 'WhatsApp', 'Push', 'Phone')"),
          col("template_id", "text"),
          col("template_version", "integer"),
          col("evidence_cipher", "bytea", restricted=True),
          col("key_version", "text"),
          col("effective_at", "timestamptz", default="now()"),
          col("expires_at", "timestamptz", null=True),
          col("revoked_at", "timestamptz", null=True),
          col("revoked_by", "uuid", null=True),
      ],
      checks=[("revocation_complete", "(status = 'Revoked') = (revoked_at IS NOT NULL)")],
      indexes=["consent_record_purpose_ix ON guest.consent_record (tenant_id, guest_id, purpose, channel) WHERE status = 'Active'"],
      extra_sql="""
CREATE FUNCTION guest.consent_record_immutable() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF (NEW.guest_id, NEW.granted_by_guest_id, NEW.purpose, NEW.channel, NEW.template_id, NEW.template_version,
        NEW.evidence_cipher, NEW.key_version, NEW.effective_at, NEW.property_id, NEW.tenant_id)
       IS DISTINCT FROM
       (OLD.guest_id, OLD.granted_by_guest_id, OLD.purpose, OLD.channel, OLD.template_id, OLD.template_version,
        OLD.evidence_cipher, OLD.key_version, OLD.effective_at, OLD.property_id, OLD.tenant_id)
       AND current_setting('spms.redacting', true) IS DISTINCT FROM 'on' THEN
        RAISE EXCEPTION 'guest.consent_record evidence is immutable; revoke and record a new consent'
            USING ERRCODE = '42501';
    END IF;
    RETURN NEW;
END $$;
CREATE TRIGGER consent_record_immutable BEFORE UPDATE ON guest.consent_record
    FOR EACH ROW EXECUTE FUNCTION guest.consent_record_immutable();
""")

table(S, "privacy_request", AGGREGATE, TENANT, key=None, handoff="completed",
      spec="IDN-006; §AI lawful operation, manager overrides and guest erasure",
      statuses=["Received", "Verified", "InProgress", "Fulfilled", "Rejected", "OnHold"],
      cols=[
          col("guest_id", "uuid", fk="guest.guest"),
          col("request_type", "text", check="request_type IN ('Access', 'Export', 'Correction', 'Restriction', "
                                            "'Deletion', 'ConsentWithdrawal')"),
          col("jurisdiction", "text", null=True),
          col("requested_at", "timestamptz", default="now()"),
          col("due_at", "timestamptz"),
          col("verified_at", "timestamptz", null=True),
          col("verified_by", "uuid", null=True),
          col("fulfilled_at", "timestamptz", null=True),
          col("rejection_reason", "text", null=True),
      ],
      checks=[("fulfilled_complete", "(status = 'Fulfilled') = (fulfilled_at IS NOT NULL)"),
              ("rejected_explained", "status <> 'Rejected' OR rejection_reason IS NOT NULL")],
      dropped=[("effective_from/effective_to", "requested_at/due_at")])

table(S, "guest_magic_link", CHILD, TENANT, handoff="new",
      spec="SEC-010 (signed, short-lived, revocable, purpose-limited); SEC-011",
      doc="Only the SHA-256 of the token is stored. Single use: guest.resolve_magic_link consumes it atomically.",
      cols=[
          col("guest_id", "uuid", fk="guest.guest"),
          col("principal_id", "uuid", fk="core.principal"),
          col("guest_contact_point_id", "uuid", fk="guest.guest_contact_point",
              doc="the verified contact the link was sent to"),
          col("token_hash", "bytea", check="octet_length(token_hash) = 32"),
          col("purpose", "text", check="purpose IN ('SignIn', 'ManageBooking', 'CompleteIntake', 'Pay')"),
          col("scope_entity_type", "text", null=True),
          col("scope_entity_id", "uuid", null=True),
          col("issued_at", "timestamptz", default="now()"),
          col("expires_at", "timestamptz"),
          col("consumed_at", "timestamptz", null=True),
          col("revoked_at", "timestamptz", null=True),
      ],
      checks=[("expiry_forward", "expires_at > issued_at"),
              ("scope_pair", "(scope_entity_type IS NULL) = (scope_entity_id IS NULL)")],
      uniques=[("token_uq", "token_hash")],
      indexes=["guest_magic_link_expiry_ix ON guest.guest_magic_link (expires_at) WHERE consumed_at IS NULL"])
