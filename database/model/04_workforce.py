"""workforce: staff, HR profile (kept apart: SEC-007), roles, qualifications, credentials, documents, schedule."""
from dsl import *

S = "workforce"

ROLES = ["provider", "front_desk", "spa_manager", "hr_compliance", "finance", "platform_admin",
         "configuration_approver", "scheduler", "housekeeping", "inventory_manager", "marketing", "support",
         "operations_analyst", "executive", "release_manager", "security_admin", "integration_service"]

table(S, "staff", AGGREGATE, TENANT, key=None,
      spec="§Spa service provider and small HR module; §53.2 Staff management",
      doc="Operational staff profile, broadly visible. Property membership comes from staff_role_assignment.",
      cols=[
          col("principal_id", "uuid", null=True, fk="core.principal"),
          col("home_property_id", "uuid", null=True, fk="core.property.property_id"),
          col("department_code", "text", null=True, doc="code_list Department"),
          col("preferred_name", "text"),
          col("employment_status", "text", default="'Active'",
              check="employment_status IN ('Pending', 'Active', 'OnLeave', 'Suspended', 'Terminated')"),
          col("bookable", "boolean", default="true"),
          col("hris_system", "text", null=True),
          col("hris_worker_id", "text", null=True),
          col("payroll_system", "text", null=True),
          col("payroll_worker_id", "text", null=True),
      ],
      uniques=[("principal_uq", "principal_id"), ("hris_uq", "tenant_id, hris_system, hris_worker_id")],
      dropped=[("status", "employment_status"), ("staff_property (table)", "membership = an active role at the property"),
               ("provider_identity (table)", "core.principal_login")])

