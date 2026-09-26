import { Injectable, computed, effect, inject, signal, untracked } from '@angular/core';
import type {
  Appointment, ArrivalRow, DeviceRow, LaneSlot, LedgerRow,
  MessageRule, OwnerRow, SlotState, StaffRow, StockLine, Lane,
} from '../models/spa.model';
import {
  APPOINTMENTS, ARRIVALS, DEVICES, HOURS, LEDGER, MESSAGE_RULES,
  OWNERS, STAFF, STOCK, LANES, WEEK_BOOKINGS, WEEK_LABELS,
} from '../data/workspace-data';
import { API_ERROR, CONFLICTS, type ConflictDefinition } from '../models/contract';
import {
  PREFLIGHT_TTL_MS, UNDO_WINDOW_MS, isExpired, toConflictView,
  type CommitMoveResult, type ConflictView, type MoveProposal, type PreflightResult,
} from '../models/preflight';
import type {
  AppointmentDto, AvailabilityDto, CatalogServiceDto, ConflictDto, PreflightResponseDto,
} from '../models/api';
import type { ApiProblem } from '../models/api-problem';
import { IdempotencyKeys, SchedulingApi } from '../api/scheduling-api';
import { OperationsApi } from '../api/operations-api';
import type { ArrivalDto } from '../models/operations';
import {
  browserToday, clockLabel, dayFromAvailability, fallbackDay, hourTicks,
  pctAt, pctForMinutes, spanning, type BusinessDay,
} from './board-time';
import { environment } from '../../../environments/environment';
import { AuthService } from './auth.service';
import { BoardStream } from './board-stream';

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
  | { kind: 'denied'; code: string };

const now = () =>
  new Intl.DateTimeFormat('en-GB', { hour: '2-digit', minute: '2-digit' }).format(new Date());

/**
 * The workspace's single source of truth.
 *
 * The schedule reads and writes through the real API; every other screen is
 * still the in-memory stand-in, behind `environment.useRealApi`. Both paths
 * live here because the components must not know which one they are on — the
 * public members below have the same shape either way.
 *
 * The flag is not a migration artefact to be tidied away: the API has no
 * database, so a demo with no server running is still how most of these
 * screens are shown.
 */
@Injectable({ providedIn: 'root' })
export class WorkspaceStore {
  private readonly api  = inject(SchedulingApi);
  private readonly ops  = inject(OperationsApi);
  private readonly keys = inject(IdempotencyKeys);

  /** Read once. A flag that could change mid-session would be untestable. */
  private readonly realApi = environment.useRealApi;

  // ---- collections ------------------------------------------------------
  /** The demo arrivals; against the API the list is `arrivalRows` from /front-desk/arrivals. */
  private readonly demoArrivals = signal<ArrivalRow[]>([...ARRIVALS]);
  /** The server's arrivals for today, exactly as they arrived. */
  readonly arrivalRows = signal<readonly ArrivalDto[]>([]);
  readonly arrivalsProblem = signal<ApiProblem | null>(null);

  readonly arrivals = computed<readonly ArrivalRow[]>(() => this.realApi
    ? this.arrivalRows().map(toArrivalRow)
    : this.demoArrivals());
  readonly devices      = signal<DeviceRow[]>([...DEVICES]);
  readonly stock        = signal<StockLine[]>([...STOCK]);
  readonly ledger       = signal<LedgerRow[]>([...LEDGER]);
  readonly rules        = signal<MessageRule[]>([...MESSAGE_RULES]);
  readonly owners       = signal<OwnerRow[]>([...OWNERS]);
  readonly staff        = signal<StaffRow[]>([...STAFF]);

  private readonly demoCheckedIn = signal<readonly string[]>([]);
  /** Ids already through the desk: the server's status, or the demo's list. */
  readonly checkedIn = computed<readonly string[]>(() => this.realApi
    ? this.arrivalRows().filter((a) => a.status !== 'Confirmed' && a.status !== 'Held').map((a) => a.appointmentId)
    : this.demoCheckedIn());
  readonly audit     = signal<readonly AuditEntry[]>([]);

  /* ---- the board -------------------------------------------------------
     The demo collections stay writable; the API ones are replaced wholesale
     from a response. `appointments` and `lanes` are computed over whichever
     is live, so the four screens that read them are unaware of the switch. */

  private readonly demoAppointments = signal<Appointment[]>([...APPOINTMENTS]);
  private readonly demoLanes = signal<Lane[]>(LANES.map((l) => ({ ...l, slots: [...l.slots] })));

