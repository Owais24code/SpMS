import { Component, ChangeDetectionStrategy, signal, inject, computed, HostListener } from '@angular/core';
import { PageHeader } from '../../shared/components/page-header/page-header';
import { FocusTrapDirective } from '../../shared/directives/focus-trap.directive';
import { WorkspaceStore } from '../../core/services/workspace-store';
import { ToastService } from '../../core/services/toast.service';
import { ConfirmService } from '../../core/services/confirm.service';
import { HOURS, ACTIVE_CONFLICT, HARD_CONFLICT } from '../../core/data/workspace-data';
import type { Conflict } from '../../core/models/spa.model';

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
  protected readonly drawer = signal<Conflict | null>(null);
  protected readonly reason = signal('');
  protected readonly committing = signal(false);

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
    if (this.drawer()) this.close();
  }

  protected openSoft(): void { this.reason.set(''); this.drawer.set(ACTIVE_CONFLICT); }
  protected openHard(): void { this.reason.set(''); this.drawer.set(HARD_CONFLICT); }

  protected onSlot(state: string): void {
    if (state === 'conflict') this.openSoft();
    else if (state === 'blocked') this.openHard();
    else this.toast.info('Appointment selected', 'Drag to move it, or press M to move with the keyboard.');
  }

  protected close(): void { this.drawer.set(null); this.reason.set(''); }

  protected canCommit(): boolean {
    const c = this.drawer();
    return !!c && c.severity === 'soft' && this.reason().trim().length > 3 && !this.committing();
  }

  protected async commit(): Promise<void> {
    if (!this.canCommit()) return;
    this.committing.set(true);

    const conflicted = this.store.appointments().find((a) => a.state === 'conflict');
    const res = await this.store.resolveConflict(conflicted?.id ?? 'a-4824', this.reason().trim());
    this.committing.set(false);

    if (res.kind === 'committed') {
      this.toast.success('Override recorded', 'The appointment is booked and your reason is on the audit trail.');
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
}
