/**
 * Wire shapes for the SpMS API, transcribed from api/src/Spms.Api/Endpoints/
 * Contracts.cs. These mirror the server exactly — including the fields the
 * board does not use — so a contract change shows up as a type error here
 * rather than as `undefined` on screen.
 *
 * Nothing in this file is a view model. The store maps these onto spa.model.ts
 * once, so a component never sees a DTO.
 */

/** §7.1 state machine. The enum NAMES, not the display strings. */
export const APPOINTMENT_STATUS_WIRE = [
  'Draft', 'Held', 'Confirmed', 'CheckedIn', 'Ready',
  'InService', 'Completed', 'Cancelled', 'NoShow',
] as const;
export type AppointmentStatusWire = (typeof APPOINTMENT_STATUS_WIRE)[number];

/**
 * A single appointment.
 *
 * `guestId` is deliberately absent: the server never serializes it (SEC-020),
 * so any client code that wanted it would be reading a field that cannot
 * arrive. The alias is what the board renders.
 */
export interface AppointmentDto {
  readonly appointmentId: string;
  readonly guestAlias: string;
  readonly serviceId: string;
  readonly serviceName: string;
  readonly durationMinutes: number;
  readonly providerId: string | null;
  readonly roomId: string | null;
  /** ISO-8601 instant, always UTC. */
  readonly startUtc: string;
  readonly endUtc: string;
  /** `yyyy-MM-dd HH:mm` in the property's zone — NOT a parseable instant. */
  readonly startLocal: string;
  readonly propertyTimeZone: string;
  readonly status: string;
  readonly reschedulable: boolean;
  readonly allowedTransitions: readonly string[];
  /** Numeric, and the value If-Match and fromRowVersion must quote. */
  readonly rowVersion: number;
  /** The same version, quoted, as the ETag header carries it. */
  readonly eTag: string;
  readonly confirmationNumber: string | null;
  readonly source?: string;
  readonly priceMinor?: number;
  readonly currencyCode?: string;
  readonly visitId?: string | null;
  /** Set while Held: an online slot hold is released at this instant. */
  readonly holdExpiresUtc?: string | null;
  readonly checkedInUtc?: string | null;
  /** CON-006: set on a committed reassign — the same token undoes it until then. */
  readonly undoUntilUtc?: string | null;
}

/** Every collection response is a page; an unbounded list is a latent outage. */
export interface PageDto<T> {
  readonly items: readonly T[];
  readonly total: number;
  readonly offset: number;
  readonly limit: number;
}

export interface CatalogServiceDto {
  readonly serviceId: string;
  readonly name: string;
  readonly durationMinutes: number;
}

/**
 * One breach of one rule.
 *
 * `rule` is load-bearing. Room turnover and provider transition are BOTH
 * CON-004 and differ only here, so grouping or de-duplicating by `code` drops
 * one of two real breaches along with its resolutions. Use `code + rule`
 * wherever an identity is needed.
 */
export interface ConflictDto {
  readonly code: string;
  readonly rule: string;
  readonly severity: 'soft' | 'hard';
  readonly summary: string;
  readonly operationalImpact: string;
  readonly financialImpact: string;
  readonly overridable: boolean;
  readonly resolutions: readonly string[];
}

export interface PreflightResponseDto {
  readonly token: string;
  readonly expiresUtc: string;
  readonly ttlSeconds: number;
  readonly appointmentId: string;
  readonly fromRowVersion: number;
  readonly proposedStartUtc: string;
  readonly proposedEndUtc: string;
  readonly commitAllowed: boolean;
  readonly requiresReason: boolean;
  readonly conflicts: readonly ConflictDto[];
}

export interface AvailabilitySlotDto {
  readonly startUtc: string;
  readonly startLocal: string;
  readonly open: boolean;
  /** Which resource is busy, not whether the property is idle. */
  readonly busyRooms: readonly string[];
  readonly busyProviders: readonly string[];
}

export interface AvailabilityDto {
  readonly date: string;
  /** Null when no serviceId was asked for; the grid then assumes 60 minutes. */
  readonly serviceId: string | null;
  readonly durationMinutes: number;
  /** IANA id. The only trustworthy source for the board's business day. */
  readonly timeZone: string;
  readonly slots: readonly AvailabilitySlotDto[];
}

export interface AuditEntryDto {
  readonly atUtc: string;
  readonly actor: string;
  readonly action: string;
  readonly purpose: string;
  readonly subjectType: string;
  readonly subjectId: string;
  readonly subjectVersion: number;
  readonly beforeHash: string | null;
  readonly afterHash: string | null;
  readonly conflictCodes: readonly string[];
  readonly selectedResolution: string | null;
  readonly targetStatus: string | null;
  readonly reason: string | null;
  readonly correlationId: string;
}

/** /audit answers `{ items, count }` rather than the paged envelope. */
export interface AuditPageDto {
  readonly items: readonly AuditEntryDto[];
  readonly count: number;
}

export interface HealthDto {
  readonly status: string;
  readonly utc: string;
  readonly release?: string;
  readonly persistence?: string;
}

/* ------------------------------------------------------------------ */
/* Request bodies                                                      */
/* ------------------------------------------------------------------ */

export interface CreateAppointmentRequest {
  readonly guestId: string;
  readonly guestAlias: string;
  readonly serviceId: string;
  readonly startUtc: string;
  readonly providerId?: string | null;
  readonly roomId?: string | null;
  readonly reason?: string | null;
}

/** fromRowVersion is required. See preflight-request rationale on the server. */
export interface PreflightRequest {
  readonly appointmentId: string;
  readonly startUtc: string;
  readonly providerId?: string | null;
  readonly roomId?: string | null;
  readonly fromRowVersion: number;
}

export interface ReassignRequest {
  readonly token: string;
  readonly reason?: string | null;
}

export interface TransitionRequest {
  readonly to: string;
  readonly reason?: string | null;
  /** Recorded on a cancellation: GuestRequest, HoldExpired, VisitCancelled… */
  readonly reasonCode?: string | null;
}

export interface AppointmentQuery {
  readonly date?: string;
  readonly from?: string;
  readonly to?: string;
  readonly offset?: number;
  readonly limit?: number;
}