  /** The server's appointments for `boardDate`, exactly as they arrived. */
  readonly boardRows = signal<readonly AppointmentDto[]>([]);
  readonly services = signal<readonly CatalogServiceDto[]>([]);

  /** `yyyy-MM-dd` in the property's zone. */
  readonly boardDate = signal<string>(browserToday());
  readonly boardLoading = signal(false);
  /** The last read failure, so the board can say why it is empty. */
  readonly boardProblem = signal<ApiProblem | null>(null);

  /**
   * The business day the board is drawn across.
   *
   * Starts as an assumed window in UTC and is replaced by the derived one as
   * soon as /availability answers. It is a signal rather than a constant
   * because it changes with the date AND with the property.
   */
  readonly businessDay = signal<BusinessDay>(fallbackDay(browserToday(), 'UTC'));

  readonly appointments = computed<readonly Appointment[]>(() => {
    if (!this.realApi) return this.demoAppointments();
    const day = this.businessDay();
    return this.boardRows().map((a) => toViewAppointment(day, a));
  });

  readonly lanes = computed<readonly Lane[]>(() =>
    this.realApi ? buildLanes(this.businessDay(), this.boardRows()) : this.demoLanes());

  /** The hour scale. Derived from the real business day, not a fixed array. */
  readonly hours = computed<readonly string[]>(() =>
    this.realApi ? hourTicks(this.businessDay()) : HOURS);

  constructor() {
    // Fired here rather than from the schedule component because the
    // dashboard, appointments and treatments screens read `appointments()`
    // too, and each of them would otherwise render an empty state until
    // somebody visited the board.
    //
    // Gated on a principal existing: every scoped endpoint answers 403 with no
    // scopes, and a read fired from the sign-in screen would leave a stale
    // authorization failure on the board for the session that follows.
    //
    // Keyed on who and where: signing in as someone else, or switching
    // property, reloads the board and the catalogue for the new scope.
    const auth = inject(AuthService);
    const scopeKey = computed(() => {
      const u = auth.user();
      return u ? `${u.principalId}|${u.propertyId ?? ''}|${u.scopes.join(' ')}` : null;
    });
    effect(() => {
      const key = scopeKey();
      if (!this.realApi) return;
      if (key === null) { untracked(() => this.stream.stop()); return; }
      untracked(() => {
        this.services.set([]);
        this.boardRows.set([]);
        this.arrivalRows.set([]);
        this.lastMove.set(null);
        void this.loadBoard();
        void this.loadArrivals();
        // Live: another desk's change reloads this board, debounced so a burst is one reload.
        this.stream.start(() => this.refreshSoon());
      });
    });
  }

  private readonly stream = inject(BoardStream);
  /** True while the live board connection is open. */
  readonly live = this.stream.connected;
  private refreshTimer: ReturnType<typeof setTimeout> | null = null;

  private refreshSoon(): void {
    if (this.refreshTimer) clearTimeout(this.refreshTimer);
    this.refreshTimer = setTimeout(() => {
      void this.loadBoard();
      void this.loadArrivals();
    }, 400);
  }

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

  /** Today's arrivals, with the server's readiness (room turnover, intake). */
  async loadArrivals(): Promise<void> {
    if (!this.realApi) return;
    try {
      const res = await this.ops.arrivals(this.boardDate());
      this.arrivalRows.set(res.items);
      this.arrivalsProblem.set(null);
    } catch (err) {
      this.arrivalsProblem.set(err as ApiProblem);
    }
  }

  async checkIn(id: string): Promise<WriteResult<string>> {
    if (this.realApi) return this.checkInReal(id);
    await this.settle();
    const row = this.arrivals().find((a) => a.id === id);
    if (!row) return { kind: 'denied', code: API_ERROR.authorizationDenied.code };
    if (!(row.formsComplete && row.depositSettled && row.roomReady)) {
      return { kind: 'denied', code: API_ERROR.authorizationDenied.code };
    }
    this.demoCheckedIn.update((l) => [...l, id]);
    this.record('Check-in', `${row.guestAlias} checked in`);
    return { kind: 'committed', value: id };
  }

  /**
   * The server's check-in: a Confirmed → CheckedIn transition asserting the
   * version the desk read. The visit arrives with it, server-side.
   */
  private async checkInReal(id: string): Promise<WriteResult<string>> {
    const row = this.arrivalRows().find((a) => a.appointmentId === id);
    if (!row) return { kind: 'denied', code: API_ERROR.notFound.code };
    try {
      await this.api.transition(id, row.rowVersion, { to: 'CheckedIn', reason: null });
      this.record('Check-in', `${row.guestAlias} checked in`);
      await Promise.all([this.loadArrivals(), this.loadBoard()]);
      return { kind: 'committed', value: id };
    } catch (err) {
      const p = err as ApiProblem;
      if (p.code === API_ERROR.staleVersion.code) void this.loadArrivals();
      return { kind: 'denied', code: p.code };
    }
  }

