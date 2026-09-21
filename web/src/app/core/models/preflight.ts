import type { ConflictDefinition } from './contract';

/**
 * A proposed change to the schedule, before anything is committed.
 *
 * SCH-020 / GUI-003: a drag or a keyboard Move produces a *proposal* that the
 * server validates. The appointment is unchanged until the operator confirms
 * against the returned token. This is the difference between "the board moved
 * and we'll sort out the consequences" and "here is what would happen".
 */
export interface MoveProposal {
  readonly appointmentId: string;
  readonly laneName: string;
  readonly startPct: number;
  readonly widthPct: number;
  /** Version the proposal was built from; commit fails if it has moved on. */
  readonly fromVersion: string;
}

export interface PreflightResult {
  /** Opaque token the commit must quote. Single use. */
  readonly token: string;
  readonly expiresAtMs: number;
  readonly proposal: MoveProposal;
  /** Human-readable proposed window, already in property-local time. */
  readonly proposedStart: string;
  readonly proposedEnd: string;
  /** Every conflict the complete post-change state produces (CON-005). */
  readonly conflicts: readonly ConflictDefinition[];
  /** False when any conflict is non-overridable. */
  readonly commitAllowed: boolean;
}

/** How long a preflight token stays valid. Server-owned in production. */
export const PREFLIGHT_TTL_MS = 90_000;

/** CON-006 undo window. Configurable per property in production. */
export const UNDO_WINDOW_MS = 20_000;

export const isExpired = (r: PreflightResult, now = Date.now()): boolean =>
  now > r.expiresAtMs;