table(S, "spa_service_provider", AGGREGATE, TENANT, key=None, source_cols=False,
      spec="§What belongs on file; SEC-007; §24 Workforce records (separate operational and employment data)",
      doc="HR profile, 1:1 with staff but deliberately a separate table: the spec requires operational and "
          "employment data to be separable with category-specific access.",
      cols=[
          col("staff_id", "uuid", fk="workforce.staff"),
          col("employee_number", "text"),
          col("first_name", "text"),
          col("middle_name", "text", null=True),
          col("last_name", "text"),
          col("work_email", "text", null=True),
          col("personal_email", "text", null=True, restricted=True),
          col("mobile_phone", "text", null=True, restricted=True),
          col("home_address", "jsonb", null=True, restricted=True),
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
      dropped=[("address_line1..country_code", "home_address jsonb (never queried by part)")])

table(S, "staff_role_assignment", MASTER, TENANT_OPT, key=None, source_cols=False, handoff="completed",
      spec="technical/config/role_permissions.json; security.assign_role:approved",
      doc="Source of truth for OpenFGA role tuples. property_id NULL = tenant-wide role.",
      statuses=["Proposed", "Active", "Revoked", "Expired"],
      cols=[
          col("staff_id", "uuid", fk="workforce.staff"),
          col("role_code", "text", check="role_code IN (" + ", ".join(f"'{r}'" for r in ROLES) + ")"),
          col("approved_by", "uuid", null=True),
          col("approved_at", "timestamptz", null=True),
          col("fga_synced_at", "timestamptz", null=True, doc="set when the outbox worker confirms the tuple write"),
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

table(S, "credential", AGGREGATE, TENANT, key=None, source_cols=False,
      spec="§Credential rules; §53.2 expiration alerts, screening status; DEC-006",
      doc="Licenses, certifications and background checks: each is a verified, expiring fact about a worker.",
      statuses=["Pending", "Verified", "Expired", "Revoked", "Rejected"],
      cols=[
          col("staff_id", "uuid", fk="workforce.staff"),
          col("credential_kind", "text", check="credential_kind IN ('License', 'Certification', 'Training', 'BackgroundCheck')"),
          col("license_type_code", "text", null=True, doc="code_list LicenseType"),
          col("jurisdiction", "text", null=True),
          col("issuer_name", "text", null=True, doc="licensing body, or screening provider"),
          col("number_cipher", "bytea", null=True, restricted=True),
          col("number_key_version", "text", null=True),
          col("number_last4", "char(4)", null=True),
          col("provider_case_reference", "text", null=True, doc="screening provider case id"),
          col("consent_reference", "text", null=True),
          col("adjudication", "text", null=True, restricted=True,
              check="adjudication IN ('Eligible', 'NotEligible', 'ReviewRequired', 'Withdrawn')"),
          col("issued_at", "date", null=True),
          col("expires_at", "date", null=True),
          col("verified_by", "uuid", null=True),
          col("verified_at", "timestamptz", null=True),
          col("evidence_document_id", "uuid", null=True, fk="workforce.staff_document"),
          col("restrictions", "text", null=True),
      ],
      checks=[("cipher_has_key", "(number_cipher IS NULL) = (number_key_version IS NULL)"),
              ("verified_complete", "status <> 'Verified' OR (verified_by IS NOT NULL AND verified_at IS NOT NULL)"),
              ("adjudication_only_screening", "adjudication IS NULL OR credential_kind = 'BackgroundCheck'"),
              ("license_typed", "credential_kind NOT IN ('License', 'Certification') OR license_type_code IS NOT NULL"),
              ("dates_ordered", "expires_at IS NULL OR issued_at IS NULL OR expires_at >= issued_at")],
      indexes=["credential_expiry_ix ON workforce.credential (tenant_id, expires_at) WHERE status = 'Verified'",
               "credential_staff_type_ix ON workforce.credential (tenant_id, staff_id, license_type_code) WHERE status = 'Verified'"],
      dropped=[("background_screening (table)", "credential_kind = 'BackgroundCheck'"),
               ("credential_assignment (table)", "credential.staff_id"),
               ("number_vault_reference", "number_cipher")])

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
          col("retention_key", "text", null=True, doc="core.setting retention.<class>"),
          col("expires_on", "date", null=True),
          col("acknowledged_at", "timestamptz", null=True),
      ])

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

table(S, "work_schedule", AGGREGATE, PROPERTY, key=None, handoff="completed (absorbs staff_leave)", source_cols=False,
      spec="§53.2 availability", statuses=["Requested", "Draft", "Published", "Approved", "Rejected", "Cancelled"],
      doc="Everything that decides whether a person can be booked: shifts, on-call and leave.",
      cols=[
          col("staff_id", "uuid", fk="workforce.staff"),
          col("entry_type", "text", check="entry_type IN ('Shift', 'OnCall', 'Leave')"),
          col("leave_type", "text", null=True, check="leave_type IN ('Planned', 'Unplanned', 'Training', 'Other')"),
          col("starts_at", "timestamptz"),
          col("ends_at", "timestamptz"),
          col("approved_by", "uuid", null=True),
          col("approved_at", "timestamptz", null=True),
      ],
      checks=[("range_forward", "ends_at > starts_at"),
              ("leave_typed", "(entry_type = 'Leave') = (leave_type IS NOT NULL)")],
      indexes=["work_schedule_window_ix ON workforce.work_schedule USING gist (tenant_id, staff_id, tstzrange(starts_at, ends_at, '[)'))"],
      dropped=[("staff_leave (table)", "entry_type = 'Leave'"), ("schedule_date/available_range/timezone", "starts_at/ends_at")],
      extra_sql="""
-- A person is in one place at a time: no overlapping published shifts, across properties.
ALTER TABLE workforce.work_schedule ADD CONSTRAINT work_schedule_no_overlap
    EXCLUDE USING gist (tenant_id WITH =, staff_id WITH =, tstzrange(starts_at, ends_at, '[)') WITH &&)
    WHERE (status = 'Published' AND entry_type IN ('Shift', 'OnCall'));
""")
