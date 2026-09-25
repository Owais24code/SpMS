-- CON-002 (hard, database-enforced), CON-001 (soft, allowed), end_at derivation,
-- cancel frees the room, optimistic version, same-property room, tenant-wide CON-005 lookup.
\set ON_ERROR_STOP 1
BEGIN;
\ir fixture.psql
SET LOCAL ROLE spms_app;
SELECT core.begin_scope('a0000000-0000-7000-8000-000000000000',
       ARRAY['a1000000-0000-7000-8000-000000000000', 'a2000000-0000-7000-8000-000000000000']::uuid[], NULL);

-- end_at is derived from start_at + duration, whatever the caller sent.
DO $$ BEGIN
    ASSERT (SELECT end_at - start_at FROM scheduling.appointment
             WHERE appointment_id = 'a1000000-0000-7000-8000-0000000000b1') = interval '60 minutes',
           'end_at not derived from duration';
END $$;

-- CON-002: the same room over an overlapping interval (10:30-11:30) is refused by the database.
DO $$ BEGIN
    INSERT INTO scheduling.appointment (tenant_id, property_id, guest_id, service_id, room_id, duration_minutes,
           start_at, end_at, entered_timezone, source, currency_code, status)
    VALUES ('a0000000-0000-7000-8000-000000000000', 'a1000000-0000-7000-8000-000000000000',
            'a0000000-0000-7000-8000-0000000000e1', 'a0000000-0000-7000-8000-0000000000c1', 'a1000000-0000-7000-8000-0000000000a1',
            60, date_trunc('day', now()) + interval '1 day 10 hours 30 minutes', 'epoch', 'Asia/Dubai', 'Desk', 'AED', 'Confirmed');
    RAISE EXCEPTION 'CON-002 overlap was accepted';
EXCEPTION WHEN exclusion_violation THEN NULL; END $$;

-- Half-open interval: back-to-back at 11:00 in the same room is fine.
INSERT INTO scheduling.appointment (tenant_id, property_id, guest_id, service_id, room_id, duration_minutes,
       start_at, end_at, entered_timezone, source, currency_code, status)
VALUES ('a0000000-0000-7000-8000-000000000000', 'a1000000-0000-7000-8000-000000000000',
        'a0000000-0000-7000-8000-0000000000e1', 'a0000000-0000-7000-8000-0000000000c1', 'a1000000-0000-7000-8000-0000000000a1',
        60, date_trunc('day', now()) + interval '1 day 11 hours', 'epoch', 'Asia/Dubai', 'Desk', 'AED', 'Confirmed');

-- CON-001 is soft: the same provider overlapping in another room is accepted (the override is audited by the app).
INSERT INTO scheduling.appointment (appointment_id, tenant_id, property_id, guest_id, service_id, provider_id, room_id,
       duration_minutes, start_at, end_at, entered_timezone, source, currency_code, status)
VALUES ('a1000000-0000-7000-8000-0000000000b2', 'a0000000-0000-7000-8000-000000000000', 'a1000000-0000-7000-8000-000000000000',
        'a0000000-0000-7000-8000-0000000000e1', 'a0000000-0000-7000-8000-0000000000c1', 'a0000000-0000-7000-8000-0000000000f1',
        'a1000000-0000-7000-8000-0000000000a2', 60, date_trunc('day', now()) + interval '1 day 10 hours 30 minutes',
        'epoch', 'Asia/Dubai', 'Desk', 'AED', 'Confirmed');

-- A room at another property cannot be used for an A1 appointment (same-property FK).
DO $$ BEGIN
    UPDATE scheduling.appointment SET room_id = 'a2000000-0000-7000-8000-0000000000a1', version = version + 1
     WHERE appointment_id = 'a1000000-0000-7000-8000-0000000000b2';
    RAISE EXCEPTION 'cross-property room was accepted';
EXCEPTION WHEN foreign_key_violation THEN NULL; END $$;

-- Optimistic concurrency is enforced by the database: version must advance by exactly one.
DO $$ BEGIN
    UPDATE scheduling.appointment SET price_minor = 1 WHERE appointment_id = 'a1000000-0000-7000-8000-0000000000b1';
    RAISE EXCEPTION 'update without a version bump was accepted';
