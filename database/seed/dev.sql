-- Development seed: one tenant, two properties, staff, services, rooms, guests
-- and a day of appointments at Riverside. Idempotent. Runs as spms_owner with
-- a tenant scope (FORCE ROW LEVEL SECURITY binds the owner too).
--
-- The ids are fixed so the front end's development identity
-- (frontend/src/environments/environment.development.ts) and the acceptance
-- sweep can name them. They are UUIDv7-shaped but hand-picked; never reuse
-- them outside development.
--
--   tenant     01920000-0000-7000-8000-000000000001
--   property   ...0101 Riverside (America/New_York), ...0102 Harbour (Europe/London)
--   principals ...0201 Dana (front desk), ...0202 Morgan (spa manager + platform admin), ...0203-0205 providers,
--              ...0206 Riley (scheduler), ...0207 Sam (finance), ...0208 Ada (platform admin); dev logins by first name
--   staff      ...0601 Lena, ...0602 Marco, ...0603 Priya, ...0604 Dana, ...0605 Morgan
--   services   ...0401-0406   rooms ...0501-0506   guests ...0701-0705   appointments ...0801-0805
--   spare rooms ...1001-1040 and walk-in guests ...2001-2040 (used by the HTTP sweep)

BEGIN;
SELECT core.begin_scope('01920000-0000-7000-8000-000000000001',
                        ARRAY['01920000-0000-7000-8000-000000000101', '01920000-0000-7000-8000-000000000102']::uuid[],
                        NULL, 'dev-seed');

INSERT INTO core.tenant (tenant_id, code, name, data_region)
VALUES ('01920000-0000-7000-8000-000000000001', 'aarfid-demo', 'AARFID Demo', 'westeurope')
ON CONFLICT (tenant_id) DO NOTHING;

INSERT INTO core.property (property_id, tenant_id, code, name, timezone, currency_code, opening_hours)
VALUES
  ('01920000-0000-7000-8000-000000000101', '01920000-0000-7000-8000-000000000001', 'riverside', 'Riverside Spa', 'America/New_York', 'USD',
   '[{"day":1,"opens":"09:00","closes":"21:00"},{"day":2,"opens":"09:00","closes":"21:00"},{"day":3,"opens":"09:00","closes":"21:00"},
     {"day":4,"opens":"09:00","closes":"21:00"},{"day":5,"opens":"09:00","closes":"21:00"},{"day":6,"opens":"09:00","closes":"21:00"},
     {"day":7,"opens":"10:00","closes":"18:00"}]'),
  ('01920000-0000-7000-8000-000000000102', '01920000-0000-7000-8000-000000000001', 'harbour', 'Harbour Spa', 'Europe/London', 'USD', '[]')
ON CONFLICT (property_id) DO NOTHING;

INSERT INTO core.principal (principal_id, tenant_id, principal_type, display_name) VALUES
  ('01920000-0000-7000-8000-000000000201', '01920000-0000-7000-8000-000000000001', 'Staff', 'Dana (front desk)'),
  ('01920000-0000-7000-8000-000000000202', '01920000-0000-7000-8000-000000000001', 'Staff', 'Morgan (spa manager)'),
  ('01920000-0000-7000-8000-000000000203', '01920000-0000-7000-8000-000000000001', 'Staff', 'Lena'),
  ('01920000-0000-7000-8000-000000000204', '01920000-0000-7000-8000-000000000001', 'Staff', 'Marco'),
  ('01920000-0000-7000-8000-000000000205', '01920000-0000-7000-8000-000000000001', 'Staff', 'Priya'),
  ('01920000-0000-7000-8000-000000000206', '01920000-0000-7000-8000-000000000001', 'Staff', 'Riley (scheduler)'),
  ('01920000-0000-7000-8000-000000000207', '01920000-0000-7000-8000-000000000001', 'Staff', 'Sam (finance)'),
  ('01920000-0000-7000-8000-000000000208', '01920000-0000-7000-8000-000000000001', 'Staff', 'Ada (platform admin)'),
  ('01920000-0000-7000-8000-000000000209', '01920000-0000-7000-8000-000000000001', 'Staff', 'Hana (housekeeping)')
ON CONFLICT (principal_id) DO NOTHING;

