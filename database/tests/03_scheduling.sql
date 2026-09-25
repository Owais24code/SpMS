-- CON-002 (hard, database-enforced), CON-001 (soft, allowed), end_at derivation,
-- release on cancel, optimistic version, tenant-wide CON-005 lookup.
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

-- A second appointment overlapping 10:30-11:30.
INSERT INTO scheduling.appointment (appointment_id, tenant_id, property_id, guest_id, service_id, service_version_id,
       duration_minutes, start_at, end_at, entered_timezone, source, currency_code, status)
VALUES ('a1000000-0000-7000-8000-0000000000b2', 'a0000000-0000-7000-8000-000000000000', 'a1000000-0000-7000-8000-000000000000',
        'a0000000-0000-7000-8000-0000000000e1', 'a0000000-0000-7000-8000-0000000000c1', 'a0000000-0000-7000-8000-0000000000d1',
        60, date_trunc('day', now()) + interval '1 day 10 hours 30 minutes', 'epoch', 'Asia/Dubai', 'Desk', 'AED', 'Confirmed');

-- CON-002: the same room over an overlapping interval is refused by the database.
DO $$ BEGIN
    INSERT INTO scheduling.appointment_resource_assignment (tenant_id, property_id, appointment_id, assignment_role,
           resource_id, starts_at, ends_at)
    VALUES ('a0000000-0000-7000-8000-000000000000', 'a1000000-0000-7000-8000-000000000000', 'a1000000-0000-7000-8000-0000000000b2',
            'Room', 'a1000000-0000-7000-8000-0000000000a1',
            date_trunc('day', now()) + interval '1 day 10 hours 30 minutes', date_trunc('day', now()) + interval '1 day 11 hours 30 minutes');
    RAISE EXCEPTION 'CON-002 overlap was accepted';
EXCEPTION WHEN exclusion_violation THEN NULL; END $$;

-- Half-open interval: back-to-back 11:00 in the same room is fine.
INSERT INTO scheduling.appointment_resource_assignment (tenant_id, property_id, appointment_id, assignment_role,
       resource_id, starts_at, ends_at)
VALUES ('a0000000-0000-7000-8000-000000000000', 'a1000000-0000-7000-8000-000000000000', 'a1000000-0000-7000-8000-0000000000b2',
        'Room', 'a1000000-0000-7000-8000-0000000000a2',
        date_trunc('day', now()) + interval '1 day 10 hours 30 minutes', date_trunc('day', now()) + interval '1 day 11 hours 30 minutes');

-- CON-001 is soft: the same provider may be double-booked (the override is audited by the app).
INSERT INTO scheduling.appointment_resource_assignment (tenant_id, property_id, appointment_id, assignment_role,
       staff_id, starts_at, ends_at)
VALUES ('a0000000-0000-7000-8000-000000000000', 'a1000000-0000-7000-8000-000000000000', 'a1000000-0000-7000-8000-0000000000b2',
        'Provider', 'a0000000-0000-7000-8000-0000000000f1',
        date_trunc('day', now()) + interval '1 day 10 hours 30 minutes', date_trunc('day', now()) + interval '1 day 11 hours 30 minutes');

-- A room at another property cannot be assigned to an A1 appointment (same_property FK).
DO $$ BEGIN
    INSERT INTO scheduling.appointment_resource_assignment (tenant_id, property_id, appointment_id, assignment_role,
           resource_id, starts_at, ends_at)
    VALUES ('a0000000-0000-7000-8000-000000000000', 'a1000000-0000-7000-8000-000000000000', 'a1000000-0000-7000-8000-0000000000b2',
            'Equipment', 'a2000000-0000-7000-8000-0000000000a1', now(), now() + interval '1 hour');
    RAISE EXCEPTION 'cross-property resource was accepted';
EXCEPTION WHEN foreign_key_violation THEN NULL; END $$;

-- Optimistic concurrency is enforced by the database: version must advance by exactly one.
DO $$ BEGIN
    UPDATE scheduling.appointment SET price_minor = 1 WHERE appointment_id = 'a1000000-0000-7000-8000-0000000000b1';
    RAISE EXCEPTION 'update without a version bump was accepted';
EXCEPTION WHEN serialization_failure THEN NULL; END $$;

-- Cancelling releases the room, after which the overlapping booking fits.
UPDATE scheduling.appointment SET status = 'Cancelled', cancelled_at = now(), version = version + 1
 WHERE appointment_id = 'a1000000-0000-7000-8000-0000000000b1';
DO $$ BEGIN
    ASSERT (SELECT count(*) FROM scheduling.appointment_resource_assignment
             WHERE appointment_id = 'a1000000-0000-7000-8000-0000000000b1' AND status = 'Active') = 0,
           'cancel did not release assignments';
END $$;
INSERT INTO scheduling.appointment_resource_assignment (tenant_id, property_id, appointment_id, assignment_role,
       resource_id, starts_at, ends_at)
VALUES ('a0000000-0000-7000-8000-000000000000', 'a1000000-0000-7000-8000-000000000000', 'a1000000-0000-7000-8000-0000000000b2',
        'Equipment', 'a1000000-0000-7000-8000-0000000000a1',
        date_trunc('day', now()) + interval '1 day 10 hours 30 minutes', date_trunc('day', now()) + interval '1 day 11 hours 30 minutes');

-- CON-005 is tenant-wide: an A2 appointment is visible to the lookup from an A1-only scope.
SELECT core.begin_scope('a0000000-0000-7000-8000-000000000000', ARRAY['a2000000-0000-7000-8000-000000000000']::uuid[], NULL);
INSERT INTO scheduling.appointment (tenant_id, property_id, guest_id, service_id, service_version_id,
       duration_minutes, start_at, end_at, entered_timezone, source, currency_code, status)
VALUES ('a0000000-0000-7000-8000-000000000000', 'a2000000-0000-7000-8000-000000000000',
        'a0000000-0000-7000-8000-0000000000e1', 'a0000000-0000-7000-8000-0000000000c1', 'a0000000-0000-7000-8000-0000000000d1',
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
