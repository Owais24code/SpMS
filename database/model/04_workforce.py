"""workforce: staff, identity links, roles, qualifications, credentials, schedules."""
from dsl import *

S = "workforce"

ROLES = ["guest", "provider", "front_desk", "spa_manager", "hr_compliance", "finance", "platform_admin",
         "configuration_approver", "scheduler", "housekeeping", "inventory_manager", "marketing", "support",
         "operations_analyst", "executive", "release_manager", "security_admin", "integration_service"]

table(S, "staff", AGGREGATE, TENANT, key=None, source_cols=False,
      spec="§Spa service provider and small HR module; §53.2 Staff management",
      doc="Operational staff profile, broadly visible. HR detail lives in spa_service_provider.",
      cols=[
          col("principal_id", "uuid", null=True, fk="core.principal"),
          col("home_property_id", "uuid", null=True, fk="core.property.property_id"),
          col("department_id", "uuid", null=True, fk="core.department"),
          col("preferred_name", "text"),
          col("employment_status", "text", default="'Active'",
              check="employment_status IN ('Pending', 'Active', 'OnLeave', 'Suspended', 'Terminated')"),
          col("hris_system", "text", null=True),
          col("hris_worker_id", "text", null=True),
          col("payroll_system", "text", null=True),
          col("payroll_worker_id", "text", null=True),
      ],
      uniques=[("principal_uq", "principal_id"),
               ("hris_uq", "tenant_id, hris_system, hris_worker_id")],
      dropped=[("status", "employment_status is this entity's lifecycle")])

table(S, "staff_property", MASTER, PROPERTY, key=None, handoff="new",
      spec="§53.2 Staff management; RLS and OpenFGA property membership",
      statuses=["Active", "Ended"],
      cols=[
          col("staff_id", "uuid", fk="workforce.staff"),
          col("bookable", "boolean", default="true"),
      ],
      extra_sql="""
ALTER TABLE workforce.staff_property ADD CONSTRAINT staff_property_no_overlap
    EXCLUDE USING gist (tenant_id WITH =, property_id WITH =, staff_id WITH =,
                        tstzrange(effective_from, coalesce(effective_to, 'infinity'), '[)') WITH &&)
    WHERE (status = 'Active');
""")

table(S, "spa_service_provider", AGGREGATE, TENANT, key=None, source_cols=False,
      spec="§What belongs on file; SEC-007",
      doc="HR profile. Personal contact and address fields are HR-restricted (hr_compliance, or the worker).",
      cols=[
          col("staff_id", "uuid", fk="workforce.staff"),
          col("employee_number", "text"),
          col("first_name", "text"),
          col("middle_name", "text", null=True),
          col("last_name", "text"),
          col("work_email", "text", null=True),
          col("personal_email", "text", null=True, restricted=True),
          col("mobile_phone", "text", null=True, restricted=True),
          col("work_phone", "text", null=True),
          col("address_line1", "text", null=True, restricted=True),
          col("address_line2", "text", null=True, restricted=True),
          col("city", "text", null=True, restricted=True),
          col("region", "text", null=True, restricted=True),
          col("postal_code", "text", null=True, restricted=True),
          col("country_code", "char(2)", null=True),
          col("worker_type", "text", check="worker_type IN ('Employee', 'Contractor', 'Agency')"),
          col("job_title", "text"),
          col("supervisor_staff_id", "uuid", null=True, fk="workforce.staff.staff_id"),
          col("hire_date", "date", null=True),
          col("end_date", "date", null=True),
          col("languages", "text[]", default="'{}'"),
          col("public_bio", "text", null=True),
          col("public_profile_consent", "boolean", default="false"),
      ],
      uniques=[("staff_uq", "staff_id"), ("employee_number_uq", "tenant_id, employee_number")],
      checks=[("dates_ordered", "end_date IS NULL OR hire_date IS NULL OR end_date >= hire_date")],
      dropped=[("home_property_id", "on staff"), ("preferred_name", "on staff (one owner per concept)"),
               ("department text", "staff.department_id"), ("timezone", "derived from the property")])

table(S, "provider_identity", AGGREGATE, TENANT, key=None, source_cols=False,
      spec="§Security and audit (provider identity rows never contain a password); §24 OIDC/SSO",
      doc="Links a staff member's principal to their Entra ID subject. The API resolves (issuer, subject) here.",
      statuses=["Active", "Disabled", "Locked"],
      cols=[
          col("staff_id", "uuid", fk="workforce.staff"),
          col("principal_id", "uuid", fk="core.principal"),
          col("username", "text"),
          col("idp_issuer", "text"),
          col("idp_subject", "text", doc="Entra object id (oid)"),
          col("mfa_required", "boolean", default="true"),
          col("last_authenticated_at", "timestamptz", null=True),
          col("last_identity_sync_at", "timestamptz", null=True),
          col("external_person_ref", "text", null=True),
      ],
      uniques=[("subject_uq", "idp_issuer, idp_subject"), ("staff_uq", "staff_id"), ("principal_uq", "principal_id")],
      dropped=[("account_state", "status")])

