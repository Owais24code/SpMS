import type { ConflictDefinition } from './contract';
import type { AppointmentDto, ConflictDto } from './api';

/**
 * A proposed change to the schedule, before anything is committed.
 *
 * SCH-020 / GUI-003: a keyboard Move produces a *proposal* that the server
 * validates. The appointment is unchanged until the operator confirms against
 * the returned token. This is the difference between "the board moved and
 * we'll sort out the consequences" and "here is what would happen".
 *
 * The fields are the server's, not the board's. It previously carried
 * `laneName`, `startPct` and a string `fromVersion`, none of which the API
 * accepts: percentages are a property of how the board is drawn, and a lane
 * name is not a resource id. The percentage arithmetic now lives in
 * board-time.ts and stops at the edge of this interface.
 */
export interface MoveProposal {
  readonly appointmentId: string;
  /** ISO-8601 instant, UTC, snapped to whole minutes. */
  readonly startUtc: string;
  /** Null leaves the current assignment alone; the server treats it as absent. */
  readonly providerId: string | null;
  readonly roomId: string | null;
  /**
   * Numeric, and REQUIRED. The server refuses to default it: defaulting made
   * the optimistic check assert the server's own value, so a concurrent edit
   * between read and commit could not be detected.
   */
  readonly fromRowVersion: number;
}

/**
 * A conflict as the drawer renders it.
 *
 * Extends the static register entry with the two things only the server can
 * supply: the `rule` discriminator and the ranked resolutions. `rule` is not
 * decoration — room turnover and provider transition are BOTH CON-004 and
 * differ only here, so anything keyed on `code` alone collapses two separate
 * breaches into one and drops a set of resolutions with it.
 */
export interface ConflictView extends ConflictDefinition {
  readonly rule: string;
  readonly resolutions: readonly string[];
}

/** Identity of a conflict for tracking and comparison. Never the code alone. */
export const conflictKey = (c: ConflictView | ConflictDto): string => `${c.code}|${c.rule}`;

export const toConflictView = (c: ConflictDto): ConflictView => ({
  code: c.code,
  rule: c.rule,
  severity: c.severity,
  summary: c.summary,
  operationalImpact: c.operationalImpact,
  financialImpact: c.financialImpact,
  overridable: c.overridable,
  resolutions: c.resolutions,
});

export interface PreflightResult {
  /** Opaque token the commit must quote. Single use. */
  readonly token: string;
  readonly expiresAtMs: number;
  readonly proposal: MoveProposal;
  /** Human-readable proposed window, already in property-local time. */
  readonly proposedStart: string;
  readonly proposedEnd: string;
  /** The same window as instants, for anything that has to compute with it. */
  readonly proposedStartUtc: string;
  readonly proposedEndUtc: string;
  /** Every conflict the complete post-change state produces (CON-005). */
  readonly conflicts: readonly ConflictView[];
  /** False when any conflict is non-overridable. */
  readonly commitAllowed: boolean;
  /** True when the commit will be refused until a reason is supplied. */
  readonly requiresReason: boolean;
}

/**
 * What a commit can come back as.
 *
 * Deliberately NOT WriteResult: three of these outcomes have no equivalent
 * there, and the operator has to be told a different thing for each. Folding
 * them into `denied` is what produced the "that did not commit" message that
 * told nobody anything.
 */
export type CommitMoveResult =
  /** Null only on the in-memory path, where nothing served the move. */
  | { readonly kind: 'committed'; readonly appointment: AppointmentDto | null }
  /** The token is STILL VALID. Prompt for a reason and retry the same token. */
  | {
      readonly kind: 'reason-required';
      readonly token: string;
      readonly expiresAtMs: number;
      readonly conflicts: readonly ConflictView[];
    }
  /** No override path exists for any role. Offer the alternatives instead. */
  | { readonly kind: 'hard-conflict'; readonly conflicts: readonly ConflictView[] }
  /**
   * Someone else changed the board inside the token's window. Both sets are
   * carried so the operator can be told what changed, not just "try again".
   */
  | {
      readonly kind: 'board-changed';
      readonly conflicts: readonly ConflictView[];
      readonly conflictsWhenShown: readonly ConflictView[];
    }
  /** The token is gone. Re-run preflight against the current board. */
  | { readonly kind: 'expired' }
  /** The appointment moved on. `current` is it, as the server now holds it. */
  | { readonly kind: 'stale'; readonly current: AppointmentDto | null }
  | { readonly kind: 'denied'; readonly code: string; readonly detail: string | null };

/** How long a preflight token stays valid. The server's value wins when it sends one. */
export const PREFLIGHT_TTL_MS = 90_000;

/** CON-006 undo window. Configurable per property in production. */
export const UNDO_WINDOW_MS = 20_000;

export const isExpired = (r: PreflightResult, now = Date.now()): boolean =>
  now > r.expiresAtMs;
