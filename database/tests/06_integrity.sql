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

-- Settings: one active value per key and scope, and never approved by its author (SEC-014).
INSERT INTO core.setting (tenant_id, property_id, setting_key, value_json, status, approved_by, approved_at, created_by)
VALUES ('a0000000-0000-7000-8000-000000000000', 'a1000000-0000-7000-8000-000000000000', 'policy.cancellation',
        '{"free_until_hours": 24}', 'Active', 'a0000000-0000-7000-8000-00000000aa02', now(), 'a0000000-0000-7000-8000-00000000aa01');
DO $$ BEGIN
    INSERT INTO core.setting (tenant_id, property_id, setting_key, value_json, status, approved_by, approved_at, created_by)
    VALUES ('a0000000-0000-7000-8000-000000000000', 'a1000000-0000-7000-8000-000000000000', 'policy.cancellation',
            '{"free_until_hours": 12}', 'Active', 'a0000000-0000-7000-8000-00000000aa02', now(), 'a0000000-0000-7000-8000-00000000aa01');
    RAISE EXCEPTION 'two active values for one setting were accepted';
EXCEPTION WHEN exclusion_violation THEN NULL; END $$;
DO $$ BEGIN
    INSERT INTO core.setting (tenant_id, setting_key, value_json, status, approved_by, approved_at, created_by)
    VALUES ('a0000000-0000-7000-8000-000000000000', 'feature.online_booking', 'true', 'Active',
            'a0000000-0000-7000-8000-00000000aa01', now(), 'a0000000-0000-7000-8000-00000000aa01');
    RAISE EXCEPTION 'a self-approved setting was accepted';
EXCEPTION WHEN check_violation THEN NULL; END $$;

-- Commerce: a Draft order is the cart; its lines may be removed only while it is Draft.
INSERT INTO commerce.commerce_order (commerce_order_id, tenant_id, property_id, guest_id, currency_code)
VALUES ('a1000000-0000-7000-8000-0000000000d1', 'a0000000-0000-7000-8000-000000000000', 'a1000000-0000-7000-8000-000000000000',
        'a0000000-0000-7000-8000-0000000000e1', 'AED');
INSERT INTO commerce.order_line (tenant_id, property_id, commerce_order_id, line_number, line_kind, service_id,
       appointment_id, description, quantity, unit_price_minor, net_minor)
VALUES ('a0000000-0000-7000-8000-000000000000', 'a1000000-0000-7000-8000-000000000000', 'a1000000-0000-7000-8000-0000000000d1',
        1, 'Service', 'a0000000-0000-7000-8000-0000000000c1', 'a1000000-0000-7000-8000-0000000000b1', 'Massage 60', 1, 45000, 45000),
       ('a0000000-0000-7000-8000-000000000000', 'a1000000-0000-7000-8000-000000000000', 'a1000000-0000-7000-8000-0000000000d1',
        2, 'Fee', NULL, NULL, 'Late fee', 1, 5000, 5000);
DELETE FROM commerce.order_line WHERE line_number = 2;
UPDATE commerce.commerce_order SET status = 'Open', ordered_at = now(), version = version + 1
 WHERE commerce_order_id = 'a1000000-0000-7000-8000-0000000000d1';
DO $$ BEGIN
    DELETE FROM commerce.order_line WHERE line_number = 1;
    RAISE EXCEPTION 'a line of a placed order was deleted';
EXCEPTION WHEN insufficient_privilege THEN NULL; END $$;

-- A refund must name the transaction it returns and be approved by someone other than the requester.
INSERT INTO commerce.payment_transaction (payment_transaction_id, tenant_id, property_id, commerce_order_id, tender_code,
       transaction_type, outcome, amount_minor, currency_code, provider_code, processed_at, idempotency_key)
VALUES ('a1000000-0000-7000-8000-0000000000d2', 'a0000000-0000-7000-8000-000000000000', 'a1000000-0000-7000-8000-000000000000',
        'a1000000-0000-7000-8000-0000000000d1', 'Card', 'Sale', 'Approved', 45000, 'AED', 'pending-dec-010', now(), 'sale-0001');
DO $$ BEGIN
    INSERT INTO commerce.payment_intent (tenant_id, property_id, commerce_order_id, purpose, original_transaction_id,
           amount_minor, currency_code, provider_code, status, approved_by, approved_at, created_by, idempotency_key)
    VALUES ('a0000000-0000-7000-8000-000000000000', 'a1000000-0000-7000-8000-000000000000', 'a1000000-0000-7000-8000-0000000000d1',
            'Refund', 'a1000000-0000-7000-8000-0000000000d2', 45000, 'AED', 'pending-dec-010', 'Approved',
            'a0000000-0000-7000-8000-00000000aa01', now(), 'a0000000-0000-7000-8000-00000000aa01', 'refund-0001');
    RAISE EXCEPTION 'a self-approved refund was accepted';
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
