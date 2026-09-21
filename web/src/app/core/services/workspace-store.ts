import { Injectable, signal, computed } from '@angular/core';
import type {
  Appointment, ArrivalRow, DeviceRow, LedgerRow,
  MessageRule, OwnerRow, StaffRow, StockLine, Lane,
} from '../models/spa.model';
import {
  APPOINTMENTS, ARRIVALS, DEVICES, LEDGER, MESSAGE_RULES,
  OWNERS, STAFF, STOCK, LANES, WEEK_BOOKINGS, WEEK_LABELS,
} from '../data/workspace-data';
import { API_ERROR, CONFLICTS, type ConflictDefinition } from '../models/contract';
import {
  PREFLIGHT_TTL_MS, UNDO_WINDOW_MS, isExpired,
  type MoveProposal, type PreflightResult,
} from '../models/preflight';

export interface AuditEntry {
  readonly at: string;
  readonly actor: string;
  readonly action: string;
  readonly detail: string;
  readonly code?: string;
}

/** What a write can come back as. Mirrors the API's OptimisticResult. */
export type WriteResult<T> =
  | { kind: 'committed'; value: T }
  | { kind: 'stale'; current: T; code: typeof API_ERROR.staleVersion.code }
  | { kind: 'ambiguous'; code: typeof API_ERROR.paymentOutcomeAmbiguous.code }
  | { kind: 'denied'; code: typeof API_ERROR.authorizationDenied.code };

const now = () =>
  new Intl.DateTimeFormat('en-GB', { hour: '2-digit', minute: '2-digit' }).format(new Date());

/**
 * In-memory stand-in for the SpMS API.
 *
 * Every mutation goes through here rather than a component touching an array,
 * so when the real API lands only this file changes. Latency and the occasional
 * stale/ambiguous outcome are simulated deliberately: the non-happy paths are
 * the ones that are expensive to retrofit, so they are exercised from day one.
 */
@Injectable({ providedIn: 'root' })
export class WorkspaceStore {
  // ---- collections ------------------------------------------------------
  readonly appointments = signal<Appointment[]>([...APPOINTMENTS]);
  readonly arrivals     = signal<ArrivalRow[]>([...ARRIVALS]);
  readonly devices      = signal<DeviceRow[]>([...DEVICES]);
  readonly stock        = signal<StockLine[]>([...STOCK]);
  readonly ledger       = signal<LedgerRow[]>([...LEDGER]);
  readonly rules        = signal<MessageRule[]>([...MESSAGE_RULES]);
  readonly owners       = signal<OwnerRow[]>([...OWNERS]);
  readonly staff        = signal<StaffRow[]>([...STAFF]);
  readonly lanes        = signal<Lane[]>(LANES.map((l) => ({ ...l, slots: [...l.slots] })));

  readonly checkedIn = signal<readonly string[]>([]);
  readonly audit     = signal<readonly AuditEntry[]>([]);

  // ---- chart ranges -----------------------------------------------------
  readonly range = signal<'7' | '30'>('7');
  readonly weekLabels = WEEK_LABELS;
  readonly series = computed(() =>
    this.range() === '7'
      ? WEEK_BOOKINGS
      : [318, 402, 377, 441, 486, 512, 468],
  );
  readonly rangeLabels = computed(() =>
    this.range() === '7' ? WEEK_LABELS : ['W1', 'W2', 'W3', 'W4', 'W5', 'W6', 'W7'],
  );

  // ---- derived ----------------------------------------------------------
  readonly openConflicts = computed(
    () => this.appointments().filter((a) => a.state === 'conflict').length,
  );
  readonly pendingArrivals = computed(
    () => this.arrivals().filter((a) => !this.checkedIn().includes(a.id)).length,
  );

  // ---- helpers ----------------------------------------------------------
  private async settle(ms = 420): Promise<void> {
    await new Promise((r) => setTimeout(r, ms));
  }

  private record(action: string, detail: string, code?: string): void {
    this.audit.update((list) => [
      { at: now(), actor: 'You', action, detail, code },
      ...list,
    ].slice(0, 40));
  }

  // ---- arrivals ---------------------------------------------------------
  async checkIn(id: string): Promise<WriteResult<string>> {
    await this.settle();
    const row = this.arrivals().find((a) => a.id === id);
    if (!row) return { kind: 'denied', code: API_ERROR.authorizationDenied.code };
    if (!(row.formsComplete && row.depositSettled && row.roomReady)) {
      return { kind: 'denied', code: API_ERROR.authorizationDenied.code };
    }
    this.checkedIn.update((l) => [...l, id]);
    this.record('Check-in', `${row.guestAlias} checked in`);
    return { kind: 'committed', value: id };
  }

