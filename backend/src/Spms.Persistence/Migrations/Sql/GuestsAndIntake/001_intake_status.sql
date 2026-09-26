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
