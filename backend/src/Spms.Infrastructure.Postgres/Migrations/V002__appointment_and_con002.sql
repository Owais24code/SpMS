-- V002  The appointment aggregate, and CON-002 enforced by the database.
--
-- This migration is the point of the whole exercise. Until now the room-overlap
-- invariant was held by an in-process lock in SchedulingService, which is
-- correct on one node and worthless on two. CON-002 is declared "physically
-- impossible" and overridable by no role, so it belongs in the database where
-- no application bug, no second instance and no direct SQL can get round it.

-- Status is text with a CHECK rather than a native enum type.
--
-- A PostgreSQL enum is the tidier schema, but it requires the driver, the ORM
-- and the EF model to agree on one type mapping, and a disagreement fails at
-- runtime with 42804 rather than at compile time. Text plus a CHECK gives the
-- same database-level guarantee — an invalid status cannot enter the table the
-- room-overlap constraint filters on — with one moving part instead of three.
-- Adding a status means editing this list, which is the right amount of
-- friction for a change to the lifecycle.

CREATE TABLE appointment (
    tenant_id        text NOT NULL,
    property_id      text NOT NULL,
    appointment_id   text NOT NULL,

    guest_id         text NOT NULL,
    service_id       text NOT NULL,
    duration_minutes int  NOT NULL,

    provider_id      text NULL,
    room_id          text NULL,

    start_utc        timestamptz NOT NULL,
    -- Maintained by the trigger below, not by the application: the exclusion
    -- constraint indexes it, and a value the application derived could
    -- disagree with the constraint that depends on it.
    --
    -- Not a GENERATED column: `timestamptz + interval` is STABLE rather than
    -- IMMUTABLE in PostgreSQL (adding months or days depends on the session
    -- time zone), and a generation expression must be immutable. A trigger has
    -- no such restriction, and `tstzrange(timestamptz, timestamptz)` — which is
    -- what the constraint actually indexes — is immutable.
    end_utc          timestamptz NOT NULL,

    status           text NOT NULL DEFAULT 'Draft',

    -- Optimistic concurrency. A plain integer the aggregate owns, rather than
    -- xmin, because the value travels to the client as an ETag and back as
    -- If-Match, and it has to survive a round trip through JSON.
    row_version      int NOT NULL DEFAULT 1,

    confirmation_number text NULL,
    correlation_id      text NOT NULL,
    created_utc         timestamptz NOT NULL,
    updated_utc         timestamptz NOT NULL,

    PRIMARY KEY (tenant_id, property_id, appointment_id),
    FOREIGN KEY (tenant_id, property_id) REFERENCES property (tenant_id, property_id),
    FOREIGN KEY (tenant_id, guest_id)    REFERENCES guest (tenant_id, guest_id),
    FOREIGN KEY (tenant_id, service_id)  REFERENCES service (tenant_id, service_id),
    FOREIGN KEY (tenant_id, property_id, room_id)     REFERENCES room (tenant_id, property_id, room_id),
    FOREIGN KEY (tenant_id, property_id, provider_id) REFERENCES staff (tenant_id, property_id, provider_id),

    CONSTRAINT appointment_duration_positive CHECK (duration_minutes > 0),
    CONSTRAINT appointment_row_version_positive CHECK (row_version >= 1),
    -- Immutable, so it is allowed here, and it catches a corrupted end_utc
    -- even though the trigger is what sets it.
    CONSTRAINT appointment_interval_forward CHECK (end_utc > start_utc),
    CONSTRAINT appointment_status_known CHECK (status IN (
        'Draft', 'Held', 'Confirmed', 'CheckedIn', 'Ready', 'InService',
        'Completed', 'Cancelled', 'NoShow'))
);

-- end_utc is derived on every insert and update, so no caller can set it to
-- something the room-overlap constraint would then index incorrectly.
CREATE FUNCTION appointment_set_end_utc() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    NEW.end_utc := NEW.start_utc + make_interval(mins => NEW.duration_minutes);
    RETURN NEW;
END;
$$;

CREATE TRIGGER appointment_end_utc
    BEFORE INSERT OR UPDATE OF start_utc, duration_minutes, end_utc ON appointment
    FOR EACH ROW EXECUTE FUNCTION appointment_set_end_utc();

-- Guest-facing and quoted at the desk, so it must not repeat within a tenant.
CREATE UNIQUE INDEX appointment_confirmation_unique
    ON appointment (tenant_id, confirmation_number)
    WHERE confirmation_number IS NOT NULL;

-- =====================================================================
-- CON-002  room double-booking, HARD
-- =====================================================================
-- A room cannot hold two treatments. Enforced with a GiST exclusion
-- constraint over the half-open interval [start_utc, end_utc), scoped to one
-- tenant, property and room.
--
-- The WHERE clause matters as much as the constraint: Cancelled and NoShow
-- rows hold nothing, so they must not block. Completed rows DO stay in scope,
-- because the treatment physically occupied the room and a later booking that
-- overlaps it would be a record of two treatments in one place.
--
-- Violations surface as SQLSTATE 23P01, which the adapter translates back to
-- CON-002 so the operator sees a conflict rather than a 500.
ALTER TABLE appointment
    ADD CONSTRAINT appointment_room_no_overlap
    EXCLUDE USING gist (
        tenant_id   WITH =,
        property_id WITH =,
        room_id     WITH =,
        tstzrange(start_utc, end_utc, '[)') WITH &&
    )
    WHERE (room_id IS NOT NULL AND status NOT IN ('Cancelled', 'NoShow'));

-- =====================================================================
-- CON-001  provider overlap, SOFT  -- deliberately NOT a constraint
-- =====================================================================
-- A provider double-booking is overridable with an audited reason, so it must
-- remain committable. Making this an exclusion constraint too would be the
-- easy mistake: it would turn a soft conflict into a hard one and remove the
-- override the specification requires. The index below exists only to make
-- the application's conflict scan fast.
CREATE INDEX appointment_provider_window
    ON appointment (tenant_id, property_id, provider_id, start_utc)
    WHERE provider_id IS NOT NULL AND status NOT IN ('Cancelled', 'NoShow');

-- The board's main read: everything overlapping a window at one property.
CREATE INDEX appointment_property_window
    ON appointment USING gist (
        tenant_id gist_text_ops,
        property_id gist_text_ops,
        tstzrange(start_utc, end_utc, '[)')
    );

CREATE INDEX appointment_guest_window
    ON appointment (tenant_id, guest_id, start_utc)
    WHERE status NOT IN ('Cancelled', 'NoShow');
