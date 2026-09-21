import { Injectable, signal, computed } from '@angular/core';
import type {
  Appointment, ArrivalRow, DeviceRow, LedgerRow,
  MessageRule, OwnerRow, StaffRow, StockLine, Lane,
} from '../models/spa.model';
import {
  APPOINTMENTS, ARRIVALS, DEVICES, LEDGER, MESSAGE_RULES,
  OWNERS, STAFF, STOCK, LANES, WEEK_BOOKINGS, WEEK_LABELS,
} from '../data/workspace-data';
import { API_ERROR } from '../models/contract';

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
  readonly preflight = signal<'idle' | 'running' | 'pass' | 'fail'>('idle');

  async runPreflight(): Promise<'pass' | 'fail'> {
    this.preflight.set('running');
    await this.settle(1100);
    const outcome = this.owners().some((o) => o.state === 'awaiting-approval') ? 'fail' : 'pass';
    this.preflight.set(outcome);
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
    this.preflight.set('idle');
    this.record('Demo data reset', 'All screens returned to their starting state');
  }
}

const bump = (v: string) => 'v' + (Number(v.replace('v', '')) + 1);
