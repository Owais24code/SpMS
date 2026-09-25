-- Identity resolution before a tenant is known; single-use magic links.
\set ON_ERROR_STOP 1
BEGIN;
\ir fixture.psql

INSERT INTO guest.guest_contact_point (guest_contact_point_id, tenant_id, guest_id, contact_type, contact_cipher,
       key_version, lookup_hash, display_hint, is_primary, verified_at)
VALUES ('a0000000-0000-7000-8000-0000000000e2', 'a0000000-0000-7000-8000-000000000000', 'a0000000-0000-7000-8000-0000000000e1',
        'Email', '\x00', 'k1', repeat('a', 64), 'g***@example.com', true, now());
INSERT INTO guest.guest_magic_link (tenant_id, guest_id, principal_id, guest_contact_point_id, token_hash, purpose,
       issued_at, expires_at)
VALUES ('a0000000-0000-7000-8000-000000000000', 'a0000000-0000-7000-8000-0000000000e1', 'a0000000-0000-7000-8000-00000000aa02',
        'a0000000-0000-7000-8000-0000000000e2', sha256('live-token'), 'SignIn', now(), now() + interval '15 minutes'),
       ('a0000000-0000-7000-8000-000000000000', 'a0000000-0000-7000-8000-0000000000e1', 'a0000000-0000-7000-8000-00000000aa02',
        'a0000000-0000-7000-8000-0000000000e2', sha256('old-token'), 'SignIn', now() - interval '1 hour', now() - interval '1 second');

SET LOCAL ROLE spms_app;   -- no begin_scope: the caller is not yet known
DO $$ BEGIN
    ASSERT (SELECT tenant_id FROM core.resolve_principal('https://login.microsoftonline.com/t/v2.0', 'oid-provider-one'))
           = 'a0000000-0000-7000-8000-000000000000', 'staff subject did not resolve';
    ASSERT NOT EXISTS (SELECT 1 FROM core.resolve_principal('https://login.microsoftonline.com/t/v2.0', 'unknown')),
           'unknown subject resolved';
    ASSERT (SELECT count(*) FROM core.principal) = 0, 'resolver leaked table access to the caller';

    ASSERT (SELECT guest_id FROM guest.resolve_magic_link(sha256('live-token')))
           = 'a0000000-0000-7000-8000-0000000000e1', 'live link did not resolve';
    ASSERT NOT EXISTS (SELECT 1 FROM guest.resolve_magic_link(sha256('live-token'))), 'link was usable twice';
    ASSERT NOT EXISTS (SELECT 1 FROM guest.resolve_magic_link(sha256('old-token'))), 'expired link resolved';
END $$;

-- Only hashes are stored, and a token hash must be SHA-256 sized.
RESET ROLE;
DO $$ BEGIN
    INSERT INTO guest.guest_magic_link (tenant_id, guest_id, principal_id, guest_contact_point_id, token_hash, purpose, expires_at)
    VALUES ('a0000000-0000-7000-8000-000000000000', 'a0000000-0000-7000-8000-0000000000e1', 'a0000000-0000-7000-8000-00000000aa02',
            'a0000000-0000-7000-8000-0000000000e2', convert_to('plain-token', 'UTF8'), 'SignIn', now() + interval '5 minutes');
    RAISE EXCEPTION 'a non-hash token was stored';
EXCEPTION WHEN check_violation THEN NULL; END $$;
ROLLBACK;
