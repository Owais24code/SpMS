
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

-- Tenant and property from their public codes, before any tenant is in scope:
-- the guest web and the kiosk arrive at /t/<tenant>/p/<property> with no
-- identity yet, and RLS would otherwise hide the very rows that say which
-- tenant they are at. Ids and presentation fields only.
CREATE FUNCTION core.resolve_property(p_tenant_code text, p_property_code text)
RETURNS TABLE (tenant_id uuid, property_id uuid, property_name text, timezone text, operating_mode text)
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $$
    SELECT t.tenant_id, p.property_id, p.name, p.timezone, p.operating_mode
      FROM core.tenant t
      JOIN core.property p ON p.tenant_id = t.tenant_id
     WHERE t.code = p_tenant_code AND p.code = p_property_code
       AND t.status = 'Active' AND p.status = 'Active'
$$;

GRANT SELECT ON core.tenant, core.property TO spms_definer;
REVOKE ALL ON FUNCTION core.resolve_property(text, text) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION core.resolve_property(text, text) TO spms_app;
GRANT CREATE ON SCHEMA core TO spms_definer;
ALTER FUNCTION core.resolve_property(text, text) OWNER TO spms_definer;
REVOKE CREATE ON SCHEMA core FROM spms_definer;

-- The active tenants, for the outbox publisher's full authorization
-- reconciliation. Ids only; granted to the publisher alone.
CREATE FUNCTION core.active_tenants()
RETURNS SETOF uuid
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $$
    SELECT t.tenant_id FROM core.tenant t WHERE t.status = 'Active'
$$;
REVOKE ALL ON FUNCTION core.active_tenants() FROM PUBLIC;
GRANT EXECUTE ON FUNCTION core.active_tenants() TO spms_outbox;
GRANT CREATE ON SCHEMA core TO spms_definer;
ALTER FUNCTION core.active_tenants() OWNER TO spms_definer;
REVOKE CREATE ON SCHEMA core FROM spms_definer;

-- The active properties of every active tenant, for the per-property job
-- runner (hold expiry, token expiry). Ids only; granted to the publisher role.
CREATE FUNCTION core.active_properties()
RETURNS TABLE (tenant_id uuid, property_id uuid)
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $$
    SELECT p.tenant_id, p.property_id
      FROM core.property p JOIN core.tenant t ON t.tenant_id = p.tenant_id
     WHERE t.status = 'Active' AND p.status = 'Active'
$$;
REVOKE ALL ON FUNCTION core.active_properties() FROM PUBLIC;
GRANT EXECUTE ON FUNCTION core.active_properties() TO spms_outbox;
GRANT CREATE ON SCHEMA core TO spms_definer;
ALTER FUNCTION core.active_properties() OWNER TO spms_definer;
REVOKE CREATE ON SCHEMA core FROM spms_definer;

-- The desk's view of intake: status only, never an answer (SEC-008). The API
-- role cannot read intake tables at all; this answers "is the form done" for
-- the appointments on the arrivals list, inside the caller's own scope.
CREATE FUNCTION scheduling.intake_status(p_appointment_ids uuid[])
RETURNS TABLE (appointment_id uuid, status text, requires_review boolean)
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $$
    SELECT DISTINCT ON (s.appointment_id) s.appointment_id, s.status, s.requires_review
      FROM intake.intake_submission s
     WHERE s.tenant_id = core.current_tenant_id()
       AND s.property_id = ANY ((core.current_property_ids())::uuid[])
       AND s.appointment_id = ANY (p_appointment_ids)
       AND s.status <> 'Superseded'
     ORDER BY s.appointment_id, s.created_at DESC
$$;
GRANT USAGE ON SCHEMA intake TO spms_definer;
GRANT SELECT ON intake.intake_submission TO spms_definer;
REVOKE ALL ON FUNCTION scheduling.intake_status(uuid[]) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION scheduling.intake_status(uuid[]) TO spms_app;
GRANT CREATE ON SCHEMA scheduling TO spms_definer;
ALTER FUNCTION scheduling.intake_status(uuid[]) OWNER TO spms_definer;
REVOKE CREATE ON SCHEMA scheduling FROM spms_definer;

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
