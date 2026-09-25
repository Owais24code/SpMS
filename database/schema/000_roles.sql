-- 000 roles. Cluster-level, so idempotent. Run by the server admin before the
-- schema files, which run as spms_owner.
--
--   spms_owner    owns every schema and table; migrations run as this role.
--                 Never used by the API at runtime.
--   spms_app      the API's runtime role. NOBYPASSRLS and not an owner, so every row
--                 it touches passes the tenant/property policies (spec §Authorization
--                 and retention: never a table-owner or BYPASSRLS account).
--   spms_intake   health/intake data (SEC-008). Granted to spms_app WITHOUT inherit:
--                 the intake module must SET LOCAL ROLE spms_intake inside its unit of
--                 work, so an ordinary query path cannot read intake tables at all.
--   spms_reporting read-only reporting and export.
--   spms_erasure  privacy worker: reads what it must scan; changes rows only through
--                 core.redact_* functions (GOV212-ERASURE).
--   spms_outbox   the outbox publisher. Polls core.event_outbox across tenants, so it
--                 bypasses RLS - and holds privileges on that one table only.
--   spms_definer  owns the narrow SECURITY DEFINER functions that must read across
--                 tenants: identity resolution before a tenant is known, and the
--                 tenant-wide guest-overlap check (CON-005).

DO $$
DECLARE r text;
BEGIN
    FOREACH r IN ARRAY ARRAY['spms_owner','spms_app','spms_intake','spms_reporting',
                             'spms_erasure','spms_outbox','spms_definer']
    LOOP
        IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = r) THEN
            EXECUTE format('CREATE ROLE %I NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE', r);
        END IF;
    END LOOP;
END $$;

ALTER ROLE spms_app       NOBYPASSRLS;
ALTER ROLE spms_intake    NOBYPASSRLS;
ALTER ROLE spms_reporting NOBYPASSRLS;
ALTER ROLE spms_erasure   NOBYPASSRLS;
ALTER ROLE spms_outbox    BYPASSRLS;
ALTER ROLE spms_definer   BYPASSRLS;

-- PG16 membership without inheritance: spms_app may SET ROLE spms_intake but does
-- not hold its privileges by default.
GRANT spms_intake TO spms_app WITH INHERIT FALSE, SET TRUE;

-- The migration role hands SECURITY DEFINER functions to their owner.
GRANT spms_definer TO spms_owner WITH INHERIT FALSE, SET TRUE;

-- Trusted extensions: a database owner may create them (Azure Flexible Server
-- allow-lists both).
CREATE EXTENSION IF NOT EXISTS btree_gist;   -- '=' on uuid/text inside exclusion constraints
CREATE EXTENSION IF NOT EXISTS pg_trgm;      -- universal search on names (UX-002)
REVOKE CREATE ON SCHEMA public FROM PUBLIC;