table(S, "provider_license_type", MASTER, GLOBAL, pk="license_type_code", pk_type="text", key=None,
      source_cols=False, spec="§Provider license and credential catalog (37 types)",
      statuses=["Active", "Retired"],
      cols=[
          col("name", "text"),
          col("category", "text"),
          col("typical_issuer", "text"),
          col("jurisdiction_dependent", "boolean", default="true"),
          col("usage_notes", "text"),
          col("source_url", "text"),
          col("reviewed_on", "date"),
      ])

table(S, "staff_document", AGGREGATE, TENANT, pk="document_id", key=None, source_cols=False,
      spec="SEC-007; §53.2 document upload", doc="Metadata only; the file lives in blob storage.",
      statuses=["Active", "Superseded", "Expired"],
      cols=[
          col("staff_id", "uuid", fk="workforce.staff"),
          col("document_type", "text"),
          col("object_reference", "text", restricted=True),
          col("content_sha256", HASH),
          col("mime_type", "text"),
          col("size_bytes", "bigint", check="size_bytes > 0"),
          col("malware_scan_status", "text", default="'Pending'",
              check="malware_scan_status IN ('Pending', 'Clean', 'Infected', 'Failed')"),
          col("access_class", "text", check="access_class IN ('Operational', 'Employment', 'Screening', 'Payroll')"),
          col("data_retention_rule_id", "uuid", null=True, fk="core.data_retention_rule"),
          col("expires_on", "date", null=True),
          col("acknowledged_at", "timestamptz", null=True),
          col("template_version", "text", null=True),
      ],
      dropped=[("retention_rule text", "data_retention_rule_id")])

table(S, "credential", AGGREGATE, TENANT, key=None, source_cols=False,
      spec="§Credential rules; §53.2 expiration alerts",
      statuses=["Pending", "Verified", "Expired", "Revoked", "Rejected"],
      cols=[
          col("staff_id", "uuid", fk="workforce.staff"),
          col("license_type_code", "text", fk="workforce.provider_license_type"),
          col("jurisdiction", "text", null=True),
          col("issuer_name", "text", null=True),
          col("number_cipher", "bytea", null=True, restricted=True),
          col("number_key_version", "text", null=True),
          col("number_last4", "char(4)", null=True),
          col("issued_at", "date", null=True),
          col("expires_at", "date", null=True),
          col("renewal_due_at", "date", null=True),
          col("verified_by", "uuid", null=True),
          col("verified_at", "timestamptz", null=True),
          col("verification_reference", "text", null=True),
          col("evidence_document_id", "uuid", null=True, fk="workforce.staff_document"),
          col("restrictions", "text", null=True),
      ],
      checks=[("cipher_has_key", "(number_cipher IS NULL) = (number_key_version IS NULL)"),
              ("verified_complete", "status <> 'Verified' OR (verified_by IS NOT NULL AND verified_at IS NOT NULL)"),
              ("dates_ordered", "expires_at IS NULL OR issued_at IS NULL OR expires_at >= issued_at")],
      indexes=["credential_expiry_ix ON workforce.credential (tenant_id, expires_at) WHERE status = 'Verified'"],
      dropped=[("credential_type", "license_type_code"), ("number_vault_reference", "number_cipher (one storage mechanism)"),
               ("credential_assignment (table)", "credential.staff_id already assigns it")])

table(S, "service_qualification_requirement", MASTER, TENANT, key=None, handoff="completed",
      spec="CON-003", statuses=["Active", "Retired"],
      cols=[
          col("service_id", "uuid", fk="catalog.service"),
          col("requirement_type", "text", check="requirement_type IN ('License', 'Certification', 'Training')"),
          col("license_type_code", "text", null=True, fk="workforce.provider_license_type"),
          col("jurisdiction", "text", null=True),
      ],
      checks=[("license_named", "requirement_type = 'Training' OR license_type_code IS NOT NULL")])

table(S, "staff_qualification", MASTER, TENANT, pk="qualification_id", key=None, effective=False,
      source_cols=False, spec="CON-003 (fails closed: no row, no qualification)",
      statuses=["Active", "Suspended", "Expired", "Revoked"],
      cols=[
          col("staff_id", "uuid", fk="workforce.staff"),
          col("service_id", "uuid", fk="catalog.service"),
          col("credential_id", "uuid", null=True, fk="workforce.credential"),
          col("effective_range", "tstzrange"),
          col("granted_by", "uuid", null=True),
      ],
      extra_sql="""
ALTER TABLE workforce.staff_qualification ADD CONSTRAINT staff_qualification_no_overlap
    EXCLUDE USING gist (tenant_id WITH =, staff_id WITH =, service_id WITH =, effective_range WITH &&)
    WHERE (status = 'Active');
""")

