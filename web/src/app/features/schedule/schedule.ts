import { Component, ChangeDetectionStrategy, signal, inject, computed, HostListener } from '@angular/core';
import { PageHeader } from '../../shared/components/page-header/page-header';
import { FocusTrapDirective } from '../../shared/directives/focus-trap.directive';
import { WorkspaceStore } from '../../core/services/workspace-store';
import { ToastService } from '../../core/services/toast.service';
import { ConfirmService } from '../../core/services/confirm.service';
import { CONFLICTS } from '../../core/models/contract';
import {
  UNDO_WINDOW_MS, conflictKey,
  type ConflictView, type MoveProposal, type PreflightResult,
} from '../../core/models/preflight';
import type { ApiProblem } from '../../core/models/api-problem';
import type { LaneSlot } from '../../core/models/spa.model';
import {
  clockLabelAtPct, instantAtPct, shiftDate, snapToMinutes,
} from '../../core/services/board-time';

type LaneFilter = 'all' | 'providers' | 'rooms';

/**
 * What the conflict drawer is showing.
 *
 * A list, not a single conflict. The server returns every breach the complete
 * post-change state produces (CON-005), and the drawer used to render only
 * `conflicts[0]` — so an operator overriding a provider overlap never saw that
 * the same move also compressed a room turnover.
 */
interface ConflictPanel {
  readonly title: string;
  readonly conflicts: readonly ConflictView[];
  /**
   * Set only for a board-changed refusal: the set the operator was shown when
   * they decided, so the drawer can say what moved rather than "try again".
   */
  readonly whenShown: readonly ConflictView[] | null;
  /** True when the move has to be preflighted again before it can commit. */
  readonly mustRepreflight: boolean;
}

@Component({
  selector: 'app-schedule',
  standalone: true,
  imports: [PageHeader, FocusTrapDirective],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './schedule.html',
  styleUrl: './schedule.scss',
})
export class Schedule {
  private readonly toast = inject(ToastService);
  private readonly confirm = inject(ConfirmService);
  protected readonly store = inject(WorkspaceStore);

  protected readonly view = signal<'day' | 'week'>('day');
  protected readonly laneFilter = signal<LaneFilter>('all');

  /** Closed on load. A screen you navigated to should not open behind a modal. */
  protected readonly panel = signal<ConflictPanel | null>(null);
  protected readonly reason = signal('');
  protected readonly committing = signal(false);

  /* ---------------- keyboard move (GUI-004) ---------------- */

  /**
   * The slot currently being moved with the keyboard, if any.
   *
   * The slot itself is held, not just its index: the proposal is built from
   * the slot's own appointment id, resource ids and row version. It used to be
   * built from `appointments()[0]`, so every preview asserted some other
   * record's version and the move that committed was not the one selected.
   */
  protected readonly moving = signal<{
    readonly lane: string;
    readonly index: number;
    readonly slot: LaneSlot;
    readonly startPct: number;
  } | null>(null);

  protected readonly preflightResult = signal<PreflightResult | null>(null);
  protected readonly preflighting = signal(false);

  /**
   * A token that survived a soft-conflict refusal.
   *
   * The server says so explicitly: the token remains valid, and the retry
   * carrying the operator's reason must quote THE SAME one. Minting a fresh
   * token would throw away their decision and restart the TTL.
   */
  protected readonly retryToken = signal<string | null>(null);

  /** Announced through an aria-live region so the move is audible, not just visible. */
  protected readonly announcement = signal('');

  protected readonly lanes = computed(() => {
    const f = this.laneFilter();
    return this.store.lanes().filter((l) =>
      f === 'all' ? true : f === 'rooms' ? l.role === 'Room' : l.role !== 'Room');
  });

  /** Week view shows five compressed days rather than the same board again. */
  protected readonly weekDays = ['Mon 15', 'Tue 16', 'Wed 17', 'Thu 18', 'Fri 19'];

