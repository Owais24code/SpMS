-- Append-only evidence: UPDATE and DELETE raise; only the redaction function may change audit rows.
\set ON_ERROR_STOP 1
BEGIN;
\ir fixture.psql
SET LOCAL ROLE spms_app;
SELECT core.begin_scope('a0000000-0000-7000-8000-000000000000', ARRAY['a1000000-0000-7000-8000-000000000000']::uuid[], NULL);

INSERT INTO core.audit_event (tenant_id, property_id, actor_type, action, entity_type, entity_id, after_data)
VALUES ('a0000000-0000-7000-8000-000000000000', 'a1000000-0000-7000-8000-000000000000', 'Staff', 'appointment.created',
        'Appointment', 'a1000000-0000-7000-8000-0000000000b1', '{"note": "personal detail"}');

DO $$ BEGIN
    ASSERT (SELECT count(*) FROM core.audit_event) = 1, 'audit row not visible';
END $$;

-- Partitions are not reachable directly by the runtime role, only through the parent.
DO $$ BEGIN
    PERFORM 1 FROM core.audit_event_default;
    RAISE EXCEPTION 'spms_app read a partition directly';
EXCEPTION WHEN insufficient_privilege THEN NULL; END $$;

-- The runtime role has no UPDATE/DELETE privilege at all...
DO $$ BEGIN
    UPDATE core.audit_event SET action = 'rewritten';
    RAISE EXCEPTION 'spms_app updated the audit trail';
EXCEPTION WHEN insufficient_privilege THEN NULL; END $$;

-- ...and even the owner is stopped by the trigger.
RESET ROLE;
SET LOCAL ROLE spms_owner;
DO $$ BEGIN
    UPDATE core.audit_event SET action = 'rewritten';
    RAISE EXCEPTION 'owner updated the audit trail';
EXCEPTION WHEN insufficient_privilege THEN NULL; END $$;
DO $$ BEGIN
    DELETE FROM core.audit_event;
    RAISE EXCEPTION 'owner deleted from the audit trail';
EXCEPTION WHEN insufficient_privilege THEN NULL; END $$;

-- Erasure redacts the payload through the one sanctioned path, leaving the row and hashes.
RESET ROLE;
SET LOCAL ROLE spms_erasure;
DO $$ BEGIN
    ASSERT core.redact_audit_subject('Appointment', 'a1000000-0000-7000-8000-0000000000b1') = 1, 'redaction did not apply';
END $$;
RESET ROLE;
DO $$ BEGIN
    ASSERT (SELECT after_data FROM core.audit_event) IS NULL, 'payload survived redaction';
    ASSERT (SELECT action FROM core.audit_event) = 'appointment.created', 'redaction changed more than the payload';
    ASSERT NOT EXISTS (SELECT 1 FROM core.audit_event_default), 'audit row fell into the DEFAULT partition';
END $$;
ROLLBACK;
