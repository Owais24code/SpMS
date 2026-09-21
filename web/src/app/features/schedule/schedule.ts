import { Component, ChangeDetectionStrategy, signal, inject, computed, HostListener } from '@angular/core';
import { PageHeader } from '../../shared/components/page-header/page-header';
import { FocusTrapDirective } from '../../shared/directives/focus-trap.directive';
import { WorkspaceStore } from '../../core/services/workspace-store';
import { ToastService } from '../../core/services/toast.service';
import { ConfirmService } from '../../core/services/confirm.service';
import { HOURS } from '../../core/data/workspace-data';
import { CONFLICTS, type ConflictDefinition } from '../../core/models/contract';
import { UNDO_WINDOW_MS, type PreflightResult } from '../../core/models/preflight';


type LaneFilter = 'all' | 'providers' | 'rooms';

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

  protected readonly hours = HOURS;
  protected readonly view = signal<'day' | 'week'>('day');
  protected readonly laneFilter = signal<LaneFilter>('all');
  protected readonly dayOffset = signal(0);

  /** Closed on load. A screen you navigated to should not open behind a modal. */
  protected readonly drawer = signal<ConflictDefinition | null>(null);
  protected readonly reason = signal('');
  protected readonly committing = signal(false);

  /* ---------------- keyboard move (GUI-004) ---------------- */

  /** The slot currently being moved with the keyboard, if any. */
  protected readonly moving = signal<{ lane: string; index: number; startPct: number } | null>(null);
  protected readonly preflightResult = signal<PreflightResult | null>(null);
  protected readonly preflighting = signal(false);

  /** Announced through an aria-live region so the move is audible, not just visible. */
  protected readonly announcement = signal('');

  protected readonly lanes = computed(() => {
    const f = this.laneFilter();
    return this.store.lanes().filter((l) =>
      f === 'all' ? true : f === 'rooms' ? l.role === 'Room' : l.role !== 'Room');
  });

  /** Week view shows five compressed days rather than the same board again. */
  protected readonly weekDays = ['Mon 15', 'Tue 16', 'Wed 17', 'Thu 18', 'Fri 19'];

  protected readonly dateLabel = computed(() => {
    const d = new Date(2026, 8, 19 + this.dayOffset());
    return new Intl.DateTimeFormat('en-GB', { weekday: 'short', day: '2-digit', month: 'short' }).format(d);
  });

  @HostListener('document:keydown.escape')
  protected onEscape(): void {
    if (this.drawer()) { this.close(); return; }
    if (this.moving()) this.cancelMove();
  }

  protected openSoft(): void { this.reason.set(''); this.drawer.set(CONFLICTS['CON-001']); }
  protected openHard(): void { this.reason.set(''); this.drawer.set(CONFLICTS['CON-002']); }

  /**
   * Enters keyboard move mode. Parity with drag is a spec requirement, not a
   * nicety: the board is a primary surface and dragging is unusable with a
   * keyboard or a screen reader.
   */
  protected startKeyboardMove(laneName: string, index: number, startPct: number): void {
    this.moving.set({ lane: laneName, index, startPct });
    this.announce(`Moving. Arrow keys shift the appointment, Enter previews the change, Escape cancels.`);
  }

  protected nudge(by: number): void {
    const m = this.moving();
    if (!m) return;
    const next = Math.max(0, Math.min(94, m.startPct + by));
    this.moving.set({ ...m, startPct: next });
    this.announce(`Proposed start ${this.clockAt(next)}.`);
  }

  protected cancelMove(): void {
    if (!this.moving()) return;
    this.moving.set(null);
    this.preflightResult.set(null);
    this.store.discardProposal();
    this.announce('Move cancelled. Nothing changed.');
  }

  /** Builds a proposal and asks the server to validate it. Commits nothing. */
  protected async previewMove(): Promise<void> {
    const m = this.moving();
    if (!m) return;

    this.preflighting.set(true);
    const res = await this.store.preflight({
      appointmentId: this.store.appointments()[0]?.id ?? 'a-4821',
      laneName: m.lane,
      startPct: m.startPct,
      widthPct: 20,
      fromVersion: this.store.appointments()[0]?.version ?? 'v1',
    });
    this.preflighting.set(false);
    this.preflightResult.set(res);

    if (res.conflicts.length === 0) {
      this.announce(`No conflicts. ${res.proposedStart} to ${res.proposedEnd}. Press Enter again to commit.`);
    } else {
      const c = res.conflicts[0];
      this.announce(`${res.conflicts.length} conflict. ${c.summary}. ${c.overridable ? 'Override permitted with a reason.' : 'Not overridable.'}`);
      this.drawer.set(c);
    }
  }

  /** Second Enter: commit against the token from the preview. */
  protected async confirmMove(): Promise<void> {
    const res = this.preflightResult();
    if (!res) { await this.previewMove(); return; }

    this.committing.set(true);
    const out = await this.store.commitMove(res.token, this.reason().trim() || undefined);
    this.committing.set(false);

    if (out.kind === 'committed') {
      this.moving.set(null);
      this.preflightResult.set(null);
      this.close();
      this.announce('Move committed.');
      this.toast.success('Appointment moved', `${res.proposedStart}–${res.proposedEnd}.`,
        () => this.undo());
    } else {
      this.announce('That did not commit.');
      this.toast.error('Preflight no longer valid',
        'The board changed while you were deciding. Review it and try the move again.',
        out.code);
      this.preflightResult.set(null);
      this.store.discardProposal();
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
    const m = Math.round(9 * 60 + (pct / 100) * 8 * 60);
    const h24 = Math.floor(m / 60);
    const mm = String(m % 60).padStart(2, '0');
    const h12 = h24 % 12 === 0 ? 12 : h24 % 12;
    return `${h12}:${mm}${h24 < 12 ? 'am' : 'pm'}`;
  }

  private announce(msg: string): void {
    this.announcement.set(msg);
  }

  protected onSlot(state: string): void {
    if (state === 'conflict') this.openSoft();
    else if (state === 'blocked') this.openHard();
    else this.toast.info('Appointment selected', 'Drag to move it, or press M to move with the keyboard.');
  }

  protected close(): void { this.drawer.set(null); this.reason.set(''); }

  protected canCommit(): boolean {
    const c = this.drawer();
    return !!c && c.overridable && this.reason().trim().length > 3 && !this.committing();
  }

  protected async commit(): Promise<void> {
    if (!this.canCommit()) return;
    this.committing.set(true);

    const pf = this.preflightResult();
    if (pf) { this.committing.set(false); await this.confirmMove(); return; }

    const conflicted = this.store.appointments().find((a) => a.state === 'conflict');
    const res = await this.store.resolveConflict(conflicted?.id ?? 'a-4824', this.reason().trim());
    this.committing.set(false);

    if (res.kind === 'committed') {
      this.toast.success('Override recorded',
        'The appointment is booked and your reason is on the audit trail.',
        () => this.undo());
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
    this.toast.success('Appointment created', `${a.guestAlias} — ${a.service} at ${a.start}`, );
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
    this.dayOffset.update((d) => d + by);
  }

  protected setFilter(f: LaneFilter): void { this.laneFilter.set(f); }

  /**
   * CON-002 requires ranked, one-click resolutions per conflict class.
   * Replace with the server's suggestions once /schedule/preflight returns
   * them — the shape is already right.
   */
  protected resolutionsFor(code: string): readonly string[] {
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