  undoCheckIn(id: string): void {
    this.checkedIn.update((l) => l.filter((x) => x !== id));
    this.record('Check-in reversed', id);
  }

  assignLocker(id: string): void {
    const n = 100 + Math.floor(Math.random() * 300);
    this.arrivals.update((rows) =>
      rows.map((r) => (r.id === id ? { ...r, locker: `L-${n}` } : r)));
    this.record('Locker assigned', `${id} → L-${n}`);
  }

  settleDeposit(id: string): void {
    this.arrivals.update((rows) =>
      rows.map((r) => (r.id === id ? { ...r, depositSettled: true } : r)));
    this.record('Deposit settled', id);
  }

  completeForms(id: string): void {
    this.arrivals.update((rows) =>
      rows.map((r) => (r.id === id ? { ...r, formsComplete: true } : r)));
    this.record('Forms completed', id);
  }

  markRoomReady(id: string): void {
    this.arrivals.update((rows) =>
      rows.map((r) => (r.id === id ? { ...r, roomReady: true } : r)));
    this.record('Room ready', id);
  }

  addWalkIn(): ArrivalRow {
    const n = 4900 + this.arrivals().length;
    const row: ArrivalRow = {
      id: `r-${n}`, guestAlias: `Guest ${n}`, time: now(), service: 'Express 30',
      formsComplete: false, depositSettled: false, roomReady: true, locker: null, pager: null,
    };
    this.arrivals.update((rows) => [row, ...rows]);
    this.record('Walk-in created', row.guestAlias);
    return row;
  }

  // ---- preflight and commit (SCH-020) -----------------------------------

  /** Tokens we have issued and not yet spent. Server-side in production. */
  private readonly issued = new Map<string, PreflightResult>();

  /** The proposal currently on screen, if any. Nothing is committed yet. */
  readonly pendingProposal = signal<PreflightResult | null>(null);

  /**
   * Validates a proposed move against the complete post-change state and
   * returns a token. Nothing is written — the appointment is untouched until
   * commitMove quotes this token.
   */
  async preflight(proposal: MoveProposal): Promise<PreflightResult> {
    await this.settle(320);

    const lane = this.lanes().find((l) => l.name === proposal.laneName);
    const conflicts: ConflictDefinition[] = [];

    // A room already occupied in the target window is physically impossible.
    const overlaps = lane?.slots.some((s) =>
      s.state !== 'turnover' &&
      proposal.startPct < s.startPct + s.widthPct &&
      s.startPct < proposal.startPct + proposal.widthPct);

    if (overlaps) {
      conflicts.push(lane?.role === 'Room' ? CONFLICTS['CON-002'] : CONFLICTS['CON-001']);
    }
    if (lane?.slots.some((s) => s.state === 'blocked')) {
      conflicts.push(CONFLICTS['CON-004']);
    }

    const appt = this.appointments().find((a) => a.id === proposal.appointmentId);
    const startMin = Math.round(9 * 60 + (proposal.startPct / 100) * 8 * 60);
    const endMin = startMin + (appt?.durationMin ?? 60);

    const result: PreflightResult = {
      token: 'pf_' + Math.random().toString(36).slice(2, 10),
      expiresAtMs: Date.now() + PREFLIGHT_TTL_MS,
      proposal,
      proposedStart: fmtMin(startMin),
      proposedEnd: fmtMin(endMin),
      conflicts,
      commitAllowed: conflicts.every((c) => c.overridable),
    };

    this.issued.set(result.token, result);
    this.pendingProposal.set(result);
    return result;
  }

  discardProposal(): void {
    const p = this.pendingProposal();
    if (p) this.issued.delete(p.token);
    this.pendingProposal.set(null);
  }

  /**
   * Commits a previously preflighted move.
   *
   * Refuses an unknown or expired token rather than silently re-validating —
   * a stale token means the board has moved under the operator and they need
   * to see the new state before committing to it.
   */
  async commitMove(token: string, reason?: string): Promise<WriteResult<string>> {
    await this.settle();

    const pf = this.issued.get(token);
    if (!pf || isExpired(pf)) {
      this.issued.delete(token);
      this.pendingProposal.set(null);
      return { kind: 'denied', code: API_ERROR.authorizationDenied.code };
    }
    if (!pf.commitAllowed) {
      return { kind: 'denied', code: API_ERROR.authorizationDenied.code };
    }

    const before = this.lanes().map((l) => ({ ...l, slots: [...l.slots] }));
    this.undoable.set({ lanes: before, at: Date.now(), what: 'Move committed' });

    this.lanes.update((ls) => ls.map((l) => l.name !== pf.proposal.laneName ? l : {
      ...l,
      slots: [...l.slots, {
        label: 'Moved', startPct: pf.proposal.startPct,
        widthPct: pf.proposal.widthPct, state: 'booked' as const,
      }],
    }));

    this.issued.delete(token);
    this.pendingProposal.set(null);
    this.record('Move committed', `${pf.proposedStart}–${pf.proposedEnd}${reason ? ' — ' + reason : ''}`,
      pf.conflicts[0]?.code);

    return { kind: 'committed', value: token };
  }