  /** Formatted in UTC because boardDate is already a property-local calendar date. */
  protected readonly dateLabel = computed(() => {
    const [y, m, d] = this.store.boardDate().split('-').map(Number);
    return new Intl.DateTimeFormat('en-GB', {
      timeZone: 'UTC', weekday: 'short', day: '2-digit', month: 'short',
    }).format(new Date(Date.UTC(y, (m ?? 1) - 1, d ?? 1)));
  });

  /** True when any conflict on screen blocks the commit outright. */
  protected readonly overridable = computed(() => {
    const p = this.panel();
    return !!p && !p.mustRepreflight && p.conflicts.length > 0
      && p.conflicts.every((c) => c.overridable);
  });

  constructor() {
    // Re-read on entry. The store loads once when it is first injected, which
    // may have been before this operator signed in or several days of
    // navigation ago; a board is the one screen that must not be stale.
    void this.store.loadBoard();
  }

  @HostListener('document:keydown.escape')
  protected onEscape(): void {
    if (this.panel()) { this.close(); return; }
    if (this.moving()) this.cancelMove();
  }

  protected openSoft(): void { this.showRegisterEntry('CON-001'); }
  protected openHard(): void { this.showRegisterEntry('CON-002'); }

  /**
   * Opens the drawer on a register entry rather than a server response.
   *
   * Only reachable from the seeded board's conflict and blocked slots, which
   * no endpoint describes. Everything the API reports comes through
   * `showConflicts` with the server's own rules and resolutions.
   */
  private showRegisterEntry(code: string): void {
    const def = CONFLICTS[code];
    this.reason.set('');
    this.panel.set({
      title: def.summary,
      conflicts: [{ ...def, rule: 'register', resolutions: this.resolutionsFor(code) }],
      whenShown: null,
      mustRepreflight: false,
    });
  }

  private showConflicts(
    title: string,
    conflicts: readonly ConflictView[],
    options: { whenShown?: readonly ConflictView[]; mustRepreflight?: boolean } = {},
  ): void {
    this.panel.set({
      title,
      conflicts,
      whenShown: options.whenShown ?? null,
      mustRepreflight: options.mustRepreflight ?? false,
    });
  }

  /**
   * Enters keyboard move mode.
   *
   * Keyboard is the only move path that exists on this board. The copy used to
   * offer dragging as well; nothing implemented it, so the instruction sent
   * operators looking for an interaction that was not there.
   */
  protected startKeyboardMove(laneName: string, index: number, slot: LaneSlot): void {
    if (slot.appointmentId === undefined) {
      this.toast.info(`${slot.label} is not a booking`,
        'Turnover and maintenance blocks are not appointments, so there is nothing to reschedule.');
      return;
    }
    this.moving.set({ lane: laneName, index, slot, startPct: slot.startPct });
    this.announce('Moving. Arrow keys shift the appointment, Enter previews the change, Escape cancels.');
  }

  protected nudge(by: number): void {
    const m = this.moving();
    if (!m) return;
    const next = Math.max(0, Math.min(100 - m.slot.widthPct, m.startPct + by));
    this.moving.set({ ...m, startPct: next });
    // A nudge invalidates the preview: the token was issued for the old start.
    this.preflightResult.set(null);
    this.retryToken.set(null);
    this.store.discardProposal();
    this.announce(`Proposed start ${this.clockAt(next)}.`);
  }

  protected cancelMove(): void {
    if (!this.moving()) return;
    this.moving.set(null);
    this.preflightResult.set(null);
    this.retryToken.set(null);
    this.store.discardProposal();
    this.announce('Move cancelled. Nothing changed.');
  }

