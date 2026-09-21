/** Shared view models for the workspace. All optimistic-concurrency carrying
 *  records expose `version`, which the API requires back on every edit. */

export type SlotState = 'booked' | 'in-progress' | 'complete' | 'conflict' | 'turnover' | 'blocked';
export type Severity = 'soft' | 'hard';

export interface Appointment {
  readonly id: string;
  readonly guestAlias: string;
  readonly service: string;
  readonly provider: string;
  readonly room: string;
  readonly start: string;
  readonly durationMin: number;
  readonly state: SlotState;
  readonly version: string;
}

/**
 * One drawn block on a lane.
 *
 * The identity fields below are what a Move proposal is built from. They are
 * optional only because a turnover or maintenance block is not an appointment;
 * a slot without an `appointmentId` cannot be moved, and the board refuses
 * rather than proposing some other record's id — which is exactly the defect
 * that made every preview quote the wrong version.
 */
export interface LaneSlot {
  readonly label: string;
  readonly startPct: number;
  readonly widthPct: number;
  readonly state: SlotState;
  readonly appointmentId?: string;
  /** ISO-8601 instant. The board's percentage is derived from this, not vice versa. */
  readonly startUtc?: string;
  readonly providerId?: string | null;
  readonly roomId?: string | null;
  /** Numeric row version, as preflight's fromRowVersion and If-Match require. */
  readonly rowVersion?: number;
  readonly durationMinutes?: number;
}

export interface Lane {
  readonly name: string;
  readonly role: string;
  readonly slots: readonly LaneSlot[];
}

export interface Conflict {
  readonly code: string;
  readonly severity: Severity;
  readonly summary: string;
  readonly consequence: string;
  readonly alternatives: readonly string[];
}

export interface ArrivalRow {
  readonly id: string;
  readonly guestAlias: string;
  readonly time: string;
  readonly service: string;
  readonly formsComplete: boolean;
  readonly depositSettled: boolean;
  readonly roomReady: boolean;
  readonly locker: string | null;
  readonly pager: string | null;
}

export interface StockLine {
  readonly item: string;
  readonly onHand: number;
  readonly clean: number;
  readonly soiled: number;
  readonly inWash: number;
  readonly reserved: number;
  readonly forecast: number;
  readonly confidence: 'high' | 'medium' | 'low';
}

export interface DeviceRow {
  readonly id: string;
  readonly kind: 'Pager' | 'Locker' | 'Reader';
  readonly battery: number;
  readonly online: boolean;
  readonly assignedToken: string | null;
  readonly state: 'available' | 'assigned' | 'cleaning' | 'out-of-service';
}

export interface StaffRow {
  readonly id: string;
  readonly name: string;
  readonly role: string;
  readonly credential: string;
  readonly expires: string;
  readonly assignable: boolean;
  /** Deliberately vague — the reason must not leak restricted detail. */
  readonly blockHint: string | null;
}

export interface LedgerRow {
  readonly id: string;
  readonly connector: string;
  readonly reference: string;
  readonly amount: number;
  readonly state: 'matched' | 'unmatched' | 'ambiguous' | 'resolved';
  readonly captured: string;
}

export interface MessageRule {
  readonly id: string;
  readonly name: string;
  readonly trigger: string;
  readonly offset: string;
  readonly channel: 'Email' | 'SMS' | 'Push';
  readonly active: boolean;
  readonly lastSent: string;
}

export interface OwnerRow {
  readonly capability: string;
  readonly owner: string;
  readonly effective: string;
  readonly state: 'active' | 'pending' | 'awaiting-approval' | 'rolled-back';
  readonly dependencies: number;
}
