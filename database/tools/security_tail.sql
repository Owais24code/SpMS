
-- ---------------------------------------------------------------------------
-- Identity resolution before a tenant is known.
-- ---------------------------------------------------------------------------
-- Authentication yields (issuer, subject) or a magic-link hash; nothing yet says
-- which tenant the caller belongs to, so RLS would hide every row. These narrow
-- SECURITY DEFINER functions are owned by spms_definer (BYPASSRLS, and
-- SELECT on the identity tables only), pin search_path, and return ids only.
-- Resolve an authenticated IdP subject to a principal before any tenant is set.
CREATE FUNCTION core.resolve_principal(p_issuer text, p_subject text)
RETURNS TABLE (principal_id uuid, tenant_id uuid, principal_type text, status text)
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $$
    SELECT p.principal_id, p.tenant_id, p.principal_type, p.status
      FROM core.principal_login l
      JOIN core.principal p ON p.tenant_id = l.tenant_id AND p.principal_id = l.principal_id
     WHERE l.idp_issuer = p_issuer AND l.idp_subject = p_subject AND l.status = 'Active'
$$;

-- Consume a guest magic link: single use, unexpired, unrevoked. Atomic, so two
-- concurrent clicks cannot both succeed.
CREATE FUNCTION guest.resolve_magic_link(p_token_hash bytea)
RETURNS TABLE (guest_id uuid, tenant_id uuid, principal_id uuid, purpose text,
               scope_entity_type text, scope_entity_id uuid)
LANGUAGE sql VOLATILE SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $$
    UPDATE guest.guest_magic_link l
       SET consumed_at = now()
     WHERE l.token_hash = p_token_hash
       AND l.consumed_at IS NULL AND l.revoked_at IS NULL AND l.expires_at > now()
    RETURNING l.guest_id, l.tenant_id, l.principal_id, l.purpose, l.scope_entity_type, l.scope_entity_id
$$;

-- CON-005 is tenant-wide: a guest cannot be in two treatments at once at any of
-- the tenant's properties. Request scope covers one property, so this function
-- answers for the whole CURRENT tenant - intervals only, no appointment detail.
CREATE FUNCTION scheduling.guest_busy_intervals(p_guest_id uuid, p_from timestamptz, p_to timestamptz)
RETURNS TABLE (property_id uuid, appointment_id uuid, start_at timestamptz, end_at timestamptz)
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $$
    SELECT a.property_id, a.appointment_id, a.start_at, a.end_at
      FROM scheduling.appointment a
     WHERE a.tenant_id = core.current_tenant_id()
       AND a.guest_id = p_guest_id
       AND a.status NOT IN ('Cancelled', 'NoShow')
       AND tstzrange(a.start_at, a.end_at, '[)') && tstzrange(p_from, p_to, '[)')
$$;

GRANT USAGE ON SCHEMA core, guest, scheduling TO spms_definer;
GRANT SELECT ON core.principal, core.principal_login TO spms_definer;
GRANT SELECT, UPDATE (consumed_at) ON guest.guest_magic_link TO spms_definer;
GRANT SELECT ON scheduling.appointment TO spms_definer;
REVOKE ALL ON FUNCTION core.resolve_principal(text, text) FROM PUBLIC;
REVOKE ALL ON FUNCTION guest.resolve_magic_link(bytea) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION core.resolve_principal(text, text) TO spms_app;
GRANT EXECUTE ON FUNCTION guest.resolve_magic_link(bytea) TO spms_app;
REVOKE ALL ON FUNCTION scheduling.guest_busy_intervals(uuid, timestamptz, timestamptz) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION scheduling.guest_busy_intervals(uuid, timestamptz, timestamptz) TO spms_app;

-- Ownership moves last: ACL entries granted above transfer to the new owner.
GRANT CREATE ON SCHEMA core, guest, scheduling TO spms_definer;
ALTER FUNCTION core.resolve_principal(text, text) OWNER TO spms_definer;
ALTER FUNCTION guest.resolve_magic_link(bytea) OWNER TO spms_definer;
ALTER FUNCTION scheduling.guest_busy_intervals(uuid, timestamptz, timestamptz) OWNER TO spms_definer;
REVOKE CREATE ON SCHEMA core, guest, scheduling FROM spms_definer;

-- ---------------------------------------------------------------------------
-- Outbox publisher: one table, across tenants.
-- ---------------------------------------------------------------------------
GRANT USAGE ON SCHEMA core TO spms_outbox;
GRANT SELECT, UPDATE (published_at, attempt_count, next_attempt_at, last_error) ON core.event_outbox TO spms_outbox;

-- ---------------------------------------------------------------------------
-- Redaction (guest erasure). The only path that may change append-only rows.
-- ---------------------------------------------------------------------------
CREATE FUNCTION core.redact_audit_subject(p_entity_type text, p_entity_id uuid) RETURNS bigint
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $$
DECLARE n bigint;
BEGIN
    PERFORM set_config('spms.redacting', 'on', true);
    UPDATE core.audit_event
       SET before_data = NULL, after_data = NULL, reason_text = NULL
     WHERE tenant_id = core.current_tenant_id()
       AND entity_type = p_entity_type AND entity_id = p_entity_id;
    GET DIAGNOSTICS n = ROW_COUNT;
    PERFORM set_config('spms.redacting', 'off', true);
    RETURN n;
END $$;
REVOKE ALL ON FUNCTION core.redact_audit_subject(text, uuid) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION core.redact_audit_subject(text, uuid) TO spms_erasure;

-- Controlled deletion of transient rows: lines of a Draft order (a cart) and
-- expired idempotency claims. order_line refuses DELETE once its order leaves Draft. Everything else is status + audit, never DELETE (no hard delete).
GRANT DELETE ON commerce.order_line, core.idempotency_record TO spms_app;

-- Partitions are reached only through their parent, whose grants and policies
-- apply; no role other than the owner holds privileges on a partition itself.