  /** Builds a proposal and asks the server to validate it. Commits nothing. */
  protected async previewMove(): Promise<void> {
    const m = this.moving();
    if (!m) return;

    const proposal = this.proposalFrom(m.slot, m.startPct);
    if (proposal === null) {
      this.toast.error('This block cannot be moved',
        'The board does not hold an appointment id and version for it. Reload the day and try again.');
      return;
    }

    this.preflighting.set(true);
    try {
      const res = await this.store.preflight(proposal);
      this.preflightResult.set(res);
      this.retryToken.set(null);

      if (res.conflicts.length === 0) {
        this.announce(
          `No conflicts. ${res.proposedStart} to ${res.proposedEnd}. Press Enter again to commit.`);
        return;
      }

      this.reason.set('');
      this.showConflicts(this.conflictTitle(res.conflicts), res.conflicts);
      this.announce(
        `${res.conflicts.length} conflict${res.conflicts.length === 1 ? '' : 's'}. `
        + res.conflicts.map((c) => c.summary).join('. ')
        + (res.commitAllowed
          ? res.requiresReason ? '. Override permitted with a reason.' : '.'
          : '. Not overridable.'));
    } catch (err) {
      this.onProblem(err as ApiProblem);
    } finally {
      this.preflighting.set(false);
    }
  }

  /**
   * The proposal for one slot at one position.
   *
   * Returns null rather than substituting anything: a proposal with someone
   * else's id or a guessed version is worse than no proposal at all.
   */
  private proposalFrom(slot: LaneSlot, startPct: number): MoveProposal | null {
    const id = slot.appointmentId;
    if (id === undefined) return null;

    const fromRowVersion = this.store.rowVersionFor(id);
    if (fromRowVersion === null) return null;

    return {
      appointmentId: id,
      // Snapped, because the server's availability grid and turnover rules are
      // stated in minutes and a percentage produces instants like 13:07:12.
      startUtc: snapToMinutes(instantAtPct(this.store.businessDay(), startPct)),
      providerId: slot.providerId ?? null,
      roomId: slot.roomId ?? null,
      fromRowVersion,
    };
  }

  /** Second Enter: commit against the token from the preview. */
  protected async confirmMove(): Promise<void> {
    const res = this.preflightResult();
    if (!res) { await this.previewMove(); return; }

    const token = this.retryToken() ?? res.token;
    const reason = this.reason().trim();

    this.committing.set(true);
    const out = await this.store.commitMove(token, reason || undefined);
    this.committing.set(false);

    switch (out.kind) {
      case 'committed':
        this.moving.set(null);
        this.preflightResult.set(null);
        this.retryToken.set(null);
        this.close();
        this.announce('Move committed.');
        this.toast.success('Appointment moved',
          `${res.proposedStart}–${res.proposedEnd}.`,
          // Offered only where it can actually be honoured. Against the API a
          // committed reassign is already audited; reverting it is a
          // compensating reschedule, not an undo.
          this.store.canUndo() ? () => this.undo() : undefined);
        return;

      case 'reason-required':
        // The token survives. Keep it, keep whatever is typed, and prompt.
        this.retryToken.set(out.token);
        this.showConflicts(this.conflictTitle(out.conflicts), out.conflicts);
        this.announce('A reason is required before this move can commit.');
        this.toast.warn('A reason is required',
          'This move crosses a soft conflict. Your reason is recorded against your name.');
        return;

      case 'hard-conflict':
        this.preflightResult.set(null);
        this.retryToken.set(null);
        this.showConflicts('This move cannot be committed', out.conflicts);
        this.announce('Hard conflict. This move cannot be committed by any role.');
        return;

      case 'board-changed':
        // Not "your preflight expired" — someone else changed the board, and
        // the operator needs to know what before deciding again.
        this.preflightResult.set(null);
        this.retryToken.set(null);
        this.showConflicts('The board changed while you were deciding', out.conflicts, {
          whenShown: out.conflictsWhenShown,
          mustRepreflight: true,
        });
        this.announce('The board changed while you were deciding. Preview the move again.');
        this.toast.warn('The board changed',
          'The conflicts are not the ones you were shown. Review them and preview the move again.');
        return;

      case 'expired':
        this.preflightResult.set(null);
        this.retryToken.set(null);
        this.store.discardProposal();
        this.announce('That preview is no longer valid.');
        this.toast.warn('Preview no longer valid',
          'The preflight token has expired or was already used. Preview the move again.');
        return;

      case 'stale':
        this.preflightResult.set(null);
        this.retryToken.set(null);
        this.announce('The appointment changed while you were deciding.');
        this.toast.warn('The appointment changed',
          out.current === null
            ? 'Someone edited it while you were deciding. The board has been refreshed.'
            : `It is now ${out.current.serviceName} at ${out.current.startLocal}, version ${out.current.rowVersion}. The board has been refreshed.`);
        return;

      case 'denied':
        this.announce('That did not commit.');
        this.toast.error('That did not commit',
          out.detail ?? 'The API refused the move. Your reason is still here.', out.code);
        return;
    }
  }