EXCEPTION WHEN serialization_failure THEN NULL; END $$;

-- Cancelling frees the room at once: the previously refused 10:30 booking now fits.
UPDATE scheduling.appointment SET status = 'Cancelled', cancelled_at = now(), version = version + 1
 WHERE appointment_id = 'a1000000-0000-7000-8000-0000000000b1';
UPDATE scheduling.appointment SET room_id = 'a1000000-0000-7000-8000-0000000000a1',
       start_at = date_trunc('day', now()) + interval '1 day 10 hours', version = version + 1
 WHERE appointment_id = 'a1000000-0000-7000-8000-0000000000b2';

-- A Held appointment must say when the hold lapses.
DO $$ BEGIN
    INSERT INTO scheduling.appointment (tenant_id, property_id, guest_id, service_id, duration_minutes,
           start_at, end_at, entered_timezone, source, currency_code, status)
    VALUES ('a0000000-0000-7000-8000-000000000000', 'a1000000-0000-7000-8000-000000000000',
            'a0000000-0000-7000-8000-0000000000e1', 'a0000000-0000-7000-8000-0000000000c1',
            60, now() + interval '3 days', 'epoch', 'Asia/Dubai', 'Online', 'AED', 'Held');
    RAISE EXCEPTION 'a hold without an expiry was accepted';
EXCEPTION WHEN check_violation THEN NULL; END $$;

-- A preflight can never record a hard conflict as overridden.
DO $$ BEGIN
    INSERT INTO scheduling.schedule_change_proposal (tenant_id, property_id, appointment_id, token_hash,
           proposed_start, proposed_end, from_version, conflicts, expires_at)
    VALUES ('a0000000-0000-7000-8000-000000000000', 'a1000000-0000-7000-8000-000000000000',
            'a1000000-0000-7000-8000-0000000000b2', repeat('b', 64), now(), now() + interval '1 hour', 1,
            '[{"code": "CON-002", "severity": "Hard", "override_allowed": false, "overridden": true}]', now() + interval '90 seconds');
    RAISE EXCEPTION 'an overridden hard conflict was accepted';
EXCEPTION WHEN check_violation THEN NULL; END $$;

-- CON-005 is tenant-wide: an A2 appointment is visible to the lookup from an A1-only scope.
SELECT core.begin_scope('a0000000-0000-7000-8000-000000000000', ARRAY['a2000000-0000-7000-8000-000000000000']::uuid[], NULL);
INSERT INTO scheduling.appointment (tenant_id, property_id, guest_id, service_id, duration_minutes,
       start_at, end_at, entered_timezone, source, currency_code, status)
VALUES ('a0000000-0000-7000-8000-000000000000', 'a2000000-0000-7000-8000-000000000000',
        'a0000000-0000-7000-8000-0000000000e1', 'a0000000-0000-7000-8000-0000000000c1',
        60, date_trunc('day', now()) + interval '2 days 9 hours', 'epoch', 'Asia/Dubai', 'Online', 'AED', 'Confirmed');
SELECT core.begin_scope('a0000000-0000-7000-8000-000000000000', ARRAY['a1000000-0000-7000-8000-000000000000']::uuid[], NULL);
DO $$ BEGIN
    ASSERT (SELECT count(*) FROM scheduling.appointment WHERE property_id = 'a2000000-0000-7000-8000-000000000000') = 0,
           'A2 rows leaked into an A1 scope';
    ASSERT (SELECT count(*) FROM scheduling.guest_busy_intervals('a0000000-0000-7000-8000-0000000000e1',
             date_trunc('day', now()) + interval '2 days', date_trunc('day', now()) + interval '3 days')) = 1,
           'CON-005 lookup did not see the other property';
END $$;

-- ...but never another tenant's.
SELECT core.begin_scope('b0000000-0000-7000-8000-000000000000', ARRAY['b1000000-0000-7000-8000-000000000000']::uuid[], NULL);
DO $$ BEGIN
    ASSERT (SELECT count(*) FROM scheduling.guest_busy_intervals('a0000000-0000-7000-8000-0000000000e1',
             now() - interval '10 days', now() + interval '10 days')) = 0,
           'CON-005 lookup crossed tenants';
END $$;
ROLLBACK;