  /** Demo only: a checked-in appointment has no way back to Confirmed on the server. */
  readonly canUndoCheckIn = !this.realApi;

  undoCheckIn(id: string): void {
    if (this.realApi) return;
    this.demoCheckedIn.update((l) => l.filter((x) => x !== id));
    this.record('Check-in reversed', id);
  }

  assignLocker(id: string): void {
    const n = 100 + Math.floor(Math.random() * 300);
    this.demoArrivals.update((rows) =>
      rows.map((r) => (r.id === id ? { ...r, locker: `L-${n}` } : r)));
    this.record('Locker assigned', `${id} → L-${n}`);
  }

  settleDeposit(id: string): void {
    this.demoArrivals.update((rows) =>
      rows.map((r) => (r.id === id ? { ...r, depositSettled: true } : r)));
    this.record('Deposit settled', id);
  }

  completeForms(id: string): void {
    this.demoArrivals.update((rows) =>
      rows.map((r) => (r.id === id ? { ...r, formsComplete: true } : r)));
    this.record('Forms completed', id);
  }

  markRoomReady(id: string): void {
    this.demoArrivals.update((rows) =>
      rows.map((r) => (r.id === id ? { ...r, roomReady: true } : r)));
    this.record('Room ready', id);
  }

  addWalkIn(): ArrivalRow {
    const n = 4900 + this.demoArrivals().length;
    const row: ArrivalRow = {
      id: `r-${n}`, guestAlias: `Guest ${n}`, time: now(), service: 'Express 30',
      formsComplete: false, depositSettled: false, roomReady: true, locker: null, pager: null,
    };
    this.demoArrivals.update((rows) => [row, ...rows]);
    this.record('Walk-in created', row.guestAlias);
    return row;
  }

  // ---- the board (real API) ---------------------------------------------

  /**
   * Reads one property-local day and derives the board from it.
   *
   * Availability is read FIRST, and not in parallel, because it is the only
   * endpoint that publishes the property's time zone — and the appointment
   * window cannot be stated correctly without it. The two endpoints interpret
   * `date` differently: /availability builds its grid in the property's zone,
   * while /appointments?date= builds the interval from UTC midnight. For a
   * property east of UTC+12 those are different days, and the board would lose
   * its whole morning. So the zone comes from availability and the
   * appointments are asked for by an explicit from/to instead.
   *
   * Availability's own failure is swallowed: a 422 on the grid must not leave
   * the operator with no appointments at all.
   */
  async loadBoard(date = this.boardDate()): Promise<void> {
    if (!this.realApi) return;

    this.boardLoading.set(true);
    this.boardProblem.set(null);
    try {
      const availability = await this.api.availability(date)
        .catch((): AvailabilityDto | null => null);

      const page = availability === null
        // No zone to reason with, so fall back to the server's own
        // interpretation of the date rather than inventing a window.
        ? await this.api.appointments({ date, limit: BOARD_PAGE_LIMIT })
        : await this.api.appointments({
            ...localDayWindow(date, availability.timeZone),
            limit: BOARD_PAGE_LIMIT,
          });

      this.boardDate.set(date);
      this.boardRows.set(page.items);

      const zone = availability?.timeZone ?? page.items[0]?.propertyTimeZone ?? 'UTC';
      const published = availability === null
        ? fallbackDay(date, zone)
        : dayFromAvailability(availability);

      // Widened to cover anything outside opening hours. An appointment drawn
      // at a negative percentage is invisible, which reads as "it is gone".
      this.businessDay.set(
        spanning(published, page.items.flatMap((a) => [a.startUtc, a.endUtc])));

      if (page.total > page.items.length) {
        this.record('Board truncated',
          `${page.items.length} of ${page.total} appointments shown for ${date}`);
      }
    } catch (err) {
      this.boardProblem.set(err as ApiProblem);
    } finally {
      this.boardLoading.set(false);
    }
  }

  /** The service catalogue, for the booking and availability screens. */
  async loadServices(): Promise<void> {
    if (!this.realApi || this.services().length > 0) return;
    try {
      this.services.set(await this.api.services());
    } catch (err) {
      this.boardProblem.set(err as ApiProblem);
    }
  }

  /** The appointment behind a drawn slot, or null for a turnover block. */
  rowFor(appointmentId: string): AppointmentDto | null {
    return this.boardRows().find((a) => a.appointmentId === appointmentId) ?? null;
  }