-- Development logins: issuer 'spms-dev', subject = a readable handle.
INSERT INTO core.principal_login (principal_login_id, tenant_id, principal_id, login_type, idp_issuer, idp_subject, username) VALUES
  ('01920000-0000-7000-8000-000000000211', '01920000-0000-7000-8000-000000000001', '01920000-0000-7000-8000-000000000201', 'EntraUser', 'spms-dev', 'dana', 'dana@aarfid.dev'),
  ('01920000-0000-7000-8000-000000000212', '01920000-0000-7000-8000-000000000001', '01920000-0000-7000-8000-000000000202', 'EntraUser', 'spms-dev', 'morgan', 'morgan@aarfid.dev'),
  ('01920000-0000-7000-8000-000000000213', '01920000-0000-7000-8000-000000000001', '01920000-0000-7000-8000-000000000203', 'EntraUser', 'spms-dev', 'lena', 'lena@aarfid.dev'),
  ('01920000-0000-7000-8000-000000000214', '01920000-0000-7000-8000-000000000001', '01920000-0000-7000-8000-000000000204', 'EntraUser', 'spms-dev', 'marco', 'marco@aarfid.dev'),
  ('01920000-0000-7000-8000-000000000215', '01920000-0000-7000-8000-000000000001', '01920000-0000-7000-8000-000000000205', 'EntraUser', 'spms-dev', 'priya', 'priya@aarfid.dev'),
  ('01920000-0000-7000-8000-000000000216', '01920000-0000-7000-8000-000000000001', '01920000-0000-7000-8000-000000000206', 'EntraUser', 'spms-dev', 'riley', 'riley@aarfid.dev'),
  ('01920000-0000-7000-8000-000000000217', '01920000-0000-7000-8000-000000000001', '01920000-0000-7000-8000-000000000207', 'EntraUser', 'spms-dev', 'sam', 'sam@aarfid.dev'),
  ('01920000-0000-7000-8000-000000000218', '01920000-0000-7000-8000-000000000001', '01920000-0000-7000-8000-000000000208', 'EntraUser', 'spms-dev', 'ada', 'ada@aarfid.dev'),
  ('01920000-0000-7000-8000-000000000219', '01920000-0000-7000-8000-000000000001', '01920000-0000-7000-8000-000000000209', 'EntraUser', 'spms-dev', 'hana', 'hana@aarfid.dev')
ON CONFLICT (idp_issuer, idp_subject) DO NOTHING;

INSERT INTO catalog.service (service_id, tenant_id, code, name, duration_minutes, base_price_minor, currency_code, status, online_bookable, requires_intake) VALUES
  ('01920000-0000-7000-8000-000000000401', '01920000-0000-7000-8000-000000000001', 'deep-90',     'Deep tissue 90',  90, 16000, 'USD', 'Active', true,  true),
  ('01920000-0000-7000-8000-000000000402', '01920000-0000-7000-8000-000000000001', 'aroma-60',    'Aromatherapy 60', 60, 12000, 'USD', 'Active', true,  true),
  ('01920000-0000-7000-8000-000000000403', '01920000-0000-7000-8000-000000000001', 'facial-45',   'Facial 45',       45,  9500, 'USD', 'Active', true,  false),
  ('01920000-0000-7000-8000-000000000404', '01920000-0000-7000-8000-000000000001', 'hotstone-60', 'Hot stone 60',    60, 13500, 'USD', 'Active', true,  true),
  ('01920000-0000-7000-8000-000000000405', '01920000-0000-7000-8000-000000000001', 'swedish-60',  'Swedish 60',      60, 11000, 'USD', 'Active', true,  true),
  ('01920000-0000-7000-8000-000000000406', '01920000-0000-7000-8000-000000000001', 'peel-30',     'Peel 30',         30,  7000, 'USD', 'Active', false, false)
ON CONFLICT (service_id) DO NOTHING;

INSERT INTO catalog.property_service (tenant_id, property_id, service_id)
SELECT '01920000-0000-7000-8000-000000000001', p.id, s.service_id
  FROM catalog.service s
 CROSS JOIN (VALUES ('01920000-0000-7000-8000-000000000101'::uuid), ('01920000-0000-7000-8000-000000000102'::uuid)) p(id)
ON CONFLICT ON CONSTRAINT property_service_service_uq DO NOTHING;

