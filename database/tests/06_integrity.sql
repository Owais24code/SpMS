-- Domain integrity the database guarantees on its own.
\set ON_ERROR_STOP 1
BEGIN;
\ir fixture.psql
SET LOCAL ROLE spms_app;
SELECT core.begin_scope('a0000000-0000-7000-8000-000000000000', ARRAY['a1000000-0000-7000-8000-000000000000']::uuid[], NULL);

-- DEC-001: one authority per capability, property and instant.
INSERT INTO core.capability_ownership (tenant_id, property_id, capability_code, owner_system, effective_range,
       status, approved_by, approved_at, created_by)
VALUES ('a0000000-0000-7000-8000-000000000000', 'a1000000-0000-7000-8000-000000000000', 'Payment', 'Spa',
        tstzrange(now(), NULL), 'Active', 'a0000000-0000-7000-8000-00000000aa02', now(), 'a0000000-0000-7000-8000-00000000aa01');
DO $$ BEGIN
    INSERT INTO core.capability_ownership (tenant_id, property_id, capability_code, owner_system, effective_range,
           status, approved_by, approved_at, created_by)
    VALUES ('a0000000-0000-7000-8000-000000000000', 'a1000000-0000-7000-8000-000000000000', 'Payment', 'Marquee',
            tstzrange(now() + interval '1 day', NULL), 'Active', 'a0000000-0000-7000-8000-00000000aa02', now(),
            'a0000000-0000-7000-8000-00000000aa01');
    RAISE EXCEPTION 'two authorities for one capability were accepted';
EXCEPTION WHEN exclusion_violation THEN NULL; END $$;

-- SEC-014: the proposer cannot approve their own change.
DO $$ BEGIN
    INSERT INTO core.capability_ownership (tenant_id, property_id, capability_code, owner_system, effective_range,
           status, approved_by, approved_at, created_by)
    VALUES ('a0000000-0000-7000-8000-000000000000', 'a1000000-0000-7000-8000-000000000000', 'Catalog', 'Spa',
            tstzrange(now(), NULL), 'Active', 'a0000000-0000-7000-8000-00000000aa01', now(), 'a0000000-0000-7000-8000-00000000aa01');
    RAISE EXCEPTION 'self-approval was accepted';
EXCEPTION WHEN check_violation THEN NULL; END $$;

-- Consent evidence is immutable; revocation is the only change allowed.
INSERT INTO guest.consent_record (consent_id, tenant_id, guest_id, purpose, channel, template_id, template_version,
       evidence_cipher, key_version)
VALUES ('a0000000-0000-7000-8000-0000000000c9', 'a0000000-0000-7000-8000-000000000000', 'a0000000-0000-7000-8000-0000000000e1',
        'Marketing', 'Email', 'mkt', 1, '\x01', 'k1');
DO $$ BEGIN
    UPDATE guest.consent_record SET purpose = 'Transactional', version = version + 1
     WHERE consent_id = 'a0000000-0000-7000-8000-0000000000c9';
    RAISE EXCEPTION 'consent evidence was rewritten';
EXCEPTION WHEN insufficient_privilege THEN NULL; END $$;
UPDATE guest.consent_record SET status = 'Revoked', revoked_at = now(), version = version + 1
 WHERE consent_id = 'a0000000-0000-7000-8000-0000000000c9';

-- IDN-003: a delegation needs a delegate, actions from the fixed set, and an expiry.
DO $$ BEGIN
    INSERT INTO guest.delegated_authority (tenant_id, guest_id, delegate_principal_id, allowed_actions,
           information_visibility, evidence_reference, effective_to)
    VALUES ('a0000000-0000-7000-8000-000000000000', 'a0000000-0000-7000-8000-0000000000e1',
            'a0000000-0000-7000-8000-00000000aa01', ARRAY['ViewHealth'], 'Standard', 'form-1', now() + interval '30 days');
    RAISE EXCEPTION 'a delegation granting health access was accepted';
EXCEPTION WHEN check_violation THEN NULL; END $$;

-- Money: the line total is derived, never supplied.
INSERT INTO scheduling.appointment_line (tenant_id, property_id, appointment_id, line_number, line_type, description,
       quantity, unit_price_minor, currency_code)
VALUES ('a0000000-0000-7000-8000-000000000000', 'a1000000-0000-7000-8000-000000000000', 'a1000000-0000-7000-8000-0000000000b1',
        1, 'Service', 'Massage 60', 1.5, 45000, 'AED');
DO $$ BEGIN
    ASSERT (SELECT line_total_minor FROM scheduling.appointment_line) = 67500, 'line total not derived';
END $$;

-- Hard conflicts can never be marked overridable.
DO $$ BEGIN
    INSERT INTO scheduling.schedule_change_proposal (schedule_change_proposal_id, tenant_id, property_id, appointment_id,
           token_hash, proposed_start, proposed_end, from_version, expires_at)
    VALUES ('a1000000-0000-7000-8000-0000000000f9', 'a0000000-0000-7000-8000-000000000000', 'a1000000-0000-7000-8000-000000000000',
            'a1000000-0000-7000-8000-0000000000b1', repeat('b', 64), now(), now() + interval '1 hour', 1, now() + interval '90 seconds');
    INSERT INTO scheduling.conflict_result (tenant_id, property_id, schedule_change_proposal_id, conflict_code, severity,
           subject_type, override_allowed)
    VALUES ('a0000000-0000-7000-8000-000000000000', 'a1000000-0000-7000-8000-000000000000',
            'a1000000-0000-7000-8000-0000000000f9', 'CON-002', 'Hard', 'Room', true);
    RAISE EXCEPTION 'an overridable hard conflict was accepted';
EXCEPTION WHEN check_violation THEN NULL; END $$;

-- UUIDv7 default: version nibble 7, variant 10, time-ordered.
DO $$
DECLARE a uuid := core.uuid_v7(); b uuid;
BEGIN
    PERFORM pg_sleep(0.002);
    b := core.uuid_v7();
    ASSERT substr(a::text, 15, 1) = '7', 'not a version 7 uuid';
    ASSERT substr(a::text, 20, 1) IN ('8', '9', 'a', 'b'), 'wrong variant';
    ASSERT a < b, 'uuid_v7 not time-ordered';
END $$;
ROLLBACK;
