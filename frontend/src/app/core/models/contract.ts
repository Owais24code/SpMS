/**
 * Contract constants taken verbatim from the AARFID Spa handoff package
 * v2.13.26. These are NOT ours to invent — they appear in the API, the audit
 * trail and the acceptance evidence, so the strings must match exactly.
 *
 * Sources:
 *   technical/config/role_permissions.json   — roles, scopes, restricted fields
 *   technical/docs/API_Error_Catalog.md      — problem+json codes
 *   SPECIFICATION_v2.12.md §7.1, §53.5       — state machines
 *   SPECIFICATION_v2.12.md §2953-2959        — CON-001..007 conflict register
 */

/* ------------------------------------------------------------------ */
/* OAuth scopes (role_permissions.json → scopeMap)                      */
/* ------------------------------------------------------------------ */
export const SCOPES = {
  read:             'spa.read',
  write:            'spa.write',
  schedule:         'spa.schedule',
  guestWrite:       'spa.guest.write',
  healthRestricted: 'spa.health.restricted',
  workforceRead:    'spa.workforce.read',
  commerce:         'spa.commerce',
  messaging:        'spa.messaging',
  device:           'spa.device',
  inventory:        'spa.inventory',
  reconcile:        'spa.reconcile',
  admin:            'spa.admin',
} as const;

export type Scope = (typeof SCOPES)[keyof typeof SCOPES];

/** The 18 roles defined in role_permissions.json. defaultEffect is deny. */
export type RoleCode =
  | 'guest' | 'provider' | 'front_desk' | 'spa_manager' | 'hr_compliance'
  | 'finance' | 'platform_admin' | 'configuration_approver' | 'scheduler'
  | 'housekeeping' | 'inventory_manager' | 'marketing' | 'support'
  | 'operations_analyst' | 'executive' | 'release_manager' | 'security_admin'
  | 'integration_service';

/**
 * Fields the API refuses to serialize without scope AND relationship AND
 * purpose. Listed here so the UI never assumes a hidden column is protected —
 * SEC-020: hiding in the interface is not authorization.
 */
export const RESTRICTED_FIELDS = [
  'intake.responses',
  'treatment_note.content',
  'credential.number',
  'staff_document.object_reference',
  'background_screening.adjudication',
  'commerce.payment_token',
] as const;

/* ------------------------------------------------------------------ */
/* problem+json error codes (API_Error_Catalog.md)                      */
/* ------------------------------------------------------------------ */
export const API_ERROR = {
  validationFailed:        { code: 'VALIDATION_FAILED',              status: 422 },
  authenticationRequired:  { code: 'AUTHENTICATION_REQUIRED',        status: 401 },
  authorizationDenied:     { code: 'AUTHORIZATION_DENIED',           status: 403 },
  notFound:                { code: 'NOT_FOUND',                      status: 404 },
  idempotencyMismatch:     { code: 'IDEMPOTENCY_MISMATCH',           status: 409 },
  staleVersion:            { code: 'STALE_VERSION',                  status: 412 },
  hardConflict:            { code: 'HARD_CONFLICT',                  status: 409 },
  softConflictApproval:    { code: 'SOFT_CONFLICT_APPROVAL_REQUIRED',status: 409 },
  preflightExpired:        { code: 'PREFLIGHT_EXPIRED',              status: 409 },
  ownershipAmbiguous:      { code: 'OWNERSHIP_AMBIGUOUS',            status: 503 },
  dependencyTimeout:       { code: 'DEPENDENCY_TIMEOUT',             status: 503 },
  /** 202, not an error. BR-014: never blind-retry — query the original. */
  paymentOutcomeAmbiguous: { code: 'PAYMENT_OUTCOME_AMBIGUOUS',      status: 202 },
  rateLimited:             { code: 'RATE_LIMITED',                   status: 429 },
  offlineActionBlocked:    { code: 'OFFLINE_ACTION_BLOCKED',         status: 503 },
} as const;