table(S, "staff_role_assignment", MASTER, TENANT_OPT, key=None, handoff="completed",
      spec="technical/config/role_permissions.json; security.assign_role:approved",
      doc="Source of truth for OpenFGA role tuples. property_id NULL = tenant-wide role (e.g. hr_compliance).",
      statuses=["Proposed", "Active", "Revoked", "Expired"],
      cols=[
          col("staff_id", "uuid", fk="workforce.staff"),
          col("role_code", "text", check="role_code IN (" + ", ".join(f"'{r}'" for r in ROLES) + ")"),
          col("approved_by", "uuid", null=True),
          col("approved_at", "timestamptz", null=True),
          col("fga_synced_at", "timestamptz", null=True,
              doc="set when the outbox worker confirms the tuple write"),
      ],
      checks=[("active_is_approved", "status <> 'Active' OR approved_by IS NOT NULL"),
              ("approver_not_proposer", "approved_by IS NULL OR approved_by IS DISTINCT FROM created_by")],
      extra_sql="""
ALTER TABLE workforce.staff_role_assignment ADD CONSTRAINT staff_role_assignment_no_overlap
    EXCLUDE USING gist (tenant_id WITH =, (coalesce(property_id, '00000000-0000-0000-0000-000000000000'::uuid)) WITH =,
                        staff_id WITH =, role_code WITH =,
                        tstzrange(effective_from, coalesce(effective_to, 'infinity'), '[)') WITH &&)
    WHERE (status IN ('Proposed', 'Active'));
""")

table(S, "staff_leave", AGGREGATE, TENANT, key=None, handoff="completed", source_cols=False,
      statuses=["Requested", "Approved", "Rejected", "Cancelled"],
      cols=[
          col("staff_id", "uuid", fk="workforce.staff"),
          col("leave_type", "text", check="leave_type IN ('Planned', 'Unplanned', 'Training', 'Other')"),
          col("starts_at", "timestamptz"),
          col("ends_at", "timestamptz"),
          col("approved_by", "uuid", null=True),
          col("approved_at", "timestamptz", null=True),
      ],
      checks=[("range_forward", "ends_at > starts_at")],
      indexes=["staff_leave_window_ix ON workforce.staff_leave USING gist (tenant_id, staff_id, tstzrange(starts_at, ends_at, '[)')) "
               "WHERE status = 'Approved'"],
      dropped=[("requested_range", "starts_at/ends_at"), ("effective_from/effective_to", "starts_at/ends_at")])

table(S, "work_schedule", AGGREGATE, PROPERTY, key=None, handoff="completed", source_cols=False,
      spec="§53.2 availability", statuses=["Draft", "Published", "Cancelled"],
      cols=[
          col("staff_id", "uuid", fk="workforce.staff"),
          col("schedule_date", "date"),
          col("starts_at", "timestamptz"),
          col("ends_at", "timestamptz"),
          col("shift_type", "text", default="'Scheduled'", check="shift_type IN ('Scheduled', 'OnCall')"),
      ],
      checks=[("range_forward", "ends_at > starts_at")],
      dropped=[("available_range", "starts_at/ends_at"), ("timezone", "the property's")],
      extra_sql="""
-- A person is in one place at a time: no overlapping published shifts, across properties.
ALTER TABLE workforce.work_schedule ADD CONSTRAINT work_schedule_no_overlap
    EXCLUDE USING gist (tenant_id WITH =, staff_id WITH =, tstzrange(starts_at, ends_at, '[)') WITH &&)
    WHERE (status = 'Published');
""")

table(S, "background_screening", AGGREGATE, TENANT, pk="screening_id", key=None, source_cols=False,
      spec="DEC-006 (status, reference, dates, adjudication only); SEC-007",
      statuses=["Requested", "InProgress", "Completed", "Cancelled", "Expired"],
      cols=[
          col("staff_id", "uuid", fk="workforce.staff"),
          col("provider_name", "text"),
          col("provider_case_reference", "text"),
          col("package_code", "text"),
          col("jurisdiction", "text"),
          col("consent_reference", "text"),
          col("requested_at", "timestamptz"),
          col("completed_at", "timestamptz", null=True),
          col("expires_at", "timestamptz", null=True),
          col("adjudication", "text", null=True, restricted=True,
              check="adjudication IN ('Eligible', 'NotEligible', 'ReviewRequired', 'Withdrawn')"),
          col("adjudicated_by", "uuid", null=True),
          col("adjudicated_at", "timestamptz", null=True),
      ],
      checks=[("adjudication_complete", "(adjudication IS NULL) = (adjudicated_at IS NULL)")])
