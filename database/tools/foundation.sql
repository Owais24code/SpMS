-- ---------------------------------------------------------------------------
-- Foundation functions shared by every module.
-- ---------------------------------------------------------------------------

-- UUIDv7 (RFC 9562): 48-bit Unix ms timestamp, version 7, variant 10.
-- The application generates ids itself (a command knows its id before commit);
-- this default exists for SQL-side inserts, seeds and repairs.
CREATE FUNCTION core.uuid_v7() RETURNS uuid
LANGUAGE plpgsql VOLATILE PARALLEL SAFE AS $$
DECLARE
    ms bigint := floor(extract(epoch FROM clock_timestamp()) * 1000);
    b  bytea  := uuid_send(gen_random_uuid());
BEGIN
    b := set_byte(b, 0, ((ms >> 40) & 255)::int);
    b := set_byte(b, 1, ((ms >> 32) & 255)::int);
    b := set_byte(b, 2, ((ms >> 24) & 255)::int);
    b := set_byte(b, 3, ((ms >> 16) & 255)::int);
    b := set_byte(b, 4, ((ms >> 8)  & 255)::int);
    b := set_byte(b, 5, (ms & 255)::int);
    b := set_byte(b, 6, (get_byte(b, 6) & 15) | 112);   -- version 7
    b := set_byte(b, 8, (get_byte(b, 8) & 63) | 128);   -- variant 10
    RETURN encode(b, 'hex')::uuid;
END $$;

-- Request scope. Set with SET LOCAL semantics (is_local = true) inside the
-- request's transaction, so a pooled connection can never carry one request's
-- tenant into the next. Unset means NULL / empty, and every policy fails closed.
CREATE FUNCTION core.current_tenant_id() RETURNS uuid
LANGUAGE sql STABLE PARALLEL SAFE AS $$
    SELECT nullif(current_setting('app.tenant_id', true), '')::uuid
$$;

CREATE FUNCTION core.current_property_ids() RETURNS uuid[]
LANGUAGE sql STABLE PARALLEL SAFE AS $$
    SELECT coalesce(nullif(current_setting('app.property_ids', true), '')::uuid[], '{}'::uuid[])
$$;

CREATE FUNCTION core.current_principal_id() RETURNS uuid
LANGUAGE sql STABLE PARALLEL SAFE AS $$
    SELECT nullif(current_setting('app.principal_id', true), '')::uuid
$$;

-- The one call the API makes after BEGIN. Refuses outside a transaction block,
-- because set_config(..., true) there would silently last only one statement.
CREATE FUNCTION core.begin_scope(p_tenant uuid, p_properties uuid[], p_principal uuid,
                                 p_correlation text DEFAULT NULL) RETURNS void
LANGUAGE plpgsql AS $$
BEGIN
    IF p_tenant IS NULL THEN
        RAISE EXCEPTION 'begin_scope: tenant is required' USING ERRCODE = '22023';
    END IF;
    -- In an implicit single-statement transaction the two timestamps coincide.
    IF transaction_timestamp() = statement_timestamp() THEN
        RAISE EXCEPTION 'begin_scope must run inside an explicit transaction' USING ERRCODE = '25P01';
    END IF;
    PERFORM set_config('app.tenant_id', p_tenant::text, true);
    PERFORM set_config('app.property_ids', coalesce(p_properties, '{}')::text, true);
    PERFORM set_config('app.principal_id', coalesce(p_principal::text, ''), true);
    PERFORM set_config('app.correlation_id', coalesce(p_correlation, ''), true);
END $$;

-- updated_at on every mutable row.
CREATE FUNCTION core.touch() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    NEW.updated_at := now();
    NEW.created_at := OLD.created_at;
    NEW.created_by := OLD.created_by;
    RETURN NEW;
END $$;

-- Versioned rows: optimistic concurrency is enforced by the database, not only
-- by the ORM. Every update must advance version by exactly one, so a writer that
-- forgot the concurrency check (or a hand-written UPDATE) cannot overwrite a
-- newer row silently (DEC-005).
CREATE FUNCTION core.touch_versioned() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    IF NEW.version IS DISTINCT FROM OLD.version + 1 THEN
        RAISE EXCEPTION '%.%: version must advance from % to %, got %',
            TG_TABLE_SCHEMA, TG_TABLE_NAME, OLD.version, OLD.version + 1, NEW.version
            USING ERRCODE = '40001';
    END IF;
    NEW.updated_at := now();
    NEW.created_at := OLD.created_at;
    NEW.created_by := OLD.created_by;
    RETURN NEW;
END $$;

-- Append-only. Raises rather than ignoring, so a bug fails its test. The only
-- permitted change is a redaction performed by core.redact_* functions, which
-- set spms.redacting for the duration of one statement.
CREATE FUNCTION core.forbid_mutation() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'UPDATE' AND current_setting('spms.redacting', true) = 'on' THEN
        RETURN NEW;
    END IF;
    RAISE EXCEPTION '%.% is append-only (% refused)', TG_TABLE_SCHEMA, TG_TABLE_NAME, TG_OP
        USING ERRCODE = '42501';
END $$;

-- Monthly range partitions in UTC. Run ahead by a maintenance job; the DEFAULT
-- partition is a safety net that must stay empty (tests assert it).
CREATE FUNCTION core.ensure_monthly_partitions(p_parent regclass, p_from date, p_months int)
RETURNS void LANGUAGE plpgsql AS $$
DECLARE
    m date;
    sch text;
    rel text;
BEGIN
    SELECT n.nspname, c.relname INTO sch, rel
      FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace WHERE c.oid = p_parent;
    FOR i IN 0 .. p_months - 1 LOOP
        m := (date_trunc('month', p_from) + make_interval(months => i))::date;
        EXECUTE format(
            'CREATE TABLE IF NOT EXISTS %I.%I PARTITION OF %s FOR VALUES FROM (%L) TO (%L)',
            sch, rel || '_' || to_char(m, 'YYYYMM'), p_parent,
            (m::timestamp AT TIME ZONE 'UTC'),
            ((m + interval '1 month')::timestamp AT TIME ZONE 'UTC'));
    END LOOP;
END $$;