/* ------------------------------------------------------------------ */
/* Scheduling conflict register (CON-001 … CON-007)                     */
/* ------------------------------------------------------------------ */
export type ConflictSeverity = 'soft' | 'hard';

export interface ConflictDefinition {
  readonly code: string;
  readonly severity: ConflictSeverity;
  readonly summary: string;
  /** CON-001 requires operational AND financial impact to be stated. */
  readonly operationalImpact: string;
  readonly financialImpact: string;
  readonly overridable: boolean;
}

export const CONFLICTS: Record<string, ConflictDefinition> = {
  'CON-001': {
    code: 'CON-001',
    severity: 'soft',
    summary: 'Provider overlap or unavailable shift',
    operationalImpact: 'Two appointments sit with one therapist; the later guest waits.',
    financialImpact: 'Likely service recovery on a $240.00 booking.',
    overridable: true,
  },
  'CON-002': {
    code: 'CON-002',
    severity: 'hard',
    summary: 'Room or equipment overlap, or incompatible capability',
    operationalImpact: 'A room cannot hold two treatments. Physically impossible.',
    financialImpact: 'Commit would be reversed; both bookings at risk.',
    overridable: false,
  },
  'CON-003': {
    code: 'CON-003',
    severity: 'hard',
    summary: 'Provider lacks an active licence or qualification',
    operationalImpact: 'Delivering the service would be unlicensed.',
    financialImpact: 'Regulatory exposure; insurance may not respond.',
    overridable: false,
  },
  'CON-004': {
    code: 'CON-004',
    severity: 'soft',
    summary: 'Duration, buffer or turnover crosses blocked time',
    operationalImpact: 'Turnover is compressed; the next guest may wait.',
    financialImpact: 'Minor — possible discount on the following booking.',
    overridable: true,
  },
  'CON-005': {
    code: 'CON-005',
    severity: 'soft',
    summary: 'Guest has an overlapping appointment or insufficient transition time',
    operationalImpact: 'The guest cannot physically make both.',
    financialImpact: 'One booking becomes a no-show unless acknowledged.',
    overridable: true,
  },
  'CON-006': {
    code: 'CON-006',
    severity: 'soft',
    summary: 'Stale price or service version',
    operationalImpact: 'The quoted price no longer matches the catalogue.',
    financialImpact: 'Revenue variance on commit.',
    overridable: true,
  },
  'CON-007': {
    code: 'CON-007',
    severity: 'hard',
    summary: 'External dependency unavailable',
    operationalImpact: 'The owning system cannot confirm; state would diverge.',
    financialImpact: 'Unknown until the dependency responds.',
    overridable: false,
  },
};

/* ------------------------------------------------------------------ */
/* State machines (§7.1, §53.5)                                         */
/* ------------------------------------------------------------------ */
export const APPOINTMENT_STATUS = [
  'Draft', 'Held', 'Confirmed', 'Checked In', 'Ready',
  'In Service', 'Completed', 'Cancelled', 'No-show',
] as const;
export type AppointmentStatus = (typeof APPOINTMENT_STATUS)[number];

export const COMMERCE_STATUS = [
  'Initiated', 'Pending', 'Authorized', 'Captured', 'Settled',
  'Declined', 'Cancelled', 'Refunded', 'Disputed', 'Ambiguous',
] as const;

export const QUIET_DEVICE_STATUS = [
  'Available', 'Assigned', 'Alerting', 'Acknowledged', 'Returned',
  'Escalated', 'Lost', 'Out of Service',
] as const;

export const RESOURCE_STATUS = [
  'Proposed', 'Reserved', 'In Use', 'Turnover', 'Ready', 'Blocked', 'Out of Service',
] as const;

/**
 * Effective operating mode published by the server (OFF-001..006).
 * The client must enforce whatever the server says, not guess from
 * navigator.onLine.
 */
export const OPERATING_MODE = [
  'Online', 'Degraded Read', 'Offline Operational', 'Recovery Sync', 'Reconciliation Required',
] as const;
export type OperatingMode = (typeof OPERATING_MODE)[number];