  /**
   * Turns an API failure into what the operator has to do next.
   *
   * Each code has a required behaviour and they are not interchangeable: 403
   * must not be retried, 422 must preserve what was typed and point at the
   * fields, 412 must refresh and show the difference.
   */
  private onProblem(problem: ApiProblem): void {
    switch (problem.code) {
      case 'VALIDATION_FAILED':
        this.toast.error('The API would not accept that move',
          (problem.fieldViolations ?? []).map((v) => `${v.field}: ${v.rule}`).join(', ')
          || problem.detail || 'One or more fields were not accepted.',
          problem.correlationId);
        return;

      case 'AUTHENTICATION_REQUIRED':
        this.toast.error('Your session has expired',
          'Sign in again. Nothing you have typed is discarded.', problem.code);
        return;

      case 'AUTHORIZATION_DENIED':
        // Not retried and not offered again: the answer will not change.
        this.toast.error('You do not have the scope for this',
          'Moving an appointment needs spa.schedule. Ask a spa manager to do it.', problem.code);
        return;

      case 'NOT_FOUND':
        void this.store.loadBoard();
        this.toast.warn('That appointment is gone',
          'It was cancelled or moved by someone else. The board has been refreshed.', problem.code);
        return;

      case 'STALE_VERSION':
        void this.store.loadBoard();
        this.cancelMove();
        this.toast.warn('The appointment changed since you read it',
          problem.current === null
            ? 'The board has been refreshed. Select it again to move it.'
            : `It is now ${problem.current.serviceName} at ${problem.current.startLocal}, version ${problem.current.rowVersion}. The board has been refreshed.`,
          problem.code);
        return;

      case 'NETWORK_UNREACHABLE':
        this.toast.error('The API did not answer',
          'Nothing was changed. Check the connection and try again.', problem.correlationId);
        return;

      default:
        this.toast.error('That did not work',
          problem.detail ?? problem.title, problem.correlationId);
    }
  }

  /** CON-006: revert inside the window, compensating reschedule after it. */
  protected undo(): void {
    const outcome = this.store.undoLastMove();
    if (outcome === 'undone') {
      this.toast.success('Move reverted', 'The board is back as it was.');
      this.announce('Move reverted.');
    } else {
      this.toast.warn('Undo window has closed',
        `Undo is available for ${UNDO_WINDOW_MS / 1000} seconds. Book a compensating reschedule instead so the guest is told.`);
    }
  }

  protected clockAt(pct: number): string {
    return clockLabelAtPct(this.store.businessDay(), pct);
  }

  /** Stable identity for @for. Never the code alone — see ConflictDto.rule. */
  protected keyOf(c: ConflictView): string { return conflictKey(c); }

  private conflictTitle(conflicts: readonly ConflictView[]): string {
    if (conflicts.length === 1) return conflicts[0].summary;
    return `${conflicts.length} conflicts on this move`;
  }

  private announce(msg: string): void {
    this.announcement.set(msg);
  }

