import type { AppointmentDto, ConflictDto } from './api';

/**
 * The normalised form of every API failure.
 *
 * The problem+json interceptor turns anything that goes wrong — a 422, a
 * dropped connection, an HTML error page from a proxy — into one of these, so
 * no call site ever imports HttpErrorResponse or tests `err.status`. The whole
 * point is that the handling code below is total: every branch a caller must
 * behave differently for is a named member of `ApiProblemCode`.
 */

export const API_PROBLEM_CODES = [
  'VALIDATION_FAILED',
  'AUTHENTICATION_REQUIRED',
  'AUTHORIZATION_DENIED',
  'NOT_FOUND',
  'STALE_VERSION',
  'HARD_CONFLICT',
  'SOFT_CONFLICT_APPROVAL_REQUIRED',
  'PREFLIGHT_EXPIRED',
  'IDEMPOTENCY_MISMATCH',
  'INTERNAL_ERROR',
  /** Not from the catalogue: the request never reached a server. */
  'NETWORK_UNREACHABLE',
  /** Not from the catalogue: something answered, but not problem+json. */
  'UNEXPECTED_RESPONSE',
] as const;

export type ApiProblemCode = (typeof API_PROBLEM_CODES)[number];

export interface FieldViolation {
  readonly field: string;
  readonly rule: string;
}

export interface ApiProblem {
  readonly type: string;
  readonly title: string;
  readonly status: number;
  /** A code outside the catalogue is kept verbatim rather than coerced. */
  readonly code: ApiProblemCode | string;
  readonly detail: string | null;
  /** Quote this to support. Always present, even when the server never answered. */
  readonly correlationId: string;
  readonly retryable: boolean;
  readonly retryAfterSeconds: number | null;

  /* ---- per-code extensions. Present only for the codes that define them --- */

  /** VALIDATION_FAILED: highlight these fields and keep what was typed. */
  readonly fieldViolations: readonly FieldViolation[] | null;
  /** STALE_VERSION: the record as the server now holds it. */
  readonly current: AppointmentDto | null;
  /** HARD_CONFLICT / SOFT_CONFLICT_APPROVAL_REQUIRED: the board now. */
  readonly conflicts: readonly ConflictDto[] | null;
  /**
   * A board-changed refusal carries this as well: what the operator was shown
   * when they decided. Its presence is what distinguishes "this was always
   * impossible" from "someone took the room while you were deciding".
   */
  readonly conflictsWhenShown: readonly ConflictDto[] | null;
  /**
   * SOFT_CONFLICT_APPROVAL_REQUIRED: the token SURVIVES this refusal. Retry
   * the same token with a reason; minting a new one throws away the operator's
   * decision and the remaining TTL.
   */
  readonly token: string | null;
  readonly expiresUtc: string | null;
}

/** A discriminated view for the cases the UI must branch on. */
export const isCode = (p: ApiProblem, code: ApiProblemCode): boolean => p.code === code;

/**
 * True when the operator was shown a conflict set the server has since
 * re-evaluated. The message has to say what changed, not just "try again".
 */
export const isBoardChanged = (p: ApiProblem): boolean =>
  p.code === 'HARD_CONFLICT' && p.conflictsWhenShown !== null;

/**
 * Conflicts present in one set and not the other, compared on code AND rule.
 * Comparing on code alone reports no change when a room-turnover breach was
 * replaced by a provider-transition one — both are CON-004.
 */
export const conflictDelta = (
  shown: readonly ConflictDto[],
  now: readonly ConflictDto[],
): { readonly added: readonly ConflictDto[]; readonly cleared: readonly ConflictDto[] } => {
  const key = (c: ConflictDto) => `${c.code}|${c.rule}`;
  const shownKeys = new Set(shown.map(key));
  const nowKeys = new Set(now.map(key));
  return {
    added: now.filter((c) => !shownKeys.has(key(c))),
    cleared: shown.filter((c) => !nowKeys.has(key(c))),
  };
};