  // ---- undo (CON-006) ---------------------------------------------------

  private readonly undoable = signal<{ lanes: Lane[]; at: number; what: string } | null>(null);

  readonly canUndo = computed(() => {
    const u = this.undoable();
    return !!u && Date.now() - u.at < UNDO_WINDOW_MS;
  });

  /**
   * Reverts inside the window. Past it the spec requires an explicit
   * compensating reschedule instead, because downstream systems may already
   * have acted on the change.
   */
  undoLastMove(): 'undone' | 'window-closed' {
    const u = this.undoable();
    if (!u) return 'window-closed';
    if (Date.now() - u.at >= UNDO_WINDOW_MS) {
      this.undoable.set(null);
      return 'window-closed';
    }
    this.lanes.set(u.lanes);
    this.undoable.set(null);
    this.record('Move undone', u.what);
    return 'undone';
  }

  // ---- appointments -----------------------------------------------------
  async resolveConflict(id: string, reason: string): Promise<WriteResult<Appointment>> {
    await this.settle();
    const appt = this.appointments().find((a) => a.id === id);
    if (!appt) return { kind: 'denied', code: API_ERROR.authorizationDenied.code };

    this.appointments.update((list) =>
      list.map((a) => (a.id === id ? { ...a, state: 'booked', version: bump(a.version) } : a)));
    this.lanes.update((ls) =>
      ls.map((l) => ({ ...l, slots: l.slots.map((s) => s.state === 'conflict' ? { ...s, state: 'booked', label: 'Rebooked' } : s) })));
    this.record('Conflict overridden', `${appt.guestAlias} — ${reason}`, 'CON-001');
    return { kind: 'committed', value: appt };
  }

  createAppointment(): Appointment {
    const n = 4900 + this.appointments().length;
    const a: Appointment = {
      id: `a-${n}`, guestAlias: `Guest ${n}`, service: 'Aromatherapy 60',
      provider: 'Priya Nair', room: 'Room 2', start: '5:30pm',
      durationMin: 60, state: 'booked', version: 'v1',
    };
    this.appointments.update((l) => [...l, a]);
    this.lanes.update((ls) => ls.map((l, i) => i === 2
      ? { ...l, slots: [...l.slots, { label: 'Aromatherapy 60', startPct: 78, widthPct: 18, state: 'booked' as const }] }
      : l));
    this.record('Appointment created', a.guestAlias);
    return a;
  }

  cancelAppointment(id: string): void {
    const a = this.appointments().find((x) => x.id === id);
    this.appointments.update((l) => l.filter((x) => x.id !== id));
    this.record('Appointment cancelled', a?.guestAlias ?? id);
  }

  // ---- devices ----------------------------------------------------------
  assignDevice(id: string): void {
    const token = 'tkn-' + Math.random().toString(16).slice(2, 6);
    this.devices.update((ds) =>
      ds.map((d) => (d.id === id ? { ...d, state: 'assigned', assignedToken: token } : d)));
    this.record('Device assigned', `${id} → ${token}`);
  }

  returnDevice(id: string): void {
    this.devices.update((ds) =>
      ds.map((d) => (d.id === id ? { ...d, state: 'cleaning', assignedToken: null } : d)));
    this.record('Device returned', id);
  }

  restoreDevice(id: string): void {
    this.devices.update((ds) =>
      ds.map((d) => (d.id === id ? { ...d, state: 'available' } : d)));
    this.record('Device back in service', id);
  }

  // ---- inventory --------------------------------------------------------
  async approveWash(item: string): Promise<WriteResult<string>> {
    await this.settle(600);
    this.stock.update((rows) =>
      rows.map((s) => s.item === item
        ? { ...s, soiled: Math.max(0, s.soiled - 120), inWash: s.inWash + 120 }
        : s));
    this.record('Wash approved', `${item} — 120 units`);
    return { kind: 'committed', value: item };
  }

