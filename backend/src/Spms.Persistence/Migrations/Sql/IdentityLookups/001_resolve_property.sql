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