INSERT INTO resources.resource (resource_id, tenant_id, property_id, resource_type, code, name) VALUES
  ('01920000-0000-7000-8000-000000000501', '01920000-0000-7000-8000-000000000001', '01920000-0000-7000-8000-000000000101', 'TreatmentRoom', 'T1', 'Treatment 1'),
  ('01920000-0000-7000-8000-000000000502', '01920000-0000-7000-8000-000000000001', '01920000-0000-7000-8000-000000000101', 'TreatmentRoom', 'T2', 'Treatment 2'),
  ('01920000-0000-7000-8000-000000000503', '01920000-0000-7000-8000-000000000001', '01920000-0000-7000-8000-000000000101', 'TreatmentRoom', 'T4', 'Treatment 4'),
  ('01920000-0000-7000-8000-000000000504', '01920000-0000-7000-8000-000000000001', '01920000-0000-7000-8000-000000000101', 'TreatmentRoom', 'T5', 'Treatment 5'),
  ('01920000-0000-7000-8000-000000000505', '01920000-0000-7000-8000-000000000001', '01920000-0000-7000-8000-000000000101', 'CoupleRoom',    'S1', 'Suite 1'),
  ('01920000-0000-7000-8000-000000000506', '01920000-0000-7000-8000-000000000001', '01920000-0000-7000-8000-000000000101', 'CoupleRoom',    'S3', 'Suite 3')
ON CONFLICT (resource_id) DO NOTHING;

-- Spare rooms and walk-in guests: the HTTP acceptance sweep books into these,
-- so it never has to move the demo board's own rows out of the way.
INSERT INTO resources.resource (resource_id, tenant_id, property_id, resource_type, code, name)
SELECT ('01920000-0000-7000-8000-00000000' || (1000 + n)::text)::uuid, '01920000-0000-7000-8000-000000000001',
       '01920000-0000-7000-8000-000000000101', 'TreatmentRoom', 'X' || lpad(n::text, 2, '0'), 'Spare ' || lpad(n::text, 2, '0')
  FROM generate_series(1, 40) n
ON CONFLICT (resource_id) DO NOTHING;

INSERT INTO guest.guest (guest_id, tenant_id, home_property_id, display_alias, public_queue_id)
SELECT ('01920000-0000-7000-8000-00000000' || (2000 + n)::text)::uuid, '01920000-0000-7000-8000-000000000001',
       '01920000-0000-7000-8000-000000000101', 'Walk-in ' || lpad(n::text, 2, '0'), 'W' || lpad(n::text, 2, '0')
  FROM generate_series(1, 40) n
ON CONFLICT (guest_id) DO NOTHING;

INSERT INTO workforce.staff (staff_id, tenant_id, principal_id, home_property_id, preferred_name, bookable) VALUES
  ('01920000-0000-7000-8000-000000000601', '01920000-0000-7000-8000-000000000001', '01920000-0000-7000-8000-000000000203', '01920000-0000-7000-8000-000000000101', 'Lena',   true),
  ('01920000-0000-7000-8000-000000000602', '01920000-0000-7000-8000-000000000001', '01920000-0000-7000-8000-000000000204', '01920000-0000-7000-8000-000000000101', 'Marco',  true),
  ('01920000-0000-7000-8000-000000000603', '01920000-0000-7000-8000-000000000001', '01920000-0000-7000-8000-000000000205', '01920000-0000-7000-8000-000000000101', 'Priya',  true),
  ('01920000-0000-7000-8000-000000000604', '01920000-0000-7000-8000-000000000001', '01920000-0000-7000-8000-000000000201', '01920000-0000-7000-8000-000000000101', 'Dana',   false),
  ('01920000-0000-7000-8000-000000000605', '01920000-0000-7000-8000-000000000001', '01920000-0000-7000-8000-000000000202', '01920000-0000-7000-8000-000000000101', 'Morgan', false),
  ('01920000-0000-7000-8000-000000000606', '01920000-0000-7000-8000-000000000001', '01920000-0000-7000-8000-000000000206', '01920000-0000-7000-8000-000000000101', 'Riley',  false),
  ('01920000-0000-7000-8000-000000000607', '01920000-0000-7000-8000-000000000001', '01920000-0000-7000-8000-000000000207', NULL,                                   'Sam',    false),
  ('01920000-0000-7000-8000-000000000608', '01920000-0000-7000-8000-000000000001', '01920000-0000-7000-8000-000000000208', NULL,                                   'Ada',    false),
  ('01920000-0000-7000-8000-000000000609', '01920000-0000-7000-8000-000000000001', '01920000-0000-7000-8000-000000000209', '01920000-0000-7000-8000-000000000101', 'Hana',   false)
