-- V003  The append-only audit trail, idempotency keys and preflight tokens.

-- =====================================================================
-- Audit (SEC-004, §54.3)
-- =====================================================================
-- before_hash/after_hash are SHA-256 over the canonical state rather than the
-- values themselves, so the trail proves what changed without becoming a
-- second copy of restricted data.
CREATE TABLE audit_entry (
    audit_id       bigserial PRIMARY KEY,
    at_utc         timestamptz NOT NULL,
    tenant_id      text NOT NULL,
    property_id    text NOT NULL,
    actor          text NOT NULL,
    action         text NOT NULL,
    purpose        text NOT NULL,
    subject_type   text NOT NULL,
    subject_id     text NOT NULL,
    subject_version int NOT NULL,
    before_hash    text NULL,
    after_hash     text NULL,
    conflict_codes text[] NOT NULL DEFAULT '{}',
    selected_resolution text NULL,
    target_status  text NULL,
    reason         text NULL,
    correlation_id text NOT NULL
);

CREATE INDEX audit_entry_scope ON audit_entry (tenant_id, property_id, audit_id DESC);
CREATE INDEX audit_entry_subject ON audit_entry (tenant_id, subject_type, subject_id, audit_id DESC);
CREATE INDEX audit_entry_correlation ON audit_entry (correlation_id);

-- Append-only is enforced, not merely documented. A trail an application can
-- quietly rewrite is not evidence, and "append-only" in an interface comment
-- is not a control.
CREATE RULE audit_entry_no_update AS ON UPDATE TO audit_entry DO INSTEAD NOTHING;
CREATE RULE audit_entry_no_delete AS ON DELETE TO audit_entry DO INSTEAD NOTHING;

-- =====================================================================
-- Idempotency (API-001)
-- =====================================================================
-- Scoped by tenant, property, operation AND route. Property because one
-- client-chosen key arriving at two properties must not replay the first
-- property's record to the second; route because the route id is load-bearing
-- for a reassign, so two reassigns of different appointments with one key
-- must not replay each other.
CREATE TABLE idempotency_key (
    tenant_id     text NOT NULL,
    property_id   text NOT NULL,
    operation     text NOT NULL,
    route         text NOT NULL,
    key           text NOT NULL,
    request_hash  text NOT NULL,
    completed     boolean NOT NULL DEFAULT false,
    status_code   int NOT NULL DEFAULT 0,
    response_json text NULL,
    -- When the reservation was taken or the result stored. A reservation older
    -- than the lease may be stolen, so a request killed between claim and
    -- complete cannot poison the key for a day.
    at_utc        timestamptz NOT NULL,
    PRIMARY KEY (tenant_id, property_id, operation, route, key)
);

CREATE INDEX idempotency_key_sweep ON idempotency_key (at_utc) WHERE completed;

-- =====================================================================
-- Preflight tokens (SCH-020 / GUI-003)
-- =====================================================================
-- Single use, short lived, and scoped to the tenant AND property that minted
-- them. The conflict set is stored as JSON because it is a record of what the
-- operator was SHOWN, not a live projection — the commit path re-evaluates
-- the board and compares.
CREATE TABLE preflight_token (
    tenant_id      text NOT NULL,
    token          text NOT NULL,
    property_id    text NOT NULL,
    appointment_id text NOT NULL,
    proposed_start_utc timestamptz NOT NULL,
    proposed_end_utc   timestamptz NOT NULL,
    proposed_provider_id text NULL,
    proposed_room_id     text NULL,
    from_row_version int NOT NULL,
    conflicts_json   jsonb NOT NULL,
    issued_utc       timestamptz NOT NULL,
    expires_utc      timestamptz NOT NULL,
    PRIMARY KEY (tenant_id, token)
);

CREATE INDEX preflight_token_expiry ON preflight_token (expires_utc);

-- =====================================================================
-- Schema version ledger
-- =====================================================================
-- TST-023 requires production readiness to record execution of V001 through
-- V003. The runner writes a row per file with its checksum, so a migration
-- edited after the fact is detectable rather than silently divergent.
CREATE TABLE schema_migration (
    version     text PRIMARY KEY,
    file_name   text NOT NULL,
    checksum    text NOT NULL,
    applied_utc timestamptz NOT NULL DEFAULT now(),
    applied_by  text NOT NULL DEFAULT current_user
);
