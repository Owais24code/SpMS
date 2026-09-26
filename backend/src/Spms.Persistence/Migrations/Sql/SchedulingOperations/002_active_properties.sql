-- The per-property job runner's list of where to run. Ids only.
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