ON CONFLICT (staff_id) DO NOTHING;

-- Roles, approved (the approver differs from the proposer, SEC-014).
INSERT INTO workforce.staff_role_assignment (staff_role_assignment_id, tenant_id, property_id, staff_id, role_code, status, approved_by, approved_at)
VALUES
  ('01920000-0000-7000-8000-000000000611', '01920000-0000-7000-8000-000000000001', '01920000-0000-7000-8000-000000000101', '01920000-0000-7000-8000-000000000601', 'provider',    'Active', '01920000-0000-7000-8000-000000000202', now()),
  ('01920000-0000-7000-8000-000000000612', '01920000-0000-7000-8000-000000000001', '01920000-0000-7000-8000-000000000101', '01920000-0000-7000-8000-000000000602', 'provider',    'Active', '01920000-0000-7000-8000-000000000202', now()),
  ('01920000-0000-7000-8000-000000000613', '01920000-0000-7000-8000-000000000001', '01920000-0000-7000-8000-000000000101', '01920000-0000-7000-8000-000000000603', 'provider',    'Active', '01920000-0000-7000-8000-000000000202', now()),
  ('01920000-0000-7000-8000-000000000614', '01920000-0000-7000-8000-000000000001', '01920000-0000-7000-8000-000000000101', '01920000-0000-7000-8000-000000000604', 'front_desk',  'Active', '01920000-0000-7000-8000-000000000202', now()),
  ('01920000-0000-7000-8000-000000000615', '01920000-0000-7000-8000-000000000001', NULL,                                   '01920000-0000-7000-8000-000000000605', 'spa_manager', 'Active', '01920000-0000-7000-8000-000000000201', now()),
  ('01920000-0000-7000-8000-000000000616', '01920000-0000-7000-8000-000000000001', NULL,                                   '01920000-0000-7000-8000-000000000605', 'platform_admin', 'Active', '01920000-0000-7000-8000-000000000208', now()),
  ('01920000-0000-7000-8000-000000000617', '01920000-0000-7000-8000-000000000001', '01920000-0000-7000-8000-000000000101', '01920000-0000-7000-8000-000000000606', 'scheduler',   'Active', '01920000-0000-7000-8000-000000000202', now()),
  ('01920000-0000-7000-8000-000000000618', '01920000-0000-7000-8000-000000000001', NULL,                                   '01920000-0000-7000-8000-000000000607', 'finance',     'Active', '01920000-0000-7000-8000-000000000202', now()),
  ('01920000-0000-7000-8000-000000000619', '01920000-0000-7000-8000-000000000001', NULL,                                   '01920000-0000-7000-8000-000000000608', 'platform_admin', 'Active', '01920000-0000-7000-8000-000000000202', now()),
  ('01920000-0000-7000-8000-000000000620', '01920000-0000-7000-8000-000000000001', '01920000-0000-7000-8000-000000000101', '01920000-0000-7000-8000-000000000609', 'housekeeping', 'Active', '01920000-0000-7000-8000-000000000202', now())
ON CONFLICT (staff_role_assignment_id) DO NOTHING;

-- CON-003 source of truth. Priya is deliberately NOT qualified for massage, so
-- the unqualified-provider refusal is demonstrable on the demo board.
INSERT INTO workforce.staff_qualification (tenant_id, staff_id, service_id, effective_range)
SELECT '01920000-0000-7000-8000-000000000001', q.staff::uuid, q.service::uuid, tstzrange('-infinity', 'infinity')
  FROM (VALUES
    ('01920000-0000-7000-8000-000000000601', '01920000-0000-7000-8000-000000000401'),
    ('01920000-0000-7000-8000-000000000601', '01920000-0000-7000-8000-000000000402'),
    ('01920000-0000-7000-8000-000000000601', '01920000-0000-7000-8000-000000000404'),
    ('01920000-0000-7000-8000-000000000601', '01920000-0000-7000-8000-000000000405'),
    ('01920000-0000-7000-8000-000000000602', '01920000-0000-7000-8000-000000000401'),
    ('01920000-0000-7000-8000-000000000602', '01920000-0000-7000-8000-000000000404'),
    ('01920000-0000-7000-8000-000000000602', '01920000-0000-7000-8000-000000000405'),
    ('01920000-0000-7000-8000-000000000603', '01920000-0000-7000-8000-000000000403'),
    ('01920000-0000-7000-8000-000000000603', '01920000-0000-7000-8000-000000000406')) q(staff, service)
 WHERE NOT EXISTS (SELECT 1 FROM workforce.staff_qualification x
                    WHERE x.staff_id = q.staff::uuid AND x.service_id = q.service::uuid AND x.status = 'Active');