  adjustStock(item: string, delta: number): void {
    this.stock.update((rows) =>
      rows.map((s) => s.item === item
        ? { ...s, clean: Math.max(0, s.clean + delta), onHand: Math.max(0, s.onHand + delta) }
        : s));
    this.record('Stock adjusted', `${item} ${delta >= 0 ? '+' : ''}${delta}`);
  }

  // ---- messaging --------------------------------------------------------
  toggleRule(id: string): boolean {
    let next = false;
    this.rules.update((rs) => rs.map((r) => {
      if (r.id !== id) return r;
      next = !r.active;
      return { ...r, active: next };
    }));
    this.record('Rule ' + (next ? 'enabled' : 'paused'), id);
    return next;
  }

  addRule(): MessageRule {
    const r: MessageRule = {
      id: `m-${this.rules().length + 1}`, name: 'New rule',
      trigger: 'Before appointment', offset: '24 hours',
      channel: 'Email', active: false, lastSent: '—',
    };
    this.rules.update((rs) => [...rs, r]);
    this.record('Rule created', r.name);
    return r;
  }

  // ---- reconciliation ---------------------------------------------------
  /** Queries the original by idempotency key. Never re-sends the payment. */
  async queryOriginal(id: string): Promise<WriteResult<LedgerRow>> {
    await this.settle(900);
    const row = this.ledger().find((t) => t.id === id);
    if (!row) return { kind: 'denied', code: API_ERROR.authorizationDenied.code };

    // Two thirds of the time the gateway now confirms; otherwise still unknown.
    if (Math.random() > 0.34) {
      this.ledger.update((ts) => ts.map((t) => t.id === id ? { ...t, state: 'matched' } : t));
      this.record('Ambiguous transaction resolved', `${id} confirmed captured`, API_ERROR.paymentOutcomeAmbiguous.code);
      return { kind: 'committed', value: row };
    }
    this.record('Ambiguous transaction still unknown', id, API_ERROR.paymentOutcomeAmbiguous.code);
    return { kind: 'ambiguous', code: API_ERROR.paymentOutcomeAmbiguous.code };
  }

  resolveLedger(id: string, disposition: string): void {
    this.ledger.update((ts) => ts.map((t) => t.id === id ? { ...t, state: 'resolved' } : t));
    this.record('Manual resolution', `${id} — ${disposition}`);
  }

  // ---- ownership --------------------------------------------------------
  /** Ownership-cutover preflight (UX-010), distinct from schedule preflight. */
  readonly ownershipPreflight = signal<'idle' | 'running' | 'pass' | 'fail'>('idle');

  async runPreflight(): Promise<'pass' | 'fail'> {
    this.ownershipPreflight.set('running');
    await this.settle(1100);
    const outcome = this.owners().some((o) => o.state === 'awaiting-approval') ? 'fail' : 'pass';
    this.ownershipPreflight.set(outcome);
    this.record('Preflight run', outcome === 'pass' ? 'All checks passed' : 'Outage runbook missing');
    return outcome;
  }

  proposeChange(capability: string): void {
    this.owners.update((os) => os.map((o) =>
      o.capability === capability ? { ...o, state: 'awaiting-approval' } : o));
    this.record('Ownership change proposed', capability);
  }

  // ---- staff ------------------------------------------------------------
  renewCredential(id: string): void {
    this.staff.update((ss) => ss.map((s) => s.id === id
      ? { ...s, assignable: true, blockHint: null, expires: '30 Sep 2027' }
      : s));
    this.record('Credential renewed', id);
  }

  reset(): void {
    this.appointments.set([...APPOINTMENTS]);
    this.arrivals.set([...ARRIVALS]);
    this.devices.set([...DEVICES]);
    this.stock.set([...STOCK]);
    this.ledger.set([...LEDGER]);
    this.rules.set([...MESSAGE_RULES]);
    this.owners.set([...OWNERS]);
    this.staff.set([...STAFF]);
    this.lanes.set(LANES.map((l) => ({ ...l, slots: [...l.slots] })));
    this.checkedIn.set([]);
    this.ownershipPreflight.set('idle');
    this.record('Demo data reset', 'All screens returned to their starting state');
  }
}

const bump = (v: string) => 'v' + (Number(v.replace('v', '')) + 1);

/** Minutes since midnight → property-local clock label. */
const fmtMin = (m: number): string => {
  const h24 = Math.floor(m / 60);
  const mm = String(m % 60).padStart(2, '0');
  const h12 = h24 % 12 === 0 ? 12 : h24 % 12;
  return `${h12}:${mm}${h24 < 12 ? 'am' : 'pm'}`;
};
