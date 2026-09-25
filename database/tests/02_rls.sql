-- Tenant and property isolation, exercised as the runtime role.
\set ON_ERROR_STOP 1
BEGIN;
\ir fixture.psql
SET LOCAL ROLE spms_app;

-- No scope: nothing is visible (fail closed), and begin_scope refuses no tenant.
DO $$ BEGIN
    ASSERT (SELECT count(*) FROM guest.guest) = 0, 'guests visible without a scope';
    ASSERT (SELECT count(*) FROM core.tenant) = 0, 'tenant directory visible without a scope';
    ASSERT (SELECT count(*) FROM scheduling.appointment) = 0, 'appointments visible without a scope';
END $$;

SELECT core.begin_scope('a0000000-0000-7000-8000-000000000000', ARRAY['a1000000-0000-7000-8000-000000000000']::uuid[], NULL);
DO $$ BEGIN
    ASSERT (SELECT count(*) FROM core.tenant) = 1, 'tenant directory must show exactly the current tenant';
    ASSERT (SELECT count(*) FROM guest.guest) = 1, 'tenant A should see its one guest';
    ASSERT (SELECT count(*) FROM catalog.service) = 1, 'tenant A should see its one service';
    ASSERT (SELECT count(*) FROM scheduling.appointment) = 1, 'A1 appointment visible under A1 scope';
    ASSERT (SELECT count(*) FROM resources.resource) = 2, 'only A1 rooms under A1 scope';
END $$;

-- Writing into another tenant is refused by WITH CHECK.
DO $$ BEGIN
    INSERT INTO guest.guest (tenant_id, legal_first_name) VALUES ('b0000000-0000-7000-8000-000000000000', 'Intruder');
    RAISE EXCEPTION 'cross-tenant insert was accepted';
EXCEPTION WHEN insufficient_privilege THEN NULL; END $$;

-- Writing into a property outside the scope is refused too.
DO $$ BEGIN
    INSERT INTO resources.resource (tenant_id, property_id, resource_type, code, name)
    VALUES ('a0000000-0000-7000-8000-000000000000', 'a2000000-0000-7000-8000-000000000000', 'TreatmentRoom', 'X', 'X');
    RAISE EXCEPTION 'out-of-scope property insert was accepted';
EXCEPTION WHEN insufficient_privilege THEN NULL; END $$;

-- Widening the scope to both properties shows both.
SELECT core.begin_scope('a0000000-0000-7000-8000-000000000000',
       ARRAY['a1000000-0000-7000-8000-000000000000', 'a2000000-0000-7000-8000-000000000000']::uuid[], NULL);
DO $$ BEGIN
    ASSERT (SELECT count(*) FROM resources.resource) = 3, 'A1 + A2 rooms under the widened scope';
END $$;

-- Intake is unreachable from the ordinary role, even with a scope.
DO $$ BEGIN
    PERFORM count(*) FROM intake.intake_submission;
    RAISE EXCEPTION 'spms_app read intake without SET ROLE';
EXCEPTION WHEN insufficient_privilege THEN NULL; END $$;

-- ...and reachable, under the same policies, once the intake unit of work sets the role.
SET LOCAL ROLE spms_intake;
DO $$ BEGIN
    ASSERT (SELECT count(*) FROM intake.intake_submission) = 0;
END $$;
ROLLBACK;

-- begin_scope outside an explicit transaction is refused (it would last one statement).
DO $$ BEGIN
    RAISE NOTICE 'checking implicit transaction refusal';
END $$;
\set ON_ERROR_STOP 0
SELECT core.begin_scope('a0000000-0000-7000-8000-000000000000', '{}'::uuid[], NULL) AS must_fail \gset
\set ON_ERROR_STOP 1
\if :{?must_fail}
    DO $$ BEGIN RAISE EXCEPTION 'begin_scope accepted an implicit transaction'; END $$;
\endif