INSERT INTO guest.guest (guest_id, tenant_id, home_property_id, preferred_name, display_alias, public_queue_id) VALUES
  ('01920000-0000-7000-8000-000000000701', '01920000-0000-7000-8000-000000000001', '01920000-0000-7000-8000-000000000101', 'Ava',   'Guest 4821', 'Q4821'),
  ('01920000-0000-7000-8000-000000000702', '01920000-0000-7000-8000-000000000001', '01920000-0000-7000-8000-000000000101', 'Ben',   'Guest 4822', 'Q4822'),
  ('01920000-0000-7000-8000-000000000703', '01920000-0000-7000-8000-000000000001', '01920000-0000-7000-8000-000000000101', 'Chloe', 'Guest 4823', 'Q4823'),
  ('01920000-0000-7000-8000-000000000704', '01920000-0000-7000-8000-000000000001', '01920000-0000-7000-8000-000000000101', 'Dev',   'Guest 4824', 'Q4824'),
  ('01920000-0000-7000-8000-000000000705', '01920000-0000-7000-8000-000000000001', '01920000-0000-7000-8000-000000000101', 'Ezra',  'Guest 4825', 'Q4825')
ON CONFLICT (guest_id) DO NOTHING;

-- Today's board at Riverside, in the property's own zone, conflict-free so a
-- demo conflict is something the operator creates rather than has to clear.
INSERT INTO scheduling.appointment
    (appointment_id, tenant_id, property_id, confirmation_number, guest_id, service_id, provider_id, room_id,
     duration_minutes, start_at, end_at, entered_timezone, source, price_minor, currency_code, status, correlation_id)
SELECT a.id::uuid, '01920000-0000-7000-8000-000000000001', '01920000-0000-7000-8000-000000000101', a.conf,
       a.guest::uuid, a.svc::uuid, a.prov::uuid, a.room::uuid, a.mins,
       (CURRENT_DATE + a.at::time) AT TIME ZONE 'America/New_York', 'epoch', 'America/New_York', 'Desk',
       s.base_price_minor, 'USD', a.status, 'dev-seed'
  FROM (VALUES
    ('01920000-0000-7000-8000-000000000801', 'AAR000000001', '01920000-0000-7000-8000-000000000701', '01920000-0000-7000-8000-000000000401', '01920000-0000-7000-8000-000000000601', '01920000-0000-7000-8000-000000000506', 90, '13:00', 'InService'),
    ('01920000-0000-7000-8000-000000000802', 'AAR000000002', '01920000-0000-7000-8000-000000000702', '01920000-0000-7000-8000-000000000403', '01920000-0000-7000-8000-000000000603', '01920000-0000-7000-8000-000000000502', 45, '13:30', 'Confirmed'),
    ('01920000-0000-7000-8000-000000000803', 'AAR000000003', '01920000-0000-7000-8000-000000000703', '01920000-0000-7000-8000-000000000402', NULL,                                   '01920000-0000-7000-8000-000000000505', 60, '15:00', 'Confirmed'),
    ('01920000-0000-7000-8000-000000000804', 'AAR000000004', '01920000-0000-7000-8000-000000000704', '01920000-0000-7000-8000-000000000404', '01920000-0000-7000-8000-000000000602', '01920000-0000-7000-8000-000000000503', 60, '14:15', 'Confirmed'),
    ('01920000-0000-7000-8000-000000000805', 'AAR000000005', '01920000-0000-7000-8000-000000000705', '01920000-0000-7000-8000-000000000405', '01920000-0000-7000-8000-000000000602', '01920000-0000-7000-8000-000000000504', 60, '16:00', 'Confirmed')
  ) a(id, conf, guest, svc, prov, room, mins, at, status)
  JOIN catalog.service s ON s.service_id = a.svc::uuid
ON CONFLICT (appointment_id) DO NOTHING;

COMMIT;