  /**
   * The numeric version a proposal must assert, or null when the board does
   * not know it.
   *
   * Null is a refusal, not a zero: `fromRowVersion` is required and asserting
   * a guessed value either fails the optimistic check or, worse, passes it
   * against a record the operator never looked at.
   */
  rowVersionFor(appointmentId: string): number | null {
    if (this.realApi) return this.rowFor(appointmentId)?.rowVersion ?? null;
    const demo = this.demoAppointments().find((a) => a.id === appointmentId);
    if (demo === undefined) return null;
    const n = Number(demo.version.replace('v', ''));
    return Number.isFinite(n) ? n : null;
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
   *
   * Rejects with an ApiProblem rather than returning a union: the only failure
   * the operator can act on differently is STALE_VERSION, which arrives with
   * the current record attached, and the caller has to handle a thrown problem
   * for the network cases anyway.
   */
  async preflight(proposal: MoveProposal): Promise<PreflightResult> {
    if (!this.realApi) return this.preflightInMemory(proposal);

    const res = await this.api.preflight({
      appointmentId: proposal.appointmentId,
      startUtc: proposal.startUtc,
      providerId: proposal.providerId,
      roomId: proposal.roomId,
      fromRowVersion: proposal.fromRowVersion,
    });

    const result = this.fromPreflightResponse(res, proposal);
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
   * The server owns the move. Nothing here edits a lane: on success the day is
   * re-read and the board re-renders from the response. It used to APPEND a
   * slot labelled "Moved" and leave the original in place, so every commit
   * drew the same appointment twice and the second copy had no identity.
   */
  async commitMove(token: string, reason?: string): Promise<CommitMoveResult> {
    if (!this.realApi) return this.commitMoveInMemory(token, reason);

    const pf = this.issued.get(token);
    if (!pf) return { kind: 'expired' };

    const id = pf.proposal.appointmentId;

    // The reason is part of the identity because the server hashes method,
    // path AND body. Reusing the key after the operator types a reason would
    // answer IDEMPOTENCY_MISMATCH; minting a new key for a bare retry of the
    // SAME body would defeat the replay. Both halves matter.
    const attempt = `reassign|${id}|${token}|${reason ?? ''}`;
    const key = this.keys.keyFor(attempt);

    try {
      const appointment = await this.api.reassign(
        id, { token, reason: reason ?? null }, key);

      this.keys.forget(attempt);
      this.issued.delete(token);
      this.pendingProposal.set(null);
      this.record('Move committed',
        `${pf.proposedStart}–${pf.proposedEnd}${reason ? ' — ' + reason : ''}`,
        pf.conflicts[0]?.code);

      // CON-006: the server says until when this token can undo the move.
      const until = appointment.undoUntilUtc ? Date.parse(appointment.undoUntilUtc) : NaN;
      if (Number.isFinite(until) && until > Date.now()) {
        this.lastMove.set({ appointmentId: id, token, untilMs: until, what: `${pf.proposedStart}–${pf.proposedEnd}` });
        this.startUndoClock(until);
      }

      await this.loadBoard();
      return { kind: 'committed', appointment };
    } catch (err) {
      return this.interpretCommitFailure(err as ApiProblem, token, attempt);
    }
  }

  /**
   * Turns a refused commit into something the operator can act on.
   *
   * Each branch exists because the required behaviour differs: one keeps the
   * token, one destroys it, one has no override path at all. Collapsing them
   * into a single "that did not commit" is what left the board claiming the
   * preflight was invalid when in fact a reason was all that was missing.
   */
  private interpretCommitFailure(
    problem: ApiProblem,
    token: string,
    attempt: string,
  ): CommitMoveResult {
    const views = (rows: readonly ConflictDto[] | null) => (rows ?? []).map(toConflictView);

    switch (problem.code) {
      case 'SOFT_CONFLICT_APPROVAL_REQUIRED': {
        // The token SURVIVES this refusal. The retry carries a reason, so its
        // body differs and it needs a new key — this one is spent.
        this.keys.forget(attempt);
        const kept = problem.token ?? token;
        const expiresAtMs = problem.expiresUtc === null
          ? Date.now() + PREFLIGHT_TTL_MS
          : Date.parse(problem.expiresUtc);
        const pf = this.issued.get(token);
        if (pf && kept !== token) this.issued.set(kept, { ...pf, token: kept });
        return {
          kind: 'reason-required',
          token: kept,
          expiresAtMs: Number.isFinite(expiresAtMs) ? expiresAtMs : Date.now() + PREFLIGHT_TTL_MS,
          conflicts: views(problem.conflicts),
        };
      }

      case 'HARD_CONFLICT':
        this.keys.forget(attempt);
        this.issued.delete(token);
        this.pendingProposal.set(null);
        // Both sets present means the board changed under the operator, not
        // that the move was always impossible. Refresh so what they see next
        // is the board the server just described.
        if (problem.conflictsWhenShown !== null) {
          void this.loadBoard();
          this.record('Move refused — board changed', problem.detail ?? '',
            problem.conflicts?.[0]?.code);
          return {
            kind: 'board-changed',
            conflicts: views(problem.conflicts),
            conflictsWhenShown: views(problem.conflictsWhenShown),
          };
        }
        this.record('Move refused — hard conflict', problem.detail ?? '',
          problem.conflicts?.[0]?.code);
        return { kind: 'hard-conflict', conflicts: views(problem.conflicts) };

      case 'PREFLIGHT_EXPIRED':
        this.keys.forget(attempt);
        this.issued.delete(token);
        this.pendingProposal.set(null);
        return { kind: 'expired' };

      case 'STALE_VERSION':
        this.keys.forget(attempt);
        this.issued.delete(token);
        this.pendingProposal.set(null);
        void this.loadBoard();
        return { kind: 'stale', current: problem.current };

      default:
        // A retryable failure keeps its key: the next attempt is the same
        // request, and the server must be able to recognise it as a replay
        // rather than book the move a second time.
        if (!problem.retryable) this.keys.forget(attempt);
        return { kind: 'denied', code: String(problem.code), detail: problem.detail };
    }
  }

  private fromPreflightResponse(
    res: PreflightResponseDto,
    proposal: MoveProposal,
  ): PreflightResult {
    const day = this.businessDay();
    const expires = Date.parse(res.expiresUtc);
    return {
      token: res.token,
      // The server's own expiry wins; ttlSeconds is the same value counted
      // down, and PREFLIGHT_TTL_MS is only a shape for the in-memory path.
      expiresAtMs: Number.isFinite(expires) ? expires : Date.now() + res.ttlSeconds * 1000,
      proposal: { ...proposal, fromRowVersion: res.fromRowVersion },
      proposedStart: clockLabel(day, res.proposedStartUtc),
      proposedEnd: clockLabel(day, res.proposedEndUtc),
      proposedStartUtc: res.proposedStartUtc,
      proposedEndUtc: res.proposedEndUtc,
      conflicts: res.conflicts.map(toConflictView),
      commitAllowed: res.commitAllowed,
      requiresReason: res.requiresReason,
    };
  }

  /* ---- the in-memory path, unchanged in behaviour ---------------------- */

  private async preflightInMemory(proposal: MoveProposal): Promise<PreflightResult> {
    await this.settle(320);

    const day = this.businessDay();
    const startPct = pctAt(day, proposal.startUtc);
    const appt = this.demoAppointments().find((a) => a.id === proposal.appointmentId);
    const widthPct = pctForMinutes(day, appt?.durationMin ?? 60);

    // The demo lanes carry no resource ids, so the target lane is the one the
    // appointment is already drawn on.
    const lane = this.demoLanes().find((l) =>
      l.slots.some((slotOf) => slotOf.appointmentId === proposal.appointmentId));
    const conflicts: ConflictDefinition[] = [];

    // A room already occupied in the target window is physically impossible.
    const overlaps = lane?.slots.some((slotOf) =>
      slotOf.state !== 'turnover' &&
      slotOf.appointmentId !== proposal.appointmentId &&
      startPct < slotOf.startPct + slotOf.widthPct &&
      slotOf.startPct < startPct + widthPct);

    if (overlaps) {
      conflicts.push(lane?.role === 'Room' ? CONFLICTS['CON-002'] : CONFLICTS['CON-001']);
    }
    if (lane?.slots.some((slotOf) => slotOf.state === 'blocked')) {
      conflicts.push(CONFLICTS['CON-004']);
    }

    const endMs = Date.parse(proposal.startUtc) + (appt?.durationMin ?? 60) * 60_000;
    const views: ConflictView[] = conflicts.map((c) => ({
      ...c,
      // The in-memory path has no rule discriminator to report, and inventing
      // one would make a demo conflict indistinguishable from a real breach.
      rule: 'simulated',
      resolutions: [],
    }));

    const result: PreflightResult = {
      token: 'pf_' + Math.random().toString(36).slice(2, 10),
      expiresAtMs: Date.now() + PREFLIGHT_TTL_MS,
      proposal,
      proposedStart: clockLabel(day, proposal.startUtc),
      proposedEnd: clockLabel(day, new Date(endMs).toISOString()),
      proposedStartUtc: proposal.startUtc,
      proposedEndUtc: new Date(endMs).toISOString(),
      conflicts: views,
      commitAllowed: views.every((c) => c.overridable),
      requiresReason: views.some((c) => c.overridable),
    };

    this.issued.set(result.token, result);
    this.pendingProposal.set(result);
    return result;
  }

  /**
   * Moves the slot the proposal names, rather than appending a new one.
   *
   * Even in the demo this has to be a move: an append meant the board grew a
   * duplicate on every commit, and no later screen could tell which of the two
   * was the appointment.
   */
  private async commitMoveInMemory(token: string, reason?: string): Promise<CommitMoveResult> {
    await this.settle();

    const pf = this.issued.get(token);
    if (!pf || isExpired(pf)) {
      this.issued.delete(token);
      this.pendingProposal.set(null);
      return { kind: 'expired' };
    }
    if (!pf.commitAllowed) {
      return { kind: 'hard-conflict', conflicts: pf.conflicts };
    }
    if (pf.requiresReason && (reason ?? '').trim().length === 0) {
      return {
        kind: 'reason-required',
        token,
        expiresAtMs: pf.expiresAtMs,
        conflicts: pf.conflicts,
      };
    }

    const before = this.demoLanes().map((l) => ({ ...l, slots: [...l.slots] }));
    this.undoable.set({ lanes: before, at: Date.now(), what: 'Move committed' });

    const day = this.businessDay();
    const startPct = pctAt(day, pf.proposal.startUtc);

    this.demoLanes.update((ls) => ls.map((l) => ({
      ...l,
      slots: l.slots.map((slotOf) => slotOf.appointmentId === pf.proposal.appointmentId
        ? { ...slotOf, startPct, startUtc: pf.proposal.startUtc }
        : slotOf),
    })));

    this.issued.delete(token);
    this.pendingProposal.set(null);
    this.record('Move committed', `${pf.proposedStart}–${pf.proposedEnd}${reason ? ' — ' + reason : ''}`,
      pf.conflicts[0]?.code);

    // No DTO: nothing served this move. The caller reads the board, not this.
    return { kind: 'committed', appointment: null };
  }

  // ---- undo (CON-006) ---------------------------------------------------

  private readonly undoable = signal<{ lanes: Lane[]; at: number; what: string } | null>(null);

  /** The last committed reassign the server will still undo, and until when. */
  private readonly lastMove = signal<{ appointmentId: string; token: string; untilMs: number; what: string } | null>(null);
  /** Ticks once a second while an undo is open, so canUndo closes on time. */
  private readonly undoNow = signal(Date.now());
  private undoTimer: ReturnType<typeof setInterval> | null = null;

  private startUndoClock(untilMs: number): void {
    if (this.undoTimer !== null) clearInterval(this.undoTimer);
    this.undoNow.set(Date.now());
    this.undoTimer = setInterval(() => {
      this.undoNow.set(Date.now());
      if (Date.now() >= untilMs && this.undoTimer !== null) {
        clearInterval(this.undoTimer);
        this.undoTimer = null;
      }
    }, 1000);
  }

  /**
   * Against the API: the server's undo window for the last committed
   * reassign (the same token undoes it, once). In the demo: a local snapshot.
   */
  readonly canUndo = computed(() => {
    if (this.realApi) {
      const m = this.lastMove();
      return !!m && this.undoNow() < m.untilMs;
    }
    const u = this.undoable();
    return !!u && Date.now() - u.at < UNDO_WINDOW_MS;
  });

  /** Seconds left on the undo window, for the countdown. */
  readonly undoSecondsLeft = computed(() => {
    const m = this.lastMove();
    return m ? Math.max(0, Math.ceil((m.untilMs - this.undoNow()) / 1000)) : 0;
  });

  /**
   * Reverts inside the window. Past it the spec requires an explicit
   * compensating reschedule instead, because downstream systems may already
   * have acted on the change. `refused` carries the server's reason — the
   * original slot was taken, or the appointment changed since the move.
   */
  async undoLastMove(): Promise<{ kind: 'undone' } | { kind: 'window-closed' } | { kind: 'refused'; problem: ApiProblem }> {
    if (this.realApi) {
      const m = this.lastMove();
      if (!m || Date.now() >= m.untilMs) { this.lastMove.set(null); return { kind: 'window-closed' }; }
      const attempt = `undo|${m.appointmentId}|${m.token}`;
      try {
        await this.ops.undoReassign(m.appointmentId, m.token, this.keys.keyFor(attempt));
        this.keys.forget(attempt);
        this.lastMove.set(null);
        this.record('Move undone', m.what);
        await this.loadBoard();
        return { kind: 'undone' };
      } catch (err) {
        this.keys.forget(attempt);
        this.lastMove.set(null);
        const problem = err as ApiProblem;
        void this.loadBoard();
        return problem.code === 'PREFLIGHT_EXPIRED' ? { kind: 'window-closed' } : { kind: 'refused', problem };
      }
    }
    const u = this.undoable();
    if (!u) return { kind: 'window-closed' };
    if (Date.now() - u.at >= UNDO_WINDOW_MS) {
      this.undoable.set(null);
      return { kind: 'window-closed' };
    }
    this.demoLanes.set(u.lanes);
    this.undoable.set(null);
    this.record('Move undone', u.what);
    return { kind: 'undone' };
  }

  // ---- appointments -----------------------------------------------------
  async resolveConflict(id: string, reason: string): Promise<WriteResult<Appointment>> {
    await this.settle();
    const appt = this.demoAppointments().find((a) => a.id === id);
    if (!appt) return { kind: 'denied', code: API_ERROR.authorizationDenied.code };

    this.demoAppointments.update((list) =>
      list.map((a) => (a.id === id ? { ...a, state: 'booked', version: bump(a.version) } : a)));
    this.demoLanes.update((ls) =>
      ls.map((l) => ({ ...l, slots: l.slots.map((s) => s.state === 'conflict' ? { ...s, state: 'booked', label: 'Rebooked' } : s) })));
    this.record('Conflict overridden', `${appt.guestAlias} — ${reason}`, 'CON-001');
    return { kind: 'committed', value: appt };
  }

  createAppointment(): Appointment {
    const n = 4900 + this.demoAppointments().length;
    const a: Appointment = {
      id: `a-${n}`, guestAlias: `Guest ${n}`, service: 'Aromatherapy 60',
      provider: 'Priya Nair', room: 'Room 2', start: '5:30pm',
      durationMin: 60, state: 'booked', version: 'v1',
    };
    this.demoAppointments.update((l) => [...l, a]);
    this.demoLanes.update((ls) => ls.map((l, i) => i === 2
      ? { ...l, slots: [...l.slots, { label: 'Aromatherapy 60', startPct: 78, widthPct: 18, state: 'booked' as const, appointmentId: a.id }] }
      : l));
    this.record('Appointment created', a.guestAlias);
    return a;
  }

  /** Against the API a cancellation is a transition, asserting the version read. */
  async cancelAppointmentReal(id: string, reasonCode = 'GuestRequest'): Promise<WriteResult<string>> {
    const row = this.rowFor(id);
    if (!row) return { kind: 'denied', code: API_ERROR.notFound.code };
    try {
      await this.api.transition(id, row.rowVersion, { to: 'Cancelled', reason: null, reasonCode });
      this.record('Appointment cancelled', row.guestAlias);
      await Promise.all([this.loadBoard(), this.loadArrivals()]);
      return { kind: 'committed', value: id };
    } catch (err) {
      return { kind: 'denied', code: (err as ApiProblem).code };
    }
  }

  cancelAppointment(id: string): void {
    if (this.realApi) { void this.cancelAppointmentReal(id); return; }
    const a = this.demoAppointments().find((x) => x.id === id);
    this.demoAppointments.update((l) => l.filter((x) => x.id !== id));
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
    this.demoAppointments.set([...APPOINTMENTS]);
    this.demoArrivals.set([...ARRIVALS]);
    this.devices.set([...DEVICES]);
    this.stock.set([...STOCK]);
    this.ledger.set([...LEDGER]);
    this.rules.set([...MESSAGE_RULES]);
    this.owners.set([...OWNERS]);
    this.staff.set([...STAFF]);
    this.demoLanes.set(LANES.map((l) => ({ ...l, slots: [...l.slots] })));
    this.demoCheckedIn.set([]);
    this.ownershipPreflight.set('idle');
    this.record('Demo data reset', 'All screens returned to their starting state');

    // Reset means "show the starting state", and against the real API that is
    // whatever the server holds — not the seed arrays above.
    if (this.realApi) void this.loadBoard();
  }
}

const bump = (v: string) => 'v' + (Number(v.replace('v', '')) + 1);

/**
 * One page is the whole board.
 *
 * A day at one property does not exceed this, and the server caps the page
 * anyway. When it does, loadBoard records that the board is truncated rather
 * than quietly drawing part of a day — a scheduler who cannot see an
 * appointment will double-book over it.
 */
const BOARD_PAGE_LIMIT = 200;

/**
 * One calendar day in the property's zone, as instants.
 *
 * Local midnight to the next local midnight, so an appointment is returned by
 * the day the property considers it to be on — not the day UTC does.
 */
const localDayWindow = (date: string, timeZone: string): { from: string; to: string } => {
  const window = fallbackDay(date, timeZone, 0, 24 * 60);
  return {
    from: new Date(window.openUtcMs).toISOString(),
    to: new Date(window.closeUtcMs).toISOString(),
  };
};

/* ------------------------------------------------------------------ */
/* Wire → view mapping. The only place a DTO becomes a view model.     */
/* ------------------------------------------------------------------ */

/**
 * §7.1 status → the board's visual state.
 *
 * `conflict` is absent by design: a standing conflict is not a property of an
 * appointment the server publishes, it is the answer to a preflight. Painting
 * a slot amber because a previous preflight said so would show a conflict the
 * server no longer believes in.
 */
const slotState = (status: string): SlotState => {
  switch (status) {
    case 'InService':  return 'in-progress';
    case 'Completed':  return 'complete';
    case 'Cancelled':
    case 'NoShow':     return 'blocked';
    default:           return 'booked';
  }
};

const toViewAppointment = (day: BusinessDay, a: AppointmentDto): Appointment => ({
  id: a.appointmentId,
  guestAlias: a.guestAlias,
  service: a.serviceName,
  // The server sends null when nothing is assigned yet; the board reads it as
  // a word, and an empty cell would look like a rendering fault.
  provider: a.providerId ?? 'Unassigned',
  room: a.roomId ?? 'Unassigned',
  start: clockLabel(day, a.startUtc),
  durationMin: a.durationMinutes,
  state: slotState(a.status),
  // Stringified for the view model's optimistic-concurrency field. Every
  // request that needs the real value takes it from the DTO, not from here.
  version: String(a.rowVersion),
});

const toSlot = (day: BusinessDay, a: AppointmentDto): LaneSlot => ({
  label: a.serviceName,
  startPct: pctAt(day, a.startUtc),
  // Floored so a 30-minute service on a long board is still clickable.
  widthPct: Math.max(3, pctForMinutes(day, a.durationMinutes)),
  state: slotState(a.status),
  appointmentId: a.appointmentId,
  startUtc: a.startUtc,
  providerId: a.providerId,
  roomId: a.roomId,
  rowVersion: a.rowVersion,
  durationMinutes: a.durationMinutes,
});

/**
 * One lane per resource the day actually uses.
 *
 * An appointment with both a provider and a room appears on BOTH lanes — it
 * occupies both, and a room lane that omitted it would read as free. Lanes are
 * not read from configuration because no resource endpoint exists yet, so an
 * empty room is simply not drawn; that is a gap, not a decision.
 */
const buildLanes = (day: BusinessDay, rows: readonly AppointmentDto[]): Lane[] => {
  const lanes = new Map<string, { role: string; slots: LaneSlot[] }>();

  const add = (name: string, role: string, slot: LaneSlot): void => {
    const key = `${role}|${name}`;
    const lane = lanes.get(key) ?? { role, slots: [] };
    lane.slots.push(slot);
    lanes.set(key, lane);
  };

  for (const a of rows) {
    const slot = toSlot(day, a);
    if (a.providerId !== null) add(a.providerId, 'Provider', slot);
    if (a.roomId !== null) add(a.roomId, 'Room', slot);
    if (a.providerId === null && a.roomId === null) add('Unassigned', 'Provider', slot);
  }

  return [...lanes.entries()]
    // Providers first, then rooms, each alphabetically — the same reading
    // order as the seeded board, so the lane filter behaves the same way.
    .sort(([a], [b]) => a.localeCompare(b))
    .map(([key, lane]) => ({
      name: key.slice(key.indexOf('|') + 1),
      role: lane.role,
      slots: lane.slots.sort((x, y) => x.startPct - y.startPct),
    }));
};

/**
 * A server arrival as the desk table draws it. Deposit is not tracked by
 * scheduling, so it does not block; intake blocks only while Pending.
 */
function toArrivalRow(a: ArrivalDto): ArrivalRow {
  return {
    id: a.appointmentId,
    guestAlias: a.guestAlias,
    time: a.startLocal.slice(11, 16),
    service: a.serviceName,
    formsComplete: a.intake !== 'Pending',
    depositSettled: a.deposit !== 'Pending',
    roomReady: a.roomReady,
    locker: null,
    pager: null,
  };
}