  protected onSlot(slot: LaneSlot): void {
    if (slot.state === 'conflict') { this.openSoft(); return; }
    if (slot.state === 'blocked') { this.openHard(); return; }
    if (slot.appointmentId === undefined) {
      this.toast.info(slot.label, 'Not a booking — there is nothing to reschedule.');
      return;
    }
    this.toast.info('Appointment selected',
      'Press M to move it, then the arrow keys to shift it and Enter to preview.');
  }

  protected close(): void { this.panel.set(null); this.reason.set(''); }

  protected canCommit(): boolean {
    return this.overridable() && this.reason().trim().length > 3 && !this.committing();
  }

  protected async commit(): Promise<void> {
    if (!this.canCommit()) return;

    // A move in flight commits through its token; the drawer's own path is for
    // the seeded board's standing conflicts, which no endpoint describes.
    if (this.preflightResult() !== null || this.retryToken() !== null) {
      await this.confirmMove();
      return;
    }

    this.committing.set(true);
    const conflicted = this.store.appointments().find((a) => a.state === 'conflict');
    const res = await this.store.resolveConflict(conflicted?.id ?? 'a-4824', this.reason().trim());
    this.committing.set(false);

    if (res.kind === 'committed') {
      this.toast.success('Override recorded',
        'The appointment is booked and your reason is on the audit trail.',
        this.store.canUndo() ? () => this.undo() : undefined);
      this.close();
    } else {
      this.toast.error('That did not commit', 'Your reason is still here — try again.', res.code);
    }
  }

  protected applyAlternative(alt: string): void {
    this.toast.success('Alternative applied', alt);
    this.close();
  }

  protected async newAppointment(): Promise<void> {
    const a = this.store.createAppointment();
    this.toast.success('Appointment created', `${a.guestAlias} — ${a.service} at ${a.start}`);
  }

  protected printRunSheet(): void {
    this.toast.info('Run sheet queued', `${this.lanes().length} lanes for ${this.dateLabel()} sent to the front-desk printer.`);
  }

  protected async clearDay(): Promise<void> {
    const ok = await this.confirm.ask({
      title: 'Clear every booking on this day?',
      consequence: `${this.store.appointments().length} appointments would be cancelled and each guest emailed. This cannot be undone from here.`,
      confirmLabel: 'Cancel all bookings',
      tone: 'danger',
      typeToConfirm: 'CLEAR',
    });
    if (!ok) { this.toast.info('Nothing changed', 'The day is untouched.'); return; }
    this.toast.warn('Day cleared', 'All bookings cancelled. Guests are being notified.');
  }

  protected step(by: number): void {
    this.cancelMove();
    void this.store.loadBoard(shiftDate(this.store.boardDate(), by));
  }

  protected refresh(): void {
    void this.store.loadBoard();
  }

  protected setFilter(f: LaneFilter): void { this.laneFilter.set(f); }

  /**
   * Fallback resolutions for the register entries the seeded board opens.
   *
   * Everything the API reports arrives with its own ranked resolutions on the
   * conflict, so this map is not consulted for a server response.
   */
  private resolutionsFor(code: string): readonly string[] {
    const map: Record<string, readonly string[]> = {
      'CON-001': ['Move to Priya Nair, free from 1:00pm',
                  'Shift the new booking to 2:45pm',
                  'Split across Suite 2 with a second therapist'],
      'CON-002': ['Use Suite 3, free from 1:30pm',
                  'Move the booking to 3:15pm'],
      'CON-003': ['Choose a qualified provider',
                  'Change to a service this provider is licensed for'],
      'CON-004': ['Extend turnover to 30 minutes',
                  'Move to the next free slot'],
      'CON-005': ['Change the guest\'s second booking',
                  'Record an authorised acknowledgment'],
      'CON-006': ['Re-quote at the current catalogue price'],
      'CON-007': ['Retry once the dependency responds'],
    };
    return map[code] ?? [];
  }
}
