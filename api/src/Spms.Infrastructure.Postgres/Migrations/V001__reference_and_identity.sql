-- V001  Tenant, property and the reference data the scheduling slice needs.
--
-- Scope note: the specification names 284 physical tables. This migration set
-- deliberately builds only the ten that the R1 scheduling slice actually
-- reads or writes. A table nobody queries yet is a maintenance liability and
-- a false signal of progress, so the rest arrive with the slices that use them.

CREATE EXTENSION IF NOT EXISTS btree_gist;

CREATE TABLE tenant (
    tenant_id     text PRIMARY KEY,
    display_name  text        NOT NULL,
    created_utc   timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE property (
    tenant_id     text        NOT NULL REFERENCES tenant (tenant_id),
    property_id   text        NOT NULL,
    display_name  text        NOT NULL,
    -- IANA zone. The board is always read in the property's zone, never the
    -- viewer's, so this is operational data and not a display preference.
    time_zone_id  text        NOT NULL,
    open_minute   int         NOT NULL DEFAULT 540,   -- 09:00 local
    close_minute  int         NOT NULL DEFAULT 1020,  -- 17:00 local
    created_utc   timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (tenant_id, property_id),
    CONSTRAINT property_business_day CHECK (open_minute >= 0 AND close_minute > open_minute AND close_minute <= 1440)
);

CREATE TABLE service (
    tenant_id        text NOT NULL REFERENCES tenant (tenant_id),
    service_id       text NOT NULL,
    display_name     text NOT NULL,
    duration_minutes int  NOT NULL,
    PRIMARY KEY (tenant_id, service_id),
    -- A zero or negative duration makes end_utc <= start_utc, which silently
    -- disables overlap detection for every appointment of that service.
    CONSTRAINT service_duration_positive CHECK (duration_minutes > 0)
);

CREATE TABLE room (
    tenant_id   text NOT NULL,
    property_id text NOT NULL,
    room_id     text NOT NULL,
    display_name text NOT NULL,
    PRIMARY KEY (tenant_id, property_id, room_id),
    FOREIGN KEY (tenant_id, property_id) REFERENCES property (tenant_id, property_id)
);

CREATE TABLE staff (
    tenant_id    text NOT NULL,
    property_id  text NOT NULL,
    provider_id  text NOT NULL,
    display_name text NOT NULL,
    -- Terminated staff keep their history but must not take new work.
    assignable   boolean NOT NULL DEFAULT true,
    PRIMARY KEY (tenant_id, property_id, provider_id),
    FOREIGN KEY (tenant_id, property_id) REFERENCES property (tenant_id, property_id)
);

-- CON-003 fails closed: a provider absent from this table is NOT qualified,
-- so the absence of a row is a refusal rather than a permission.
CREATE TABLE staff_qualification (
    tenant_id   text NOT NULL,
    property_id text NOT NULL,
    provider_id text NOT NULL,
    service_id  text NOT NULL,
    granted_utc timestamptz NOT NULL DEFAULT now(),
    expires_utc timestamptz NULL,
    PRIMARY KEY (tenant_id, property_id, provider_id, service_id),
    FOREIGN KEY (tenant_id, property_id, provider_id) REFERENCES staff (tenant_id, property_id, provider_id),
    FOREIGN KEY (tenant_id, service_id) REFERENCES service (tenant_id, service_id)
);

CREATE TABLE guest (
    tenant_id   text NOT NULL REFERENCES tenant (tenant_id),
    guest_id    text NOT NULL,
    -- The alias is what reaches a screen. The id never leaves the server.
    display_alias text NOT NULL,
    created_utc timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (tenant_id, guest_id)
);

-- Buffer policy per property, with an optional per-service override.
-- Spec baselines: 15 minutes room turnover, 10 minutes provider transition.
CREATE TABLE buffer_policy (
    tenant_id                    text NOT NULL,
    property_id                  text NOT NULL,
    service_id                   text NULL,
    room_turnover_minutes        int  NOT NULL,
    provider_transition_minutes  int  NOT NULL,
    FOREIGN KEY (tenant_id, property_id) REFERENCES property (tenant_id, property_id),
    CONSTRAINT buffer_non_negative CHECK (room_turnover_minutes >= 0 AND provider_transition_minutes >= 0)
);

-- One property-wide row and at most one row per service.
CREATE UNIQUE INDEX buffer_policy_property_default
    ON buffer_policy (tenant_id, property_id) WHERE service_id IS NULL;
CREATE UNIQUE INDEX buffer_policy_per_service
    ON buffer_policy (tenant_id, property_id, service_id) WHERE service_id IS NOT NULL;
