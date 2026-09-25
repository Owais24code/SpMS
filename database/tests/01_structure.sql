-- Structural invariants of the whole schema, read from the catalogue.
\set ON_ERROR_STOP 1
DO $$
DECLARE bad text;
    app_schemas text[] := ARRAY['core','catalog','resources','workforce','guest','scheduling',
                                'intake','inventory','commerce','messaging','reporting'];
BEGIN
    -- 1. Every table that carries tenant_id has RLS enabled AND forced (partitions are reached via parents).
    SELECT string_agg(n.nspname || '.' || c.relname, ', ') INTO bad
      FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
     WHERE n.nspname = ANY (app_schemas) AND c.relkind IN ('r', 'p') AND NOT c.relispartition
       AND EXISTS (SELECT 1 FROM pg_attribute a WHERE a.attrelid = c.oid AND a.attname = 'tenant_id' AND NOT a.attisdropped)
       AND NOT (c.relrowsecurity AND c.relforcerowsecurity);
    ASSERT bad IS NULL, 'tables without forced RLS: ' || bad;

    -- 2. Everything is owned by spms_owner, never by the runtime role.
    SELECT string_agg(n.nspname || '.' || c.relname, ', ') INTO bad
      FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
     WHERE n.nspname = ANY (app_schemas) AND c.relkind IN ('r', 'p') AND pg_get_userbyid(c.relowner) <> 'spms_owner';
    ASSERT bad IS NULL, 'tables not owned by spms_owner: ' || bad;

    -- 3. The runtime role cannot DELETE outside the transient allow-list (no hard delete).
    SELECT string_agg(table_schema || '.' || table_name, ', ') INTO bad
      FROM information_schema.role_table_grants
     WHERE grantee = 'spms_app' AND privilege_type = 'DELETE'
       AND table_schema || '.' || table_name NOT IN
           ('commerce.order_line', 'core.idempotency_record');
    ASSERT bad IS NULL, 'unexpected DELETE grants: ' || bad;

    -- 4. The runtime role holds nothing on intake tables; only spms_intake does.
    SELECT string_agg(DISTINCT table_name, ', ') INTO bad
      FROM information_schema.role_table_grants WHERE grantee = 'spms_app' AND table_schema = 'intake';
    ASSERT bad IS NULL, 'spms_app has direct intake grants: ' || bad;

    -- 5. Every foreign key column set is covered by an index whose leading columns match.
    SELECT string_agg(conrelid::regclass || ' ' || conname, ', ') INTO bad
      FROM pg_constraint k
     WHERE k.contype = 'f' AND connamespace::regnamespace::text = ANY (app_schemas)
       AND NOT EXISTS (SELECT 1 FROM pg_index i
                        WHERE i.indrelid = k.conrelid
                          AND (i.indkey::int2[])[0:cardinality(k.conkey) - 1] = k.conkey);
    ASSERT bad IS NULL, 'foreign keys without a supporting index: ' || bad;

    -- 6. Module DAG, checked in the database as well as the generator.
    WITH allowed(m, dep) AS (VALUES
        ('catalog','core'), ('resources','core'), ('resources','catalog'), ('workforce','core'), ('workforce','catalog'),
        ('guest','core'),
        ('scheduling','core'), ('scheduling','catalog'), ('scheduling','resources'), ('scheduling','workforce'), ('scheduling','guest'),
        ('intake','core'), ('intake','catalog'), ('intake','guest'), ('intake','workforce'), ('intake','scheduling'),
        ('inventory','core'), ('inventory','catalog'), ('inventory','resources'), ('inventory','scheduling'),
        ('commerce','core'), ('commerce','catalog'), ('commerce','resources'), ('commerce','guest'), ('commerce','scheduling'),
        ('commerce','inventory'),
        ('messaging','core'), ('messaging','catalog'), ('messaging','guest'), ('messaging','scheduling'),
        ('reporting','core'))
    SELECT string_agg(DISTINCT s.nspname || ' -> ' || t.nspname, ', ') INTO bad
      FROM pg_constraint k
      JOIN pg_class sc ON sc.oid = k.conrelid JOIN pg_namespace s ON s.oid = sc.relnamespace
      JOIN pg_class tc ON tc.oid = k.confrelid JOIN pg_namespace t ON t.oid = tc.relnamespace
     WHERE k.contype = 'f' AND s.nspname <> t.nspname
       AND NOT EXISTS (SELECT 1 FROM allowed a WHERE a.m = s.nspname AND a.dep = t.nspname);
    ASSERT bad IS NULL, 'foreign keys against the module DAG: ' || bad;

    -- 7. Append-only tables carry the raising trigger.
    SELECT string_agg(t.tbl, ', ') INTO bad FROM (VALUES
        ('core.audit_event'), ('core.audit_seal'), ('core.event_inbox'), ('intake.treatment_note'),
        ('inventory.inventory_ledger_entry'), ('commerce.payment_transaction')) t(tbl)
     WHERE NOT EXISTS (SELECT 1 FROM pg_trigger g WHERE g.tgrelid = t.tbl::regclass
                          AND g.tgfoid = 'core.forbid_mutation'::regproc);
    ASSERT bad IS NULL, 'append-only tables without the trigger: ' || bad;

    -- 8. Monthly partitions exist ahead, and every DEFAULT partition is empty.
    ASSERT (SELECT count(*) FROM pg_inherits WHERE inhparent = 'core.audit_event'::regclass) >= 5,
           'audit_event is missing monthly partitions';
    ASSERT NOT EXISTS (SELECT 1 FROM core.audit_event_default), 'rows in audit_event_default';

    -- 10. The refactor target: one table per entity. Adding a table is a design decision, not a default.
    ASSERT (SELECT count(*) FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
             WHERE n.nspname = ANY (app_schemas) AND c.relkind IN ('r', 'p') AND NOT c.relispartition) = 61,
           'table count changed - update the design and this assertion together';

    -- 9. Every exclusion constraint the design relies on is present.
    SELECT string_agg(x, ', ') INTO bad FROM unnest(ARRAY[
        'appointment_room_no_overlap', 'capability_ownership_single_authority', 'setting_single_active',
        'staff_qualification_no_overlap', 'work_schedule_no_overlap', 'maintenance_window_no_overlap',
        'staff_role_assignment_no_overlap', 'guest_relationship_no_overlap']) x
     WHERE NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = x AND contype = 'x');
    ASSERT bad IS NULL, 'missing exclusion constraints: ' || bad;
END $$;
