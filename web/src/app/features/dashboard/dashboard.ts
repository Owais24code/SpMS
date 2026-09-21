import { Component, ChangeDetectionStrategy, inject, signal, computed } from '@angular/core';
import { RouterLink, Router } from '@angular/router';
import { PageHeader } from '../../shared/components/page-header/page-header';
import { StatCard } from '../../shared/components/stat-card/stat-card';
import { StatePanel } from '../../shared/components/state-panel/state-panel';
import { WorkspaceStore } from '../../core/services/workspace-store';
import { ToastService } from '../../core/services/toast.service';
import { AuthService } from '../../core/services/auth.service';
import { ACTIVE_CONFLICT } from '../../core/data/workspace-data';
import type { ArrivalRow } from '../../core/models/spa.model';

@Component({
  selector: 'app-dashboard',
  standalone: true,
  imports: [RouterLink, PageHeader, StatCard, StatePanel],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './dashboard.html',
  styleUrl: './dashboard.scss',
})
export class Dashboard {
  private readonly toast = inject(ToastService);
  private readonly router = inject(Router);
  protected readonly store = inject(WorkspaceStore);
  protected readonly auth = inject(AuthService);

  protected readonly conflict = ACTIVE_CONFLICT;
  protected readonly snoozed = signal(false);
  protected readonly busy = signal<string | null>(null);

  protected readonly arrivals = computed(() =>
    this.store.arrivals().filter((a) => !this.store.checkedIn().includes(a.id)).slice(0, 4));

  protected readonly kpis = computed(() => [
    { label: 'Booked today',   value: String(this.store.appointments().length * 14), delta: '+12%', trend: 'up' as const,   tone: 'positive' as const, hint: 'vs. same day last week' },
    { label: 'Utilisation',    value: '78%',   delta: '+4pt',   trend: 'up' as const,   tone: 'positive' as const, hint: 'therapist hours filled' },
    { label: 'Still to arrive',value: String(this.store.pendingArrivals()), delta: `${this.store.checkedIn().length} in`, trend: 'down' as const, tone: 'positive' as const, hint: 'next two hours' },
    { label: 'Open conflicts', value: String(this.store.openConflicts()), delta: this.store.openConflicts() ? 'needs a decision' : 'all clear', trend: this.store.openConflicts() ? 'up' as const : 'flat' as const, tone: this.store.openConflicts() ? 'negative' as const : 'positive' as const, hint: 'hard ones cannot be overridden' },
  ]);

  protected readonly peak = computed(() => Math.max(...this.store.series()));

  protected height(v: number): string {
    return `${Math.round((v / this.peak()) * 100)}%`;
  }

  protected async checkIn(a: ArrivalRow): Promise<void> {
    this.busy.set(a.id);
    const res = await this.store.checkIn(a.id);
    this.busy.set(null);
    if (res.kind === 'committed') {
      this.toast.success(`${a.guestAlias} checked in`, undefined, () => this.store.undoCheckIn(a.id));
    } else {
      this.toast.warn('Not ready', 'Open Check-in to clear the outstanding items.', res.code);
    }
  }

  protected exportDay(): void {
    this.toast.success(
      'Export queued',
      `${this.store.appointments().length} appointments and ${this.store.arrivals().length} arrivals. The file carries the filters that produced it.`,
    );
  }

  protected snooze(): void {
    this.snoozed.set(true);
    this.toast.info('Snoozed for an hour', 'It will come back if nobody resolves it.',);
  }

  protected resolve(): void {
    this.router.navigateByUrl('/app/schedule');
  }

  protected ready(a: ArrivalRow): boolean {
    return a.formsComplete && a.depositSettled && a.roomReady;
  }
}
